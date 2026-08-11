#ifndef EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_ATOMICMEMCONTRACTS_H_
#define EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_ATOMICMEMCONTRACTS_H_

#include "graphics/guest_gpu/pm4.h"

#include <atomic>
#include <cstdint>

namespace Libs::Graphics {

enum class AtomicMemOperation : uint32_t {
	CompareSwap32 = 72u,
	Add32         = 79u,
	Sub32         = 80u,
	CompareSwap64 = 104u,
	Add64         = 111u,
	Sub64         = 112u,
	Unsupported   = UINT32_MAX
};
enum class AtomicMemCommand : uint32_t { WaitForConfirm = 2u, Unsupported = UINT32_MAX };
enum class AtomicMemResult : uint32_t { Unsupported, Completed };

struct AtomicMemPacket {
	uint64_t           address   = 0;
	uint64_t           source    = 0;
	uint64_t           compare   = 0;
	AtomicMemOperation operation = AtomicMemOperation::Unsupported;
	AtomicMemCommand   command   = AtomicMemCommand::Unsupported;
};

[[nodiscard]] constexpr bool DecodeAtomicMemPacket(uint32_t cmd_id, const uint32_t* payload,
                                                   AtomicMemPacket& packet) noexcept {
	if (cmd_id != KYTY_PM4(9u, Pm4::IT_ATOMIC_MEM, Pm4::R_ZERO) || payload == nullptr) {
		return false;
	}

	packet.address = payload[1] | (static_cast<uint64_t>(payload[2]) << 32u);
	packet.source  = payload[3] | (static_cast<uint64_t>(payload[4]) << 32u);
	packet.compare = payload[5] | (static_cast<uint64_t>(payload[6]) << 32u);
	switch (payload[0] & 0x7fu) {
		case static_cast<uint32_t>(AtomicMemOperation::CompareSwap32):
			packet.operation = AtomicMemOperation::CompareSwap32;
			break;
		case static_cast<uint32_t>(AtomicMemOperation::Add32):
			packet.operation = AtomicMemOperation::Add32;
			break;
		case static_cast<uint32_t>(AtomicMemOperation::Sub32):
			packet.operation = AtomicMemOperation::Sub32;
			break;
		case static_cast<uint32_t>(AtomicMemOperation::CompareSwap64):
			packet.operation = AtomicMemOperation::CompareSwap64;
			break;
		case static_cast<uint32_t>(AtomicMemOperation::Add64):
			packet.operation = AtomicMemOperation::Add64;
			break;
		case static_cast<uint32_t>(AtomicMemOperation::Sub64):
			packet.operation = AtomicMemOperation::Sub64;
			break;
		default: packet.operation = AtomicMemOperation::Unsupported; break;
	}
	packet.command = ((payload[0] >> 8u) & 0xfu) ==
	                         static_cast<uint32_t>(AtomicMemCommand::WaitForConfirm)
	                     ? AtomicMemCommand::WaitForConfirm
	                     : AtomicMemCommand::Unsupported;
	return packet.operation != AtomicMemOperation::Unsupported &&
	       packet.command != AtomicMemCommand::Unsupported;
}

[[nodiscard]] inline AtomicMemResult ExecuteAtomicMemPacket(const AtomicMemPacket& packet,
	                                                         uint32_t& destination) noexcept {
	if (packet.command != AtomicMemCommand::WaitForConfirm) {
		return AtomicMemResult::Unsupported;
	}

	std::atomic_ref destination_ref(destination);
	switch (packet.operation) {
		case AtomicMemOperation::CompareSwap32: {
			auto expected = static_cast<uint32_t>(packet.compare);
			destination_ref.compare_exchange_strong(expected, static_cast<uint32_t>(packet.source),
			                                        std::memory_order_seq_cst);
			return AtomicMemResult::Completed;
		}
		case AtomicMemOperation::Add32:
			destination_ref.fetch_add(static_cast<uint32_t>(packet.source),
			                          std::memory_order_seq_cst);
			return AtomicMemResult::Completed;
		case AtomicMemOperation::Sub32:
			destination_ref.fetch_sub(static_cast<uint32_t>(packet.source),
			                          std::memory_order_seq_cst);
			return AtomicMemResult::Completed;
		case AtomicMemOperation::CompareSwap64:
		case AtomicMemOperation::Add64:
		case AtomicMemOperation::Sub64:
		case AtomicMemOperation::Unsupported: return AtomicMemResult::Unsupported;
	}
	return AtomicMemResult::Unsupported;
}

[[nodiscard]] inline AtomicMemResult ExecuteAtomicMemPacket(const AtomicMemPacket& packet,
	                                                         uint64_t& destination) noexcept {
	if (packet.command != AtomicMemCommand::WaitForConfirm) {
		return AtomicMemResult::Unsupported;
	}

	std::atomic_ref destination_ref(destination);
	switch (packet.operation) {
		case AtomicMemOperation::CompareSwap64: {
			auto expected = packet.compare;
			destination_ref.compare_exchange_strong(expected, packet.source,
			                                        std::memory_order_seq_cst);
			return AtomicMemResult::Completed;
		}
		case AtomicMemOperation::Add64:
			destination_ref.fetch_add(packet.source, std::memory_order_seq_cst);
			return AtomicMemResult::Completed;
		case AtomicMemOperation::Sub64:
			destination_ref.fetch_sub(packet.source, std::memory_order_seq_cst);
			return AtomicMemResult::Completed;
		case AtomicMemOperation::CompareSwap32:
		case AtomicMemOperation::Add32:
		case AtomicMemOperation::Sub32:
		case AtomicMemOperation::Unsupported: return AtomicMemResult::Unsupported;
	}
	return AtomicMemResult::Unsupported;
}

} // namespace Libs::Graphics

#endif // EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_ATOMICMEMCONTRACTS_H_
