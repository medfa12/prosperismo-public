#ifndef EMULATOR_INCLUDE_EMULATOR_LOADER_RUNTIMELINKERBOUNDS_H_
#define EMULATOR_INCLUDE_EMULATOR_LOADER_RUNTIMELINKERBOUNDS_H_

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>

namespace Loader::RuntimeLinkerBounds {

constexpr uint64_t ELF64_RELA_SIZE              = 24;
constexpr uint64_t ELF64_RELA_ALIGNMENT         = 8;
constexpr uint64_t ELF64_RELOCATION_TARGET_SIZE = 8;
constexpr uint64_t ELF64_PLTGOT_RESOLVER_SIZE   = 24;
constexpr uint64_t ELF64_PLTGOT_ALIGNMENT       = 8;
constexpr uint64_t ELF64_SYM_SIZE               = 24;
constexpr uint64_t ELF64_SYM_ALIGNMENT          = 8;
constexpr uint64_t ELF64_ADDR_SIZE              = 8;
constexpr uint64_t RELRO_PAGE_SIZE               = 0x4000;
constexpr uint32_t ELF64_R_X86_64_64            = 1;
constexpr uint32_t ELF64_R_X86_64_GLOB_DAT      = 6;
constexpr uint32_t ELF64_R_X86_64_JUMP_SLOT     = 7;
constexpr uint32_t ELF64_R_X86_64_RELATIVE      = 8;
constexpr uint32_t ELF64_R_X86_64_DTPMOD64      = 16;
constexpr uint64_t SYSV_HASH_HEADER_SIZE         = 8;
constexpr uint64_t SYSV_HASH_WORD_SIZE           = 4;
constexpr uint64_t SYSV_HASH_ALIGNMENT           = SYSV_HASH_WORD_SIZE;
constexpr uint64_t TLS_TCB_SIZE                  = 0x40;
constexpr uint64_t TLS_TCB_ALIGNMENT             = 0x20;
constexpr uint16_t ELF64_SHN_UNDEF                = 0;
constexpr uint16_t ELF64_SHN_LORESERVE            = 0xff00;
constexpr uint16_t ELF64_SHN_ABS                  = 0xfff1;
constexpr uint8_t  ELF64_STB_LOCAL                = 0;
constexpr uint8_t  ELF64_STB_GLOBAL               = 1;
constexpr uint8_t  ELF64_STB_WEAK                 = 2;
constexpr uint8_t  ELF64_STT_NOTYPE               = 0;
constexpr uint8_t  ELF64_STT_OBJECT               = 1;
constexpr uint8_t  ELF64_STT_FUNC                 = 2;

struct TlsLayout {
	uint64_t init_size       = 0;
	uint64_t image_size      = 0;
	uint64_t tcb_offset      = 0;
	uint64_t allocation_size = 0;
};

struct TlsAllocationLayout {
	uint64_t tcb_offset      = 0;
	uint64_t allocation_size = 0;
};

struct SysvHashLayout {
	uint64_t table_size   = 0;
	uint64_t symbol_count = 0;
};

[[nodiscard]] constexpr bool IsSysvHashAddressAligned(uint64_t table_vaddr) {
	return table_vaddr % SYSV_HASH_ALIGNMENT == 0;
}

[[nodiscard]] constexpr bool DecodeRelocatedRange(uint64_t base_vaddr,
                                                  uint64_t relative_vaddr,
                                                  uint64_t size,
                                                  uint64_t* out_vaddr) {
	if (out_vaddr == nullptr ||
	    relative_vaddr > std::numeric_limits<uint64_t>::max() - base_vaddr) {
		return false;
	}

	const uint64_t vaddr = base_vaddr + relative_vaddr;
	if (size > std::numeric_limits<uint64_t>::max() - vaddr) {
		return false;
	}

	*out_vaddr = vaddr;
	return true;
}

[[nodiscard]] constexpr bool DecodeRelativeRelocationValue(uint64_t base_vaddr, int64_t addend,
                                                           uint64_t* out_vaddr) {
	if (out_vaddr == nullptr) {
		return false;
	}

	if (addend >= 0) {
		const auto amount = static_cast<uint64_t>(addend);
		if (amount > std::numeric_limits<uint64_t>::max() - base_vaddr) {
			return false;
		}
		*out_vaddr = base_vaddr + amount;
		return true;
	}

	const auto magnitude = static_cast<uint64_t>(-(addend + 1)) + 1;
	if (magnitude > base_vaddr) {
		return false;
	}
	*out_vaddr = base_vaddr - magnitude;
	return true;
}

[[nodiscard]] constexpr bool DecodeSymbolRelocationValue(uint64_t symbol_vaddr, int64_t addend,
                                                         uint64_t* out_vaddr) {
	return DecodeRelativeRelocationValue(symbol_vaddr, addend, out_vaddr);
}

[[nodiscard]] constexpr bool DecodeDynamicSymbolAddress(uint16_t section_index,
                                                        uint64_t symbol_value,
                                                        uint64_t base_vaddr,
                                                        uint64_t* out_vaddr) {
	if (out_vaddr == nullptr || section_index == ELF64_SHN_UNDEF) {
		return false;
	}

	if (section_index == ELF64_SHN_ABS) {
		*out_vaddr = symbol_value;
		return true;
	}

	return DecodeRelocatedRange(base_vaddr, symbol_value, 0, out_vaddr);
}

[[nodiscard]] constexpr bool IsRegularDynamicFunctionDefinition(uint16_t section_index,
                                                                uint8_t symbol_type) {
	return symbol_type == ELF64_STT_FUNC && section_index != ELF64_SHN_UNDEF &&
	       section_index < ELF64_SHN_LORESERVE;
}

[[nodiscard]] constexpr bool IsRegularDynamicObjectDefinition(uint16_t section_index,
                                                              uint8_t symbol_type,
                                                              uint64_t object_size) {
	return object_size != 0 && symbol_type == ELF64_STT_OBJECT &&
	       section_index != ELF64_SHN_UNDEF && section_index < ELF64_SHN_LORESERVE;
}

[[nodiscard]] constexpr bool IsDynamicSymbolResolutionComplete(uint16_t section_index,
                                                               uint8_t binding,
                                                               uint64_t resolved_address) {
	return resolved_address != 0 ||
	       (section_index == ELF64_SHN_UNDEF && binding == ELF64_STB_WEAK);
}

[[nodiscard]] constexpr bool DecodeSysvHashLayout(uint64_t bucket_count,
                                                  uint64_t chain_count,
                                                  uint64_t declared_size,
                                                  bool declared_size_known,
                                                  SysvHashLayout* out) {
	if (out == nullptr ||
	    bucket_count > std::numeric_limits<uint64_t>::max() - chain_count) {
		return false;
	}

	const uint64_t word_count = bucket_count + chain_count;
	if (word_count >
	    (std::numeric_limits<uint64_t>::max() - SYSV_HASH_HEADER_SIZE) /
	        SYSV_HASH_WORD_SIZE) {
		return false;
	}

	const uint64_t table_size =
	    SYSV_HASH_HEADER_SIZE + word_count * SYSV_HASH_WORD_SIZE;
	if (declared_size_known && declared_size != table_size) {
		return false;
	}

	out->table_size   = table_size;
	out->symbol_count = chain_count;
	return true;
}

[[nodiscard]] constexpr bool DecodeDynamicSymbolTableSize(
    uint64_t symbol_count, uint64_t entry_size, uint64_t declared_size,
    bool declared_size_known, uint64_t* out) {
	if (out == nullptr || entry_size != ELF64_SYM_SIZE ||
	    symbol_count > std::numeric_limits<uint64_t>::max() / ELF64_SYM_SIZE) {
		return false;
	}

	const uint64_t table_size = symbol_count * ELF64_SYM_SIZE;
	if (declared_size_known && declared_size != table_size) {
		return false;
	}

	*out = table_size;
	return true;
}

[[nodiscard]] inline bool IsNullDynamicSymbolEntryValid(const void* symbol_table,
	                                                     uint64_t table_size) {
	if (symbol_table == nullptr || table_size < ELF64_SYM_SIZE) {
		return false;
	}

	const uint8_t null_entry[ELF64_SYM_SIZE] {};
	return std::memcmp(symbol_table, null_entry, sizeof(null_entry)) == 0;
}

[[nodiscard]] constexpr bool IsDynamicSymbolTableAddressAligned(uint64_t table_vaddr) {
	return table_vaddr % ELF64_SYM_ALIGNMENT == 0;
}

[[nodiscard]] constexpr bool DecodeTlsAllocationLayout(uint64_t image_size,
                                                       TlsAllocationLayout* out) {
	if (out == nullptr ||
	    image_size > std::numeric_limits<uint64_t>::max() - (TLS_TCB_ALIGNMENT - 1) -
	                     TLS_TCB_SIZE) {
		return false;
	}

	const uint64_t tcb_offset =
	    (image_size + TLS_TCB_ALIGNMENT - 1) & ~(TLS_TCB_ALIGNMENT - 1);
	out->tcb_offset      = tcb_offset;
	out->allocation_size = tcb_offset + TLS_TCB_SIZE;
	return true;
}

[[nodiscard]] constexpr bool DecodeTlsLayout(uint64_t file_offset, uint64_t virtual_address,
                                             uint64_t file_size, uint64_t memory_size,
                                             uint64_t alignment, TlsLayout* out) {
	if (out == nullptr || file_size > memory_size ||
	    (alignment > 1 && (alignment & (alignment - 1)) != 0)) {
		return false;
	}

	if (alignment > 1 &&
	    (file_offset % alignment != virtual_address % alignment ||
	     memory_size > std::numeric_limits<uint64_t>::max() - (alignment - 1))) {
		return false;
	}

	const uint64_t image_size =
	    alignment > 1 ? (memory_size + alignment - 1) & ~(alignment - 1) : memory_size;
	TlsAllocationLayout allocation {};
	if (!DecodeTlsAllocationLayout(image_size, &allocation)) {
		return false;
	}

	out->init_size       = file_size;
	out->image_size      = image_size;
	out->tcb_offset      = allocation.tcb_offset;
	out->allocation_size = allocation.allocation_size;
	return true;
}

[[nodiscard]] constexpr bool IsTableRangeValid(uint64_t table_vaddr, uint64_t table_size,
                                               uint64_t storage_vaddr,
                                               uint64_t storage_size) {
	if (storage_vaddr > std::numeric_limits<uint64_t>::max() - storage_size ||
	    table_vaddr < storage_vaddr) {
		return false;
	}

	const uint64_t table_offset = table_vaddr - storage_vaddr;
	return table_offset <= storage_size && table_size <= storage_size - table_offset;
}

[[nodiscard]] constexpr bool IsRelroRangeInMappedLoadSegment(
    uint64_t relro_vaddr, uint64_t relro_size, uint64_t load_vaddr,
    uint64_t mapped_load_size) {
	return relro_size == 0 ||
	       (relro_vaddr % RELRO_PAGE_SIZE == 0 && relro_size % RELRO_PAGE_SIZE == 0 &&
	        IsTableRangeValid(relro_vaddr, relro_size, load_vaddr, mapped_load_size));
}

[[nodiscard]] constexpr bool IsDynamicObjectRangeInSegment(uint64_t object_vaddr,
                                                           uint64_t object_size,
                                                           uint64_t segment_vaddr,
                                                           uint64_t segment_mem_size) {
	return object_size != 0 &&
	       IsTableRangeValid(object_vaddr, object_size, segment_vaddr, segment_mem_size);
}

[[nodiscard]] constexpr bool IsExecutableEntryInSegment(uint64_t entry_vaddr,
                                                        uint64_t segment_vaddr,
                                                        uint64_t segment_file_size,
                                                        bool segment_is_executable) {
	return segment_is_executable &&
	       IsTableRangeValid(entry_vaddr, 1, segment_vaddr, segment_file_size);
}

[[nodiscard]] constexpr bool IsLifecycleArrayRangeValid(uint64_t array_vaddr,
                                                        uint64_t array_size,
                                                        uint64_t storage_vaddr,
                                                        uint64_t storage_size) {
	return array_size == 0 ||
	       (array_size % ELF64_ADDR_SIZE == 0 &&
	        IsTableRangeValid(array_vaddr, array_size, storage_vaddr, storage_size));
}

[[nodiscard]] constexpr bool IsRelaTableRangeValid(uint64_t table_vaddr, uint64_t table_size,
                                                   uint64_t entry_size,
                                                   uint64_t storage_vaddr,
                                                   uint64_t storage_size) {
	if (entry_size != ELF64_RELA_SIZE || table_vaddr % ELF64_RELA_ALIGNMENT != 0 ||
	    table_size % ELF64_RELA_SIZE != 0) {
		return false;
	}

	return IsTableRangeValid(table_vaddr, table_size, storage_vaddr, storage_size);
}

[[nodiscard]] constexpr bool IsRelaIndexValid(uint64_t record_index, uint64_t table_size) {
	return record_index < table_size / ELF64_RELA_SIZE;
}

[[nodiscard]] constexpr bool IsJmprelExtentMetadataComplete(bool table_address_present,
                                                            bool table_size_present) {
	return !table_address_present || table_size_present;
}

[[nodiscard]] constexpr bool IsStandardStringExtentMetadataComplete(
    bool table_address_present, bool table_size_present) {
	return !table_address_present || table_size_present;
}

[[nodiscard]] constexpr bool IsStandardSymbolTableBoundAvailable(
    bool symbol_table_present, bool sysv_hash_present) {
	return !symbol_table_present || sysv_hash_present;
}

[[nodiscard]] constexpr bool IsRelaExtentMetadataComplete(bool table_address_present,
                                                          bool table_size_present) {
	return !table_address_present || table_size_present;
}

[[nodiscard]] constexpr bool IsRelativeRelocationSymbolValid(uint32_t relocation_type,
                                                             uint32_t symbol_index) {
	return relocation_type != ELF64_R_X86_64_RELATIVE || symbol_index == 0;
}

[[nodiscard]] constexpr bool IsSupportedRelocationType(uint32_t relocation_type) {
	return relocation_type == ELF64_R_X86_64_64 ||
	       relocation_type == ELF64_R_X86_64_GLOB_DAT ||
	       relocation_type == ELF64_R_X86_64_JUMP_SLOT ||
	       relocation_type == ELF64_R_X86_64_RELATIVE ||
	       relocation_type == ELF64_R_X86_64_DTPMOD64;
}

[[nodiscard]] constexpr bool IsSupportedRelocationRecord(uint32_t relocation_type,
	                                                      uint32_t symbol_index) {
	return IsSupportedRelocationType(relocation_type) &&
	       IsRelativeRelocationSymbolValid(relocation_type, symbol_index);
}

[[nodiscard]] constexpr bool IsSupportedRelocationSymbolEntry(uint32_t relocation_type,
                                                              uint8_t binding,
                                                              uint8_t symbol_type) {
	const bool consumes_symbol = relocation_type == ELF64_R_X86_64_64 ||
	                             relocation_type == ELF64_R_X86_64_GLOB_DAT ||
	                             relocation_type == ELF64_R_X86_64_JUMP_SLOT;
	if (!consumes_symbol) {
		return true;
	}

	const bool supported_binding = binding == ELF64_STB_LOCAL ||
	                               binding == ELF64_STB_GLOBAL ||
	                               binding == ELF64_STB_WEAK;
	const bool supported_type = symbol_type == ELF64_STT_NOTYPE ||
	                            symbol_type == ELF64_STT_OBJECT ||
	                            symbol_type == ELF64_STT_FUNC;
	return supported_binding && supported_type;
}

[[nodiscard]] constexpr bool IsRelocationTargetRangeValid(uint64_t target_vaddr,
                                                          uint64_t segment_vaddr,
                                                          uint64_t segment_memory_size) {
	return IsTableRangeValid(target_vaddr, ELF64_RELOCATION_TARGET_SIZE, segment_vaddr,
	                         segment_memory_size);
}

[[nodiscard]] constexpr bool IsPltGotResolverRangeValid(uint64_t pltgot_vaddr,
                                                        uint64_t segment_vaddr,
                                                        uint64_t segment_memory_size) {
	return pltgot_vaddr % ELF64_PLTGOT_ALIGNMENT == 0 &&
	       IsTableRangeValid(pltgot_vaddr, ELF64_PLTGOT_RESOLVER_SIZE, segment_vaddr,
	                         segment_memory_size);
}

[[nodiscard]] constexpr bool IsSymbolIndexValid(uint64_t symbol_index,
                                                uint64_t symbol_table_size,
                                                uint64_t symbol_entry_size) {
	return symbol_entry_size == ELF64_SYM_SIZE &&
	       symbol_index < symbol_table_size / ELF64_SYM_SIZE;
}

[[nodiscard]] inline const char* GetBoundedString(const char* table, uint64_t table_size,
                                                  uint64_t string_offset) {
	if (table == nullptr || string_offset >= table_size) {
		return nullptr;
	}

	const uint64_t remaining = table_size - string_offset;
	if (remaining > std::numeric_limits<size_t>::max()) {
		return nullptr;
	}

	const char* string = table + string_offset;
	return std::memchr(string, '\0', static_cast<size_t>(remaining)) != nullptr ? string : nullptr;
}

[[nodiscard]] inline bool IsDynamicStringTableShapeValid(const char* table,
	                                                      uint64_t table_size) {
	return table != nullptr && table_size != 0 &&
	       table_size <= std::numeric_limits<size_t>::max() && table[0] == '\0' &&
	       table[static_cast<size_t>(table_size - 1)] == '\0';
}

[[nodiscard]] inline const char* GetDynamicString(const char* table, uint64_t table_size,
                                                  bool table_size_known,
                                                  uint64_t string_offset) {
	if (table == nullptr) {
		return nullptr;
	}
	if (!table_size_known) {
		return table + string_offset;
	}
	return GetBoundedString(table, table_size, string_offset);
}

[[nodiscard]] inline bool IsRelocationSymbolNameValid(uint8_t binding, const char* string_table,
                                                      uint64_t string_table_size,
                                                      bool string_table_size_known,
                                                      uint64_t string_offset) {
	if (binding == ELF64_STB_LOCAL) {
		return true;
	}
	if (binding != ELF64_STB_GLOBAL && binding != ELF64_STB_WEAK) {
		return false;
	}
	return GetDynamicString(string_table, string_table_size, string_table_size_known,
	                        string_offset) != nullptr;
}

[[nodiscard]] constexpr uint64_t GetPlatformStringOffset(uint64_t dynamic_value) {
	return dynamic_value & std::numeric_limits<uint32_t>::max();
}

} // namespace Loader::RuntimeLinkerBounds

#endif /* EMULATOR_INCLUDE_EMULATOR_LOADER_RUNTIMELINKERBOUNDS_H_ */
