#include "graphics/host_gpu/renderer/pipeline/pipelineCache.h"

#include <array>
#include <cstdio>
#include <cstdlib>
#include <utility>

namespace {

using Libs::Graphics::ResolvePipelineRasterOp;

void Check(bool value, const char* message) {
	if (!value) {
		std::fprintf(stderr, "RasterOpContractTests: failed: %s\n", message);
		std::abort();
	}
}

void TestCompleteRasterOpMapping() {
	constexpr std::array mappings = {
	    std::pair {0x00u, vk::LogicOp::eClear},
	    std::pair {0x05u, vk::LogicOp::eNor},
	    std::pair {0x0au, vk::LogicOp::eAndInverted},
	    std::pair {0x0fu, vk::LogicOp::eCopyInverted},
	    std::pair {0x44u, vk::LogicOp::eAndReverse},
	    std::pair {0x55u, vk::LogicOp::eInvert},
	    std::pair {0x5au, vk::LogicOp::eXor},
	    std::pair {0x5fu, vk::LogicOp::eNand},
	    std::pair {0x88u, vk::LogicOp::eAnd},
	    std::pair {0x99u, vk::LogicOp::eEquivalent},
	    std::pair {0xaau, vk::LogicOp::eNoOp},
	    std::pair {0xafu, vk::LogicOp::eOrInverted},
	    std::pair {0xccu, vk::LogicOp::eCopy},
	    std::pair {0xddu, vk::LogicOp::eOrReverse},
	    std::pair {0xeeu, vk::LogicOp::eOr},
	    std::pair {0xffu, vk::LogicOp::eSet},
	};

	for (const auto& [guest, host]: mappings) {
		const auto state = ResolvePipelineRasterOp(guest, true, false);
		Check(state.representable && state.operation == host,
		      "a documented raster operation was not mapped exactly");
		Check(state.enable == (guest != 0xccu),
		      "copy and Boolean raster-operation enable state was decoded incorrectly");
	}
}

void TestRepresentabilityGuards() {
	const auto copy = ResolvePipelineRasterOp(0xccu, false, true);
	Check(copy.representable && !copy.enable && copy.operation == vk::LogicOp::eCopy,
	      "copy incorrectly required the optional host feature or disabled blending");
	Check(!ResolvePipelineRasterOp(0x5au, false, false).representable,
	      "XOR was accepted without the host logic-op feature");
	Check(!ResolvePipelineRasterOp(0x5au, true, true).representable,
	      "a Boolean raster operation was combined with alpha blending");
	Check(!ResolvePipelineRasterOp(0x33u, true, false).representable,
	      "an undocumented raster-operation truth table was accepted");
}

} // namespace

int main() {
	TestCompleteRasterOpMapping();
	TestRepresentabilityGuards();
	std::puts("RasterOpContractTests: all cases passed");
	return 0;
}
