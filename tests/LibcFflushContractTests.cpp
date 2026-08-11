#include "libs/libcContracts.h"

#include <cassert>
#include <cstdint>
#include <cstdio>

int main() {
	using Libs::LibcInternal::FflushStreamIsSupported;

	assert(FflushStreamIsSupported(nullptr, stdout));
	assert(FflushStreamIsSupported(stdout, stdout));
	const auto* invalid = reinterpret_cast<const FILE*>(static_cast<uintptr_t>(1));
	assert(!FflushStreamIsSupported(invalid, stdout));

	std::puts("LibcFflushContractTests: all cases passed");
	return 0;
}
