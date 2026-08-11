#include "graphics/guest_gpu/gpu_defs.h"
#include "graphics/host_gpu/renderer/pipeline/pipelineCache.h"

#include <cstdio>
#include <cstdlib>

namespace {

using Libs::Graphics::PipelineBlendFactorComponent;
using Libs::Graphics::PipelineForceDestAlphaToOneEnabled;
using Libs::Graphics::ResolvePipelineAttachmentBlendState;
using Libs::Graphics::ResolvePipelineForceDestAlphaBlendFactor;
using Libs::Graphics::Prospero::BlendFactor;

constexpr uint8_t Factor(BlendFactor value) {
	return static_cast<uint8_t>(value);
}

void Check(bool value, const char *message) {
	if (!value) {
		std::fprintf(stderr, "ForceDestAlphaContractTests: failed: %s\n", message);
		std::abort();
	}
}

void TestActivation() {
	Check(PipelineForceDestAlphaToOneEnabled(true, true, false),
	      "an enabled blending surface did not activate forced destination alpha");
	Check(!PipelineForceDestAlphaToOneEnabled(false, true, false),
	      "a disabled render-target flag changed blending");
	Check(!PipelineForceDestAlphaToOneEnabled(true, false, false),
	      "a non-blended attachment retained an irrelevant forced-alpha state");
	Check(!PipelineForceDestAlphaToOneEnabled(true, true, true),
	      "blend bypass retained an irrelevant forced-alpha state");
}

void TestDisabledStatePreservesFactors() {
	for (const auto component : {PipelineBlendFactorComponent::Color,
	                             PipelineBlendFactorComponent::Alpha}) {
		for (const auto factor :
		     {BlendFactor::kDstAlpha, BlendFactor::kOneMinusDstAlpha,
		      BlendFactor::kSrcAlphaSaturate, BlendFactor::kDstColor}) {
			Check(ResolvePipelineForceDestAlphaBlendFactor(Factor(factor), false, component) ==
			          Factor(factor),
			      "disabled forced-alpha state rewrote a guest factor");
		}
	}
}

void TestDestinationAlphaFactors() {
	for (const auto component : {PipelineBlendFactorComponent::Color,
	                             PipelineBlendFactorComponent::Alpha}) {
		Check(ResolvePipelineForceDestAlphaBlendFactor(
		          Factor(BlendFactor::kDstAlpha), true, component) == Factor(BlendFactor::kOne),
		      "destination alpha was not replaced with one");
		Check(ResolvePipelineForceDestAlphaBlendFactor(
		          Factor(BlendFactor::kOneMinusDstAlpha), true, component) ==
		          Factor(BlendFactor::kZero),
		      "one-minus-destination-alpha was not replaced with zero");
	}
}

void TestSaturateAndUnrelatedFactors() {
	Check(ResolvePipelineForceDestAlphaBlendFactor(Factor(BlendFactor::kSrcAlphaSaturate), true,
	                                               PipelineBlendFactorComponent::Color) ==
	          Factor(BlendFactor::kZero),
	      "source-alpha-saturate did not observe forced destination alpha for RGB");
	Check(ResolvePipelineForceDestAlphaBlendFactor(Factor(BlendFactor::kSrcAlphaSaturate), true,
	                                               PipelineBlendFactorComponent::Alpha) ==
	          Factor(BlendFactor::kOne),
	      "source-alpha-saturate lost its alpha-component value");
	Check(ResolvePipelineForceDestAlphaBlendFactor(Factor(BlendFactor::kDstColor), true,
	                                               PipelineBlendFactorComponent::Color) ==
	          Factor(BlendFactor::kDstColor),
	      "an unrelated destination-color factor was rewritten");
	Check(ResolvePipelineForceDestAlphaBlendFactor(Factor(BlendFactor::kConstantAlpha), true,
	                                               PipelineBlendFactorComponent::Alpha) ==
	          Factor(BlendFactor::kConstantAlpha),
	      "an unrelated constant-alpha factor was rewritten");
}

void TestAttachmentFactorSelection() {
	const auto combined = ResolvePipelineAttachmentBlendState(
	    Factor(BlendFactor::kSrcAlphaSaturate), Factor(BlendFactor::kDstAlpha), 3u,
	    Factor(BlendFactor::kDstColor), Factor(BlendFactor::kConstantAlpha), 4u, false, true);
	Check(combined.color_src_factor == Factor(BlendFactor::kZero) &&
	          combined.color_dst_factor == Factor(BlendFactor::kOne),
	      "combined RGB factors did not observe forced destination alpha");
	Check(combined.alpha_src_factor == Factor(BlendFactor::kOne) &&
	          combined.alpha_dst_factor == Factor(BlendFactor::kOne),
	      "combined alpha factors did not use the alpha components of the color factors");
	Check(combined.color_operation == 3u && combined.alpha_operation == 3u,
	      "combined alpha blending did not inherit the color operation");

	const auto separate = ResolvePipelineAttachmentBlendState(
	    Factor(BlendFactor::kSrcColor), Factor(BlendFactor::kDstColor), 1u,
	    Factor(BlendFactor::kDstAlpha), Factor(BlendFactor::kOneMinusDstAlpha), 4u, true,
	    true);
	Check(separate.color_src_factor == Factor(BlendFactor::kSrcColor) &&
	          separate.color_dst_factor == Factor(BlendFactor::kDstColor),
	      "separate alpha blending changed unrelated RGB factors");
	Check(separate.alpha_src_factor == Factor(BlendFactor::kOne) &&
	          separate.alpha_dst_factor == Factor(BlendFactor::kZero),
	      "separate alpha factors did not observe forced destination alpha");
	Check(separate.color_operation == 1u && separate.alpha_operation == 4u,
	      "separate alpha blending lost its independent operation");
}

} // namespace

int main() {
	TestActivation();
	TestDisabledStatePreservesFactors();
	TestDestinationAlphaFactors();
	TestSaturateAndUnrelatedFactors();
	TestAttachmentFactorSelection();
	std::puts("ForceDestAlphaContractTests: all cases passed");
	return 0;
}
