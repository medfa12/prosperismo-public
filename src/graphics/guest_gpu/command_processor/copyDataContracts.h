#ifndef EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_COPYDATACONTRACTS_H_
#define EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_COPYDATACONTRACTS_H_

#include <array>
#include <cstdint>

namespace Libs::Graphics {

enum class CopyDataImmediate64Action { Unsupported, TwoDwordFills };

[[nodiscard]] constexpr CopyDataImmediate64Action ResolveCopyDataImmediate64Action(
	uint32_t source_selector, uint32_t destination_selector, uint32_t num_bytes) noexcept {
	if ((source_selector == 10u || source_selector == 11u) && destination_selector == 4u &&
	    num_bytes == sizeof(uint64_t)) {
		return CopyDataImmediate64Action::TwoDwordFills;
	}
	return CopyDataImmediate64Action::Unsupported;
}

[[nodiscard]] constexpr std::array<uint32_t, 2> SplitCopyDataImmediate64(
	uint64_t value) noexcept {
	return {static_cast<uint32_t>(value), static_cast<uint32_t>(value >> 32u)};
}

} // namespace Libs::Graphics

#endif // EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_COPYDATACONTRACTS_H_
