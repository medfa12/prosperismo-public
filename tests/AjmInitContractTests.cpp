#include "libs/audio.h"

#include <cstdint>
#include <cstdio>
#include <cstdlib>

// Production provides this through libs.cpp; this focused test does not initialize that stack.
namespace Kyty::Libs {
void PrintNameImpl(const char*, const char*, const char*) {}
} // namespace Kyty::Libs

namespace {

namespace Ajm = Libs::Audio::Ajm;

constexpr int32_t AjmErrorInvalidParameter = static_cast<int32_t>(0x80930005u);

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "AjmInitContractTests: failed: %s\n", text);
		std::abort();
	}
}

void TestNullContextOutputIsRejected() {
	Check(Ajm::AjmInitialize(0, nullptr) == AjmErrorInvalidParameter,
	      "null context output returned the wrong error");
}

void TestValidContextOutputRemainsAccepted() {
	uint32_t context = 0;
	Check(Ajm::AjmInitialize(0, &context) == 0, "valid initialization was rejected");
	Check(context != 0, "valid initialization did not produce a context");
}

} // namespace

int main() {
	TestNullContextOutputIsRejected();
	TestValidContextOutputRemainsAccepted();
	std::printf("AjmInitContractTests: all cases passed\n");
	return 0;
}
