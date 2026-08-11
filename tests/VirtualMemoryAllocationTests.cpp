#include "common/commonSubsystem.h"
#include "common/emulatorConfig.h"
#include "common/file.h"
#include "common/logging/log.h"
#include "common/subsystems.h"
#include "common/threads.h"
#include "common/virtualMemory.h"
#include "kernel/fileSystem.h"
#include "kernel/memory.h"
#include "kernel/pthread.h"
#include "libs/errno.h"
#include "loader/runtimeLinker.h"
#include "loader/systemContent.h"

#include <array>
#include <cinttypes>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
#include <pthread_compat.h>
#endif
#include <sched.h>
#include <string>

namespace Libs {
void InitLibKernel_1(Loader::SymbolDatabase* symbols);
}

namespace {

using Libs::LibKernel::Memory::VirtualQueryInfo;

// Prospero ABI?
constexpr uint64_t SceKernelPageSize             = 0x4000;
constexpr uint64_t SceKernelTotalPhysicalSize    = 13824ull * 1024ull * 1024ull;
constexpr uint64_t TestFlexibleMemorySize        = 3072ull * 1024ull * 1024ull;
constexpr int      SceKernelProtCpuRead          = 0x01;
constexpr int      SceKernelProtCpuRw            = 0x02;
constexpr int      SceKernelProtCpuExec          = 0x04;
constexpr int      SceKernelProtAmprRead         = 0x40;
constexpr int      SceKernelProtAmprWrite        = 0x80;
constexpr int      SceKernelProtAcpRead          = 0x100;
constexpr int      SceKernelProtAcpWrite         = 0x200;
constexpr int      SceKernelMapFixed             = 0x10;
constexpr int      SceKernelMapNoOverwrite       = 0x80;
constexpr int      SceKernelMapDmemCompat        = 0x400;
constexpr int      SceKernelMapNoCoalesce        = 0x400000;
constexpr int      SceKernelMapAligned64Kb       = 16 << 24;
constexpr int      SceKernelVqFindNext           = 1;
constexpr int      SceKernelMtypeC               = 11;
constexpr uint64_t SceKernelDirectMemoryStart    = 0;
constexpr uint64_t SceKernelMemoryPoolReserveLen = 0x200000;
constexpr uint64_t SceKernelMemoryPoolCommitLen  = 0x10000;
constexpr uint64_t SceKernelMemoryPoolExpandLen  = 0x400000;
constexpr uint64_t SceKernelMemoryPoolAlignment  = 0x10000;
constexpr int      ErrorAccess                   = Libs::LibKernel::KERNEL_ERROR_EACCES;

struct TestFailure {};

int g_failed_tests = 0;

[[noreturn]] void Fail(const char* test, const std::string& message) {
	std::fflush(stdout);
	std::fprintf(stderr, "VirtualMemoryAllocationTests: %s failed: %s\n", test, message.c_str());
	g_failed_tests++;
	throw TestFailure {};
}

void Check(const char* test, bool value, const std::string& message) {
	if (!value) {
		Fail(test, message);
	}
}

void CheckOk(const char* test, int result, const char* action) {
	if (result != OK) {
		char buffer[256] = {};
		std::snprintf(buffer, sizeof(buffer), "%s returned 0x%08" PRIx32, action,
		              static_cast<uint32_t>(result));
		Fail(test, buffer);
	}
}

void CheckFailed(const char* test, int result, const char* action) {
	if (result >= OK) {
		char buffer[256] = {};
		std::snprintf(buffer, sizeof(buffer), "%s returned success, expected negative error",
		              action);
		Fail(test, buffer);
	}
}

void InitSubsystems() {
	static bool initialized = false;
	if (initialized) {
		return;
	}

	static char  arg0[] = "virtual_memory_allocation_tests";
	static char* argv[] = {arg0};

	auto* slist  = Common::SubsystemsList::Instance();
	auto* core   = Common::CommonSubsystem::Instance();
	auto* config = Config::ConfigSubsystem::Instance();
	auto* fs     = Libs::LibKernel::FileSystem::FileSystemSubsystem::Instance();
	auto* log    = Log::LogSubsystem::Instance();
	auto* memory = Libs::LibKernel::Memory::MemorySubsystem::Instance();
	auto* thread = Common::ThreadsSubsystem::Instance();

	slist->SetArgs(1, argv);
	slist->Add(thread, {});
	slist->Add(core, {});
	slist->Add(config, {core});
	Check("InitSubsystems", slist->InitAll(false), "failed to initialize base subsystems");

	Config::ConfigOptions options;
	options.printf_direction = Config::OutputDirection::Silent;
	Config::Load(options);

	slist->Add(log, {core, config});
	Check("InitSubsystems", slist->InitAll(false), "failed to initialize logging subsystem");

	const auto param_json = std::filesystem::temp_directory_path() /
	                        ("kyty_virtual_memory_" +
	                         std::to_string(reinterpret_cast<uintptr_t>(&initialized)) + ".json");
	constexpr char json[] = R"({"kernel":{"flexibleMemorySize":3221225472}})";
	Common::File   param_file;
	Check("InitSubsystems", param_file.Create(param_json), "failed to create temporary param.json");
	uint32_t bytes_written = 0;
	param_file.Write(json, sizeof(json) - 1, &bytes_written);
	param_file.Close();
	Check("InitSubsystems", bytes_written == sizeof(json) - 1,
	      "failed to write temporary param.json");

	Loader::SystemContentLoadParamSfo(param_json);
	const auto flexible_memory_size = Loader::SystemContentGetFlexibleMemorySize();
	Check("InitSubsystems", Common::File::DeleteFile(param_json),
	      "failed to remove temporary param.json");
	Check("InitSubsystems", flexible_memory_size == TestFlexibleMemorySize,
	      "failed to read flexible memory size from param.json");
	Libs::LibKernel::Memory::SetFlexibleMemorySize(flexible_memory_size);

	slist->Add(memory, {core, log, thread});
	slist->Add(fs, {core, log, thread});
	Check("InitSubsystems", slist->InitAll(false), "failed to initialize runtime subsystems");

	initialized = true;
}

void RunTest(void (*test_func)()) {
	if (g_failed_tests != 0) {
		return;
	}
	try {
		test_func();
	} catch (const TestFailure&) {
	}
}

VirtualQueryInfo Query(const char* test, uint64_t addr, int flags = 0) {
	VirtualQueryInfo info {};
	const int ret = Libs::LibKernel::Memory::KernelVirtualQuery(reinterpret_cast<const void*>(addr),
	                                                            flags, &info, sizeof(info));
	CheckOk(test, ret, "KernelVirtualQuery");
	return info;
}

int QueryResult(uint64_t addr, int flags = 0) {
	VirtualQueryInfo info {};
	return Libs::LibKernel::Memory::KernelVirtualQuery(reinterpret_cast<const void*>(addr), flags,
	                                                   &info, sizeof(info));
}

size_t AvailableFlexibleMemory(const char* test) {
	size_t    size = 0;
	const int ret  = Libs::LibKernel::Memory::KernelAvailableFlexibleMemorySize(&size);
	CheckOk(test, ret, "KernelAvailableFlexibleMemorySize");
	return size;
}

size_t ConfiguredFlexibleMemory(const char* test) {
	size_t size = 0;
	CheckOk(test, Libs::LibKernel::Memory::KernelConfiguredFlexibleMemorySize(&size),
	        "KernelConfiguredFlexibleMemorySize");
	return size;
}

uint64_t MapNamedFlexible(const char* test, uint64_t size, int prot, const char* name) {
	void*     addr = nullptr;
	const int ret =
	    Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(&addr, size, prot, 0, name);
	CheckOk(test, ret, "KernelMapNamedFlexibleMemory");
	Check(test, addr != nullptr, "flexible mapping returned null");
	return reinterpret_cast<uint64_t>(addr);
}

void ExpectRange(const char* test, const VirtualQueryInfo& info, uint64_t start, uint64_t end,
                 int prot, uint32_t flexible, uint32_t direct, uint32_t pooled, uint32_t committed,
                 const char* name = nullptr, uint64_t offset = 0) {
	Check(test, info.start == start, "unexpected range start");
	Check(test, info.end == end, "unexpected range end");
	Check(test, info.protection == prot, "unexpected range protection");
	Check(test, info.is_flexible == flexible, "unexpected flexible flag");
	Check(test, info.is_direct == direct, "unexpected direct flag");
	Check(test, info.is_pooled == pooled, "unexpected pooled flag");
	Check(test, info.is_committed == committed, "unexpected committed flag");
	Check(test, info.offset == offset, "unexpected range offset");
	if (name != nullptr) {
		Check(test,
		      std::strncmp(info.name, name, Libs::LibKernel::Memory::KERNEL_MAXIMUM_NAME_LENGTH) ==
		          0,
		      "unexpected range name");
	}
}

void ExpectUnmapped(const char* test, uint64_t addr) {
	const int ret = QueryResult(addr);
	if (ret != ErrorAccess) {
		char buffer[256] = {};
		std::snprintf(buffer, sizeof(buffer), "KernelVirtualQuery(unmapped) returned 0x%08" PRIx32,
		              static_cast<uint32_t>(ret));
		Fail(test, buffer);
	}
}

void TestProsperoArgumentAndInfoSizeContracts() {
	const char* test = "ProsperoArgumentAndInfoSizeContracts";
	void*       addr = nullptr;

	Check(test, sizeof(VirtualQueryInfo) == 72, "SceKernelVirtualQueryInfo layout drifted");
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(&addr, 0, SceKernelProtCpuRw,
	                                                                  0, "zero_len"),
	            "KernelMapNamedFlexibleMemory(len=0)");
	CheckFailed(test, QueryResult(0), "KernelVirtualQuery(null)");

	VirtualQueryInfo info {};
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelVirtualQuery(nullptr, 0, &info, sizeof(info) - 1),
	            "KernelVirtualQuery(short info)");
	CheckFailed(test, Libs::LibKernel::Memory::KernelVirtualQuery(nullptr, 2, &info, sizeof(info)),
	            "KernelVirtualQuery(unknown flags)");

	std::printf("[host]    %-48s ok\n", test);
}

void TestGuestAddressSpaceOwnsReservationsBeforeBacking() {
	const char* test = "GuestAddressSpaceOwnsReservationsBeforeBacking";
	void*       addr = nullptr;

	Check(test, Libs::LibKernel::Memory::TestGuestBackingOutsideAddressSpace(),
	      "boot-time shared backing alias overlaps an owned guest interval");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReserveVirtualRange(&addr, SceKernelPageSize, 0,
	                                                           SceKernelPageSize),
	        "KernelReserveVirtualRange");
	const auto base = reinterpret_cast<uint64_t>(addr);
	Check(test, Libs::LibKernel::Memory::TestGuestAddressRangeIsOwned(base, SceKernelPageSize),
	      "guest reservation was allocated outside the early owner");
	Check(test, Libs::LibKernel::Memory::TestPlaceholderRangeIsFree(base, SceKernelPageSize),
	      "semantic reservation replaced the owner's placeholder");
	Check(test,
	      Libs::LibKernel::Memory::ProtectGuestHostMemory(base, SceKernelPageSize,
	                                                      Common::VirtualMemory::Mode::NoAccess),
	      "owner rejected a sparse placeholder protection no-op");
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelPageSize), "KernelMunmap");
	Check(test, Libs::LibKernel::Memory::TestPlaceholderRangeIsFree(base, SceKernelPageSize),
	      "released semantic reservation escaped owner control");

	std::printf("[host]    %-48s ok\n", test);
}

void TestGuestAddressSpaceHasNoFixedFallback() {
	const char* test            = "GuestAddressSpaceHasNoFixedFallback";
	const auto  unowned_address = reinterpret_cast<void*>(0x10000);
	void*       addr            = unowned_address;

	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelReserveVirtualRange(
	                &addr, SceKernelPageSize, SceKernelMapFixed | SceKernelMapNoOverwrite,
	                SceKernelPageSize),
	            "KernelReserveVirtualRange(unowned fixed address)");
	Check(test, reinterpret_cast<uint64_t>(addr) == 0x10000,
	      "failed fixed reservation unexpectedly moved");

	addr = unowned_address;
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(
	                &addr, SceKernelPageSize, SceKernelProtCpuRw,
	                SceKernelMapFixed | SceKernelMapNoOverwrite, "unowned_flexible"),
	            "KernelMapNamedFlexibleMemory(unowned fixed address)");

	int64_t phys_addr = -1;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(), SceKernelPageSize,
	            SceKernelPageSize, SceKernelMtypeC, &phys_addr),
	        "KernelAllocateDirectMemory");
	addr = unowned_address;
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	                &addr, SceKernelPageSize, SceKernelProtCpuRw,
	                SceKernelMapFixed | SceKernelMapNoOverwrite, phys_addr, SceKernelPageSize,
	                "unowned_direct"),
	            "KernelMapNamedDirectMemory(unowned fixed address)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelCheckedReleaseDirectMemory(phys_addr, SceKernelPageSize),
	        "KernelCheckedReleaseDirectMemory");

	std::printf("[host]    %-48s ok\n", test);
}

void TestGuestFreeRangeSearchDoesNotUnderflow() {
	const char* test = "GuestFreeRangeSearchDoesNotUnderflow";

	Check(test, Libs::LibKernel::Memory::TestGuestFreeRangeBounds(),
	      "free-range containment accepted a candidate beyond the range end");

	std::printf("[host]    %-48s ok\n", test);
}

void TestFlexibleMemoryCapacityIsBootFixed() {
	const char* test       = "FlexibleMemoryCapacityIsBootFixed";
	const auto  configured = ConfiguredFlexibleMemory(test);
	const auto  baseline   = AvailableFlexibleMemory(test);
	const auto  backing    = Libs::LibKernel::Memory::TestGuestBackingSize();

	Check(test, configured == TestFlexibleMemorySize,
	      "boot flexible pool did not use the param.json value");
	Check(test, configured == baseline, "boot flexible pool did not start at configured capacity");
	Check(test, backing == SceKernelTotalPhysicalSize,
	      "boot backing is not the single 13.5 GiB physical file");
	Check(test, backing == Libs::LibKernel::Memory::KernelGetDirectMemorySize() + configured,
	      "direct and flexible regions do not partition the boot backing");

	const auto address =
	    MapNamedFlexible(test, SceKernelPageSize, SceKernelProtCpuRw, "boot_fixed_flexible");
	Check(test, ConfiguredFlexibleMemory(test) == configured,
	      "configured flexible capacity changed after allocation");
	Check(test, Libs::LibKernel::Memory::TestGuestBackingSize() == backing,
	      "shared backing size changed after allocation");
	Check(test, AvailableFlexibleMemory(test) == baseline - SceKernelPageSize,
	      "flexible allocation did not consume the boot-time pool");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(address, SceKernelPageSize),
	        "KernelMunmap");
	Check(test, ConfiguredFlexibleMemory(test) == configured,
	      "configured flexible capacity changed after release");
	Check(test, AvailableFlexibleMemory(test) == baseline,
	      "flexible release did not restore the boot-time pool");

	std::printf("[host]    %-48s ok\n", test);
}

void TestFlexibleMemoryUsesSharedBacking() {
	const char* test     = "FlexibleMemoryUsesSharedBacking";
	const auto  baseline = AvailableFlexibleMemory(test);
	void*       address  = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(
	            &address, SceKernelPageSize * 2, SceKernelProtCpuRw, 0, "shared_flexible"),
	        "KernelMapNamedFlexibleMemory");
	const auto base = reinterpret_cast<uint64_t>(address);
	Check(test, Libs::LibKernel::Memory::TestGuestAddressRangeIsOwned(base, SceKernelPageSize * 2),
	      "flexible mapping escaped the guest owner");

	constexpr uint64_t first_value     = 0x464c45584241434bull; // "FLEXBACK"
	constexpr uint64_t second_value    = 0x534841524544464cull; // "SHAREDFL"
	*reinterpret_cast<uint64_t*>(base) = first_value;
	uint64_t value                     = 0;
	Check(test, Libs::LibKernel::Memory::TryReadBacking(base, &value, sizeof(value)),
	      "TryReadBacking did not resolve flexible memory");
	Check(test, value == first_value, "backing did not observe a flexible-memory CPU write");
	Check(test,
	      Libs::LibKernel::Memory::TryWriteBacking(base + SceKernelPageSize, &second_value,
	                                               sizeof(second_value)),
	      "TryWriteBacking did not resolve flexible memory");
	Check(test, *reinterpret_cast<uint64_t*>(base + SceKernelPageSize) == second_value,
	      "flexible-memory view did not observe a backing write");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelPageSize * 2),
	        "KernelMunmap");
	Check(test, AvailableFlexibleMemory(test) == baseline,
	      "flexible backing offsets were not returned to the boot-time pool");
	Check(test, !Libs::LibKernel::Memory::TryReadBacking(base, &value, sizeof(value)),
	      "unmapped flexible memory remained registered in the backing owner");

	std::printf("[host]    %-48s ok\n", test);
}

void TestFlexibleDmemCompatAndAlignmentFlags() {
	const char* test     = "FlexibleDmemCompatAndAlignmentFlags";
	const auto  baseline = AvailableFlexibleMemory(test);
	void*       address  = nullptr;

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(
	            &address, SceKernelPageSize, SceKernelProtCpuRw,
	            SceKernelMapDmemCompat | SceKernelMapAligned64Kb, "dmem_compat"),
	        "KernelMapNamedFlexibleMemory(DMEM_COMPAT|ALIGNED_64KB)");
	const auto base = reinterpret_cast<uint64_t>(address);
	Check(test, (base & (0x10000 - 1u)) == 0, "SDK alignment flag was not honored");
	const auto info = Query(test, base);
	Check(test, info.is_flexible == 1 && info.is_stack == 0,
	      "SCE_KERNEL_MAP_DMEM_COMPAT was misclassified as MAP_STACK");
	Check(test, AvailableFlexibleMemory(test) + SceKernelPageSize == baseline,
	      "DMEM_COMPAT mapping did not consume boot-time flexible backing");

	void* stack_start = reinterpret_cast<void*>(UINT64_MAX);
	void* stack_end   = reinterpret_cast<void*>(UINT64_MAX);
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelIsStack(reinterpret_cast<void*>(base), &stack_start,
	                                               &stack_end),
	        "KernelIsStack");
	Check(test, stack_start == nullptr && stack_end == nullptr,
	      "DMEM_COMPAT flexible mapping was reported as a stack");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelPageSize), "KernelMunmap");
	Check(test, AvailableFlexibleMemory(test) == baseline,
	      "DMEM_COMPAT cleanup did not restore flexible capacity");

	void* opaque = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(
	            &opaque, SceKernelPageSize, SceKernelProtCpuRw, 0x8000, "opaque_runtime_flag"),
	        "KernelMapNamedFlexibleMemory(opaque runtime flag)");
	Check(test, AvailableFlexibleMemory(test) + SceKernelPageSize == baseline,
	      "opaque runtime flag mapping did not consume boot-time flexible backing");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(reinterpret_cast<uint64_t>(opaque),
	                                              SceKernelPageSize),
	        "KernelMunmap(opaque runtime flag)");
	Check(test, AvailableFlexibleMemory(test) == baseline,
	      "opaque runtime flag cleanup did not restore flexible capacity");

	void* invalid_flag = nullptr;
	CheckFailed(
	    test,
	    Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(
	        &invalid_flag, SceKernelPageSize, SceKernelProtCpuRw, 0x10000, "unsupported_flag"),
	    "KernelMapNamedFlexibleMemory(unsupported flag)");

	void* invalid_alignment = nullptr;
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(
	                &invalid_alignment, SceKernelPageSize, SceKernelProtCpuRw, 13 << 24,
	                "invalid_alignment"),
	            "KernelMapNamedFlexibleMemory(invalid alignment)");

	std::printf("[host]    %-48s ok\n", test);
}

void TestFlexibleNoCoalescePreservesBoundaries() {
	const char* test     = "FlexibleNoCoalescePreservesBoundaries";
	const auto  baseline = AvailableFlexibleMemory(test);
	void*       reserve  = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReserveVirtualRange(&reserve, SceKernelPageSize * 2, 0,
	                                                           SceKernelPageSize),
	        "KernelReserveVirtualRange");
	const auto base = reinterpret_cast<uint64_t>(reserve);

	void* left = reinterpret_cast<void*>(base);
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(
	            &left, SceKernelPageSize, SceKernelProtCpuRw,
	            SceKernelMapFixed | SceKernelMapNoCoalesce, "no_coalesce"),
	        "KernelMapNamedFlexibleMemory(left)");
	void* right = reinterpret_cast<void*>(base + SceKernelPageSize);
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(
	            &right, SceKernelPageSize, SceKernelProtCpuRw,
	            SceKernelMapFixed | SceKernelMapNoCoalesce, "no_coalesce"),
	        "KernelMapNamedFlexibleMemory(right)");

	ExpectRange(test, Query(test, base), base, base + SceKernelPageSize, SceKernelProtCpuRw, 1, 0,
	            0, 1, "no_coalesce");
	ExpectRange(test, Query(test, base + SceKernelPageSize), base + SceKernelPageSize,
	            base + SceKernelPageSize * 2, SceKernelProtCpuRw, 1, 0, 0, 1, "no_coalesce");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelPageSize * 2),
	        "KernelMunmap");
	Check(test, AvailableFlexibleMemory(test) == baseline,
	      "NO_COALESCE cleanup did not restore flexible capacity");

	std::printf("[host]    %-48s ok\n", test);
}

void TestFlexibleMemoryReuseIsZeroFilled() {
	const char* test     = "FlexibleMemoryReuseIsZeroFilled";
	const auto  baseline = AvailableFlexibleMemory(test);
	const auto  first =
	    MapNamedFlexible(test, SceKernelPageSize, SceKernelProtCpuRw, "flexible_zero_source");
	std::memset(reinterpret_cast<void*>(first), 0xa5, SceKernelPageSize);
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(first, SceKernelPageSize),
	        "KernelMunmap(source)");

	const auto reused =
	    MapNamedFlexible(test, SceKernelPageSize, SceKernelProtCpuRw, "flexible_zero_reuse");
	const auto* bytes = reinterpret_cast<const uint8_t*>(reused);
	Check(test,
	      std::all_of(bytes, bytes + SceKernelPageSize, [](uint8_t value) { return value == 0; }),
	      "reused flexible backing exposed stale bytes");
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(reused, SceKernelPageSize),
	        "KernelMunmap(reuse)");
	Check(test, AvailableFlexibleMemory(test) == baseline,
	      "zero-fill test leaked flexible backing capacity");

	std::printf("[host]    %-48s ok\n", test);
}

void TestPthreadAttrDetachStateValidation() {
	const char* test = "PthreadAttrDetachStateValidation";

	Libs::LibKernel::PthreadAttr attr = nullptr;
	CheckOk(test, Libs::LibKernel::PthreadAttrInit(&attr), "PthreadAttrInit");

	int detach_state = -1;
	CheckOk(test, Libs::LibKernel::PthreadAttrGetdetachstate(&attr, &detach_state),
	        "PthreadAttrGetdetachstate(default)");
	Check(test, detach_state == 0, "default attribute is not joinable");

	Check(test,
	      Libs::LibKernel::PthreadAttrSetdetachstate(&attr, 2) ==
	          Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "detach state above the valid range did not return EINVAL");
	CheckOk(test, Libs::LibKernel::PthreadAttrGetdetachstate(&attr, &detach_state),
	        "PthreadAttrGetdetachstate(after upper invalid)");
	Check(test, detach_state == 0, "upper invalid detach state changed the attribute");

	CheckOk(test, Libs::LibKernel::PthreadAttrSetdetachstate(&attr, 1),
	        "PthreadAttrSetdetachstate(detached)");
	Check(test,
	      Libs::LibKernel::PthreadAttrSetdetachstate(&attr, -1) ==
	          Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "negative detach state did not return EINVAL");
	CheckOk(test, Libs::LibKernel::PthreadAttrGetdetachstate(&attr, &detach_state),
	        "PthreadAttrGetdetachstate(after negative invalid)");
	Check(test, detach_state == 1, "negative invalid detach state changed the attribute");

	CheckOk(test, Libs::LibKernel::PthreadAttrDestroy(&attr), "PthreadAttrDestroy");
	std::printf("[host]    %-48s ok\n", test);
}

void TestPthreadAttrInheritSchedValidation() {
	const char* test = "PthreadAttrInheritSchedValidation";

	Libs::LibKernel::PthreadAttr attr = nullptr;
	CheckOk(test, Libs::LibKernel::PthreadAttrInit(&attr), "PthreadAttrInit");

	int inherit_sched = -1;
	CheckOk(test, Libs::LibKernel::PthreadAttrGetinheritsched(&attr, &inherit_sched),
	        "PthreadAttrGetinheritsched(default)");
	Check(test, inherit_sched == 4, "default attribute is not inheritance mode");

	Check(test,
	      Libs::LibKernel::PthreadAttrSetinheritsched(&attr, 1) ==
	          Libs::LibKernel::KERNEL_ERROR_ENOTSUP,
	      "invalid inheritance mode did not return ENOTSUP");
	CheckOk(test, Libs::LibKernel::PthreadAttrGetinheritsched(&attr, &inherit_sched),
	        "PthreadAttrGetinheritsched(after invalid)");
	Check(test, inherit_sched == 4, "invalid inheritance mode changed the attribute");

	CheckOk(test, Libs::LibKernel::PthreadAttrSetinheritsched(&attr, 0),
	        "PthreadAttrSetinheritsched(explicit)");
	Check(test,
	      Libs::LibKernel::PthreadAttrSetinheritsched(&attr, -1) ==
	          Libs::LibKernel::KERNEL_ERROR_ENOTSUP,
	      "negative inheritance mode did not return ENOTSUP");
	CheckOk(test, Libs::LibKernel::PthreadAttrGetinheritsched(&attr, &inherit_sched),
	        "PthreadAttrGetinheritsched(after negative invalid)");
	Check(test, inherit_sched == 0, "negative inheritance mode changed the attribute");

	CheckOk(test, Libs::LibKernel::PthreadAttrDestroy(&attr), "PthreadAttrDestroy");
	std::printf("[host]    %-48s ok\n", test);
}

void TestPthreadAttrSchedPolicyValidation() {
	const char* test = "PthreadAttrSchedPolicyValidation";

	Libs::LibKernel::PthreadAttr attr = nullptr;
	CheckOk(test, Libs::LibKernel::PthreadAttrInit(&attr), "PthreadAttrInit");

	int policy = 0;
	CheckOk(test, Libs::LibKernel::PthreadAttrGetschedpolicy(&attr, &policy),
	        "PthreadAttrGetschedpolicy(default)");
	Check(test, policy == 1, "default scheduling policy is not FIFO");

	Check(test,
	      Libs::LibKernel::PthreadAttrSetschedpolicy(&attr, 2) ==
	          Libs::LibKernel::KERNEL_ERROR_ENOTSUP,
	      "invalid scheduling policy did not return ENOTSUP");
	CheckOk(test, Libs::LibKernel::PthreadAttrGetschedpolicy(&attr, &policy),
	        "PthreadAttrGetschedpolicy(after invalid)");
	Check(test, policy == 1, "invalid scheduling policy changed the attribute");

	Libs::LibKernel::KernelSchedParam param {.sched_priority = 300};
	CheckOk(test, Libs::LibKernel::PthreadAttrSetschedparam(&attr, &param),
	        "PthreadAttrSetschedparam(non-default)");
	CheckOk(test, Libs::LibKernel::PthreadAttrSetschedpolicy(&attr, 3),
	        "PthreadAttrSetschedpolicy(round-robin)");
	CheckOk(test, Libs::LibKernel::PthreadAttrGetschedpolicy(&attr, &policy),
	        "PthreadAttrGetschedpolicy(round-robin)");
	Check(test, policy == 3, "round-robin scheduling policy was not retained");

	param.sched_priority = 0;
	CheckOk(test, Libs::LibKernel::PthreadAttrGetschedparam(&attr, &param),
	        "PthreadAttrGetschedparam(after policy change)");
	Check(test, param.sched_priority == 700,
	      "changing scheduling policy did not restore the default priority");

	Check(test,
	      Libs::LibKernel::PthreadAttrSetschedpolicy(&attr, 4) ==
	          Libs::LibKernel::KERNEL_ERROR_ENOTSUP,
	      "upper invalid scheduling policy did not return ENOTSUP");
	CheckOk(test, Libs::LibKernel::PthreadAttrGetschedpolicy(&attr, &policy),
	        "PthreadAttrGetschedpolicy(after upper invalid)");
	Check(test, policy == 3, "upper invalid scheduling policy changed the attribute");

	CheckOk(test, Libs::LibKernel::PthreadAttrDestroy(&attr), "PthreadAttrDestroy");
	std::printf("[host]    %-48s ok\n", test);
}

void TestPthreadAttrSchedPriorityValidation() {
	const char* test = "PthreadAttrSchedPriorityValidation";

	Libs::LibKernel::PthreadAttr attr = nullptr;
	CheckOk(test, Libs::LibKernel::PthreadAttrInit(&attr), "PthreadAttrInit");

	Libs::LibKernel::KernelSchedParam param {.sched_priority = 256};
	CheckOk(test, Libs::LibKernel::PthreadAttrSetschedparam(&attr, &param),
	        "PthreadAttrSetschedparam(highest)");
	Libs::LibKernel::KernelSchedParam invalid_param {.sched_priority = 255};
	Check(test,
	      Libs::LibKernel::PthreadAttrSetschedparam(&attr, &invalid_param) ==
	          Libs::LibKernel::KERNEL_ERROR_ENOTSUP,
	      "priority above the supported range did not return ENOTSUP");
	param.sched_priority = 0;
	CheckOk(test, Libs::LibKernel::PthreadAttrGetschedparam(&attr, &param),
	        "PthreadAttrGetschedparam(after upper invalid)");
	Check(test, param.sched_priority == 256, "upper invalid priority changed the attribute");

	param.sched_priority = 767;
	CheckOk(test, Libs::LibKernel::PthreadAttrSetschedparam(&attr, &param),
	        "PthreadAttrSetschedparam(lowest)");
	invalid_param.sched_priority = 768;
	Check(test,
	      Libs::LibKernel::PthreadAttrSetschedparam(&attr, &invalid_param) ==
	          Libs::LibKernel::KERNEL_ERROR_ENOTSUP,
	      "priority below the supported range did not return ENOTSUP");
	param.sched_priority = 0;
	CheckOk(test, Libs::LibKernel::PthreadAttrGetschedparam(&attr, &param),
	        "PthreadAttrGetschedparam(after lower invalid)");
	Check(test, param.sched_priority == 767, "lower invalid priority changed the attribute");

	param.sched_priority = 300;
	CheckOk(test, Libs::LibKernel::PthreadAttrSetschedparam(&attr, &param),
	        "PthreadAttrSetschedparam(exact high priority)");
	param.sched_priority = 0;
	CheckOk(test, Libs::LibKernel::PthreadAttrGetschedparam(&attr, &param),
	        "PthreadAttrGetschedparam(exact high priority)");
	Check(test, param.sched_priority == 300,
	      "scheduling attribute lost the exact high guest priority");

	param.sched_priority = 600;
	CheckOk(test, Libs::LibKernel::PthreadAttrSetschedparam(&attr, &param),
	        "PthreadAttrSetschedparam(exact normal priority)");
	param.sched_priority = 0;
	CheckOk(test, Libs::LibKernel::PthreadAttrGetschedparam(&attr, &param),
	        "PthreadAttrGetschedparam(exact normal priority)");
	Check(test, param.sched_priority == 600,
	      "scheduling attribute lost the exact normal guest priority");

	CheckOk(test, Libs::LibKernel::PthreadAttrDestroy(&attr), "PthreadAttrDestroy");
	std::printf("[host]    %-48s ok\n", test);
}

void TestPthreadAttrSoloSchedValidation() {
	const char* test = "PthreadAttrSoloSchedValidation";

	Libs::LibKernel::PthreadAttr attr = nullptr;
	CheckOk(test, Libs::LibKernel::PthreadAttrInit(&attr), "PthreadAttrInit");

	int solosched = -1;
	CheckOk(test, Libs::LibKernel::PthreadAttrGetsolosched(&attr, &solosched),
	        "PthreadAttrGetsolosched(default)");
	Check(test, solosched == 0, "default attribute unexpectedly enables solo scheduling");

	Check(test,
	      Libs::LibKernel::PthreadAttrSetsolosched(&attr, 1) ==
	          Libs::LibKernel::KERNEL_ERROR_ENOTSUP,
	      "invalid solo scheduling value did not return ENOTSUP");
	CheckOk(test, Libs::LibKernel::PthreadAttrGetsolosched(&attr, &solosched),
	        "PthreadAttrGetsolosched(after invalid)");
	Check(test, solosched == 0, "invalid solo scheduling value changed the attribute");

	CheckOk(test, Libs::LibKernel::PthreadAttrSetsolosched(&attr, 0x10),
	        "PthreadAttrSetsolosched(solo)");
	Check(test,
	      Libs::LibKernel::PthreadAttrSetsolosched(&attr, -1) ==
	          Libs::LibKernel::KERNEL_ERROR_ENOTSUP,
	      "negative solo scheduling value did not return ENOTSUP");
	CheckOk(test, Libs::LibKernel::PthreadAttrGetsolosched(&attr, &solosched),
	        "PthreadAttrGetsolosched(after negative invalid)");
	Check(test, solosched == 0x10, "negative solo scheduling value changed the attribute");

	CheckOk(test, Libs::LibKernel::PthreadAttrSetsolosched(&attr, 0),
	        "PthreadAttrSetsolosched(unsolo)");
	CheckOk(test, Libs::LibKernel::PthreadAttrDestroy(&attr), "PthreadAttrDestroy");
	std::printf("[host]    %-48s ok\n", test);
}

void TestPthreadMutexAttrProtocolRoundTrip() {
	const char* test = "PthreadMutexAttrProtocolRoundTrip";
	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	const auto* record = symbols.FindByNid("GoTmFeui+hQ", Loader::SymbolType::Func);
	Check(test, record != nullptr, "scePthreadMutexattrGetprotocol NID is not registered");
	Check(test,
	      record->vaddr ==
	          reinterpret_cast<uint64_t>(&Libs::LibKernel::PthreadMutexattrGetprotocol),
	      "scePthreadMutexattrGetprotocol NID resolves to the wrong ABI entry");
	const auto* posix_record = symbols.FindByNid("yDaWxUE50s0", Loader::SymbolType::Func);
	Check(test, posix_record != nullptr, "pthread_mutexattr_getprotocol NID is not registered");
	Check(test,
	      posix_record->vaddr ==
	          reinterpret_cast<uint64_t>(&Libs::Posix::pthread_mutexattr_getprotocol),
	      "pthread_mutexattr_getprotocol NID resolves to the wrong ABI entry");

	Libs::LibKernel::PthreadMutexattr attr = nullptr;
	CheckOk(test, Libs::LibKernel::PthreadMutexattrInit(&attr), "PthreadMutexattrInit");

	int protocol = -1;
	CheckOk(test, Libs::LibKernel::PthreadMutexattrGetprotocol(&attr, &protocol),
	        "PthreadMutexattrGetprotocol(default)");
	Check(test, protocol == 0, "default mutex protocol is not PRIO_NONE");

	CheckOk(test, Libs::LibKernel::PthreadMutexattrSetprotocol(&attr, 1),
	        "PthreadMutexattrSetprotocol(inherit)");
	CheckOk(test, Libs::LibKernel::PthreadMutexattrGetprotocol(&attr, &protocol),
	        "PthreadMutexattrGetprotocol(inherit)");
	Check(test, protocol == 1, "priority-inheritance protocol was not retained");

	Check(test,
	      Libs::LibKernel::PthreadMutexattrSetprotocol(&attr, -1) ==
	          Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "negative mutex protocol did not return EINVAL");
	CheckOk(test, Libs::LibKernel::PthreadMutexattrGetprotocol(&attr, &protocol),
	        "PthreadMutexattrGetprotocol(after lower invalid)");
	Check(test, protocol == 1, "negative mutex protocol changed the attribute");

	CheckOk(test, Libs::LibKernel::PthreadMutexattrSetprotocol(&attr, 2),
	        "PthreadMutexattrSetprotocol(protect)");
	Check(test,
	      Libs::LibKernel::PthreadMutexattrSetprotocol(&attr, 3) ==
	          Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "mutex protocol above the valid range did not return EINVAL");
	CheckOk(test, Libs::LibKernel::PthreadMutexattrGetprotocol(&attr, &protocol),
	        "PthreadMutexattrGetprotocol(after upper invalid)");
	Check(test, protocol == 2, "upper invalid mutex protocol changed the attribute");

	Check(test,
	      Libs::LibKernel::PthreadMutexattrGetprotocol(nullptr, &protocol) ==
	          Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "null mutex attribute did not return EINVAL");
	CheckOk(test, Libs::LibKernel::PthreadMutexattrDestroy(&attr), "PthreadMutexattrDestroy");
	std::printf("[host]    %-48s ok\n", test);
}

void TestPthreadRwlockAttrTypeValidation() {
	const char* test = "PthreadRwlockAttrTypeValidation";

	Libs::LibKernel::PthreadRwlockattr attr = nullptr;
	CheckOk(test, Libs::LibKernel::PthreadRwlockattrInit(&attr), "PthreadRwlockattrInit");

	int type = 0;
	CheckOk(test, Libs::LibKernel::PthreadRwlockattrGettype(&attr, &type),
	        "PthreadRwlockattrGettype(default)");
	Check(test, type == 1, "default reader/writer lock type is not writer-priority");

	Check(test,
	      Libs::LibKernel::PthreadRwlockattrSettype(&attr, 0) ==
	          Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "type below the valid range did not return EINVAL");
	CheckOk(test, Libs::LibKernel::PthreadRwlockattrGettype(&attr, &type),
	        "PthreadRwlockattrGettype(after lower invalid)");
	Check(test, type == 1, "invalid lower type changed the attribute");

	CheckOk(test, Libs::LibKernel::PthreadRwlockattrSettype(&attr, 2),
	        "PthreadRwlockattrSettype(reader-priority)");
	Check(test,
	      Libs::LibKernel::PthreadRwlockattrSettype(&attr, 3) ==
	          Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "type above the valid range did not return EINVAL");
	CheckOk(test, Libs::LibKernel::PthreadRwlockattrGettype(&attr, &type),
	        "PthreadRwlockattrGettype(after upper invalid)");
	Check(test, type == 2, "invalid upper type changed the attribute");

	CheckOk(test, Libs::LibKernel::PthreadRwlockattrDestroy(&attr),
	        "PthreadRwlockattrDestroy");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelLseekInvalidWhence() {
	const char* test = "KernelLseekInvalidWhence";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_lseek_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_file = root / "seek.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directory(root, ec) && !ec,
	      "failed to create temporary directory");

	constexpr char contents[] = "seek-contract";
	Common::File   fixture;
	Check(test, fixture.Create(host_file), "failed to create temporary file");
	uint32_t bytes_written = 0;
	fixture.Write(contents, sizeof(contents) - 1, &bytes_written);
	fixture.Close();
	Check(test, bytes_written == sizeof(contents) - 1, "failed to populate temporary file");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/lseek-contract");
	const int fd = KernelOpen("/lseek-contract/seek.bin", 0, 0);
	Check(test, fd >= 3, "failed to open mounted temporary file");
	Check(test, KernelLseek(fd, 2, 0) == 2, "valid SEEK_SET failed");
	Check(test, KernelLseek(fd, 7, 3) == Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "invalid whence did not return EINVAL");
	Check(test, KernelLseek(fd, 0, 1) == 2, "invalid whence changed the file position");
	CheckOk(test, KernelClose(fd), "KernelClose");
	Umount("/lseek-contract");

	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelLseekOverflowContract() {
	const char* test = "KernelLseekOverflowContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_lseek_overflow_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_file = root / "seek.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directory(root, ec) && !ec,
	      "failed to create temporary directory");

	constexpr char contents[] = "seek-overflow";
	Common::File   fixture;
	Check(test, fixture.Create(host_file), "failed to create temporary file");
	uint32_t bytes_written = 0;
	fixture.Write(contents, sizeof(contents) - 1, &bytes_written);
	fixture.Close();
	Check(test, bytes_written == sizeof(contents) - 1, "failed to populate temporary file");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/lseek-overflow");
	const int fd = KernelOpen("/lseek-overflow/seek.bin", 0, 0);
	Check(test, fd >= 3, "failed to open mounted temporary file");
	Check(test, KernelLseek(fd, 2, 0) == 2, "valid SEEK_SET failed");
	Check(test,
	      KernelLseek(fd, INT64_MAX, 1) == Libs::LibKernel::KERNEL_ERROR_EOVERFLOW,
	      "overflowing SEEK_CUR did not return EOVERFLOW");
	Check(test, KernelLseek(fd, 0, 1) == 2, "overflowing SEEK_CUR changed the position");
	Check(test,
	      KernelLseek(fd, INT64_MAX, 2) == Libs::LibKernel::KERNEL_ERROR_EOVERFLOW,
	      "overflowing SEEK_END did not return EOVERFLOW");
	Check(test, KernelLseek(fd, 0, 1) == 2, "overflowing SEEK_END changed the position");
	CheckOk(test, KernelClose(fd), "KernelClose");
	Umount("/lseek-overflow");

	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelLseekInvalidDescriptorContract() {
	const char* test = "KernelLseekInvalidDescriptorContract";

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("oib76F-12fk", Loader::SymbolType::Func) != nullptr,
	      "sceKernelLseek NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Check(test, KernelLseek(-1, 0, 0) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "negative file descriptor did not return EBADF");
	Check(test, KernelLseek(0, 0, 0) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "reserved file descriptor did not return EBADF");
	Check(test, KernelLseek(0x7fffffff, 0, 0) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "unallocated file descriptor did not return EBADF");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelCloseDescriptorErrorContract() {
	const char* test = "KernelCloseDescriptorErrorContract";

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("UK2Tl2DWUns", Loader::SymbolType::Func) != nullptr,
	      "sceKernelClose NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Check(test, KernelClose(-1) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "negative inactive descriptor did not return EBADF");
	Check(test, KernelClose(0) == Libs::LibKernel::KERNEL_ERROR_EPERM,
	      "standard input close did not retain EPERM");
	Check(test, KernelClose(1) == Libs::LibKernel::KERNEL_ERROR_EPERM,
	      "standard output close did not retain EPERM");
	Check(test, KernelClose(2) == Libs::LibKernel::KERNEL_ERROR_EPERM,
	      "standard error close did not retain EPERM");
	Check(test, KernelClose(0x7fffffff) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "unallocated descriptor did not return EBADF");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelFileSynchronization() {
	const char* test = "KernelFileSynchronization";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_fsync_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_file = root / "durable.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directory(root, ec) && !ec,
	      "failed to create temporary directory");

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("fTx66l5iWIA", Loader::SymbolType::Func) != nullptr,
	      "sceKernelFsync NID is not registered");
	Check(test, symbols.FindByNid("30Rh4ixbKy4", Loader::SymbolType::Func) != nullptr,
	      "sceKernelFdatasync NID is not registered");
	Check(test, symbols.FindByNid("juWbTNM+8hw", Loader::SymbolType::Func) != nullptr,
	      "POSIX fsync NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/fsync-contract");
	constexpr int create_read_write_truncate = 0x2 | 0x200 | 0x400;
	const int fd = KernelOpen("/fsync-contract/durable.bin", create_read_write_truncate, 0600);
	Check(test, fd >= 3, "failed to create mounted synchronization file");

	constexpr char first[] = "durable";
	Check(test, KernelWrite(fd, first, sizeof(first) - 1) == sizeof(first) - 1,
	      "initial synchronized write failed");
	CheckOk(test, KernelFsync(fd), "KernelFsync");

	Common::File observer(host_file, Common::File::Mode::Read);
	Check(test, !observer.IsInvalid(), "synchronized file was not visible to another handle");
	char     observed[16] = {};
	uint32_t observed_size = 0;
	observer.Read(observed, sizeof(first) - 1, &observed_size);
	observer.Close();
	Check(test, observed_size == sizeof(first) - 1 &&
	                std::memcmp(observed, first, sizeof(first) - 1) == 0,
	      "sceKernelFsync did not publish written file content");

	constexpr char suffix[] = "-data";
	Check(test, KernelWrite(fd, suffix, sizeof(suffix) - 1) == sizeof(suffix) - 1,
	      "fdatasync write failed");
	CheckOk(test, KernelFdatasync(fd), "KernelFdatasync");
	KernelSync();
	CheckOk(test, KernelClose(fd), "KernelClose");
	Check(test, KernelFsync(fd) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "closed descriptor did not return EBADF from fsync");
	Check(test, KernelFdatasync(-1) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "negative descriptor did not return EBADF from fdatasync");
	Check(test, std::filesystem::file_size(host_file, ec) ==
	                (sizeof(first) - 1) + (sizeof(suffix) - 1) &&
	                !ec,
	      "synchronized file has the wrong durable size");

	Umount("/fsync-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelFtruncateContract() {
	const char* test = "KernelFtruncateContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_ftruncate_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_file = root / "resize.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directory(root, ec) && !ec,
	      "failed to create temporary directory");

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("VW3TVZiM4-E", Loader::SymbolType::Func) != nullptr,
	      "sceKernelFtruncate NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/ftruncate-contract");
	constexpr int create_read_write_truncate = 0x2 | 0x200 | 0x400;
	const int writable_fd =
	    KernelOpen("/ftruncate-contract/resize.bin", create_read_write_truncate, 0600);
	Check(test, writable_fd >= 3, "failed to create writable fixture");

	constexpr std::array<char, 8> contents {'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h'};
	Check(test, KernelWrite(writable_fd, contents.data(), contents.size()) == contents.size(),
	      "failed to populate writable fixture");
	Check(test, KernelLseek(writable_fd, 3, 0) == 3, "failed to set preserved position");
	CheckOk(test, KernelFtruncate(writable_fd, 5), "KernelFtruncate(shrink)");
	Check(test, KernelLseek(writable_fd, 0, 1) == 3,
	      "shrinking changed the file position");
	Check(test, std::filesystem::file_size(host_file, ec) == 5 && !ec,
	      "shrinking produced the wrong file size");

	CheckOk(test, KernelFtruncate(writable_fd, 9), "KernelFtruncate(expand)");
	Check(test, KernelLseek(writable_fd, 0, 1) == 3,
	      "expanding changed the file position");
	std::array<char, 9> expanded {};
	Common::File observer(host_file, Common::File::Mode::Read);
	uint32_t     observed_size = 0;
	observer.Read(expanded.data(), expanded.size(), &observed_size);
	observer.Close();
	Check(test, observed_size == expanded.size(), "failed to read expanded file");
	Check(test, std::memcmp(expanded.data(), contents.data(), 5) == 0,
	      "truncation changed the retained prefix");
	Check(test, expanded[5] == 0 && expanded[6] == 0 && expanded[7] == 0 && expanded[8] == 0,
	      "expanded extent was not zero-filled");
	Check(test, KernelFtruncate(writable_fd, -1) == Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "negative length did not return EINVAL");
	Check(test, KernelLseek(writable_fd, 0, 1) == 3,
	      "rejected negative length changed the position");
	CheckOk(test, KernelClose(writable_fd), "KernelClose(writable)");

	const int read_fd = KernelOpen("/ftruncate-contract/resize.bin", 0, 0);
	Check(test, read_fd >= 3, "failed to open read-only fixture");
	Check(test, KernelFtruncate(read_fd, 2) == Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "read-only descriptor did not return EINVAL");
	Check(test, std::filesystem::file_size(host_file, ec) == 9 && !ec,
	      "rejected read-only truncation changed the file");
	CheckOk(test, KernelClose(read_fd), "KernelClose(read-only)");
	Check(test, KernelFtruncate(read_fd, 2) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "closed descriptor did not return EBADF");
	Check(test, KernelFtruncate(-1, 2) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "negative descriptor did not return EBADF");

	Umount("/ftruncate-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelTruncateContract() {
	const char* test = "KernelTruncateContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_truncate_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_file = root / "resize.bin";
	const auto host_dir  = root / "directory";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(host_dir, ec) && !ec,
	      "failed to create temporary fixture");
	constexpr std::array<char, 8> contents {'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h'};
	Common::File fixture;
	Check(test, fixture.Create(host_file), "failed to create resize fixture");
	uint32_t written = 0;
	fixture.Write(contents.data(), contents.size(), &written);
	fixture.Close();
	Check(test, written == contents.size(), "failed to populate resize fixture");

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("WlyEA-sLDf0", Loader::SymbolType::Func) != nullptr,
	      "sceKernelTruncate NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/truncate-contract");
	const int fd = KernelOpen("/truncate-contract/resize.bin", 2, 0);
	Check(test, fd >= 3, "failed to open resize fixture");
	Check(test, KernelLseek(fd, 3, 0) == 3, "failed to set preserved descriptor position");
	CheckOk(test, KernelTruncate("/truncate-contract/resize.bin", 5),
	        "KernelTruncate(shrink)");
	Check(test, KernelLseek(fd, 0, 1) == 3, "shrinking changed an open descriptor position");
	Check(test, std::filesystem::file_size(host_file, ec) == 5 && !ec,
	      "path shrink produced the wrong file size");

	CheckOk(test, KernelTruncate("/truncate-contract/resize.bin", 9),
	        "KernelTruncate(expand)");
	Check(test, KernelLseek(fd, 0, 1) == 3, "expanding changed an open descriptor position");
	std::array<char, 9> expanded {};
	Common::File observer(host_file, Common::File::Mode::Read);
	uint32_t     observed_size = 0;
	observer.Read(expanded.data(), expanded.size(), &observed_size);
	observer.Close();
	Check(test, observed_size == expanded.size(), "failed to read path-expanded file");
	Check(test, std::memcmp(expanded.data(), contents.data(), 5) == 0,
	      "path truncation changed the retained prefix");
	Check(test, expanded[5] == 0 && expanded[6] == 0 && expanded[7] == 0 && expanded[8] == 0,
	      "path-expanded extent was not zero-filled");

	Check(test, KernelTruncate("/truncate-contract/resize.bin", -1) ==
	                Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "negative length did not return EINVAL");
	Check(test, KernelTruncate("relative.bin", 2) == Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "relative path did not return EINVAL");
	Check(test, KernelTruncate(nullptr, 2) == Libs::LibKernel::KERNEL_ERROR_EFAULT,
	      "null path did not return EFAULT");
	Check(test, KernelTruncate("/truncate-contract/missing.bin", 2) ==
	                Libs::LibKernel::KERNEL_ERROR_ENOENT,
	      "missing file did not return ENOENT");
	Check(test, KernelTruncate("/truncate-contract/directory", 2) ==
	                Libs::LibKernel::KERNEL_ERROR_EISDIR,
	      "directory path did not return EISDIR");
	Check(test, std::filesystem::file_size(host_file, ec) == 9 && !ec,
	      "rejected path truncation changed the file");
	CheckOk(test, KernelClose(fd), "KernelClose");

	Umount("/truncate-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelRenameNullPathContract() {
	const char* test = "KernelRenameNullPathContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_rename_null_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_source = root / "source.bin";
	const auto host_target = root / "target.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(root, ec) && !ec,
	      "failed to create temporary fixture");
	Common::File source;
	Check(test, source.Create(host_source), "failed to create source fixture");
	constexpr std::array<char, 4> contents {'k', 'y', 't', 'y'};
	uint32_t written = 0;
	source.Write(contents.data(), contents.size(), &written);
	source.Close();
	Check(test, written == contents.size(), "failed to populate source fixture");

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("52NcYU9+lEo", Loader::SymbolType::Func) != nullptr,
	      "sceKernelRename NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/rename-null-contract");
	Check(test, KernelRename(nullptr, "/rename-null-contract/target.bin") ==
	                Libs::LibKernel::KERNEL_ERROR_EFAULT,
	      "null source path did not return EFAULT");
	Check(test, KernelRename("/rename-null-contract/source.bin", nullptr) ==
	                Libs::LibKernel::KERNEL_ERROR_EFAULT,
	      "null destination path did not return EFAULT");
	Check(test, std::filesystem::is_regular_file(host_source, ec) && !ec,
	      "rejected rename removed the source file");
	Check(test, !std::filesystem::exists(host_target, ec) && !ec,
	      "rejected rename created the destination file");
	Check(test, std::filesystem::file_size(host_source, ec) == contents.size() && !ec,
	      "rejected rename changed the source file");

	Umount("/rename-null-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelUnlinkNullPathContract() {
	const char* test = "KernelUnlinkNullPathContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_unlink_null_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_file = root / "retained.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(root, ec) && !ec,
	      "failed to create temporary fixture");
	Common::File retained;
	Check(test, retained.Create(host_file), "failed to create retained file");
	constexpr std::array<char, 4> contents {'k', 'e', 'e', 'p'};
	uint32_t written = 0;
	retained.Write(contents.data(), contents.size(), &written);
	retained.Close();
	Check(test, written == contents.size(), "failed to populate retained file");

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("AUXVxWeJU-A", Loader::SymbolType::Func) != nullptr,
	      "sceKernelUnlink NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/unlink-null-contract");
	Check(test, KernelUnlink(nullptr) == Libs::LibKernel::KERNEL_ERROR_EFAULT,
	      "null unlink path did not return EFAULT");
	Check(test, std::filesystem::is_regular_file(host_file, ec) && !ec,
	      "rejected unlink removed the retained file");
	Check(test, std::filesystem::file_size(host_file, ec) == contents.size() && !ec,
	      "rejected unlink changed the retained file");

	Umount("/unlink-null-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelUnlinkAbsolutePathContract() {
	const char* test = "KernelUnlinkAbsolutePathContract";
	static int  path_tag;
	const auto relative_path =
	    "kyty_kernel_unlink_relative_" +
	    std::to_string(reinterpret_cast<uintptr_t>(&path_tag)) + ".bin";

	std::error_code ec;
	std::filesystem::remove(relative_path, ec);
	ec.clear();
	Common::File fixture;
	Check(test, fixture.Create(relative_path), "failed to create relative unlink fixture");
	fixture.Close();

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("AUXVxWeJU-A", Loader::SymbolType::Func) != nullptr,
	      "sceKernelUnlink NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Check(test, KernelUnlink(relative_path.c_str()) == Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "relative unlink path did not return EINVAL");
	Check(test, std::filesystem::is_regular_file(relative_path, ec) && !ec,
	      "rejected relative unlink deleted the fixture");

	ec.clear();
	std::filesystem::remove(relative_path, ec);
	Check(test, !ec && !std::filesystem::exists(relative_path),
	      "failed to remove relative unlink fixture");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelOpenNullPathContract() {
	const char* test = "KernelOpenNullPathContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_open_null_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_file = root / "retained.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(root, ec) && !ec,
	      "failed to create temporary fixture");
	Common::File retained;
	Check(test, retained.Create(host_file), "failed to create retained file");
	retained.Close();

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("1G3lF1Gg1k8", Loader::SymbolType::Func) != nullptr,
	      "sceKernelOpen NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/open-null-contract");
	Check(test, KernelOpen(nullptr, 0, 0) == Libs::LibKernel::KERNEL_ERROR_EFAULT,
	      "null open path did not return EFAULT");
	const int fd = KernelOpen("/open-null-contract/retained.bin", 0, 0);
	Check(test, fd >= 3, "rejected null open corrupted descriptor allocation");
	CheckOk(test, KernelClose(fd), "KernelClose");
	Check(test, std::filesystem::is_regular_file(host_file, ec) && !ec,
	      "rejected open changed the retained file");

	Umount("/open-null-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelOpenAbsolutePathContract() {
	const char* test = "KernelOpenAbsolutePathContract";
	static int  path_tag;
	const auto relative_path =
	    "kyty_kernel_open_relative_" +
	    std::to_string(reinterpret_cast<uintptr_t>(&path_tag)) + ".bin";

	std::error_code ec;
	std::filesystem::remove(relative_path, ec);
	ec.clear();
	Common::File retained;
	Check(test, retained.Create(relative_path), "failed to create relative open fixture");
	retained.Close();

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("1G3lF1Gg1k8", Loader::SymbolType::Func) != nullptr,
	      "sceKernelOpen NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Check(test, KernelOpen(relative_path.c_str(), 0, 0) ==
	                Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "relative open path did not return EINVAL");
	Check(test, std::filesystem::is_regular_file(relative_path, ec) && !ec,
	      "rejected relative open changed the fixture");

	ec.clear();
	std::filesystem::remove(relative_path, ec);
	Check(test, !ec && !std::filesystem::exists(relative_path),
	      "failed to remove relative open fixture");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelOpenAccessModeContract() {
	const char* test = "KernelOpenAccessModeContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_open_mode_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_file = root / "retained.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(root, ec) && !ec,
	      "failed to create temporary fixture");
	Common::File retained;
	Check(test, retained.Create(host_file), "failed to create retained file");
	retained.Close();

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("1G3lF1Gg1k8", Loader::SymbolType::Func) != nullptr,
	      "sceKernelOpen NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/open-mode-contract");
	Check(test, KernelOpen("/open-mode-contract/retained.bin", 3, 0) ==
	                Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "conflicting open access-mode bits did not return EINVAL");
	const int fd = KernelOpen("/open-mode-contract/retained.bin", 0, 0);
	Check(test, fd >= 3, "rejected access mode corrupted descriptor allocation");
	CheckOk(test, KernelClose(fd), "KernelClose");
	Check(test, std::filesystem::is_regular_file(host_file, ec) && !ec,
	      "rejected access mode changed the retained file");

	Umount("/open-mode-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelOpenDirectoryWriteContract() {
	const char* test = "KernelOpenDirectoryWriteContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_open_directory_write_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_directory = root / "guestdir";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(host_directory, ec) && !ec,
	      "failed to create directory fixture");

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("1G3lF1Gg1k8", Loader::SymbolType::Func) != nullptr,
	      "sceKernelOpen NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/open-directory-write-contract");
	constexpr const char* directory_path = "/open-directory-write-contract/guestdir";
	Check(test, KernelOpen(directory_path, 1, 0) == Libs::LibKernel::KERNEL_ERROR_EISDIR,
	      "write-only directory open did not return EISDIR");
	Check(test, KernelOpen(directory_path, 2, 0) == Libs::LibKernel::KERNEL_ERROR_EISDIR,
	      "read/write directory open did not return EISDIR");
	Check(test, KernelOpen(directory_path, 0x0401, 0) == Libs::LibKernel::KERNEL_ERROR_EISDIR,
	      "truncating directory open did not return EISDIR");
	Check(test, KernelOpen(directory_path, 0x0201, 0) == Libs::LibKernel::KERNEL_ERROR_EISDIR,
	      "creating directory open did not return EISDIR");
	Check(test, KernelOpen(directory_path, 0x00020001, 0) ==
	                Libs::LibKernel::KERNEL_ERROR_EISDIR,
	      "O_DIRECTORY write-only open did not return EISDIR");
	const int fd = KernelOpen(directory_path, 0, 0);
	Check(test, fd >= 3, "rejected directory opens corrupted descriptor allocation");
	CheckOk(test, KernelClose(fd), "KernelClose");
	Check(test, std::filesystem::is_directory(host_directory, ec) && !ec,
	      "rejected directory open changed the directory fixture");

	Umount("/open-directory-write-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelOpenNameLengthContract() {
	const char* test = "KernelOpenNameLengthContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_open_name_length_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_file = root / "retained.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(root, ec) && !ec,
	      "failed to create temporary fixture");
	Common::File retained;
	Check(test, retained.Create(host_file), "failed to create retained file");
	retained.Close();

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("1G3lF1Gg1k8", Loader::SymbolType::Func) != nullptr,
	      "sceKernelOpen NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/open-name-length-contract");
	const auto max_name = "/open-name-length-contract/" + std::string(255, 'a');
	const auto overlong_name = "/open-name-length-contract/" + std::string(256, 'b');
	Check(test, KernelOpen(max_name.c_str(), 0, 0) == Libs::LibKernel::KERNEL_ERROR_ENOENT,
	      "maximum-length open component was rejected as overlong");
	Check(test, KernelOpen(overlong_name.c_str(), 0, 0) ==
	                Libs::LibKernel::KERNEL_ERROR_ENAMETOOLONG,
	      "overlong open component did not return ENAMETOOLONG");
	const int fd = KernelOpen("/open-name-length-contract/retained.bin", 0, 0);
	Check(test, fd >= 3, "rejected open components corrupted descriptor allocation");
	CheckOk(test, KernelClose(fd), "KernelClose");
	Check(test, std::filesystem::is_regular_file(host_file, ec) && !ec,
	      "rejected open component changed the retained file");

	Umount("/open-name-length-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelStatNullPointerContract() {
	const char* test = "KernelStatNullPointerContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_stat_null_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_file = root / "retained.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(root, ec) && !ec,
	      "failed to create temporary fixture");
	Common::File retained;
	Check(test, retained.Create(host_file), "failed to create retained file");
	retained.Close();

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("eV9wAD2riIA", Loader::SymbolType::Func) != nullptr,
	      "sceKernelStat NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/stat-null-contract");
	FileStat output;
	std::memset(&output, 0xa5, sizeof(output));
	const FileStat retained_output = output;
	Check(test, KernelStat(nullptr, &output) == Libs::LibKernel::KERNEL_ERROR_EFAULT,
	      "null stat path did not return EFAULT");
	Check(test, std::memcmp(&output, &retained_output, sizeof(output)) == 0,
	      "rejected null stat path changed the output structure");
	Check(test, KernelStat("/stat-null-contract/retained.bin", nullptr) ==
	                Libs::LibKernel::KERNEL_ERROR_EFAULT,
	      "null stat output did not return EFAULT");
	CheckOk(test, KernelStat("/stat-null-contract/retained.bin", &output),
	        "valid KernelStat after rejected pointers");
	Check(test, std::filesystem::is_regular_file(host_file, ec) && !ec,
	      "rejected stat changed the retained file");

	Umount("/stat-null-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelStatAbsolutePathContract() {
	const char* test = "KernelStatAbsolutePathContract";
	static int  path_tag;
	const auto relative_path =
	    "kyty_kernel_stat_relative_" +
	    std::to_string(reinterpret_cast<uintptr_t>(&path_tag)) + ".bin";

	std::error_code ec;
	std::filesystem::remove(relative_path, ec);
	ec.clear();
	Common::File fixture;
	Check(test, fixture.Create(relative_path), "failed to create relative stat fixture");
	fixture.Close();

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("eV9wAD2riIA", Loader::SymbolType::Func) != nullptr,
	      "sceKernelStat NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	FileStat output;
	std::memset(&output, 0xa5, sizeof(output));
	const FileStat retained_output = output;
	Check(test, KernelStat(relative_path.c_str(), &output) == Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "relative stat path did not return EINVAL");
	Check(test, std::memcmp(&output, &retained_output, sizeof(output)) == 0,
	      "rejected relative stat path changed the output structure");
	Check(test, std::filesystem::is_regular_file(relative_path, ec) && !ec,
	      "rejected relative stat changed the fixture");

	ec.clear();
	std::filesystem::remove(relative_path, ec);
	Check(test, !ec && !std::filesystem::exists(relative_path),
	      "failed to remove relative stat fixture");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelStatNameLengthContract() {
	const char* test = "KernelStatNameLengthContract";

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("eV9wAD2riIA", Loader::SymbolType::Func) != nullptr,
	      "sceKernelStat NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	FileStat output;
	std::memset(&output, 0xa5, sizeof(output));
	const FileStat retained_output = output;
	const auto     max_name        = "/" + std::string(255, 'a');
	const auto     overlong_name   = "/" + std::string(256, 'b');
	Check(test, KernelStat(max_name.c_str(), &output) == Libs::LibKernel::KERNEL_ERROR_ENOENT,
	      "maximum-length stat component was rejected as overlong");
	Check(test, std::memcmp(&output, &retained_output, sizeof(output)) == 0,
	      "missing maximum-length stat path changed the output structure");
	Check(test, KernelStat(overlong_name.c_str(), &output) ==
	                Libs::LibKernel::KERNEL_ERROR_ENAMETOOLONG,
	      "overlong stat component did not return ENAMETOOLONG");
	Check(test, std::memcmp(&output, &retained_output, sizeof(output)) == 0,
	      "rejected overlong stat path changed the output structure");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelFstatInvalidDescriptorContract() {
	const char* test = "KernelFstatInvalidDescriptorContract";

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("kBwCPsYX-m4", Loader::SymbolType::Func) != nullptr,
	      "sceKernelFstat NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	FileStat output;
	std::memset(&output, 0xa5, sizeof(output));
	const FileStat retained_output = output;
	Check(test, KernelFstat(-1, &output) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "negative file descriptor did not return EBADF");
	Check(test, std::memcmp(&output, &retained_output, sizeof(output)) == 0,
	      "rejected negative descriptor changed the output structure");
	Check(test, KernelFstat(0, &output) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "reserved file descriptor did not return EBADF");
	Check(test, std::memcmp(&output, &retained_output, sizeof(output)) == 0,
	      "rejected reserved descriptor changed the output structure");
	Check(test, KernelFstat(0x7fffffff, &output) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "unallocated file descriptor did not return EBADF");
	Check(test, std::memcmp(&output, &retained_output, sizeof(output)) == 0,
	      "rejected unallocated descriptor changed the output structure");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelReadInvalidDescriptorContract() {
	const char* test = "KernelReadInvalidDescriptorContract";

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("Cg4srZ6TKbU", Loader::SymbolType::Func) != nullptr,
	      "sceKernelRead NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	std::array<uint8_t, 8> output;
	output.fill(0xa5);
	const auto retained_output = output;
	Check(test, KernelRead(-1, output.data(), output.size()) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "negative file descriptor did not return EBADF");
	Check(test, output == retained_output, "rejected negative descriptor changed the buffer");
	Check(test, KernelRead(0, output.data(), output.size()) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "reserved file descriptor did not return EBADF");
	Check(test, output == retained_output, "rejected reserved descriptor changed the buffer");
	Check(test, KernelRead(0x7fffffff, output.data(), output.size()) ==
	                Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "unallocated file descriptor did not return EBADF");
	Check(test, output == retained_output, "rejected unallocated descriptor changed the buffer");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelPreadInvalidDescriptorContract() {
	const char* test = "KernelPreadInvalidDescriptorContract";

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("+r3rMFwItV4", Loader::SymbolType::Func) != nullptr,
	      "sceKernelPread NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	std::array<uint8_t, 8> output;
	output.fill(0xa5);
	const auto retained_output = output;
	Check(test, KernelPread(-1, output.data(), output.size(), 0) ==
	                Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "negative file descriptor did not return EBADF");
	Check(test, output == retained_output, "rejected negative descriptor changed the buffer");
	Check(test, KernelPread(0, output.data(), output.size(), 0) ==
	                Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "reserved file descriptor did not return EBADF");
	Check(test, output == retained_output, "rejected reserved descriptor changed the buffer");
	Check(test, KernelPread(0x7fffffff, output.data(), output.size(), 0) ==
	                Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "unallocated file descriptor did not return EBADF");
	Check(test, output == retained_output, "rejected unallocated descriptor changed the buffer");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelPwriteInvalidDescriptorContract() {
	const char* test = "KernelPwriteInvalidDescriptorContract";

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("nKWi-N2HBV4", Loader::SymbolType::Func) != nullptr,
	      "sceKernelPwrite NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	const std::array<uint8_t, 8> input {0x10, 0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87};
	Check(test, KernelPwrite(-1, input.data(), input.size(), 0) ==
	                Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "negative file descriptor did not return EBADF");
	Check(test, KernelPwrite(0, input.data(), input.size(), 0) ==
	                Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "reserved file descriptor did not return EBADF");
	Check(test, KernelPwrite(0x7fffffff, input.data(), input.size(), 0) ==
	                Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "unallocated file descriptor did not return EBADF");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelMkdirNullPathContract() {
	const char* test = "KernelMkdirNullPathContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_mkdir_null_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_directory = root / "created";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(root, ec) && !ec,
	      "failed to create temporary fixture");

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("1-LFLmRFxxM", Loader::SymbolType::Func) != nullptr,
	      "sceKernelMkdir NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/mkdir-null-contract");
	Check(test, KernelMkdir(nullptr, 0777) == Libs::LibKernel::KERNEL_ERROR_EFAULT,
	      "null mkdir path did not return EFAULT");
	Check(test, !std::filesystem::exists(host_directory, ec) && !ec,
	      "rejected mkdir created a directory");
	CheckOk(test, KernelMkdir("/mkdir-null-contract/created", 0777),
	        "valid KernelMkdir after rejected null path");
	Check(test, std::filesystem::is_directory(host_directory, ec) && !ec,
	      "valid mkdir did not create the directory");

	Umount("/mkdir-null-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelMkdirAbsolutePathContract() {
	const char* test = "KernelMkdirAbsolutePathContract";
	static int  path_tag;
	const auto relative_path =
	    "kyty_kernel_mkdir_relative_" +
	    std::to_string(reinterpret_cast<uintptr_t>(&path_tag));

	std::error_code ec;
	std::filesystem::remove_all(relative_path, ec);
	ec.clear();
	Check(test, !std::filesystem::exists(relative_path, ec) && !ec,
	      "failed to prepare relative mkdir fixture");

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("1-LFLmRFxxM", Loader::SymbolType::Func) != nullptr,
	      "sceKernelMkdir NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Check(test, KernelMkdir(relative_path.c_str(), 0777) ==
	                Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "relative mkdir path did not return EINVAL");
	Check(test, !std::filesystem::exists(relative_path, ec) && !ec,
	      "rejected relative mkdir created a directory");

	ec.clear();
	std::filesystem::remove_all(relative_path, ec);
	Check(test, !ec && !std::filesystem::exists(relative_path),
	      "failed to remove relative mkdir fixture");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelMkdirPathLengthContract() {
	const char* test = "KernelMkdirPathLengthContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_mkdir_path_length_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));

	const std::string first(80, 'a');
	const std::string second(80, 'b');
	const std::string third(80, 'c');
	const auto        host_parent = root / first / second / third;
	const auto        host_created = host_parent / "created";
	const auto        valid_long_path =
	    "/mkdir-path-length-contract/" + first + "/" + second + "/" + third + "/created";
	const auto overlong_component =
	    "/mkdir-path-length-contract/" + std::string(256, 'd');
	const auto overlong_path = "/" + std::string(255, 'e') + "/" + std::string(255, 'f') +
	                           "/" + std::string(255, 'g') + "/" + std::string(255, 'h');

	Check(test, valid_long_path.size() > 255 && valid_long_path.size() < 1024,
	      "valid long-path fixture is outside the intended bounds");
	Check(test, overlong_path.size() == 1024,
	      "whole-path overflow fixture is not exactly 1024 bytes");

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(host_parent, ec) && !ec,
	      "failed to create long-path parent fixture");

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("1-LFLmRFxxM", Loader::SymbolType::Func) != nullptr,
	      "sceKernelMkdir NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/mkdir-path-length-contract");
	CheckOk(test, KernelMkdir(valid_long_path.c_str(), 0777),
	        "KernelMkdir(valid multi-component long path)");
	Check(test, std::filesystem::is_directory(host_created, ec) && !ec,
	      "valid multi-component long path was not created");
	Check(test, KernelMkdir(overlong_component.c_str(), 0777) ==
	                Libs::LibKernel::KERNEL_ERROR_ENAMETOOLONG,
	      "overlong mkdir component did not return ENAMETOOLONG");
	Check(test, KernelMkdir(overlong_path.c_str(), 0777) ==
	                Libs::LibKernel::KERNEL_ERROR_ENAMETOOLONG,
	      "overlong whole mkdir path did not return ENAMETOOLONG");

	Umount("/mkdir-path-length-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary fixture");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelRmdirNullPathContract() {
	const char* test = "KernelRmdirNullPathContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_rmdir_null_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_directory = root / "retained";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(host_directory, ec) && !ec,
	      "failed to create temporary fixture");

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("naInUjYt3so", Loader::SymbolType::Func) != nullptr,
	      "sceKernelRmdir NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/rmdir-null-contract");
	Check(test, KernelRmdir(nullptr) == Libs::LibKernel::KERNEL_ERROR_EFAULT,
	      "null rmdir path did not return EFAULT");
	Check(test, std::filesystem::is_directory(host_directory, ec) && !ec,
	      "rejected rmdir removed the retained directory");

	Umount("/rmdir-null-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelRmdirAbsolutePathContract() {
	const char* test = "KernelRmdirAbsolutePathContract";
	static int  path_tag;
	const auto relative_path =
	    "kyty_kernel_rmdir_relative_" +
	    std::to_string(reinterpret_cast<uintptr_t>(&path_tag));

	std::error_code ec;
	std::filesystem::remove_all(relative_path, ec);
	ec.clear();
	Check(test, std::filesystem::create_directory(relative_path, ec) && !ec,
	      "failed to create relative rmdir fixture");

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("naInUjYt3so", Loader::SymbolType::Func) != nullptr,
	      "sceKernelRmdir NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Check(test, KernelRmdir(relative_path.c_str()) == Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "relative rmdir path did not return EINVAL");
	Check(test, std::filesystem::is_directory(relative_path, ec) && !ec,
	      "rejected relative rmdir deleted the directory");

	ec.clear();
	std::filesystem::remove_all(relative_path, ec);
	Check(test, !ec && !std::filesystem::exists(relative_path),
	      "failed to remove relative rmdir fixture");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelCheckReachabilityNullPathContract() {
	const char* test = "KernelCheckReachabilityNullPathContract";

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("uWyW3v98sU4", Loader::SymbolType::Func) != nullptr,
	      "sceKernelCheckReachability NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Check(test, KernelCheckReachability(nullptr) == Libs::LibKernel::KERNEL_ERROR_ENOENT,
	      "null reachability path did not return ENOENT");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelCheckReachabilityAbsolutePathContract() {
	const char* test = "KernelCheckReachabilityAbsolutePathContract";
	static int  path_tag;
	const auto relative_path =
	    "kyty_kernel_reachability_relative_" +
	    std::to_string(reinterpret_cast<uintptr_t>(&path_tag));

	std::error_code ec;
	std::filesystem::remove_all(relative_path, ec);
	ec.clear();
	Check(test, std::filesystem::create_directory(relative_path, ec) && !ec,
	      "failed to create relative-path fixture");

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("uWyW3v98sU4", Loader::SymbolType::Func) != nullptr,
	      "sceKernelCheckReachability NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	const auto result = KernelCheckReachability(relative_path.c_str());
	ec.clear();
	std::filesystem::remove_all(relative_path, ec);
	Check(test, !ec && !std::filesystem::exists(relative_path),
	      "failed to remove relative-path fixture");
	Check(test, result == Libs::LibKernel::KERNEL_ERROR_ENOENT,
	      "relative reachability path did not return ENOENT");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelCheckReachabilityOverlengthPathContract() {
	const char* test = "KernelCheckReachabilityOverlengthPathContract";

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("uWyW3v98sU4", Loader::SymbolType::Func) != nullptr,
	      "sceKernelCheckReachability NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	const std::string path = "/" + std::string(256, 'a');
	Check(test, KernelCheckReachability(path.c_str()) == Libs::LibKernel::KERNEL_ERROR_ENOENT,
	      "overlength reachability path did not return ENOENT");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelRenameAbsolutePathContract() {
	const char* test = "KernelRenameAbsolutePathContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_rename_absolute_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_source = root / "source.bin";
	const auto host_target = root / "target.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(root, ec) && !ec,
	      "failed to create temporary fixture");
	Common::File source;
	Check(test, source.Create(host_source), "failed to create source fixture");
	constexpr std::array<char, 4> contents {'p', 'a', 't', 'h'};
	uint32_t written = 0;
	source.Write(contents.data(), contents.size(), &written);
	source.Close();
	Check(test, written == contents.size(), "failed to populate source fixture");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/rename-absolute-contract");
	Check(test, KernelRename("source.bin", "/rename-absolute-contract/target.bin") ==
	                Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "relative source path did not return EINVAL");
	Check(test, KernelRename("/rename-absolute-contract/source.bin", "target.bin") ==
	                Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "relative destination path did not return EINVAL");
	Check(test, std::filesystem::is_regular_file(host_source, ec) && !ec,
	      "rejected rename removed the source file");
	Check(test, !std::filesystem::exists(host_target, ec) && !ec,
	      "rejected rename created the destination file");
	Check(test, std::filesystem::file_size(host_source, ec) == contents.size() && !ec,
	      "rejected rename changed the source file");

	Umount("/rename-absolute-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelRenameOpenSourceContract() {
	const char* test = "KernelRenameOpenSourceContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_rename_open_source_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_source = root / "source.bin";
	const auto host_target = root / "target.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(root, ec) && !ec,
	      "failed to create temporary fixture");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/rename-open-source-contract");
	constexpr int create_read_write_truncate = 0x2 | 0x200 | 0x400;
	const int fd =
	    KernelOpen("/rename-open-source-contract/source.bin", create_read_write_truncate, 0600);
	Check(test, fd >= 3, "failed to create open source fixture");
	constexpr std::array<char, 4> contents {'o', 'p', 'e', 'n'};
	Check(test, KernelWrite(fd, contents.data(), contents.size()) == contents.size(),
	      "failed to populate open source fixture");
	Check(test, KernelLseek(fd, 0, 0) == 0, "failed to rewind open source descriptor");

	CheckOk(test,
	        KernelRename("/rename-open-source-contract/source.bin",
	                     "/rename-open-source-contract/target.bin"),
	        "KernelRename(open source)");
	Check(test, !std::filesystem::exists(host_source, ec) && !ec,
	      "successful rename retained the old source path");
	Check(test, std::filesystem::is_regular_file(host_target, ec) && !ec,
	      "successful rename did not create the target path");

	Check(test, KernelLseek(fd, contents.size(), 0) == contents.size(),
	      "renamed source descriptor was no longer seekable");
	constexpr std::array<char, 2> suffix {'e', 'd'};
	Check(test, KernelWrite(fd, suffix.data(), suffix.size()) == suffix.size(),
	      "renamed source descriptor was no longer writable");
	CheckOk(test, KernelClose(fd), "KernelClose");

	std::array<char, 6> observed {};
	Common::File        target(host_target, Common::File::Mode::Read);
	uint32_t            observed_size = 0;
	target.Read(observed.data(), observed.size(), &observed_size);
	target.Close();
	Check(test, observed_size == observed.size(), "renamed target has the wrong size");
	Check(test, std::equal(contents.begin(), contents.end(), observed.begin()) &&
	                std::equal(suffix.begin(), suffix.end(), observed.begin() + contents.size()),
	      "renamed descriptor did not retain the same file contents");

	Umount("/rename-open-source-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelRenameOpenDestinationContract() {
	const char* test = "KernelRenameOpenDestinationContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_rename_open_destination_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_source = root / "source.bin";
	const auto host_target = root / "target.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(root, ec) && !ec,
	      "failed to create temporary fixture");
	constexpr std::array<char, 3> source_contents {'n', 'e', 'w'};
	constexpr std::array<char, 3> target_contents {'o', 'l', 'd'};
	for (const auto& [path, contents]:
	     std::array {std::pair {host_source, source_contents},
	                 std::pair {host_target, target_contents}}) {
		Common::File file;
		Check(test, file.Create(path), "failed to create rename fixture");
		uint32_t written = 0;
		file.Write(contents.data(), contents.size(), &written);
		file.Close();
		Check(test, written == contents.size(), "failed to populate rename fixture");
	}

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/rename-open-destination-contract");
	const int fd = KernelOpen("/rename-open-destination-contract/target.bin", 2, 0);
	Check(test, fd >= 3, "failed to open destination fixture");

	CheckOk(test,
	        KernelRename("/rename-open-destination-contract/source.bin",
	                     "/rename-open-destination-contract/target.bin"),
	        "KernelRename(open destination)");
	Check(test, !std::filesystem::exists(host_source, ec) && !ec,
	      "successful replacement retained the source path");
	Check(test, std::filesystem::is_regular_file(host_target, ec) && !ec,
	      "successful replacement removed the destination path");

	Check(test, KernelLseek(fd, target_contents.size(), 0) == target_contents.size(),
	      "replaced destination descriptor was no longer seekable");
	constexpr char suffix = '!';
	Check(test, KernelWrite(fd, &suffix, 1) == 1,
	      "replaced destination descriptor was no longer writable");
	CheckOk(test, KernelClose(fd), "KernelClose");

	std::array<char, 3> observed {};
	Common::File        replacement(host_target, Common::File::Mode::Read);
	uint32_t            observed_size = 0;
	replacement.Read(observed.data(), observed.size(), &observed_size);
	replacement.Close();
	Check(test, observed_size == source_contents.size() && observed == source_contents,
	      "the live old descriptor modified the replacement pathname");
	Check(test, std::filesystem::file_size(host_target, ec) == source_contents.size() && !ec,
	      "replacement pathname retained the old destination file");

	Umount("/rename-open-destination-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelRmdirNonemptyContract() {
	const char* test = "KernelRmdirNonemptyContract";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_rmdir_nonempty_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_dir  = root / "directory";
	const auto host_file = host_dir / "retained.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(host_dir, ec) && !ec,
	      "failed to create directory fixture");
	Common::File retained;
	Check(test, retained.Create(host_file), "failed to create retained file");
	constexpr std::array<char, 4> contents {'k', 'e', 'e', 'p'};
	uint32_t written = 0;
	retained.Write(contents.data(), contents.size(), &written);
	retained.Close();
	Check(test, written == contents.size(), "failed to populate retained file");

	Loader::SymbolDatabase symbols;
	Libs::InitLibKernel_1(&symbols);
	Check(test, symbols.FindByNid("naInUjYt3so", Loader::SymbolType::Func) != nullptr,
	      "sceKernelRmdir NID is not registered");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/rmdir-nonempty-contract");
	Check(test, KernelRmdir("/rmdir-nonempty-contract/directory") ==
	                Libs::LibKernel::KERNEL_ERROR_ENOTEMPTY,
	      "nonempty directory did not return ENOTEMPTY");
	Check(test, std::filesystem::is_directory(host_dir, ec) && !ec,
	      "rejected rmdir removed the directory");
	Check(test, std::filesystem::is_regular_file(host_file, ec) && !ec,
	      "rejected rmdir removed the retained file");
	Check(test, std::filesystem::file_size(host_file, ec) == contents.size() && !ec,
	      "rejected rmdir changed the retained file");

	Umount("/rmdir-nonempty-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelScalarIoSizeLimit() {
	const char* test = "KernelScalarIoSizeLimit";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_io_size_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_file = root / "limit.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directory(root, ec) && !ec,
	      "failed to create temporary directory");

	constexpr char contents[] = "size-contract";
	Common::File   fixture;
	Check(test, fixture.Create(host_file), "failed to create temporary file");
	uint32_t bytes_written = 0;
	fixture.Write(contents, sizeof(contents) - 1, &bytes_written);
	fixture.Close();
	Check(test, bytes_written == sizeof(contents) - 1, "failed to populate temporary file");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/io-size-contract");
	const int fd = KernelOpen("/io-size-contract/limit.bin", 2, 0);
	Check(test, fd >= 3, "failed to open mounted temporary file");

	char         byte                  = static_cast<char>(0x5a);
	const size_t oversized             = static_cast<size_t>(INT_MAX) + 1u;
	const size_t legacy_abort_boundary = static_cast<size_t>(UINT_MAX) + 1u;
	const auto   invalid_io            = Libs::LibKernel::KERNEL_ERROR_EINVAL;
	Check(test, KernelRead(fd, &byte, legacy_abort_boundary) == invalid_io,
	      "oversized read above the former abort boundary did not return EINVAL");
	Check(test, KernelRead(fd, &byte, oversized) == invalid_io,
	      "oversized read did not return EINVAL");
	Check(test, KernelPread(fd, &byte, oversized, 0) == invalid_io,
	      "oversized pread did not return EINVAL");
	Check(test, KernelWrite(fd, &byte, oversized) == invalid_io,
	      "oversized write did not return EINVAL");
	Check(test, KernelPwrite(fd, &byte, oversized, 0) == invalid_io,
	      "oversized pwrite did not return EINVAL");
	Check(test, byte == static_cast<char>(0x5a), "rejected I/O changed the guest buffer");
	Check(test, KernelLseek(fd, 0, 1) == 0, "rejected I/O changed the file position");
	Check(test, std::filesystem::file_size(host_file, ec) == sizeof(contents) - 1 && !ec,
	      "rejected I/O changed the file size");

	CheckOk(test, KernelClose(fd), "KernelClose");
	Umount("/io-size-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelPwriteRequiresWritableDescriptor() {
	const char* test = "KernelPwriteRequiresWritableDescriptor";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_pwrite_access_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_file = root / "readonly.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directory(root, ec) && !ec,
	      "failed to create temporary directory");

	constexpr char contents[] = "readonly-contract";
	Common::File   fixture;
	Check(test, fixture.Create(host_file), "failed to create temporary file");
	uint32_t bytes_written = 0;
	fixture.Write(contents, sizeof(contents) - 1, &bytes_written);
	fixture.Close();
	Check(test, bytes_written == sizeof(contents) - 1, "failed to populate temporary file");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/pwrite-access-contract");
	const int fd = KernelOpen("/pwrite-access-contract/readonly.bin", 0, 0);
	Check(test, fd >= 3, "failed to open mounted file read-only");
	Check(test, KernelLseek(fd, 4, 0) == 4, "failed to establish file position");

	const char replacement = 'X';
	Check(test, KernelPwrite(fd, &replacement, 1, 0) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "pwrite through a read-only descriptor did not return EBADF");
	Check(test, KernelLseek(fd, 0, 1) == 4,
	      "rejected pwrite changed the descriptor position");

	std::array<char, sizeof(contents) - 1> observed {};
	Common::File verify(host_file, Common::File::Mode::Read);
	uint32_t     bytes_read = 0;
	verify.Read(observed.data(), observed.size(), &bytes_read);
	verify.Close();
	Check(test, bytes_read == observed.size() &&
	                std::memcmp(observed.data(), contents, observed.size()) == 0,
	      "rejected pwrite changed file contents");

	CheckOk(test, KernelClose(fd), "KernelClose");
	Umount("/pwrite-access-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelPwriteIgnoresAppendPosition() {
	const char* test = "KernelPwriteIgnoresAppendPosition";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_pwrite_append_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_file = root / "append.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directory(root, ec) && !ec,
	      "failed to create temporary directory");

	constexpr char initial[] = "abcdef";
	Common::File   fixture;
	Check(test, fixture.Create(host_file), "failed to create temporary file");
	uint32_t bytes_written = 0;
	fixture.Write(initial, sizeof(initial) - 1, &bytes_written);
	fixture.Close();
	Check(test, bytes_written == sizeof(initial) - 1, "failed to populate temporary file");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/pwrite-append-contract");
	constexpr int append_read_write = 0x0008 | 0x0002;
	const int fd = KernelOpen("/pwrite-append-contract/append.bin", append_read_write, 0);
	Check(test, fd >= 3, "failed to open mounted file in append mode");
	Check(test, KernelLseek(fd, 4, 0) == 4, "failed to establish descriptor position");

	const char replacement = 'X';
	Check(test, KernelPwrite(fd, &replacement, 1, 1) == 1,
	      "positional write on append descriptor failed");
	Check(test, KernelLseek(fd, 0, 1) == 4,
	      "positional write changed the descriptor position");

	std::array<char, sizeof(initial) - 1> positioned {};
	Common::File verify_positioned(host_file, Common::File::Mode::Read);
	uint32_t     bytes_read = 0;
	verify_positioned.Read(positioned.data(), positioned.size(), &bytes_read);
	verify_positioned.Close();
	constexpr std::array<char, sizeof(initial) - 1> expected_positioned {'a', 'X', 'c', 'd', 'e',
	                                                                   'f'};
	Check(test, bytes_read == positioned.size() && positioned == expected_positioned,
	      "pwrite appended instead of honoring its explicit offset");

	const char appended = 'Y';
	Check(test, KernelWrite(fd, &appended, 1) == 1, "ordinary append write failed");
	CheckOk(test, KernelClose(fd), "KernelClose");
	std::array<char, sizeof(initial)> final_contents {};
	Common::File verify_append(host_file, Common::File::Mode::Read);
	verify_append.Read(final_contents.data(), final_contents.size(), &bytes_read);
	verify_append.Close();
	constexpr std::array<char, sizeof(initial)> expected_final {'a', 'X', 'c', 'd', 'e', 'f', 'Y'};
	Check(test, bytes_read == final_contents.size() && final_contents == expected_final,
	      "ordinary append behavior changed after positional write");

	Umount("/pwrite-append-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelWriteRequiresWritableDescriptor() {
	const char* test = "KernelWriteRequiresWritableDescriptor";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_write_access_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto host_file = root / "readonly.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directory(root, ec) && !ec,
	      "failed to create temporary directory");

	constexpr char contents[] = "readonly-contract";
	Common::File   fixture;
	Check(test, fixture.Create(host_file), "failed to create temporary file");
	uint32_t bytes_written = 0;
	fixture.Write(contents, sizeof(contents) - 1, &bytes_written);
	fixture.Close();
	Check(test, bytes_written == sizeof(contents) - 1, "failed to populate temporary file");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/write-access-contract");
	const int fd = KernelOpen("/write-access-contract/readonly.bin", 0, 0);
	Check(test, fd >= 3, "failed to open mounted file read-only");
	Check(test, KernelLseek(fd, 4, 0) == 4, "failed to establish file position");

	const char replacement = 'X';
	Check(test, KernelWrite(fd, &replacement, 1) == Libs::LibKernel::KERNEL_ERROR_EBADF,
	      "write through a read-only descriptor did not return EBADF");
	Check(test, KernelLseek(fd, 0, 1) == 4,
	      "rejected write changed the descriptor position");

	std::array<char, sizeof(contents) - 1> observed {};
	Common::File verify(host_file, Common::File::Mode::Read);
	uint32_t     bytes_read = 0;
	verify.Read(observed.data(), observed.size(), &bytes_read);
	verify.Close();
	Check(test, bytes_read == observed.size() &&
	                std::memcmp(observed.data(), contents, observed.size()) == 0,
	      "rejected write changed file contents");

	CheckOk(test, KernelClose(fd), "KernelClose");
	Umount("/write-access-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelDirectoryReadError() {
	const char* test = "KernelDirectoryReadError";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_directory_read_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto guest_directory = root / "guestdir";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(guest_directory, ec) && !ec,
	      "failed to create temporary directory fixture");

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/directory-read-contract");
	constexpr int open_directory = 0x00020000;
	const int fd = KernelOpen("/directory-read-contract/guestdir", open_directory, 0);
	Check(test, fd >= 3, "failed to open mounted directory");

	char byte = static_cast<char>(0x5a);
	Check(test, KernelRead(fd, &byte, 1) == Libs::LibKernel::KERNEL_ERROR_EISDIR,
	      "directory read did not return EISDIR");
	Check(test, KernelPread(fd, &byte, 1, 0) == Libs::LibKernel::KERNEL_ERROR_EISDIR,
	      "directory pread did not return EISDIR");
	Check(test, byte == static_cast<char>(0x5a), "rejected directory read changed the buffer");

	CheckOk(test, KernelClose(fd), "KernelClose");
	Umount("/directory-read-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestKernelDirectorySeekPositions() {
	const char* test = "KernelDirectorySeekPositions";
	static int  path_tag;
	const auto  root = std::filesystem::temp_directory_path() /
	                  ("kyty_kernel_directory_seek_" +
	                   std::to_string(reinterpret_cast<uintptr_t>(&path_tag)));
	const auto guest_directory = root / "guestdir";
	const auto guest_file      = guest_directory / "entry.bin";

	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	ec.clear();
	Check(test, std::filesystem::create_directories(guest_directory, ec) && !ec,
	      "failed to create temporary directory fixture");
	Common::File fixture;
	Check(test, fixture.Create(guest_file), "failed to create directory entry fixture");
	fixture.Close();

	using namespace Libs::LibKernel::FileSystem;
	Mount(root, "/directory-seek-contract");
	constexpr int open_directory = 0x00020000;
	const int fd = KernelOpen("/directory-seek-contract/guestdir", open_directory, 0);
	Check(test, fd >= 3, "failed to open mounted directory");

	std::array<char, 512> first {};
	std::array<char, 512> second {};
	int64_t               first_base = -1;
	const int64_t first_size = KernelGetdirentries(fd, first.data(), first.size(), &first_base);
	Check(test, first_size > 0 && first_base == 0, "initial directory enumeration failed");
	Check(test, KernelLseek(fd, 1, 0) == Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "unobserved directory position was accepted");
	Check(test, KernelLseek(fd, 0, 1) == first_size,
	      "invalid directory seek changed the current position");
	Check(test, KernelLseek(fd, 0, 0) == 0, "directory rewind failed");

	int64_t second_base = -1;
	const int64_t second_size = KernelGetdirentries(fd, second.data(), second.size(), &second_base);
	Check(test, second_size == first_size && second_base == 0,
	      "rewound directory enumeration returned a different extent");
	Check(test, std::memcmp(first.data(), second.data(), static_cast<size_t>(first_size)) == 0,
	      "rewound directory enumeration returned different entries");
	Check(test, KernelLseek(fd, 0, 2) == first_size,
	      "known directory end position was rejected");
	Check(test, KernelLseek(fd, -first_size, 1) == 0,
	      "relative directory seek to an observed position failed");
	Check(test, KernelLseek(fd, first_size, 0) == first_size,
	      "previously returned directory position was rejected");

	CheckOk(test, KernelClose(fd), "KernelClose");
	Umount("/directory-seek-contract");
	ec.clear();
	std::filesystem::remove_all(root, ec);
	Check(test, !ec && !std::filesystem::exists(root), "failed to remove temporary directory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestGuestStackUsesPrivateOwnerMemoryAndCache() {
	const char* test     = "GuestStackUsesPrivateOwnerMemoryAndCache";
	const auto  baseline = AvailableFlexibleMemory(test);
	uint64_t    first    = 0;
	uint64_t    second   = 0;
	uint64_t    map_size = 0;

	Check(test, Libs::LibKernel::TestGuestStackOwnerLifecycle(&first, &second, &map_size),
	      "guest stack owner lifecycle failed");
	Check(test, first != 0 && first == second, "guest stack cache did not reuse its owner mapping");
	Check(test, map_size != 0 && (map_size & (SceKernelPageSize - 1u)) == 0,
	      "guest stack mapping is not 16 KiB aligned");
	Check(test, AvailableFlexibleMemory(test) == baseline,
	      "private guest stack changed flexible backing capacity");

	std::printf("[host]    %-48s ok\n", test);
}

void TestMainEntryUsesGuestStackAndDisablesHostChecks() {
	const char* test = "MainEntryUsesGuestStackAndDisablesHostChecks";

	Check(test, Loader::TestMainEntryUsesGuestStack(),
	      "main-entry stack switch did not preserve the guest/host stack invariants");

	std::printf("[host]    %-48s ok\n", test);
}

void TestFragmentedBackingUnmapRollback() {
	const char* test     = "FragmentedBackingUnmapRollback";
	const auto  baseline = AvailableFlexibleMemory(test);
	const auto  left =
	    MapNamedFlexible(test, SceKernelPageSize, SceKernelProtCpuRw, "backing_hole_left");
	const auto blocker =
	    MapNamedFlexible(test, SceKernelPageSize, SceKernelProtCpuRw, "backing_blocker");
	const auto right =
	    MapNamedFlexible(test, SceKernelPageSize, SceKernelProtCpuRw, "backing_hole_right");
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(left, SceKernelPageSize),
	        "KernelMunmap(left hole)");
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(right, SceKernelPageSize),
	        "KernelMunmap(right hole)");

	const auto fragmented =
	    MapNamedFlexible(test, SceKernelPageSize * 2, SceKernelProtCpuRw, "fragmented_backing");
	auto* first_word = reinterpret_cast<uint64_t*>(fragmented);
	auto* last_word =
	    reinterpret_cast<uint64_t*>(fragmented + SceKernelPageSize * 2 - sizeof(uint64_t));
	*first_word = 0x465241474c454654ull; // "FRAGLEFT"
	*last_word  = 0x4652414752474854ull; // "FRAGRGHT"

	Libs::LibKernel::Memory::TestFailGuestBackingStoreUnmapAfter(1);
	CheckFailed(test, Libs::LibKernel::Memory::KernelMunmap(fragmented, SceKernelPageSize * 2),
	            "KernelMunmap(injected second-view failure)");
	ExpectRange(test, Query(test, fragmented), fragmented, fragmented + SceKernelPageSize * 2,
	            SceKernelProtCpuRw, 1, 0, 0, 1, "fragmented_backing");
	Check(test, *first_word == 0x465241474c454654ull && *last_word == 0x4652414752474854ull,
	      "transactional backing-unmap rollback lost mapped contents");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(fragmented, SceKernelPageSize * 2),
	        "KernelMunmap(retry)");
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(blocker, SceKernelPageSize),
	        "KernelMunmap(blocker)");
	Check(test, AvailableFlexibleMemory(test) == baseline,
	      "fragmented backing rollback test leaked flexible capacity");

	std::printf("[host]    %-48s ok\n", test);
}

void TestRuntimeMemoryOwnerLifecycle() {
	const char* test = "RuntimeMemoryOwnerLifecycle";
	Check(test,
	      Libs::LibKernel::Memory::AllocateRuntimeMemory(0x10000, SceKernelPageSize,
	                                                     Common::VirtualMemory::Mode::ReadWrite,
	                                                     "runtime_outside_owner", true) == 0,
	      "fixed runtime allocation escaped the guest owner");

	const auto base = Libs::LibKernel::Memory::AllocateRuntimeMemory(
	    0, SceKernelPageSize * 2, Common::VirtualMemory::Mode::ReadWrite, "runtime_lifecycle");
	Check(test, base != 0, "runtime allocation failed");
	Check(test, Libs::LibKernel::Memory::TestGuestAddressRangeIsOwned(base, SceKernelPageSize * 2),
	      "runtime allocation is outside the owner");
	*reinterpret_cast<uint64_t*>(base) = 0x52554e54494d454full; // "RUNTIMEO"
	Check(test,
	      Libs::LibKernel::Memory::ProtectGuestMemory(base, SceKernelPageSize,
	                                                  Common::VirtualMemory::Mode::Read),
	      "runtime protection failed");
	Check(test, Libs::LibKernel::Memory::FreeGuestMemory(base, SceKernelPageSize * 2),
	      "runtime free failed");
	Check(test, Libs::LibKernel::Memory::TestPlaceholderRangeIsFree(base, SceKernelPageSize * 2),
	      "runtime free did not restore the owner placeholder");

	const auto reused = Libs::LibKernel::Memory::AllocateRuntimeMemory(
	    base, SceKernelPageSize * 2, Common::VirtualMemory::Mode::ReadWrite, "runtime_reuse", true);
	Check(test, reused == base, "fixed runtime allocation did not reuse the owner placeholder");
	Check(test, Libs::LibKernel::Memory::FreeGuestMemory(reused, SceKernelPageSize * 2),
	      "reused runtime free failed");

	const auto adjacent_first = Libs::LibKernel::Memory::AllocateRuntimeMemory(
	    0, SceKernelPageSize, Common::VirtualMemory::Mode::ReadWrite, "runtime_adjacent_first");
	Check(test, adjacent_first != 0, "first adjacent runtime allocation failed");
	const auto adjacent_second = Libs::LibKernel::Memory::AllocateRuntimeMemory(
	    adjacent_first + SceKernelPageSize, SceKernelPageSize,
	    Common::VirtualMemory::Mode::ReadWrite, "runtime_adjacent_second", true);
	Check(test, adjacent_second == adjacent_first + SceKernelPageSize,
	      "second adjacent runtime allocation failed");
	Check(test, Libs::LibKernel::Memory::FreeGuestMemory(adjacent_first, SceKernelPageSize * 2),
	      "combined adjacent runtime free failed");
	Check(
	    test,
	    Libs::LibKernel::Memory::TestPlaceholderRangeIsFree(adjacent_first, SceKernelPageSize * 2),
	    "combined adjacent runtime free did not restore one owner placeholder");

	std::printf("[host]    %-48s ok\n", test);
}

void TestFlexibleMapQueryAndWholeMunmap() {
	const char* test     = "FlexibleMapQueryAndWholeMunmap";
	const auto  baseline = AvailableFlexibleMemory(test);
	const auto  size     = SceKernelPageSize * 2;
	const auto  base     = MapNamedFlexible(test, size, SceKernelProtCpuRw, "prospero_flex");

	ExpectRange(test, Query(test, base), base, base + size, SceKernelProtCpuRw, 1, 0, 0, 1,
	            "prospero_flex");
	Check(test, AvailableFlexibleMemory(test) + size == baseline,
	      "flexible allocation should consume Prospero-reported flexible budget");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, size), "KernelMunmap");
	ExpectUnmapped(test, base);
	Check(test, AvailableFlexibleMemory(test) == baseline,
	      "whole munmap should return flexible memory to Prospero-reported budget");

	std::printf("[host]    %-48s ok\n", test);
}

void TestPartialFlexibleMunmapAndFindNext() {
	const char* test     = "PartialFlexibleMunmapAndFindNext";
	const auto  baseline = AvailableFlexibleMemory(test);
	const auto  base =
	    MapNamedFlexible(test, SceKernelPageSize * 3, SceKernelProtCpuRw, "prospero_part");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(base + SceKernelPageSize, SceKernelPageSize),
	        "KernelMunmap(middle page)");

	ExpectRange(test, Query(test, base), base, base + SceKernelPageSize, SceKernelProtCpuRw, 1, 0,
	            0, 1, "prospero_part");
	ExpectUnmapped(test, base + SceKernelPageSize);
	ExpectRange(test, Query(test, base + SceKernelPageSize, SceKernelVqFindNext),
	            base + SceKernelPageSize * 2, base + SceKernelPageSize * 3, SceKernelProtCpuRw, 1,
	            0, 0, 1, "prospero_part");
	Check(test, AvailableFlexibleMemory(test) + SceKernelPageSize * 2 == baseline,
	      "partial munmap should return only the unmapped flexible page");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelPageSize),
	        "KernelMunmap(left cleanup)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(base + SceKernelPageSize * 2, SceKernelPageSize),
	        "KernelMunmap(right cleanup)");
	Check(test, AvailableFlexibleMemory(test) == baseline,
	      "cleanup should return all flexible memory to Prospero-reported budget");

	std::printf("[host]    %-48s ok\n", test);
}

void TestReserveMapFixedAndNoOverwrite() {
	const char* test = "ReserveMapFixedAndNoOverwrite";
	void*       addr = nullptr;

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReserveVirtualRange(&addr, SceKernelPageSize * 3, 0,
	                                                           SceKernelPageSize),
	        "KernelReserveVirtualRange");
	const auto base = reinterpret_cast<uint64_t>(addr);

	ExpectRange(test, Query(test, base), base, base + SceKernelPageSize * 3, 0, 0, 0, 0, 0);

	void* fixed = reinterpret_cast<void*>(base + SceKernelPageSize);
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(
	            &fixed, SceKernelPageSize, SceKernelProtCpuRead, SceKernelMapFixed, "fixed_mid"),
	        "KernelMapNamedFlexibleMemory(fixed)");
	Check(test, reinterpret_cast<uint64_t>(fixed) == base + SceKernelPageSize,
	      "MAP_FIXED mapping moved");

	ExpectRange(test, Query(test, base), base, base + SceKernelPageSize, 0, 0, 0, 0, 0);
	ExpectRange(test, Query(test, base + SceKernelPageSize), base + SceKernelPageSize,
	            base + SceKernelPageSize * 2, SceKernelProtCpuRead, 1, 0, 0, 1, "fixed_mid");
	ExpectRange(test, Query(test, base + SceKernelPageSize * 2), base + SceKernelPageSize * 2,
	            base + SceKernelPageSize * 3, 0, 0, 0, 0, 0);

	void* blocked = reinterpret_cast<void*>(base + SceKernelPageSize);
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(
	                &blocked, SceKernelPageSize, SceKernelProtCpuRw,
	                SceKernelMapFixed | SceKernelMapNoOverwrite, "blocked"),
	            "KernelMapNamedFlexibleMemory(MAP_FIXED|MAP_NO_OVERWRITE)");
	ExpectRange(test, Query(test, base + SceKernelPageSize), base + SceKernelPageSize,
	            base + SceKernelPageSize * 2, SceKernelProtCpuRead, 1, 0, 0, 1, "fixed_mid");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelPageSize),
	        "KernelMunmap(left reserve cleanup)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(base + SceKernelPageSize, SceKernelPageSize),
	        "KernelMunmap(fixed cleanup)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(base + SceKernelPageSize * 2, SceKernelPageSize),
	        "KernelMunmap(right reserve cleanup)");

	std::printf("[host]    %-48s ok\n", test);
}

void TestFixedNoOverwriteRejectsReservedRange() {
	const char* test = "FixedNoOverwriteRejectsReservedRange";
	void*       addr = nullptr;

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReserveVirtualRange(&addr, SceKernelPageSize, 0,
	                                                           SceKernelPageSize),
	        "KernelReserveVirtualRange");
	const auto base = reinterpret_cast<uint64_t>(addr);

	ExpectRange(test, Query(test, base), base, base + SceKernelPageSize, 0, 0, 0, 0, 0);

	void*     fixed = reinterpret_cast<void*>(base);
	const int ret   = Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(
	    &fixed, SceKernelPageSize, SceKernelProtCpuRw, SceKernelMapFixed | SceKernelMapNoOverwrite,
	    "reserved_blocked");
	const bool rejected = ret < OK;

	if (ret == OK) {
		CheckOk(test,
		        Libs::LibKernel::Memory::KernelMunmap(reinterpret_cast<uint64_t>(fixed),
		                                              SceKernelPageSize),
		        "KernelMunmap(unexpected fixed map cleanup)");
		if (reinterpret_cast<uint64_t>(fixed) != base) {
			CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelPageSize),
			        "KernelMunmap(reserve cleanup)");
		}
	} else {
		CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelPageSize),
		        "KernelMunmap(reserve cleanup)");
	}

	Check(test, rejected,
	      "MAP_FIXED|MAP_NO_OVERWRITE should reject an already reserved virtual "
	      "range");

	std::printf("[host]    %-48s ok\n", test);
}

void TestDirectMapQueryOffsetAndPartialMunmap() {
	const char* test = "DirectMapQueryOffsetAndPartialMunmap";

	int64_t phys_addr = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            SceKernelDirectMemoryStart, Libs::LibKernel::Memory::KernelGetDirectMemorySize(),
	            SceKernelPageSize * 4, SceKernelPageSize, SceKernelMtypeC, &phys_addr),
	        "KernelAllocateDirectMemory");

	void* addr = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &addr, SceKernelPageSize * 4, SceKernelProtCpuRw, 0, phys_addr, SceKernelPageSize,
	            "prospero_direct"),
	        "KernelMapNamedDirectMemory");
	const auto base = reinterpret_cast<uint64_t>(addr);
	const auto phys = static_cast<uint64_t>(phys_addr);
	Check(test, Libs::LibKernel::Memory::TestGuestAddressRangeIsOwned(base, SceKernelPageSize * 4),
	      "direct mapping escaped the guest owner");
	void* alias = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &alias, SceKernelPageSize * 4, SceKernelProtCpuRw, 0, phys_addr, SceKernelPageSize,
	            "prospero_direct_alias"),
	        "KernelMapNamedDirectMemory(alias)");
	const auto alias_base = reinterpret_cast<uint64_t>(alias);

	constexpr uint64_t alias_test_value = 0x4b595459444d454dull; // "KYTYDMEM"
	*reinterpret_cast<uint64_t*>(base)  = alias_test_value;
	Check(test, *reinterpret_cast<const uint64_t*>(alias_base) == alias_test_value,
	      "direct mappings of the same physical offset must share backing storage");
	uint64_t backing_read = 0;
	Check(test, Libs::LibKernel::Memory::TryReadBacking(base, &backing_read, sizeof(backing_read)),
	      "TryReadBacking should resolve a direct mapping");
	Check(test, backing_read == alias_test_value,
	      "TryReadBacking should observe the physical backing bytes");
	constexpr uint64_t backing_write = 0x524541444241434bull; // "READBACK"
	Check(test,
	      Libs::LibKernel::Memory::TryWriteBacking(alias_base + sizeof(uint64_t), &backing_write,
	                                               sizeof(backing_write)),
	      "TryWriteBacking should resolve a direct alias");
	backing_read = 0;
	Check(test,
	      Libs::LibKernel::Memory::TryReadBacking(base + sizeof(uint64_t), &backing_read,
	                                              sizeof(backing_read)),
	      "TryReadBacking should resolve an aliased physical offset");
	Check(test, backing_read == backing_write,
	      "backing reads and writes should preserve direct-memory aliasing");

	auto info = Query(test, base);
	ExpectRange(test, info, base, base + SceKernelPageSize * 4, SceKernelProtCpuRw, 0, 1, 0, 1,
	            "prospero_direct", phys);
	Check(test, info.memory_type == SceKernelMtypeC, "unexpected direct memory type");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(base + SceKernelPageSize, SceKernelPageSize),
	        "KernelMunmap(direct middle page)");
	ExpectUnmapped(test, base + SceKernelPageSize);

	constexpr uint64_t transaction_sentinel = 0x5452414e53414354ull; // "TRANSACT"
	constexpr uint64_t rejected_write       = 0x4e4f504152544941ull; // "NOPARTIA"
	const auto         crossing_address     = base + SceKernelPageSize - sizeof(uint32_t);
	std::memcpy(reinterpret_cast<void*>(alias_base + SceKernelPageSize - sizeof(uint32_t)),
	            &transaction_sentinel, sizeof(transaction_sentinel));
	Check(test,
	      !Libs::LibKernel::Memory::TryWriteBacking(crossing_address, &rejected_write,
	                                                sizeof(rejected_write)),
	      "TryWriteBacking should reject a range crossing an unmapped span");
	uint64_t backing_after_rejected_write = 0;
	std::memcpy(&backing_after_rejected_write,
	            reinterpret_cast<const void*>(alias_base + SceKernelPageSize - sizeof(uint32_t)),
	            sizeof(backing_after_rejected_write));
	Check(test, backing_after_rejected_write == transaction_sentinel,
	      "failed backing writes must not modify a validated prefix");
	uint64_t rejected_read = transaction_sentinel;
	Check(test,
	      !Libs::LibKernel::Memory::TryReadBacking(crossing_address, &rejected_read,
	                                               sizeof(rejected_read)),
	      "TryReadBacking should reject a range crossing an unmapped span");
	Check(test, rejected_read == transaction_sentinel,
	      "failed backing reads must not modify a destination prefix");
	Check(test,
	      Libs::LibKernel::Memory::ClampRangeSize(base + SceKernelPageSize - 0xf30, 0x1560) ==
	          0xf30,
	      "ClampRangeSize did not stop at an unmapped span");

	info = Query(test, base + SceKernelPageSize, SceKernelVqFindNext);
	ExpectRange(test, info, base + SceKernelPageSize * 2, base + SceKernelPageSize * 4,
	            SceKernelProtCpuRw, 0, 1, 0, 1, "prospero_direct", phys + SceKernelPageSize * 2);
	Check(test, info.memory_type == SceKernelMtypeC, "unexpected right direct memory type");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelPageSize),
	        "KernelMunmap(direct left cleanup)");
	CheckOk(
	    test,
	    Libs::LibKernel::Memory::KernelMunmap(base + SceKernelPageSize * 2, SceKernelPageSize * 2),
	    "KernelMunmap(direct right cleanup)");
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(alias_base, SceKernelPageSize * 4),
	        "KernelMunmap(direct alias cleanup)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReleaseDirectMemory(phys_addr, SceKernelPageSize * 4),
	        "KernelReleaseDirectMemory");

	std::printf("[host]    %-48s ok\n", test);
}

void TestDirectPartialProtectUnmapPreservesNeighbors() {
	const char* test      = "DirectPartialProtectUnmapPreservesNeighbors";
	const auto  size      = SceKernelPageSize * 3;
	int64_t     phys_addr = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(), size, SceKernelPageSize,
	            SceKernelMtypeC, &phys_addr),
	        "KernelAllocateDirectMemory");

	void* address = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(&address, size, SceKernelProtCpuRw,
	                                                            0, phys_addr, SceKernelPageSize,
	                                                            "partial_protect_direct"),
	        "KernelMapNamedDirectMemory");
	const auto base = reinterpret_cast<uint64_t>(address);
	CheckOk(
	    test,
	    Libs::LibKernel::Memory::KernelMprotect(reinterpret_cast<void*>(base + SceKernelPageSize),
	                                            SceKernelPageSize, SceKernelProtCpuRead),
	    "KernelMprotect(middle)");
	Check(test,
	      Libs::LibKernel::Memory::ProtectGuestHostMemory(base, size,
	                                                      Common::VirtualMemory::Mode::Read),
	      "owner could not protect fragmented backing views");
	Check(test,
	      Libs::LibKernel::Memory::ProtectGuestHostMemory(base, size,
	                                                      Common::VirtualMemory::Mode::ReadWrite),
	      "owner could not restore fragmented backing views");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(base + SceKernelPageSize, SceKernelPageSize),
	        "KernelMunmap(middle)");

	Common::VirtualMemory::Mode old_left {};
	Common::VirtualMemory::Mode old_right {};
	Check(test,
	      Common::VirtualMemory::Protect(base, SceKernelPageSize,
	                                     Common::VirtualMemory::Mode::ReadWrite, &old_left),
	      "could not inspect left-page protection");
	Check(test,
	      Common::VirtualMemory::Protect(base + SceKernelPageSize * 2, SceKernelPageSize,
	                                     Common::VirtualMemory::Mode::ReadWrite, &old_right),
	      "could not inspect right-page protection");
	Check(test, old_left == Common::VirtualMemory::Mode::ReadWrite,
	      "partial unmap changed the left neighbor protection");
	Check(test, old_right == Common::VirtualMemory::Mode::ReadWrite,
	      "partial unmap changed the right neighbor protection");
	*reinterpret_cast<uint64_t*>(base) = 0x4c45465450524f54ull; // "LEFTPROT"
	*reinterpret_cast<uint64_t*>(base + SceKernelPageSize * 2) =
	    0x5247485450524f54ull; // "RGHTPROT"

	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(phys_addr, size),
	        "KernelReleaseDirectMemory");
	ExpectUnmapped(test, base);
	ExpectUnmapped(test, base + SceKernelPageSize * 2);

	std::printf("[host]    %-48s ok\n", test);
}

void TestDirectProtectionMaskContract() {
	const char* test      = "DirectProtectionMaskContract";
	int64_t     phys_addr = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(), SceKernelPageSize,
	            SceKernelPageSize, SceKernelMtypeC, &phys_addr),
	        "KernelAllocateDirectMemory");

	constexpr int InvalidProtectionBit = 0x400;
	void*         rejected             = nullptr;
	const int     invalid_result       = Libs::LibKernel::Memory::KernelMapDirectMemory(
	    &rejected, SceKernelPageSize, SceKernelProtCpuRead | InvalidProtectionBit, 0, phys_addr,
	    SceKernelPageSize);
	Check(test, invalid_result == Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "mixed valid and unsupported protection bits were accepted");
	Check(test, rejected == nullptr, "rejected protection changed the output address");

	constexpr int EngineProtection = SceKernelProtAmprRead | SceKernelProtAmprWrite |
	                                 SceKernelProtAcpRead | SceKernelProtAcpWrite;
	void* address = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &address, SceKernelPageSize, EngineProtection, 0, phys_addr, SceKernelPageSize,
	            "engine_protection"),
	        "KernelMapNamedDirectMemory(engine-only protection)");
	const auto base = reinterpret_cast<uint64_t>(address);
	ExpectRange(test, Query(test, base), base, base + SceKernelPageSize, EngineProtection, 0, 1, 0,
	            1, "engine_protection", static_cast<uint64_t>(phys_addr));

	void* protection_start = nullptr;
	void* protection_end   = nullptr;
	int   protection       = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelQueryMemoryProtection(address, &protection_start,
	                                                             &protection_end, &protection),
	        "KernelQueryMemoryProtection(engine-only protection)");
	Check(test,
	      protection_start == address &&
	          reinterpret_cast<uint64_t>(protection_end) == base + SceKernelPageSize &&
	          protection == EngineProtection,
	      "engine protection was not preserved by protection query");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelPageSize), "KernelMunmap");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(phys_addr, SceKernelPageSize),
	        "KernelReleaseDirectMemory");

	std::printf("[host]    %-48s ok\n", test);
}

void TestDirectMapValidationBeforeOwnerMutation() {
	const char* test    = "DirectMapValidationBeforeOwnerMutation";
	int64_t     invalid = -1;
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	                0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(), SceKernelPageSize + 1,
	                SceKernelPageSize, SceKernelMtypeC, &invalid),
	            "KernelAllocateDirectMemory(unaligned size)");
	Check(test, invalid == -1, "invalid direct allocation changed the output address");
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	                0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(), SceKernelPageSize,
	                0x1000, SceKernelMtypeC, &invalid),
	            "KernelAllocateDirectMemory(sub-page alignment)");
	Check(test, invalid == -1, "invalid alignment changed the output address");

	int64_t phys_addr = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(), SceKernelPageSize * 2,
	            SceKernelPageSize, SceKernelMtypeC, &phys_addr),
	        "KernelAllocateDirectMemory");

	auto expect_invalid = [&](size_t len, int prot, int flags, int64_t phys, size_t alignment,
	                          const char* action) {
		void* address = nullptr;
		CheckFailed(test,
		            Libs::LibKernel::Memory::KernelMapDirectMemory(&address, len, prot, flags, phys,
		                                                           alignment),
		            action);
		Check(test, address == nullptr, "invalid direct map changed the output address");
	};
	expect_invalid(SceKernelPageSize + 1, SceKernelProtCpuRw, 0, phys_addr, SceKernelPageSize,
	               "KernelMapDirectMemory(unaligned size)");
	expect_invalid(SceKernelPageSize, SceKernelProtCpuRw, 0, phys_addr + 1, SceKernelPageSize,
	               "KernelMapDirectMemory(unaligned physical address)");
	expect_invalid(SceKernelPageSize, SceKernelProtCpuExec, 0, phys_addr, SceKernelPageSize,
	               "KernelMapDirectMemory(executable)");

	void* aligned = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapDirectMemory(
	            &aligned, SceKernelPageSize, SceKernelProtCpuRw, 0, phys_addr, 0xc000),
	        "KernelMapDirectMemory(16K-multiple alignment)");
	Check(test, reinterpret_cast<uint64_t>(aligned) % 0xc000 == 0,
	      "non-power-of-two 16K alignment was not honored");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(reinterpret_cast<uint64_t>(aligned),
	                                              SceKernelPageSize),
	        "KernelMunmap(16K-multiple alignment)");

	void* ignored_flag = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapDirectMemory(&ignored_flag, SceKernelPageSize,
	                                                       SceKernelProtCpuRw, 0x08, phys_addr,
	                                                       SceKernelPageSize),
	        "KernelMapDirectMemory(ignored flag)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(reinterpret_cast<uint64_t>(ignored_flag),
	                                              SceKernelPageSize),
	        "KernelMunmap(ignored flag)");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReleaseDirectMemory(phys_addr, SceKernelPageSize * 2),
	        "KernelReleaseDirectMemory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestDirectReleaseRollbackRestoresOwnerMapping() {
	const char* test      = "DirectReleaseRollbackRestoresOwnerMapping";
	int64_t     phys_addr = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(), SceKernelPageSize,
	            SceKernelPageSize, SceKernelMtypeC, &phys_addr),
	        "KernelAllocateDirectMemory");
	void* address = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &address, SceKernelPageSize, SceKernelProtCpuRw, 0, phys_addr, SceKernelPageSize,
	            "release_rollback"),
	        "KernelMapNamedDirectMemory");
	const auto base                    = reinterpret_cast<uint64_t>(address);
	*reinterpret_cast<uint64_t*>(base) = 0x52454c524f4c4c42ull; // "RELROLLB"

	Libs::LibKernel::Memory::TestFailNextPhysicalMemoryUnmap();
	CheckFailed(
	    test,
	    Libs::LibKernel::Memory::KernelCheckedReleaseDirectMemory(phys_addr, SceKernelPageSize),
	    "KernelCheckedReleaseDirectMemory(injected failure)");
	ExpectRange(test, Query(test, base), base, base + SceKernelPageSize, SceKernelProtCpuRw, 0, 1,
	            0, 1, "release_rollback", static_cast<uint64_t>(phys_addr));
	Check(test, *reinterpret_cast<uint64_t*>(base) == 0x52454c524f4c4c42ull,
	      "release rollback lost the shared-backing contents");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelCheckedReleaseDirectMemory(phys_addr, SceKernelPageSize),
	        "KernelCheckedReleaseDirectMemory(retry)");
	ExpectUnmapped(test, base);
	std::printf("[host]    %-48s ok\n", test);
}

void TestDirectReleaseContracts() {
	const char* test = "DirectReleaseContracts";
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(0, 0),
	        "KernelReleaseDirectMemory(zero length)");
	CheckOk(test, Libs::LibKernel::Memory::KernelCheckedReleaseDirectMemory(0, 0),
	        "KernelCheckedReleaseDirectMemory(zero length)");
	CheckFailed(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(1, SceKernelPageSize),
	            "KernelReleaseDirectMemory(unaligned start)");
	CheckFailed(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(0, SceKernelPageSize + 1),
	            "KernelReleaseDirectMemory(unaligned size)");

	const auto free_offset = static_cast<int64_t>(
	    Libs::LibKernel::Memory::KernelGetDirectMemorySize() - SceKernelPageSize);
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReleaseDirectMemory(free_offset, SceKernelPageSize),
	        "KernelReleaseDirectMemory(unallocated range)");
	Check(test,
	      Libs::LibKernel::Memory::KernelCheckedReleaseDirectMemory(
	          free_offset, SceKernelPageSize) == Libs::LibKernel::KERNEL_ERROR_ENOENT,
	      "checked release did not report an unallocated range");

	std::printf("[host]    %-48s ok\n", test);
}

void TestReleasedReserveCanBeReused() {
	const char* test = "ReleasedReserveCanBeReused";
	void*       addr = nullptr;

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReserveVirtualRange(&addr, SceKernelPageSize, 0,
	                                                           SceKernelPageSize),
	        "KernelReserveVirtualRange");
	const auto base = reinterpret_cast<uint64_t>(addr);
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelPageSize), "KernelMunmap");

	void* reused = reinterpret_cast<void*>(base);
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReserveVirtualRange(
	            &reused, SceKernelPageSize, SceKernelMapFixed | SceKernelMapNoOverwrite,
	            SceKernelPageSize),
	        "KernelReserveVirtualRange(reuse)");
	Check(test, reinterpret_cast<uint64_t>(reused) == base,
	      "released host reservation was not reusable at the same address");
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelPageSize),
	        "KernelMunmap(reuse cleanup)");

	std::printf("[host]    %-48s ok\n", test);
}

void TestMunmapAcrossAdjacentFlexibleMappings() {
	const char* test     = "MunmapAcrossAdjacentFlexibleMappings";
	const auto  baseline = AvailableFlexibleMemory(test);
	void*       reserve  = nullptr;

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReserveVirtualRange(&reserve, SceKernelPageSize * 2, 0,
	                                                           SceKernelPageSize),
	        "KernelReserveVirtualRange");
	const auto base = reinterpret_cast<uint64_t>(reserve);

	void* left  = reinterpret_cast<void*>(base);
	void* right = reinterpret_cast<void*>(base + SceKernelPageSize);
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(
	            &left, SceKernelPageSize, SceKernelProtCpuRw, SceKernelMapFixed, "adjacent_left"),
	        "KernelMapNamedFlexibleMemory(left)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(
	            &right, SceKernelPageSize, SceKernelProtCpuRw, SceKernelMapFixed, "adjacent_right"),
	        "KernelMapNamedFlexibleMemory(right)");

	Check(test,
	      Libs::LibKernel::Memory::ClampRangeSize(base + SceKernelPageSize - 0x100, 0x200) == 0x200,
	      "ClampRangeSize did not cross adjacent committed mappings");
	Check(test,
	      Libs::LibKernel::Memory::ProtectGuestHostMemory(base, SceKernelPageSize * 2,
	                                                      Common::VirtualMemory::Mode::Read),
	      "owner could not protect adjacent backing mappings");
	Check(test,
	      Libs::LibKernel::Memory::ProtectGuestHostMemory(base, SceKernelPageSize * 2,
	                                                      Common::VirtualMemory::Mode::ReadWrite),
	      "owner could not restore adjacent backing mappings");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelPageSize * 2),
	        "KernelMunmap(adjacent mappings)");
	Check(test, AvailableFlexibleMemory(test) == baseline,
	      "multi-range unmap leaked flexible-memory budget");
	ExpectUnmapped(test, base);
	ExpectUnmapped(test, base + SceKernelPageSize);

	std::printf("[host]    %-48s ok\n", test);
}

void TestNonzeroDirectOffsetAliasesSharedBacking() {
	const char* test = "NonzeroDirectOffsetAliasesSharedBacking";

	int64_t first  = 0;
	int64_t second = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(), SceKernelPageSize,
	            SceKernelPageSize, SceKernelMtypeC, &first),
	        "KernelAllocateDirectMemory(first)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(), SceKernelPageSize,
	            SceKernelPageSize, SceKernelMtypeC, &second),
	        "KernelAllocateDirectMemory(second)");
	Check(test, second == first + static_cast<int64_t>(SceKernelPageSize),
	      "second allocation should use a nonzero 16 KiB offset");

	void* first_alias  = nullptr;
	void* second_alias = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &first_alias, SceKernelPageSize, SceKernelProtCpuRw, 0, second, SceKernelPageSize,
	            "prospero_nonzero_a"),
	        "KernelMapNamedDirectMemory(first alias)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &second_alias, SceKernelPageSize, SceKernelProtCpuRw, 0, second, SceKernelPageSize,
	            "prospero_nonzero_b"),
	        "KernelMapNamedDirectMemory(second alias)");

	*reinterpret_cast<uint64_t*>(first_alias) = 0x4b59545931364b42ull; // "KYTY16KB"
	Check(test, *reinterpret_cast<const uint64_t*>(second_alias) == 0x4b59545931364b42ull,
	      "nonzero-offset mappings must share backing storage");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(reinterpret_cast<uint64_t>(first_alias),
	                                              SceKernelPageSize),
	        "KernelMunmap(first alias)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(reinterpret_cast<uint64_t>(second_alias),
	                                              SceKernelPageSize),
	        "KernelMunmap(second alias)");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(second, SceKernelPageSize),
	        "KernelReleaseDirectMemory(second)");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(first, SceKernelPageSize),
	        "KernelReleaseDirectMemory(first)");

	std::printf("[host]    %-48s ok\n", test);
}

void TestDirectMapAcrossContiguousAllocations() {
	const char* test   = "DirectMapAcrossContiguousAllocations";
	const auto  end    = Libs::LibKernel::Memory::KernelGetDirectMemorySize();
	int64_t     first  = 0;
	int64_t     second = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, end, SceKernelPageSize, SceKernelPageSize, SceKernelMtypeC, &first),
	        "KernelAllocateDirectMemory(first)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, end, SceKernelPageSize, SceKernelPageSize, SceKernelMtypeC, &second),
	        "KernelAllocateDirectMemory(second)");
	Check(test, second == first + static_cast<int64_t>(SceKernelPageSize),
	      "test allocations are not physically contiguous");

	void* mapping = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &mapping, SceKernelPageSize * 2, SceKernelProtCpuRw, 0, first, SceKernelPageSize,
	            "contiguous_allocations"),
	        "KernelMapNamedDirectMemory");
	auto* words = reinterpret_cast<uint64_t*>(mapping);
	words[0]    = 0x434f4e5449474c46ull; // "CONTIGLF"
	*reinterpret_cast<uint64_t*>(reinterpret_cast<uint64_t>(mapping) + SceKernelPageSize) =
	    0x434f4e5449475254ull; // "CONTIGRT"

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelCheckedReleaseDirectMemory(first, SceKernelPageSize * 2),
	        "KernelCheckedReleaseDirectMemory(contiguous span)");
	ExpectUnmapped(test, reinterpret_cast<uint64_t>(mapping));

	int64_t reclaimed = -1;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, end, SceKernelPageSize * 2, SceKernelPageSize, SceKernelMtypeC, &reclaimed),
	        "KernelAllocateDirectMemory(reclaimed)");
	Check(test, reclaimed == first, "released contiguous span was not coalesced");
	CheckOk(
	    test,
	    Libs::LibKernel::Memory::KernelCheckedReleaseDirectMemory(reclaimed, SceKernelPageSize * 2),
	    "KernelCheckedReleaseDirectMemory(reclaimed)");

	std::printf("[host]    %-48s ok\n", test);
}

void TestDirectPhysicalFreeRangeReuseAndCoalescing() {
	const char* test = "DirectPhysicalFreeRangeReuseAndCoalescing";
	const auto  end  = Libs::LibKernel::Memory::KernelGetDirectMemorySize();

	int64_t first = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, end, SceKernelPageSize * 3, SceKernelPageSize, SceKernelMtypeC, &first),
	        "KernelAllocateDirectMemory(first)");
	const auto middle = first + static_cast<int64_t>(SceKernelPageSize);
	const auto last   = middle + static_cast<int64_t>(SceKernelPageSize);

	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(middle, SceKernelPageSize),
	        "KernelReleaseDirectMemory(middle split)");
	int64_t reused = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, end, SceKernelPageSize, SceKernelPageSize, SceKernelMtypeC, &reused),
	        "KernelAllocateDirectMemory(reused)");
	Check(test, reused == middle, "released physical gap was not reused");

	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(first, SceKernelPageSize),
	        "KernelReleaseDirectMemory(left split)");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(reused, SceKernelPageSize),
	        "KernelReleaseDirectMemory(reused)");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(last, SceKernelPageSize),
	        "KernelReleaseDirectMemory(right split)");

	int64_t coalesced = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, end, SceKernelPageSize * 3, SceKernelPageSize, SceKernelMtypeC, &coalesced),
	        "KernelAllocateDirectMemory(coalesced)");
	Check(test, coalesced == first, "adjacent released physical ranges were not coalesced");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReleaseDirectMemory(coalesced, SceKernelPageSize * 3),
	        "KernelReleaseDirectMemory(coalesced)");

	std::printf("[host]    %-48s ok\n", test);
}

void TestDirectAlignmentStaysWithinSearchRange() {
	const char*        test         = "DirectAlignmentStaysWithinSearchRange";
	constexpr int64_t  search_start = SceKernelPageSize * 2;
	constexpr uint64_t alignment    = SceKernelPageSize * 3;
	const auto         search_end   = Libs::LibKernel::Memory::KernelGetDirectMemorySize();

	int64_t phys_addr = -1;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(search_start, search_end,
	                                                            SceKernelPageSize, alignment,
	                                                            SceKernelMtypeC, &phys_addr),
	        "KernelAllocateDirectMemory(non-power-of-two alignment)");
	Check(test, phys_addr >= search_start, "aligned allocation escaped below search_start");
	Check(test, static_cast<uint64_t>(phys_addr) % alignment == 0,
	      "allocation did not honor the requested alignment");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(phys_addr, SceKernelPageSize),
	        "KernelReleaseDirectMemory");

	constexpr size_t out_of_range_alignment = UINT64_MAX - (SceKernelPageSize - 1);
	phys_addr                               = -1;
	const int result                        = Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	    search_start, search_end, SceKernelPageSize, out_of_range_alignment, SceKernelMtypeC,
	    &phys_addr);
	CheckFailed(test, result, "KernelAllocateDirectMemory(out-of-range alignment)");
	Check(test, phys_addr == -1, "failed allocation modified physAddrOut");

	std::printf("[host]    %-48s ok\n", test);
}

void TestDefaultDirectMapUsesSystemAddressRange() {
	const char* test = "DefaultDirectMapUsesSystemAddressRange";

	int64_t phys_addr = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(), SceKernelPageSize,
	            SceKernelPageSize, SceKernelMtypeC, &phys_addr),
	        "KernelAllocateDirectMemory");

	void* address = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(&address, SceKernelPageSize,
	                                                            SceKernelProtCpuRw, 0, phys_addr,
	                                                            SceKernelPageSize, "system_direct"),
	        "KernelMapNamedDirectMemory");
	Check(test, address != nullptr, "direct mapping returned null");
#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	constexpr uint64_t SystemManagedMin = 0x0000040000ull;
	constexpr uint64_t SystemManagedMax = 0x07fffeffffull;
	const auto         mapped           = reinterpret_cast<uint64_t>(address);
	Check(test, mapped >= SystemManagedMin && mapped + SceKernelPageSize - 1 <= SystemManagedMax,
	      "default direct mapping fell outside the system-managed host range");
#endif

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(reinterpret_cast<uint64_t>(address),
	                                              SceKernelPageSize),
	        "KernelMunmap");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(phys_addr, SceKernelPageSize),
	        "KernelReleaseDirectMemory");

	std::printf("[host]    %-48s ok\n", test);
}

void TestLargeDirectMapAliasesAcrossChunks() {
	const char*        test     = "LargeDirectMapAliasesAcrossChunks";
	constexpr uint64_t size     = 0x400000;
	constexpr uint64_t boundary = 0x200000;

	int64_t phys_addr = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(), size, 0x10000,
	            SceKernelMtypeC, &phys_addr),
	        "KernelAllocateDirectMemory");

	void* first_alias  = nullptr;
	void* second_alias = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &first_alias, size, SceKernelProtCpuRw, 0, phys_addr, 0x10000, "large_direct_a"),
	        "KernelMapNamedDirectMemory(first alias)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &second_alias, size, SceKernelProtCpuRw, 0, phys_addr, 0x10000, "large_direct_b"),
	        "KernelMapNamedDirectMemory(second alias)");

	auto* first                                        = static_cast<uint8_t*>(first_alias);
	auto* second                                       = static_cast<uint8_t*>(second_alias);
	*reinterpret_cast<uint64_t*>(first)                = 0x1111222233334444ull;
	*reinterpret_cast<uint64_t*>(first + boundary - 8) = 0x5555666677778888ull;
	*reinterpret_cast<uint64_t*>(first + boundary)     = 0x9999aaaabbbbccccull;
	*reinterpret_cast<uint64_t*>(first + size - 8)     = 0xddddeeeeffff0001ull;
	Check(test,
	      *reinterpret_cast<const uint64_t*>(second) == 0x1111222233334444ull &&
	          *reinterpret_cast<const uint64_t*>(second + boundary - 8) == 0x5555666677778888ull &&
	          *reinterpret_cast<const uint64_t*>(second + boundary) == 0x9999aaaabbbbccccull &&
	          *reinterpret_cast<const uint64_t*>(second + size - 8) == 0xddddeeeeffff0001ull,
	      "large direct aliases diverged at a mapping chunk boundary");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(reinterpret_cast<uint64_t>(first_alias), size),
	        "KernelMunmap(first alias)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(reinterpret_cast<uint64_t>(second_alias), size),
	        "KernelMunmap(second alias)");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(phys_addr, size),
	        "KernelReleaseDirectMemory");

	std::printf("[host]    %-48s ok\n", test);
}

void TestHintlessDirectMapUsesCanonicalGuestBase() {
	// Mirrors the allocation Sony's libc.prx makes for its internal heap: 4 MiB of
	// direct memory, 2 MiB aligned, mapped with no address hint. The PS5 kernel never
	// places hint-less user mappings below 0x200000000 and guest code relies on that
	// (libc fails its mspace setup for a lower heap address, and the first malloc then
	// dereferences a null mspace). Writes through the mapping must also stick.
	const char* test = "HintlessDirectMapUsesCanonicalGuestBase";

	constexpr uint64_t Len   = 0x400000;
	constexpr uint64_t Align = 0x200000;

	int64_t phys_addr = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(0, 0x260000000ull, Len, Align, 12,
	                                                            &phys_addr),
	        "KernelAllocateDirectMemory");

	void* address = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(&address, Len, SceKernelProtCpuRw,
	                                                            0, phys_addr, Align, "libc_heap"),
	        "KernelMapNamedDirectMemory");
	const auto base = reinterpret_cast<uint64_t>(address);
	{
		char message[128] = {};
		std::snprintf(message, sizeof(message),
		              "hint-less direct map landed below the PS5 base: 0x%016" PRIx64, base);
		Check(test, base >= 0x200000000ull, message);
	}

	auto* header = reinterpret_cast<uint64_t*>(base);
	header[0]    = 0x4d53504143453030ull; // "MSPACE00"
	header[7]    = 0x58585858ull;         // magic at +0x38, like the libc mspace
	*reinterpret_cast<uint64_t*>(base + Len - 8) = 0x454e444d41524bull;

	Check(test, header[0] == 0x4d53504143453030ull, "immediate readback of header[0] failed");
	Check(test, header[7] == 0x58585858ull, "immediate readback of header[7] failed");
	Check(test, *reinterpret_cast<const uint64_t*>(base + Len - 8) == 0x454e444d41524bull,
	      "immediate readback of tail failed");

	uint64_t backing = 0;
	Check(test, Libs::LibKernel::Memory::TryReadBacking(base + 0x38, &backing, sizeof(backing)),
	      "TryReadBacking(header+0x38)");
	Check(test, backing == 0x58585858ull, "backing store does not see the guest write at +0x38");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, Len), "KernelMunmap");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(phys_addr, Len),
	        "KernelReleaseDirectMemory");

	std::printf("[host]    %-48s ok\n", test);
}

void TestDirectMemoryContentPersistsAcrossRemap() {
	const char* test = "DirectMemoryContentPersistsAcrossRemap";

	constexpr uint64_t MapSize = SceKernelPageSize * 4;

	int64_t phys_addr = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            SceKernelDirectMemoryStart, Libs::LibKernel::Memory::KernelGetDirectMemorySize(),
	            MapSize, SceKernelPageSize, SceKernelMtypeC, &phys_addr),
	        "KernelAllocateDirectMemory");

	// Direct memory is physical: contents must survive unmapping and remapping, including
	// a remap of a sub-range at a nonzero physical offset.
	void* address = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(&address, MapSize,
	                                                            SceKernelProtCpuRw, 0, phys_addr,
	                                                            SceKernelPageSize, "persist_a"),
	        "KernelMapNamedDirectMemory(first)");
	const auto base = reinterpret_cast<uint64_t>(address);
	for (uint64_t offset = 0; offset < MapSize; offset += sizeof(uint64_t)) {
		*reinterpret_cast<uint64_t*>(base + offset) = offset ^ 0x4b5954595045525aull; // "KYTYPERZ"
	}
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, MapSize), "KernelMunmap(first)");

	void* remap = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(&remap, MapSize,
	                                                            SceKernelProtCpuRw, 0, phys_addr,
	                                                            SceKernelPageSize, "persist_b"),
	        "KernelMapNamedDirectMemory(remap)");
	const auto remap_base = reinterpret_cast<uint64_t>(remap);
	for (uint64_t offset = 0; offset < MapSize; offset += sizeof(uint64_t)) {
		const auto expected = offset ^ 0x4b5954595045525aull;
		const auto actual   = *reinterpret_cast<const uint64_t*>(remap_base + offset);
		if (actual != expected) {
			char message[160] = {};
			std::snprintf(message, sizeof(message),
			              "content lost across remap at offset 0x%" PRIx64 ": expected 0x%016" PRIx64
			              ", read 0x%016" PRIx64,
			              offset, expected, actual);
			Fail(test, message);
		}
	}
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(remap_base, MapSize), "KernelMunmap(remap)");

	// Sub-range remap at a nonzero physical offset: page 2 of the original allocation.
	void* partial = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &partial, SceKernelPageSize, SceKernelProtCpuRw, 0,
	            phys_addr + static_cast<int64_t>(SceKernelPageSize * 2), SceKernelPageSize,
	            "persist_c"),
	        "KernelMapNamedDirectMemory(partial)");
	const auto partial_base = reinterpret_cast<uint64_t>(partial);
	for (uint64_t offset = 0; offset < SceKernelPageSize; offset += sizeof(uint64_t)) {
		const auto expected = (SceKernelPageSize * 2 + offset) ^ 0x4b5954595045525aull;
		const auto actual   = *reinterpret_cast<const uint64_t*>(partial_base + offset);
		if (actual != expected) {
			char message[160] = {};
			std::snprintf(message, sizeof(message),
			              "content lost in partial remap at offset 0x%" PRIx64
			              ": expected 0x%016" PRIx64 ", read 0x%016" PRIx64,
			              offset, expected, actual);
			Fail(test, message);
		}
	}
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(partial_base, SceKernelPageSize),
	        "KernelMunmap(partial)");

	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(phys_addr, MapSize),
	        "KernelReleaseDirectMemory");

	std::printf("[host]    %-48s ok\n", test);
}

void TestDirectMapUnmapReusesHostAddress() {
	const char* test = "DirectMapUnmapReusesHostAddress";

	int64_t phys_addr = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            SceKernelDirectMemoryStart, Libs::LibKernel::Memory::KernelGetDirectMemorySize(),
	            SceKernelPageSize, SceKernelPageSize, SceKernelMtypeC, &phys_addr),
	        "KernelAllocateDirectMemory");

	uint64_t first_address = 0;
	for (int iteration = 0; iteration < 64; iteration++) {
		void* address = nullptr;
		CheckOk(test,
		        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
		            &address, SceKernelPageSize, SceKernelProtCpuRw, 0, phys_addr,
		            SceKernelPageSize, "reuse_direct"),
		        "KernelMapNamedDirectMemory");
		const auto current_address = reinterpret_cast<uint64_t>(address);
		if (iteration == 0) {
			first_address = current_address;
		} else {
			char message[160] = {};
			std::snprintf(message, sizeof(message),
			              "direct map address changed from 0x%016" PRIx64 " to 0x%016" PRIx64,
			              first_address, current_address);
			Check(test, current_address == first_address, message);
		}
		CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(current_address, SceKernelPageSize),
		        "KernelMunmap");
	}

	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(phys_addr, SceKernelPageSize),
	        "KernelReleaseDirectMemory");

	std::printf("[host]    %-48s ok\n", test);
}

void TestFixedReserveReplacesPartialDirectMapping() {
	const char*        test         = "FixedReserveReplacesPartialDirectMapping";
	constexpr uint64_t page_count   = 13;
	constexpr uint64_t keep_pages   = 5;
	constexpr uint64_t total_size   = SceKernelPageSize * page_count;
	constexpr uint64_t keep_size    = SceKernelPageSize * keep_pages;
	constexpr uint64_t replace_size = total_size - keep_size;

	int64_t phys_addr = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            SceKernelDirectMemoryStart, Libs::LibKernel::Memory::KernelGetDirectMemorySize(),
	            total_size, SceKernelPageSize, SceKernelMtypeC, &phys_addr),
	        "KernelAllocateDirectMemory");
	void* alias = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &alias, total_size, SceKernelProtCpuRw, 0, phys_addr, SceKernelPageSize,
	            "partial_replace_alias"),
	        "KernelMapNamedDirectMemory(alias)");
	*reinterpret_cast<uint64_t*>(reinterpret_cast<uint64_t>(alias) + keep_size) =
	    0x4b595459414c4941ull; // "KYTYALIA"

	void* reserve = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReserveVirtualRange(&reserve, total_size, 0,
	                                                           SceKernelPageSize),
	        "KernelReserveVirtualRange(container)");
	const auto base = reinterpret_cast<uint64_t>(reserve);

	void* mapped = reserve;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &mapped, total_size, SceKernelProtCpuRw, SceKernelMapFixed | SceKernelMapNoCoalesce,
	            phys_addr, SceKernelPageSize, "partial_replace_direct"),
	        "KernelMapNamedDirectMemory");
	Check(test, mapped == reserve, "fixed direct mapping moved");
	*reinterpret_cast<uint64_t*>(base) = 0x4b5954594b454550ull; // "KYTYKEEP"

	void* replacement = reinterpret_cast<void*>(base + keep_size);
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReserveVirtualRange(
	            &replacement, replace_size, SceKernelMapFixed | SceKernelMapNoCoalesce,
	            SceKernelPageSize),
	        "KernelReserveVirtualRange(partial replacement)");
	Check(test, reinterpret_cast<uint64_t>(replacement) == base + keep_size,
	      "partial fixed reservation moved");
	Check(test, *reinterpret_cast<uint64_t*>(base) == 0x4b5954594b454550ull,
	      "partial replacement damaged the neighboring direct mapping");
	ExpectRange(test, Query(test, base), base, base + keep_size, SceKernelProtCpuRw, 0, 1, 0, 1,
	            "partial_replace_direct");
	ExpectRange(test, Query(test, base + keep_size), base + keep_size, base + total_size, 0, 0, 0,
	            0, 0);

	void* remapped = replacement;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &remapped, replace_size, SceKernelProtCpuRw,
	            SceKernelMapFixed | SceKernelMapNoCoalesce, phys_addr + keep_size,
	            SceKernelPageSize, "partial_replace_remap"),
	        "KernelMapNamedDirectMemory(replacement reuse)");
	Check(test, remapped == replacement, "replacement reservation was not reusable in place");
	Check(test, *reinterpret_cast<uint64_t*>(remapped) == 0x4b595459414c4941ull,
	      "replacement remap did not preserve its direct-memory backing offset");
	*reinterpret_cast<uint64_t*>(remapped) = 0x4b59545952455553ull; // "KYTYREUS"
	Check(test,
	      *reinterpret_cast<uint64_t*>(reinterpret_cast<uint64_t>(alias) + keep_size) ==
	          0x4b59545952455553ull,
	      "replacement remap did not alias the original direct-memory backing");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, keep_size),
	        "KernelMunmap(direct remainder)");
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base + keep_size, replace_size),
	        "KernelMunmap(reused replacement)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(reinterpret_cast<uint64_t>(alias), total_size),
	        "KernelMunmap(alias)");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(phys_addr, total_size),
	        "KernelReleaseDirectMemory");

	std::printf("[host]    %-48s ok\n", test);
}

void TestFixedReserveRollbackSkipsUntouchedChunks() {
	const char*        test       = "FixedReserveRollbackSkipsUntouchedChunks";
	constexpr uint64_t part_size  = SceKernelPageSize * 2;
	constexpr uint64_t total_size = part_size * 2;
	int64_t            left_phys  = 0;
	int64_t            right_phys = 0;

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            SceKernelDirectMemoryStart, Libs::LibKernel::Memory::KernelGetDirectMemorySize(),
	            part_size, SceKernelPageSize, SceKernelMtypeC, &left_phys),
	        "KernelAllocateDirectMemory(left)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            SceKernelDirectMemoryStart, Libs::LibKernel::Memory::KernelGetDirectMemorySize(),
	            part_size, SceKernelPageSize, SceKernelMtypeC, &right_phys),
	        "KernelAllocateDirectMemory(right)");

	void* reserve = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReserveVirtualRange(&reserve, total_size, 0,
	                                                           SceKernelPageSize),
	        "KernelReserveVirtualRange");
	const auto base  = reinterpret_cast<uint64_t>(reserve);
	void*      left  = reserve;
	void*      right = reinterpret_cast<void*>(base + part_size);
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &left, part_size, SceKernelProtCpuRw, SceKernelMapFixed | SceKernelMapNoCoalesce,
	            left_phys, SceKernelPageSize, "rollback_left"),
	        "KernelMapNamedDirectMemory(left)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &right, part_size, SceKernelProtCpuRw, SceKernelMapFixed | SceKernelMapNoCoalesce,
	            right_phys, SceKernelPageSize, "rollback_right"),
	        "KernelMapNamedDirectMemory(right)");
	*reinterpret_cast<uint64_t*>(left)  = 0x4b5954594c454654ull; // "KYTYLEFT"
	*reinterpret_cast<uint64_t*>(right) = 0x4b59545952474854ull; // "KYTYRGHT"

	Libs::LibKernel::Memory::TestFailPhysicalMemoryUnmapAfter(1);
	void* replacement = reserve;
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelReserveVirtualRange(
	                &replacement, total_size, SceKernelMapFixed | SceKernelMapNoCoalesce,
	                SceKernelPageSize),
	            "KernelReserveVirtualRange(second-chunk rollback)");
	Check(test, *reinterpret_cast<uint64_t*>(left) == 0x4b5954594c454654ull,
	      "rollback did not restore the mutated first chunk");
	Check(test, *reinterpret_cast<uint64_t*>(right) == 0x4b59545952474854ull,
	      "rollback damaged the failing second chunk");
	Check(test, !Libs::LibKernel::Memory::TestPlaceholderRangeIsFree(base, part_size),
	      "first restored mapping remained recorded as a free placeholder");
	Check(test, !Libs::LibKernel::Memory::TestPlaceholderRangeIsFree(base + part_size, part_size),
	      "second restored mapping remained recorded as a free placeholder");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, part_size), "KernelMunmap(left)");
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base + part_size, part_size),
	        "KernelMunmap(right)");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(left_phys, part_size),
	        "KernelReleaseDirectMemory(left)");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(right_phys, part_size),
	        "KernelReleaseDirectMemory(right)");

	std::printf("[host]    %-48s ok\n", test);
}

void TestFixedReserveRollbackConsumesRestoredPlaceholder() {
	const char*        test       = "FixedReserveRollbackConsumesRestoredPlaceholder";
	constexpr uint64_t total_size = SceKernelPageSize * 4;

	int64_t phys_addr = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            SceKernelDirectMemoryStart, Libs::LibKernel::Memory::KernelGetDirectMemorySize(),
	            total_size, SceKernelPageSize, SceKernelMtypeC, &phys_addr),
	        "KernelAllocateDirectMemory");

	void* reserve = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReserveVirtualRange(&reserve, total_size, 0,
	                                                           SceKernelPageSize),
	        "KernelReserveVirtualRange");
	const auto base = reinterpret_cast<uint64_t>(reserve);

	void* mapped = reserve;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &mapped, total_size, SceKernelProtCpuRw, SceKernelMapFixed | SceKernelMapNoCoalesce,
	            phys_addr, SceKernelPageSize, "rollback_direct"),
	        "KernelMapNamedDirectMemory");
	*reinterpret_cast<uint64_t*>(base) = 0x4b595459524f4c4cull; // "KYTYROLL"

	Libs::LibKernel::Memory::TestFailNextPhysicalMemoryUnmap();
	void* replacement = reserve;
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelReserveVirtualRange(
	                &replacement, total_size, SceKernelMapFixed | SceKernelMapNoCoalesce,
	                SceKernelPageSize),
	            "KernelReserveVirtualRange(injected rollback)");
	Check(test, *reinterpret_cast<uint64_t*>(base) == 0x4b595459524f4c4cull,
	      "rollback did not restore direct-memory contents");
	Check(test, !Libs::LibKernel::Memory::TestPlaceholderRangeIsFree(base, total_size),
	      "rollback left a mapped direct range recorded as a free placeholder");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, total_size), "KernelMunmap");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(phys_addr, total_size),
	        "KernelReleaseDirectMemory");

	std::printf("[host]    %-48s ok\n", test);
}

void TestFixedReserveRangeAddRollbackKeepsPlaceholder() {
	const char*        test      = "FixedReserveRangeAddRollbackKeepsPlaceholder";
	constexpr uint64_t size      = SceKernelPageSize * 4;
	int64_t            phys_addr = 0;

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            SceKernelDirectMemoryStart, Libs::LibKernel::Memory::KernelGetDirectMemorySize(),
	            size, SceKernelPageSize, SceKernelMtypeC, &phys_addr),
	        "KernelAllocateDirectMemory");
	void* reserve = nullptr;
	CheckOk(
	    test,
	    Libs::LibKernel::Memory::KernelReserveVirtualRange(&reserve, size, 0, SceKernelPageSize),
	    "KernelReserveVirtualRange");
	const auto base   = reinterpret_cast<uint64_t>(reserve);
	void*      mapped = reserve;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &mapped, size, SceKernelProtCpuRw, SceKernelMapFixed | SceKernelMapNoCoalesce,
	            phys_addr, SceKernelPageSize, "range_add_rollback"),
	        "KernelMapNamedDirectMemory");
	*reinterpret_cast<uint64_t*>(mapped) = 0x4b59545952414e47ull; // "KTYRANG"

	Libs::LibKernel::Memory::TestFailNextFixedReserveRangeRegistration();
	void* replacement = mapped;
	CheckFailed(
	    test,
	    Libs::LibKernel::Memory::KernelReserveVirtualRange(
	        &replacement, size, SceKernelMapFixed | SceKernelMapNoCoalesce, SceKernelPageSize),
	    "KernelReserveVirtualRange(range-add rollback)");
	Check(test, *reinterpret_cast<uint64_t*>(mapped) == 0x4b59545952414e47ull,
	      "range-add rollback did not restore direct-memory contents");
	Check(test, !Libs::LibKernel::Memory::TestPlaceholderRangeIsFree(base, size),
	      "range-add rollback left the restored mapping recorded as free");
	ExpectRange(test, Query(test, base), base, base + size, SceKernelProtCpuRw, 0, 1, 0, 1,
	            "range_add_rollback");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, size), "KernelMunmap");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(phys_addr, size),
	        "KernelReleaseDirectMemory");
	std::printf("[host]    %-48s ok\n", test);
}

void TestLargeHintedReserveHostsSmallDirectMap() {
	const char* test = "LargeHintedReserveHostsSmallDirectMap";

	constexpr uint64_t arena_base  = 0x1000000000ull;
	constexpr uint64_t arena_size  = 0x04000000ull;
	constexpr uint64_t window_size = 0x00200000ull;

	void* arena = reinterpret_cast<void*>(arena_base);
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReserveVirtualRange(&arena, arena_size, 0, 0x200000),
	        "KernelReserveVirtualRange(arena)");
	const auto actual_arena = reinterpret_cast<uint64_t>(arena);
	Check(test, actual_arena >= arena_base && (actual_arena & (0x200000 - 1u)) == 0,
	      "large hinted reserve violated its search start or alignment");

	void* window = reinterpret_cast<void*>(arena_base);
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReserveVirtualRange(&window, window_size, 0,
	                                                           SceKernelPageSize),
	        "KernelReserveVirtualRange(window)");
	Check(test, reinterpret_cast<uint64_t>(window) >= actual_arena + arena_size,
	      "second hinted reserve overlaps the large arena");

	int64_t phys = 0;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(), SceKernelPageSize * 2,
	            SceKernelPageSize, SceKernelMtypeC, &phys),
	        "KernelAllocateDirectMemory");

	void* mapped = window;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedDirectMemory(
	            &mapped, SceKernelPageSize * 2, SceKernelProtCpuRw,
	            SceKernelMapFixed | SceKernelMapNoCoalesce, phys, 0, "prospero_large_reserve"),
	        "KernelMapNamedDirectMemory");
	Check(test, mapped == window, "fixed direct mapping moved away from the reserved window");
	*reinterpret_cast<uint64_t*>(mapped) = 0x4b59545952455356ull; // "KYTYRESV"

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(reinterpret_cast<uint64_t>(mapped),
	                                              SceKernelPageSize * 2),
	        "KernelMunmap(direct)");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(phys, SceKernelPageSize * 2),
	        "KernelReleaseDirectMemory");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(reinterpret_cast<uint64_t>(window) +
	                                                  SceKernelPageSize * 2,
	                                              window_size - SceKernelPageSize * 2),
	        "KernelMunmap(window reserve remainder)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(reinterpret_cast<uint64_t>(arena), arena_size),
	        "KernelMunmap(arena reserve)");

	std::printf("[host]    %-48s ok\n", test);
}

void TestMemoryPoolAlignmentContracts() {
	const char* test = "MemoryPoolAlignmentContracts";
	void*       addr = nullptr;

	CheckFailed(
	    test,
	    Libs::LibKernel::Memory::KernelMemoryPoolReserve(nullptr, SceKernelPageSize, 0, 0, &addr),
	    "KernelMemoryPoolReserve(16KiB len)");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolReserve(nullptr, SceKernelMemoryPoolReserveLen,
	                                                         0, 0, &addr),
	        "KernelMemoryPoolReserve");
	const auto base = reinterpret_cast<uint64_t>(addr);

	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelMemoryPoolCommit(reinterpret_cast<void*>(base),
	                                                            SceKernelPageSize, SceKernelMtypeC,
	                                                            SceKernelProtCpuRw, 0),
	            "KernelMemoryPoolCommit(16KiB len)");
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelMemoryPoolDecommit(reinterpret_cast<void*>(base),
	                                                              SceKernelPageSize, 0),
	            "KernelMemoryPoolDecommit(16KiB len)");

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelMemoryPoolReserveLen),
	        "KernelMunmap(pool reserve cleanup)");

	std::printf("[host]    %-48s ok\n", test);
}

void TestProsperoSampleMemoryPoolExpandCommit() {
	const char* test = "ProsperoSampleMemoryPoolExpandCommit";

	int64_t pool_offset = -1;
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelMemoryPoolExpand(
	                0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(), SceKernelPageSize,
	                SceKernelMemoryPoolAlignment, &pool_offset),
	            "KernelMemoryPoolExpand(16KiB len)");
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelMemoryPoolExpand(
	                0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(),
	                SceKernelMemoryPoolExpandLen, SceKernelPageSize, &pool_offset),
	            "KernelMemoryPoolExpand(16KiB alignment)");
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelMemoryPoolExpand(
	                0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(),
	                SceKernelMemoryPoolExpandLen, SceKernelMemoryPoolAlignment * 3, &pool_offset),
	            "KernelMemoryPoolExpand(non-power-of-two alignment)");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolExpand(
	            0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(),
	            SceKernelMemoryPoolExpandLen, SceKernelMemoryPoolAlignment, &pool_offset),
	        "KernelMemoryPoolExpand");
	Check(test,
	      pool_offset >= 0 &&
	          (static_cast<uint64_t>(pool_offset) & (SceKernelMemoryPoolAlignment - 1u)) == 0,
	      "expanded physical range is not 64 KiB aligned");
	void* direct_alias = nullptr;
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelMapDirectMemory(
	                &direct_alias, SceKernelMemoryPoolCommitLen, SceKernelProtCpuRw, 0, pool_offset,
	                SceKernelMemoryPoolAlignment),
	            "KernelMapDirectMemory(pool expansion)");

	Libs::LibKernel::Memory::KernelMemoryPoolBlockStats stats {};
	CheckOk(test, Libs::LibKernel::Memory::KernelMemoryPoolGetBlockStats(&stats, sizeof(stats)),
	        "KernelMemoryPoolGetBlockStats(expanded)");
	Check(test,
	      stats.available_flushed_blocks ==
	          static_cast<int32_t>(SceKernelMemoryPoolExpandLen / SceKernelMemoryPoolAlignment),
	      "expanded pages were not added to the pool budget");

	void* arena = reinterpret_cast<void*>(0x1000000000ull);
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolReserve(arena, SceKernelMemoryPoolReserveLen,
	                                                         0, 0, &arena),
	        "KernelMemoryPoolReserve");
	const auto base              = reinterpret_cast<uint64_t>(arena);
	const auto flexible_baseline = AvailableFlexibleMemory(test);
	const auto commit_len        = SceKernelMemoryPoolCommitLen * 2;

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolCommit(arena, commit_len, SceKernelMtypeC,
	                                                        SceKernelProtCpuRw, 0),
	        "KernelMemoryPoolCommit");
	ExpectRange(test, Query(test, base), base, base + commit_len, SceKernelProtCpuRw, 0, 0, 1, 1);
	Check(test, Libs::LibKernel::Memory::TestGuestAddressRangeIsOwned(base, commit_len),
	      "pooled commit escaped the guest owner");
	Check(test, AvailableFlexibleMemory(test) == flexible_baseline,
	      "pooled commit consumed flexible memory instead of expanded direct "
	      "backing");
	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelCheckedReleaseDirectMemory(
	                pool_offset, SceKernelMemoryPoolExpandLen),
	            "KernelCheckedReleaseDirectMemory(committed pool expansion)");

	constexpr uint64_t first_value     = 0x504f4f4c4241434bull; // "POOLBACK"
	constexpr uint64_t second_value    = 0x5348415245444d45ull; // "SHAREDME"
	*reinterpret_cast<uint64_t*>(base) = first_value;
	*reinterpret_cast<uint64_t*>(base + SceKernelMemoryPoolCommitLen) = second_value;
	uint64_t backing_read                                             = 0;
	Check(test, Libs::LibKernel::Memory::TryReadBacking(base, &backing_read, sizeof(backing_read)),
	      "TryReadBacking did not resolve pooled memory");
	Check(test, backing_read == first_value,
	      "shared backing did not observe a pooled-memory CPU write");

	CheckOk(
	    test,
	    Libs::LibKernel::Memory::KernelMemoryPoolDecommit(arena, SceKernelMemoryPoolCommitLen, 0),
	    "KernelMemoryPoolDecommit(first page)");
	ExpectRange(test, Query(test, base), base, base + SceKernelMemoryPoolCommitLen, 0, 0, 0, 1, 0);
	ExpectRange(test, Query(test, base + SceKernelMemoryPoolCommitLen),
	            base + SceKernelMemoryPoolCommitLen, base + commit_len, SceKernelProtCpuRw, 0, 0, 1,
	            1);
	CheckOk(test, Libs::LibKernel::Memory::KernelMemoryPoolGetBlockStats(&stats, sizeof(stats)),
	        "KernelMemoryPoolGetBlockStats(partially decommitted)");
	Check(test,
	      stats.available_flushed_blocks ==
	              static_cast<int32_t>(SceKernelMemoryPoolExpandLen / SceKernelMemoryPoolAlignment -
	                                   1) &&
	          stats.allocated_flushed_blocks == 1,
	      "partial decommit returned the wrong number of pages to the expanded "
	      "pool");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolCommit(arena, SceKernelMemoryPoolCommitLen,
	                                                        SceKernelMtypeC, SceKernelProtCpuRw, 0),
	        "KernelMemoryPoolCommit(first-page recommit)");
	Check(test,
	      *reinterpret_cast<const uint64_t*>(base) == first_value &&
	          *reinterpret_cast<const uint64_t*>(base + SceKernelMemoryPoolCommitLen) ==
	              second_value,
	      "partially recommitted pooled pages did not retain shared-backing "
	      "contents");

	CheckOk(test, Libs::LibKernel::Memory::KernelMemoryPoolDecommit(arena, commit_len, 0),
	        "KernelMemoryPoolDecommit(cleanup)");
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelMemoryPoolReserveLen),
	        "KernelMunmap(pool reserve cleanup)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReleaseDirectMemory(pool_offset,
	                                                           SceKernelMemoryPoolExpandLen),
	        "KernelReleaseDirectMemory(pool expansion)");
	CheckOk(test, Libs::LibKernel::Memory::KernelMemoryPoolGetBlockStats(&stats, sizeof(stats)),
	        "KernelMemoryPoolGetBlockStats(released)");
	Check(test, stats.available_flushed_blocks == 0 && stats.allocated_flushed_blocks == 0,
	      "released expansion remained in the pool budget");

	std::printf("[host]    %-48s ok\n", test);
}

void TestFragmentedMemoryPoolBacking() {
	const char* test = "FragmentedMemoryPoolBacking";

	int64_t    first_pool  = -1;
	int64_t    direct_gap  = -1;
	int64_t    second_pool = -1;
	const auto direct_end =
	    static_cast<int64_t>(Libs::LibKernel::Memory::KernelGetDirectMemorySize());
	CheckOk(
	    test,
	    Libs::LibKernel::Memory::KernelMemoryPoolExpand(0, direct_end, SceKernelMemoryPoolCommitLen,
	                                                    SceKernelMemoryPoolAlignment, &first_pool),
	    "KernelMemoryPoolExpand(first)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelAllocateDirectMemory(
	            0, direct_end, SceKernelMemoryPoolCommitLen, SceKernelMemoryPoolAlignment,
	            SceKernelMtypeC, &direct_gap),
	        "KernelAllocateDirectMemory(gap)");
	CheckOk(
	    test,
	    Libs::LibKernel::Memory::KernelMemoryPoolExpand(0, direct_end, SceKernelMemoryPoolCommitLen,
	                                                    SceKernelMemoryPoolAlignment, &second_pool),
	    "KernelMemoryPoolExpand(second)");
	Check(test,
	      first_pool + static_cast<int64_t>(SceKernelMemoryPoolCommitLen) == direct_gap &&
	          direct_gap + static_cast<int64_t>(SceKernelMemoryPoolCommitLen) == second_pool,
	      "test setup did not create nonadjacent pool expansions");

	void* arena = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolReserve(nullptr, SceKernelMemoryPoolReserveLen,
	                                                         0, 0, &arena),
	        "KernelMemoryPoolReserve");
	const auto base       = reinterpret_cast<uint64_t>(arena);
	const auto commit_len = SceKernelMemoryPoolCommitLen * 2;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolCommit(arena, commit_len, SceKernelMtypeC,
	                                                        SceKernelProtCpuRw, 0),
	        "KernelMemoryPoolCommit(fragmented)");

	CheckFailed(test,
	            Libs::LibKernel::Memory::KernelMemoryPoolCommit(
	                reinterpret_cast<void*>(base + commit_len), SceKernelMemoryPoolCommitLen,
	                SceKernelMtypeC, SceKernelProtCpuRw, 0),
	            "KernelMemoryPoolCommit(exhausted)");
	ExpectRange(test, Query(test, base + commit_len), base + commit_len,
	            base + SceKernelMemoryPoolReserveLen, 0, 0, 0, 1, 0);

	*reinterpret_cast<uint64_t*>(base) = 0x465241474d454e54ull; // "FRAGMENT"
	*reinterpret_cast<uint64_t*>(base + SceKernelMemoryPoolCommitLen) = 0x504f4f4c50414745ull;
	CheckOk(test, Libs::LibKernel::Memory::KernelMemoryPoolDecommit(arena, commit_len, 0),
	        "KernelMemoryPoolDecommit(fragmented)");

	Libs::LibKernel::Memory::KernelMemoryPoolBlockStats stats {};
	CheckOk(test, Libs::LibKernel::Memory::KernelMemoryPoolGetBlockStats(&stats, sizeof(stats)),
	        "KernelMemoryPoolGetBlockStats(fragmented decommit)");
	Check(test, stats.available_flushed_blocks == 2 && stats.allocated_flushed_blocks == 0,
	      "fragmented decommit did not restore both pool pages");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolCommit(arena, commit_len, SceKernelMtypeC,
	                                                        SceKernelProtCpuRw, 0),
	        "KernelMemoryPoolCommit(fragmented recommit)");
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, commit_len),
	        "KernelMunmap(fragmented commit)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(base + commit_len,
	                                              SceKernelMemoryPoolReserveLen - commit_len),
	        "KernelMunmap(fragmented reserve remainder)");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReleaseDirectMemory(first_pool,
	                                                           SceKernelMemoryPoolCommitLen),
	        "KernelReleaseDirectMemory(first pool)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReleaseDirectMemory(second_pool,
	                                                           SceKernelMemoryPoolCommitLen),
	        "KernelReleaseDirectMemory(second pool)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReleaseDirectMemory(direct_gap,
	                                                           SceKernelMemoryPoolCommitLen),
	        "KernelReleaseDirectMemory(gap)");

	std::printf("[host]    %-48s ok\n", test);
}

void TestMemoryPoolMultiRangeDecommit() {
	const char* test        = "MemoryPoolMultiRangeDecommit";
	const auto  expand_len  = SceKernelMemoryPoolCommitLen * 2;
	int64_t     pool_offset = -1;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolExpand(
	            0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(), expand_len,
	            SceKernelMemoryPoolAlignment, &pool_offset),
	        "KernelMemoryPoolExpand");

	void* arena = nullptr;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolReserve(nullptr, SceKernelMemoryPoolReserveLen,
	                                                         0, 0, &arena),
	        "KernelMemoryPoolReserve");
	const auto base = reinterpret_cast<uint64_t>(arena);
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolCommit(arena, SceKernelMemoryPoolCommitLen,
	                                                        SceKernelMtypeC, SceKernelProtCpuRw, 0),
	        "KernelMemoryPoolCommit(read-write)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolCommit(
	            reinterpret_cast<void*>(base + SceKernelMemoryPoolCommitLen),
	            SceKernelMemoryPoolCommitLen, SceKernelMtypeC, SceKernelProtCpuRead, 0),
	        "KernelMemoryPoolCommit(read-only)");
	CheckOk(test, Libs::LibKernel::Memory::KernelMemoryPoolDecommit(arena, expand_len, 0),
	        "KernelMemoryPoolDecommit(different protections)");
	const auto decommitted = Query(test, base);
	Check(test,
	      decommitted.start <= base && decommitted.end >= base + expand_len &&
	          decommitted.is_pooled == 1 && decommitted.is_committed == 0,
	      "multi-range decommit did not restore the reserved pool span");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolCommit(
	            reinterpret_cast<void*>(base + SceKernelMemoryPoolCommitLen),
	            SceKernelMemoryPoolCommitLen, SceKernelMtypeC, SceKernelProtCpuRead, 0),
	        "KernelMemoryPoolCommit(mixed span)");
	CheckOk(test, Libs::LibKernel::Memory::KernelMemoryPoolDecommit(arena, expand_len, 0),
	        "KernelMemoryPoolDecommit(reserved and committed span)");
	const auto mixed_decommitted = Query(test, base);
	Check(test,
	      mixed_decommitted.start <= base && mixed_decommitted.end >= base + expand_len &&
	          mixed_decommitted.is_pooled == 1 && mixed_decommitted.is_committed == 0,
	      "mixed reserved/committed decommit left committed pages behind");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolCommit(arena, SceKernelMemoryPoolCommitLen,
	                                                        SceKernelMtypeC, SceKernelProtCpuRw, 0),
	        "KernelMemoryPoolCommit(preflight prefix)");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMunmap(base + SceKernelMemoryPoolCommitLen,
	                                              SceKernelMemoryPoolCommitLen),
	        "KernelMunmap(preflight tail reserve)");
	void* flexible_tail = reinterpret_cast<void*>(base + SceKernelMemoryPoolCommitLen);
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMapNamedFlexibleMemory(
	            &flexible_tail, SceKernelMemoryPoolCommitLen, SceKernelProtCpuRead,
	            SceKernelMapFixed, "pool_invalid_tail"),
	        "KernelMapNamedFlexibleMemory(preflight tail)");
	CheckFailed(test, Libs::LibKernel::Memory::KernelMemoryPoolDecommit(arena, expand_len, 0),
	            "KernelMemoryPoolDecommit(invalid tail)");
	ExpectRange(test, Query(test, base), base, base + SceKernelMemoryPoolCommitLen,
	            SceKernelProtCpuRw, 0, 0, 1, 1);
	ExpectRange(test, Query(test, base + SceKernelMemoryPoolCommitLen),
	            base + SceKernelMemoryPoolCommitLen, base + expand_len, SceKernelProtCpuRead, 1, 0,
	            0, 1, "pool_invalid_tail");

	CheckOk(
	    test,
	    Libs::LibKernel::Memory::KernelMemoryPoolDecommit(arena, SceKernelMemoryPoolCommitLen, 0),
	    "KernelMemoryPoolDecommit(preflight prefix cleanup)");
	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelMemoryPoolReserveLen),
	        "KernelMunmap(pool reserve cleanup)");
	CheckOk(test, Libs::LibKernel::Memory::KernelReleaseDirectMemory(pool_offset, expand_len),
	        "KernelReleaseDirectMemory(pool expansion)");

	std::printf("[host]    %-48s ok\n", test);
}

void TestMemoryPoolCommitDecommitQueryFlags() {
	const char* test        = "MemoryPoolCommitDecommitQueryFlags";
	void*       addr        = nullptr;
	int64_t     pool_offset = -1;
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolExpand(
	            0, Libs::LibKernel::Memory::KernelGetDirectMemorySize(),
	            SceKernelMemoryPoolCommitLen, SceKernelMemoryPoolAlignment, &pool_offset),
	        "KernelMemoryPoolExpand");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolReserve(nullptr, SceKernelMemoryPoolReserveLen,
	                                                         0, 0, &addr),
	        "KernelMemoryPoolReserve");
	const auto base = reinterpret_cast<uint64_t>(addr);

	const auto reserved    = Query(test, base);
	const bool reserved_ok = reserved.start <= base && base < reserved.end &&
	                         reserved.is_pooled == 1 && reserved.is_committed == 0;

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolCommit(reinterpret_cast<void*>(base),
	                                                        SceKernelMemoryPoolCommitLen,
	                                                        SceKernelMtypeC, SceKernelProtCpuRw, 0),
	        "KernelMemoryPoolCommit");
	const auto committed    = Query(test, base);
	const bool committed_ok = committed.start == base &&
	                          committed.end == base + SceKernelMemoryPoolCommitLen &&
	                          committed.protection == SceKernelProtCpuRw &&
	                          committed.is_pooled == 1 && committed.is_committed == 1;

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMemoryPoolDecommit(reinterpret_cast<void*>(base),
	                                                          SceKernelMemoryPoolCommitLen, 0),
	        "KernelMemoryPoolDecommit");
	const auto decommitted    = Query(test, base);
	const bool decommitted_ok = decommitted.start <= base && base < decommitted.end &&
	                            decommitted.is_pooled == 1 && decommitted.is_committed == 0;

	CheckOk(test, Libs::LibKernel::Memory::KernelMunmap(base, SceKernelMemoryPoolReserveLen),
	        "KernelMunmap(pool reserve cleanup)");
	Check(test, reserved_ok, "pool reserve should query as pooled/uncommitted");
	Check(test, committed_ok, "pool commit should query as pooled/committed");
	Check(test, decommitted_ok, "pool decommit should return to pooled/uncommitted");
	CheckOk(test,
	        Libs::LibKernel::Memory::KernelReleaseDirectMemory(pool_offset,
	                                                           SceKernelMemoryPoolCommitLen),
	        "KernelReleaseDirectMemory(pool expansion)");

	std::printf("[host]    %-48s ok\n", test);
}

void TestProgramMemoryAllocationAndProtection() {
	const char* test = "ProgramMemoryAllocationAndProtection";
	const auto  size = SceKernelPageSize * 3;
	const auto  base = Libs::LibKernel::Memory::AllocateProgramMemory(
	    0x900000000, size, Common::VirtualMemory::Mode::ReadWrite, "program_test");
	Check(test, base != 0, "program guest allocation failed");
	Check(test, Libs::LibKernel::Memory::TestGuestAddressRangeIsOwned(base, size),
	      "program allocation escaped the guest owner");
	ExpectRange(test, Query(test, base), base, base + size,
	            SceKernelProtCpuRead | SceKernelProtCpuRw, 0, 0, 0, 1, "program_test");

	Check(test,
	      Libs::LibKernel::Memory::ProtectGuestMemory(base, SceKernelPageSize,
	                                                  Common::VirtualMemory::Mode::Read),
	      "ProtectGuestMemory(first page) failed");
	ExpectRange(test, Query(test, base), base, base + SceKernelPageSize, SceKernelProtCpuRead, 0, 0,
	            0, 1, "program_test");

	Common::VirtualMemory::Mode previous_mode = Common::VirtualMemory::Mode::NoAccess;
	Check(test,
	      Libs::LibKernel::Memory::ProtectGuestMemory(
	          base, SceKernelPageSize, Common::VirtualMemory::Mode::ReadWrite, &previous_mode),
	      "ProtectGuestMemory(tracked restore) failed");
	Check(test, previous_mode == Common::VirtualMemory::Mode::Read,
	      "semantic guest protection did not preserve its tracked old mode");

	CheckOk(test,
	        Libs::LibKernel::Memory::KernelMprotect(
	            reinterpret_cast<void*>(base + SceKernelPageSize - 0x10), 0x20,
	            SceKernelProtCpuRead | SceKernelProtCpuRw),
	        "KernelMprotect(program split span)");
	ExpectRange(test, Query(test, base), base, base + size,
	            SceKernelProtCpuRead | SceKernelProtCpuRw, 0, 0, 0, 1, "program_test");

	Check(test,
	      Libs::LibKernel::Memory::ProtectGuestMemory(
	          base + SceKernelPageSize * 2, SceKernelPageSize, Common::VirtualMemory::Mode::Read),
	      "ProtectGuestMemory(last page) failed");
	ExpectRange(test, Query(test, base + SceKernelPageSize * 2), base + SceKernelPageSize * 2,
	            base + size, SceKernelProtCpuRead, 0, 0, 0, 1, "program_test");

	Check(test, Libs::LibKernel::Memory::FreeGuestMemory(base, size), "program guest free failed");
	ExpectUnmapped(test, base);

	std::printf("[host]    %-48s ok\n", test);
}

void TestModuleRelocationUsesWritableHostMapping() {
	const char* test = "ModuleRelocationUsesWritableHostMapping";
	Check(test, Loader::TestModuleRelocationUsesWritableHostMapping(),
	      "module relocation did not retain writable host memory and semantic guest protection");
	std::printf("[host]    %-48s ok\n", test);
}

} // namespace

int main() {
	InitSubsystems();

	RunTest(TestProsperoArgumentAndInfoSizeContracts);
	RunTest(TestGuestAddressSpaceOwnsReservationsBeforeBacking);
	RunTest(TestGuestAddressSpaceHasNoFixedFallback);
	RunTest(TestGuestFreeRangeSearchDoesNotUnderflow);
	RunTest(TestFlexibleMemoryCapacityIsBootFixed);
	RunTest(TestFlexibleMemoryUsesSharedBacking);
	RunTest(TestFlexibleDmemCompatAndAlignmentFlags);
	RunTest(TestFlexibleNoCoalescePreservesBoundaries);
	RunTest(TestFlexibleMemoryReuseIsZeroFilled);
	RunTest(TestPthreadAttrDetachStateValidation);
	RunTest(TestPthreadAttrInheritSchedValidation);
	RunTest(TestPthreadAttrSchedPolicyValidation);
	RunTest(TestPthreadAttrSchedPriorityValidation);
	RunTest(TestPthreadAttrSoloSchedValidation);
	RunTest(TestPthreadMutexAttrProtocolRoundTrip);
	RunTest(TestPthreadRwlockAttrTypeValidation);
	RunTest(TestKernelLseekInvalidWhence);
	RunTest(TestKernelLseekOverflowContract);
	RunTest(TestKernelLseekInvalidDescriptorContract);
	RunTest(TestKernelCloseDescriptorErrorContract);
	RunTest(TestKernelFileSynchronization);
	RunTest(TestKernelFtruncateContract);
	RunTest(TestKernelTruncateContract);
	RunTest(TestKernelRenameNullPathContract);
	RunTest(TestKernelUnlinkNullPathContract);
	RunTest(TestKernelUnlinkAbsolutePathContract);
	RunTest(TestKernelOpenNullPathContract);
	RunTest(TestKernelOpenAbsolutePathContract);
	RunTest(TestKernelOpenAccessModeContract);
	RunTest(TestKernelOpenDirectoryWriteContract);
	RunTest(TestKernelOpenNameLengthContract);
	RunTest(TestKernelStatNullPointerContract);
	RunTest(TestKernelStatAbsolutePathContract);
	RunTest(TestKernelStatNameLengthContract);
	RunTest(TestKernelFstatInvalidDescriptorContract);
	RunTest(TestKernelReadInvalidDescriptorContract);
	RunTest(TestKernelPreadInvalidDescriptorContract);
	RunTest(TestKernelPwriteInvalidDescriptorContract);
	RunTest(TestKernelMkdirNullPathContract);
	RunTest(TestKernelMkdirAbsolutePathContract);
	RunTest(TestKernelMkdirPathLengthContract);
	RunTest(TestKernelRmdirNullPathContract);
	RunTest(TestKernelRmdirAbsolutePathContract);
	RunTest(TestKernelCheckReachabilityNullPathContract);
	RunTest(TestKernelCheckReachabilityAbsolutePathContract);
	RunTest(TestKernelCheckReachabilityOverlengthPathContract);
	RunTest(TestKernelRenameAbsolutePathContract);
	RunTest(TestKernelRenameOpenSourceContract);
	RunTest(TestKernelRenameOpenDestinationContract);
	RunTest(TestKernelRmdirNonemptyContract);
	RunTest(TestKernelScalarIoSizeLimit);
	RunTest(TestKernelPwriteRequiresWritableDescriptor);
	RunTest(TestKernelPwriteIgnoresAppendPosition);
	RunTest(TestKernelWriteRequiresWritableDescriptor);
	RunTest(TestKernelDirectoryReadError);
	RunTest(TestKernelDirectorySeekPositions);
	RunTest(TestGuestStackUsesPrivateOwnerMemoryAndCache);
	RunTest(TestMainEntryUsesGuestStackAndDisablesHostChecks);
	RunTest(TestFragmentedBackingUnmapRollback);
	RunTest(TestRuntimeMemoryOwnerLifecycle);
	RunTest(TestFlexibleMapQueryAndWholeMunmap);
	RunTest(TestPartialFlexibleMunmapAndFindNext);
	RunTest(TestReserveMapFixedAndNoOverwrite);
	RunTest(TestFixedNoOverwriteRejectsReservedRange);
	RunTest(TestReleasedReserveCanBeReused);
	RunTest(TestMunmapAcrossAdjacentFlexibleMappings);
	RunTest(TestDirectMapQueryOffsetAndPartialMunmap);
	RunTest(TestDirectPartialProtectUnmapPreservesNeighbors);
	RunTest(TestDirectProtectionMaskContract);
	RunTest(TestDirectMapValidationBeforeOwnerMutation);
	RunTest(TestDirectReleaseRollbackRestoresOwnerMapping);
	RunTest(TestDirectReleaseContracts);
	RunTest(TestNonzeroDirectOffsetAliasesSharedBacking);
	RunTest(TestDirectMapAcrossContiguousAllocations);
	RunTest(TestDirectPhysicalFreeRangeReuseAndCoalescing);
	RunTest(TestDirectAlignmentStaysWithinSearchRange);
	RunTest(TestDefaultDirectMapUsesSystemAddressRange);
	RunTest(TestLargeDirectMapAliasesAcrossChunks);
	RunTest(TestHintlessDirectMapUsesCanonicalGuestBase);
	RunTest(TestDirectMemoryContentPersistsAcrossRemap);
	RunTest(TestDirectMapUnmapReusesHostAddress);
	RunTest(TestFixedReserveReplacesPartialDirectMapping);
	RunTest(TestFixedReserveRollbackConsumesRestoredPlaceholder);
	RunTest(TestFixedReserveRollbackSkipsUntouchedChunks);
	RunTest(TestFixedReserveRangeAddRollbackKeepsPlaceholder);
	RunTest(TestLargeHintedReserveHostsSmallDirectMap);
	RunTest(TestMemoryPoolAlignmentContracts);
	RunTest(TestProsperoSampleMemoryPoolExpandCommit);
	RunTest(TestFragmentedMemoryPoolBacking);
	RunTest(TestMemoryPoolMultiRangeDecommit);
	RunTest(TestMemoryPoolCommitDecommitQueryFlags);
	RunTest(TestProgramMemoryAllocationAndProtection);
	RunTest(TestModuleRelocationUsesWritableHostMapping);

	if (g_failed_tests != 0) {
		std::printf("VirtualMemoryAllocationTests: %d case(s) failed\n", g_failed_tests);
		return 1;
	}

	std::printf("VirtualMemoryAllocationTests: all cases passed\n");
	return 0;
}
