#include "kernel/syncOnAddress.h"
#include "libs/libKernelContracts.h"

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <string_view>
#include <thread>

namespace {

void Check(bool value, const char* message) {
	if (!value) {
		std::fprintf(stderr, "Prosperismo sync-on-address contract test failed: %s\n", message);
		std::abort();
	}
}

} // namespace

int main() {
	Libs::Posix::SyncOnAddressRegistry registry;
	constexpr uint64_t address = 0x514080000ull;

	Check(registry.Generation(address) == 0, "a new address did not start at generation zero");
	Check(std::string_view(Libs::LibKernel::SYNC_ON_ADDRESS_WAIT_NID) == "Hc4CaR6JBL0",
	      "the address-wait export does not use the firmware NID");
	Check(std::string_view(Libs::LibKernel::SYNC_ON_ADDRESS_WAKE_NID) == "q2y-wDIVWZA",
	      "the address-wake export does not use the firmware NID");
	std::atomic_bool entered = false;
	std::atomic_bool returned = false;
	std::thread waiter([&] {
		entered.store(true, std::memory_order_release);
		registry.Wait(address, 500'000);
		returned.store(true, std::memory_order_release);
	});
	while (!entered.load(std::memory_order_acquire)) {
		std::this_thread::yield();
	}
	std::this_thread::sleep_for(std::chrono::milliseconds(5));
	Check(!returned.load(std::memory_order_acquire), "a waiter returned before its address was woken");
	registry.Wake(address, 1);
	waiter.join();
	Check(returned.load(std::memory_order_acquire), "a matching wake did not release the waiter");
	Check(registry.Generation(address) == 1, "a wake did not advance the address generation");

	const auto before_timeout = std::chrono::steady_clock::now();
	registry.Wait(address + 8, 1'000);
	const auto elapsed_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
	    std::chrono::steady_clock::now() - before_timeout);
	Check(elapsed_ms.count() < 100, "a missed wake did not self-heal within the bounded timeout");

	std::atomic_int released = 0;
	std::thread waiter_a([&] {
		registry.Wait(address + 16, 500'000);
		released.fetch_add(1, std::memory_order_relaxed);
	});
	std::thread waiter_b([&] {
		registry.Wait(address + 16, 500'000);
		released.fetch_add(1, std::memory_order_relaxed);
	});
	std::this_thread::sleep_for(std::chrono::milliseconds(5));
	registry.Wake(address + 16, 0);
	waiter_a.join();
	waiter_b.join();
	Check(released.load(std::memory_order_relaxed) == 2, "wake-all did not release every waiter");

	return 0;
}
