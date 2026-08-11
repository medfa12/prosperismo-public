#include "kernel/semaphoreContracts.h"

#include <cstdio>
#include <cstdlib>

namespace {

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "Prosperismo kernel semaphore contract test failed: %s\n", text);
		std::abort();
	}
}

void TestQueueOrderAttributeDecoding() {
	using Libs::LibKernel::Semaphore::DecodeKernelSemaQueueOrder;
	using Libs::LibKernel::Semaphore::KernelSemaQueueOrder;

	Check(DecodeKernelSemaQueueOrder(0) == KernelSemaQueueOrder::Fifo,
	      "the default attribute did not select FIFO order");
	Check(DecodeKernelSemaQueueOrder(0x01) == KernelSemaQueueOrder::Fifo,
	      "the explicit FIFO attribute did not select FIFO order");
	Check(DecodeKernelSemaQueueOrder(0x02) == KernelSemaQueueOrder::ThreadPriority,
	      "the priority attribute did not select thread-priority order");
}

} // namespace

int main() {
	TestQueueOrderAttributeDecoding();
	return 0;
}
