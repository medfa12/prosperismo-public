#ifndef EMULATOR_INCLUDE_EMULATOR_LIBS_VIDEODEC2CONTRACTS_H_
#define EMULATOR_INCLUDE_EMULATOR_LIBS_VIDEODEC2CONTRACTS_H_

#include "libs/errno.h"

#include <cstdint>

namespace Libs::VideoDec2 {

constexpr int32_t VIDEODEC2_ERROR_INPUT_QUEUE_DEPTH = -2128805370; // 0x811d0206
constexpr int32_t VIDEODEC2_ERROR_FRAME_BUFFER_ALIGNMENT = -2128805624; // 0x811d0108
constexpr uintptr_t VIDEODEC2_FRAME_BUFFER_ALIGNMENT = 0x100;

[[nodiscard]] constexpr int32_t ValidateInputQueueDepth(uint32_t depth) noexcept {
	return depth >= 1 && depth <= 8 ? OK : VIDEODEC2_ERROR_INPUT_QUEUE_DEPTH;
}

[[nodiscard]] constexpr int32_t ValidateFrameBufferAlignment(uintptr_t address) noexcept {
	return (address & (VIDEODEC2_FRAME_BUFFER_ALIGNMENT - 1)) == 0
	           ? OK
	           : VIDEODEC2_ERROR_FRAME_BUFFER_ALIGNMENT;
}

} // namespace Libs::VideoDec2

#endif /* EMULATOR_INCLUDE_EMULATOR_LIBS_VIDEODEC2CONTRACTS_H_ */
