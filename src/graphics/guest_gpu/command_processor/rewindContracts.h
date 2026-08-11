#ifndef EMULATOR_INCLUDE_EMULATOR_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_REWIND_CONTRACTS_H_
#define EMULATOR_INCLUDE_EMULATOR_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_REWIND_CONTRACTS_H_

#include "common/common.h"

namespace Libs::Graphics {

enum class RewindAction {
	Suspend,
	Continue,
};

[[nodiscard]] constexpr RewindAction ResolveRewindAction(uint32_t control) {
	return (control & (1u << 31u)) != 0 ? RewindAction::Continue : RewindAction::Suspend;
}

} // namespace Libs::Graphics

#endif // EMULATOR_INCLUDE_EMULATOR_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_REWIND_CONTRACTS_H_
