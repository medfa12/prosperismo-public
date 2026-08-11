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
void InitUserService_1(Loader::SymbolDatabase* symbols);
} // namespace Libs

namespace {

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "Prosperismo UserService contract test failed: %s\n", text);
		std::abort();
	}
}

const Loader::SymbolRecord* FindFunction(const Loader::SymbolDatabase& symbols, const char* nid) {
	const auto* record = symbols.FindByNid(nid, Loader::SymbolType::Func);
	Check(record != nullptr, "required UserService export is not registered");
	return record;
}

void TestInitializationAndTerminationLifecycle() {
	Loader::SymbolDatabase symbols;
	Libs::InitUserService_1(&symbols);

	const auto* initialize_record  = FindFunction(symbols, "j3YMu1MVNNo");
	const auto* initialize2_record = FindFunction(symbols, "az-0R6eviZ0");
	const auto* terminate_record   = FindFunction(symbols, "bwFjS+bX9mA");
	const auto* user_number_record = FindFunction(symbols, "qbwy0Ub8b3M");

	Check(terminate_record->vaddr != user_number_record->vaddr,
	      "termination is incorrectly aliased to user-number lookup");

	using InitializeAbi  = int(KYTY_SYSV_ABI*)(const void*);
	using Initialize2Abi = int(KYTY_SYSV_ABI*)(int, uint64_t);
	using TerminateAbi   = int(KYTY_SYSV_ABI*)();

	const auto initialize  = reinterpret_cast<InitializeAbi>(initialize_record->vaddr);
	const auto initialize2 = reinterpret_cast<Initialize2Abi>(initialize2_record->vaddr);
	const auto terminate   = reinterpret_cast<TerminateAbi>(terminate_record->vaddr);

	Check(terminate() == Libs::UserService::USER_SERVICE_ERROR_NOT_INITIALIZED,
	      "termination before initialization returned the wrong error");
	Check(initialize(nullptr) == OK, "initialization with default parameters failed");
	Check(initialize2(700, 0) == Libs::UserService::USER_SERVICE_ERROR_ALREADY_INITIALIZED,
	      "the two initialization entry points do not share lifecycle state");
	Check(terminate() == OK, "termination after initialization failed");

	Check(initialize2(700, 0) == OK, "reinitialization after termination failed");
	Check(initialize(nullptr) == Libs::UserService::USER_SERVICE_ERROR_ALREADY_INITIALIZED,
	      "duplicate initialization was accepted");
	Check(terminate() == OK, "second lifecycle termination failed");
	Check(terminate() == Libs::UserService::USER_SERVICE_ERROR_NOT_INITIALIZED,
	      "duplicate termination was accepted");
}

} // namespace

int main() {
	TestInitializationAndTerminationLifecycle();
	return 0;
}
