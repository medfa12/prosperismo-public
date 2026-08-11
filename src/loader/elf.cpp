#include "loader/elf.h"

#include "common/assert.h"
#include "common/file.h"
#include "common/logging/log.h"

#include <cstring>
#include <limits>
#include <memory>

namespace Loader {

static std::unique_ptr<SelfHeader> LoadSelf(Common::File& f) {
	if (f.Remaining() < sizeof(SelfHeader)) {
		return nullptr;
	}

	auto self = std::make_unique<SelfHeader>();

	f.Read(self.get(), sizeof(SelfHeader));

	return self;
}

static std::unique_ptr<SelfSegment[]> LoadSelfSegments(Common::File& f, uint16_t num) {
	auto segs = std::make_unique<SelfSegment[]>(num);

	f.Read(segs.get(), sizeof(SelfSegment) * num);

	return segs;
}

static std::unique_ptr<Elf64_Ehdr> LoadEhdr64(Common::File& f) {
	if (f.Remaining() < sizeof(Elf64_Ehdr)) {
		return nullptr;
	}

	auto ehdr = std::make_unique<Elf64_Ehdr>();

	f.Read(ehdr.get(), sizeof(Elf64_Ehdr));

	return ehdr;
}

static bool IsFileRangeValid(uint64_t offset, uint64_t size, uint64_t file_size) {
	return offset <= file_size && size <= file_size - offset;
}

static bool IsVirtualRangeValid(uint64_t address, uint64_t size) {
	return size <= std::numeric_limits<uint64_t>::max() - address;
}

static bool IsSegmentAlignmentValid(const Elf64_Phdr& phdr) {
	if (phdr.p_align <= 1) {
		return true;
	}

	const uint64_t mask = phdr.p_align - 1;
	return (phdr.p_align & mask) == 0 && (phdr.p_vaddr & mask) == (phdr.p_offset & mask);
}

static bool IsLoadSegmentSizeRoundingValid(const Elf64_Phdr& phdr) {
	return phdr.p_align <= 1 ||
	       phdr.p_memsz <= std::numeric_limits<uint64_t>::max() - (phdr.p_align - 1);
}

static bool IsLoadSegmentAlignedExtentValid(const Elf64_Phdr& phdr) {
	uint64_t aligned_size = phdr.p_memsz;
	if (phdr.p_align > 1) {
		const uint64_t mask = phdr.p_align - 1;
		aligned_size        = (phdr.p_memsz + mask) & ~mask;
	}

	return IsVirtualRangeValid(phdr.p_vaddr, aligned_size);
}

static bool AreLoadSegmentsOrdered(const Elf64_Phdr* phdr, Elf64_Half phnum) {
	Elf64_Addr previous_address = 0;
	bool       have_previous    = false;

	for (Elf64_Half i = 0; i < phnum; i++) {
		if (phdr[i].p_type != PT_LOAD) {
			continue;
		}
		if (have_previous && phdr[i].p_vaddr < previous_address) {
			return false;
		}
		previous_address = phdr[i].p_vaddr;
		have_previous    = true;
	}

	return true;
}

static bool IsTlsInitializationImageInLoadSegment(const Elf64_Phdr& tls,
                                                  const Elf64_Phdr* phdr,
                                                  Elf64_Half phnum) {
	if (tls.p_filesz == 0) {
		return true;
	}

	for (Elf64_Half i = 0; i < phnum; i++) {
		if (phdr[i].p_type != PT_LOAD || tls.p_offset < phdr[i].p_offset ||
		    tls.p_vaddr < phdr[i].p_vaddr) {
			continue;
		}

		const uint64_t file_offset    = tls.p_offset - phdr[i].p_offset;
		const uint64_t virtual_offset = tls.p_vaddr - phdr[i].p_vaddr;
		if (file_offset == virtual_offset && file_offset <= phdr[i].p_filesz &&
		    tls.p_filesz <= phdr[i].p_filesz - file_offset) {
			return true;
		}
	}

	return false;
}

static bool IsEntryPointValid(Elf64_Addr entry, const Elf64_Phdr* phdr, Elf64_Half phnum) {
	if (entry == 0) {
		return true;
	}

	for (Elf64_Half i = 0; i < phnum; i++) {
		if (phdr[i].p_type == PT_LOAD && (phdr[i].p_flags & PF_X) != 0 &&
		    entry >= phdr[i].p_vaddr && entry - phdr[i].p_vaddr < phdr[i].p_filesz) {
			return true;
		}
	}

	return false;
}

static uint64_t GetDynamicEntryCount(const Elf64_Ehdr* ehdr, const Elf64_Phdr* phdr) {
	if (ehdr == nullptr || phdr == nullptr) {
		return 0;
	}

	for (Elf64_Half i = 0; i < ehdr->e_phnum; i++) {
		if (phdr[i].p_type == PT_DYNAMIC) {
			return phdr[i].p_filesz / sizeof(Elf64_Dyn);
		}
	}

	return 0;
}

static void SaveEhdr64(Common::File& f, const Elf64_Ehdr* ehdr) {
	EXIT_IF(ehdr == nullptr);

	uint32_t bytes_written = 0;

	f.Write(ehdr, sizeof(Elf64_Ehdr), &bytes_written);

	EXIT_IF(bytes_written == 0);
}

static std::unique_ptr<Elf64_Phdr[]> LoadPhdr64(Common::File& f, uint64_t offset, Elf64_Half num) {
	auto phdr = std::make_unique<Elf64_Phdr[]>(num);

	f.Seek(offset);
	f.Read(phdr.get(), sizeof(Elf64_Phdr) * num);

	return phdr;
}

static void SavePhdr64(Common::File& f, uint64_t offset, Elf64_Half num, const Elf64_Phdr* phdr) {
	EXIT_IF(phdr == nullptr);

	uint32_t bytes_written = 0;

	f.Seek(offset);
	f.Write(phdr, sizeof(Elf64_Phdr) * num, &bytes_written);

	EXIT_IF(bytes_written == 0);
}

static std::unique_ptr<Elf64_Shdr[]> LoadShdr64(Common::File& f, uint64_t offset, Elf64_Half num) {
	if (num == 0) {
		return nullptr;
	}

	auto shdr = std::make_unique<Elf64_Shdr[]>(num);

	f.Seek(offset);
	f.Read(shdr.get(), sizeof(Elf64_Shdr) * num);

	return shdr;
}

static void SaveShdr64(Common::File& f, uint64_t offset, Elf64_Half num, const Elf64_Shdr* shdr) {
	if (num == 0) {
		return;
	}

	EXIT_IF(shdr == nullptr);

	uint32_t bytes_written = 0;

	f.Seek(offset);
	f.Write(shdr, sizeof(Elf64_Shdr) * num, &bytes_written);

	EXIT_IF(bytes_written == 0);
}

static std::unique_ptr<uint8_t[]> LoadDynamic64(Elf64* f, uint64_t offset, uint64_t size) {
	auto dynamic_data = std::make_unique<uint8_t[]>(size);

	// f.Seek(offset);
	// f.Read(dynamic_data, size);

	f->LoadSegment(reinterpret_cast<uint64_t>(dynamic_data.get()), offset, size);

	return dynamic_data;
}

static std::unique_ptr<char[]> LoadStrTable(Common::File& f, uint64_t offset, uint32_t size) {
	if (size == 0) {
		return nullptr;
	}

	auto str_table = std::make_unique<char[]>(size);
	f.Seek(offset);
	f.Read(str_table.get(), size);
	return str_table;
}

static void DbgPrintEhdr64(Elf64_Ehdr* ehdr, Common::File& f) {
	f.Printf("ehdr->e_ident = ");
	for (auto i: ehdr->e_ident) {
		f.Printf("%02x", i);
	}
	f.Printf("\n");

	f.Printf("ehdr->e_type = 0x%04" PRIx16 "\n", ehdr->e_type);
	f.Printf("ehdr->e_machine = 0x%04" PRIx16 "\n", ehdr->e_machine);
	f.Printf("ehdr->e_version = 0x%08" PRIx32 "\n", ehdr->e_version);

	f.Printf("ehdr->e_entry = 0x%016" PRIx64 "\n", ehdr->e_entry);
	f.Printf("ehdr->e_phoff = 0x%016" PRIx64 "\n", ehdr->e_phoff);
	f.Printf("ehdr->e_shoff = 0x%016" PRIx64 "\n", ehdr->e_shoff);
	f.Printf("ehdr->e_flags = 0x%08" PRIx32 "\n", ehdr->e_flags);
	f.Printf("ehdr->e_ehsize = 0x%04" PRIx16 "\n", ehdr->e_ehsize);
	f.Printf("ehdr->e_phentsize = 0x%04" PRIx16 "\n", ehdr->e_phentsize);
	f.Printf("ehdr->e_phnum = %" PRIu16 "\n", ehdr->e_phnum);
	f.Printf("ehdr->e_shentsize = 0x%04" PRIx16 "\n", ehdr->e_shentsize);
	f.Printf("ehdr->e_shnum = %" PRIu16 "\n", ehdr->e_shnum);
	f.Printf("ehdr->e_shstrndx = %" PRIu16 "\n", ehdr->e_shstrndx);
}

static void DbgPrintPhdr64(Elf64_Phdr* phdr, Common::File& f) {
	f.Printf("phdr->p_type = 0x%08" PRIx32 "\n", phdr->p_type);
	f.Printf("phdr->p_flags = 0x%08" PRIx32 "\n", phdr->p_flags);
	f.Printf("phdr->p_offset = 0x%016" PRIx64 "\n", phdr->p_offset);
	f.Printf("phdr->p_vaddr = 0x%016" PRIx64 "\n", phdr->p_vaddr);
	f.Printf("phdr->p_paddr = 0x%016" PRIx64 "\n", phdr->p_paddr);
	f.Printf("phdr->p_filesz = 0x%016" PRIx64 "\n", phdr->p_filesz);
	f.Printf("phdr->p_memsz = 0x%016" PRIx64 "\n", phdr->p_memsz);
	f.Printf("phdr->p_align = 0x%016" PRIx64 "\n", phdr->p_align);
}

static void DbgPrintShdr64(Elf64_Shdr* shdr, Common::File& f) {
	f.Printf("shdr->sh_name = %d\n", shdr->sh_name);
	f.Printf("shdr->sh_type = 0x%08" PRIx32 "\n", shdr->sh_type);
	f.Printf("shdr->sh_flags = 0x%016" PRIx64 "\n", shdr->sh_flags);
	f.Printf("shdr->sh_addr = 0x%016" PRIx64 "\n", shdr->sh_addr);
	f.Printf("shdr->sh_offset = 0x%016" PRIx64 "\n", shdr->sh_offset);
	f.Printf("shdr->sh_size = 0x%016" PRIx64 "\n", shdr->sh_size);
	f.Printf("shdr->sh_link = %" PRId32 "\n", shdr->sh_link);
	f.Printf("shdr->sh_info = 0x%08" PRIx32 "\n", shdr->sh_info);
	f.Printf("shdr->sh_addralign = 0x%016" PRIx64 "\n", shdr->sh_addralign);
	f.Printf("shdr->sh_entsize = 0x%016" PRIx64 "\n", shdr->sh_entsize);
}

// NOLINTNEXTLINE(cppcoreguidelines-macro-usage)
#define DBG_NAME(tag)                                                                              \
	case tag: name = #tag; break;

static void DbgPrintDynamic64(const Elf64_Dyn* dyn, Common::File& f) {
	const char* name = "Unknown";
	switch (dyn->d_tag) {
		DBG_NAME(DT_OS_HASH)
		DBG_NAME(DT_HASH)
		DBG_NAME(DT_OS_STRTAB)
		DBG_NAME(DT_OS_STRSZ)
		DBG_NAME(DT_STRTAB)
		DBG_NAME(DT_STRSZ)
		DBG_NAME(DT_OS_SYMTAB)
		DBG_NAME(DT_SYMTAB)
		DBG_NAME(DT_OS_HASHSZ)
		DBG_NAME(DT_OS_SYMTABSZ)
		DBG_NAME(DT_INIT)
		DBG_NAME(DT_FINI)
		DBG_NAME(DT_OS_PLTGOT)
		DBG_NAME(DT_PLTGOT)
		DBG_NAME(DT_OS_JMPREL)
		DBG_NAME(DT_JMPREL)
		DBG_NAME(DT_OS_PLTRELSZ)
		DBG_NAME(DT_PLTRELSZ)
		DBG_NAME(DT_OS_PLTREL)
		DBG_NAME(DT_PLTREL)
		DBG_NAME(DT_OS_RELA)
		DBG_NAME(DT_RELA)
		DBG_NAME(DT_OS_RELASZ)
		DBG_NAME(DT_RELASZ)
		DBG_NAME(DT_OS_RELAENT)
		DBG_NAME(DT_RELAENT)
		DBG_NAME(DT_INIT_ARRAY)
		DBG_NAME(DT_INIT_ARRAYSZ)
		DBG_NAME(DT_FINI_ARRAY)
		DBG_NAME(DT_FINI_ARRAYSZ)
		DBG_NAME(DT_PREINIT_ARRAY)
		DBG_NAME(DT_PREINIT_ARRAYSZ)
		DBG_NAME(DT_OS_SYMENT)
		DBG_NAME(DT_SYMENT)
		DBG_NAME(DT_DEBUG)
		DBG_NAME(DT_TEXTREL)
		DBG_NAME(DT_FLAGS)
		DBG_NAME(DT_NEEDED)
		DBG_NAME(DT_OS_NEEDED_MODULE)
		DBG_NAME(DT_OS_NEEDED_MODULE_1)
		DBG_NAME(DT_OS_IMPORT_LIB)
		DBG_NAME(DT_OS_IMPORT_LIB_1)
		DBG_NAME(DT_OS_IMPORT_LIB_ATTR)
		DBG_NAME(DT_OS_FINGERPRINT)
		DBG_NAME(DT_OS_ORIGINAL_FILENAME)
		DBG_NAME(DT_OS_ORIGINAL_FILENAME_1)
		DBG_NAME(DT_OS_MODULE_INFO)
		DBG_NAME(DT_OS_MODULE_INFO_1)
		DBG_NAME(DT_OS_MODULE_ATTR)
		DBG_NAME(DT_SONAME)
		DBG_NAME(DT_OS_EXPORT_LIB)
		DBG_NAME(DT_OS_EXPORT_LIB_1)
		DBG_NAME(DT_OS_EXPORT_LIB_ATTR)
		DBG_NAME(DT_RELACOUNT)
		DBG_NAME(DT_NULL)
	}
	f.Printf("d_tag = 0x%016" PRIx64 ", d_val = 0x%016" PRIx64 ", name = %s\n", dyn->d_tag,
	         dyn->d_un.d_val, name);
}

Elf64::~Elf64() {
	Clear();
}

void Elf64::LoadSegment(uint64_t vaddr, uint64_t file_offset, uint64_t size) {
	EXIT_IF(m_f == nullptr);

	if (m_self != nullptr) {
		EXIT_IF(m_self_segments == nullptr);
		EXIT_IF(m_phdr == nullptr);

		for (uint16_t i = 0; i < m_self->segments_num; i++) {
			const auto& seg = m_self_segments[i];
			if ((seg.type & 0x800u) != 0) {
				auto phdr_id = ((seg.type >> 20u) & 0xFFFu);

				const auto& phdr = m_phdr[phdr_id];

				if (file_offset >= phdr.p_offset && file_offset < phdr.p_offset + phdr.p_filesz) {
					EXIT_NOT_IMPLEMENTED(seg.decompressed_size != phdr.p_filesz);
					EXIT_NOT_IMPLEMENTED(seg.compressed_size != seg.decompressed_size);

					auto offset = file_offset - phdr.p_offset;

					EXIT_NOT_IMPLEMENTED(offset + size > seg.decompressed_size);

					m_f->Seek(offset + seg.offset);
					m_f->Read(reinterpret_cast<void*>(static_cast<uintptr_t>(vaddr)), size);

					return;
				}
			}
		}

		if (m_f->Size() - m_self->file_size == size) {
			m_f->Seek(m_self->file_size);
			m_f->Read(reinterpret_cast<void*>(static_cast<uintptr_t>(vaddr)), size);

			return;
		}

		EXIT("missing self segment\n");
	} else {
		m_f->Seek(file_offset);
		m_f->Read(reinterpret_cast<void*>(static_cast<uintptr_t>(vaddr)), size);
	}
}

const Elf64_Dyn* Elf64::GetDynValue(Elf64_Sxword tag) const {
	const auto*    dynamic     = GetDynamic();
	const uint64_t entry_count = GetDynamicEntryCount(m_ehdr.get(), m_phdr.get());
	for (uint64_t i = 0; i < entry_count && dynamic[i].d_tag != DT_NULL; i++) {
		const auto* dyn = dynamic + i;
		if (dyn->d_tag == tag) {
			return dyn;
		}
	}
	return nullptr;
}

std::vector<const Elf64_Dyn*> Elf64::GetDynList(Elf64_Sxword tag) const {
	std::vector<const Elf64_Dyn*> ret;
	const auto*    dynamic     = GetDynamic();
	const uint64_t entry_count = GetDynamicEntryCount(m_ehdr.get(), m_phdr.get());
	for (uint64_t i = 0; i < entry_count && dynamic[i].d_tag != DT_NULL; i++) {
		const auto* dyn = dynamic + i;
		if (dyn->d_tag == tag) {
			ret.push_back(dyn);
		}
	}
	return ret;
}

bool Elf64::IsShared() const {
	return (m_ehdr->e_type == ET_DYNAMIC);
}

bool Elf64::IsNextGen() const {
	return (m_ehdr->e_ident[EI_ABIVERSION] == ELF_ABI_VERSION_NEXT_GEN);
}

const char* Elf64::GetSectionName(int index) const {
	if (m_ehdr == nullptr || m_shdr == nullptr || m_str_table == nullptr || index < 0 ||
	    index >= m_ehdr->e_shnum) {
		return nullptr;
	}

	const auto name_offset = m_shdr[index].sh_name;
	if (name_offset >= m_str_table_size) {
		return nullptr;
	}

	const auto remaining = m_str_table_size - name_offset;
	if (std::memchr(m_str_table.get() + name_offset, '\0', remaining) == nullptr) {
		return nullptr;
	}

	return m_str_table.get() + name_offset;
}

void Elf64::Clear() {
	if (m_f != nullptr) {
		m_f->Close();
	}

	m_f.reset();
	m_self.reset();
	m_ehdr.reset();
	m_self_segments.reset();
	m_phdr.reset();
	m_shdr.reset();
	m_str_table.reset();
	m_str_table_size = 0;
	m_dynamic.reset();
	m_dynamic_data.reset();
}

void Elf64::DbgDump(const std::string& folder) {
	auto folder_str = Common::FixDirectorySlash(folder);

	Common::File::CreateDirectories(folder_str);

	for (uint16_t i = 0; i < m_ehdr->e_phnum; i++) {
		if (m_phdr[i].p_filesz == 0u) {
			continue;
		}

		char str[512];
		int  s = snprintf(str, 512, "phdr_%03d", i);
		EXIT_NOT_IMPLEMENTED(s >= 512);

		Common::File fout;
		fout.Create(folder_str + str);

		auto buf = std::make_unique<char[]>(static_cast<uint32_t>(m_phdr[i].p_filesz));

		// m_f->Seek(m_phdr[i].p_offset);
		// m_f->Read(buf, static_cast<uint32_t>(m_phdr[i].p_filesz));

		LoadSegment(reinterpret_cast<uint64_t>(buf.get()), m_phdr[i].p_offset, m_phdr[i].p_filesz);

		fout.Write(buf.get(), static_cast<uint32_t>(m_phdr[i].p_filesz));

		fout.Close();
	}

	for (uint16_t i = 0; i < m_ehdr->e_shnum; i++) {
		if (m_shdr[i].sh_size == 0u) {
			continue;
		}

		char str[512];
		int  s = snprintf(str, 512, "shdr_%03d", i);
		EXIT_NOT_IMPLEMENTED(s >= 512);

		Common::File fout;
		fout.Create(folder_str + str);

		auto buf = std::make_unique<char[]>(static_cast<uint32_t>(m_shdr[i].sh_size));

		m_f->Seek(m_shdr[i].sh_offset);
		m_f->Read(buf.get(), static_cast<uint32_t>(m_shdr[i].sh_size));
		fout.Write(buf.get(), static_cast<uint32_t>(m_shdr[i].sh_size));

		fout.Close();
	}

	Common::File fout;

	fout.Create(folder_str + "ehdr.txt");
	DbgPrintEhdr64(m_ehdr.get(), fout);
	fout.Close();

	fout.Create(folder_str + "phdr.txt");
	for (uint16_t i = 0; i < m_ehdr->e_phnum; i++) {
		fout.Printf("--- phdr [%d] ---\n", i);
		DbgPrintPhdr64(m_phdr.get() + i, fout);
	}
	fout.Close();

	fout.Create(folder_str + "shdr.txt");
	for (uint16_t i = 0; i < m_ehdr->e_shnum; i++) {
		const char* section_name = GetSectionName(i);
		fout.Printf("--- shdr [%d] %s ---\n", i, section_name != nullptr ? section_name : "");
		DbgPrintShdr64(m_shdr.get() + i, fout);
	}
	fout.Close();

	fout.Create(folder_str + "dynamic.txt");
	const auto*    dynamic     = GetDynamic();
	const uint64_t entry_count = GetDynamicEntryCount(m_ehdr.get(), m_phdr.get());
	for (uint64_t i = 0; i < entry_count && dynamic[i].d_tag != DT_NULL; i++) {
		DbgPrintDynamic64(dynamic + i, fout);
	}
	fout.Close();
}

uint64_t Elf64::GetEntry() {
	return m_ehdr->e_entry;
}

bool Elf64::IsSelf() const {
	if (m_f == nullptr || m_f->IsInvalid()) {
		return false;
	}

	if (m_self == nullptr) {
		return false;
	}

	const bool known_magic = (m_self->ident[0] == 0x4f && m_self->ident[1] == 0x15 &&
	                          m_self->ident[2] == 0x3d && m_self->ident[3] == 0x1d) ||
	                         (m_self->ident[0] == 0x54 && m_self->ident[1] == 0x14 &&
	                          m_self->ident[2] == 0xf5 && m_self->ident[3] == 0xee);
	if (!known_magic) {
		return false;
	}

	const bool known_ident_tail =
	    (m_self->ident[4] == 0x00 && m_self->ident[5] == 0x01 && m_self->ident[6] == 0x01 &&
	     m_self->ident[7] == 0x12 && m_self->ident[8] == 0x01 && m_self->ident[9] == 0x01 &&
	     m_self->ident[10] == 0x00 && m_self->ident[11] == 0x00 && m_self->unknown == 0x22) ||
	    (m_self->ident[4] == 0x10 && m_self->ident[5] == 0x01 && m_self->ident[6] == 0x01 &&
	     m_self->ident[7] == 0x12 && m_self->ident[8] == 0x01 && m_self->ident[9] == 0x01 &&
	     m_self->ident[10] == 0x00 && m_self->ident[11] == 0x10 && m_self->unknown == 0x32);

	if (!known_ident_tail) {
		LOGF("Unknown SELF file\n");
		return false;
	}

	return true;
}

bool Elf64::IsValid() const {
	if (m_f == nullptr || m_f->IsInvalid()) {
		return false;
	}

	if (m_ehdr == nullptr) {
		return false;
	}

	if (m_ehdr->e_ident[EI_MAG0] != '\x7f' || m_ehdr->e_ident[EI_MAG1] != 'E' ||
	    m_ehdr->e_ident[EI_MAG2] != 'L' || m_ehdr->e_ident[EI_MAG3] != 'F') {
		LOGF("Not an ELF file\n");
		return false;
	}

	if (m_ehdr->e_ident[EI_CLASS] != ELFCLASS64) {
		LOGF("ehdr->e_ident[EI_CLASS] (0x%x) != ELFCLASS64\n", m_ehdr->e_ident[EI_CLASS]);
		return false;
	}

	if (m_ehdr->e_ident[EI_DATA] != ELFDATA2LSB) {
		LOGF("ehdr->e_ident[EI_DATA] (0x%x) != ELFDATA2LSB\n", m_ehdr->e_ident[EI_DATA]);
		return false;
	}

	if (m_ehdr->e_ident[EI_VERSION] != EV_CURRENT) {
		LOGF("ehdr->e_ident[EI_VERSION] != EV_CURRENT\n");
		return false;
	}

	if (m_ehdr->e_ident[EI_OSABI] != ELFOSABI_FREEBSD) {
		LOGF("ehdr->e_ident[EI_OSABI] (0x%x) != ELFOSABI_FREEBSD\n", m_ehdr->e_ident[EI_OSABI]);
		return false;
	}

	if (m_ehdr->e_ident[EI_ABIVERSION] > ELF_ABI_VERSION_MAX_SUPPORTED) {
		LOGF("ehdr->e_ident[EI_ABIVERSION] (0x%x) > %u\n",
		     static_cast<unsigned>(m_ehdr->e_ident[EI_ABIVERSION]),
		     static_cast<unsigned>(ELF_ABI_VERSION_MAX_SUPPORTED));
		return false;
	}

	if (m_ehdr->e_type != ET_DYNEXEC && m_ehdr->e_type != ET_DYNAMIC) {
		LOGF("ehdr->e_type (%04x) != ET_DYNEXEC && m_ehdr->e_type != ET_DYNAMIC\n", m_ehdr->e_type);
		return false;
	}

	if (m_ehdr->e_machine != EM_X86_64) {
		LOGF("ehdr->e_machine (%04x) != EM_X86_64\n", m_ehdr->e_machine);
		return false;
	}

	if (m_ehdr->e_version != EV_CURRENT) {
		LOGF("ehdr->e_version != EV_CURRENT\n");
		return false;
	}

	if (m_ehdr->e_ehsize != sizeof(Elf64_Ehdr)) {
		LOGF("ehdr->e_ehsize != sizeof(Elf64_Ehdr)\n");
		return false;
	}

	if (m_ehdr->e_phentsize != sizeof(Elf64_Phdr)) {
		LOGF("ehdr->e_phentsize != sizeof(Elf64_Phdr)\n");
		return false;
	}

	if (m_ehdr->e_shentsize > 0 && m_ehdr->e_shentsize != sizeof(Elf64_Shdr)) {
		LOGF("ehdr->e_shentsize (%d) != sizeof(Elf64_Shdr)\n", m_ehdr->e_shentsize);
		return false;
	}

	return true;
}

void Elf64::Open(const std::filesystem::path& file_name) {
	Clear();

	m_f = std::make_unique<Common::File>();
	m_f->Open(file_name, Common::File::Mode::Read);

	if (m_f->IsInvalid()) {
		EXIT("Can't open %s\n", Common::PathToString(file_name).c_str());
	}

	m_self = LoadSelf(*m_f);

	if (!IsSelf()) {
		m_self.reset();
		m_f->Seek(0);
	} else {
		m_self_segments = LoadSelfSegments(*m_f, m_self->segments_num);
	}

	auto ehdr_pos = m_f->Tell();

	m_ehdr = LoadEhdr64(*m_f);

	if (!IsValid()) {
		m_ehdr.reset();
	}

	if (m_ehdr != nullptr /*&& m_self == nullptr*/) {
		const uint64_t file_size = m_f->Size();
		const uint64_t phdr_size =
		    static_cast<uint64_t>(m_ehdr->e_phnum) * sizeof(Elf64_Phdr);
		const bool phdr_offset_is_valid =
		    ehdr_pos <= file_size && m_ehdr->e_phoff <= file_size - ehdr_pos;
		if (!phdr_offset_is_valid ||
		    !IsFileRangeValid(ehdr_pos + m_ehdr->e_phoff, phdr_size, file_size)) {
			LOGF("ELF program header table is outside the file\n");
			m_ehdr.reset();
			return;
		}

		m_phdr = LoadPhdr64(*m_f, ehdr_pos + m_ehdr->e_phoff, m_ehdr->e_phnum);
		if (!AreLoadSegmentsOrdered(m_phdr.get(), m_ehdr->e_phnum)) {
			LOGF("ELF load segments are not ordered by virtual address\n");
			m_phdr.reset();
			m_ehdr.reset();
			return;
		}
		bool has_dynamic_segment = false;
		bool has_tls_segment     = false;
		for (Elf64_Half i = 0; i < m_ehdr->e_phnum; i++) {
			if (m_phdr[i].p_type == PT_DYNAMIC) {
				if (has_dynamic_segment) {
					LOGF("ELF has multiple dynamic segments\n");
					m_phdr.reset();
					m_ehdr.reset();
					return;
				}
				has_dynamic_segment = true;
			}
			if (m_phdr[i].p_type == PT_TLS) {
				if (has_tls_segment) {
					LOGF("ELF has multiple TLS segments\n");
					m_phdr.reset();
					m_ehdr.reset();
					return;
				}
				has_tls_segment = true;
				if (m_phdr[i].p_flags != PF_R) {
					LOGF("ELF TLS segment flags are not read-only\n");
					m_phdr.reset();
					m_ehdr.reset();
					return;
				}
				const bool tls_image_is_valid = IsTlsInitializationImageInLoadSegment(
				    m_phdr[i], m_phdr.get(), m_ehdr->e_phnum);
				if (!tls_image_is_valid) {
					LOGF("ELF TLS initialization image is outside a load segment\n");
					m_phdr.reset();
					m_ehdr.reset();
					return;
				}
			}
			if (m_phdr[i].p_type == PT_LOAD && m_phdr[i].p_filesz > m_phdr[i].p_memsz) {
				LOGF("ELF load segment file size exceeds memory size\n");
				m_phdr.reset();
				m_ehdr.reset();
				return;
			}
			if (m_self == nullptr && m_phdr[i].p_type == PT_LOAD &&
			    !IsFileRangeValid(m_phdr[i].p_offset, m_phdr[i].p_filesz, file_size)) {
				LOGF("ELF load segment is outside the file\n");
				m_phdr.reset();
				m_ehdr.reset();
				return;
			}
			if (m_self == nullptr && m_phdr[i].p_type == PT_DYNAMIC &&
			    !IsFileRangeValid(m_phdr[i].p_offset, m_phdr[i].p_filesz, file_size)) {
				LOGF("ELF dynamic segment is outside the file\n");
				m_phdr.reset();
				m_ehdr.reset();
				return;
			}
			if (m_phdr[i].p_type == PT_DYNAMIC &&
			    m_phdr[i].p_filesz % sizeof(Elf64_Dyn) != 0) {
				LOGF("ELF dynamic segment has a partial entry\n");
				m_phdr.reset();
				m_ehdr.reset();
				return;
			}
			if (m_phdr[i].p_type == PT_LOAD &&
			    !IsVirtualRangeValid(m_phdr[i].p_vaddr, m_phdr[i].p_memsz)) {
				LOGF("ELF load segment virtual address range overflows\n");
				m_phdr.reset();
				m_ehdr.reset();
				return;
			}
			if (m_phdr[i].p_type == PT_LOAD && !IsSegmentAlignmentValid(m_phdr[i])) {
				LOGF("ELF load segment alignment is invalid\n");
				m_phdr.reset();
				m_ehdr.reset();
				return;
			}
			if (m_phdr[i].p_type == PT_DYNAMIC && !IsSegmentAlignmentValid(m_phdr[i])) {
				LOGF("ELF dynamic segment alignment is invalid\n");
				m_phdr.reset();
				m_ehdr.reset();
				return;
			}
			if (m_phdr[i].p_type == PT_LOAD && !IsLoadSegmentSizeRoundingValid(m_phdr[i])) {
				LOGF("ELF load segment size alignment overflows\n");
				m_phdr.reset();
				m_ehdr.reset();
				return;
			}
			if (m_phdr[i].p_type == PT_LOAD && !IsLoadSegmentAlignedExtentValid(m_phdr[i])) {
				LOGF("ELF load segment aligned virtual address range overflows\n");
				m_phdr.reset();
				m_ehdr.reset();
				return;
			}
		}
		if (!IsEntryPointValid(m_ehdr->e_entry, m_phdr.get(), m_ehdr->e_phnum)) {
			LOGF("ELF entry point is outside executable file-backed load data\n");
			m_phdr.reset();
			m_ehdr.reset();
			return;
		}
		if (m_self == nullptr) {
			const uint64_t shdr_size =
			    static_cast<uint64_t>(m_ehdr->e_shnum) * sizeof(Elf64_Shdr);
			if (m_ehdr->e_shnum != 0 &&
			    IsFileRangeValid(m_ehdr->e_shoff, shdr_size, file_size)) {
				m_shdr = LoadShdr64(*m_f, m_ehdr->e_shoff, m_ehdr->e_shnum);
			} else if (m_ehdr->e_shnum != 0) {
				LOGF("ELF section header table is outside the file; skipping\n");
			}

			if (m_shdr != nullptr) {
				if (m_ehdr->e_shstrndx < m_ehdr->e_shnum) {
					m_str_table_size = static_cast<uint32_t>(m_shdr[m_ehdr->e_shstrndx].sh_size);
					m_str_table =
					    LoadStrTable(*m_f, m_shdr[m_ehdr->e_shstrndx].sh_offset, m_str_table_size);
				}
			}
		} else if (m_ehdr->e_shnum != 0) {
			LOGF("SELF: skipping ELF section table: shoff=0x%016" PRIx64 ", shnum=%" PRIu16 "\n",
			     m_ehdr->e_shoff, m_ehdr->e_shnum);
		}

		for (Elf64_Half i = 0; i < m_ehdr->e_phnum; i++) {
			switch (m_phdr[i].p_type) {
				case PT_DYNAMIC:
					m_dynamic = LoadDynamic64(this, m_phdr[i].p_offset, m_phdr[i].p_filesz);
					break;
				case PT_OS_DYNLIBDATA:
					m_dynamic_data = LoadDynamic64(this, m_phdr[i].p_offset, m_phdr[i].p_filesz);
					break;
				default: break;
			}
		}
	}
}

void Elf64::Save(const std::filesystem::path& file_name) {
	EXIT_IF(!IsValid());

	if (IsValid()) {
		Common::File f;
		f.Create(file_name);

		if (f.IsInvalid()) {
			EXIT("Can't create %s\n", Common::PathToString(file_name).c_str());
		}

		SaveEhdr64(f, m_ehdr.get());

		SavePhdr64(f, m_ehdr->e_phoff, m_ehdr->e_phnum, m_phdr.get());
		SaveShdr64(f, m_ehdr->e_shoff, m_ehdr->e_shnum, m_shdr.get());

		for (uint16_t i = 0; i < m_ehdr->e_phnum; i++) {
			if (m_phdr[i].p_filesz == 0u) {
				continue;
			}

			auto buf = std::make_unique<char[]>(static_cast<uint32_t>(m_phdr[i].p_filesz));

			LoadSegment(reinterpret_cast<uint64_t>(buf.get()), m_phdr[i].p_offset,
			            m_phdr[i].p_filesz);

			uint32_t bytes_written = 0;

			f.Seek(m_phdr[i].p_offset);
			f.Write(buf.get(), static_cast<uint32_t>(m_phdr[i].p_filesz), &bytes_written);

			EXIT_IF(bytes_written == 0);
		}

		for (uint16_t i = 0; i < m_ehdr->e_shnum; i++) {
			if (m_shdr[i].sh_size == 0u) {
				continue;
			}

			auto buf = std::make_unique<char[]>(static_cast<uint32_t>(m_shdr[i].sh_size));

			m_f->Seek(m_shdr[i].sh_offset);
			m_f->Read(buf.get(), static_cast<uint32_t>(m_shdr[i].sh_size));

			uint32_t bytes_written = 0;

			f.Seek(m_shdr[i].sh_offset);
			f.Write(buf.get(), static_cast<uint32_t>(m_shdr[i].sh_size), &bytes_written);

			EXIT_IF(bytes_written == 0);
		}

		f.Close();
	}
}

} // namespace Loader
