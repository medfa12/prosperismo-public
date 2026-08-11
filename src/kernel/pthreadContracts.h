#ifndef EMULATOR_INCLUDE_EMULATOR_KERNEL_PTHREAD_CONTRACTS_H_
#define EMULATOR_INCLUDE_EMULATOR_KERNEL_PTHREAD_CONTRACTS_H_

namespace Libs::LibKernel {

constexpr int PthreadCondSignaltoResult(bool waiter_woken) {
	constexpr int kernel_error_eperm = -2147352575;
	return waiter_woken ? 0 : kernel_error_eperm;
}

} // namespace Libs::LibKernel

#endif // EMULATOR_INCLUDE_EMULATOR_KERNEL_PTHREAD_CONTRACTS_H_
