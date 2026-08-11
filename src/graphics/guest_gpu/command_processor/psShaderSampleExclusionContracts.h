#ifndef EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_PSSHADERSAMPLEEXCLUSIONCONTRACTS_H_
#define EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_PSSHADERSAMPLEEXCLUSIONCONTRACTS_H_

#include "graphics/guest_gpu/pm4.h"

#include <cstdint>

namespace Libs::Graphics {

constexpr uint32_t PsShaderSampleExclusionMaskDefault = 0;

[[nodiscard]] constexpr bool UpdatePsShaderSampleExclusionMask(uint32_t& mask,
                                                               uint32_t register_offset,
                                                               uint32_t value) noexcept {
	if (register_offset != Pm4::PS_SHADER_SAMPLE_EXCLUSION_MASK) {
		return false;
	}
	mask = value;
	return true;
}

[[nodiscard]] constexpr uint32_t ApplyPsShaderSampleExclusion(uint32_t covered_samples,
	                                                              uint32_t exclusion_mask) noexcept {
	return covered_samples & ~exclusion_mask;
}

} // namespace Libs::Graphics

#endif // EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_PSSHADERSAMPLEEXCLUSIONCONTRACTS_H_
