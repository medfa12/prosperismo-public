#include "common/file.h"
#include "loader/elf.h"

#include <array>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <limits>
#include <system_error>

namespace {

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "Prosperismo ELF loader contract test failed: %s\n", text);
		std::abort();
	}
}

class SyntheticElfFile {
public:
	SyntheticElfFile(): m_path(std::filesystem::temp_directory_path() /
	                           "prosperismo_loader_elf_contract_test.elf") {}

	~SyntheticElfFile() {
		std::error_code error;
		std::filesystem::remove(m_path, error);
	}

	void Write(uint8_t abi_version) const {
		const auto header = MakeHeader(abi_version);

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	void WriteHeaderSize(uint16_t header_size) const {
		auto header     = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_ehsize = header_size;

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	void WriteProgramHeaderTable(bool complete,
	                             uint64_t table_offset = sizeof(Loader::Elf64_Ehdr),
	                             uint64_t segment_file_size = 0,
	                             uint64_t segment_memory_size = 0) const {
		auto header    = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_phoff = table_offset;
		header.e_phnum = 1;

		Loader::Elf64_Phdr program_header {};
		program_header.p_type   = Loader::PT_LOAD;
		program_header.p_flags  = Loader::PF_R;
		program_header.p_offset = sizeof(header) + sizeof(program_header);
		program_header.p_filesz = segment_file_size;
		program_header.p_memsz  = segment_memory_size;

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		if (complete) {
			output.write(reinterpret_cast<const char*>(&program_header), sizeof(program_header));
			for (uint64_t i = 0; i < segment_file_size; i++) {
				output.put('\0');
			}
		}
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	void WriteSectionHeaderTable(uint64_t table_offset, uint16_t section_count,
	                             uint64_t physical_payload_size) const {
		auto header        = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_phnum     = 0;
		header.e_shoff     = table_offset;
		header.e_shentsize = section_count == 0 ? 0 : sizeof(Loader::Elf64_Shdr);
		header.e_shnum     = section_count;
		header.e_shstrndx  = 0;

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		for (uint64_t i = 0; i < physical_payload_size; i++) {
			output.put('\0');
		}
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	void WriteLoadSegmentExtent(uint64_t segment_offset, uint64_t segment_file_size,
	                            uint64_t physical_payload_size) const {
		auto header    = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_phnum = 1;

		Loader::Elf64_Phdr program_header {};
		program_header.p_type   = Loader::PT_LOAD;
		program_header.p_flags  = Loader::PF_R;
		program_header.p_offset = segment_offset;
		program_header.p_filesz = segment_file_size;
		program_header.p_memsz  = segment_file_size;

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		output.write(reinterpret_cast<const char*>(&program_header), sizeof(program_header));
		for (uint64_t i = 0; i < physical_payload_size; i++) {
			output.put('\0');
		}
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	void WriteDynamicSegmentExtent(uint64_t segment_offset, uint64_t segment_file_size,
	                               uint64_t physical_payload_size) const {
		auto header    = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_phnum = 1;

		Loader::Elf64_Phdr program_header {};
		program_header.p_type   = Loader::PT_DYNAMIC;
		program_header.p_flags  = Loader::PF_R;
		program_header.p_offset = segment_offset;
		program_header.p_filesz = segment_file_size;
		program_header.p_memsz  = segment_file_size;

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		output.write(reinterpret_cast<const char*>(&program_header), sizeof(program_header));
		for (uint64_t i = 0; i < physical_payload_size; i++) {
			output.put('\0');
		}
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	void WriteDynamicSegmentAlignment(uint64_t segment_offset, uint64_t segment_vaddr,
	                                  uint64_t segment_alignment) const {
		auto header    = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_phnum = 1;

		Loader::Elf64_Phdr program_header {};
		program_header.p_type   = Loader::PT_DYNAMIC;
		program_header.p_flags  = Loader::PF_R;
		program_header.p_offset = segment_offset;
		program_header.p_vaddr  = segment_vaddr;
		program_header.p_filesz = sizeof(Loader::Elf64_Dyn);
		program_header.p_memsz  = sizeof(Loader::Elf64_Dyn);
		program_header.p_align  = segment_alignment;

		const uint64_t headers_size = sizeof(header) + sizeof(program_header);
		Check(segment_offset >= headers_size,
		      "dynamic alignment fixture overlaps its program header");
		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		output.write(reinterpret_cast<const char*>(&program_header), sizeof(program_header));
		for (uint64_t i = headers_size; i < segment_offset; i++) {
			output.put('\0');
		}
		const Loader::Elf64_Dyn terminator {};
		output.write(reinterpret_cast<const char*>(&terminator), sizeof(terminator));
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	void WriteDynamicSegmentCount(uint16_t segment_count) const {
		Check(segment_count <= 2, "unsupported synthetic PT_DYNAMIC segment count");

		auto header    = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_phnum = segment_count;

		std::array<Loader::Elf64_Phdr, 2> program_headers {};
		const uint64_t payload_offset =
		    sizeof(header) + segment_count * sizeof(Loader::Elf64_Phdr);
		for (uint16_t i = 0; i < segment_count; i++) {
			program_headers[i].p_type   = Loader::PT_DYNAMIC;
			program_headers[i].p_flags  = Loader::PF_R;
			program_headers[i].p_offset = payload_offset;
			program_headers[i].p_filesz = sizeof(Loader::Elf64_Dyn);
			program_headers[i].p_memsz  = sizeof(Loader::Elf64_Dyn);
		}

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		output.write(reinterpret_cast<const char*>(program_headers.data()),
		             segment_count * sizeof(Loader::Elf64_Phdr));
		if (segment_count != 0) {
			const Loader::Elf64_Dyn terminator {};
			output.write(reinterpret_cast<const char*>(&terminator), sizeof(terminator));
		}
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	template <std::size_t EntryCount>
	void WriteDynamicEntries(
	    const std::array<Loader::Elf64_Dyn, EntryCount>& entries) const {
		auto header    = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_phnum = 1;

		Loader::Elf64_Phdr program_header {};
		program_header.p_type   = Loader::PT_DYNAMIC;
		program_header.p_flags  = Loader::PF_R;
		program_header.p_offset = sizeof(header) + sizeof(program_header);
		program_header.p_filesz = entries.size() * sizeof(Loader::Elf64_Dyn);
		program_header.p_memsz  = program_header.p_filesz;

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		output.write(reinterpret_cast<const char*>(&program_header), sizeof(program_header));
		output.write(reinterpret_cast<const char*>(entries.data()),
		             static_cast<std::streamsize>(program_header.p_filesz));
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	void WriteTlsSegmentCount(uint16_t segment_count,
	                          Loader::Elf64_Word segment_flags = Loader::PF_R) const {
		Check(segment_count <= 2, "unsupported synthetic PT_TLS segment count");

		auto header    = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_phnum = segment_count;

		std::array<Loader::Elf64_Phdr, 2> program_headers {};
		for (uint16_t i = 0; i < segment_count; i++) {
			program_headers[i].p_type  = Loader::PT_TLS;
			program_headers[i].p_flags = segment_flags;
			program_headers[i].p_align = 1;
		}

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		output.write(reinterpret_cast<const char*>(program_headers.data()),
		             segment_count * sizeof(Loader::Elf64_Phdr));
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	void WriteTlsInitializationImage(uint64_t tls_offset, uint64_t tls_address,
	                                 uint64_t tls_file_size,
	                                 uint64_t physical_payload_size) const {
		auto header    = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_phnum = 2;

		constexpr uint64_t load_address = 0x1000;
		constexpr uint64_t load_size    = 16;
		const uint64_t load_offset = sizeof(header) + 2 * sizeof(Loader::Elf64_Phdr);

		std::array<Loader::Elf64_Phdr, 2> program_headers {};
		program_headers[0].p_type   = Loader::PT_LOAD;
		program_headers[0].p_flags  = Loader::PF_R;
		program_headers[0].p_offset = load_offset;
		program_headers[0].p_vaddr  = load_address;
		program_headers[0].p_filesz = load_size;
		program_headers[0].p_memsz  = load_size;
		program_headers[0].p_align  = 1;

		program_headers[1].p_type   = Loader::PT_TLS;
		program_headers[1].p_flags  = Loader::PF_R;
		program_headers[1].p_offset = tls_offset;
		program_headers[1].p_vaddr  = tls_address;
		program_headers[1].p_filesz = tls_file_size;
		program_headers[1].p_memsz  = tls_file_size;
		program_headers[1].p_align  = 1;

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		output.write(reinterpret_cast<const char*>(program_headers.data()),
		             program_headers.size() * sizeof(Loader::Elf64_Phdr));
		for (uint64_t i = 0; i < physical_payload_size; i++) {
			output.put('\0');
		}
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	void WriteLoadSegmentMemoryExtent(uint64_t segment_address,
	                                  uint64_t segment_memory_size) const {
		auto header    = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_phnum = 1;

		Loader::Elf64_Phdr program_header {};
		program_header.p_type   = Loader::PT_LOAD;
		program_header.p_flags  = Loader::PF_R;
		program_header.p_offset = sizeof(header) + sizeof(program_header);
		program_header.p_vaddr  = segment_address;
		program_header.p_memsz  = segment_memory_size;

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		output.write(reinterpret_cast<const char*>(&program_header), sizeof(program_header));
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	void WriteLoadSegmentAlignment(uint64_t segment_offset, uint64_t segment_address,
	                               uint64_t segment_alignment) const {
		auto header    = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_phnum = 1;

		Loader::Elf64_Phdr program_header {};
		program_header.p_type   = Loader::PT_LOAD;
		program_header.p_flags  = Loader::PF_R;
		program_header.p_offset = segment_offset;
		program_header.p_vaddr  = segment_address;
		program_header.p_memsz  = 1;
		program_header.p_align  = segment_alignment;

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		output.write(reinterpret_cast<const char*>(&program_header), sizeof(program_header));
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	void WriteLoadSegmentOrder(uint64_t first_address, uint64_t second_address,
	                           uint64_t segment_memory_size = 1) const {
		auto header    = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_phnum = 2;

		std::array<Loader::Elf64_Phdr, 2> program_headers {};
		for (auto& program_header: program_headers) {
			program_header.p_type   = Loader::PT_LOAD;
			program_header.p_flags  = Loader::PF_R;
			program_header.p_offset = sizeof(header) + sizeof(program_headers);
			program_header.p_memsz  = segment_memory_size;
		}
		program_headers[0].p_vaddr = first_address;
		program_headers[1].p_vaddr = second_address;

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		output.write(reinterpret_cast<const char*>(program_headers.data()), sizeof(program_headers));
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	void WriteLoadSegmentRounding(uint64_t segment_memory_size,
	                              uint64_t segment_alignment) const {
		auto header    = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_phnum = 1;

		Loader::Elf64_Phdr program_header {};
		program_header.p_type  = Loader::PT_LOAD;
		program_header.p_flags = Loader::PF_R;
		program_header.p_memsz = segment_memory_size;
		program_header.p_align = segment_alignment;

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		output.write(reinterpret_cast<const char*>(&program_header), sizeof(program_header));
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	void WriteEntryPoint(uint64_t entry_address, uint64_t segment_address,
	                     uint64_t segment_file_size, uint64_t segment_memory_size,
	                     Loader::Elf64_Word segment_flags) const {
		auto header = MakeHeader(Loader::ELF_ABI_VERSION_NEXT_GEN);
		header.e_entry  = entry_address;
		header.e_phnum  = 1;

		Loader::Elf64_Phdr program_header {};
		program_header.p_type   = Loader::PT_LOAD;
		program_header.p_flags  = segment_flags;
		program_header.p_offset = sizeof(header) + sizeof(program_header);
		program_header.p_vaddr  = segment_address;
		program_header.p_filesz = segment_file_size;
		program_header.p_memsz  = segment_memory_size;

		std::ofstream output(m_path, std::ios::binary | std::ios::trunc);
		Check(output.is_open(), "could not create the synthetic ELF fixture");
		output.write(reinterpret_cast<const char*>(&header), sizeof(header));
		output.write(reinterpret_cast<const char*>(&program_header), sizeof(program_header));
		for (uint64_t i = 0; i < segment_file_size; i++) {
			output.put('\0');
		}
		Check(output.good(), "could not write the synthetic ELF fixture");
	}

	[[nodiscard]] const std::filesystem::path& Path() const { return m_path; }

private:
	static Loader::Elf64_Ehdr MakeHeader(uint8_t abi_version) {
		Loader::Elf64_Ehdr header {};
		header.e_ident[Loader::EI_MAG0]       = 0x7f;
		header.e_ident[Loader::EI_MAG1]       = 'E';
		header.e_ident[Loader::EI_MAG2]       = 'L';
		header.e_ident[Loader::EI_MAG3]       = 'F';
		header.e_ident[Loader::EI_CLASS]      = Loader::ELFCLASS64;
		header.e_ident[Loader::EI_DATA]       = Loader::ELFDATA2LSB;
		header.e_ident[Loader::EI_VERSION]    = Loader::EV_CURRENT;
		header.e_ident[Loader::EI_OSABI]      = Loader::ELFOSABI_FREEBSD;
		header.e_ident[Loader::EI_ABIVERSION] = abi_version;
		header.e_type                         = Loader::ET_DYNAMIC;
		header.e_machine                      = Loader::EM_X86_64;
		header.e_version                      = Loader::EV_CURRENT;
		header.e_phoff                        = sizeof(header);
		header.e_ehsize                       = sizeof(header);
		header.e_phentsize                    = sizeof(Loader::Elf64_Phdr);
		header.e_shentsize                    = sizeof(Loader::Elf64_Shdr);
		return header;
	}

	std::filesystem::path m_path;
};

void TestSupportedAbiVersions() {
	SyntheticElfFile file;

	for (const uint8_t abi_version: std::array<uint8_t, 4> {0, 1, 2, 3}) {
		file.Write(abi_version);
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a supported ABI version was rejected");
	}

	file.Write(4);
	Loader::Elf64 elf;
	elf.Open(file.Path());
	Check(!elf.IsValid(), "an unverified ABI version was accepted");
}

void TestNextGenerationDiscriminator() {
	SyntheticElfFile file;

	for (const uint8_t abi_version: std::array<uint8_t, 3> {1, 2, 3}) {
		file.Write(abi_version);
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a supported ABI version was rejected");
		Check(elf.IsNextGen() == (abi_version == Loader::ELF_ABI_VERSION_NEXT_GEN),
		      "the shared-module ABI version changed main-image classification");
	}
}

void TestElfHeaderSize() {
	SyntheticElfFile file;

	file.WriteHeaderSize(sizeof(Loader::Elf64_Ehdr));
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a standard ELF64 header size was rejected");
	}

	for (const uint16_t header_size:
	     std::array<uint16_t, 4> {0, sizeof(Loader::Elf64_Ehdr) - 1,
	                              sizeof(Loader::Elf64_Ehdr) + 1,
	                              std::numeric_limits<uint16_t>::max()}) {
		file.WriteHeaderSize(header_size);
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "a nonstandard ELF64 header size was accepted");
	}
}

void TestProgramHeaderTableBounds() {
	SyntheticElfFile file;

	file.Write(Loader::ELF_ABI_VERSION_NEXT_GEN);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a zero-entry program-header table was rejected");
	}

	file.WriteProgramHeaderTable(true);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a complete program-header table was rejected");
	}

	file.WriteProgramHeaderTable(false);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "a truncated program-header table was accepted");
	}

	file.WriteProgramHeaderTable(false, std::numeric_limits<uint64_t>::max());
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "an overflowing program-header offset was accepted");
	}
}

void TestSectionHeaderTableBounds() {
	SyntheticElfFile file;
	constexpr uint64_t table_offset = sizeof(Loader::Elf64_Ehdr);
	constexpr uint64_t table_size   = sizeof(Loader::Elf64_Shdr);

	file.WriteSectionHeaderTable(table_offset, 1, table_size);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an exact-end section header table was rejected");
		Check(elf.GetShdr() != nullptr, "an in-range section header table was skipped");
	}

	file.WriteSectionHeaderTable(std::numeric_limits<uint64_t>::max(), 0, 0);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a zero-entry section header table was rejected");
		Check(elf.GetShdr() == nullptr, "a zero-entry section header table was loaded");
	}

	file.WriteSectionHeaderTable(table_offset, 1, table_size - 1);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an image with a truncated optional section table was rejected");
		Check(elf.GetShdr() == nullptr, "a truncated section header table was loaded");
	}

	file.WriteSectionHeaderTable(std::numeric_limits<uint64_t>::max() - 31, 1, 0);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an image with an overflowing optional section table was rejected");
		Check(elf.GetShdr() == nullptr, "an overflowing section header table was loaded");
	}
}

void TestLoadSegmentSizeRelationship() {
	SyntheticElfFile file;

	file.WriteProgramHeaderTable(true, sizeof(Loader::Elf64_Ehdr), 0, 0);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an empty PT_LOAD was rejected");
	}

	file.WriteProgramHeaderTable(true, sizeof(Loader::Elf64_Ehdr), 0, 16);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a zero-file-size PT_LOAD was rejected");
	}

	file.WriteProgramHeaderTable(true, sizeof(Loader::Elf64_Ehdr), 16, 16);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an equal-size PT_LOAD was rejected");
	}

	file.WriteProgramHeaderTable(true, sizeof(Loader::Elf64_Ehdr), 8, 16);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a PT_LOAD with a memory-only tail was rejected");
	}

	file.WriteProgramHeaderTable(true, sizeof(Loader::Elf64_Ehdr), 17, 16);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "a PT_LOAD larger on disk than in memory was accepted");
	}
}

void TestLoadSegmentFileExtent() {
	SyntheticElfFile file;
	constexpr uint64_t payload_offset =
	    sizeof(Loader::Elf64_Ehdr) + sizeof(Loader::Elf64_Phdr);

	file.WriteLoadSegmentExtent(payload_offset, 16, 16);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a PT_LOAD ending exactly at end-of-file was rejected");
	}

	file.WriteLoadSegmentExtent(payload_offset, 16, 15);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "a truncated PT_LOAD file image was accepted");
	}

	file.WriteLoadSegmentExtent(std::numeric_limits<uint64_t>::max() - 7, 16, 0);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "an overflowing PT_LOAD file extent was accepted");
	}
}

void TestDynamicSegmentFileExtent() {
	SyntheticElfFile file;
	constexpr uint64_t payload_offset = sizeof(Loader::Elf64_Ehdr) + sizeof(Loader::Elf64_Phdr);
	constexpr uint64_t payload_size   = sizeof(Loader::Elf64_Dyn);

	file.WriteDynamicSegmentExtent(payload_offset, payload_size, payload_size);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an exact-end PT_DYNAMIC file range was rejected");
	}

	file.WriteDynamicSegmentExtent(payload_offset, 0, 0);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an exact-end zero-sized PT_DYNAMIC range was rejected");
	}

	file.WriteDynamicSegmentExtent(payload_offset, payload_size, payload_size - 1);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "a truncated PT_DYNAMIC file range was accepted");
	}

	file.WriteDynamicSegmentExtent(std::numeric_limits<uint64_t>::max() - 7, payload_size,
	                               payload_size);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "an overflowing PT_DYNAMIC file range was accepted");
	}
}

void TestDynamicSegmentEntrySize() {
	SyntheticElfFile file;
	constexpr uint64_t payload_offset = sizeof(Loader::Elf64_Ehdr) + sizeof(Loader::Elf64_Phdr);
	constexpr uint64_t entry_size     = sizeof(Loader::Elf64_Dyn);

	for (const uint64_t entry_count: std::array<uint64_t, 3> {0, 1, 2}) {
		const uint64_t table_size = entry_count * entry_size;
		file.WriteDynamicSegmentExtent(payload_offset, table_size, table_size);
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an integral PT_DYNAMIC entry count was rejected");
	}

	file.WriteDynamicSegmentExtent(payload_offset, entry_size - 1, entry_size - 1);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "a partial PT_DYNAMIC entry was accepted");
	}

	file.WriteDynamicSegmentExtent(payload_offset, entry_size + 1, entry_size + 1);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "a PT_DYNAMIC table with trailing partial data was accepted");
	}
}

void TestDynamicSegmentAlignment() {
	SyntheticElfFile file;
	constexpr uint64_t segment_offset = 0x80;

	struct AlignmentCase {
		uint64_t alignment;
		uint64_t virtual_address;
		bool     valid;
	};
	constexpr std::array<AlignmentCase, 6> cases {{
	    {0, 0x1001, true},
	    {1, 0x1001, true},
	    {8, 0x1000, true},
	    {uint64_t {1} << 63, segment_offset, true},
	    {3, segment_offset, false},
	    {8, 0x1001, false},
	}};

	for (const auto& test_case: cases) {
		file.WriteDynamicSegmentAlignment(
		    segment_offset, test_case.virtual_address, test_case.alignment);
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid() == test_case.valid,
		      test_case.valid ? "a valid PT_DYNAMIC alignment was rejected"
		                      : "an invalid PT_DYNAMIC alignment was accepted");
	}
}

void TestDynamicSegmentUniqueness() {
	SyntheticElfFile file;

	file.WriteDynamicSegmentCount(0);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an ELF without PT_DYNAMIC was rejected");
	}

	file.WriteDynamicSegmentCount(1);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a single PT_DYNAMIC segment was rejected");
	}

	file.WriteDynamicSegmentCount(2);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "multiple PT_DYNAMIC segments were accepted");
	}
}

void TestDynamicSegmentScanBounds() {
	SyntheticElfFile file;

	Loader::Elf64_Dyn needed {};
	needed.d_tag      = Loader::DT_NEEDED;
	needed.d_un.d_val = 7;
	file.WriteDynamicEntries(std::array<Loader::Elf64_Dyn, 1> {needed});
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an unterminated exact-extent dynamic table was rejected");
		const auto* entry = elf.GetDynValue(Loader::DT_NEEDED);
		Check(entry != nullptr && entry->d_un.d_val == 7,
		      "an in-range dynamic entry was not found");
		Check(elf.GetDynValue(Loader::DT_SONAME) == nullptr,
		      "a missing dynamic tag search escaped the declared table");
		Check(elf.GetDynList(Loader::DT_SONAME).empty(),
		      "a missing dynamic tag list escaped the declared table");
	}

	Loader::Elf64_Dyn terminator {};
	Loader::Elf64_Dyn trailing {};
	trailing.d_tag      = Loader::DT_SONAME;
	trailing.d_un.d_val = 11;
	file.WriteDynamicEntries(
	    std::array<Loader::Elf64_Dyn, 3> {needed, terminator, trailing});
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a terminated dynamic table was rejected");
		Check(elf.GetDynValue(Loader::DT_SONAME) == nullptr,
		      "a dynamic search continued past its terminator");
	}
}

void TestTlsSegmentUniqueness() {
	SyntheticElfFile file;

	file.WriteTlsSegmentCount(0);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an ELF without PT_TLS was rejected");
	}

	file.WriteTlsSegmentCount(1);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a single PT_TLS segment was rejected");
	}

	file.WriteTlsSegmentCount(2);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "multiple PT_TLS segments were accepted");
	}
}

void TestTlsSegmentFlags() {
	SyntheticElfFile file;

	file.WriteTlsSegmentCount(1, Loader::PF_R);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a read-only PT_TLS segment was rejected");
	}

	for (const Loader::Elf64_Word flags:
	     std::array<Loader::Elf64_Word, 4> {0, Loader::PF_W, Loader::PF_X,
	                                        Loader::PF_R | Loader::PF_W}) {
		file.WriteTlsSegmentCount(1, flags);
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "a PT_TLS segment without exact read-only flags was accepted");
	}
}

void TestTlsInitializationImageMapping() {
	SyntheticElfFile file;
	constexpr uint64_t load_offset =
	    sizeof(Loader::Elf64_Ehdr) + 2 * sizeof(Loader::Elf64_Phdr);
	constexpr uint64_t load_address = 0x1000;
	constexpr uint64_t load_size    = 16;

	file.WriteTlsInitializationImage(load_offset + 8, load_address + 8, 8, load_size);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an exact-end PT_TLS initialization image was rejected");
	}

	file.WriteTlsInitializationImage(0, 0, 0, load_size);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a zero-sized PT_TLS initialization image was rejected");
	}

	file.WriteTlsInitializationImage(load_offset + 8, load_address + 4, 8, load_size);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "a PT_TLS image with mismatched file and virtual offsets was accepted");
	}

	file.WriteTlsInitializationImage(load_offset + 12, load_address + 12, 8, load_size + 4);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "a PT_TLS image extending beyond its PT_LOAD was accepted");
	}

	constexpr uint64_t overflowing_start = std::numeric_limits<uint64_t>::max() - 3;
	file.WriteTlsInitializationImage(overflowing_start, overflowing_start, 8, load_size);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "an overflowing PT_TLS initialization image was accepted");
	}
}

void TestLoadSegmentMemoryExtent() {
	SyntheticElfFile file;
	constexpr auto max_address = std::numeric_limits<uint64_t>::max();

	file.WriteLoadSegmentMemoryExtent(max_address, 0);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a zero-sized PT_LOAD at the maximum address was rejected");
	}

	file.WriteLoadSegmentMemoryExtent(max_address - 16, 16);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a PT_LOAD with an exactly representable virtual end was rejected");
	}

	file.WriteLoadSegmentMemoryExtent(max_address - 15, 16);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "an overflowing PT_LOAD virtual range was accepted");
	}
}

void TestLoadSegmentAlignment() {
	SyntheticElfFile file;
	constexpr uint64_t segment_offset =
	    sizeof(Loader::Elf64_Ehdr) + sizeof(Loader::Elf64_Phdr);

	for (const uint64_t unconstrained_alignment: std::array<uint64_t, 2> {0, 1}) {
		file.WriteLoadSegmentAlignment(segment_offset, segment_offset + 1,
		                               unconstrained_alignment);
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an unconstrained PT_LOAD alignment was rejected");
	}

	file.WriteLoadSegmentAlignment(segment_offset, segment_offset + 0x4000, 0x4000);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a congruent PT_LOAD alignment was rejected");
	}

	constexpr uint64_t largest_power_of_two = uint64_t {1} << 63;
	file.WriteLoadSegmentAlignment(segment_offset, segment_offset, largest_power_of_two);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "the largest power-of-two PT_LOAD alignment was rejected");
	}

	file.WriteLoadSegmentAlignment(segment_offset, segment_offset, 3);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "a non-power-of-two PT_LOAD alignment was accepted");
	}

	file.WriteLoadSegmentAlignment(segment_offset, segment_offset + 1, 0x4000);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "an incongruent PT_LOAD address and offset were accepted");
	}
}

void TestLoadSegmentOrder() {
	SyntheticElfFile file;

	file.WriteLoadSegmentOrder(0x1000, 0x2000);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "increasing PT_LOAD virtual addresses were rejected");
	}

	file.WriteLoadSegmentOrder(0x1000, 0x1000, 0);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "equal zero-sized PT_LOAD virtual addresses were rejected");
	}

	file.WriteLoadSegmentOrder(0x1001, 0x1000);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "decreasing PT_LOAD virtual addresses were accepted");
	}
}

void TestLoadSegmentSizeRounding() {
	SyntheticElfFile file;
	constexpr auto max_size = std::numeric_limits<uint64_t>::max();

	file.WriteLoadSegmentRounding(max_size - 0xf, 0x10);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an exactly representable PT_LOAD size round-up was rejected");
	}

	file.WriteLoadSegmentRounding(max_size - 0xe, 0x10);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "an overflowing PT_LOAD size round-up was accepted");
	}
}

void TestLoadSegmentAlignedExtent() {
	SyntheticElfFile file;
	constexpr auto max_address = std::numeric_limits<uint64_t>::max();

	file.WriteLoadSegmentAlignment(0xf, max_address - 0x10, 0x10);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an exactly representable aligned PT_LOAD virtual end was rejected");
	}

	file.WriteLoadSegmentAlignment(0, max_address - 0xf, 0x10);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "an overflowing aligned PT_LOAD virtual range was accepted");
	}
}

void TestEntryPointExtent() {
	SyntheticElfFile file;
	constexpr uint64_t segment_address = 0x4000;
	constexpr uint64_t segment_size    = 0x20;
	constexpr auto     executable      = Loader::PF_R | Loader::PF_X;

	file.WriteEntryPoint(0, segment_address, segment_size, segment_size, Loader::PF_R);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "a conventional zero entry point was rejected");
	}

	file.WriteEntryPoint(segment_address, segment_address, segment_size, segment_size, executable);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an entry point at the start of executable file data was rejected");
	}

	file.WriteEntryPoint(segment_address + segment_size - 1, segment_address, segment_size,
	                     segment_size, executable);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "an entry point at the final executable file byte was rejected");
	}

	constexpr uint64_t max_address = std::numeric_limits<uint64_t>::max();
	file.WriteEntryPoint(max_address - 1, max_address - segment_size, segment_size, segment_size,
	                     executable);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(elf.IsValid(), "the highest representable executable entry point was rejected");
	}

	file.WriteEntryPoint(segment_address + segment_size, segment_address, segment_size, segment_size,
	                     executable);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "a one-past executable entry point was accepted");
	}

	file.WriteEntryPoint(segment_address, segment_address, segment_size, segment_size, Loader::PF_R);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "an entry point in a non-executable load segment was accepted");
	}

	file.WriteEntryPoint(segment_address + segment_size, segment_address, segment_size,
	                     segment_size * 2, executable);
	{
		Loader::Elf64 elf;
		elf.Open(file.Path());
		Check(!elf.IsValid(), "an entry point in a memory-only segment tail was accepted");
	}
}

} // namespace

int main() {
	TestSupportedAbiVersions();
	TestNextGenerationDiscriminator();
	TestElfHeaderSize();
	TestProgramHeaderTableBounds();
	TestSectionHeaderTableBounds();
	TestLoadSegmentSizeRelationship();
	TestLoadSegmentFileExtent();
	TestDynamicSegmentFileExtent();
	TestDynamicSegmentEntrySize();
	TestDynamicSegmentAlignment();
	TestDynamicSegmentUniqueness();
	TestDynamicSegmentScanBounds();
	TestTlsSegmentUniqueness();
	TestTlsSegmentFlags();
	TestTlsInitializationImageMapping();
	TestLoadSegmentMemoryExtent();
	TestLoadSegmentAlignment();
	TestLoadSegmentOrder();
	TestLoadSegmentSizeRounding();
	TestLoadSegmentAlignedExtent();
	TestEntryPointExtent();
	return 0;
}
