#ifndef EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_CONDWRITECONTRACTS_H_
#define EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_CONDWRITECONTRACTS_H_

#include <cstdint>

namespace Libs::Graphics {

enum class CondWriteResult { Unsupported, Skipped, Written };

[[nodiscard]] inline CondWriteResult ExecuteCondWriteGl2(
    uint32_t compare_function, uint32_t read_value, uint32_t reference, uint32_t mask,
    uint32_t write_value, volatile uint32_t& destination) noexcept {
	const auto value = read_value & mask;
	bool       write = false;
	switch (compare_function) {
		case 0u: write = true; break;
		case 1u: write = value < reference; break;
		case 2u: write = value <= reference; break;
		case 3u: write = value == reference; break;
		case 4u: write = value != reference; break;
		case 5u: write = value >= reference; break;
		case 6u: write = value > reference; break;
		default: return CondWriteResult::Unsupported;
	}

	if (!write) {
		return CondWriteResult::Skipped;
	}
	destination = write_value;
	return CondWriteResult::Written;
}

} // namespace Libs::Graphics

#endif // EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_CONDWRITECONTRACTS_H_
