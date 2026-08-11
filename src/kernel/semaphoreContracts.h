#ifndef EMULATOR_INCLUDE_EMULATOR_KERNEL_SEMAPHORECONTRACTS_H_
#define EMULATOR_INCLUDE_EMULATOR_KERNEL_SEMAPHORECONTRACTS_H_

#include <cstdint>

namespace Libs::LibKernel::Semaphore {

enum class KernelSemaQueueOrder : uint8_t { Fifo, ThreadPriority };

// KernelCreateSema validates the accepted attribute range before decoding its queue order.
[[nodiscard]] constexpr KernelSemaQueueOrder DecodeKernelSemaQueueOrder(uint32_t attr) noexcept {
	return attr == 0x02 ? KernelSemaQueueOrder::ThreadPriority : KernelSemaQueueOrder::Fifo;
}

} // namespace Libs::LibKernel::Semaphore

#endif /* EMULATOR_INCLUDE_EMULATOR_KERNEL_SEMAPHORECONTRACTS_H_ */
