#include "graphics/host_gpu/renderer/sync.h"

#include "common/assert.h"
#include "common/common.h"
#include "common/logging/log.h"
#include "common/threads.h"
#include "graphics/host_gpu/graphicContext.h"
#include "graphics/host_gpu/renderer/cache/bufferCache.h"
#include "graphics/host_gpu/renderer/render.h"
#include "graphics/host_gpu/renderer/renderContext.h"
#include "graphics/host_gpu/renderer/syncContracts.h"
#include "graphics/presentation/videoOut.h"
#include "kernel/eventQueue.h"
#include "kernel/pthread.h"
#include "libs/errno.h"

#include <array>
#include <cstring>
#include <limits>
#include <optional>

namespace Libs::Graphics::Sync {

constexpr int      GRAPHICS_EVENT_QUEUED_GRAPHICS_INTERRUPT = 0x00;
constexpr int      GRAPHICS_EVENT_EOP                       = 0x40;
constexpr uint64_t GRAPHICS_REFERENCE_CLOCK_FREQUENCY       = 100000000;

bool ScaleReferenceClock(uint64_t host_ticks, uint64_t host_frequency, uint64_t& value) {
	if (host_frequency == 0) {
		return false;
	}

	const auto     whole_seconds = host_ticks / host_frequency;
	const auto     remainder     = host_ticks % host_frequency;
	constexpr auto MAX_VALUE     = std::numeric_limits<uint64_t>::max();
	if (whole_seconds > MAX_VALUE / GRAPHICS_REFERENCE_CLOCK_FREQUENCY ||
	    remainder > MAX_VALUE / GRAPHICS_REFERENCE_CLOCK_FREQUENCY) {
		return false;
	}

	const auto whole_value      = whole_seconds * GRAPHICS_REFERENCE_CLOCK_FREQUENCY;
	const auto fractional_value = (remainder * GRAPHICS_REFERENCE_CLOCK_FREQUENCY) / host_frequency;
	if (whole_value > MAX_VALUE - fractional_value) {
		return false;
	}
	value = whole_value + fractional_value;
	return true;
}

uint64_t ReadReferenceClock() {
	const auto host_frequency = LibKernel::KernelGetTscFrequency();
	const auto host_ticks     = LibKernel::KernelReadTsc();
	uint64_t   value          = 0;
	if (!ScaleReferenceClock(host_ticks, host_frequency, value)) {
		EXIT("cannot scale host clock, ticks=0x%016" PRIx64 " frequency=%" PRIu64 "\n", host_ticks,
		     host_frequency);
	}
	return value;
}

enum class EndOfPipeCompletion { None, Interrupt, Flip, FlipAndInterrupt };

struct EndOfPipeSignal {
	CommandBuffer*          buffer          = nullptr;
	uint64_t                submit_id       = 0;
	CommandBufferDebugOp    debug_operation = CommandBufferDebugOp::Unknown;
	std::array<uint32_t, 4> debug_args      = {};
	uint64_t                debug_data      = 0;
	std::optional<uint64_t> destination;
	EndOfPipeWritePayload   write;
	EndOfPipeCompletion     completion      = EndOfPipeCompletion::None;
	uint64_t                completion_data = 0;
};

enum class EndOfPipeWriteAction { Write, WriteBack, Interrupt, InterruptWriteBack };

static void ValidateEndOfPipeSignal(const EndOfPipeSignal& signal) {
	if (signal.destination.has_value()) {
		EXIT_IF(*signal.destination == 0);
	}
	if (signal.write.size != EndOfPipeWriteSize::None) {
		EXIT_IF(signal.write.destination == nullptr);
	}
	EXIT_IF(signal.buffer == nullptr);
	(void)signal.buffer->Handle();
}

static void RecordEndOfPipeSignal(const EndOfPipeSignal& signal) {
	ValidateEndOfPipeSignal(signal);
	signal.buffer->SetDebugInfo(static_cast<uint32_t>(signal.debug_operation), signal.submit_id,
	                            signal.debug_args[0], signal.debug_args[1], signal.debug_args[2],
	                            signal.debug_args[3], signal.debug_data);

	auto& renderer  = signal.buffer->GetContext();
	auto& scheduler = renderer.GetCommandScheduler();
	if (signal.completion != EndOfPipeCompletion::None ||
	    signal.write.size != EndOfPipeWriteSize::None) {
		EXIT_IF(!scheduler.Active() || signal.buffer != &scheduler.Current());
	}
	if (signal.completion == EndOfPipeCompletion::None &&
	    signal.write.size == EndOfPipeWriteSize::None) {
		return;
	}
	const auto write           = signal.write;
	const auto completion      = signal.completion;
	const auto completion_data = signal.completion_data;
	scheduler.DeferPriorityOperation([&renderer, write, completion, completion_data] {
		const bool completed = CompleteEndOfPipeSignal(
		    write, EndOfPipeSignalStage::GpuComplete, [] { return ReadReferenceClock(); },
		    [&renderer, completion, completion_data] {
			    switch (completion) {
				    case EndOfPipeCompletion::None: return;
				    case EndOfPipeCompletion::Interrupt:
					    renderer.TriggerEopEvent(static_cast<uint32_t>(completion_data));
					    return;
				    case EndOfPipeCompletion::Flip:
					    renderer.GetVideoOut().CompleteFlip(completion_data);
					    return;
				    case EndOfPipeCompletion::FlipAndInterrupt:
					    renderer.GetVideoOut().CompleteFlip(completion_data);
					    renderer.TriggerEopEvent(0);
					    return;
			    }
		    });
		EXIT_IF(!completed);
	});
}

static CommandBufferDebugOp DebugOperation(EndOfPipeWriteAction action) {
	switch (action) {
		case EndOfPipeWriteAction::Write: return CommandBufferDebugOp::EopWrite;
		case EndOfPipeWriteAction::WriteBack:
		case EndOfPipeWriteAction::InterruptWriteBack: return CommandBufferDebugOp::EopWriteBack;
		case EndOfPipeWriteAction::Interrupt: return CommandBufferDebugOp::EopInterrupt;
	}
	EXIT("unsupported end-of-pipe write action\n");
	return CommandBufferDebugOp::Unknown;
}

static bool TriggersInterrupt(EndOfPipeWriteAction action) {
	return action == EndOfPipeWriteAction::Interrupt ||
	       action == EndOfPipeWriteAction::InterruptWriteBack;
}

static void RecordEndOfPipeWrite(uint64_t submit_id, CommandBuffer& buffer, uint64_t destination,
                                 uint64_t value, EndOfPipeWriteSize size,
                                 EndOfPipeWriteAction action, uint32_t context_id = 0,
                                 EndOfPipeWriteValueSource value_source =
                                     EndOfPipeWriteValueSource::Immediate) {
	const auto width      = static_cast<uint32_t>(size);
	const auto value_low  = static_cast<uint32_t>(value);
	const auto value_high = static_cast<uint32_t>(value >> 32u);
	const bool interrupt  = TriggersInterrupt(action);

	EndOfPipeSignal signal {
	    .buffer          = &buffer,
	    .submit_id       = submit_id,
	    .debug_operation = DebugOperation(action),
	    .debug_args      = interrupt ? std::array {width, context_id, value_low, value_high}
	                                 : std::array {width, value_low, value_high, 0u},
	    .debug_data      = destination,
	    .destination     = destination,
	    .write           = {reinterpret_cast<void*>(destination), value, size, value_source},
	    .completion      = interrupt ? EndOfPipeCompletion::Interrupt : EndOfPipeCompletion::None,
	    .completion_data = ResolveEndOfPipeInterruptContext(value, context_id),
	};
	RecordEndOfPipeSignal(signal);
}

void TriggerAgcUserInterrupt() {
	auto tsc    = LibKernel::KernelReadTsc();
	auto result = LibKernel::EventQueue::KernelTriggerUserEventForAll(AGC_USER_INTERRUPT_EVENT,
	                                                                  reinterpret_cast<void*>(tsc));
	EXIT_NOT_IMPLEMENTED(result != OK && result != LibKernel::KERNEL_ERROR_ENOENT);
}

void WriteAtEndOfPipe32(uint64_t submit_id, CommandBuffer& buffer, uint32_t* dst_gpu_addr,
                        uint32_t value) {
	RecordEndOfPipeWrite(submit_id, buffer, reinterpret_cast<uint64_t>(dst_gpu_addr), value,
	                     EndOfPipeWriteSize::Dword, EndOfPipeWriteAction::Write);
}

void WriteAtEndOfPipeGds32(uint64_t submit_id, CommandBuffer& buffer, uint32_t* dst_gpu_addr,
                           uint32_t dw_offset, uint32_t dw_num) {
	const auto destination = reinterpret_cast<uint64_t>(dst_gpu_addr);
	RecordEndOfPipeSignal({
	    .buffer          = &buffer,
	    .submit_id       = submit_id,
	    .debug_operation = CommandBufferDebugOp::EopWrite,
	    .debug_args      = {dw_offset, dw_num, 0, 0},
	    .debug_data      = destination,
	    .destination     = destination,
	});
}

void WriteAtEndOfPipe64(uint64_t submit_id, CommandBuffer& buffer, uint64_t* dst_gpu_addr,
                        uint64_t value) {
	RecordEndOfPipeWrite(submit_id, buffer, reinterpret_cast<uint64_t>(dst_gpu_addr), value,
	                     EndOfPipeWriteSize::Qword, EndOfPipeWriteAction::Write);
}

void WriteAtEndOfPipeClockCounter(uint64_t submit_id, CommandBuffer& buffer,
                                  uint64_t* dst_gpu_addr, bool with_interrupt,
                                  uint32_t context_id) {
	const auto action = with_interrupt ? EndOfPipeWriteAction::Interrupt
	                                   : EndOfPipeWriteAction::Write;
	RecordEndOfPipeWrite(submit_id, buffer, reinterpret_cast<uint64_t>(dst_gpu_addr), 0,
	                     EndOfPipeWriteSize::Qword, action, context_id,
	                     EndOfPipeWriteValueSource::ReferenceClock);

	LOGF_COLOR(Log::Color::BrightGreen,
	           "EndOfPipe Signal!!! [0x%016" PRIx64 "] <- queued reference clock\n",
	           reinterpret_cast<uint64_t>(dst_gpu_addr));
}

void WriteAtEndOfPipeClockCounterWithWriteBack(uint64_t submit_id, CommandBuffer& buffer,
                                               uint64_t* dst_gpu_addr, bool with_interrupt,
                                               uint32_t context_id) {
	const auto action = with_interrupt ? EndOfPipeWriteAction::InterruptWriteBack
	                                   : EndOfPipeWriteAction::WriteBack;
	RecordEndOfPipeWrite(submit_id, buffer, reinterpret_cast<uint64_t>(dst_gpu_addr), 0,
	                     EndOfPipeWriteSize::Qword, action, context_id,
	                     EndOfPipeWriteValueSource::ReferenceClock);

	LOGF_COLOR(Log::Color::BrightGreen,
	           "EndOfPipe Signal!!! [0x%016" PRIx64 "] <- queued reference clock\n",
	           reinterpret_cast<uint64_t>(dst_gpu_addr));
}

void WriteAtEndOfPipeWithWriteBack64(uint64_t submit_id, CommandBuffer& buffer,
                                     uint64_t* dst_gpu_addr, uint64_t value) {
	RecordEndOfPipeWrite(submit_id, buffer, reinterpret_cast<uint64_t>(dst_gpu_addr), value,
	                     EndOfPipeWriteSize::Qword, EndOfPipeWriteAction::WriteBack);
}

void WriteAtEndOfPipeWithWriteBack32(uint64_t submit_id, CommandBuffer& buffer,
                                     uint32_t* dst_gpu_addr, uint32_t value) {
	RecordEndOfPipeWrite(submit_id, buffer, reinterpret_cast<uint64_t>(dst_gpu_addr), value,
	                     EndOfPipeWriteSize::Dword, EndOfPipeWriteAction::WriteBack);
}

void SignalMemSemaphoreAtEndOfPipe(uint64_t submit_id, CommandBuffer& buffer, uint64_t* counter,
                                   MemSemaphoreSignalType signal_type) {
	EXIT_IF(counter == nullptr || (reinterpret_cast<uintptr_t>(counter) & 0x7u) != 0);
	EXIT_IF(!CompleteMemSemaphoreSignal(*counter, signal_type,
	                                    MemSemaphoreSignalStage::Recorded));

	buffer.SetDebugInfo(static_cast<uint32_t>(CommandBufferDebugOp::EopWrite), submit_id,
	                    sizeof(*counter), static_cast<uint32_t>(signal_type), 0, 0,
	                    reinterpret_cast<uint64_t>(counter));
	auto& scheduler = buffer.GetContext().GetCommandScheduler();
	EXIT_IF(!scheduler.Active() || &buffer != &scheduler.Current());
	scheduler.DeferPriorityOperation([counter, signal_type] {
		EXIT_IF(!CompleteMemSemaphoreSignal(*counter, signal_type,
		                                    MemSemaphoreSignalStage::GpuComplete));
	});
}

void WriteAtEndOfPipeWithInterruptWriteBack64(uint64_t submit_id, CommandBuffer& buffer,
                                              uint64_t* dst_gpu_addr, uint64_t value,
                                              uint32_t context_id) {
	RecordEndOfPipeWrite(submit_id, buffer, reinterpret_cast<uint64_t>(dst_gpu_addr), value,
	                     EndOfPipeWriteSize::Qword, EndOfPipeWriteAction::InterruptWriteBack,
	                     context_id);
}

void WriteAtEndOfPipeWithInterruptWriteBack32(uint64_t submit_id, CommandBuffer& buffer,
                                              uint32_t* dst_gpu_addr, uint32_t value,
                                              uint32_t context_id) {
	RecordEndOfPipeWrite(submit_id, buffer, reinterpret_cast<uint64_t>(dst_gpu_addr), value,
	                     EndOfPipeWriteSize::Dword, EndOfPipeWriteAction::InterruptWriteBack,
	                     context_id);
}

void WriteAtEndOfPipeWithInterrupt64(uint64_t submit_id, CommandBuffer& buffer,
                                     uint64_t* dst_gpu_addr, uint64_t value, uint32_t context_id) {
	RecordEndOfPipeWrite(submit_id, buffer, reinterpret_cast<uint64_t>(dst_gpu_addr), value,
	                     EndOfPipeWriteSize::Qword, EndOfPipeWriteAction::Interrupt, context_id);
}

void WriteAtEndOfPipeWithInterrupt32(uint64_t submit_id, CommandBuffer& buffer,
                                     uint32_t* dst_gpu_addr, uint32_t value, uint32_t context_id) {
	RecordEndOfPipeWrite(submit_id, buffer, reinterpret_cast<uint64_t>(dst_gpu_addr), value,
	                     EndOfPipeWriteSize::Dword, EndOfPipeWriteAction::Interrupt, context_id);
}

uint64_t PrepareVideoOutFlip(CommandBuffer& buffer, int handle, int index, int flip_mode,
                             int64_t flip_arg) {
	for (;;) {
		uint64_t   request_id = 0;
		auto&      video_out  = buffer.GetContext().GetVideoOut();
		const auto result =
		    video_out.SubmitFlipFromGpu(buffer, handle, index, flip_mode, flip_arg, request_id);
		if (result == OK) {
			EXIT_IF(request_id == 0);
			return request_id;
		}
		if (result != VideoOut::VIDEO_OUT_ERROR_FLIP_QUEUE_FULL) {
			EXIT("GPU flip submission failed, result=%d handle=%d index=%d mode=%d arg=%" PRId64
			     "\n",
			     result, handle, index, flip_mode, flip_arg);
		}
		video_out.WaitForSubmitSlot();
	}
}

void WriteAtEndOfPipeWithInterruptWriteBackFlip32(uint64_t submit_id, CommandBuffer& buffer,
                                                  uint32_t* dst_gpu_addr, uint32_t value,
                                                  int handle, int index, int flip_mode,
                                                  int64_t flip_arg, uint64_t request_id) {
	const auto destination = reinterpret_cast<uint64_t>(dst_gpu_addr);
	RecordEndOfPipeSignal({
	    .buffer          = &buffer,
	    .submit_id       = submit_id,
	    .debug_operation = CommandBufferDebugOp::EopWriteBackFlip,
	    .debug_args      = {static_cast<uint32_t>(handle), static_cast<uint32_t>(index),
	                        static_cast<uint32_t>(flip_mode), value},
	    .debug_data      = static_cast<uint64_t>(flip_arg),
	    .destination     = destination,
	    .write           = {dst_gpu_addr, value, EndOfPipeWriteSize::Dword},
	    .completion      = EndOfPipeCompletion::FlipAndInterrupt,
	    .completion_data = request_id,
	});
}

void WriteAtEndOfPipeWithFlip32(uint64_t submit_id, CommandBuffer& buffer, uint32_t* dst_gpu_addr,
                                uint32_t value, int handle, int index, int flip_mode,
                                int64_t flip_arg, uint64_t request_id) {
	const auto destination = reinterpret_cast<uint64_t>(dst_gpu_addr);
	RecordEndOfPipeSignal({
	    .buffer          = &buffer,
	    .submit_id       = submit_id,
	    .debug_operation = CommandBufferDebugOp::EopFlip,
	    .debug_args      = {static_cast<uint32_t>(handle), static_cast<uint32_t>(index),
	                        static_cast<uint32_t>(flip_mode), value},
	    .debug_data      = static_cast<uint64_t>(flip_arg),
	    .destination     = destination,
	    .write           = {dst_gpu_addr, value, EndOfPipeWriteSize::Dword},
	    .completion      = EndOfPipeCompletion::Flip,
	    .completion_data = request_id,
	});
}

void WriteAtEndOfPipeOnlyFlip(uint64_t submit_id, CommandBuffer& buffer, int handle, int index,
                              int flip_mode, int64_t flip_arg, uint64_t request_id) {
	RecordEndOfPipeSignal({
	    .buffer          = &buffer,
	    .submit_id       = submit_id,
	    .debug_operation = CommandBufferDebugOp::EopOnlyFlip,
	    .debug_args      = {static_cast<uint32_t>(handle), static_cast<uint32_t>(index),
	                        static_cast<uint32_t>(flip_mode), 0},
	    .debug_data      = static_cast<uint64_t>(flip_arg),
	    .completion      = EndOfPipeCompletion::Flip,
	    .completion_data = request_id,
	});
}

void TriggerEopEventAtEndOfPipe(CommandBuffer& buffer, uint32_t context_id) {
	ValidateEndOfPipeSignal({.buffer = &buffer});
	auto& renderer  = buffer.GetContext();
	auto& scheduler = renderer.GetCommandScheduler();
	EXIT_IF(!scheduler.Active() || &buffer != &scheduler.Current());
	const auto resolved_context_id = ResolveEndOfPipeInterruptContext(0, context_id);
	scheduler.DeferPriorityOperation(
	    [&renderer, resolved_context_id] { renderer.TriggerEopEvent(resolved_context_id); });
}

void TriggerEopEventIfLessOrEqual32AtEndOfPipe(CommandBuffer& buffer, const void* address,
                                               uint32_t reference, uint32_t context_id) {
	ValidateEndOfPipeSignal({.buffer = &buffer});
	EXIT_IF(address == nullptr);
	auto& renderer  = buffer.GetContext();
	auto& scheduler = renderer.GetCommandScheduler();
	EXIT_IF(!scheduler.Active() || &buffer != &scheduler.Current());
	const EndOfPipeCompare32Payload compare {.address = address, .reference = reference};
	const auto resolved_context_id = ResolveEndOfPipeInterruptContext(0, context_id);
	scheduler.DeferPriorityOperation([&renderer, compare, resolved_context_id] {
		const bool completed = CompleteEndOfPipeCompare32Interrupt(
		    compare, EndOfPipeSignalStage::GpuComplete,
		    [&renderer, resolved_context_id] { renderer.TriggerEopEvent(resolved_context_id); });
		EXIT_IF(!completed);
	});
}

void TriggerEopEventIfLessOrEqual64AtEndOfPipe(CommandBuffer& buffer, const void* address,
                                               uint64_t reference, uint32_t context_id) {
	ValidateEndOfPipeSignal({.buffer = &buffer});
	EXIT_IF(address == nullptr);
	auto& renderer  = buffer.GetContext();
	auto& scheduler = renderer.GetCommandScheduler();
	EXIT_IF(!scheduler.Active() || &buffer != &scheduler.Current());
	const EndOfPipeCompare64Payload compare {.address = address, .reference = reference};
	const auto resolved_context_id = ResolveEndOfPipeInterruptContext(0, context_id);
	scheduler.DeferPriorityOperation([&renderer, compare, resolved_context_id] {
		const bool completed = CompleteEndOfPipeCompare64Interrupt(
		    compare, EndOfPipeSignalStage::GpuComplete,
		    [&renderer, resolved_context_id] { renderer.TriggerEopEvent(resolved_context_id); });
		EXIT_IF(!completed);
	});
}

static void EopEventResetFunc(LibKernel::EventQueue::KernelEqueueEvent* event) {
	EXIT_IF(event == nullptr);
	event->triggered    = false;
	event->event.fflags = 0;
	event->event.data   = 0;
}

static void EopEventDeleteFunc(LibKernel::EventQueue::KernelEqueue       eq,
                               LibKernel::EventQueue::KernelEqueueEvent* event) {
	EXIT_IF(event == nullptr);
	EXIT_NOT_IMPLEMENTED(event->event.filter != LibKernel::EventQueue::KERNEL_EVFILT_GRAPHICS);
	if (event->event.ident == GRAPHICS_EVENT_QUEUED_GRAPHICS_INTERRUPT ||
	    event->event.ident == GRAPHICS_EVENT_EOP) {
		auto* renderer = static_cast<RenderContext*>(event->filter.data);
		EXIT_IF(renderer == nullptr);
		renderer->DeleteEopEq(eq, static_cast<int>(event->event.ident));
	}
}

static void EopEventTriggerFunc(LibKernel::EventQueue::KernelEqueueEvent* event,
                                void*                                     trigger_data) {
	EXIT_IF(event == nullptr);

	auto triggered_event = event->event;
	triggered_event.fflags++;
	triggered_event.data = reinterpret_cast<intptr_t>(trigger_data);
	if (event->triggered) {
		event->pending_events.push_back(triggered_event);
	} else {
		event->event     = triggered_event;
		event->triggered = true;
	}
}

int AddEqEvent(RenderContext& renderer, LibKernel::EventQueue::KernelEqueue eq, int id,
               void* udata) {
	LibKernel::EventQueue::KernelEqueueEvent event;
	event.triggered                = false;
	event.event.ident              = static_cast<uintptr_t>(id);
	event.event.filter             = LibKernel::EventQueue::KERNEL_EVFILT_GRAPHICS;
	event.event.udata              = udata;
	event.event.fflags             = 0;
	event.event.data               = id;
	event.filter.delete_event_func = EopEventDeleteFunc;
	event.filter.reset_func        = EopEventResetFunc;
	event.filter.trigger_func      = EopEventTriggerFunc;
	event.filter.data              = &renderer;

	int result = LibKernel::EventQueue::KernelAddEvent(eq, event);

	if (result == 0 &&
	    (id == GRAPHICS_EVENT_QUEUED_GRAPHICS_INTERRUPT || id == GRAPHICS_EVENT_EOP)) {
		renderer.AddEopEq(eq, id);
	}

	return result;
}

int DeleteEqEvent(LibKernel::EventQueue::KernelEqueue eq, int id) {
	int result = LibKernel::EventQueue::KernelDeleteEvent(
	    eq, static_cast<uintptr_t>(id), LibKernel::EventQueue::KERNEL_EVFILT_GRAPHICS);

	return result;
}

void ReadGds(Buffer& gds, uint32_t* dst, uint32_t dw_offset, uint32_t dw_size) {
	const auto offset = uint64_t {dw_offset} * sizeof(uint32_t);
	const auto size   = uint64_t {dw_size} * sizeof(uint32_t);
	EXIT_IF(dst == nullptr || offset > gds.Size() || size > gds.Size() - offset ||
	        gds.Mapped().empty());
	std::memcpy(dst, gds.Mapped().data() + offset, static_cast<size_t>(size));
}

} // namespace Libs::Graphics::Sync
