#ifndef EMULATOR_INCLUDE_EMULATOR_GRAPHICS_VIDEOOUTCONTRACTS_H_
#define EMULATOR_INCLUDE_EMULATOR_GRAPHICS_VIDEOOUTCONTRACTS_H_

#include "libs/errno.h"

#include <cstdint>

namespace Libs::VideoOut {

constexpr int VIDEO_OUT_FLIP_MODE_VSYNC       = 1;
constexpr int VIDEO_OUT_FLIP_MODE_ASAP        = 2;
constexpr int VIDEO_OUT_FLIP_MODE_WINDOW      = 3;
constexpr int VIDEO_OUT_FLIP_MODE_VSYNC_MULTI = 4;
constexpr int VIDEO_OUT_BUFFER_INDEX_BLANK     = -1;

[[nodiscard]] constexpr int ValidatePrimaryFlipMode(int mode) noexcept {
	return mode >= VIDEO_OUT_FLIP_MODE_VSYNC && mode <= VIDEO_OUT_FLIP_MODE_VSYNC_MULTI
	           ? OK
	           : VIDEO_OUT_ERROR_INVALID_FLIP_MODE;
}

[[nodiscard]] constexpr int ValidatePrimaryFlipRate(int rate) noexcept {
	return rate >= 0 && rate <= 3 ? OK : VIDEO_OUT_ERROR_INVALID_VALUE;
}

[[nodiscard]] constexpr bool IsFlipRateIntervalDue(uint64_t vblank_count, int rate) noexcept {
	return ValidatePrimaryFlipRate(rate) == OK &&
	       vblank_count % static_cast<uint64_t>(rate + 1) == 0;
}

[[nodiscard]] constexpr bool IsFlipRequestDue(uint64_t vblank_count, int rate,
                                               int mode) noexcept {
	return ValidatePrimaryFlipMode(mode) == OK && ValidatePrimaryFlipRate(rate) == OK &&
	       (mode == VIDEO_OUT_FLIP_MODE_ASAP || mode == VIDEO_OUT_FLIP_MODE_VSYNC_MULTI ||
	        IsFlipRateIntervalDue(vblank_count, rate));
}

[[nodiscard]] constexpr bool IsLifecycleFlipRequestDue(
    uint64_t vblank_count, int rate, int mode, uint64_t completed_flip_count,
    bool buffer_set_transition = false, bool blank_destination = false,
    bool blank_exit_transition = false) noexcept {
	return ValidatePrimaryFlipMode(mode) == OK && ValidatePrimaryFlipRate(rate) == OK &&
	       (completed_flip_count == 0 || buffer_set_transition || blank_destination ||
	        blank_exit_transition || IsFlipRequestDue(vblank_count, rate, mode));
}

[[nodiscard]] constexpr bool IsBlankOutputDestination(int buffer_index) noexcept {
	return buffer_index == VIDEO_OUT_BUFFER_INDEX_BLANK;
}

[[nodiscard]] constexpr bool IsBlankToRegisteredBufferTransition(int current_buffer_index,
                                                                  int target_buffer_index,
                                                                  int target_set_index) noexcept {
	return current_buffer_index == VIDEO_OUT_BUFFER_INDEX_BLANK && target_buffer_index >= 0 &&
	       target_set_index >= 0;
}

[[nodiscard]] constexpr bool IsRegisteredBufferSetTransition(
    int current_buffer_index, int target_buffer_index, int current_set_index,
    int target_set_index) noexcept {
	return current_buffer_index >= 0 && target_buffer_index >= 0 && current_set_index >= 0 &&
	       target_set_index >= 0 && current_set_index != target_set_index;
}

[[nodiscard]] constexpr bool ShouldWaitUntilSafeForRendering(bool request_pending) noexcept {
	return request_pending;
}

struct VideoOutEventAccumulator {
	bool     triggered        = false;
	uint32_t occurrence_count = 0;
	uint64_t encoded_data     = 0;
};

[[nodiscard]] constexpr VideoOutEventAccumulator AccumulateVideoOutEvent(
    VideoOutEventAccumulator state, uint64_t payload, uint64_t timestamp) noexcept {
	state.triggered = true;
	if (state.occurrence_count < 0xfu) {
		state.occurrence_count++;
	}
	state.encoded_data = (timestamp & 0xfffu) |
	                     (static_cast<uint64_t>(state.occurrence_count) << 12u) |
	                     ((payload & 0x0000ffffffffffffULL) << 16u);
	return state;
}

[[nodiscard]] constexpr VideoOutEventAccumulator InitializeVideoOutEventRegistration() noexcept {
	return {};
}

struct VideoOutFlipQueueCounts {
	int32_t gc_queue_num;
	int32_t flip_pending_num;
};

[[nodiscard]] constexpr VideoOutFlipQueueCounts CompleteFlipExecution(
    VideoOutFlipQueueCounts counts, bool gpu_eop_request) noexcept {
	if (gpu_eop_request && counts.gc_queue_num > 0) {
		counts.gc_queue_num--;
	}
	return counts;
}

struct VideoOutFlipSubmitCounterState {
	uint64_t published;
	uint64_t request;
};

[[nodiscard]] constexpr VideoOutFlipSubmitCounterState BeginFlipSubmitCounter(
    uint64_t last_completed, uint64_t cpu_request_counter, bool gpu_eop_request) noexcept {
	return {last_completed, gpu_eop_request ? 0 : cpu_request_counter};
}

[[nodiscard]] constexpr VideoOutFlipSubmitCounterState CompleteFlipSubmitCounter(
    VideoOutFlipSubmitCounterState state, uint64_t gpu_completion_counter,
    bool gpu_eop_request) noexcept {
	if (gpu_eop_request) {
		state.request = gpu_completion_counter;
	}
	return state;
}

[[nodiscard]] constexpr VideoOutFlipSubmitCounterState PublishFlipSubmitCounter(
    VideoOutFlipSubmitCounterState state) noexcept {
	state.published = state.request;
	return state;
}

[[nodiscard]] constexpr uint64_t CaptureFlipRequestOrderAtReserve(
    uint64_t next_order, bool gpu_eop_request) noexcept {
	return gpu_eop_request ? 0 : next_order;
}

[[nodiscard]] constexpr uint64_t CaptureFlipRequestOrderAtCompletion(
    uint64_t request_order, uint64_t next_order, bool gpu_eop_request) noexcept {
	return gpu_eop_request ? next_order : request_order;
}

// Zero denotes an AGC flip whose command has been reserved but has not executed yet.
[[nodiscard]] constexpr bool FlipRequestOrderPrecedes(uint64_t lhs_order,
                                                       uint64_t rhs_order) noexcept {
	return lhs_order != 0 && (rhs_order == 0 || lhs_order < rhs_order);
}

[[nodiscard]] constexpr bool ShouldDeferFlipForPendingCpu(uint64_t front_order,
                                                          uint64_t pending_cpu_order) noexcept {
	return FlipRequestOrderPrecedes(pending_cpu_order, front_order);
}

[[nodiscard]] constexpr int ValidateUnregisterBufferSet(int set_index,
                                                        int displayed_set_index,
                                                        bool pending_flip_use) noexcept {
	if (displayed_set_index == set_index || pending_flip_use) {
		return VIDEO_OUT_ERROR_RESOURCE_BUSY;
	}
	return OK;
}

[[nodiscard]] constexpr bool IsFlipRequestUsingBufferSet(bool same_port, int buffer_index,
                                                         int buffer_count, int buffer_set_index,
                                                         int requested_set_index) noexcept {
	return same_port && buffer_index >= 0 && buffer_index < buffer_count &&
	       buffer_set_index == requested_set_index;
}

[[nodiscard]] constexpr int ValidateTiledBufferAddressAlignment(uint64_t data_address) noexcept {
	constexpr uint64_t TILE_BUFFER_ALIGNMENT = 64u * 1024u;
	return (data_address & (TILE_BUFFER_ALIGNMENT - 1u)) == 0
	           ? OK
	           : VIDEO_OUT_ERROR_MEMORY_INVALID_ALIGNMENT;
}

[[nodiscard]] constexpr int ValidateRegisterPixelFormat(bool supported) noexcept {
	return supported ? OK : VIDEO_OUT_ERROR_INVALID_PIXEL_FORMAT;
}

[[nodiscard]] constexpr int ValidateRegisterTilingMode(uint32_t tiling_mode) noexcept {
	return tiling_mode == 0 ? OK : VIDEO_OUT_ERROR_INVALID_TILING_MODE;
}

[[nodiscard]] constexpr int ValidateRegisterResolution(uint32_t width, uint32_t height) noexcept {
	const bool supported =
	    (width == 3840 && height == 2160) || (width == 3680 && height == 2070) ||
	    (width == 3520 && height == 1980) || (width == 3360 && height == 1890) ||
	    (width == 3200 && height == 1800) || (width == 2880 && height == 1620) ||
	    (width == 2560 && height == 1440) || (width == 2240 && height == 1260) ||
	    (width == 1920 && height == 1080);
	return supported ? OK : VIDEO_OUT_ERROR_INVALID_RESOLUTION;
}

[[nodiscard]] constexpr int ValidateRegisterBufferDataAddress(uint64_t data_address) noexcept {
	return data_address != 0 ? OK : VIDEO_OUT_ERROR_INVALID_MEMORY;
}

[[nodiscard]] constexpr int ValidateRegisterBufferSet(bool occupied) noexcept {
	return occupied ? VIDEO_OUT_ERROR_SLOT_OCCUPIED : OK;
}

[[nodiscard]] constexpr int ValidateRegisterBufferDomain(int buffer_index_start, int buffer_num,
                                                         int buffer_count) noexcept {
	return buffer_index_start >= 0 && buffer_index_start < buffer_count && buffer_num >= 1 &&
	               buffer_num <= buffer_count
	           ? OK
	           : VIDEO_OUT_ERROR_INVALID_VALUE;
}

[[nodiscard]] constexpr int ValidateRegisterBufferSetAndTotalRange(bool occupied,
                                                                   int buffer_index_start,
                                                                   int buffer_num,
                                                                   int buffer_count) noexcept {
	if (occupied) {
		return VIDEO_OUT_ERROR_SLOT_OCCUPIED;
	}
	return buffer_num <= buffer_count - buffer_index_start ? OK : VIDEO_OUT_ERROR_INVALID_VALUE;
}

[[nodiscard]] constexpr int ValidateRegisterBufferSetIndex(int set_index, int set_count) noexcept {
	if (set_index < 0) {
		return VIDEO_OUT_ERROR_INVALID_VALUE;
	}
	return set_index < set_count ? OK : VIDEO_OUT_ERROR_INVALID_INDEX;
}

[[nodiscard]] constexpr int ValidateRegisterBufferPitch(uint32_t width,
                                                        uint32_t pitch_in_pixel) noexcept {
	return pitch_in_pixel == 0 || pitch_in_pixel >= width ? OK : VIDEO_OUT_ERROR_INVALID_PITCH;
}

[[nodiscard]] constexpr int ValidateBufferAttributeChangeFootprint(
    uint64_t registered_size, uint64_t replacement_size) noexcept {
	return replacement_size <= registered_size ? OK : VIDEO_OUT_ERROR_INVALID_VALUE;
}

} // namespace Libs::VideoOut

#endif /* EMULATOR_INCLUDE_EMULATOR_GRAPHICS_VIDEOOUTCONTRACTS_H_ */
