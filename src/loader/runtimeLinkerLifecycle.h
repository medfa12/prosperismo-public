#ifndef EMULATOR_INCLUDE_EMULATOR_LOADER_RUNTIMELINKERLIFECYCLE_H_
#define EMULATOR_INCLUDE_EMULATOR_LOADER_RUNTIMELINKERLIFECYCLE_H_

#include <cstdint>
#include <limits>

namespace Loader::RuntimeLinkerLifecycle {

struct ModuleReferenceState {
	uint64_t count = 0;
};

enum class ModuleReleaseAction { Untracked, Retained, Final };

template <class Callback>
constexpr int InvokeOptionalModuleLifecycleEntry(uint64_t entry_vaddr, Callback&& callback) {
	return entry_vaddr == 0 ? 0 : callback(entry_vaddr);
}

template <class Restore, class Relocate, class Protect>
[[nodiscard]] constexpr bool RunRelocationProtectionLifecycle(bool already_relocated,
                                                              Restore&& restore,
                                                              Relocate&& relocate,
                                                              Protect&& protect) {
	if (already_relocated && !restore()) {
		return false;
	}
	if (!relocate()) {
		return false;
	}
	return protect();
}

[[nodiscard]] constexpr bool TryAddReference(ModuleReferenceState* state) {
	if (state == nullptr || state->count == std::numeric_limits<uint64_t>::max()) {
		return false;
	}

	state->count++;
	return true;
}

[[nodiscard]] constexpr ModuleReleaseAction PrepareRelease(ModuleReferenceState* state) {
	if (state == nullptr || state->count == 0) {
		return ModuleReleaseAction::Untracked;
	}
	if (state->count == 1) {
		// Keep the final reference live until stop/unload succeeds so a busy release is retryable.
		return ModuleReleaseAction::Final;
	}

	state->count--;
	return ModuleReleaseAction::Retained;
}

} // namespace Loader::RuntimeLinkerLifecycle

#endif /* EMULATOR_INCLUDE_EMULATOR_LOADER_RUNTIMELINKERLIFECYCLE_H_ */
