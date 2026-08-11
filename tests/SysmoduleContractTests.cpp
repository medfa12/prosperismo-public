#include "common/abi.h"
#include "libs/sysmoduleContracts.h"
#include "loader/symbolDatabase.h"

#include <cstdint>
#include <cstdio>
#include <cstdlib>

namespace Kyty::Libs {
void PrintNameImpl(const char*, const char*, const char*) {}
} // namespace Kyty::Libs

namespace Libs {
void InitSysmodule_1(Loader::SymbolDatabase* symbols);

namespace LibKernel {
struct ModuleInfoForUnwind;
int KYTY_SYSV_ABI KernelGetModuleInfoForUnwind(uint64_t, int, ModuleInfoForUnwind*) {
	return 0;
}
} // namespace LibKernel
} // namespace Libs

namespace {

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "Prosperismo Sysmodule contract test failed: %s\n", text);
		std::abort();
	}
}

void TestReservedModuleIdValidation() {
	using namespace Libs::Sysmodule;

	Loader::SymbolDatabase symbols;
	Libs::InitSysmodule_1(&symbols);

	constexpr const char* public_nids[] = {"g8cM39EUZ6o", "eR2bZFAAU0Q", "fMP5NHUOaMk"};
	for (const char* nid: public_nids) {
		const auto* record = symbols.FindByNid(nid, Loader::SymbolType::Func);
		Check(record != nullptr, "required Sysmodule export is not registered");

		using ModuleApi = int(KYTY_SYSV_ABI*)(uint16_t);
		const auto api   = reinterpret_cast<ModuleApi>(record->vaddr);
		Check(api(0) == SYSMODULE_ERROR_INVALID_VALUE,
		      "reserved module ID zero returned success");
	}
}

} // namespace

int main() {
	TestReservedModuleIdValidation();
	return 0;
}
