#include "common/abi.h"
#include "libs/errno.h"
#include "loader/symbolDatabase.h"

#include <array>
#include <cstdint>
#include <cstdio>
#include <cstdlib>

namespace Kyty::Libs {
void PrintNameImpl(const char*, const char*, const char*) {}
} // namespace Kyty::Libs

namespace Loader {
bool SystemContentGetChunksNum(uint32_t* num) {
	if (num != nullptr) {
		*num = 1;
	}
	return true;
}
} // namespace Loader

namespace Libs {
void InitPlayGo_1(Loader::SymbolDatabase* symbols);
} // namespace Libs

namespace {

struct PlayGoInitParams {
	const void* buffer;
	uint32_t    size;
	uint32_t    reserved;
};

using InitializeApi = int(KYTY_SYSV_ABI*)(const PlayGoInitParams*);
using TerminateApi  = int(KYTY_SYSV_ABI*)();
using OpenApi       = int(KYTY_SYSV_ABI*)(int*, const void*);
using CloseApi      = int(KYTY_SYSV_ABI*)(int);

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "Prosperismo PlayGo lifecycle test failed: %s\n", text);
		std::abort();
	}
}

void TestLifecycleThroughNids() {
	Loader::SymbolDatabase symbols;
	Libs::InitPlayGo_1(&symbols);

	const auto* initialize_record = symbols.FindByNid("ts6GlZOKRrE", Loader::SymbolType::Func);
	const auto* terminate_record  = symbols.FindByNid("MPe0EeBGM-E", Loader::SymbolType::Func);
	const auto* open_record       = symbols.FindByNid("M1Gma1ocrGE", Loader::SymbolType::Func);
	const auto* close_record      = symbols.FindByNid("Uco1I0dlDi8", Loader::SymbolType::Func);
	Check(initialize_record != nullptr, "scePlayGoInitialize is not registered");
	Check(terminate_record != nullptr, "scePlayGoTerminate is not registered");
	Check(open_record != nullptr, "scePlayGoOpen is not registered");
	Check(close_record != nullptr, "scePlayGoClose is not registered");

	const auto initialize = reinterpret_cast<InitializeApi>(initialize_record->vaddr);
	const auto terminate  = reinterpret_cast<TerminateApi>(terminate_record->vaddr);
	const auto open       = reinterpret_cast<OpenApi>(open_record->vaddr);
	const auto close      = reinterpret_cast<CloseApi>(close_record->vaddr);

	alignas(16) static std::array<uint8_t, 2u * 1024u * 1024u> work_buffer {};
	const PlayGoInitParams params {work_buffer.data(), static_cast<uint32_t>(work_buffer.size()), 0};

	int handle = 0x12345678;
	Check(open(&handle, nullptr) == Libs::PlayGo::PLAYGO_ERROR_NOT_INITIALIZED,
	      "package open before initialization succeeded");
	Check(handle == 0x12345678, "failed package open modified its output handle");
	Check(close(1) == Libs::PlayGo::PLAYGO_ERROR_NOT_INITIALIZED,
	      "package close before initialization succeeded");
	Check(terminate() == Libs::PlayGo::PLAYGO_ERROR_NOT_INITIALIZED,
	      "termination before initialization succeeded");
	Check(initialize(&params) == OK, "initialization failed");
	Check(initialize(&params) == Libs::PlayGo::PLAYGO_ERROR_ALREADY_INITIALIZED,
	      "duplicate initialization succeeded");
	Check(open(&handle, nullptr) == OK && handle == 1, "first package open failed");
	int second_handle = -1;
	Check(open(&second_handle, nullptr) == OK && second_handle == handle,
	      "repeated package open did not retain the package handle");
	Check(close(handle) == OK, "first package close failed");
	Check(close(handle) == OK, "reference-counted package close failed");
	Check(close(handle) == Libs::PlayGo::PLAYGO_ERROR_BAD_HANDLE,
	      "package close succeeded with no open reference");
	Check(terminate() == OK, "termination of initialized library failed");
	Check(terminate() == Libs::PlayGo::PLAYGO_ERROR_NOT_INITIALIZED,
	      "repeated termination succeeded");
	Check(initialize(&params) == OK, "reinitialization after termination failed");
	Check(close(handle) == Libs::PlayGo::PLAYGO_ERROR_BAD_HANDLE,
	      "a package handle survived termination");
	Check(terminate() == OK, "final cleanup failed");
}

} // namespace

int main() {
	TestLifecycleThroughNids();
	return 0;
}
