#include "kernel/syncOnAddress.h"

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <memory>
#include <mutex>
#include <unordered_map>

namespace Libs::Posix {

struct SyncOnAddressRegistry::State {
	mutable std::mutex      mutex;
	std::condition_variable condition;
	uint64_t                generation = 0;
};

struct SyncOnAddressRegistry::Impl {
	mutable std::mutex                                      mutex;
	mutable std::unordered_map<uint64_t, std::shared_ptr<State>> states;

	std::shared_ptr<State> Get(uint64_t address) const {
		std::scoped_lock lock(mutex);
		auto             it = states.find(address);
		if (it != states.end()) {
			return it->second;
		}
		// The map is mutable so const Generation() can share the same state
		// lookup without exposing a second synchronization path.
		auto state       = std::make_shared<State>();
		states[address] = state;
		return state;
	}
};

SyncOnAddressRegistry::SyncOnAddressRegistry(): m_impl(new Impl()) {}

SyncOnAddressRegistry::~SyncOnAddressRegistry() {
	delete m_impl;
	m_impl = nullptr;
}

void SyncOnAddressRegistry::Wait(uint64_t address, uint64_t timeout_us) {
	static constexpr uint64_t DEFAULT_TIMEOUT_US = 100'000;
	const auto                timeout = std::chrono::microseconds(
	    std::min(timeout_us == 0 ? DEFAULT_TIMEOUT_US : timeout_us, DEFAULT_TIMEOUT_US));

	auto state = m_impl->Get(address);
	std::unique_lock lock(state->mutex);
	const auto       observed = state->generation;
	(void) state->condition.wait_for(lock, timeout,
	                                [&] { return state->generation != observed; });
}

void SyncOnAddressRegistry::Wake(uint64_t address, uint64_t count) {
	auto state = m_impl->Get(address);
	{
		std::scoped_lock lock(state->mutex);
		state->generation++;
	}
	if (count == 1) {
		state->condition.notify_one();
	} else {
		state->condition.notify_all();
	}
}

uint64_t SyncOnAddressRegistry::Generation(uint64_t address) const {
	auto state = m_impl->Get(address);
	std::scoped_lock lock(state->mutex);
	return state->generation;
}

} // namespace Libs::Posix
