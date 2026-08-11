#include "libs/controller.h"
#include "libs/padData.h"

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace Kyty::Libs {
void PrintNameImpl(const char*, const char*, const char*) {}
} // namespace Kyty::Libs

namespace Libs::LibKernel {
uint64_t KYTY_SYSV_ABI KernelGetProcessTime() {
	return 0;
}
} // namespace Libs::LibKernel

namespace {

constexpr int PAD_ERROR_INVALID_ARG = -2137915391;

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "Prosperismo pad contract test failed: %s\n", text);
		std::abort();
	}
}

void TestReadRejectsInvalidSampleCounts() {
	constexpr int invalid_counts[] = {0, -1, 65};

	for (const int count: invalid_counts) {
		Libs::Controller::PadData data {};
		std::memset(&data, 0xa5, sizeof(data));
		const Libs::Controller::PadData before = data;

		Check(Libs::Controller::PadRead(1, &data, count) == PAD_ERROR_INVALID_ARG,
		      "scePadRead accepted an out-of-range sample count");
		Check(std::memcmp(&data, &before, sizeof(data)) == 0,
		      "scePadRead modified output after rejecting the sample count");
	}
}

} // namespace

int main() {
	TestReadRejectsInvalidSampleCounts();
	return 0;
}
