#ifndef EMULATOR_INCLUDE_EMULATOR_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_DMA_DATA_CONTRACTS_H_
#define EMULATOR_INCLUDE_EMULATOR_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_DMA_DATA_CONTRACTS_H_

#include "common/common.h"

namespace Libs::Graphics {

enum class DmaDataDestinationAction {
	Memory,
	Gds,
	Discard,
	Unsupported,
};

[[nodiscard]] constexpr DmaDataDestinationAction ResolveDmaDataDestinationAction(uint8_t selector) {
	switch (selector) {
		case 0:
		case 3: return DmaDataDestinationAction::Memory;
		case 1: return DmaDataDestinationAction::Gds;
		case 2: return DmaDataDestinationAction::Discard;
		default: return DmaDataDestinationAction::Unsupported;
	}
}

} // namespace Libs::Graphics

#endif // EMULATOR_INCLUDE_EMULATOR_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_DMA_DATA_CONTRACTS_H_
