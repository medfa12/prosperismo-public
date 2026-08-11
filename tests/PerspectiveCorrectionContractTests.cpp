#include "graphics/shader/shader.h"

#include <cstdio>
#include <cstdlib>

namespace {

using namespace Libs::Graphics;

void Check(bool condition, const char* message) {
	if (!condition) {
		std::fprintf(stderr, "PerspectiveCorrectionContractTests: %s\n", message);
		std::abort();
	}
}

} // namespace

int main() {
	Check(!ResolvePixelNoPerspective(false, false),
	      "perspective-correct shader unexpectedly selected linear interpolation");
	Check(ResolvePixelNoPerspective(false, true),
	      "global perspective-correction disable did not select linear interpolation");
	Check(ResolvePixelNoPerspective(true, false),
	      "shader-local linear interpolation was not preserved");
	Check(ResolvePixelNoPerspective(true, true),
	      "combined global and shader-local disable was not monotonic");

	Check(PixelNoPerspectiveCacheIdentity(false) != PixelNoPerspectiveCacheIdentity(true),
	      "pixel shader cache identity omitted effective interpolation mode");
	Check(PixelParameterUsesNoPerspective(true, false),
	      "non-flat pixel parameter omitted NoPerspective");
	Check(!PixelParameterUsesNoPerspective(true, true),
	      "flat pixel parameter also selected NoPerspective");
	Check(!PixelParameterUsesNoPerspective(false, false),
	      "perspective-correct pixel parameter selected NoPerspective");

	std::puts("PerspectiveCorrectionContractTests: all cases passed");
	return 0;
}
