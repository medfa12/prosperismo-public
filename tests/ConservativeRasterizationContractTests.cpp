#include "graphics/host_gpu/renderer/pipeline/pipelineCache.h"
#include "graphics/guest_gpu/hardwareContext.h"

#include <cstdio>
#include <cstdlib>

namespace {

using Libs::Graphics::PipelineUsesPointOrLineRasterization;
using Libs::Graphics::ResolvePipelineConservativeRasterization;
using Libs::Graphics::HW::ConservativeRasterizationControl;

void Check(bool value, const char* message) {
	if (!value) {
		std::fprintf(stderr, "ConservativeRasterizationContractTests: failed: %s\n", message);
		std::abort();
	}
}

void TestModes() {
	const auto disabled = ResolvePipelineConservativeRasterization(0u, false, false, false, false);
	Check(disabled.representable &&
	          disabled.mode == vk::ConservativeRasterizationModeEXT::eDisabled,
	      "disabled mode required the optional host extension");

	const auto overestimate =
	    ResolvePipelineConservativeRasterization(0x01u, true, false, false, false);
	Check(overestimate.representable &&
	          overestimate.mode == vk::ConservativeRasterizationModeEXT::eOverestimate,
	      "overestimating mode did not map to Vulkan");
	Check(!ResolvePipelineConservativeRasterization(0x01u, false, false, false, false)
	           .representable,
	      "overestimating mode was accepted without the extension");

	const auto underestimate =
	    ResolvePipelineConservativeRasterization(0x20u, true, true, false, false);
	Check(underestimate.representable &&
	          underestimate.mode == vk::ConservativeRasterizationModeEXT::eUnderestimate,
	      "supported underestimating mode did not map to Vulkan");
	Check(!ResolvePipelineConservativeRasterization(0x20u, true, false, false, false)
	           .representable,
	      "underestimating mode ignored the host property");
}

void TestPointAndLineGate() {
	Check(!ResolvePipelineConservativeRasterization(0x01u, true, false, false, true).representable,
	      "point/line conservative rasterization ignored the host property");
	Check(ResolvePipelineConservativeRasterization(0x01u, true, false, true, true).representable,
	      "supported point/line conservative rasterization was rejected");

	Check(PipelineUsesPointOrLineRasterization(vk::PrimitiveTopology::ePointList,
	                                           vk::PolygonMode::eFill),
	      "point-list topology was not classified as point/line rasterization");
	Check(PipelineUsesPointOrLineRasterization(vk::PrimitiveTopology::eLineStrip,
	                                           vk::PolygonMode::eFill),
	      "line-strip topology was not classified as point/line rasterization");
	Check(PipelineUsesPointOrLineRasterization(vk::PrimitiveTopology::eTriangleList,
	                                           vk::PolygonMode::eLine),
	      "wireframe polygon mode was not classified as line rasterization");
	Check(!PipelineUsesPointOrLineRasterization(vk::PrimitiveTopology::eTriangleList,
	                                            vk::PolygonMode::eFill),
	      "filled triangles were misclassified as point/line rasterization");
}

void TestInvalidState() {
	Check(!ResolvePipelineConservativeRasterization(0x21u, true, true, true, false).representable,
	      "combined over/under mode was accepted");
	const auto legacy = ConservativeRasterizationControl::FromRegister(0x000d0001u);
	Check(legacy.mode == 0x01u && legacy.uncertainty_region == 0x000d0000u,
	      "mode decoding was contaminated by the legacy uncertainty-region field");
}

} // namespace

int main() {
	TestModes();
	TestPointAndLineGate();
	TestInvalidState();
	std::puts("ConservativeRasterizationContractTests: all cases passed");
	return 0;
}
