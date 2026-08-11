#include "graphics/guest_gpu/hardwareContext.h"
#include "graphics/host_gpu/renderer/pipeline/pipelineCache.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>

namespace {

using Libs::Graphics::PipelineSampleShadingState;
using Libs::Graphics::ResolvePipelineSampleShading;
using Libs::Graphics::HW::PsShaderRateControl;

void Check(bool value, const char* message) {
	if (!value) {
		std::fprintf(stderr, "PsSampleRateContractTests: failed: %s\n", message);
		std::abort();
	}
}

void CheckFraction(const PipelineSampleShadingState& state, float expected, const char* message) {
	Check(std::fabs(state.minimum_fraction - expected) < 0.00001f, message);
}

void TestRegisterDecode() {
	Check(!PsShaderRateControl::FromRegister(0u).per_sample,
	      "the default per-pixel register value decoded as per-sample");
	Check(PsShaderRateControl::FromRegister(0x00010000u).per_sample,
	      "the documented per-sample bit was ignored");
	Check(!PsShaderRateControl::FromRegister(0xfffeffffu).per_sample,
	      "unrelated PA_SC_MODE_CNTL_1 bits changed the shader rate");
}

void TestPerPixelAndInactiveControls() {
	const auto per_pixel = ResolvePipelineSampleShading(true, 4u, false, false, 7u);
	Check(per_pixel.representable && !per_pixel.enable,
	      "per-pixel shading incorrectly enabled sample-rate shading");

	const auto inactive = ResolvePipelineSampleShading(false, 4u, false, true, 7u);
	Check(inactive.representable && !inactive.enable,
	      "an inactive pixel stage retained sample-rate pipeline state");

	const auto single_sample = ResolvePipelineSampleShading(true, 1u, false, true, 0u);
	Check(single_sample.representable && !single_sample.enable,
	      "single-sample rendering enabled irrelevant sample-rate shading");
}

void TestExplicitIterationCounts() {
	const auto one_of_four = ResolvePipelineSampleShading(true, 4u, false, true, 0u);
	Check(one_of_four.representable && one_of_four.enable,
	      "explicit per-sample shading was not enabled");
	CheckFraction(one_of_four, 0.25f, "one-of-four sample iteration used the wrong fraction");

	const auto two_of_four = ResolvePipelineSampleShading(true, 4u, false, true, 1u);
	Check(two_of_four.representable && two_of_four.enable,
	      "two-of-four sample iteration was not enabled");
	CheckFraction(two_of_four, 0.5f, "two-of-four sample iteration used the wrong fraction");

	const auto four_of_four = ResolvePipelineSampleShading(true, 4u, false, true, 2u);
	Check(four_of_four.representable && four_of_four.enable,
	      "four-of-four sample iteration was not enabled");
	CheckFraction(four_of_four, 1.0f, "four-of-four sample iteration was not full rate");
}

void TestShaderRequirementAndInvalidCount() {
	const auto shader_required = ResolvePipelineSampleShading(true, 8u, true, false, 0u);
	Check(shader_required.representable && shader_required.enable,
	      "a shader sample-input requirement stopped enabling sample-rate shading");
	CheckFraction(shader_required, 1.0f,
	              "a shader sample-input requirement stopped using full sample rate");

	const auto excessive = ResolvePipelineSampleShading(true, 4u, false, true, 3u);
	Check(!excessive.representable,
	      "a PS iteration count larger than the attachment sample count was accepted");
}

} // namespace

int main() {
	TestRegisterDecode();
	TestPerPixelAndInactiveControls();
	TestExplicitIterationCounts();
	TestShaderRequirementAndInvalidCount();
	std::puts("PsSampleRateContractTests: all cases passed");
	return 0;
}
