#include "common/abi.h"
#include "libs/errno.h"
#include "loader/symbolDatabase.h"

#include <cstdint>
#include <cstdio>
#include <cstdlib>

namespace Kyty::Libs {
void PrintNameImpl(const char*, const char*, const char*) {}
} // namespace Kyty::Libs

namespace Libs {
void InitDialog_1(Loader::SymbolDatabase* symbols);
} // namespace Libs

namespace {

constexpr int32_t COMMON_DIALOG_ERROR_ALREADY_SYSTEM_INITIALIZED =
    static_cast<int32_t>(0x80b80002u);

using InitializeApi = int(KYTY_SYSV_ABI*)();

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "Prosperismo CommonDialog contract test failed: %s\n", text);
		std::abort();
	}
}

void TestInitializeLifetimeThroughNid() {
	Loader::SymbolDatabase symbols;
	Libs::InitDialog_1(&symbols);
	// The NID/export pairing and repeat-call result were independently confirmed in 3.20 and 12.40.
	const auto* record = symbols.FindByNid("uoUpLGNkygk", Loader::SymbolType::Func);
	Check(record != nullptr, "sceCommonDialogInitialize is not registered");
	const auto initialize = reinterpret_cast<InitializeApi>(record->vaddr);

	Check(initialize() == OK, "first common-dialog initialization failed");
	Check(initialize() == COMMON_DIALOG_ERROR_ALREADY_SYSTEM_INITIALIZED,
	      "duplicate common-dialog initialization was accepted");
	Check(initialize() == COMMON_DIALOG_ERROR_ALREADY_SYSTEM_INITIALIZED,
	      "common-dialog process state did not persist for the application lifetime");
}

} // namespace

int main() {
	TestInitializeLifetimeThroughNid();
	return 0;
}
