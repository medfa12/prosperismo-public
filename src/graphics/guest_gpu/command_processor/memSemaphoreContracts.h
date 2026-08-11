#ifndef EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_MEMSEMAPHORECONTRACTS_H_
#define EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_MEMSEMAPHORECONTRACTS_H_

#include "graphics/guest_gpu/pm4.h"

#include <atomic>
#include <cstdint>

namespace Libs::Graphics {

enum class MemSemaphoreOperation : uint32_t { Unsupported, Signal, Wait };
enum class MemSemaphoreSignalType : uint32_t { Increment, WriteOne };
enum class MemSemaphoreSignalStage : uint32_t { Recorded, GpuComplete };
enum class MemSemaphoreWaitResult : uint32_t { Unsupported, Waiting, Consumed };

struct MemSemaphorePacket {
	uint64_t               address          = 0;
	MemSemaphoreOperation  operation        = MemSemaphoreOperation::Unsupported;
	MemSemaphoreSignalType signal_type      = MemSemaphoreSignalType::Increment;
	bool                   wait_for_mailbox = false;
};

[[nodiscard]] constexpr bool DecodeMemSemaphorePacket(uint32_t cmd_id, const uint32_t* payload,
                                                      MemSemaphorePacket& packet) noexcept {
	if (cmd_id != KYTY_PM4(4u, Pm4::IT_MEM_SEMAPHORE, Pm4::R_ZERO) || payload == nullptr) {
		return false;
	}

	const auto operation = (payload[2] >> 29u) & 0x7u;
	switch (operation) {
		case 6u: packet.operation = MemSemaphoreOperation::Signal; break;
		case 7u: packet.operation = MemSemaphoreOperation::Wait; break;
		default: return false;
	}
	packet.address          = payload[0] | (static_cast<uint64_t>(payload[1]) << 32u);
	packet.signal_type      = ((payload[2] >> 20u) & 0x1u) != 0 ? MemSemaphoreSignalType::WriteOne
	                                                            : MemSemaphoreSignalType::Increment;
	packet.wait_for_mailbox = ((payload[2] >> 16u) & 0x1u) != 0;
	return true;
}

[[nodiscard]] inline MemSemaphoreWaitResult TryConsumeMemSemaphore(uint64_t& counter) noexcept {
	std::atomic_ref counter_ref(counter);
	auto            observed = counter_ref.load(std::memory_order_acquire);
	while (observed != 0) {
		if (counter_ref.compare_exchange_weak(observed, observed - 1u, std::memory_order_acq_rel,
		                                      std::memory_order_acquire)) {
			return MemSemaphoreWaitResult::Consumed;
		}
	}
	return MemSemaphoreWaitResult::Waiting;
}

[[nodiscard]] inline bool CompleteMemSemaphoreSignal(uint64_t&               counter,
                                                     MemSemaphoreSignalType  signal_type,
                                                     MemSemaphoreSignalStage stage) noexcept {
	if (signal_type != MemSemaphoreSignalType::Increment &&
	    signal_type != MemSemaphoreSignalType::WriteOne) {
		return false;
	}
	if (stage != MemSemaphoreSignalStage::Recorded &&
	    stage != MemSemaphoreSignalStage::GpuComplete) {
		return false;
	}
	if (stage == MemSemaphoreSignalStage::Recorded) {
		return true;
	}

	std::atomic_ref counter_ref(counter);
	switch (signal_type) {
		case MemSemaphoreSignalType::Increment:
			counter_ref.fetch_add(1u, std::memory_order_release);
			return true;
		case MemSemaphoreSignalType::WriteOne:
			counter_ref.store(1u, std::memory_order_release);
			return true;
	}
	return false;
}

} // namespace Libs::Graphics

#endif // EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_MEMSEMAPHORECONTRACTS_H_
