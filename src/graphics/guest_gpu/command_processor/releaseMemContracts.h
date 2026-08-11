#ifndef EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_RELEASEMEMCONTRACTS_H_
#define EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_RELEASEMEMCONTRACTS_H_

#include "graphics/guest_gpu/pm4.h"

namespace Libs::Graphics {

[[nodiscard]] constexpr bool IsReleaseMemPacketHeader(uint32_t cmd_id) noexcept {
	const auto header = cmd_id & ~1u;
	return header == KYTY_PM4(8u, Pm4::IT_NOP, Pm4::R_RELEASE_MEM) ||
	       header == KYTY_PM4(8u, Pm4::IT_RELEASE_MEM, Pm4::R_ZERO);
}

} // namespace Libs::Graphics

#endif // EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_RELEASEMEMCONTRACTS_H_
