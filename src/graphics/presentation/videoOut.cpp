#include "graphics/presentation/videoOut.h"

#include "common/abi.h"
#include "common/assert.h"
#include "common/common.h"
#include "common/emulatorConfig.h"
#include "common/logging/log.h"
#include "common/profiler.h"
#include "common/stringUtils.h"
#include "common/threads.h"
#include "common/timer.h"
#include "graphics/guest_gpu/gpu_defs.h"
#include "graphics/guest_gpu/graphicsRun.h"
#include "graphics/guest_gpu/tile.h"
#include "graphics/host_gpu/renderer/image/imageInfo.h"
#include "graphics/host_gpu/renderer/render.h"
#include "graphics/host_gpu/renderer/renderContext.h"
#include "graphics/presentation/presenter.h"
#include "graphics/presentation/videoOutContracts.h"
#include "kernel/pthread.h"
#include "libs/errno.h"
#include "libs/libs.h"

#include <algorithm>
#include <array>
#include <list>
#include <thread>
#include <vector>

namespace Libs::Graphics {
struct GraphicContext;
} // namespace Libs::Graphics

namespace Libs::VideoOut {

LIB_NAME("VideoOut", "VideoOut");

namespace EventQueue = LibKernel::EventQueue;

constexpr int      VIDEO_OUT_EVENT_FLIP                                 = 0;
constexpr int      VIDEO_OUT_EVENT_VBLANK                               = 1;
constexpr int      VIDEO_OUT_EVENT_PRE_VBLANK_START                     = 2;
constexpr int      VIDEO_OUT_EVENT_SET_MODE                             = 8;
constexpr int      VIDEO_OUT_TRUE                                       = 1;
constexpr int      VIDEO_OUT_FALSE                                      = 0;
constexpr int      VIDEO_OUT_BUFFER_INDEX_BLACK                         = -2;
constexpr int      VIDEO_OUT_BUFFER_NUM_MAX                             = 16;
constexpr size_t   VIDEO_OUT_FLIP_QUEUE_CAPACITY                        = 16;
constexpr int      VIDEO_OUT_BUFFER_ATTRIBUTE_NUM_MAX                   = 4;
constexpr uint64_t VIDEO_OUT_OUTPUT_MODE_DEFAULT                        = 0x0000000000000001ULL;
constexpr uint64_t VIDEO_OUT_OUTPUT_MODE_119_88HZ                       = 0x000000000000000FULL;
constexpr uint64_t VIDEO_OUT_REFRESH_RATE_59_94HZ                       = 3;
constexpr uint64_t VIDEO_OUT_REFRESH_RATE_119_88HZ                      = 13;
constexpr int      VIDEO_OUT_BUFFER_ATTRIBUTE_CATEGORY_UNCOMPRESSED     = 0;
constexpr int      VIDEO_OUT_BUFFER_ATTRIBUTE_CATEGORY_COMPRESSED       = 1;
constexpr uint64_t VIDEO_OUT_BUFFER_ATTRIBUTE_OPTION_STRICT_COLORIMETRY = 8;

enum class VideoOutEventKind : uintptr_t {
	Flip           = VIDEO_OUT_EVENT_FLIP,
	Vblank         = VIDEO_OUT_EVENT_VBLANK,
	PreVblankStart = VIDEO_OUT_EVENT_PRE_VBLANK_START,
	OutputMode     = VIDEO_OUT_EVENT_SET_MODE,
};

enum class FlipRequestSource { Cpu, GpuEop };

struct VideoOutEventState;

struct VideoOutEventRegistration {
	EventQueue::KernelEqueue            handle = EventQueue::KERNEL_EQUEUE_INVALID;
	std::shared_ptr<VideoOutEventState> state;
	uint64_t                            generation = 0;
	VideoOutEventKind                   kind       = VideoOutEventKind::Flip;
};

using VideoOutEventRegistrationRef = std::shared_ptr<VideoOutEventRegistration>;
using VideoOutEventQueues          = std::vector<VideoOutEventRegistrationRef>;

struct VideoOutEventState {
	Common::Mutex       mutex;
	VideoOutEventQueues flip;
	VideoOutEventQueues pre_vblank;
	VideoOutEventQueues vblank;
	VideoOutEventQueues output_mode;
};

struct VideoOutBufferAttribute2 {
	uint32_t reserved0;
	uint32_t tiling_mode;
	uint32_t aspect_ratio;
	uint32_t width;
	uint32_t height;
	uint32_t pitch_in_pixel;
	uint64_t option;
	uint64_t pixel_format;
	uint64_t dcc_cb_register_clear_color;
	uint32_t dcc_control;
	uint32_t pad0;
	uint64_t reserved1[3];
};

// PS5 layout
struct VideoOutFlipStatus {
	uint64_t count                    = 0;
	uint64_t processTime              = 0;
	uint64_t reserved0                = 0;
	int64_t  flipArg                  = 0;
	uint64_t reserved1                = 0;
	uint64_t processTimeCounter       = 0;
	int32_t  gcQueueNum               = 0;
	int32_t  flipPendingNum           = 0;
	int32_t  currentBuffer            = 0;
	uint32_t reserved2                = 0;
	uint64_t submitProcessTimeCounter = 0;
	uint64_t reserved3[7]             = {};
};

// PS5 layout
struct VideoOutVblankStatus {
	uint64_t count              = 0;
	uint64_t processTime        = 0;
	uint64_t reserved           = 0;
	uint64_t processTimeCounter = 0;
	uint8_t  flags              = 0;
	uint8_t  phase              = 0;
	uint8_t  pad1[6]            = {};
};

struct VideoOutOutputStatus {
	uint32_t resolution   = 0;
	uint32_t dynamicRange = 0;
	uint64_t refreshRate  = 0;
	uint64_t flags        = 0;
	uint64_t reserved[3]  = {};
};

struct VideoOutOutputOptions {
	uint32_t internalData[16] = {};
};

struct VideoOutColorSettings {
	float    gamma       = 1.0f;
	uint32_t reserved[3] = {};
};

struct VideoOutBuffers {
	const void* data;
	const void* metadata;
	const void* reserved[2];
};

struct VideoOutBuffer {
	int      group_index      = -1;
	uint64_t data_address     = 0;
	uint64_t metadata_address = 0;

	[[nodiscard]] bool Occupied() const noexcept { return group_index >= 0; }
};

struct BufferAttributeGroup {
	VideoOutBufferAttribute2 attribute {};
	uint64_t                 registered_data_size = 0;
	int                      category = VIDEO_OUT_BUFFER_ATTRIBUTE_CATEGORY_UNCOMPRESSED;
	bool                     occupied = false;

	[[nodiscard]] Graphics::ImageInfo ImageInfo(const VideoOutBuffer& buffer) const;
};

struct VideoOutConfig {
	Common::Mutex                       mutex;
	Common::CondVar                     vblank_cond;
	std::shared_ptr<VideoOutEventState> events      = std::make_shared<VideoOutEventState>();
	uint32_t                            width       = 0;
	uint32_t                            height      = 0;
	uint64_t                            generation  = 0;
	bool                                opened      = false;
	bool                                closing     = false;
	int                                 flip_rate   = 0;
	uint64_t                            output_mode = VIDEO_OUT_OUTPUT_MODE_DEFAULT;
	float                               gamma       = 1.0f;
	VideoOutFlipStatus                  flip_status;
	VideoOutVblankStatus                pre_vblank_status;
	VideoOutVblankStatus                vblank_status;
	std::array<VideoOutBuffer, VIDEO_OUT_BUFFER_NUM_MAX>                 buffers;
	std::array<BufferAttributeGroup, VIDEO_OUT_BUFFER_ATTRIBUTE_NUM_MAX> groups;
};

class FlipQueue {
public:
	explicit FlipQueue(Graphics::Presenter& presenter): m_presenter(presenter) {
		EXIT_NOT_IMPLEMENTED(!Common::Thread::IsMainThread());
	}
	~FlipQueue();
	KYTY_CLASS_NO_COPY(FlipQueue);

	bool Reserve(VideoOutConfig& cfg, int index, int flip_mode, int64_t flip_arg,
	             FlipRequestSource source, uint64_t& request_id);
	void Cancel(VideoOutConfig& cfg);
	void Prepare(uint64_t request_id, Graphics::CommandBuffer& buffer);
	void Complete(uint64_t request_id);
	void WaitForSubmitSlot();
	bool Flip(uint32_t micros);
	void GetFlipStatus(VideoOutConfig& cfg, VideoOutFlipStatus& out);
	void Wait(VideoOutConfig& cfg, int index);
	bool UsesBufferSet(const VideoOutConfig& cfg, int set_index);

private:
	enum class RequestState { Reserved, Recording, Ready, Presenting };

	struct Request {
		uint64_t                    id;
		VideoOutConfig*             cfg;
		uint64_t                    generation;
		int                         index;
		int                         flip_mode;
		int64_t                     flip_arg;
		uint64_t                    submit_ptc;
		uint64_t                    request_order;
		FlipRequestSource           source;
		RequestState                state;
		Graphics::Presenter::Frame* frame;
	};

	Graphics::Presenter& m_presenter;
	Common::Mutex        m_mutex;
	Common::CondVar      m_submit_cond_var;
	Common::CondVar      m_submit_slot_cond_var;
	Common::CondVar      m_done_cond_var;
	std::list<Request>   m_requests;
	std::list<Request>   m_cpu_requests;
	std::list<Request>   m_cancelled_requests;
	bool                 m_processing      = false;
	uint64_t             m_next_request_id = 1;
	uint64_t             m_next_order      = 1;
};

struct VideoOutDriver::Impl {
public:
	static constexpr int VIDEO_OUT_NUM_MAX = 2;

	Impl(uint32_t width, uint32_t height, Graphics::Presenter& presenter)
	    : m_renderer(presenter.Renderer()), m_presenter(presenter), m_flip_queue(presenter) {
		EXIT_NOT_IMPLEMENTED(!Common::Thread::IsMainThread());
		Init(width, height);
		m_present_thread = std::jthread([this](std::stop_token token) { PresentThread(token); });
	}
	~Impl();
	KYTY_CLASS_NO_COPY(Impl);

	int             Open();
	bool            Close(int handle);
	VideoOutConfig* Get(int handle);
	VideoOutConfig* Get(int handle, uint64_t& generation);
	bool            IsOpened(int handle);

	void                     Init(uint32_t width, uint32_t height);
	FlipQueue&               GetFlipQueue() { return m_flip_queue; }
	Graphics::RenderContext& Renderer() const noexcept { return m_renderer; }

	void VblankBegin();
	void VblankEnd();
	void PresentThread(std::stop_token token);

private:
	Common::Mutex            m_mutex;
	VideoOutConfig           m_video_out_ctx[VIDEO_OUT_NUM_MAX];
	Graphics::RenderContext& m_renderer;
	Graphics::Presenter&     m_presenter;
	FlipQueue                m_flip_queue;
	std::jthread             m_present_thread;
};

static std::unique_ptr<VideoOutDriver> g_video_out_driver;

static VideoOutDriver::Impl& DriverState() {
	EXIT_IF(g_video_out_driver == nullptr);
	return g_video_out_driver->State();
}

static uintptr_t VideoOutEventId(VideoOutEventKind kind) {
	return static_cast<uintptr_t>(kind);
}

static VideoOutEventQueues& VideoOutEventQueuesFor(VideoOutEventState& state,
                                                   VideoOutEventKind   kind) {
	switch (kind) {
		case VideoOutEventKind::Flip: return state.flip;
		case VideoOutEventKind::Vblank: return state.vblank;
		case VideoOutEventKind::PreVblankStart: return state.pre_vblank;
		case VideoOutEventKind::OutputMode: return state.output_mode;
	}
	EXIT("unsupported video-out event kind\n");
	return state.flip;
}

static void ResetVideoOutEvent(EventQueue::KernelEqueueEvent* event) {
	EXIT_IF(event == nullptr);
	event->triggered    = false;
	event->event.fflags = 0;
	event->event.data   = 0;
}

static void TriggerVideoOutEvent(EventQueue::KernelEqueueEvent* event, void* trigger_data) {
	EXIT_IF(event == nullptr);

	const auto state = AccumulateVideoOutEvent(
	    {.triggered        = event->triggered,
	     .occurrence_count = event->event.fflags,
	     .encoded_data     = static_cast<uint64_t>(event->event.data)},
	    static_cast<uint64_t>(reinterpret_cast<uintptr_t>(trigger_data)),
	    LibKernel::KernelReadTsc());
	event->triggered    = state.triggered;
	event->event.fflags = state.occurrence_count;
	event->event.data   = static_cast<intptr_t>(state.encoded_data);
}

static void RemoveVideoOutEventQueue(EventQueue::KernelEqueue       eq,
                                     EventQueue::KernelEqueueEvent* event) {
	if (event == nullptr || event->filter.data == nullptr) {
		return;
	}

	auto* registration = static_cast<VideoOutEventRegistration*>(event->filter.data);
	auto  state        = registration->state;
	if (registration->handle != eq || !state) {
		return;
	}
	auto&             queues = VideoOutEventQueuesFor(*state, registration->kind);
	Common::LockGuard lock(state->mutex);
	const auto        entry =
	    std::find_if(queues.begin(), queues.end(), [registration](const auto& candidate) {
		    return candidate.get() == registration;
	    });
	if (entry != queues.end()) {
		queues.erase(entry);
	}
}

static void TriggerVideoOutEvents(VideoOutConfig& video_out, VideoOutEventKind kind,
                                  void* trigger_data) {
	VideoOutEventQueues queues;
	{
		Common::LockGuard lock(video_out.events->mutex);
		queues = VideoOutEventQueuesFor(*video_out.events, kind);
	}
	for (const auto& registration: queues) {
		if (!registration || registration->generation != video_out.generation) {
			continue;
		}
		const auto result =
		    EventQueue::KernelTriggerEvent(registration->handle, VideoOutEventId(kind),
		                                   EventQueue::KERNEL_EVFILT_VIDEO_OUT, trigger_data);
		EXIT_NOT_IMPLEMENTED(result != OK && result != LibKernel::KERNEL_ERROR_EBADF &&
		                     result != LibKernel::KERNEL_ERROR_ENOENT);
	}
}

static void DeleteVideoOutEvents(const VideoOutEventQueues& queues, VideoOutEventKind kind) {
	for (const auto& registration: queues) {
		if (!registration) {
			continue;
		}
		const auto result = EventQueue::KernelDeleteEvent(
		    registration->handle, VideoOutEventId(kind), EventQueue::KERNEL_EVFILT_VIDEO_OUT);
		EXIT_NOT_IMPLEMENTED(result != OK && result != LibKernel::KERNEL_ERROR_EBADF &&
		                     result != LibKernel::KERNEL_ERROR_ENOENT);
	}
}

static int RegisterVideoOutEvent(int handle, EventQueue::KernelEqueue eq, VideoOutEventKind kind,
                                 void* udata) {
	uint64_t generation = 0;
	auto*    video_out  = DriverState().Get(handle, generation);
	if (video_out == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	Common::LockGuard lock(video_out->mutex);
	if (!video_out->opened || video_out->closing || video_out->generation != generation) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}
	if (kind == VideoOutEventKind::OutputMode) {
		LOGF("\t eq     = 0x%016" PRIx64 "\n"
		     "\t handle = %d\n"
		     "\t udata  = 0x%016" PRIx64 "\n",
		     static_cast<uint64_t>(eq), handle, reinterpret_cast<uint64_t>(udata));
	}
	if (eq == EventQueue::KERNEL_EQUEUE_INVALID) {
		return VIDEO_OUT_ERROR_INVALID_EVENT_QUEUE;
	}
	if (!EventQueue::KernelPinEqueue(eq)) {
		return VIDEO_OUT_ERROR_INVALID_EVENT_QUEUE;
	}
	auto       event_state   = video_out->events;
	auto&      queues        = VideoOutEventQueuesFor(*event_state, kind);
	const auto initial_event = InitializeVideoOutEventRegistration();

	EventQueue::KernelEqueueEvent event {};
	event.triggered    = initial_event.triggered;
	event.event.ident  = VideoOutEventId(kind);
	event.event.filter = EventQueue::KERNEL_EVFILT_VIDEO_OUT;
	event.event.udata  = udata;
	event.event.fflags = initial_event.occurrence_count;
	event.event.data   = static_cast<intptr_t>(initial_event.encoded_data);
	event.filter.delete_event_func = RemoveVideoOutEventQueue;
	event.filter.reset_func        = ResetVideoOutEvent;
	event.filter.trigger_func      = TriggerVideoOutEvent;

	VideoOutEventRegistrationRef registration;
	bool                         add_queue = false;
	{
		Common::LockGuard event_lock(event_state->mutex);
		const auto        existing =
		    std::find_if(queues.begin(), queues.end(), [&](const auto& candidate) {
			    return candidate->handle == eq && candidate->generation == generation;
		    });
		if (existing != queues.end()) {
			registration = *existing;
		} else {
			registration = std::make_shared<VideoOutEventRegistration>(VideoOutEventRegistration {
			    .handle = eq, .state = event_state, .generation = generation, .kind = kind});
			queues.push_back(registration);
			add_queue = true;
		}
	}
	event.filter.data  = registration.get();
	event.filter.owner = registration;
	const int result   = EventQueue::KernelAddEvent(eq, event);
	if (result != OK && add_queue) {
		Common::LockGuard event_lock(event_state->mutex);
		const auto        added = std::find(queues.begin(), queues.end(), registration);
		if (added != queues.end()) {
			queues.erase(added);
		}
	}
	return result == LibKernel::KERNEL_ERROR_EBADF ? VIDEO_OUT_ERROR_INVALID_EVENT_QUEUE : result;
}

static int DeleteVideoOutEvent(int handle, EventQueue::KernelEqueue eq, VideoOutEventKind kind) {
	uint64_t generation = 0;
	auto*    video_out  = DriverState().Get(handle, generation);
	if (video_out == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}
	Common::LockGuard lock(video_out->mutex);
	if (!video_out->opened || video_out->closing || video_out->generation != generation) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}
	if (!EventQueue::KernelPinEqueue(eq)) {
		return VIDEO_OUT_ERROR_INVALID_EVENT_QUEUE;
	}
	const int result = EventQueue::KernelDeleteEvent(eq, VideoOutEventId(kind),
	                                                 EventQueue::KERNEL_EVFILT_VIDEO_OUT);
	if (result == LibKernel::KERNEL_ERROR_EBADF) {
		return VIDEO_OUT_ERROR_INVALID_EVENT_QUEUE;
	}
	return result == LibKernel::KERNEL_ERROR_ENOENT ? OK : result;
}

static bool IsFlipDueLocked(const VideoOutConfig& cfg, uint64_t generation, int buffer_index,
                            int flip_mode) {
	if (!cfg.opened || cfg.closing || cfg.generation != generation) {
		return false;
	}
	const int current_buffer = cfg.flip_status.currentBuffer;
	const int current_set = current_buffer >= 0 && current_buffer < VIDEO_OUT_BUFFER_NUM_MAX
	                            ? cfg.buffers[static_cast<size_t>(current_buffer)].group_index
	                            : -1;
	const int target_set = buffer_index >= 0 && buffer_index < VIDEO_OUT_BUFFER_NUM_MAX
	                           ? cfg.buffers[static_cast<size_t>(buffer_index)].group_index
	                           : -1;
	const bool buffer_set_transition = IsRegisteredBufferSetTransition(
	    current_buffer, buffer_index, current_set, target_set);
	const bool blank_destination     = IsBlankOutputDestination(buffer_index);
	const bool blank_exit_transition =
	    IsBlankToRegisteredBufferTransition(current_buffer, buffer_index, target_set);
	return IsLifecycleFlipRequestDue(cfg.vblank_status.count, cfg.flip_rate, flip_mode,
	                                 cfg.flip_status.count, buffer_set_transition,
	                                 blank_destination, blank_exit_transition);
}

static bool IsValidBufferIndex(int index) {
	return index >= VIDEO_OUT_BUFFER_INDEX_BLACK && index < VIDEO_OUT_BUFFER_NUM_MAX;
}

static bool IsSpecialBufferIndex(int index) {
	return index == VIDEO_OUT_BUFFER_INDEX_BLANK || index == VIDEO_OUT_BUFFER_INDEX_BLACK;
}

static int ReserveFlipRequest(VideoOutDriver::Impl& driver, int handle, int index, int flip_mode,
                              int64_t flip_arg, FlipRequestSource source, uint64_t& request_id) {
	auto* video_out = driver.Get(handle);
	if (video_out == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}
	const int flip_mode_result = ValidatePrimaryFlipMode(flip_mode);
	if (flip_mode_result != OK) {
		return flip_mode_result;
	}
	if (!IsValidBufferIndex(index)) {
		return VIDEO_OUT_ERROR_INVALID_INDEX;
	}

	Common::LockGuard lock(video_out->mutex);
	if (video_out->closing ||
	    (!IsSpecialBufferIndex(index) && !video_out->buffers[index].Occupied())) {
		return VIDEO_OUT_ERROR_INVALID_INDEX;
	}
	if (!driver.GetFlipQueue().Reserve(*video_out, index, flip_mode, flip_arg, source, request_id)) {
		return VIDEO_OUT_ERROR_FLIP_QUEUE_FULL;
	}
	return OK;
}

Graphics::ImageInfo BufferAttributeGroup::ImageInfo(const VideoOutBuffer& buffer) const {
	const auto compression = Graphics::ClassifyVideoOutCompression(
	    category == VIDEO_OUT_BUFFER_ATTRIBUTE_CATEGORY_COMPRESSED, buffer.metadata_address,
	    attribute.dcc_control, attribute.dcc_cb_register_clear_color);
	if (attribute.reserved0 != 0 || attribute.aspect_ratio != 0 || attribute.width == 0 ||
	    attribute.height == 0 || attribute.width > 16384 || attribute.height > 16384 ||
	    (attribute.option != 0 &&
	     attribute.option != VIDEO_OUT_BUFFER_ATTRIBUTE_OPTION_STRICT_COLORIMETRY) ||
	    attribute.tiling_mode != 0 || attribute.pad0 != 0 || attribute.reserved1[0] != 0 ||
	    attribute.reserved1[1] != 0 || attribute.reserved1[2] != 0 || buffer.data_address == 0 ||
	    compression == Graphics::VideoOutCompression::Unsupported) {
		EXIT("unsupported or invalid video-out surface attributes\n");
	}
	Graphics::VideoOutPixelFormatInfo pixel_format {};
	if (!Graphics::DecodeVideoOutPixelFormat(attribute.pixel_format, pixel_format)) {
		EXIT("unsupported video-out pixel format: 0x%016" PRIx64 "\n", attribute.pixel_format);
	}
	const auto tile_mode =
	    Graphics::Prospero::GpuEnumValue(Graphics::Prospero::TileMode::kRenderTarget);
	const auto pitch = Graphics::TileResolveRenderTargetPitch(
	    attribute.width, attribute.pitch_in_pixel, pixel_format.bytes_per_element);
	Graphics::TileSizeAlign total {};
	if (pitch == 0 ||
	    !Graphics::TileGetRenderTargetSize(attribute.width, attribute.height, pitch,
	                                       pixel_format.bytes_per_element, total) ||
	    total.size == 0 || total.align != 65536 ||
	    (buffer.data_address & (total.align - 1u)) != 0) {
		EXIT("invalid video-out surface footprint or alignment\n");
	}
	Graphics::ImageInfo info {};
	info.data            = {buffer.data_address, total.size};
	info.pixel_format    = pixel_format.format;
	info.guest_format    = pixel_format.guest_format;
	info.type            = Graphics::Prospero::ImageType::kColor2D;
	info.extent          = vk::Extent3D{attribute.width, attribute.height, 1};
	info.resources       = {1, 1};
	info.pitch           = pitch;
	info.bytes_per_block = pixel_format.bytes_per_element;
	info.samples         = 1;
	info.tile_mode       = tile_mode;
	info.bgra16          = pixel_format.bgra16;
	info.mip_layout[0]   = {0, total.size, pitch, attribute.height};
	if (compression != Graphics::VideoOutCompression::Uncompressed) {
		info.metadata.range       = {buffer.metadata_address, 0};
		info.metadata.kind        = Graphics::ImageMetadataKind::Dcc;
		info.metadata.control     = attribute.dcc_control;
		info.metadata.compression = compression;
	}
	Graphics::ImageOps::Validate(info);
	if (!Graphics::IsSupportedVideoOutFormat(info)) {
		EXIT("unsupported normalized video-out format\n");
	}
	return info;
}

VideoOutDriver::VideoOutDriver(uint32_t width, uint32_t height, Graphics::Presenter& presenter)
    : m_impl(std::make_unique<Impl>(width, height, presenter)) {}

VideoOutDriver::~VideoOutDriver() = default;

VideoOutDriver::Impl& VideoOutDriver::State() noexcept {
	return *m_impl;
}

VideoOutDriver& VideoOutInit(uint32_t width, uint32_t height, Graphics::Presenter& presenter) {
	EXIT_IF(g_video_out_driver != nullptr);
	g_video_out_driver = std::make_unique<VideoOutDriver>(width, height, presenter);
	return *g_video_out_driver;
}

void VideoOutShutdown() {
	g_video_out_driver.reset();
}

VideoOutDriver::Impl::~Impl() {
	if (m_present_thread.joinable()) {
		m_present_thread.request_stop();
		m_present_thread.join();
	}
	for (int handle = 1; handle < VIDEO_OUT_NUM_MAX; handle++) {
		(void)Close(handle);
	}
}

void VideoOutDriver::Impl::Init(uint32_t width, uint32_t height) {
	for (auto& ctx: m_video_out_ctx) {
		ctx.width  = width;
		ctx.height = height;
	}
}

int VideoOutDriver::Impl::Open() {
	Common::LockGuard lock(m_mutex);

	int handle = -1;

	for (int i = 1; i < VIDEO_OUT_NUM_MAX; i++) {
		if (!m_video_out_ctx[i].opened) {
			handle = i;
			break;
		}
	}

	if (handle < 0) {
		return -1;
	}
	auto&             config = m_video_out_ctx[handle];
	Common::LockGuard config_lock(config.mutex);

	{
		Common::LockGuard event_lock(config.events->mutex);
		EXIT_IF(!config.events->flip.empty());
		EXIT_IF(!config.events->pre_vblank.empty());
		EXIT_IF(!config.events->vblank.empty());
		EXIT_IF(!config.events->output_mode.empty());
	}
	EXIT_IF(config.flip_rate != 0);
	for (const auto& buffer: config.buffers) {
		EXIT_IF(buffer.Occupied());
	}
	for (const auto& group: config.groups) {
		EXIT_IF(group.occupied);
	}

	config.closing = false;
	config.opened  = true;
	if (++config.generation == 0) {
		EXIT("video-out port generation wrapped\n");
	}
	config.output_mode               = VIDEO_OUT_OUTPUT_MODE_DEFAULT;
	config.flip_status               = VideoOutFlipStatus();
	config.flip_status.flipArg       = -1;
	config.flip_status.currentBuffer = -1;
	config.flip_status.count         = 0;
	config.pre_vblank_status         = VideoOutVblankStatus();
	config.vblank_status             = VideoOutVblankStatus();

	return handle;
}

bool VideoOutDriver::Impl::Close(int handle) {
	Common::LockGuard lock(m_mutex);

	if (handle <= 0 || handle >= VIDEO_OUT_NUM_MAX || !m_video_out_ctx[handle].opened) {
		return false;
	}

	auto&               config = m_video_out_ctx[handle];
	VideoOutEventQueues flip_events;
	VideoOutEventQueues pre_vblank_events;
	VideoOutEventQueues vblank_events;
	VideoOutEventQueues output_mode_events;
	{
		Common::LockGuard config_lock(config.mutex);
		if (config.closing) {
			return false;
		}
		config.opened  = false;
		config.closing = true;
		if (++config.generation == 0) {
			EXIT("video-out port generation wrapped\n");
		}
		{
			Common::LockGuard event_lock(config.events->mutex);
			flip_events        = std::move(config.events->flip);
			pre_vblank_events  = std::move(config.events->pre_vblank);
			vblank_events      = std::move(config.events->vblank);
			output_mode_events = std::move(config.events->output_mode);
		}
		config.flip_rate = 0;

		for (const auto& buffer: config.buffers) {
			if (buffer.Occupied() &&
			    (buffer.group_index >= VIDEO_OUT_BUFFER_ATTRIBUTE_NUM_MAX ||
			     buffer.data_address == 0 || !config.groups[buffer.group_index].occupied)) {
				EXIT("inconsistent registered video-out buffer state\n");
			}
		}
		for (auto& buffer: config.buffers) {
			buffer = VideoOutBuffer {};
		}
		for (auto& group: config.groups) {
			group = BufferAttributeGroup {};
		}
		config.vblank_cond.SignalAll();
	}

	m_flip_queue.Cancel(config);
	DeleteVideoOutEvents(flip_events, VideoOutEventKind::Flip);
	DeleteVideoOutEvents(pre_vblank_events, VideoOutEventKind::PreVblankStart);
	DeleteVideoOutEvents(vblank_events, VideoOutEventKind::Vblank);
	DeleteVideoOutEvents(output_mode_events, VideoOutEventKind::OutputMode);
	return true;
}

VideoOutConfig* VideoOutDriver::Impl::Get(int handle) {
	Common::LockGuard lock(m_mutex);
	if (handle <= 0 || handle >= VIDEO_OUT_NUM_MAX || !m_video_out_ctx[handle].opened) {
		return nullptr;
	}

	return m_video_out_ctx + handle;
}

VideoOutConfig* VideoOutDriver::Impl::Get(int handle, uint64_t& generation) {
	Common::LockGuard lock(m_mutex);
	if (handle <= 0 || handle >= VIDEO_OUT_NUM_MAX || !m_video_out_ctx[handle].opened) {
		return nullptr;
	}

	auto*             config = m_video_out_ctx + handle;
	Common::LockGuard config_lock(config->mutex);
	if (!config->opened || config->closing) {
		return nullptr;
	}
	generation = config->generation;
	return config;
}

bool VideoOutDriver::Impl::IsOpened(int handle) {
	Common::LockGuard lock(m_mutex);

	return handle > 0 && handle < VIDEO_OUT_NUM_MAX && m_video_out_ctx[handle].opened;
}

void VideoOutDriver::Impl::VblankBegin() {
	Common::LockGuard lock(m_mutex);

	for (int i = 1; i < VIDEO_OUT_NUM_MAX; i++) {
		auto& ctx = m_video_out_ctx[i];
		if (ctx.opened) {
			ctx.mutex.Lock();
			ctx.pre_vblank_status.count++;
			ctx.pre_vblank_status.processTime        = LibKernel::KernelGetProcessTime();
			ctx.pre_vblank_status.reserved           = LibKernel::KernelReadTsc();
			ctx.pre_vblank_status.processTimeCounter = LibKernel::KernelGetProcessTimeCounter();

			TriggerVideoOutEvents(ctx, VideoOutEventKind::PreVblankStart,
			                      reinterpret_cast<void*>(ctx.pre_vblank_status.count));
			ctx.mutex.Unlock();
		}
	}
}

void VideoOutDriver::Impl::VblankEnd() {
	Common::LockGuard lock(m_mutex);

	for (int i = 1; i < VIDEO_OUT_NUM_MAX; i++) {
		auto& ctx = m_video_out_ctx[i];
		if (ctx.opened) {
			ctx.mutex.Lock();
			ctx.vblank_status.count++;
			ctx.vblank_status.processTime        = LibKernel::KernelGetProcessTime();
			ctx.vblank_status.reserved           = LibKernel::KernelReadTsc();
			ctx.vblank_status.processTimeCounter = LibKernel::KernelGetProcessTimeCounter();

			TriggerVideoOutEvents(ctx, VideoOutEventKind::Vblank,
			                      reinterpret_cast<void*>(ctx.vblank_status.count));
			ctx.vblank_cond.SignalAll();
			ctx.mutex.Unlock();
		}
	}
}

void VideoOutDriver::Impl::PresentThread(std::stop_token token) {
	const auto frequency = Common::Timer::QueryPerformanceFrequency();
	EXIT_IF(frequency == 0);

	int64_t total_wait = 0;
	while (!token.stop_requested()) {
		const auto sleep_begin = Common::Timer::QueryPerformanceCounter();
		if (total_wait > 0) {
			const auto sleep_end = sleep_begin + static_cast<uint64_t>(total_wait);
			auto       now       = sleep_begin;
			while (!token.stop_requested() && now < sleep_end) {
				const auto remaining_us = (sleep_end - now) * 1000000u / frequency;
				Common::Thread::SleepMicro(
				    static_cast<uint32_t>(std::clamp<uint64_t>(remaining_us, 1, 1000)));
				now = Common::Timer::QueryPerformanceCounter();
			}
		}
		if (token.stop_requested()) {
			break;
		}
		const auto frame_begin = Common::Timer::QueryPerformanceCounter();
		total_wait -= static_cast<int64_t>(frame_begin - sleep_begin);

		const auto refresh = std::max(Config::GetVblankFrequency(), 1u);
		const auto period  = std::max(frequency / refresh, uint64_t {1});

		if (m_presenter.IsGuestPaused()) {
			if (auto* frame = m_presenter.PrepareLastFrame(); frame != nullptr) {
				m_presenter.Present(*frame, true);
			}
			const auto frame_end = Common::Timer::QueryPerformanceCounter();
			total_wait +=
			    static_cast<int64_t>(period) - static_cast<int64_t>(frame_end - frame_begin);
			continue;
		}

		VblankBegin();
		const bool presented = m_flip_queue.Flip(0);
		if (!presented && total_wait < 0) {
			bool     any_open = false;
			uint32_t width    = 0;
			uint32_t height   = 0;
			{
				Common::LockGuard lock(m_mutex);
				width  = m_video_out_ctx[0].width;
				height = m_video_out_ctx[0].height;
				for (int handle = 1; handle < VIDEO_OUT_NUM_MAX; handle++) {
					any_open |= m_video_out_ctx[handle].opened;
				}
			}
			if (!any_open) {
				auto& blank = m_presenter.PrepareBlankFrame(width, height, true);
				m_presenter.Present(blank);
			}
		}
		VblankEnd();

		const auto frame_end = Common::Timer::QueryPerformanceCounter();
		total_wait += static_cast<int64_t>(period) - static_cast<int64_t>(frame_end - frame_begin);
	}
}

bool FlipQueue::Reserve(VideoOutConfig& cfg, int index, int flip_mode, int64_t flip_arg,
                        FlipRequestSource source, uint64_t& request_id) {
	Common::LockGuard lock(m_mutex);

	if (m_requests.size() + m_cpu_requests.size() >= VIDEO_OUT_FLIP_QUEUE_CAPACITY) {
		return false;
	}
	auto& pending = source == FlipRequestSource::GpuEop ? m_requests : m_cpu_requests;

	Request r {};
	r.id         = m_next_request_id++;
	r.cfg        = &cfg;
	r.generation = cfg.generation;
	r.index      = index;
	r.flip_mode  = flip_mode;
	r.flip_arg   = flip_arg;
	r.source     = source;
	r.state      = RequestState::Reserved;
	if (source == FlipRequestSource::Cpu && m_next_order == 0) {
		EXIT("video-out flip request order exhausted\n");
	}
	r.request_order = CaptureFlipRequestOrderAtReserve(
	    m_next_order, source == FlipRequestSource::GpuEop);
	if (r.request_order != 0) {
		m_next_order++;
	}
	const auto submit_counter = BeginFlipSubmitCounter(
	    cfg.flip_status.submitProcessTimeCounter,
	    source == FlipRequestSource::Cpu ? LibKernel::KernelGetProcessTimeCounter() : 0,
	    source == FlipRequestSource::GpuEop);
	r.submit_ptc                              = submit_counter.request;
	cfg.flip_status.submitProcessTimeCounter = submit_counter.published;

	pending.push_back(r);
	request_id = r.id;

	cfg.flip_status.flipPendingNum = static_cast<int>(m_requests.size() + m_cpu_requests.size());
	if (source == FlipRequestSource::GpuEop) {
		cfg.flip_status.gcQueueNum++;
	}

	return true;
}

FlipQueue::~FlipQueue() {
	for (auto* queue: {&m_requests, &m_cpu_requests, &m_cancelled_requests}) {
		for (auto& request: *queue) {
			if (request.frame != nullptr) {
				m_presenter.Discard(*request.frame);
			}
		}
	}
}

void FlipQueue::Cancel(VideoOutConfig& cfg) {
	std::vector<Graphics::Presenter::Frame*> frames;
	m_mutex.Lock();
	while (m_processing && !m_requests.empty() && m_requests.front().cfg == &cfg) {
		m_done_cond_var.Wait(&m_mutex);
	}
	for (auto* queue: {&m_requests, &m_cpu_requests}) {
		for (auto it = queue->begin(); it != queue->end();) {
			if (it->cfg != &cfg) {
				++it;
				continue;
			}
			if (it->state == RequestState::Reserved || it->state == RequestState::Recording) {
				auto cancelled = it++;
				m_cancelled_requests.splice(m_cancelled_requests.end(), *queue, cancelled);
				continue;
			}
			if (it->state == RequestState::Presenting) {
				EXIT("video-out cancellation retained a presenting request\n");
			}
			if (it->frame != nullptr) {
				frames.push_back(it->frame);
			}
			it = queue->erase(it);
		}
	}
	m_done_cond_var.SignalAll();
	m_submit_slot_cond_var.SignalAll();
	m_submit_cond_var.SignalAll();
	m_mutex.Unlock();
	for (auto* frame: frames) {
		m_presenter.Discard(*frame);
	}
	Common::LockGuard lock(cfg.mutex);
	cfg.flip_status.flipPendingNum = 0;
	cfg.flip_status.gcQueueNum     = 0;
}

void FlipQueue::Prepare(uint64_t request_id, Graphics::CommandBuffer& buffer) {
	VideoOutConfig* cfg        = nullptr;
	uint64_t        generation = 0;
	int             index      = 0;
	{
		Common::LockGuard lock(m_mutex);
		auto request = std::find_if(m_requests.begin(), m_requests.end(),
		                            [request_id](const auto& r) { return r.id == request_id; });
		if (request == m_requests.end()) {
			auto pending = std::find_if(m_cpu_requests.begin(), m_cpu_requests.end(),
			                            [request_id](const auto& r) { return r.id == request_id; });
			if (pending == m_cpu_requests.end()) {
				auto cancelled =
				    std::find_if(m_cancelled_requests.begin(), m_cancelled_requests.end(),
				                 [request_id](const auto& r) { return r.id == request_id; });
				if (cancelled == m_cancelled_requests.end() ||
				    cancelled->state != RequestState::Reserved) {
					EXIT("cannot prepare video-out request id=%" PRIu64 "\n", request_id);
				}
				cancelled->state = RequestState::Recording;
				return;
			}
			request = m_requests.insert(m_requests.end(), *pending);
			m_cpu_requests.erase(pending);
		}
		if (request->state != RequestState::Reserved) {
			EXIT("cannot prepare video-out request id=%" PRIu64 "\n", request_id);
		}
		request->state = RequestState::Recording;
		m_requests.sort([](const Request& lhs, const Request& rhs) {
			return FlipRequestOrderPrecedes(lhs.request_order, rhs.request_order);
		});
		cfg            = request->cfg;
		generation     = request->generation;
		index          = request->index;
	}

	const bool          special = IsSpecialBufferIndex(index);
	Graphics::ImageInfo source_info;
	uint32_t            width   = 0;
	uint32_t            height  = 0;
	bool                current = false;
	{
		Common::LockGuard lock(cfg->mutex);
		current = cfg->opened && !cfg->closing && cfg->generation == generation;
		if (current) {
			if (special) {
				width  = cfg->width;
				height = cfg->height;
			} else {
				const auto& surface = cfg->buffers[index];
				if (!surface.Occupied()) {
					EXIT("cannot prepare flip from an unregistered surface, id=%" PRIu64
					     " index=%d\n",
					     request_id, index);
				}
				if (surface.group_index >= VIDEO_OUT_BUFFER_ATTRIBUTE_NUM_MAX ||
				    !cfg->groups[surface.group_index].occupied) {
					EXIT("video-out surface references an unavailable attribute group, id=%" PRIu64
					     " index=%d group=%d\n",
					     request_id, index, surface.group_index);
				}
				source_info = cfg->groups[surface.group_index].ImageInfo(surface);
			}
		}
	}
	if (!current) {
		Common::LockGuard lock(m_mutex);
		const auto        request =
		    std::find_if(m_requests.begin(), m_requests.end(),
		                 [request_id](const auto& r) { return r.id == request_id; });
		if (request != m_requests.end()) {
			m_cancelled_requests.splice(m_cancelled_requests.end(), m_requests, request);
		}
		return;
	}
	// Sample the guest surface so "is the screen actually black?" is answerable from the emulator
	// rather than from the user's eyes. Reports once, after enough flips that the title has had a
	// chance to draw something. Reading a few scattered rows is enough to distinguish a cleared
	// surface from real image content.
	if (!special && source_info.data.address != 0) {
		static uint32_t sampled_flips = 0;
		static bool     reported      = false;
		if (!reported && ++sampled_flips == 120) {
			reported                 = true;
			const auto*    pixels    = reinterpret_cast<const uint8_t*>(source_info.data.address);
			const uint64_t span      = std::min<uint64_t>(source_info.data.size, 8u * 1024u * 1024u);
			uint64_t       nonzero   = 0;
			uint64_t       inspected = 0;
			for (uint64_t off = 0; off + 64 <= span; off += 65536) {
				for (uint32_t i = 0; i < 64; i++, inspected++) {
					if (pixels[off + i] != 0) {
						nonzero++;
					}
				}
			}
			printf("VideoOut: flip %u surface %ux%u - %llu/%llu sampled bytes non-zero (%.1f%%)\n",
			       sampled_flips, source_info.extent.width, source_info.extent.height,
			       static_cast<unsigned long long>(nonzero),
			       static_cast<unsigned long long>(inspected),
			       inspected == 0 ? 0.0 : 100.0 * static_cast<double>(nonzero) /
			                                  static_cast<double>(inspected));
			fflush(stdout);
		}
	}

	Graphics::Presenter::Frame* frame = nullptr;
	if (special) {
		frame = &m_presenter.PrepareBlankFrame(width, height, index == VIDEO_OUT_BUFFER_INDEX_BLACK,
		                                       &buffer);
	} else {
		frame = &m_presenter.PrepareFrame(buffer, source_info);
	}

	Common::LockGuard lock(m_mutex);
	Request*          prepared = nullptr;
	if (const auto request =
	        std::find_if(m_requests.begin(), m_requests.end(),
	                     [request_id](const auto& r) { return r.id == request_id; });
	    request != m_requests.end()) {
		prepared = &*request;
	} else if (const auto cancelled =
	               std::find_if(m_cancelled_requests.begin(), m_cancelled_requests.end(),
	                            [request_id](const auto& r) { return r.id == request_id; });
	           cancelled != m_cancelled_requests.end()) {
		prepared = &*cancelled;
	}
	if (prepared == nullptr || prepared->state != RequestState::Recording ||
	    prepared->frame != nullptr) {
		EXIT("video-out request changed while recording, id=%" PRIu64 "\n", request_id);
	}
	prepared->frame = frame;
}

void FlipQueue::Complete(uint64_t request_id) {
	VideoOutConfig* cfg = nullptr;
	{
		Common::LockGuard lock(m_mutex);
		const auto request =
		    std::find_if(m_requests.begin(), m_requests.end(),
		                 [request_id](const auto& r) { return r.id == request_id; });
		if (request != m_requests.end()) {
			if (request->state != RequestState::Recording || request->frame == nullptr) {
				EXIT("completed GPU flip has no prepared recording, id=%" PRIu64 "\n",
				     request_id);
			}
			if (request->source == FlipRequestSource::GpuEop) {
				if (request->request_order != 0) {
					EXIT("video-out GPU flip request completed twice, id=%" PRIu64 "\n", request_id);
				}
				if (m_next_order == 0) {
					EXIT("video-out flip request order exhausted\n");
				}
				request->request_order = CaptureFlipRequestOrderAtCompletion(
				    request->request_order, m_next_order, true);
				m_next_order++;
				m_requests.sort([](const Request& lhs, const Request& rhs) {
					return FlipRequestOrderPrecedes(lhs.request_order, rhs.request_order);
				});
			}
			cfg = request->cfg;
		} else {
			const auto cancelled =
			    std::find_if(m_cancelled_requests.begin(), m_cancelled_requests.end(),
			                 [request_id](const auto& r) { return r.id == request_id; });
			if (cancelled == m_cancelled_requests.end() ||
			    cancelled->state != RequestState::Recording) {
				EXIT("completed GPU flip has no prepared recording, id=%" PRIu64 "\n",
				     request_id);
			}
			cfg = cancelled->cfg;
		}
	}

	Graphics::Presenter::Frame* frame = nullptr;
	cfg->mutex.Lock();
	m_mutex.Lock();
	auto request = std::find_if(m_requests.begin(), m_requests.end(),
	                            [request_id](const auto& r) { return r.id == request_id; });
	if (request != m_requests.end()) {
		if (request->state != RequestState::Recording || request->frame == nullptr) {
			m_mutex.Unlock();
			cfg->mutex.Unlock();
			EXIT("completed GPU flip has no prepared recording, id=%" PRIu64 "\n", request_id);
		}
		const auto counts =
		    CompleteFlipExecution({cfg->flip_status.gcQueueNum, cfg->flip_status.flipPendingNum},
		                          request->source == FlipRequestSource::GpuEop);
		const auto submit_counter = CompleteFlipSubmitCounter(
		    {cfg->flip_status.submitProcessTimeCounter, request->submit_ptc},
		    request->source == FlipRequestSource::GpuEop
		        ? LibKernel::KernelGetProcessTimeCounter()
		        : 0,
		    request->source == FlipRequestSource::GpuEop);
		cfg->flip_status.gcQueueNum     = counts.gc_queue_num;
		cfg->flip_status.flipPendingNum = counts.flip_pending_num;
		cfg->flip_status.submitProcessTimeCounter = submit_counter.published;
		request->submit_ptc                         = submit_counter.request;
		request->state = RequestState::Ready;
		m_submit_cond_var.Signal();
		m_mutex.Unlock();
		cfg->mutex.Unlock();
		return;
	}
	auto cancelled = std::find_if(m_cancelled_requests.begin(), m_cancelled_requests.end(),
	                              [request_id](const auto& r) { return r.id == request_id; });
	if (cancelled == m_cancelled_requests.end() || cancelled->state != RequestState::Recording) {
		m_mutex.Unlock();
		cfg->mutex.Unlock();
		EXIT("completed GPU flip has no prepared recording, id=%" PRIu64 "\n", request_id);
	}
	frame = cancelled->frame;
	m_cancelled_requests.erase(cancelled);
	m_done_cond_var.SignalAll();
	m_mutex.Unlock();
	cfg->mutex.Unlock();
	if (frame != nullptr) {
		m_presenter.Discard(*frame);
	}
}

void FlipQueue::WaitForSubmitSlot() {
	Common::LockGuard lock(m_mutex);
	while (m_requests.size() + m_cpu_requests.size() >= VIDEO_OUT_FLIP_QUEUE_CAPACITY) {
		if (m_requests.empty()) {
			EXIT("video-out queue is saturated by CPU flips queued behind the current EOP\n");
		}
		m_submit_slot_cond_var.Wait(&m_mutex);
	}
}

void FlipQueue::Wait(VideoOutConfig& cfg, int index) {
	Common::LockGuard lock(m_mutex);

	auto has_request = [this, &cfg, index] {
		auto matches = [&cfg, index](const auto& r) { return r.cfg == &cfg && r.index == index; };
		return std::any_of(m_requests.begin(), m_requests.end(), matches) ||
		       std::any_of(m_cpu_requests.begin(), m_cpu_requests.end(), matches);
	};
	// A display buffer becomes safe only when presentation completes its matching request, or when
	// port closure cancels that request. Condition-variable wakeups are always rechecked.
	while (ShouldWaitUntilSafeForRendering(has_request())) {
		m_done_cond_var.Wait(&m_mutex);
	}
}

bool FlipQueue::UsesBufferSet(const VideoOutConfig& cfg, int set_index) {
	Common::LockGuard lock(m_mutex);

	const auto uses_set = [&cfg, set_index](const Request& request) {
		const int request_set =
		    request.index >= 0 && request.index < VIDEO_OUT_BUFFER_NUM_MAX
		        ? cfg.buffers[static_cast<size_t>(request.index)].group_index
		        : -1;
		return IsFlipRequestUsingBufferSet(request.cfg == &cfg, request.index,
		                                       VIDEO_OUT_BUFFER_NUM_MAX, request_set, set_index);
	};
	return std::any_of(m_requests.begin(), m_requests.end(), uses_set) ||
	       std::any_of(m_cpu_requests.begin(), m_cpu_requests.end(), uses_set);
}

bool FlipQueue::Flip(uint32_t micros) {
	KYTY_PROFILER_BLOCK("FlipQueue::Flip");

	m_mutex.Lock();
	if (m_requests.empty()) {
		m_submit_cond_var.WaitFor(&m_mutex, micros);

		if (m_requests.empty()) {
			m_mutex.Unlock();
			return false;
		}
	}
	if (m_processing) {
		EXIT("video-out flip queue processing is already active\n");
	}
	const auto& front = m_requests.front();
	if (std::any_of(m_cpu_requests.begin(), m_cpu_requests.end(), [&front](const Request& pending) {
		    return pending.cfg == front.cfg &&
		           ShouldDeferFlipForPendingCpu(front.request_order, pending.request_order);
	    })) {
		m_mutex.Unlock();
		return false;
	}
	if (m_requests.front().state != RequestState::Ready) {
		m_mutex.Unlock();
		return false;
	}
	m_processing = true;
	auto r       = m_requests.front();
	m_mutex.Unlock();

	r.cfg->mutex.Lock();
	if (!IsFlipDueLocked(*r.cfg, r.generation, r.index, r.flip_mode)) {
		r.cfg->mutex.Unlock();
		Common::LockGuard queue_lock(m_mutex);
		m_processing = false;
		m_done_cond_var.SignalAll();
		return false;
	}

	m_mutex.Lock();
	if (m_requests.empty() || m_requests.front().id != r.id ||
	    m_requests.front().state != RequestState::Ready || !m_processing) {
		EXIT("video-out request changed before presentation, id=%" PRIu64 "\n", r.id);
	}
	m_requests.front().state = RequestState::Presenting;
	m_mutex.Unlock();

	m_presenter.Present(*r.frame);

	m_mutex.Lock();
	if (m_requests.empty() || m_requests.front().id != r.id ||
	    m_requests.front().state != RequestState::Presenting) {
		EXIT("video-out flip queue changed while processing its front request\n");
	}
	m_requests.pop_front();

	r.cfg->flip_status.count++;
	r.cfg->flip_status.processTime              = LibKernel::KernelGetProcessTime();
	r.cfg->flip_status.processTimeCounter       = LibKernel::KernelGetProcessTimeCounter();
	const auto submit_counter = PublishFlipSubmitCounter(
	    {r.cfg->flip_status.submitProcessTimeCounter, r.submit_ptc});
	r.cfg->flip_status.submitProcessTimeCounter = submit_counter.published;
	r.cfg->flip_status.flipArg                  = r.flip_arg;
	r.cfg->flip_status.currentBuffer            = r.index;
	r.cfg->flip_status.flipPendingNum = static_cast<int>(m_requests.size() + m_cpu_requests.size());
	TriggerVideoOutEvents(*r.cfg, VideoOutEventKind::Flip, reinterpret_cast<void*>(r.flip_arg));

	m_processing = false;
	m_done_cond_var.SignalAll();
	m_submit_slot_cond_var.Signal();
	m_mutex.Unlock();
	r.cfg->mutex.Unlock();

	if (Config::GraphicsDebugDumpEnabled() &&
	    Config::GetPrintfDirection() != Config::OutputDirection::Silent) {
		LOGF("Flip done: %d\n", r.index);
	}

	return true;
}

void FlipQueue::GetFlipStatus(VideoOutConfig& cfg, VideoOutFlipStatus& out) {
	Common::LockGuard lock(cfg.mutex);

	out = cfg.flip_status;
}

KYTY_SYSV_ABI int VideoOutOpen(int user_id, int bus_type, int index, const void* param) {
	PRINT_NAME();

	EXIT_NOT_IMPLEMENTED(user_id != 255 && user_id != 0);
	EXIT_NOT_IMPLEMENTED(bus_type != 0);
	EXIT_NOT_IMPLEMENTED(index != 0);

	LOGF("\t param = 0x%016" PRIx64 "\n", reinterpret_cast<uint64_t>(param));

	int handle = DriverState().Open();

	if (handle < 0) {
		return VIDEO_OUT_ERROR_RESOURCE_BUSY;
	}

	return handle;
}

KYTY_SYSV_ABI int VideoOutClose(int handle) {
	PRINT_NAME();

	return DriverState().Close(handle) ? OK : VIDEO_OUT_ERROR_INVALID_HANDLE;
}

KYTY_SYSV_ABI void VideoOutSetBufferAttribute2(VideoOutBufferAttribute2* attribute,
                                               uint64_t pixel_format, uint32_t tiling_mode,
                                               uint32_t width, uint32_t height, uint64_t option,
                                               uint32_t dcc_control,
                                               uint64_t dcc_cb_register_clear_color) {
	PRINT_NAME();

	EXIT_NOT_IMPLEMENTED(attribute == nullptr);

	LOGF("\t pixel_format                = %016" PRIx64 "\n"
	     "\t tiling_mode                 = %" PRIu32 "\n"
	     "\t width                       = %" PRIu32 "\n"
	     "\t height                      = %" PRIu32 "\n"
	     "\t option                      = %016" PRIx64 "\n"
	     "\t dcc_control                 = %08" PRIx32 "\n"
	     "\t dcc_cb_register_clear_color = %016" PRIx64 "\n",
	     pixel_format, tiling_mode, width, height, option, dcc_control,
	     dcc_cb_register_clear_color);

	memset(attribute, 0, sizeof(VideoOutBufferAttribute2));

	attribute->tiling_mode                 = tiling_mode;
	attribute->aspect_ratio                = 0;
	attribute->width                       = width;
	attribute->height                      = height;
	attribute->pitch_in_pixel              = 0;
	attribute->option                      = option;
	attribute->pixel_format                = pixel_format;
	attribute->dcc_cb_register_clear_color = dcc_cb_register_clear_color;
	attribute->dcc_control                 = dcc_control;
}

KYTY_SYSV_ABI int VideoOutSetFlipRate(int handle, int rate) {
	PRINT_NAME();

	LOGF("\trate = %d\n", rate);

	auto* ctx = DriverState().Get(handle);
	if (ctx == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	const int rate_result = ValidatePrimaryFlipRate(rate);
	if (rate_result != OK) {
		return rate_result;
	}

	Common::LockGuard lock(ctx->mutex);
	ctx->flip_rate = rate;

	return OK;
}

KYTY_SYSV_ABI int VideoOutDeleteFlipEvent(EventQueue::KernelEqueue eq, int handle) {
	PRINT_NAME();
	return DeleteVideoOutEvent(handle, eq, VideoOutEventKind::Flip);
}

KYTY_SYSV_ABI int VideoOutAddFlipEvent(EventQueue::KernelEqueue eq, int handle, void* udata) {
	PRINT_NAME();
	return RegisterVideoOutEvent(handle, eq, VideoOutEventKind::Flip, udata);
}

KYTY_SYSV_ABI int VideoOutDeleteVblankEvent(EventQueue::KernelEqueue eq, int handle) {
	PRINT_NAME();
	return DeleteVideoOutEvent(handle, eq, VideoOutEventKind::Vblank);
}

KYTY_SYSV_ABI int VideoOutDeletePreVblankStartEvent(EventQueue::KernelEqueue eq, int handle) {
	PRINT_NAME();
	return DeleteVideoOutEvent(handle, eq, VideoOutEventKind::PreVblankStart);
}

KYTY_SYSV_ABI int VideoOutDeleteOutputModeEvent(EventQueue::KernelEqueue eq, int handle) {
	PRINT_NAME();
	return DeleteVideoOutEvent(handle, eq, VideoOutEventKind::OutputMode);
}

KYTY_SYSV_ABI int VideoOutAddVblankEvent(LibKernel::EventQueue::KernelEqueue eq, int handle,
                                         void* udata) {
	PRINT_NAME();
	return RegisterVideoOutEvent(handle, eq, VideoOutEventKind::Vblank, udata);
}

KYTY_SYSV_ABI int VideoOutAddPreVblankStartEvent(LibKernel::EventQueue::KernelEqueue eq, int handle,
                                                 void* udata) {
	PRINT_NAME();
	return RegisterVideoOutEvent(handle, eq, VideoOutEventKind::PreVblankStart, udata);
}

KYTY_SYSV_ABI int VideoOutAddOutputModeEvent(LibKernel::EventQueue::KernelEqueue eq, int handle,
                                             void* udata) {
	PRINT_NAME();
	return RegisterVideoOutEvent(handle, eq, VideoOutEventKind::OutputMode, udata);
}

KYTY_SYSV_ABI int VideoOutRegisterBuffers2(int handle, int set_index, int buffer_index_start,
                                           const VideoOutBuffers* buffers, int buffer_num,
                                           const VideoOutBufferAttribute2* attribute, int category,
                                           void* option) {
	PRINT_NAME();

	auto* ctx = DriverState().Get(handle);
	if (ctx == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	if (buffers == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_ADDRESS;
	}

	if (attribute == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_OPTION;
	}

	const int set_index_result =
	    ValidateRegisterBufferSetIndex(set_index, VIDEO_OUT_BUFFER_ATTRIBUTE_NUM_MAX);
	if (set_index_result != OK) {
		return set_index_result;
	}
	const int buffer_domain_result = ValidateRegisterBufferDomain(
	    buffer_index_start, buffer_num, VIDEO_OUT_BUFFER_NUM_MAX);
	if (buffer_domain_result != OK) {
		return buffer_domain_result;
	}
	{
		Common::LockGuard lock(ctx->mutex);
		if (ctx->closing) {
			return VIDEO_OUT_ERROR_INVALID_HANDLE;
		}
		const int buffer_set_result = ValidateRegisterBufferSetAndTotalRange(
		    ctx->groups[set_index].occupied, buffer_index_start, buffer_num,
		    VIDEO_OUT_BUFFER_NUM_MAX);
		if (buffer_set_result != OK) {
			return buffer_set_result;
		}
	}

	LOGF("\t start_index    = %d\n"
	     "\t buffer_num     = %d\n"
	     "\t set_index      = %d\n"
	     "\t pixel_format   = 0x%016" PRIx64 "\n"
	     "\t tiling_mode    = %" PRIu32 "\n"
	     "\t aspect_ratio   = %" PRIu32 "\n"
	     "\t width          = %" PRIu32 "\n"
	     "\t height         = %" PRIu32 "\n"
	     "\t pitch_in_pixel = %" PRIu32 "\n"
	     "\t option         = %" PRIu64 "\n"
	     "\t category       = %d\n",
	     buffer_index_start, buffer_num, set_index, attribute->pixel_format, attribute->tiling_mode,
	     attribute->aspect_ratio, attribute->width, attribute->height, attribute->pitch_in_pixel,
	     attribute->option, category);

	if (option != nullptr) {
		return VIDEO_OUT_ERROR_INVALID_OPTION;
	}
	if (category != VIDEO_OUT_BUFFER_ATTRIBUTE_CATEGORY_UNCOMPRESSED &&
	    category != VIDEO_OUT_BUFFER_ATTRIBUTE_CATEGORY_COMPRESSED) {
		return VIDEO_OUT_ERROR_INVALID_CATEGORY;
	}
	const int tiling_mode_result = ValidateRegisterTilingMode(attribute->tiling_mode);
	if (tiling_mode_result != OK) {
		return tiling_mode_result;
	}
	Graphics::VideoOutPixelFormatInfo pixel_format {};
	const int                         pixel_format_result = ValidateRegisterPixelFormat(
	    Graphics::DecodeVideoOutPixelFormat(attribute->pixel_format, pixel_format));
	if (pixel_format_result != OK) {
		return pixel_format_result;
	}
	const int resolution_result =
	    ValidateRegisterResolution(attribute->width, attribute->height);
	if (resolution_result != OK) {
		return resolution_result;
	}
	const int pitch_result =
	    ValidateRegisterBufferPitch(attribute->width, attribute->pitch_in_pixel);
	if (pitch_result != OK) {
		return pitch_result;
	}
	if (attribute->pitch_in_pixel != 0 && attribute->tiling_mode == 0 &&
	    !Graphics::TileIsRenderTargetPitchValid(attribute->width, attribute->pitch_in_pixel,
	                                            pixel_format.bytes_per_element)) {
		return VIDEO_OUT_ERROR_INVALID_PITCH;
	}
	for (int i = 0; i < buffer_num; i++) {
		const int data_address_result =
		    ValidateRegisterBufferDataAddress(reinterpret_cast<uint64_t>(buffers[i].data));
		if (data_address_result != OK) {
			return data_address_result;
		}
	}

	BufferAttributeGroup group {
	    .attribute = *attribute,
	    .category  = category,
	    .occupied  = true,
	};
	std::vector<VideoOutBuffer> registrations;
	registrations.reserve(static_cast<size_t>(buffer_num));

	for (int i = 0; i < buffer_num; i++) {
		LOGF("\t buffers[%d]: data=%p metadata=%p\n", i, buffers[i].data, buffers[i].metadata);
		if (buffers[i].reserved[0] != nullptr || buffers[i].reserved[1] != nullptr) {
			LOGF("\t buffers[%d]: ignoring reserved fields {%p, %p}\n", i, buffers[i].reserved[0],
			     buffers[i].reserved[1]);
		}
		const auto data_address     = reinterpret_cast<uint64_t>(buffers[i].data);
		const auto metadata_address = reinterpret_cast<uint64_t>(buffers[i].metadata);
		if (attribute->tiling_mode == 0 && data_address != 0) {
			const int alignment_result = ValidateTiledBufferAddressAlignment(data_address);
			if (alignment_result != OK) {
				return alignment_result;
			}
		}
		registrations.push_back({
		    .group_index      = set_index,
		    .data_address     = data_address,
		    .metadata_address = metadata_address,
		});
		const auto image_info = group.ImageInfo(registrations.back());
		group.registered_data_size = image_info.data.size;
	}

	Common::LockGuard lock(ctx->mutex);
	if (ctx->closing) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}
	const int buffer_set_result = ValidateRegisterBufferSet(ctx->groups[set_index].occupied);
	if (buffer_set_result != OK) {
		return buffer_set_result;
	}
	for (int i = 0; i < buffer_num; i++) {
		if (ctx->buffers[buffer_index_start + i].Occupied()) {
			return VIDEO_OUT_ERROR_SLOT_OCCUPIED;
		}
	}

	ctx->groups[set_index] = group;
	for (int i = 0; i < buffer_num; i++) {
		ctx->buffers[buffer_index_start + i] = registrations[static_cast<size_t>(i)];
		const auto& buffer                   = registrations[static_cast<size_t>(i)];
		LOGF("\tbuffers[%d] = %016" PRIx64 " metadata = %016" PRIx64 " dcc = %08" PRIx32 "\n",
		     buffer_index_start + i, buffer.data_address, buffer.metadata_address,
		     attribute->dcc_control);
	}

	return OK;
}

KYTY_SYSV_ABI int VideoOutSubmitChangeBufferAttribute2(int handle, int set_index,
                                                       const VideoOutBufferAttribute2* attribute,
                                                       void*                           option) {
	PRINT_NAME();

	auto* ctx = DriverState().Get(handle);
	if (ctx == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	if (attribute == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_OPTION;
	}
	if (set_index < 0 || set_index >= VIDEO_OUT_BUFFER_ATTRIBUTE_NUM_MAX) {
		return VIDEO_OUT_ERROR_INVALID_INDEX;
	}

	if (option != nullptr) {
		return VIDEO_OUT_ERROR_INVALID_OPTION;
	}

	Common::LockGuard lock(ctx->mutex);
	const auto&       current = ctx->groups[set_index];
	if (ctx->closing || !current.occupied) {
		return VIDEO_OUT_ERROR_INVALID_INDEX;
	}

	BufferAttributeGroup replacement {
	    .attribute            = *attribute,
	    .registered_data_size = current.registered_data_size,
	    .category             = current.category,
	    .occupied             = true,
	};
	for (const auto& buffer: ctx->buffers) {
		if (buffer.group_index == set_index) {
			const auto replacement_info = replacement.ImageInfo(buffer);
			const int  footprint_result  = ValidateBufferAttributeChangeFootprint(
			    current.registered_data_size, replacement_info.data.size);
			if (footprint_result != OK) {
				return footprint_result;
			}
		}
	}
	ctx->groups[set_index] = replacement;

	return OK;
}

KYTY_SYSV_ABI int VideoOutUnregisterBuffers(int handle, int set_index) {
	PRINT_NAME();

	auto* ctx = DriverState().Get(handle);
	if (ctx == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}
	if (set_index < 0 || set_index >= VIDEO_OUT_BUFFER_ATTRIBUTE_NUM_MAX) {
		return VIDEO_OUT_ERROR_INVALID_INDEX;
	}

	Common::LockGuard lock(ctx->mutex);
	if (ctx->closing) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}
	if (!ctx->groups[set_index].occupied) {
		return VIDEO_OUT_ERROR_INVALID_INDEX;
	}
	const int current_buffer = ctx->flip_status.currentBuffer;
	const int displayed_set  = current_buffer >= 0 && current_buffer < VIDEO_OUT_BUFFER_NUM_MAX
	                               ? ctx->buffers[static_cast<size_t>(current_buffer)].group_index
	                               : -1;
	const bool pending_use   = DriverState().GetFlipQueue().UsesBufferSet(*ctx, set_index);
	const int  usage_result  =
	    ValidateUnregisterBufferSet(set_index, displayed_set, pending_use);
	if (usage_result != OK) {
		return usage_result;
	}
	ctx->groups[set_index] = BufferAttributeGroup {};
	for (auto& buffer: ctx->buffers) {
		if (buffer.group_index == set_index) {
			buffer = VideoOutBuffer {};
		}
	}

	return OK;
}

KYTY_SYSV_ABI int VideoOutSubmitFlip(int handle, int index, int flip_mode, int64_t flip_arg) {
	PRINT_NAME();

	uint64_t  request_id = 0;
	const int result     = ReserveFlipRequest(DriverState(), handle, index, flip_mode, flip_arg,
	                                          FlipRequestSource::Cpu, request_id);
	if (result == VIDEO_OUT_ERROR_INVALID_FLIP_MODE) {
		LOGF("\t unsupported flip_mode = %d\n", flip_mode);
	}
	if (result != OK) {
		return result;
	}
	g_video_out_driver->SubmitFlipPreparation(request_id);

	return OK;
}

int VideoOutDriver::SubmitFlipFromGpu(Graphics::CommandBuffer& buffer, int handle, int index,
                                      int flip_mode, int64_t flip_arg, uint64_t& request_id) {
	EXIT_IF(buffer.IsInvalid());

	const int result = ReserveFlipRequest(*m_impl, handle, index, flip_mode, flip_arg,
	                                      FlipRequestSource::GpuEop, request_id);
	if (result != OK) {
		return result;
	}
	m_impl->GetFlipQueue().Prepare(request_id, buffer);

	return OK;
}

void VideoOutDriver::SubmitFlipPreparation(uint64_t request_id) {
	m_impl->Renderer().GetGpu().SubmitFlipPreparation(request_id);
}

void VideoOutDriver::PrepareFlip(uint64_t request_id, Graphics::CommandBuffer& buffer) {
	m_impl->GetFlipQueue().Prepare(request_id, buffer);
}

void VideoOutDriver::CompleteFlip(uint64_t request_id) {
	m_impl->GetFlipQueue().Complete(request_id);
}

void VideoOutDriver::WaitForSubmitSlot() {
	m_impl->GetFlipQueue().WaitForSubmitSlot();
}

void VideoOutDriver::WaitFlipDone(int handle, int index) {
	auto* ctx = m_impl->Get(handle);
	EXIT_IF(ctx == nullptr);

	EXIT_NOT_IMPLEMENTED(!IsValidBufferIndex(index));
	m_impl->GetFlipQueue().Wait(*ctx, index);
}

KYTY_SYSV_ABI int VideoOutGetFlipStatus(int handle, VideoOutFlipStatus* status) {
	PRINT_NAME();

	if (status == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_ADDRESS;
	}

	auto* ctx = DriverState().Get(handle);
	if (ctx == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	DriverState().GetFlipQueue().GetFlipStatus(*ctx, *status);

	LOGF("\t count = %" PRIu64 "\n"
	     "\t processTime = %" PRIu64 "\n"
	     "\t processTimeCounter = %" PRIu64 "\n"
	     "\t submitProcessTimeCounter = %" PRIu64 "\n"
	     "\t flipArg = %" PRId64 "\n"
	     "\t gcQueueNum = %d\n"
	     "\t flipPendingNum = %d\n"
	     "\t currentBuffer = %d\n",
	     status->count, status->processTime, status->processTimeCounter,
	     status->submitProcessTimeCounter, status->flipArg, status->gcQueueNum,
	     status->flipPendingNum, status->currentBuffer);

	return OK;
}

KYTY_SYSV_ABI int VideoOutIsFlipPending(int handle) {
	PRINT_NAME();

	auto* ctx = DriverState().Get(handle);
	if (ctx == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	VideoOutFlipStatus status {};
	DriverState().GetFlipQueue().GetFlipStatus(*ctx, status);

	LOGF("\t flipPendingNum = %d\n", status.flipPendingNum);

	return status.flipPendingNum;
}

KYTY_SYSV_ABI int VideoOutGetVblankStatus(int handle, VideoOutVblankStatus* status) {
	PRINT_NAME();

	if (status == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_ADDRESS;
	}

	auto* ctx = DriverState().Get(handle);
	if (ctx == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	ctx->mutex.Lock();
	*status = ctx->vblank_status;
	ctx->mutex.Unlock();

	LOGF("\t count = %" PRIu64 "\n"
	     "\t processTime = %" PRIu64 "\n"
	     "\t processTimeCounter = %" PRIu64 "\n",
	     status->count, status->processTime, status->processTimeCounter);

	return OK;
}

KYTY_SYSV_ABI int VideoOutGetEventId(const EventQueue::KernelEvent* ev) {
	PRINT_NAME();

	if (ev == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_ADDRESS;
	}

	if (ev->filter != EventQueue::KERNEL_EVFILT_VIDEO_OUT) {
		return VIDEO_OUT_ERROR_INVALID_EVENT;
	}

	switch (ev->ident) {
		case VIDEO_OUT_EVENT_FLIP:
		case VIDEO_OUT_EVENT_VBLANK:
		case VIDEO_OUT_EVENT_PRE_VBLANK_START:
		case VIDEO_OUT_EVENT_SET_MODE: return static_cast<int>(ev->ident);
		default: return VIDEO_OUT_ERROR_INVALID_EVENT;
	}
}

KYTY_SYSV_ABI int VideoOutGetEventData(const EventQueue::KernelEvent* ev, int64_t* data) {
	PRINT_NAME();

	if (ev == nullptr || data == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_ADDRESS;
	}

	if (ev->filter != EventQueue::KERNEL_EVFILT_VIDEO_OUT) {
		return VIDEO_OUT_ERROR_INVALID_EVENT;
	}

	uint64_t event_data = static_cast<uint64_t>(ev->data) >> 16u;
	if (ev->ident == VIDEO_OUT_EVENT_FLIP &&
	    (static_cast<uint64_t>(ev->data) & 0x8000000000000000ULL) != 0) {
		event_data |= 0xffff000000000000ULL;
	}

	*data = static_cast<int64_t>(event_data);

	return OK;
}

KYTY_SYSV_ABI int VideoOutGetEventCount(const EventQueue::KernelEvent* ev) {
	PRINT_NAME();

	if (ev == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_ADDRESS;
	}

	if (ev->filter != EventQueue::KERNEL_EVFILT_VIDEO_OUT) {
		return VIDEO_OUT_ERROR_INVALID_EVENT;
	}

	return static_cast<int>((static_cast<uint64_t>(ev->data) >> 12u) & 0xfu);
}

KYTY_SYSV_ABI int VideoOutWaitVblank(int handle) {
	PRINT_NAME();

	auto* ctx = DriverState().Get(handle);
	if (ctx == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	Common::LockGuard lock(ctx->mutex);
	const auto        count = ctx->vblank_status.count;
	while (ctx->opened && ctx->vblank_status.count == count) {
		ctx->vblank_cond.Wait(&ctx->mutex);
	}

	return OK;
}

KYTY_SYSV_ABI int VideoOutGetOutputStatus(int handle, VideoOutOutputStatus* status) {
	PRINT_NAME();

	if (status == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_ADDRESS;
	}

	auto* ctx = DriverState().Get(handle);
	if (ctx == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	ctx->mutex.Lock();
	status->resolution   = (ctx->width >= 3840 || ctx->height >= 2160 ? 2u : 1u);
	status->dynamicRange = 1;
	status->refreshRate =
	    (ctx->output_mode == VIDEO_OUT_OUTPUT_MODE_119_88HZ || Config::GetVblankFrequency() >= 119
	         ? VIDEO_OUT_REFRESH_RATE_119_88HZ
	         : VIDEO_OUT_REFRESH_RATE_59_94HZ);
	status->flags       = 0;
	status->reserved[0] = 0;
	status->reserved[1] = 0;
	status->reserved[2] = 0;
	ctx->mutex.Unlock();

	return OK;
}

static int ValidateOutputConfig(int handle, uint64_t mode, const VideoOutOutputOptions* options,
                                void* reserved_ptr, uint64_t reserved) {
	if (!DriverState().IsOpened(handle)) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	if (reserved_ptr != nullptr || reserved != 0) {
		return VIDEO_OUT_ERROR_INVALID_VALUE;
	}

	if (options != nullptr) {
		for (auto v: options->internalData) {
			if (v != 0) {
				return VIDEO_OUT_ERROR_INVALID_OPTION;
			}
		}
	}

	if (mode != VIDEO_OUT_OUTPUT_MODE_DEFAULT && mode != VIDEO_OUT_OUTPUT_MODE_119_88HZ) {
		return VIDEO_OUT_ERROR_UNSUPPORTED_OUTPUT_MODE;
	}

	return OK;
}

KYTY_SYSV_ABI int VideoOutInitializeOutputOptions(VideoOutOutputOptions* options) {
	PRINT_NAME();

	if (options == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_ADDRESS;
	}

	memset(options, 0, sizeof(VideoOutOutputOptions));

	return OK;
}

KYTY_SYSV_ABI int VideoOutIsOutputSupported(int handle, uint64_t mode,
                                            const VideoOutOutputOptions* options,
                                            void* reserved_ptr, uint64_t reserved) {
	PRINT_NAME();

	LOGF("\t mode = 0x%016" PRIx64 "\n", mode);

	int result = ValidateOutputConfig(handle, mode, options, reserved_ptr, reserved);
	if (result != OK) {
		return result;
	}

	if (mode == VIDEO_OUT_OUTPUT_MODE_119_88HZ) {
		return (Config::GetVblankFrequency() >= 119 ? VIDEO_OUT_TRUE : VIDEO_OUT_FALSE);
	}

	return VIDEO_OUT_TRUE;
}

KYTY_SYSV_ABI int VideoOutConfigureOutput(int handle, uint64_t mode,
                                          const VideoOutOutputOptions* options, void* reserved_ptr,
                                          uint64_t reserved) {
	PRINT_NAME();

	LOGF("\t mode = 0x%016" PRIx64 "\n", mode);

	int result = VideoOutIsOutputSupported(handle, mode, options, reserved_ptr, reserved);
	if (result < 0) {
		return result;
	}
	if (result == VIDEO_OUT_FALSE) {
		return VIDEO_OUT_ERROR_UNAVAILABLE_OUTPUT_MODE;
	}

	auto* ctx = DriverState().Get(handle);
	if (ctx == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	ctx->mutex.Lock();
	ctx->output_mode = mode;
	TriggerVideoOutEvents(*ctx, VideoOutEventKind::OutputMode,
	                      reinterpret_cast<void*>(ctx->output_mode));
	ctx->mutex.Unlock();

	return OK;
}

KYTY_SYSV_ABI int VideoOutSetWindowModeMargins(int handle, int top, int bottom) {
	PRINT_NAME();

	[[maybe_unused]] auto* ctx = DriverState().Get(handle);
	if (ctx == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	LOGF("\t top    = %d\n"
	     "\t bottom = %d\n",
	     top, bottom);

	return OK;
}

KYTY_SYSV_ABI int VideoOutLatencyControlWaitBeforeInput(int handle) {
	PRINT_NAME();

	[[maybe_unused]] auto* ctx = DriverState().Get(handle);
	if (ctx == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	return OK;
}

KYTY_SYSV_ABI int VideoOutLatencyMeasureSetStartPoint(int handle, uint32_t point) {
	PRINT_NAME();

	[[maybe_unused]] auto* ctx = DriverState().Get(handle);
	if (ctx == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	LOGF("\t point = %" PRIu32 "\n", point);

	return OK;
}

KYTY_SYSV_ABI int VideoOutColorSettingsSetGamma(VideoOutColorSettings* settings, float gamma) {
	PRINT_NAME();

	if (settings == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_ADDRESS;
	}

	if (gamma < 0.1f || gamma > 2.0f) {
		return VIDEO_OUT_ERROR_INVALID_VALUE;
	}

	settings->gamma = gamma;
	return OK;
}

KYTY_SYSV_ABI int VideoOutAdjustColor(int handle, const VideoOutColorSettings* settings) {
	PRINT_NAME();

	if (settings == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_ADDRESS;
	}

	if (!DriverState().IsOpened(handle)) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}

	auto* ctx = DriverState().Get(handle);
	if (ctx == nullptr) {
		return VIDEO_OUT_ERROR_INVALID_HANDLE;
	}
	ctx->mutex.Lock();
	ctx->gamma = settings->gamma;
	ctx->mutex.Unlock();

	return OK;
}

} // namespace Libs::VideoOut
