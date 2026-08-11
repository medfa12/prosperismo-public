#ifndef EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_EVENTWRITECONTRACTS_H_
#define EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_EVENTWRITECONTRACTS_H_

#include <cstdint>

namespace Libs::Graphics {

[[nodiscard]] constexpr bool EventWriteRequiresDfsmFlushBarrier(uint32_t event_type) noexcept {
	return event_type == 0x12u;
}

} // namespace Libs::Graphics

#endif // EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_EVENTWRITECONTRACTS_H_
