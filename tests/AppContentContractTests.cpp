#include "common/abi.h"
#include "libs/appContentContracts.h"
#include "libs/errno.h"
#include "loader/symbolDatabase.h"

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

namespace Kyty::Libs {
void PrintNameImpl(const char*, const char*, const char*) {}
} // namespace Kyty::Libs

namespace Loader {
bool SystemContentParamSfoGetInt(const char*, int32_t*) {
	return false;
}

bool SystemContentParamSfoGetString(const char*, std::string*) {
	return false;
}
} // namespace Loader

namespace Libs {
void InitAppContent_1(Loader::SymbolDatabase* symbols);
} // namespace Libs

namespace {

constexpr int32_t EXPECTED_PARAMETER_ERROR = -2133262334;

struct InitParam {
	char reserved[32];
};

struct BootParam {
	char     reserved1[4];
	uint32_t attr;
	char     reserved2[32];
};

static_assert(sizeof(InitParam) == 32);
static_assert(sizeof(BootParam) == 40);
static_assert(Libs::AppContent::APP_CONTENT_ERROR_PARAMETER == EXPECTED_PARAMETER_ERROR);

using InitializeApi = int(KYTY_SYSV_ABI*)(const InitParam*, BootParam*);

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "Prosperismo AppContent contract test failed: %s\n", text);
		std::abort();
	}
}

void CheckRejectedWithoutOutputMutation(InitializeApi initialize, const InitParam* init_param,
                                        BootParam* boot_param, const char* text) {
	const BootParam before = *boot_param;
	Check(initialize(init_param, boot_param) == EXPECTED_PARAMETER_ERROR, text);
	Check(std::memcmp(boot_param, &before, sizeof(before)) == 0,
	      "initialization validation modified the boot output");
}

void TestInitializationValidation() {
	Loader::SymbolDatabase symbols;
	Libs::InitAppContent_1(&symbols);
	const auto* record = symbols.FindByNid("R9lA82OraNs", Loader::SymbolType::Func);
	Check(record != nullptr, "sceAppContentInitialize is not registered");
	const auto initialize = reinterpret_cast<InitializeApi>(record->vaddr);

	InitParam init {};
	BootParam boot {};
	boot.attr = 0x12345678;
	CheckRejectedWithoutOutputMutation(initialize, nullptr, &boot,
	                                   "null initialization parameters were accepted");
	Check(initialize(&init, nullptr) == EXPECTED_PARAMETER_ERROR, "null boot output was accepted");

	for (size_t index = 0; index < sizeof(init.reserved); index++) {
		init                 = {};
		boot                 = {};
		init.reserved[index] = 1;
		boot.attr            = 0x12345678;
		CheckRejectedWithoutOutputMutation(initialize, &init, &boot,
		                                   "nonzero init reserved byte was accepted");
	}

	for (size_t index = 0; index < sizeof(boot.reserved1); index++) {
		init                  = {};
		boot                  = {};
		boot.reserved1[index] = 1;
		CheckRejectedWithoutOutputMutation(initialize, &init, &boot,
		                                   "nonzero boot reserved1 byte was accepted");
	}

	for (size_t index = 0; index < sizeof(boot.reserved2); index++) {
		init                  = {};
		boot                  = {};
		boot.reserved2[index] = 1;
		CheckRejectedWithoutOutputMutation(initialize, &init, &boot,
		                                   "nonzero boot reserved2 byte was accepted");
	}

	init      = {};
	boot      = {};
	boot.attr = 0x12345678;
	Check(initialize(&init, &boot) == OK, "zeroed initialization parameters were rejected");
	const BootParam zero_boot {};
	Check(std::memcmp(&boot, &zero_boot, sizeof(boot)) == 0,
	      "successful initialization did not clear the boot output");
}

} // namespace

int main() {
	TestInitializationValidation();
	return 0;
}
