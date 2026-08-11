#include "libs/videoDec2Contracts.h"

#include <cstdio>
#include <cstdlib>
#include <limits>

namespace {

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "Prosperismo Videodec2 contract test failed: %s\n", text);
		std::abort();
	}
}

void TestInputQueueDepthValidation() {
	using namespace Libs::VideoDec2;

	Check(ValidateInputQueueDepth(1) == OK, "minimum input queue depth was rejected");
	Check(ValidateInputQueueDepth(8) == OK, "maximum input queue depth was rejected");

	constexpr uint32_t invalid_depths[] = {0, 9, std::numeric_limits<uint32_t>::max()};
	for (const uint32_t depth: invalid_depths) {
		Check(ValidateInputQueueDepth(depth) == VIDEODEC2_ERROR_INPUT_QUEUE_DEPTH,
		      "out-of-range input queue depth returned the wrong result");
	}
}

void TestFrameBufferAlignmentValidation() {
	using namespace Libs::VideoDec2;

	constexpr uintptr_t aligned_addresses[] = {0x100, 0x1000, 0x1234500};
	for (const uintptr_t address: aligned_addresses) {
		Check(ValidateFrameBufferAlignment(address) == OK,
		      "aligned output frame buffer was rejected");
	}

	constexpr uintptr_t misaligned_addresses[] = {1, 0xff, 0x101, 0x12345ff};
	for (const uintptr_t address: misaligned_addresses) {
		Check(ValidateFrameBufferAlignment(address) ==
		          VIDEODEC2_ERROR_FRAME_BUFFER_ALIGNMENT,
		      "misaligned output frame buffer returned the wrong result");
	}
}

} // namespace

int main() {
	TestInputQueueDepthValidation();
	TestFrameBufferAlignmentValidation();
	return 0;
}
