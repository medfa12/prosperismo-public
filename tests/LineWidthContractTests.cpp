#include "graphics/guest_gpu/hardwareContext.h"
#include "graphics/host_gpu/renderer/renderDraw.h"

#include <cstdio>
#include <cstdlib>
#include <limits>

namespace {

using Libs::Graphics::HW::DecodeLineWidthEighths;
using Libs::Graphics::ResolvePipelineLineWidth;

void Check(bool value, const char* message) {
	if (!value) {
		std::fprintf(stderr, "LineWidthContractTests: failed: %s\n", message);
		std::abort();
	}
}

void TestGuestEighthPixelDecode() {
	Check(DecodeLineWidthEighths(8u) == 1.0f, "the default line width was decoded incorrectly");
	Check(DecodeLineWidthEighths(20u) == 2.5f,
	      "a non-default line width lost its eighth-pixel precision");
	Check(DecodeLineWidthEighths(0xffffu) == 8191.875f,
	      "the complete 16-bit line-width field was not decoded");
}

void TestHostRepresentability() {
	auto state = ResolvePipelineLineWidth(1.0f, false, 1.0f, 1.0f);
	Check(state.representable && state.width == 1.0f,
	      "the core one-pixel width incorrectly required wideLines");

	state = ResolvePipelineLineWidth(2.5f, true, 0.5f, 8.0f);
	Check(state.representable && state.width == 2.5f,
	      "a supported wide line was not preserved");
	Check(!ResolvePipelineLineWidth(2.5f, false, 0.5f, 8.0f).representable,
	      "a wide line was accepted without the host feature");
	Check(!ResolvePipelineLineWidth(8.125f, true, 0.5f, 8.0f).representable,
	      "a width beyond the host range was silently accepted");
	Check(!ResolvePipelineLineWidth(0.0f, true, 0.0f, 8.0f).representable,
	      "a zero line width was accepted");
	Check(!ResolvePipelineLineWidth(std::numeric_limits<float>::infinity(), true, 0.5f, 8.0f)
	           .representable,
	      "a non-finite line width was accepted");
}

} // namespace

int main() {
	TestGuestEighthPixelDecode();
	TestHostRepresentability();
	std::puts("LineWidthContractTests: all cases passed");
	return 0;
}
