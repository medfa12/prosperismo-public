#include "graphics/guest_gpu/hardwareContext.h"

#include <bit>
#include <cstdio>
#include <cstdlib>

namespace {

using Libs::Graphics::Pm4::PA_SU_POLY_OFFSET_BACK_OFFSET;
using Libs::Graphics::Pm4::PA_SU_POLY_OFFSET_BACK_SCALE;
using Libs::Graphics::Pm4::PA_SU_POLY_OFFSET_CLAMP;
using Libs::Graphics::Pm4::PA_SU_POLY_OFFSET_FRONT_OFFSET;
using Libs::Graphics::Pm4::PA_SU_POLY_OFFSET_FRONT_SCALE;
using Libs::Graphics::HW::ModeControl;
using Libs::Graphics::HW::PolygonOffsetControl;
using Libs::Graphics::HW::ResolvePipelineDepthBias;

void Check(bool value, const char* message) {
	if (!value) {
		std::fprintf(stderr, "PolygonOffsetContractTests: failed: %s\n", message);
		std::abort();
	}
}

PolygonOffsetControl MakeControl() {
	PolygonOffsetControl control;
	Check(control.SetRegister(PA_SU_POLY_OFFSET_CLAMP, std::bit_cast<uint32_t>(0.25f)),
	      "clamp register was rejected");
	Check(control.SetRegister(PA_SU_POLY_OFFSET_FRONT_SCALE, std::bit_cast<uint32_t>(16.0f)),
	      "front scale register was rejected");
	Check(control.SetRegister(PA_SU_POLY_OFFSET_FRONT_OFFSET, std::bit_cast<uint32_t>(-2.0f)),
	      "front offset register was rejected");
	Check(control.SetRegister(PA_SU_POLY_OFFSET_BACK_SCALE, std::bit_cast<uint32_t>(32.0f)),
	      "back scale register was rejected");
	Check(control.SetRegister(PA_SU_POLY_OFFSET_BACK_OFFSET, std::bit_cast<uint32_t>(4.0f)),
	      "back offset register was rejected");
	return control;
}

void TestVisibleFaceSelection() {
	const auto control = MakeControl();
	ModeControl mode;
	mode.poly_offset_front_enable = true;
	mode.cull_back                = true;
	auto bias                     = ResolvePipelineDepthBias(mode, control);
	Check(bias.representable && bias.enable && bias.slope_factor == 1.0f &&
	          bias.constant_factor == -2.0f && bias.clamp == 0.25f,
	      "front-face state was not translated to Vulkan depth bias");

	mode.cull_back                = false;
	mode.cull_front               = true;
	mode.poly_offset_front_enable = false;
	mode.poly_offset_back_enable  = true;
	bias                          = ResolvePipelineDepthBias(mode, control);
	Check(bias.representable && bias.enable && bias.slope_factor == 2.0f &&
	          bias.constant_factor == 4.0f,
	      "back-face state was not translated to Vulkan depth bias");
}

void TestRepresentabilityGuards() {
	auto control = MakeControl();
	ModeControl mode;
	mode.poly_offset_front_enable = true;
	mode.poly_offset_back_enable  = true;
	auto bias = ResolvePipelineDepthBias(mode, control);
	Check(!bias.representable, "different visible-face values were silently collapsed");

	Check(control.SetRegister(PA_SU_POLY_OFFSET_FRONT_SCALE, std::bit_cast<uint32_t>(32.0f)) &&
	          control.SetRegister(PA_SU_POLY_OFFSET_FRONT_OFFSET, std::bit_cast<uint32_t>(4.0f)),
	      "matching front-face register update failed");
	bias = ResolvePipelineDepthBias(mode, control);
	Check(bias.representable && bias.enable && bias.slope_factor == 2.0f,
	      "matching visible-face values were rejected");

	mode.poly_offset_back_enable = false;
	Check(!ResolvePipelineDepthBias(mode, control).representable,
	      "different visible-face enables were silently collapsed");
	mode.poly_offset_front_enable = false;
	Check(!ResolvePipelineDepthBias(mode, control).enable,
	      "disabled polygon offset enabled host depth bias");

	mode.cull_back                = true;
	mode.poly_offset_front_enable = true;
	Check(!ResolvePipelineDepthBias(mode, control, false).representable,
	      "nonzero clamp was accepted without the host feature");
	Check(control.SetRegister(PA_SU_POLY_OFFSET_CLAMP, 0x7fc00000u),
	      "non-finite clamp register was rejected before validation");
	Check(!ResolvePipelineDepthBias(mode, control).representable,
	      "non-finite depth-bias state was accepted");
	Check(!control.SetRegister(0xffffffffu, 0u), "unknown register was accepted");
}

} // namespace

int main() {
	TestVisibleFaceSelection();
	TestRepresentabilityGuards();
	std::puts("PolygonOffsetContractTests: all cases passed");
	return 0;
}
