#include "loader/runtimeLinkerBounds.h"
#include "loader/runtimeLinkerLifecycle.h"

#include <array>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <limits>

namespace {

struct SyntheticObject {
	uint64_t load_vaddr;
	uint64_t file_size;

	[[nodiscard]] bool AcceptsRelaTable(uint64_t table_vaddr, uint64_t table_size,
	                                    uint64_t entry_size =
	                                        Loader::RuntimeLinkerBounds::ELF64_RELA_SIZE) const {
		return Loader::RuntimeLinkerBounds::IsRelaTableRangeValid(
		    table_vaddr, table_size, entry_size, load_vaddr, file_size);
	}
};

struct SyntheticSymbolTable {
	uint64_t size;
	uint64_t entry_size = Loader::RuntimeLinkerBounds::ELF64_SYM_SIZE;

	[[nodiscard]] bool Contains(uint64_t symbol_index) const {
		return Loader::RuntimeLinkerBounds::IsSymbolIndexValid(symbol_index, size, entry_size);
	}
};

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "RuntimeLinkerBoundsTests: failed: %s\n", text);
		std::abort();
	}
}

void TestRelocatedRange() {
	uint64_t relocated_vaddr = 0;
	Check(Loader::RuntimeLinkerBounds::DecodeRelocatedRange(
	          0x1000, 0x2000, 0x3000, &relocated_vaddr) &&
	          relocated_vaddr == 0x3000,
	      "a regular relocated load range was rejected or decoded incorrectly");

	constexpr uint64_t max_address = std::numeric_limits<uint64_t>::max();
	Check(Loader::RuntimeLinkerBounds::DecodeRelocatedRange(
	          max_address - 0x30, 0x10, 0x20, &relocated_vaddr) &&
	          relocated_vaddr == max_address - 0x20,
	      "an exactly representable relocated load endpoint was rejected");
	Check(Loader::RuntimeLinkerBounds::DecodeRelocatedRange(
	          max_address, 0, 0, &relocated_vaddr) && relocated_vaddr == max_address,
	      "a zero-sized relocated load range at the maximum address was rejected");

	Check(!Loader::RuntimeLinkerBounds::DecodeRelocatedRange(
	          max_address - 0xf, 0x10, 0, &relocated_vaddr),
	      "an overflowing relocated load start was accepted");
	Check(!Loader::RuntimeLinkerBounds::DecodeRelocatedRange(
	          max_address - 0x30, 0x10, 0x21, &relocated_vaddr),
	      "an overflowing relocated load endpoint was accepted");
	Check(!Loader::RuntimeLinkerBounds::DecodeRelocatedRange(0, 0, 0, nullptr),
	      "a relocated load range decoder accepted a null output");
}

void TestRelativeRelocationValue() {
	using Loader::RuntimeLinkerBounds::DecodeRelativeRelocationValue;

	uint64_t value = 0;
	Check(DecodeRelativeRelocationValue(0x1000, 0x20, &value) && value == 0x1020,
	      "a regular positive relative-relocation addend was rejected");
	Check(DecodeRelativeRelocationValue(0x1000, -0x20, &value) && value == 0xfe0,
	      "a representable negative relative-relocation addend was rejected");

	constexpr uint64_t max_address = std::numeric_limits<uint64_t>::max();
	Check(DecodeRelativeRelocationValue(max_address - 0x20, 0x20, &value) &&
	          value == max_address,
	      "an exactly representable positive relative-relocation value was rejected");
	Check(!DecodeRelativeRelocationValue(max_address - 0x1f, 0x20, &value),
	      "an overflowing positive relative-relocation value was accepted");
	Check(DecodeRelativeRelocationValue(0x8000, -0x8000, &value) && value == 0,
	      "an exactly representable negative relative-relocation value was rejected");
	Check(!DecodeRelativeRelocationValue(0x7fff, -0x8000, &value),
	      "an underflowing negative relative-relocation value was accepted");
	Check(DecodeRelativeRelocationValue(uint64_t {1} << 63,
	                                    std::numeric_limits<int64_t>::min(), &value) &&
	          value == 0,
	      "the exactly representable minimum signed addend was rejected");
	Check(!DecodeRelativeRelocationValue((uint64_t {1} << 63) - 1,
	                                     std::numeric_limits<int64_t>::min(), &value),
	      "an underflowing minimum signed addend was accepted");
	Check(!DecodeRelativeRelocationValue(0, 0, nullptr),
	      "a relative-relocation value decoder accepted a null output");
}

void TestSymbolRelocationValue() {
	using Loader::RuntimeLinkerBounds::DecodeSymbolRelocationValue;

	uint64_t value = 0;
	Check(DecodeSymbolRelocationValue(0x1000, 16, &value) && value == 0x1010,
	      "an observed positive symbol-relocation addend was rejected");
	Check(DecodeSymbolRelocationValue(0x1000, -16, &value) && value == 0xff0,
	      "a representable negative symbol-relocation addend was rejected");

	constexpr uint64_t max_address = std::numeric_limits<uint64_t>::max();
	Check(DecodeSymbolRelocationValue(max_address - 16, 16, &value) && value == max_address,
	      "an exactly representable symbol-relocation value was rejected");
	Check(!DecodeSymbolRelocationValue(max_address - 15, 16, &value),
	      "an overflowing symbol-relocation value was accepted");
	Check(DecodeSymbolRelocationValue(16, -16, &value) && value == 0,
	      "an exactly representable negative symbol-relocation value was rejected");
	Check(!DecodeSymbolRelocationValue(15, -16, &value),
	      "an underflowing symbol-relocation value was accepted");
	Check(!DecodeSymbolRelocationValue(0, 0, nullptr),
	      "a symbol-relocation value decoder accepted a null output");
}

void TestRelroMappedRange() {
	using Loader::RuntimeLinkerBounds::IsRelroRangeInMappedLoadSegment;

	constexpr uint64_t page         = 0x4000;
	constexpr uint64_t load_vaddr   = 0x8000;
	constexpr uint64_t mapped_size  = 3 * page;

	Check(IsRelroRangeInMappedLoadSegment(load_vaddr + page, 2 * page, load_vaddr,
	                                      mapped_size),
	      "an exact-end page-shaped RELRO range was rejected");
	Check(IsRelroRangeInMappedLoadSegment(std::numeric_limits<uint64_t>::max(), 0,
	                                      load_vaddr, mapped_size),
	      "a zero-sized RELRO declaration was rejected");
	Check(!IsRelroRangeInMappedLoadSegment(load_vaddr + 1, page, load_vaddr, mapped_size),
	      "a RELRO range with a partial first page was accepted");
	Check(!IsRelroRangeInMappedLoadSegment(load_vaddr + page, page - 1, load_vaddr,
	                                      mapped_size),
	      "a RELRO range with a partial final page was accepted");
	Check(!IsRelroRangeInMappedLoadSegment(load_vaddr + 2 * page, 2 * page, load_vaddr,
	                                      mapped_size),
	      "a RELRO range extending beyond mapped load storage was accepted");
	Check(!IsRelroRangeInMappedLoadSegment(load_vaddr - page, page, load_vaddr, mapped_size),
	      "a RELRO range before mapped load storage was accepted");
	Check(!IsRelroRangeInMappedLoadSegment(std::numeric_limits<uint64_t>::max() - page + 1,
	                                      page, 0, std::numeric_limits<uint64_t>::max()),
	      "an overflowing RELRO range was accepted");
	Check(!IsRelroRangeInMappedLoadSegment(load_vaddr, page,
	                                      std::numeric_limits<uint64_t>::max() - page + 1,
	                                      page),
	      "a RELRO range in an overflowing mapped load extent was accepted");
}

void TestRelroRelocationLifecycle() {
	using Loader::RuntimeLinkerLifecycle::RunRelocationProtectionLifecycle;

	auto run = [](bool already_relocated, int* trace) {
		return RunRelocationProtectionLifecycle(
		    already_relocated,
		    [trace]() {
			    *trace = *trace * 10 + 1;
			    return true;
		    },
		    [trace]() {
			    *trace = *trace * 10 + 2;
			    return true;
		    },
		    [trace]() {
			    *trace = *trace * 10 + 3;
			    return true;
		    });
	};

	int initial_trace = 0;
	Check(run(false, &initial_trace) && initial_trace == 23,
	      "an initial relocation did not close RELRO after its writes");
	int repeat_trace = 0;
	Check(run(true, &repeat_trace) && repeat_trace == 123,
	      "a repeat relocation did not reopen and then close RELRO around its writes");

	int failed_trace = 0;
	Check(!RunRelocationProtectionLifecycle(
	          true,
	          [&failed_trace]() {
		          failed_trace = failed_trace * 10 + 1;
		          return false;
	          },
	          [&failed_trace]() {
		          failed_trace = failed_trace * 10 + 2;
		          return true;
	          },
	          [&failed_trace]() {
		          failed_trace = failed_trace * 10 + 3;
		          return true;
	          }) &&
	          failed_trace == 1,
	      "relocation continued after its RELRO write window could not be restored");
}

void TestDynamicSymbolAddress() {
	uint64_t address = 0;
	Check(Loader::RuntimeLinkerBounds::DecodeDynamicSymbolAddress(1, 0x20, 0x1000, &address) &&
	          address == 0x1020,
	      "a regular defined symbol did not use its relocated address");
	Check(Loader::RuntimeLinkerBounds::DecodeDynamicSymbolAddress(
	          Loader::RuntimeLinkerBounds::ELF64_SHN_ABS, 2, 0x1000, &address) &&
	          address == 2,
	      "an absolute symbol value was incorrectly relocated by the module base");
	Check(Loader::RuntimeLinkerBounds::DecodeDynamicSymbolAddress(
	          Loader::RuntimeLinkerBounds::ELF64_SHN_ABS,
	          std::numeric_limits<uint64_t>::max(), 1, &address) &&
	          address == std::numeric_limits<uint64_t>::max(),
	      "the maximum absolute symbol value was rejected or changed");
	Check(!Loader::RuntimeLinkerBounds::DecodeDynamicSymbolAddress(
	          1, std::numeric_limits<uint64_t>::max(), 1, &address),
	      "an overflowing regular symbol address was accepted");
	Check(!Loader::RuntimeLinkerBounds::DecodeDynamicSymbolAddress(
	          Loader::RuntimeLinkerBounds::ELF64_SHN_UNDEF, 0, 0x1000, &address),
	      "an undefined symbol was assigned a definition address");
	Check(!Loader::RuntimeLinkerBounds::DecodeDynamicSymbolAddress(1, 0, 0, nullptr),
	      "a dynamic symbol address decoder accepted a null output");
}

void TestDynamicSymbolResolution() {
	using namespace Loader::RuntimeLinkerBounds;

	Check(IsDynamicSymbolResolutionComplete(ELF64_SHN_UNDEF, ELF64_STB_WEAK, 0),
	      "an unresolved weak undefined symbol did not complete with a zero address");
	Check(!IsDynamicSymbolResolutionComplete(ELF64_SHN_UNDEF, ELF64_STB_GLOBAL, 0),
	      "an unresolved strong undefined symbol was treated as complete");
	Check(IsDynamicSymbolResolutionComplete(ELF64_SHN_UNDEF, ELF64_STB_GLOBAL, 0x1000),
	      "a resolved strong import was treated as unresolved");
	Check(IsDynamicSymbolResolutionComplete(ELF64_SHN_UNDEF, ELF64_STB_WEAK, 0x1000),
	      "a resolved weak import was treated as unresolved");
}

void TestRegularDynamicFunctionDefinition() {
	using namespace Loader::RuntimeLinkerBounds;

	Check(IsRegularDynamicFunctionDefinition(1, ELF64_STT_FUNC),
	      "a regular defined dynamic function was not selected for address validation");
	Check(IsRegularDynamicFunctionDefinition(ELF64_SHN_LORESERVE - 1, ELF64_STT_FUNC),
	      "the highest regular section index was not selected for address validation");
	Check(!IsRegularDynamicFunctionDefinition(ELF64_SHN_UNDEF, ELF64_STT_FUNC),
	      "an undefined function import was treated as a definition");
	Check(!IsRegularDynamicFunctionDefinition(ELF64_SHN_ABS, ELF64_STT_FUNC),
	      "an absolute function symbol was subjected to mapped-image validation");
	Check(!IsRegularDynamicFunctionDefinition(ELF64_SHN_LORESERVE, ELF64_STT_FUNC),
	      "a reserved section index was treated as a regular definition");
	Check(!IsRegularDynamicFunctionDefinition(1, 1),
	      "a non-function definition was subjected to the function-address rule");
}

void TestRegularDynamicObjectDefinition() {
	using namespace Loader::RuntimeLinkerBounds;

	Check(IsRegularDynamicObjectDefinition(1, ELF64_STT_OBJECT, 8),
	      "a regular nonempty dynamic object was not selected for extent validation");
	Check(IsRegularDynamicObjectDefinition(ELF64_SHN_LORESERVE - 1, ELF64_STT_OBJECT, 1),
	      "the highest regular object section index was not selected for validation");
	Check(!IsRegularDynamicObjectDefinition(1, ELF64_STT_OBJECT, 0),
	      "a zero-sized object was broadened into the storage-extent contract");
	Check(!IsRegularDynamicObjectDefinition(ELF64_SHN_UNDEF, ELF64_STT_OBJECT, 8),
	      "an undefined object import was treated as a definition");
	Check(!IsRegularDynamicObjectDefinition(ELF64_SHN_ABS, ELF64_STT_OBJECT, 8),
	      "an absolute object was subjected to mapped-image validation");
	Check(!IsRegularDynamicObjectDefinition(ELF64_SHN_LORESERVE, ELF64_STT_OBJECT, 8),
	      "a reserved object section index was treated as a regular definition");
	Check(!IsRegularDynamicObjectDefinition(1, ELF64_STT_FUNC, 8),
	      "a function definition was subjected to the object-extent rule");
}

void TestDynamicObjectExtent() {
	using Loader::RuntimeLinkerBounds::IsDynamicObjectRangeInSegment;

	constexpr uint64_t segment_vaddr    = 0x4000;
	constexpr uint64_t segment_file_size = 0x20;
	constexpr uint64_t segment_mem_size  = 0x100;

	Check(IsDynamicObjectRangeInSegment(
	          segment_vaddr, 8, segment_vaddr, segment_mem_size),
	      "an object at the mapped segment start was rejected");
	Check(IsDynamicObjectRangeInSegment(
	          segment_vaddr + segment_mem_size - 8, 8, segment_vaddr, segment_mem_size),
	      "an object ending exactly at the mapped segment boundary was rejected");
	Check(segment_file_size < segment_mem_size &&
	          IsDynamicObjectRangeInSegment(segment_vaddr + segment_file_size, 8,
	                                        segment_vaddr, segment_mem_size),
	      "an object in valid zero-fill storage was rejected as non-file-backed");
	Check(!IsDynamicObjectRangeInSegment(
	          segment_vaddr + segment_mem_size - 8, 9, segment_vaddr, segment_mem_size),
	      "an object truncated by one mapped byte was accepted");
	Check(!IsDynamicObjectRangeInSegment(
	          segment_vaddr + segment_mem_size, 1, segment_vaddr, segment_mem_size),
	      "a one-past object start was accepted");
	Check(!IsDynamicObjectRangeInSegment(
	          segment_vaddr - 1, 1, segment_vaddr, segment_mem_size),
	      "an object before its mapped segment was accepted");
	Check(!IsDynamicObjectRangeInSegment(
	          std::numeric_limits<uint64_t>::max() - 7, 1,
	          std::numeric_limits<uint64_t>::max() - 7, 8),
	      "an object in an overflowing segment extent was accepted");
	Check(!IsDynamicObjectRangeInSegment(
	          std::numeric_limits<uint64_t>::max() - 7, 9, 0,
	          std::numeric_limits<uint64_t>::max()),
	      "an overflowing object extent was accepted");
}

void TestNullDynamicSymbolEntry() {
	std::array<uint8_t, Loader::RuntimeLinkerBounds::ELF64_SYM_SIZE> entry {};
	Check(Loader::RuntimeLinkerBounds::IsNullDynamicSymbolEntryValid(entry.data(), entry.size()),
	      "an exact-size null dynamic-symbol entry was rejected");
	entry.front() = 1;
	Check(!Loader::RuntimeLinkerBounds::IsNullDynamicSymbolEntryValid(entry.data(), entry.size()),
	      "a non-null reserved dynamic-symbol entry was accepted");
	entry.front() = 0;
	entry.back()  = 1;
	Check(!Loader::RuntimeLinkerBounds::IsNullDynamicSymbolEntryValid(entry.data(), entry.size()),
	      "a reserved dynamic-symbol entry with a nonzero final byte was accepted");
	entry.back() = 0;
	Check(!Loader::RuntimeLinkerBounds::IsNullDynamicSymbolEntryValid(
	          entry.data(), entry.size() - 1),
	      "a truncated reserved dynamic-symbol entry was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsNullDynamicSymbolEntryValid(nullptr, entry.size()),
	      "a null dynamic-symbol table pointer was accepted");

	std::array<uint8_t, Loader::RuntimeLinkerBounds::ELF64_SYM_SIZE * 2> symbols {};
	symbols.back() = 1;
	Check(Loader::RuntimeLinkerBounds::IsNullDynamicSymbolEntryValid(symbols.data(), symbols.size()),
	      "later dynamic-symbol data was incorrectly applied to the reserved first entry");
}

void TestDynamicSymbolTableAlignment() {
	using Loader::RuntimeLinkerBounds::IsDynamicSymbolTableAddressAligned;
	Check(IsDynamicSymbolTableAddressAligned(0),
	      "a dynamic-symbol table at address zero was rejected as misaligned");
	Check(IsDynamicSymbolTableAddressAligned(0x1000),
	      "an aligned dynamic-symbol table was rejected");
	Check(IsDynamicSymbolTableAddressAligned(std::numeric_limits<uint64_t>::max() - 7),
	      "the highest aligned dynamic-symbol table address was rejected");
	Check(!IsDynamicSymbolTableAddressAligned(0x1001),
	      "a misaligned dynamic-symbol table was accepted");
	Check(!IsDynamicSymbolTableAddressAligned(std::numeric_limits<uint64_t>::max()),
	      "a maximum-width misaligned dynamic-symbol table address was accepted");
}

void TestModuleLoadReferences() {
	using Loader::RuntimeLinkerLifecycle::ModuleReferenceState;
	using Loader::RuntimeLinkerLifecycle::ModuleReleaseAction;
	using Loader::RuntimeLinkerLifecycle::PrepareRelease;
	using Loader::RuntimeLinkerLifecycle::TryAddReference;

	ModuleReferenceState state {};
	Check(PrepareRelease(&state) == ModuleReleaseAction::Untracked && state.count == 0,
	      "an untracked module reference was treated as a live load");
	Check(TryAddReference(&state) && state.count == 1,
	      "an initial module load reference was not retained");
	Check(TryAddReference(&state) && state.count == 2,
	      "a repeated module load reference was not retained");
	Check(PrepareRelease(&state) == ModuleReleaseAction::Retained && state.count == 1,
	      "the first repeated-module release selected finalization");
	Check(PrepareRelease(&state) == ModuleReleaseAction::Final && state.count == 1,
	      "the final module release did not preserve state for stop/unload retry");

	ModuleReferenceState saturated {std::numeric_limits<uint64_t>::max()};
	Check(!TryAddReference(&saturated) &&
	          saturated.count == std::numeric_limits<uint64_t>::max(),
	      "an overflowing module reference count was accepted or mutated");
	Check(!TryAddReference(nullptr), "a null module reference state was retained");
	Check(PrepareRelease(nullptr) == ModuleReleaseAction::Untracked,
	      "a null module reference state selected an unload action");
}

void TestOptionalModuleLifecycleEntry() {
	int      calls        = 0;
	uint64_t called_entry = 0;
	auto invoke = [&](uint64_t entry) {
		calls++;
		called_entry = entry;
		return 7;
	};

	Check(Loader::RuntimeLinkerLifecycle::InvokeOptionalModuleLifecycleEntry(0, invoke) == 0 &&
	          calls == 0,
	      "an absent module lifecycle entry was invoked");
	Check(Loader::RuntimeLinkerLifecycle::InvokeOptionalModuleLifecycleEntry(0x1234, invoke) == 7 &&
	          calls == 1 && called_entry == 0x1234,
	      "a present module lifecycle entry was not invoked exactly once");
}

void TestRelaTableExtent() {
	constexpr uint64_t entry_size = Loader::RuntimeLinkerBounds::ELF64_RELA_SIZE;
	const SyntheticObject object {0x4000, entry_size * 4};

	Check(object.AcceptsRelaTable(0x4000 + entry_size * 2, entry_size * 2),
	      "an exact-end RELA table was rejected");
	Check(object.AcceptsRelaTable(0x4000 + entry_size * 4, 0),
	      "a zero-sized RELA table at the exact storage boundary was rejected");
	Check(!object.AcceptsRelaTable(0x4000 + entry_size * 2, entry_size * 2 + 1),
	      "a partial final RELA record was accepted");

	const SyntheticObject os_dynamic_data {0, entry_size * 4};
	Check(os_dynamic_data.AcceptsRelaTable(entry_size * 2, entry_size * 2),
	      "an offset-based dynamic-data table was rejected");

	const SyntheticObject truncated {0x4000, entry_size * 4 - 1};
	Check(!truncated.AcceptsRelaTable(0x4000 + entry_size * 2, entry_size * 2),
	      "a RELA table extending one byte beyond file-backed storage was accepted");

	const SyntheticObject overflowing {std::numeric_limits<uint64_t>::max() - 7, 16};
	Check(!overflowing.AcceptsRelaTable(std::numeric_limits<uint64_t>::max() - 7, entry_size),
	      "an overflowing file-backed segment extent was accepted");
	Check(!object.AcceptsRelaTable(0x4001, entry_size),
	      "a misaligned RELA table address was accepted");
	Check(!object.AcceptsRelaTable(0x4000, entry_size, entry_size - 8),
	      "a non-ELF64 RELA entry size was accepted");
}

void TestStandardStringExtentMetadata() {
	using Loader::RuntimeLinkerBounds::IsStandardStringExtentMetadataComplete;

	Check(IsStandardStringExtentMetadataComplete(false, false),
	      "an object without a standard dynamic string table was rejected");
	Check(IsStandardStringExtentMetadataComplete(true, true),
	      "a standard dynamic string table with an explicit size was rejected");
	Check(!IsStandardStringExtentMetadataComplete(true, false),
	      "a standard dynamic string table without a size tag was accepted");
	Check(IsStandardStringExtentMetadataComplete(false, true),
	      "a size-only state was broadened into the table-address companion rule");
}

void TestStandardSymbolTableBound() {
	using Loader::RuntimeLinkerBounds::IsStandardSymbolTableBoundAvailable;

	Check(IsStandardSymbolTableBoundAvailable(false, false),
	      "an object without a standard dynamic symbol table was rejected");
	Check(IsStandardSymbolTableBoundAvailable(true, true),
	      "a standard dynamic symbol table with a supported hash bound was rejected");
	Check(!IsStandardSymbolTableBoundAvailable(true, false),
	      "a standard dynamic symbol table without a supported bound was accepted");
	Check(IsStandardSymbolTableBoundAvailable(false, true),
	      "a hash-only state was broadened into the symbol-table companion rule");
}

void TestJmprelExtentMetadata() {
	using Loader::RuntimeLinkerBounds::IsJmprelExtentMetadataComplete;

	Check(IsJmprelExtentMetadataComplete(false, false),
	      "an object without PLT relocation metadata was rejected");
	Check(IsJmprelExtentMetadataComplete(true, true),
	      "a JMPREL table with an explicit size tag was rejected");
	Check(!IsJmprelExtentMetadataComplete(true, false),
	      "a JMPREL table without a size tag was accepted");
	Check(IsJmprelExtentMetadataComplete(false, true),
	      "a size-only state was broadened into the address-companion rule");
}

void TestRelaExtentMetadata() {
	using Loader::RuntimeLinkerBounds::IsRelaExtentMetadataComplete;

	Check(IsRelaExtentMetadataComplete(false, false),
	      "an object without ordinary relocation metadata was rejected");
	Check(IsRelaExtentMetadataComplete(true, true),
	      "a RELA table with an explicit size tag was rejected");
	Check(!IsRelaExtentMetadataComplete(true, false),
	      "a RELA table without a size tag was accepted");
	Check(IsRelaExtentMetadataComplete(false, true),
	      "a size-only state was broadened into the address-companion rule");
}

void TestRelaTableIndex() {
	constexpr uint64_t entry_size = Loader::RuntimeLinkerBounds::ELF64_RELA_SIZE;
	constexpr uint64_t table_size = entry_size * 4;

	Check(Loader::RuntimeLinkerBounds::IsRelaIndexValid(3, table_size),
	      "the exact last RELA index was rejected");
	Check(!Loader::RuntimeLinkerBounds::IsRelaIndexValid(4, table_size),
	      "a one-past RELA index was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsRelaIndexValid(
	          std::numeric_limits<uint64_t>::max(), table_size),
	      "a maximum-width RELA index was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsRelaIndexValid(0, 0),
	      "an index into an empty RELA table was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsRelaIndexValid(0, entry_size - 1),
	      "an index without one complete RELA record was accepted");
}

void TestRelativeRelocationSymbol() {
	using namespace Loader::RuntimeLinkerBounds;

	Check(IsRelativeRelocationSymbolValid(ELF64_R_X86_64_RELATIVE, 0),
	      "a relative relocation with no symbol operand was rejected");
	Check(!IsRelativeRelocationSymbolValid(ELF64_R_X86_64_RELATIVE, 1),
	      "a relative relocation with a symbol operand was accepted");
	Check(!IsRelativeRelocationSymbolValid(
	          ELF64_R_X86_64_RELATIVE, std::numeric_limits<uint32_t>::max()),
	      "a relative relocation with a maximum symbol index was accepted");
	Check(IsRelativeRelocationSymbolValid(7, 1),
	      "a symbol-consuming relocation was subjected to the relative-only rule");
}

void TestSupportedRelocationTypes() {
	using Loader::RuntimeLinkerBounds::IsSupportedRelocationType;

	for (const uint32_t type: std::array<uint32_t, 5> {1, 6, 7, 8, 16}) {
		Check(IsSupportedRelocationType(type), "an observed relocation family was rejected");
	}
	for (const uint32_t type:
	     std::array<uint32_t, 6> {0, 2, 5, 9, 17, std::numeric_limits<uint32_t>::max()}) {
		Check(!IsSupportedRelocationType(type), "an unsupported relocation family was accepted");
	}
}

void TestSupportedRelocationRecords() {
	using namespace Loader::RuntimeLinkerBounds;
	struct SyntheticRelocation {
		uint32_t type;
		uint32_t symbol;
	};
	const auto records_are_supported = [](const auto& records) {
		for (const auto& record: records) {
			if (!IsSupportedRelocationRecord(record.type, record.symbol)) {
				return false;
			}
		}
		return true;
	};

	for (const uint32_t type:
	     std::array<uint32_t, 4> {ELF64_R_X86_64_64, ELF64_R_X86_64_GLOB_DAT,
	                              ELF64_R_X86_64_JUMP_SLOT, ELF64_R_X86_64_DTPMOD64}) {
		Check(IsSupportedRelocationRecord(type, 1),
		      "a supported non-relative relocation record was rejected");
		Check(IsSupportedRelocationRecord(type, std::numeric_limits<uint32_t>::max()),
		      "a non-relative relocation was subjected to the relative-only symbol rule");
	}

	Check(IsSupportedRelocationRecord(ELF64_R_X86_64_RELATIVE, 0),
	      "a relative relocation without a symbol operand was rejected");
	Check(!IsSupportedRelocationRecord(ELF64_R_X86_64_RELATIVE, 1),
	      "a relative relocation with a symbol operand was accepted for preflight");
	Check(!IsSupportedRelocationRecord(ELF64_R_X86_64_RELATIVE,
	                                   std::numeric_limits<uint32_t>::max()),
	      "a relative relocation with a maximum symbol index was accepted for preflight");
	Check(!IsSupportedRelocationRecord(0, 0),
	      "an unsupported relocation family was accepted by record preflight");

	Check(records_are_supported(std::array<SyntheticRelocation, 2> {{
	          {ELF64_R_X86_64_RELATIVE, 0}, {ELF64_R_X86_64_GLOB_DAT, 1}}}),
	      "a valid synthetic relocation table was rejected");
	Check(!records_are_supported(std::array<SyntheticRelocation, 2> {{
	          {ELF64_R_X86_64_GLOB_DAT, 1}, {ELF64_R_X86_64_RELATIVE, 1}}}),
	      "a malformed later relative relocation passed whole-table preflight");
}

void TestSupportedRelocationSymbolEntries() {
	using namespace Loader::RuntimeLinkerBounds;

	for (const uint32_t relocation_type:
	     std::array<uint32_t, 3> {ELF64_R_X86_64_64, ELF64_R_X86_64_GLOB_DAT,
	                              ELF64_R_X86_64_JUMP_SLOT}) {
		for (const uint8_t binding:
		     std::array<uint8_t, 3> {ELF64_STB_LOCAL, ELF64_STB_GLOBAL, ELF64_STB_WEAK}) {
			for (const uint8_t symbol_type:
			     std::array<uint8_t, 3> {ELF64_STT_NOTYPE, ELF64_STT_OBJECT,
			                             ELF64_STT_FUNC}) {
				Check(IsSupportedRelocationSymbolEntry(relocation_type, binding, symbol_type),
				      "a relocation symbol entry already supported by execution was rejected");
			}
		}

		Check(!IsSupportedRelocationSymbolEntry(relocation_type, 3, ELF64_STT_FUNC),
		      "an unsupported relocation symbol binding was accepted");
		Check(!IsSupportedRelocationSymbolEntry(relocation_type, ELF64_STB_GLOBAL, 3),
		      "an unsupported relocation symbol type was accepted");
		Check(!IsSupportedRelocationSymbolEntry(
		          relocation_type, std::numeric_limits<uint8_t>::max(), ELF64_STT_FUNC),
		      "a maximum-width relocation symbol binding was accepted");
		Check(!IsSupportedRelocationSymbolEntry(
		          relocation_type, ELF64_STB_GLOBAL, std::numeric_limits<uint8_t>::max()),
		      "a maximum-width relocation symbol type was accepted");
	}

	Check(IsSupportedRelocationSymbolEntry(ELF64_R_X86_64_RELATIVE,
	                                       std::numeric_limits<uint8_t>::max(),
	                                       std::numeric_limits<uint8_t>::max()),
	      "a non-symbol relative relocation was subjected to symbol-entry validation");
	Check(IsSupportedRelocationSymbolEntry(ELF64_R_X86_64_DTPMOD64,
	                                       std::numeric_limits<uint8_t>::max(),
	                                       std::numeric_limits<uint8_t>::max()),
	      "a non-symbol TLS relocation was subjected to symbol-entry validation");
}

void TestRelocationTargetExtent() {
	constexpr uint64_t segment_vaddr = 0x4000;
	constexpr uint64_t segment_size  = 0x100;
	constexpr uint64_t target_size =
	    Loader::RuntimeLinkerBounds::ELF64_RELOCATION_TARGET_SIZE;

	Check(Loader::RuntimeLinkerBounds::IsRelocationTargetRangeValid(
	          segment_vaddr, segment_vaddr, segment_size),
	      "a relocation target at the start of a mapped segment was rejected");
	Check(Loader::RuntimeLinkerBounds::IsRelocationTargetRangeValid(
	          segment_vaddr + segment_size - target_size, segment_vaddr, segment_size),
	      "an exact-end relocation target was rejected");
	Check(Loader::RuntimeLinkerBounds::IsRelocationTargetRangeValid(
	          std::numeric_limits<uint64_t>::max() - target_size,
	          std::numeric_limits<uint64_t>::max() - target_size, target_size),
	      "the highest representable exact-end relocation target was rejected");
	Check(!Loader::RuntimeLinkerBounds::IsRelocationTargetRangeValid(
	          segment_vaddr + segment_size - target_size + 1, segment_vaddr, segment_size),
	      "a relocation target truncated by one byte was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsRelocationTargetRangeValid(
	          segment_vaddr + segment_size, segment_vaddr, segment_size),
	      "a one-past relocation target was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsRelocationTargetRangeValid(
	          segment_vaddr - 1, segment_vaddr, segment_size),
	      "a relocation target before the mapped segment was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsRelocationTargetRangeValid(
	          std::numeric_limits<uint64_t>::max() - target_size + 1,
	          std::numeric_limits<uint64_t>::max() - target_size + 1, target_size),
	      "an overflowing relocation target or segment extent was accepted");
}

void TestPltGotResolverExtent() {
	constexpr uint64_t segment_vaddr = 0x4000;
	constexpr uint64_t segment_size  = 0x100;
	constexpr uint64_t resolver_size =
	    Loader::RuntimeLinkerBounds::ELF64_PLTGOT_RESOLVER_SIZE;

	Check(Loader::RuntimeLinkerBounds::IsPltGotResolverRangeValid(
	          segment_vaddr, segment_vaddr, segment_size),
	      "a PLTGOT resolver block at the start of a mapped segment was rejected");
	Check(Loader::RuntimeLinkerBounds::IsPltGotResolverRangeValid(
	          segment_vaddr + segment_size - resolver_size, segment_vaddr, segment_size),
	      "an exact-end PLTGOT resolver block was rejected");
	Check(!Loader::RuntimeLinkerBounds::IsPltGotResolverRangeValid(
	          segment_vaddr + 1, segment_vaddr, segment_size),
	      "a misaligned PLTGOT resolver block was accepted");
	Check(Loader::RuntimeLinkerBounds::IsPltGotResolverRangeValid(
	          std::numeric_limits<uint64_t>::max() - 31,
	          std::numeric_limits<uint64_t>::max() - 31, resolver_size),
	      "a high aligned exact-end PLTGOT resolver block was rejected");
	Check(!Loader::RuntimeLinkerBounds::IsPltGotResolverRangeValid(
	          segment_vaddr + segment_size - resolver_size, segment_vaddr, segment_size - 1),
	      "a PLTGOT resolver block truncated by one byte was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsPltGotResolverRangeValid(
	          segment_vaddr + segment_size, segment_vaddr, segment_size),
	      "a one-past PLTGOT resolver block was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsPltGotResolverRangeValid(
	          segment_vaddr - 8, segment_vaddr, segment_size),
	      "a PLTGOT resolver block before the mapped segment was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsPltGotResolverRangeValid(
	          std::numeric_limits<uint64_t>::max() - resolver_size + 1,
	          std::numeric_limits<uint64_t>::max() - resolver_size + 1, resolver_size),
	      "an overflowing PLTGOT resolver block or segment extent was accepted");
}

void TestDynamicLifecycleEntry() {
	constexpr uint64_t segment_vaddr = 0x4000;
	constexpr uint64_t segment_size  = 0x100;

	Check(Loader::RuntimeLinkerBounds::IsExecutableEntryInSegment(
	          segment_vaddr, segment_vaddr, segment_size, true),
	      "an entry at the start of an executable segment was rejected");
	Check(Loader::RuntimeLinkerBounds::IsExecutableEntryInSegment(
	          segment_vaddr + segment_size - 1, segment_vaddr, segment_size, true),
	      "an entry at the final file-backed byte was rejected");
	Check(!Loader::RuntimeLinkerBounds::IsExecutableEntryInSegment(
	          segment_vaddr + segment_size, segment_vaddr, segment_size, true),
	      "a one-past lifecycle entry was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsExecutableEntryInSegment(
	          segment_vaddr, segment_vaddr, segment_size, false),
	      "a lifecycle entry in a non-executable segment was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsExecutableEntryInSegment(
	          std::numeric_limits<uint64_t>::max() - 7,
	          std::numeric_limits<uint64_t>::max() - 7, 16, true),
	      "a lifecycle entry in an overflowing segment was accepted");
}

void TestLifecycleArrayExtent() {
	constexpr uint64_t segment_vaddr = 0x4000;
	constexpr uint64_t segment_size  = 0x100;

	Check(Loader::RuntimeLinkerBounds::IsLifecycleArrayRangeValid(
	          segment_vaddr + segment_size - 16, 16, segment_vaddr, segment_size),
	      "an exact-end lifecycle array was rejected");
	Check(Loader::RuntimeLinkerBounds::IsLifecycleArrayRangeValid(
	          std::numeric_limits<uint64_t>::max(), 0,
	          std::numeric_limits<uint64_t>::max() - 7, 16),
	      "a zero-sized lifecycle array was rejected");
	Check(!Loader::RuntimeLinkerBounds::IsLifecycleArrayRangeValid(
	          segment_vaddr, Loader::RuntimeLinkerBounds::ELF64_ADDR_SIZE - 1,
	          segment_vaddr, segment_size),
	      "a lifecycle array with a partial pointer was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsLifecycleArrayRangeValid(
	          segment_vaddr + segment_size - 8, 16, segment_vaddr, segment_size),
	      "a lifecycle array extending beyond file-backed storage was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsLifecycleArrayRangeValid(
	          std::numeric_limits<uint64_t>::max() - 7, 8,
	          std::numeric_limits<uint64_t>::max() - 7, 16),
	      "a lifecycle array in an overflowing segment was accepted");
}

void TestRelocationSymbolIndex() {
	constexpr uint64_t entry_size = Loader::RuntimeLinkerBounds::ELF64_SYM_SIZE;
	const SyntheticSymbolTable symbols {entry_size * 4};

	Check(symbols.Contains(3), "the exact last dynamic symbol was rejected");
	Check(!symbols.Contains(4), "a one-past dynamic symbol index was accepted");
	Check(!symbols.Contains(std::numeric_limits<uint32_t>::max()),
	      "a large dynamic symbol index was accepted");
	Check(!SyntheticSymbolTable {0}.Contains(0),
	      "an index into a declared empty symbol table was accepted");
	Check(!SyntheticSymbolTable {entry_size * 4 - 1}.Contains(3),
	      "a symbol without a complete table entry was accepted");
	Check(!SyntheticSymbolTable {entry_size * 4, entry_size - 8}.Contains(0),
	      "an index into a table with the wrong entry size was accepted");
}

void TestSysvHashAlignment() {
	using Loader::RuntimeLinkerBounds::IsSysvHashAddressAligned;
	Check(IsSysvHashAddressAligned(0),
	      "a System V hash table at address zero was rejected as misaligned");
	Check(IsSysvHashAddressAligned(0x4000),
	      "an aligned System V hash table was rejected");
	Check(IsSysvHashAddressAligned(std::numeric_limits<uint64_t>::max() - 3),
	      "the highest aligned System V hash table address was rejected");
	Check(!IsSysvHashAddressAligned(0x4001),
	      "a misaligned System V hash table was accepted");
	Check(!IsSysvHashAddressAligned(std::numeric_limits<uint64_t>::max()),
	      "a maximum-width misaligned System V hash table address was accepted");
}

void TestSysvHashDerivedSymbolCount() {
	constexpr uint64_t bucket_count = 4;
	constexpr uint64_t symbol_count = 5;
	constexpr uint64_t hash_size =
	    Loader::RuntimeLinkerBounds::SYSV_HASH_HEADER_SIZE +
	    (bucket_count + symbol_count) * Loader::RuntimeLinkerBounds::SYSV_HASH_WORD_SIZE;
	constexpr uint64_t symbol_table_size =
	    symbol_count * Loader::RuntimeLinkerBounds::ELF64_SYM_SIZE;

	Loader::RuntimeLinkerBounds::SysvHashLayout layout {};
	Check(Loader::RuntimeLinkerBounds::DecodeSysvHashLayout(
	          bucket_count, symbol_count, hash_size, true, &layout),
	      "a valid explicitly sized System V hash table was rejected");
	Check(layout.table_size == hash_size && layout.symbol_count == symbol_count,
	      "a System V hash header produced an incorrect extent or symbol count");
	Check(Loader::RuntimeLinkerBounds::IsTableRangeValid(
	          0x4000 + 0x100 - hash_size, layout.table_size, 0x4000, 0x100),
	      "an exact-end System V hash table was rejected");

	uint64_t derived_symbol_table_size = 0;
	Check(Loader::RuntimeLinkerBounds::DecodeDynamicSymbolTableSize(
	          layout.symbol_count, Loader::RuntimeLinkerBounds::ELF64_SYM_SIZE,
	          symbol_table_size, true, &derived_symbol_table_size),
	      "a matching explicit dynamic-symbol size was rejected");
	Check(derived_symbol_table_size == symbol_table_size,
	      "the System V chain count produced an incorrect dynamic-symbol size");
	Check(Loader::RuntimeLinkerBounds::IsTableRangeValid(
	          0x5000 + 0x100 - derived_symbol_table_size, derived_symbol_table_size,
	          0x5000, 0x100),
	      "an exact-end hash-derived dynamic-symbol table was rejected");

	Check(Loader::RuntimeLinkerBounds::DecodeSysvHashLayout(
	          bucket_count, symbol_count, 0, false, &layout) &&
	          Loader::RuntimeLinkerBounds::DecodeDynamicSymbolTableSize(
	              layout.symbol_count, Loader::RuntimeLinkerBounds::ELF64_SYM_SIZE, 0, false,
	              &derived_symbol_table_size),
	      "size-less standard metadata did not derive its hash and symbol extents");
	Check(!Loader::RuntimeLinkerBounds::DecodeSysvHashLayout(
	          bucket_count, symbol_count, hash_size + 4, true, &layout),
	      "a mismatched explicit hash-table size was accepted");
	Check(!Loader::RuntimeLinkerBounds::DecodeDynamicSymbolTableSize(
	          symbol_count, Loader::RuntimeLinkerBounds::ELF64_SYM_SIZE,
	          symbol_table_size + Loader::RuntimeLinkerBounds::ELF64_SYM_SIZE, true,
	          &derived_symbol_table_size),
	      "a mismatched explicit dynamic-symbol size was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsTableRangeValid(0x4000 + 0x100 - hash_size,
	                                                     hash_size + 1, 0x4000, 0x100),
	      "a truncated System V hash-table extent was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsTableRangeValid(
	          0x5000 + 0x100 - symbol_table_size, symbol_table_size + 1,
	          0x5000, 0x100),
	      "a truncated hash-derived dynamic-symbol extent was accepted");
	Check(!Loader::RuntimeLinkerBounds::DecodeDynamicSymbolTableSize(
	          symbol_count, Loader::RuntimeLinkerBounds::ELF64_SYM_SIZE - 8, 0, false,
	          &derived_symbol_table_size),
	      "a non-ELF64 dynamic-symbol entry size was accepted");
	Check(!Loader::RuntimeLinkerBounds::DecodeSysvHashLayout(
	          std::numeric_limits<uint64_t>::max(), 1, 0, false, &layout),
	      "overflowing System V hash counts were accepted");
	Check(!Loader::RuntimeLinkerBounds::DecodeDynamicSymbolTableSize(
	          std::numeric_limits<uint64_t>::max(),
	          Loader::RuntimeLinkerBounds::ELF64_SYM_SIZE, 0, false,
	          &derived_symbol_table_size),
	      "an overflowing hash-derived dynamic-symbol size was accepted");
}

void TestDynamicSymbolName() {
	const std::array<char, 4> strings {'a', 'b', '\0', '\0'};
	Check(Loader::RuntimeLinkerBounds::GetBoundedString(strings.data(), strings.size(), 0) ==
	          strings.data(),
	      "an in-range terminated symbol name was rejected");
	Check(Loader::RuntimeLinkerBounds::GetBoundedString(strings.data(), strings.size(), 3) ==
	          strings.data() + 3,
	      "an empty symbol name ending exactly at the table boundary was rejected");
	Check(Loader::RuntimeLinkerBounds::GetBoundedString(strings.data(), strings.size(), 4) == nullptr,
	      "a one-past symbol-name offset was accepted");
	Check(Loader::RuntimeLinkerBounds::GetBoundedString(
	          strings.data(), strings.size(), std::numeric_limits<uint64_t>::max()) == nullptr,
	      "an overflowing symbol-name offset was accepted");

	const std::array<char, 4> unterminated {'a', 'b', 'c', 'd'};
	Check(Loader::RuntimeLinkerBounds::GetBoundedString(unterminated.data(), unterminated.size(), 0) ==
	          nullptr,
	      "an unterminated symbol name was accepted");
	Check(Loader::RuntimeLinkerBounds::GetBoundedString(nullptr, strings.size(), 0) == nullptr,
	      "a null string table was accepted");
}

void TestRelocationSymbolNamePreflight() {
	using Loader::RuntimeLinkerBounds::IsRelocationSymbolNameValid;

	const std::array<char, 5> strings {'\0', 'f', 'o', 'o', '\0'};
	Check(IsRelocationSymbolNameValid(Loader::RuntimeLinkerBounds::ELF64_STB_LOCAL, nullptr, 0,
	                                  true, std::numeric_limits<uint32_t>::max()),
	      "a local relocation symbol incorrectly required a name");
	Check(IsRelocationSymbolNameValid(Loader::RuntimeLinkerBounds::ELF64_STB_GLOBAL,
	                                  strings.data(), strings.size(), true, 1),
	      "a bounded global relocation symbol name was rejected");
	Check(IsRelocationSymbolNameValid(Loader::RuntimeLinkerBounds::ELF64_STB_WEAK, strings.data(),
	                                  strings.size(), true, 4),
	      "an exact-end weak relocation symbol name was rejected");
	Check(!IsRelocationSymbolNameValid(Loader::RuntimeLinkerBounds::ELF64_STB_GLOBAL,
	                                   strings.data(), strings.size(), true, strings.size()),
	      "a one-past relocation symbol name was accepted");
	const std::array<char, 3> unterminated {'b', 'a', 'd'};
	Check(!IsRelocationSymbolNameValid(Loader::RuntimeLinkerBounds::ELF64_STB_WEAK,
	                                   unterminated.data(), unterminated.size(), true, 0),
	      "an unterminated relocation symbol name was accepted");
	Check(IsRelocationSymbolNameValid(Loader::RuntimeLinkerBounds::ELF64_STB_GLOBAL,
	                                  strings.data(), 0, false, 1),
	      "a legacy size-less relocation symbol name changed behavior");
}

void TestDynamicTagString() {
	const std::array<char, 8> strings {'\0', 'l', 'i', 'b', '\0', 'x', 'y', 'z'};
	Check(Loader::RuntimeLinkerBounds::GetDynamicString(
	          strings.data(), strings.size(), true, 1) == strings.data() + 1,
	      "an in-range dynamic-tag string was rejected");
	Check(Loader::RuntimeLinkerBounds::GetDynamicString(
	          strings.data(), strings.size(), true, 5) == nullptr,
	      "an unterminated dynamic-tag string was accepted");
	Check(Loader::RuntimeLinkerBounds::GetDynamicString(
	          strings.data(), strings.size(), true, strings.size()) == nullptr,
	      "a one-past dynamic-tag string offset was accepted");

	constexpr uint64_t platform_value = 0x1234567800000005;
	const auto platform_offset =
	    Loader::RuntimeLinkerBounds::GetPlatformStringOffset(platform_value);
	Check(platform_offset == 5, "a platform dynamic-tag string offset retained metadata bits");
	Check(Loader::RuntimeLinkerBounds::GetDynamicString(
	          strings.data(), strings.size(), true, platform_offset) == nullptr,
	      "an unterminated platform dynamic-tag string was accepted");

	Check(Loader::RuntimeLinkerBounds::GetDynamicString(
	          strings.data(), 0, false, 1) == strings.data() + 1,
	      "a string table without an explicit size lost its preserved lookup path");
}

void TestDynamicStringTableShape() {
	const std::array<char, 1> empty_string {'\0'};
	const std::array<char, 4> strings {'\0', 'a', '\0', '\0'};
	const std::array<char, 4> bad_first {'x', 'a', '\0', '\0'};
	const std::array<char, 4> bad_last {'\0', 'a', '\0', 'x'};

	Check(Loader::RuntimeLinkerBounds::IsDynamicStringTableShapeValid(
	          empty_string.data(), empty_string.size()),
	      "a one-byte empty dynamic string table was rejected");
	Check(Loader::RuntimeLinkerBounds::IsDynamicStringTableShapeValid(
	          strings.data(), strings.size()),
	      "a valid dynamic string table was rejected");
	Check(!Loader::RuntimeLinkerBounds::IsDynamicStringTableShapeValid(
	          bad_first.data(), bad_first.size()),
	      "a dynamic string table without the initial sentinel was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsDynamicStringTableShapeValid(
	          bad_last.data(), bad_last.size()),
	      "a dynamic string table without the final sentinel was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsDynamicStringTableShapeValid(strings.data(), 0),
	      "a zero-sized dynamic string table was accepted");
	Check(!Loader::RuntimeLinkerBounds::IsDynamicStringTableShapeValid(nullptr, strings.size()),
	      "a null dynamic string table was accepted");
}

void TestTlsLayout() {
	Loader::RuntimeLinkerBounds::TlsLayout layout {};
	Check(Loader::RuntimeLinkerBounds::DecodeTlsLayout(0x200, 0x1200, 4, 10, 8, &layout),
	      "a valid TLS template layout was rejected");
	Check(layout.init_size == 4 && layout.image_size == 16 && layout.tcb_offset == 32 &&
	          layout.allocation_size == 96,
	      "a valid TLS template produced incorrect image, TCB, or allocation layout");
	constexpr uint64_t max_runtime_tls_size =
	    std::numeric_limits<uint64_t>::max() - 0x1f - 0x40;
	Check(Loader::RuntimeLinkerBounds::DecodeTlsLayout(
	          0, 0, 0, max_runtime_tls_size, 1, &layout),
	      "the largest TLS template with a representable runtime allocation was rejected");
	Check(layout.init_size == 0 && layout.image_size == max_runtime_tls_size &&
	          layout.tcb_offset == max_runtime_tls_size &&
	          layout.allocation_size == max_runtime_tls_size + 0x40,
	      "the exact-end runtime TLS boundary was decoded incorrectly");
	Check(!Loader::RuntimeLinkerBounds::DecodeTlsLayout(
	          0, 0, 0, max_runtime_tls_size + 1, 1, &layout),
	      "a TLS template whose aligned TCB allocation overflows was accepted");

	Check(Loader::RuntimeLinkerBounds::DecodeTlsLayout(0, 0, 0, 0, 0, &layout),
	      "an empty unaligned TLS template was rejected");
	Check(layout.init_size == 0 && layout.image_size == 0 && layout.tcb_offset == 0 &&
	          layout.allocation_size == 0x40,
	      "an empty TLS template produced a nonempty layout");

	Check(!Loader::RuntimeLinkerBounds::DecodeTlsLayout(0, 0, 9, 8, 8, &layout),
	      "a TLS initialization image larger than its template was accepted");
	Check(!Loader::RuntimeLinkerBounds::DecodeTlsLayout(0, 0, 0, 8, 3, &layout),
	      "a TLS template with non-power-of-two alignment was accepted");
	Check(!Loader::RuntimeLinkerBounds::DecodeTlsLayout(0, 1, 0, 8, 8, &layout),
	      "an incongruent TLS file and virtual address pair was accepted");
	Check(!Loader::RuntimeLinkerBounds::DecodeTlsLayout(
	          0, 0, 0, std::numeric_limits<uint64_t>::max(), 16, &layout),
	      "an overflowing aligned TLS template size was accepted");
	Check(!Loader::RuntimeLinkerBounds::DecodeTlsLayout(0, 0, 0, 0, 1, nullptr),
	      "a TLS layout decoder accepted a null output");
}

} // namespace

int main() {
	TestRelocatedRange();
	TestRelativeRelocationValue();
	TestSymbolRelocationValue();
	TestRelroMappedRange();
	TestRelroRelocationLifecycle();
	TestDynamicSymbolAddress();
	TestDynamicSymbolResolution();
	TestRegularDynamicFunctionDefinition();
	TestRegularDynamicObjectDefinition();
	TestDynamicObjectExtent();
	TestNullDynamicSymbolEntry();
	TestDynamicSymbolTableAlignment();
	TestModuleLoadReferences();
	TestOptionalModuleLifecycleEntry();
	TestRelaTableExtent();
	TestStandardStringExtentMetadata();
	TestStandardSymbolTableBound();
	TestJmprelExtentMetadata();
	TestRelaExtentMetadata();
	TestRelaTableIndex();
	TestRelativeRelocationSymbol();
	TestSupportedRelocationTypes();
	TestSupportedRelocationRecords();
	TestSupportedRelocationSymbolEntries();
	TestRelocationTargetExtent();
	TestPltGotResolverExtent();
	TestDynamicLifecycleEntry();
	TestLifecycleArrayExtent();
	TestRelocationSymbolIndex();
	TestSysvHashAlignment();
	TestSysvHashDerivedSymbolCount();
	TestDynamicSymbolName();
	TestRelocationSymbolNamePreflight();
	TestDynamicTagString();
	TestDynamicStringTableShape();
	TestTlsLayout();
	std::printf("RuntimeLinkerBoundsTests: all cases passed\n");
	return 0;
}
