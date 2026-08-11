// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later
//
// Execute NPXS40087 12.40's FirstWave CPU stream builder under Rosetta.
//
// The native routine at VA 0x000c6c70 builds the interleaved vertex stream
// consumed by fw_flow_vl.  Static disassembly establishes the contract:
//
//   count  = 0x1900 records
//   stride = 0x34 bytes
//   +0x00  float3 position
//   +0x0c  float  radial fade coordinate
//   +0x10  float3 direction
//
// The routine is straight-line until that buffer is built.  Its only external
// leaves are sinf/cosf and the allocation wrapper, which are redirected below;
// all mesh generation and spline evaluation still execute from Sony's eboot.
// Build this file as x86_64 and run it through Rosetta on Apple silicon.

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iostream>
#include <limits>
#include <string>
#include <vector>

#include <sys/mman.h>

namespace {

constexpr std::size_t kImageExtent = 0x04000000;
constexpr std::uintptr_t kUpdateVa = 0x000c6c70;
constexpr std::uintptr_t kSinVa = 0x00cff9a0;
constexpr std::uintptr_t kCosVa = 0x00cff9b0;
constexpr std::uintptr_t kAllocateVa = 0x00cfdd80;
constexpr std::uintptr_t kMemcpyVa = 0x00cfddb0;
constexpr std::uintptr_t kBufferCtorVa = 0x000ca670;
constexpr std::uintptr_t kStackCanaryPointerVa = 0x011ccac0;
constexpr std::uint32_t kRecordCount = 0x1900;
constexpr std::uint32_t kRecordStride = 0x34;
constexpr std::size_t kStreamBytes =
    static_cast<std::size_t>(kRecordCount) * kRecordStride;

struct Elf64Header {
  std::array<unsigned char, 16> ident;
  std::uint16_t type;
  std::uint16_t machine;
  std::uint32_t version;
  std::uint64_t entry;
  std::uint64_t phoff;
  std::uint64_t shoff;
  std::uint32_t flags;
  std::uint16_t ehsize;
  std::uint16_t phentsize;
  std::uint16_t phnum;
  std::uint16_t shentsize;
  std::uint16_t shnum;
  std::uint16_t shstrndx;
};

struct Elf64ProgramHeader {
  std::uint32_t type;
  std::uint32_t flags;
  std::uint64_t offset;
  std::uint64_t vaddr;
  std::uint64_t paddr;
  std::uint64_t filesz;
  std::uint64_t memsz;
  std::uint64_t align;
};

static_assert(sizeof(Elf64Header) == 64);
static_assert(sizeof(Elf64ProgramHeader) == 56);

std::byte* gStream = nullptr;
std::uintptr_t gImageBase = 0;

extern "C" float HostSin(float value) { return std::sinf(value); }
extern "C" float HostCos(float value) { return std::cosf(value); }

extern "C" void* HostAllocate(std::size_t bytes) {
  std::cerr << "native allocation: " << bytes << " bytes\n";
  return std::calloc(1, bytes);
}

extern "C" void* HostMemcpy(void* destination, const void* source,
                              std::size_t bytes) {
  return std::memcpy(destination, source, bytes);
}

extern "C" void HostBufferCtor(void* wrapper, std::uint32_t count,
                                std::uint32_t stride, const void*,
                                std::uint32_t) {
  std::cerr << "native stream: " << count << " records, stride " << stride << "\n";
  if (count != kRecordCount || stride != kRecordStride) {
    std::cerr << "unexpected native stream contract: count=" << count
              << " stride=" << stride << '\n';
    std::abort();
  }
  std::memset(wrapper, 0, 0x138);
  gStream = static_cast<std::byte*>(std::calloc(count, stride));
  if (gStream == nullptr) {
    std::abort();
  }
  std::memcpy(static_cast<std::byte*>(wrapper) + 0x118, &gStream,
              sizeof(gStream));
}

std::vector<std::byte> ReadFile(const std::string& path) {
  std::ifstream input(path, std::ios::binary | std::ios::ate);
  if (!input) {
    throw std::runtime_error("cannot open " + path);
  }
  const auto end = input.tellg();
  if (end <= 0) {
    throw std::runtime_error("empty input " + path);
  }
  std::vector<std::byte> data(static_cast<std::size_t>(end));
  input.seekg(0);
  input.read(reinterpret_cast<char*>(data.data()), end);
  if (!input) {
    throw std::runtime_error("short read from " + path);
  }
  return data;
}

void LoadElf(const std::vector<std::byte>& file) {
  if (file.size() < sizeof(Elf64Header)) {
    throw std::runtime_error("ELF header is truncated");
  }
  const auto& header = *reinterpret_cast<const Elf64Header*>(file.data());
  if (header.ident[0] != 0x7f || header.ident[1] != 'E' ||
      header.ident[2] != 'L' || header.ident[3] != 'F') {
    throw std::runtime_error("input is not an ELF image");
  }
  if (header.machine != 62 || header.phentsize != sizeof(Elf64ProgramHeader)) {
    throw std::runtime_error("input is not the expected x86-64 ELF layout");
  }

  void* mapped = mmap(nullptr, kImageExtent,
                      PROT_READ | PROT_WRITE | PROT_EXEC,
                      MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
  if (mapped == MAP_FAILED) {
    throw std::runtime_error("cannot reserve the native image range");
  }
  gImageBase = reinterpret_cast<std::uintptr_t>(mapped);

  for (std::uint16_t index = 0; index < header.phnum; ++index) {
    const std::size_t offset =
        static_cast<std::size_t>(header.phoff) + index * header.phentsize;
    if (offset + sizeof(Elf64ProgramHeader) > file.size()) {
      throw std::runtime_error("program-header table is truncated");
    }
    const auto& segment = *reinterpret_cast<const Elf64ProgramHeader*>(
        file.data() + offset);
    if (segment.type != 1 || segment.filesz == 0) {
      continue;
    }
    if (segment.vaddr + segment.memsz > kImageExtent ||
        segment.offset + segment.filesz > file.size()) {
      throw std::runtime_error("load segment is outside the mapped image");
    }
    std::memcpy(reinterpret_cast<void*>(gImageBase + segment.vaddr),
                file.data() + segment.offset,
                static_cast<std::size_t>(segment.filesz));
  }
}

template <typename Function>
void InstallJump(std::uintptr_t guestVa, Function hostFunction) {
  auto* destination = reinterpret_cast<std::byte*>(gImageBase + guestVa);
  const std::uintptr_t target = reinterpret_cast<std::uintptr_t>(hostFunction);
  // movabs rax, target; jmp rax
  destination[0] = std::byte{0x48};
  destination[1] = std::byte{0xb8};
  std::memcpy(destination + 2, &target, sizeof(target));
  destination[10] = std::byte{0xff};
  destination[11] = std::byte{0xe0};
}

float ReadFloat(const std::byte* record, std::size_t offset) {
  float value = 0.0f;
  std::memcpy(&value, record + offset, sizeof(value));
  return value;
}

void ValidateStream() {
  if (gStream == nullptr) {
    throw std::runtime_error("native routine did not allocate its draw stream");
  }

  std::size_t finiteRecords = 0;
  float radialMin = std::numeric_limits<float>::infinity();
  float radialMax = -std::numeric_limits<float>::infinity();
  for (std::uint32_t index = 0; index < kRecordCount; ++index) {
    const auto* record = gStream + static_cast<std::size_t>(index) * kRecordStride;
    bool finite = true;
    for (const std::size_t offset : {0u, 4u, 8u, 12u, 16u, 20u, 24u}) {
      finite &= std::isfinite(ReadFloat(record, offset));
    }
    finiteRecords += finite ? 1 : 0;
    const float radial = ReadFloat(record, 12);
    if (std::isfinite(radial)) {
      radialMin = std::min(radialMin, radial);
      radialMax = std::max(radialMax, radial);
    }
  }
  if (finiteRecords != kRecordCount || !(radialMax > radialMin)) {
    throw std::runtime_error("captured stream failed finiteness/radial checks");
  }

  std::cout << "records : " << kRecordCount << " x " << kRecordStride
            << " = " << kStreamBytes << " bytes\n";
  std::cout << "radial  : [" << radialMin << ", " << radialMax << "]\n";
}

}  // namespace

int main(int argc, char** argv) {
  if (argc != 3) {
    std::cerr << "usage: capture_firstwave_draw_stream <12.40 eboot.bin> <out.bin>\n";
    return 2;
  }

  try {
    const auto file = ReadFile(argv[1]);
    LoadElf(file);
    std::cerr << "mapped native image at 0x" << std::hex << gImageBase << std::dec << '\n';

    InstallJump(kSinVa, &HostSin);
    InstallJump(kCosVa, &HostCos);
    InstallJump(kAllocateVa, &HostAllocate);
    InstallJump(kMemcpyVa, &HostMemcpy);
    InstallJump(kBufferCtorVa, &HostBufferCtor);

    std::uint64_t canary = 0x46f6973745766176ull;
    auto* canarySlot = reinterpret_cast<std::uintptr_t*>(
        gImageBase + kStackCanaryPointerVa);
    *canarySlot = reinterpret_cast<std::uintptr_t>(&canary);

    alignas(32) std::array<std::byte, 0x500> object{};
    alignas(16) std::array<std::byte, 0x20> update{};
    using UpdateFunction = void (*)(void*, const void*);
    auto updateFunction = reinterpret_cast<UpdateFunction>(gImageBase + kUpdateVa);
    std::cerr << "entering native update at 0x" << std::hex
              << reinterpret_cast<std::uintptr_t>(updateFunction) << std::dec << '\n';
    updateFunction(object.data(), update.data());
    std::cerr << "native update returned\n";

    ValidateStream();
    std::ofstream output(argv[2], std::ios::binary);
    output.write(reinterpret_cast<const char*>(gStream), kStreamBytes);
    if (!output) {
      throw std::runtime_error("cannot write output stream");
    }
    std::cout << "output  : " << argv[2] << '\n';
    return 0;
  } catch (const std::exception& error) {
    std::cerr << "capture failed: " << error.what() << '\n';
    return 1;
  }
}
