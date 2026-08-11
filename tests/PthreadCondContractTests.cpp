#include "kernel/pthreadContracts.h"
#include "libs/libKernelContracts.h"

#include <cstdio>
#include <cstdlib>
#include <string_view>

namespace {

void Check(bool value, const char* message) {
	if (!value) {
		std::fprintf(stderr, "Prosperismo pthread condition contract test failed: %s\n", message);
		std::abort();
	}
}

} // namespace

int main() {
	using Libs::LibKernel::PthreadCondSignaltoResult;

	Check(PthreadCondSignaltoResult(true) == 0,
	      "a targeted waiter that was woken did not return success");
	Check(static_cast<uint32_t>(PthreadCondSignaltoResult(false)) == 0x80020001u,
	      "a thread not waiting on the condition did not return EPERM");
	Check(std::string_view(Libs::LibKernel::PTHREAD_RWLOCK_TRYRDLOCK_NID) == "XD3mDeybCnk",
	      "the SCE try-read-lock entry does not use the firmware NID");
	Check(std::string_view(Libs::LibKernel::PTHREAD_RWLOCK_TIMEDRDLOCK_NID) == "iPtZRWICjrM",
	      "the SCE timed-read-lock entry does not use the firmware NID");
	Check(std::string_view(Libs::LibKernel::PTHREAD_RWLOCK_TIMEDWRLOCK_NID) == "adh--6nIqTk",
	      "the SCE timed-write-lock entry does not use the firmware NID");
	Check(std::string_view(Libs::LibKernel::PTHREAD_RWLOCKATTR_GETTYPE_NID) == "Kyls1ChFyrc",
	      "the SCE rwlock attribute type getter does not use the firmware NID");

	return 0;
}
