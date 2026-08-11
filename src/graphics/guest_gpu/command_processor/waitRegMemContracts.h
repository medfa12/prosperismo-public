#ifndef EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_WAITREGMEMCONTRACTS_H_
#define EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_WAITREGMEMCONTRACTS_H_

#include "graphics/guest_gpu/pm4.h"

#include <cstdint>

namespace Libs::Graphics {

struct WaitRegMem32Packet {
	uint64_t address;
	uint32_t reference;
	uint32_t mask;
	uint32_t control;
	uint32_t poll_interval;
};

[[nodiscard]] constexpr bool DecodeWaitRegMem32Packet(uint32_t cmd_id, const uint32_t* payload,
                                                       WaitRegMem32Packet& packet) noexcept {
	const auto header = cmd_id & ~1u;
	if (header == KYTY_PM4(7u, Pm4::IT_NOP, Pm4::R_WAIT_MEM_32)) {
		packet.address       = payload[0] | (static_cast<uint64_t>(payload[1]) << 32u);
		packet.mask          = payload[2];
		packet.reference     = payload[3];
		packet.control       = payload[4];
		packet.poll_interval = payload[5];
		return true;
	}
	if (header == KYTY_PM4(7u, Pm4::IT_WAIT_REG_MEM, Pm4::R_ZERO)) {
		packet.control       = payload[0];
		packet.address       = payload[1] | (static_cast<uint64_t>(payload[2]) << 32u);
		packet.reference     = payload[3];
		packet.mask          = payload[4];
		packet.poll_interval = payload[5];
		return true;
	}
	return false;
}

} // namespace Libs::Graphics

#endif // EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_WAITREGMEMCONTRACTS_H_
