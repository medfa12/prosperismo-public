#include "graphics/host_gpu/renderer/renderDraw.h"

#include <cstdio>
#include <cstdlib>

namespace {

using Libs::Graphics::ResolveVertexWindowOffset;

void Check(bool value, const char* message) {
	if (!value) {
		std::fprintf(stderr, "VertexWindowOffsetContractTests: failed: %s\n", message);
		std::abort();
	}
}

void TestDisabled() {
	const auto offset = ResolveVertexWindowOffset(320.5f, -240.25f, 47, -19, false);
	Check(offset.x == 320.5f && offset.y == -240.25f,
	      "disabled vertex window offset changed the viewport transform");
}

void TestEnabled() {
	const auto positive = ResolveVertexWindowOffset(320.5f, 240.25f, 47, 19, true);
	Check(positive.x == 367.5f && positive.y == 259.25f,
	      "enabled positive window offset was not added to vertex coordinates");

	const auto signed_offset = ResolveVertexWindowOffset(-10.0f, 5.0f, -32768, 32767, true);
	Check(signed_offset.x == -32778.0f && signed_offset.y == 32772.0f,
	      "signed 16-bit window-offset boundaries were not preserved");
}

} // namespace

int main() {
	TestDisabled();
	TestEnabled();
	std::puts("VertexWindowOffsetContractTests: all cases passed");
	return 0;
}
