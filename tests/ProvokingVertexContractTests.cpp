#include "graphics/host_gpu/renderer/pipeline/pipelineCache.h"

#include <cstdio>
#include <cstdlib>

namespace {

using Libs::Graphics::ResolvePipelineProvokingVertex;

void Check(bool value, const char* message) {
	if (!value) {
		std::fprintf(stderr, "ProvokingVertexContractTests: failed: %s\n", message);
		std::abort();
	}
}

void TestFirstVertex() {
	const auto state = ResolvePipelineProvokingVertex(false, false);
	Check(state.representable, "first-vertex mode required an optional host feature");
	Check(state.mode == vk::ProvokingVertexModeEXT::eFirstVertex,
	      "first-vertex mode selected the wrong Vulkan state");
}

void TestLastVertex() {
	const auto supported = ResolvePipelineProvokingVertex(true, true);
	Check(supported.representable,
	      "last-vertex mode was rejected despite host provoking-vertex support");
	Check(supported.mode == vk::ProvokingVertexModeEXT::eLastVertex,
	      "last-vertex mode selected the wrong Vulkan state");

	const auto unsupported = ResolvePipelineProvokingVertex(true, false);
	Check(!unsupported.representable,
	      "last-vertex mode was silently approximated on an unsupported host");
}

} // namespace

int main() {
	TestFirstVertex();
	TestLastVertex();
	std::puts("ProvokingVertexContractTests: all cases passed");
	return 0;
}
