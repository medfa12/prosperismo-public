#include "graphics/host_gpu/renderer/image/imageInfo.h"
#include "graphics/presentation/videoOut.h"
#include "graphics/presentation/videoOutContracts.h"

#include <cstdio>
#include <cstdlib>
#include <limits>
#include <type_traits>

namespace {

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "Prosperismo VideoOut contract test failed: %s\n", text);
		std::abort();
	}
}

void TestSubmitFlipModeValidation() {
	using namespace Libs::VideoOut;

	constexpr int valid_modes[] = {VIDEO_OUT_FLIP_MODE_VSYNC, VIDEO_OUT_FLIP_MODE_ASAP,
	                               VIDEO_OUT_FLIP_MODE_WINDOW,
	                               VIDEO_OUT_FLIP_MODE_VSYNC_MULTI};
	for (const int mode: valid_modes) {
		Check(ValidatePrimaryFlipMode(mode) == OK, "a primary flip mode was rejected");
	}

	constexpr int invalid_modes[] = {0, -1, 10, std::numeric_limits<int>::max()};
	for (const int mode: invalid_modes) {
		Check(ValidatePrimaryFlipMode(mode) == VIDEO_OUT_ERROR_INVALID_FLIP_MODE,
		      "an invalid flip mode returned the wrong error");
		Check(ValidatePrimaryFlipMode(mode) != VIDEO_OUT_ERROR_INVALID_VALUE,
		      "an invalid flip mode returned generic invalid-value");
	}
}

void TestPrimaryFlipRateThree() {
	using namespace Libs::VideoOut;

	int        stored_rate       = 0;
	const auto set_if_valid_rate = [&stored_rate](int rate) {
		const int result = ValidatePrimaryFlipRate(rate);
		if (result == OK) {
			stored_rate = rate;
		}
		return result;
	};

	Check(set_if_valid_rate(3) == OK, "the documented flip-rate upper boundary was rejected");
	Check(stored_rate == 3, "the accepted flip-rate upper boundary was not stored");
	Check(IsFlipRateIntervalDue(0, stored_rate), "rate 3 rejected the initial eligible vblank");
	for (uint64_t vblank = 1; vblank < 4; vblank++) {
		Check(!IsFlipRateIntervalDue(vblank, stored_rate),
		      "rate 3 became eligible before four vblank intervals");
	}
	Check(IsFlipRateIntervalDue(4, stored_rate),
	      "rate 3 was not eligible on the fourth vblank interval");

	Check(set_if_valid_rate(4) == VIDEO_OUT_ERROR_INVALID_VALUE,
	      "an above-range flip rate returned the wrong error");
	Check(stored_rate == 3, "an above-range flip rate mutated the active rate");
	Check(set_if_valid_rate(-1) == VIDEO_OUT_ERROR_INVALID_VALUE,
	      "a negative flip rate returned the wrong error");
	Check(stored_rate == 3, "a negative flip rate mutated the active rate");
}

void TestAsapFlipIgnoresConfiguredRate() {
	using namespace Libs::VideoOut;

	constexpr int RATE_THREE = 3;
	for (uint64_t vblank = 1; vblank < 4; vblank++) {
		Check(IsFlipRequestDue(vblank, RATE_THREE, VIDEO_OUT_FLIP_MODE_ASAP),
		      "an ASAP flip was throttled by the configured flip rate");
		Check(!IsFlipRequestDue(vblank, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC),
		      "the ASAP exception disabled the configured VSYNC cadence");
	}
	Check(IsFlipRequestDue(4, RATE_THREE, VIDEO_OUT_FLIP_MODE_ASAP),
	      "an ASAP flip was rejected at a rate-aligned presenter opportunity");
	Check(IsFlipRequestDue(4, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC),
	      "the established VSYNC rate-three cadence changed");
}

void TestVsyncMultiFlipIgnoresConfiguredRate() {
	using namespace Libs::VideoOut;

	constexpr int RATE_THREE = 3;
	for (uint64_t vblank = 1; vblank < 4; vblank++) {
		Check(IsFlipRequestDue(vblank, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC_MULTI),
		      "a VSYNC_MULTI flip was throttled by the configured flip rate");
		Check(!IsFlipRequestDue(vblank, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC),
		      "the VSYNC_MULTI exception disabled the configured VSYNC cadence");
	}
	Check(IsFlipRequestDue(4, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC_MULTI),
	      "a VSYNC_MULTI flip was rejected at a rate-aligned vblank");
	Check(IsFlipRequestDue(4, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC),
	      "the established VSYNC rate-three control changed");
}

void TestFirstFlipUsesImmediateCadence() {
	using namespace Libs::VideoOut;

	constexpr int RATE_THREE = 3;
	for (uint64_t vblank = 1; vblank < 4; vblank++) {
		Check(IsLifecycleFlipRequestDue(vblank, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC, 0),
		      "the first flip was delayed by the configured flip rate");
		Check(!IsLifecycleFlipRequestDue(vblank, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC, 1),
		      "the first-flip exception changed the established steady-state cadence");
	}
	Check(IsLifecycleFlipRequestDue(4, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC, 1),
	      "steady-state VSYNC was rejected at its configured cadence");
	Check(!IsLifecycleFlipRequestDue(1, RATE_THREE, 0, 0),
	      "the first-flip exception accepted an invalid mode");
	Check(!IsLifecycleFlipRequestDue(1, 4, VIDEO_OUT_FLIP_MODE_VSYNC, 0),
	      "the first-flip exception accepted an invalid rate");
}

void TestBufferSetTransitionUsesImmediateCadence() {
	using namespace Libs::VideoOut;

	constexpr bool transition = IsRegisteredBufferSetTransition(2, 7, 0, 1);
	Check(transition, "a flip across registered buffer sets was not identified");
	Check(!IsRegisteredBufferSetTransition(2, 7, 1, 1),
	      "a flip within one registered buffer set was misidentified");
	Check(!IsRegisteredBufferSetTransition(-1, 7, -1, 1),
	      "a transition from blank output was treated as a buffer-set transition");
	Check(!IsRegisteredBufferSetTransition(2, -2, 0, -1),
	      "a transition to black output was treated as a buffer-set transition");

	constexpr int RATE_THREE = 3;
	for (uint64_t vblank = 1; vblank < 4; vblank++) {
		Check(IsLifecycleFlipRequestDue(vblank, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC, 4,
		                                transition),
		      "a cross-set flip was delayed by the configured flip rate");
		Check(!IsLifecycleFlipRequestDue(vblank, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC, 4,
		                                 false),
		      "the cross-set exception changed same-set steady-state cadence");
	}
	Check(IsLifecycleFlipRequestDue(4, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC, 4, false),
	      "same-set VSYNC was rejected at its configured cadence");
}

void TestFlipToBlankUsesImmediateCadence() {
	using namespace Libs::VideoOut;

	constexpr bool blank_destination = IsBlankOutputDestination(VIDEO_OUT_BUFFER_INDEX_BLANK);
	Check(blank_destination, "the dedicated blank-output index was not identified");
	Check(!IsBlankOutputDestination(-2), "black output was treated as blank output");
	Check(!IsBlankOutputDestination(0), "a registered display buffer was treated as blank output");

	constexpr int RATE_THREE = 3;
	for (uint64_t vblank = 1; vblank < 4; vblank++) {
		Check(IsLifecycleFlipRequestDue(vblank, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC, 4,
		                                false, blank_destination),
		      "a flip to blank output was delayed by the configured flip rate");
		Check(!IsLifecycleFlipRequestDue(vblank, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC, 4,
		                                 false, false),
		      "the blank-output exception changed registered-buffer cadence");
	}
	Check(IsLifecycleFlipRequestDue(4, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC, 4, false, false),
	      "registered-buffer VSYNC was rejected at its configured cadence");
}

void TestFlipFromBlankUsesImmediateCadence() {
	using namespace Libs::VideoOut;

	constexpr bool blank_exit =
	    IsBlankToRegisteredBufferTransition(VIDEO_OUT_BUFFER_INDEX_BLANK, 3, 1);
	Check(blank_exit, "a transition from blank output to a registered buffer was not identified");
	Check(!IsBlankToRegisteredBufferTransition(-2, 3, 1),
	      "a transition from black output was treated as a blank exit");
	Check(!IsBlankToRegisteredBufferTransition(VIDEO_OUT_BUFFER_INDEX_BLANK, -2, -1),
	      "a transition from blank to black output was treated as a registered-buffer exit");
	Check(!IsBlankToRegisteredBufferTransition(VIDEO_OUT_BUFFER_INDEX_BLANK, 3, -1),
	      "an unregistered destination was treated as a valid blank exit");

	constexpr int RATE_THREE = 3;
	for (uint64_t vblank = 1; vblank < 4; vblank++) {
		Check(IsLifecycleFlipRequestDue(vblank, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC, 4, false,
		                                false, blank_exit),
		      "a flip from blank output was delayed by the configured flip rate");
		Check(!IsLifecycleFlipRequestDue(vblank, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC, 4,
		                                 false, false, false),
		      "the blank-exit exception changed ordinary registered-buffer cadence");
	}
	Check(IsLifecycleFlipRequestDue(4, RATE_THREE, VIDEO_OUT_FLIP_MODE_VSYNC, 4, false, false,
	                                false),
	      "ordinary registered-buffer VSYNC was rejected at its configured cadence");
}

void TestSafeForRenderingWaitDoesNotExpire() {
	using namespace Libs::VideoOut;

	for (int observation = 0; observation < 1024; observation++) {
		Check(ShouldWaitUntilSafeForRendering(true),
		      "a pending display buffer became safe without flip completion");
	}
	Check(!ShouldWaitUntilSafeForRendering(false),
	      "a completed display buffer continued waiting");
}

void TestVideoOutEventCoalescing() {
	using namespace Libs::VideoOut;

	auto event = AccumulateVideoOutEvent({}, 0x1234, 0x111);
	Check(event.triggered, "the first VideoOut occurrence did not trigger its event");
	Check(event.occurrence_count == 1,
	      "the first VideoOut occurrence did not publish count one");

	event = AccumulateVideoOutEvent(event, 0x5678, 0x222);
	Check(event.occurrence_count == 2,
	      "coalesced VideoOut occurrences did not publish their combined count");
	Check(((event.encoded_data >> 12u) & 0xfu) == 2,
	      "the encoded VideoOut event count disagreed with its occurrence count");
	Check((event.encoded_data >> 16u) == 0x5678,
	      "a coalesced VideoOut event retained a stale payload");
	Check((event.encoded_data & 0xfffu) == 0x222,
	      "a coalesced VideoOut event retained a stale timestamp");

	for (uint64_t occurrence = 3; occurrence <= 20; occurrence++) {
		event = AccumulateVideoOutEvent(event, 0x1000 + occurrence, occurrence);
	}
	Check(event.occurrence_count == 15,
	      "the VideoOut occurrence count did not saturate at fifteen");
	Check(((event.encoded_data >> 12u) & 0xfu) == 15,
	      "the encoded VideoOut event count did not saturate at fifteen");
	Check((event.encoded_data >> 16u) == 0x1014,
	      "a saturated VideoOut event did not retain the last payload");
}

void TestOutputModeEventRegistrationStartsIdle() {
	using namespace Libs::VideoOut;

	const auto registered = InitializeVideoOutEventRegistration();
	Check(!registered.triggered,
	      "registering an output-mode event synthesized an initial occurrence");
	Check(registered.occurrence_count == 0,
	      "registering an output-mode event published an initial count");
	Check(registered.encoded_data == 0,
	      "registering an output-mode event published initial event data");

	const auto changed = AccumulateVideoOutEvent(registered, 0x0d, 0x123);
	Check(changed.triggered, "an actual output-mode change did not trigger its event");
	Check(changed.occurrence_count == 1,
	      "the first actual output-mode change did not publish count one");
	Check((changed.encoded_data >> 16u) == 0x0d,
	      "an actual output-mode change did not publish its latest payload");
}

void TestOutputModeEventDeleteApiSignature() {
	using namespace Libs::VideoOut;

	static_assert(std::is_same_v<decltype(&VideoOutDeleteOutputModeEvent),
	                             decltype(&VideoOutDeleteFlipEvent)>);
}

void TestGpuCompletionLeavesFlipPendingUntilPresentation() {
	using namespace Libs::VideoOut;

	const auto one_ready = CompleteFlipExecution({1, 1}, true);
	Check(one_ready.gc_queue_num == 0,
	      "a GPU-complete flip remained in the graphics-command queue");
	Check(one_ready.flip_pending_num == 1,
	      "GPU completion incorrectly completed the pending presentation");

	const auto one_of_two_ready = CompleteFlipExecution({2, 2}, true);
	Check(one_of_two_ready.gc_queue_num == 1,
	      "GPU completion removed more than one graphics-command request");
	Check(one_of_two_ready.flip_pending_num == 2,
	      "GPU completion changed the total pending presentation count");

	const auto cpu_ready = CompleteFlipExecution({1, 2}, false);
	Check(cpu_ready.gc_queue_num == 1 && cpu_ready.flip_pending_num == 2,
	      "CPU flip completion consumed another request's GPU queue count");

	const auto cancelled = CompleteFlipExecution({0, 0}, true);
	Check(cancelled.gc_queue_num == 0 && cancelled.flip_pending_num == 0,
	      "a canceled GPU flip underflowed queue counters during late completion");
}

void TestFlipSubmitCounterPublicationSequence() {
	using namespace Libs::VideoOut;

	auto gpu = BeginFlipSubmitCounter(70, 100, true);
	Check(gpu.published == 70,
	      "GPU reservation replaced the last completed flip's submit counter");
	Check(gpu.request == 0, "GPU reservation sampled before command completion");
	gpu = CompleteFlipSubmitCounter(gpu, 200, true);
	Check(gpu.published == 70,
	      "GPU command completion published a flip that had not been presented");
	Check(gpu.request == 200, "GPU completion did not capture the flip request counter");
	gpu = PublishFlipSubmitCounter(gpu);
	Check(gpu.published == 200, "presentation did not publish the GPU flip request counter");

	auto cpu = BeginFlipSubmitCounter(70, 100, false);
	Check(cpu.published == 70 && cpu.request == 100,
	      "CPU reservation did not preserve its API-call counter privately");
	cpu = CompleteFlipSubmitCounter(cpu, 200, false);
	Check(cpu.published == 70 && cpu.request == 100,
	      "CPU flip preparation replaced the API-call counter");
	cpu = PublishFlipSubmitCounter(cpu);
	Check(cpu.published == 100, "presentation did not publish the CPU API-call counter");
}

void TestMixedFlipRequestOrdering() {
	using namespace Libs::VideoOut;

	constexpr uint64_t gpu_reserved = CaptureFlipRequestOrderAtReserve(1, true);
	constexpr uint64_t cpu_requested = CaptureFlipRequestOrderAtReserve(1, false);
	constexpr uint64_t gpu_requested =
	    CaptureFlipRequestOrderAtCompletion(gpu_reserved, 2, true);
	Check(gpu_reserved == 0, "AGC reservation became a flip request before command execution");
	Check(cpu_requested == 1 && gpu_requested == 2,
	      "mixed CPU/AGC requests were not sequenced at their actual request boundaries");
	Check(FlipRequestOrderPrecedes(cpu_requested, gpu_requested),
	      "an earlier CPU request did not precede later AGC command execution");
	Check(ShouldDeferFlipForPendingCpu(gpu_requested, cpu_requested),
	      "a later ready AGC flip bypassed an earlier CPU request still being prepared");

	constexpr uint64_t first_gpu =
	    CaptureFlipRequestOrderAtCompletion(CaptureFlipRequestOrderAtReserve(1, true), 1, true);
	constexpr uint64_t later_cpu = CaptureFlipRequestOrderAtReserve(2, false);
	Check(FlipRequestOrderPrecedes(first_gpu, later_cpu),
	      "an AGC command that executed first did not retain request priority");
	Check(!ShouldDeferFlipForPendingCpu(first_gpu, later_cpu),
	      "a later CPU request blocked an earlier ready AGC flip");
	Check(FlipRequestOrderPrecedes(cpu_requested, 0),
	      "an actual request did not precede an AGC reservation that has not executed");
}

void TestInUseBufferSetCannotBeUnregistered() {
	using namespace Libs::VideoOut;

	Check(ValidateUnregisterBufferSet(2, 2, false) == VIDEO_OUT_ERROR_RESOURCE_BUSY,
	      "the displayed buffer set was unregistered");
	Check(ValidateUnregisterBufferSet(2, 1, true) == VIDEO_OUT_ERROR_RESOURCE_BUSY,
	      "a buffer set targeted by a pending flip was unregistered");
	Check(ValidateUnregisterBufferSet(2, -1, true) == VIDEO_OUT_ERROR_RESOURCE_BUSY,
	      "a pending buffer set was unregistered while blank output was displayed");
	Check(ValidateUnregisterBufferSet(2, 1, false) == OK,
	      "a buffer set not used for display was rejected");
	Check(ValidateUnregisterBufferSet(2, -1, false) == OK,
	      "a buffer set was rejected while blank output was displayed");

	Check(IsFlipRequestUsingBufferSet(true, 5, 16, 2, 2),
	      "a queued flip did not retain its target buffer set");
	Check(!IsFlipRequestUsingBufferSet(true, 5, 16, 1, 2),
	      "a queued flip retained an unrelated buffer set");
	Check(!IsFlipRequestUsingBufferSet(true, -1, 16, -1, 2),
	      "a blank flip retained a registered buffer set");
	Check(!IsFlipRequestUsingBufferSet(false, 5, 16, 2, 2),
	      "a flip from another port retained this port's buffer set");
}

void TestLinearTenBitPixelFormats() {
	using namespace Libs::Graphics;

	constexpr uint64_t LINEAR_R10_G10_B10_A2 = 0x8100000622000000ull;
	constexpr uint64_t SRGB_R10_G10_B10_A2   = 0x8100000022000000ull;
	constexpr uint64_t LINEAR_B10_G10_R10_A2 = 0x8100000600000000ull;
	constexpr uint64_t SRGB_B10_G10_R10_A2   = 0x8100000000000000ull;

	VideoOutPixelFormatInfo linear_r {};
	VideoOutPixelFormatInfo srgb_r {};
	Check(DecodeVideoOutPixelFormat(LINEAR_R10_G10_B10_A2, linear_r),
	      "the linear R10G10B10A2 format was rejected");
	Check(DecodeVideoOutPixelFormat(SRGB_R10_G10_B10_A2, srgb_r),
	      "the sRGB R10G10B10A2 control format was rejected");
	Check(linear_r.format == srgb_r.format && linear_r.guest_format == srgb_r.guest_format &&
	          linear_r.bytes_per_element == srgb_r.bytes_per_element &&
	          linear_r.bgra16 == srgb_r.bgra16,
	      "linear R10G10B10A2 did not preserve the established storage layout");

	VideoOutPixelFormatInfo linear_b {};
	VideoOutPixelFormatInfo srgb_b {};
	Check(DecodeVideoOutPixelFormat(LINEAR_B10_G10_R10_A2, linear_b),
	      "the linear B10G10R10A2 format was rejected");
	Check(DecodeVideoOutPixelFormat(SRGB_B10_G10_R10_A2, srgb_b),
	      "the sRGB B10G10R10A2 control format was rejected");
	Check(linear_b.format == srgb_b.format && linear_b.guest_format == srgb_b.guest_format &&
	          linear_b.bytes_per_element == srgb_b.bytes_per_element &&
	          linear_b.bgra16 == srgb_b.bgra16,
	      "linear B10G10R10A2 did not preserve the established storage layout");

	VideoOutPixelFormatInfo unknown {};
	Check(!DecodeVideoOutPixelFormat(0xdeadbeefull, unknown),
	      "an unknown VideoOut pixel format was accepted");
}

void TestTiledBufferAddressAlignment() {
	using namespace Libs::VideoOut;

	int        committed_set       = -1;
	const auto register_if_aligned = [&committed_set](uint64_t address, int set_index) {
		const int result = ValidateTiledBufferAddressAlignment(address);
		if (result == OK) {
			committed_set = set_index;
		}
		return result;
	};

	Check(register_if_aligned(0x10000, 1) == OK, "a 64 KiB-aligned tile buffer was rejected");
	Check(ValidateTiledBufferAddressAlignment(0x12340000) == OK,
	      "a high aligned tile buffer was rejected");
	Check(register_if_aligned(0x10001, 2) == VIDEO_OUT_ERROR_MEMORY_INVALID_ALIGNMENT,
	      "a misaligned tile buffer returned the wrong result");
	Check(committed_set == 1, "a failed tile-buffer validation mutated registration state");
	Check(ValidateTiledBufferAddressAlignment(0xffff) == VIDEO_OUT_ERROR_MEMORY_INVALID_ALIGNMENT,
	      "a tile buffer below the first alignment boundary was accepted");
}

void TestUnknownPixelFormatRegistrationError() {
	using namespace Libs::Graphics;
	using namespace Libs::VideoOut;

	VideoOutPixelFormatInfo decoded {};
	int                     committed_set = 1;
	const auto register_if_supported      = [&decoded, &committed_set](uint64_t pixel_format,
	                                                                   int      set_index) {
		const int result =
		    ValidateRegisterPixelFormat(DecodeVideoOutPixelFormat(pixel_format, decoded));
		if (result == OK) {
			committed_set = set_index;
		}
		return result;
	};

	Check(register_if_supported(0xdeadbeefull, 2) == VIDEO_OUT_ERROR_INVALID_PIXEL_FORMAT,
	      "an unknown display-buffer format returned the wrong registration error");
	Check(committed_set == 1, "an unknown display-buffer format mutated registration state");
	Check(register_if_supported(0x8100000022000000ull, 3) == OK,
	      "a supported display-buffer format was rejected");
	Check(committed_set == 3, "a supported display-buffer format did not reach registration");
}

void TestUnsupportedTilingModeRegistrationError() {
	using namespace Libs::VideoOut;

	bool       prepared_image = false;
	int        committed_set  = -1;
	const auto register_mode  = [&prepared_image, &committed_set](uint32_t tiling_mode,
	                                                              int      set_index) {
		const int result = ValidateRegisterTilingMode(tiling_mode);
		if (result == OK) {
			prepared_image = true;
			committed_set  = set_index;
		}
		return result;
	};

	Check(register_mode(0, 1) == OK, "ordinary tile-mode registration was rejected");
	Check(prepared_image && committed_set == 1,
	      "ordinary tile mode did not reach image preparation");
	prepared_image = false;
	Check(register_mode(1, 2) == VIDEO_OUT_ERROR_INVALID_TILING_MODE,
	      "unsupported linear mode returned the wrong registration error");
	Check(!prepared_image && committed_set == 1,
	      "unsupported linear mode reached image preparation or mutated registration state");
	Check(register_mode(std::numeric_limits<uint32_t>::max(), 3) ==
	          VIDEO_OUT_ERROR_INVALID_TILING_MODE,
	      "an unknown tiling mode returned the wrong registration error");
	Check(!prepared_image && committed_set == 1,
	      "an unknown tiling mode reached image preparation or mutated registration state");
}

void TestDisplayBufferResolutionRegistrationError() {
	using namespace Libs::VideoOut;

	struct Resolution {
		uint32_t width;
		uint32_t height;
	};
	constexpr Resolution ORDINARY_PRIMARY_RESOLUTIONS[] = {
	    {3840, 2160}, {3680, 2070}, {3520, 1980}, {3360, 1890}, {3200, 1800},
	    {2880, 1620}, {2560, 1440}, {2240, 1260}, {1920, 1080},
	};

	int        committed_set = -1;
	const auto register_resolution = [&committed_set](Resolution resolution, int set_index) {
		const int result = ValidateRegisterResolution(resolution.width, resolution.height);
		if (result == OK) {
			committed_set = set_index;
		}
		return result;
	};
	for (const auto resolution: ORDINARY_PRIMARY_RESOLUTIONS) {
		Check(register_resolution(resolution, 1) == OK,
		      "a documented ordinary-primary resolution was rejected");
	}
	Check(committed_set == 1, "valid resolutions did not reach registration");

	constexpr Resolution invalid[] = {{0, 1080}, {1920, 0}, {1, 1}, {1920, 1081}, {1280, 720}};
	for (const auto resolution: invalid) {
		Check(register_resolution(resolution, 2) == VIDEO_OUT_ERROR_INVALID_RESOLUTION,
		      "an unavailable display-buffer resolution returned the wrong error");
		Check(committed_set == 1,
		      "an unavailable display-buffer resolution mutated registration state");
	}
}

void TestNullBufferDataRegistrationError() {
	using namespace Libs::VideoOut;

	int        committed_set       = 1;
	const auto register_if_present = [&committed_set](uint64_t data_address, int set_index) {
		const int result = ValidateRegisterBufferDataAddress(data_address);
		if (result == OK) {
			committed_set = set_index;
		}
		return result;
	};

	Check(register_if_present(0, 2) == VIDEO_OUT_ERROR_INVALID_MEMORY,
	      "a null display-buffer address returned the wrong registration error");
	Check(committed_set == 1, "a null display-buffer address mutated registration state");
	Check(register_if_present(0x10000, 3) == OK,
	      "a present display-buffer address was rejected by the null-address preflight");
	Check(committed_set == 3, "a present display-buffer address did not reach registration");
}

void TestDuplicateBufferSetRegistrationError() {
	using namespace Libs::VideoOut;

	bool       prepared_image = false;
	bool       mutated_set    = false;
	const auto register_set   = [&prepared_image, &mutated_set](bool occupied) {
		const int result = ValidateRegisterBufferSet(occupied);
		if (result != OK) {
			return result;
		}
		prepared_image = true;
		mutated_set    = true;
		return OK;
	};

	Check(register_set(true) == VIDEO_OUT_ERROR_SLOT_OCCUPIED,
	      "a duplicate buffer set returned the wrong registration error");
	Check(!prepared_image, "a duplicate buffer set reached image preparation");
	Check(!mutated_set, "a duplicate buffer set mutated registration state");
	Check(register_set(false) == OK, "an empty buffer set was rejected");
	Check(prepared_image && mutated_set, "an empty buffer set did not reach registration");
}

void TestRegisterBufferTotalRangeErrorOrdering() {
	using namespace Libs::VideoOut;

	constexpr int BUFFER_COUNT = 16;
	int           committed_set = 1;
	const auto register_range = [&committed_set](int start, int count, bool occupied,
	                                             int set_index) {
		const int domain_result = ValidateRegisterBufferDomain(start, count, BUFFER_COUNT);
		if (domain_result != OK) {
			return domain_result;
		}
		const int range_result =
		    ValidateRegisterBufferSetAndTotalRange(occupied, start, count, BUFFER_COUNT);
		if (range_result == OK) {
			committed_set = set_index;
		}
		return range_result;
	};

	Check(register_range(15, 2, true, 2) == VIDEO_OUT_ERROR_SLOT_OCCUPIED,
	      "an occupied set did not take precedence over a total-range overflow");
	Check(committed_set == 1, "an occupied overflowing registration mutated state");
	Check(register_range(15, 2, false, 2) == VIDEO_OUT_ERROR_INVALID_VALUE,
	      "an empty-set total-range overflow returned the wrong error");
	Check(committed_set == 1, "an empty-set total-range overflow mutated state");
	Check(register_range(14, 2, false, 3) == OK,
	      "the highest valid two-buffer range was rejected");
	Check(committed_set == 3, "a valid edge range did not reach registration");
}

void TestRegisterBufferSetIndexError() {
	using namespace Libs::VideoOut;

	constexpr int SET_COUNT         = 4;
	int           committed_set     = -1;
	const auto    register_if_valid = [&committed_set](int set_index) {
		const int result = ValidateRegisterBufferSetIndex(set_index, SET_COUNT);
		if (result == OK) {
			committed_set = set_index;
		}
		return result;
	};

	Check(register_if_valid(SET_COUNT) == VIDEO_OUT_ERROR_INVALID_INDEX,
	      "the first out-of-range buffer set returned the wrong error");
	Check(committed_set == -1, "an out-of-range buffer set mutated registration state");
	Check(register_if_valid(std::numeric_limits<int>::max()) == VIDEO_OUT_ERROR_INVALID_INDEX,
	      "a large buffer set index returned the wrong error");
	Check(committed_set == -1, "a large buffer set index mutated registration state");
	Check(register_if_valid(0) == OK, "buffer set zero was rejected");
	Check(register_if_valid(SET_COUNT - 1) == OK, "the highest valid buffer set was rejected");
	Check(committed_set == SET_COUNT - 1, "a valid buffer set did not reach registration");
}

void TestTooSmallBufferPitchError() {
	using namespace Libs::VideoOut;

	int        committed_set           = -1;
	const auto register_if_pitch_valid = [&committed_set](uint32_t width, uint32_t pitch,
	                                                      int set_index) {
		const int result = ValidateRegisterBufferPitch(width, pitch);
		if (result == OK) {
			committed_set = set_index;
		}
		return result;
	};

	Check(register_if_pitch_valid(1920, 0, 1) == OK,
	      "the automatic display-buffer pitch was rejected");
	Check(register_if_pitch_valid(1920, 1919, 2) == VIDEO_OUT_ERROR_INVALID_PITCH,
	      "a too-small display-buffer pitch returned the wrong error");
	Check(committed_set == 1, "a too-small display-buffer pitch mutated registration state");
	Check(ValidateRegisterBufferPitch(1920, 1920) == OK,
	      "a width-sized pitch failed the lower-bound validation");
}

void TestAttributeChangeCannotGrowRegisteredFootprint() {
	using namespace Libs::VideoOut;

	constexpr uint64_t REGISTERED_SIZE = 0x02000000;
	uint64_t           active_size     = REGISTERED_SIZE;
	const auto submit_change = [&active_size](uint64_t replacement_size) {
		const int result =
		    ValidateBufferAttributeChangeFootprint(REGISTERED_SIZE, replacement_size);
		if (result == OK) {
			active_size = replacement_size;
		}
		return result;
	};

	Check(submit_change(REGISTERED_SIZE / 2) == OK,
	      "a smaller replacement display-buffer footprint was rejected");
	Check(active_size == REGISTERED_SIZE / 2,
	      "a smaller replacement display-buffer footprint was not committed");
	Check(submit_change(REGISTERED_SIZE) == OK,
	      "restoring the registered display-buffer footprint was rejected");
	Check(active_size == REGISTERED_SIZE,
	      "the registered display-buffer footprint was not restored");
	Check(submit_change(REGISTERED_SIZE + 0x10000) == VIDEO_OUT_ERROR_INVALID_VALUE,
	      "an oversized replacement display-buffer footprint returned the wrong error");
	Check(active_size == REGISTERED_SIZE,
	      "an oversized replacement display-buffer footprint mutated active state");
}

} // namespace

int main() {
	TestSubmitFlipModeValidation();
	TestPrimaryFlipRateThree();
	TestAsapFlipIgnoresConfiguredRate();
	TestVsyncMultiFlipIgnoresConfiguredRate();
	TestFirstFlipUsesImmediateCadence();
	TestBufferSetTransitionUsesImmediateCadence();
	TestFlipToBlankUsesImmediateCadence();
	TestFlipFromBlankUsesImmediateCadence();
	TestSafeForRenderingWaitDoesNotExpire();
	TestVideoOutEventCoalescing();
	TestOutputModeEventRegistrationStartsIdle();
	TestOutputModeEventDeleteApiSignature();
	TestGpuCompletionLeavesFlipPendingUntilPresentation();
	TestFlipSubmitCounterPublicationSequence();
	TestMixedFlipRequestOrdering();
	TestInUseBufferSetCannotBeUnregistered();
	TestLinearTenBitPixelFormats();
	TestTiledBufferAddressAlignment();
	TestUnknownPixelFormatRegistrationError();
	TestUnsupportedTilingModeRegistrationError();
	TestDisplayBufferResolutionRegistrationError();
	TestNullBufferDataRegistrationError();
	TestDuplicateBufferSetRegistrationError();
	TestRegisterBufferTotalRangeErrorOrdering();
	TestRegisterBufferSetIndexError();
	TestTooSmallBufferPitchError();
	TestAttributeChangeCannotGrowRegisteredFootprint();
	return 0;
}
