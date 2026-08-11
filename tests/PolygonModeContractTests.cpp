#include "graphics/host_gpu/renderer/pipeline/pipelineCache.h"

#include <cstdio>
#include <cstdlib>

namespace {

using Libs::Graphics::ResolvePipelinePolygonMode;

void Check(bool value, const char* message) {
	if (!value) {
		std::fprintf(stderr, "PolygonModeContractTests: failed: %s\n", message);
		std::abort();
	}
}

void TestDisabledAndFilledModes() {
	const auto disabled = ResolvePipelinePolygonMode(0u, 0u, 1u, false, false, false);
	Check(disabled.representable && disabled.mode == vk::PolygonMode::eFill,
	      "disabled guest polygon mode did not force fill");

	const auto filled = ResolvePipelinePolygonMode(1u, 2u, 2u, false, false, false);
	Check(filled.representable && filled.mode == vk::PolygonMode::eFill,
	      "matching filled faces required a non-solid host feature");
}

void TestNonSolidModes() {
	const auto line = ResolvePipelinePolygonMode(1u, 1u, 1u, false, false, true);
	Check(line.representable && line.mode == vk::PolygonMode::eLine,
	      "matching wireframe faces did not select Vulkan line mode");
	Check(!ResolvePipelinePolygonMode(1u, 1u, 1u, false, false, false).representable,
	      "wireframe mode was accepted without fillModeNonSolid");

	const auto point = ResolvePipelinePolygonMode(1u, 0u, 0u, false, false, true);
	Check(point.representable && point.mode == vk::PolygonMode::ePoint,
	      "matching point faces did not select Vulkan point mode");
}

void TestVisibleFaceSelection() {
	const auto front = ResolvePipelinePolygonMode(1u, 1u, 0u, false, true, true);
	Check(front.representable && front.mode == vk::PolygonMode::eLine,
	      "front-only rendering selected the culled back-face mode");

	const auto back = ResolvePipelinePolygonMode(1u, 1u, 0u, true, false, true);
	Check(back.representable && back.mode == vk::PolygonMode::ePoint,
	      "back-only rendering selected the culled front-face mode");

	Check(!ResolvePipelinePolygonMode(1u, 1u, 0u, false, false, true).representable,
	      "different visible front/back modes were silently collapsed");
	Check(ResolvePipelinePolygonMode(1u, 1u, 0u, true, true, false).representable,
	      "fully culled draw required an irrelevant non-solid host feature");
}

void TestInvalidState() {
	Check(!ResolvePipelinePolygonMode(2u, 2u, 2u, false, false, true).representable,
	      "unknown polygon-mode enable value was accepted");
	Check(!ResolvePipelinePolygonMode(1u, 3u, 3u, false, false, true).representable,
	      "unknown polygon fill mode was accepted");
}

} // namespace

int main() {
	TestDisabledAndFilledModes();
	TestNonSolidModes();
	TestVisibleFaceSelection();
	TestInvalidState();
	std::puts("PolygonModeContractTests: all cases passed");
	return 0;
}
