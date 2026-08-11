#ifndef EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_AASAMPLEMASKCONTRACTS_H_
#define EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_AASAMPLEMASKCONTRACTS_H_

#include "graphics/guest_gpu/pm4.h"

#include <cstdint>

namespace Libs::Graphics {

constexpr uint64_t AaSampleMaskDefault = UINT64_MAX;

[[nodiscard]] constexpr bool UpdateAaSampleMaskRegister(uint64_t& mask, uint32_t register_offset,
                                                        uint32_t value) noexcept {
	switch (register_offset) {
		case Pm4::PA_SC_AA_MASK_X0Y0_X1Y0:
			mask = (mask & 0xffffffff00000000ull) | static_cast<uint64_t>(value);
			return true;
		case Pm4::PA_SC_AA_MASK_X0Y1_X1Y1:
			mask = (mask & 0x00000000ffffffffull) | (static_cast<uint64_t>(value) << 32u);
			return true;
		default: return false;
	}
}

[[nodiscard]] constexpr uint16_t AaSampleMaskForPixel(uint64_t mask, uint32_t x,
                                                      uint32_t y) noexcept {
	const auto quadrant = ((y & 1u) << 1u) | (x & 1u);
	return static_cast<uint16_t>(mask >> (quadrant * 16u));
}

} // namespace Libs::Graphics

#endif // EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_AASAMPLEMASKCONTRACTS_H_
