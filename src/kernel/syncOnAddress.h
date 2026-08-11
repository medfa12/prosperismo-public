#ifndef EMULATOR_INCLUDE_EMULATOR_KERNEL_SYNC_ON_ADDRESS_H_
#define EMULATOR_INCLUDE_EMULATOR_KERNEL_SYNC_ON_ADDRESS_H_

#include <cstdint>

namespace Libs::Posix {

// Host-side address-keyed wait/wake primitive used by the libKernel
// sceKernelSyncOnAddress compatibility exports.  The guest re-checks its
// condition after every return, so a timeout is intentionally reported as a
// successful (spurious) wake rather than an error.
class SyncOnAddressRegistry {
public:
	SyncOnAddressRegistry();
	~SyncOnAddressRegistry();
	SyncOnAddressRegistry(const SyncOnAddressRegistry&) = delete;
	SyncOnAddressRegistry& operator=(const SyncOnAddressRegistry&) = delete;

	// Park until a matching Wake call advances the address generation or the
	// bounded self-heal timeout expires. timeout_us == 0 selects the default.
	void Wait(uint64_t address, uint64_t timeout_us);

	// Advance the address generation and wake one waiter when count == 1;
	// zero or a value greater than one wakes all waiters.
	void Wake(uint64_t address, uint64_t count);

	// Deterministic contract-test hooks. They do not expose the host wait
	// implementation and are safe to call without any guest threads.
	uint64_t Generation(uint64_t address) const;

private:
	struct State;
	struct Impl;
	Impl* m_impl = nullptr;
};

} // namespace Libs::Posix

#endif
