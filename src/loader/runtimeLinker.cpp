#include "loader/runtimeLinker.h"

#include "common/assert.h"
#include "common/common.h"
#include "common/emulatorConfig.h"
#include "common/file.h"
#include "common/hostException.h"
#include "common/logging/log.h"
#include "common/magicEnum.h"
#include "common/platform/sysDbg.h"
#include "common/profiler.h"
#include "common/singleton.h"
#include "common/stringUtils.h"
#include "common/threads.h"
#include "common/virtualMemory.h"
#include "graphics/host_gpu/pageManager.h"
#include "kernel/memory.h"
#include "kernel/pthread.h"
#include "loader/elf.h"
#include "loader/gamePatch.h"
#include "loader/jit.h"
#include "loader/runtimeLinkerBounds.h"
#include "loader/symbolDatabase.h"
#include "loader/x64InstructionEmulator.h"

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fmt/format.h>
#include <limits>
#include <memory>
#include <vector>

static_assert(sizeof(Loader::Elf64_Rela) == Loader::RuntimeLinkerBounds::ELF64_RELA_SIZE);
static_assert(sizeof(Loader::Elf64_Sym) == Loader::RuntimeLinkerBounds::ELF64_SYM_SIZE);

#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <dlfcn.h>
#include <fcntl.h>
#if defined(__APPLE__)
#include <mach/mach.h>
#include <mach/mach_vm.h>
#include <unistd.h>
#elif KYTY_PLATFORM == KYTY_PLATFORM_LINUX
#include <sys/uio.h>
#include <unistd.h>
#endif
#endif

namespace Libs::LibKernel {
void SetProgName(const std::string& name);
} // namespace Libs::LibKernel

namespace Loader {

Program::Program() = default;

Program::~Program() = default;

static void FreeTlsBlock(ThreadLocalStorage::Block* block) {
	if (block == nullptr || block->ptr == nullptr) {
		return;
	}

	if (block->free_func != nullptr) {
		block->free_func(block->ptr);
	} else if (block->vm_alloc) {
		EXIT_IF(!Libs::LibKernel::Memory::FreeGuestMemory(reinterpret_cast<uint64_t>(block->ptr),
		                                                  block->alloc_size));
	} else {
		delete[] block->ptr;
	}

	block->ptr        = nullptr;
	block->free_func  = nullptr;
	block->vm_alloc   = false;
	block->alloc_size = 0;
}

static uint64_t AlignUp(uint64_t value, uint64_t alignment) {
	return alignment != 0 ? (value + alignment - 1) & ~(alignment - 1) : value;
}

ThreadLocalStorage::~ThreadLocalStorage() {
	for (auto& [_, block]: tlss) {
		FreeTlsBlock(&block);
	}
}

#pragma pack(1)

struct EntryParams {
	int         argc;
	uint32_t    pad;
	const char* argv[3];
};

#pragma pack()

using atexit_func_t = KYTY_SYSV_ABI void (*)();
using entry_func_t  = KYTY_SYSV_ABI void (*)(EntryParams* params, atexit_func_t atexit_func);
using module_ini_fini_func_t = KYTY_SYSV_ABI int (*)(size_t args, const void* argp,
                                                     module_func_t func);

enum class BindType { Unknown, Local, Global, Weak };

struct RelocationInfo {
	bool        resolved   = false;
	BindType    bind       = BindType::Unknown;
	SymbolType  type       = SymbolType::Unknown;
	uint64_t    value      = 0;
	uint64_t    vaddr      = 0;
	uint64_t    base_vaddr = 0;
	std::string name;
	std::string dbg_name;
	bool        bind_self = false;
};

struct StubbedImportRecord {
	uint32_t    index       = 0;
	uint64_t    patch_vaddr = 0;
	uint64_t    thunk_vaddr = 0;
	std::string name;
	SymbolType  type = SymbolType::Unknown;
	BindType    bind = BindType::Unknown;
	std::string program;
};

// The structure will be passed via the stack
// since the size of an object is larger than 16 bytes
struct RelocateHandlerStack {
	uint64_t stack[3];
};

static std::vector<StubbedImportRecord> g_stubbed_imports;
static std::atomic_uint32_t             g_unresolved_stub_call_log_count {0};
static std::vector<uint64_t>            g_unresolved_stub_thunk_pages;
static uint64_t                         g_unresolved_stub_thunk_offset = 0;
static constexpr uint64_t               UNRESOLVED_STUB_PAGE_SIZE      = 4096;

static KYTY_SYSV_ABI uint64_t ResolveImportStubWithId(uint64_t record_id);

static bool PatchGuestMemory64(uint64_t vaddr, uint64_t value) {
	auto* ptr     = reinterpret_cast<uint64_t*>(vaddr);
	bool  changed = (*ptr != value);
	std::memcpy(ptr, &value, sizeof(value));
	return changed;
}

static uint64_t AllocateUnresolvedImportThunk(uint64_t record_id) {
	constexpr uint64_t thunk_size = 165;

	if (g_unresolved_stub_thunk_pages.empty() ||
	    g_unresolved_stub_thunk_offset + thunk_size > UNRESOLVED_STUB_PAGE_SIZE) {
		auto page = Libs::LibKernel::Memory::AllocateRuntimeMemory(
		    0, UNRESOLVED_STUB_PAGE_SIZE, Common::VirtualMemory::Mode::ExecuteReadWrite,
		    "unresolved_import_thunk");
		EXIT_NOT_IMPLEMENTED(page == 0);
		g_unresolved_stub_thunk_pages.push_back(page);
		g_unresolved_stub_thunk_offset = 0;
	}

	auto* code = reinterpret_cast<uint8_t*>(g_unresolved_stub_thunk_pages.back() +
	                                        g_unresolved_stub_thunk_offset);
	g_unresolved_stub_thunk_offset += thunk_size;

	const auto target = reinterpret_cast<uint64_t>(ResolveImportStubWithId);
	uint8_t    bytes[thunk_size] {};
	size_t     i      = 0;
	const auto emit   = [&](uint8_t b) { bytes[i++] = b; };
	const auto emit64 = [&](uint64_t v) {
		std::memcpy(bytes + i, &v, sizeof(v));
		i += sizeof(v);
	};
	const auto emit32 = [&](uint32_t v) {
		std::memcpy(bytes + i, &v, sizeof(v));
		i += sizeof(v);
	};
	const auto save_xmm = [&](uint8_t reg, uint8_t offset) {
		emit(0xf3);
		emit(0x0f);
		emit(0x7f);
		if (offset == 0) {
			emit(static_cast<uint8_t>(0x04u | (reg << 3u)));
			emit(0x24);
		} else {
			emit(static_cast<uint8_t>(0x44u | (reg << 3u)));
			emit(0x24);
			emit(offset);
		}
	};
	const auto load_xmm = [&](uint8_t reg, uint8_t offset) {
		emit(0xf3);
		emit(0x0f);
		emit(0x6f);
		if (offset == 0) {
			emit(static_cast<uint8_t>(0x04u | (reg << 3u)));
			emit(0x24);
		} else {
			emit(static_cast<uint8_t>(0x44u | (reg << 3u)));
			emit(0x24);
			emit(offset);
		}
	};

	emit(0x50); // push rax; preserve AL for variadic SysV calls
	emit(0x57); // push rdi
	emit(0x56); // push rsi
	emit(0x52); // push rdx
	emit(0x51); // push rcx
	emit(0x41);
	emit(0x50); // push r8
	emit(0x41);
	emit(0x51); // push r9
	emit(0x48);
	emit(0x81);
	emit(0xec);
	emit32(0x80); // sub rsp, 0x80
	for (uint8_t reg = 0; reg < 8; reg++) {
		save_xmm(reg, static_cast<uint8_t>(reg * 0x10u));
	}
	emit(0x48);
	emit(0xbf);
	emit64(record_id); // mov rdi, record_id
	emit(0x48);
	emit(0xb8);
	emit64(target); // mov rax, ResolveImportStubWithId
	emit(0xff);
	emit(0xd0); // call rax
	emit(0x49);
	emit(0x89);
	emit(0xc3); // mov r11, rax
	for (uint8_t reg = 0; reg < 8; reg++) {
		load_xmm(reg, static_cast<uint8_t>(reg * 0x10u));
	}
	emit(0x48);
	emit(0x81);
	emit(0xc4);
	emit32(0x80); // add rsp, 0x80
	emit(0x41);
	emit(0x59); // pop r9
	emit(0x41);
	emit(0x58); // pop r8
	emit(0x59); // pop rcx
	emit(0x5a); // pop rdx
	emit(0x5e); // pop rsi
	emit(0x5f); // pop rdi
	emit(0x58); // pop rax
	emit(0x4d);
	emit(0x85);
	emit(0xdb); // test r11, r11
	emit(0x74);
	emit(0x03); // jz +3
	emit(0x41);
	emit(0xff);
	emit(0xe3); // jmp r11
	// Match the integer fallback for floating-point return values.
	emit(0x0f);
	emit(0x57);
	emit(0xc0); // xorps xmm0, xmm0
	emit(0x31);
	emit(0xc0); // xor eax, eax
	emit(0xc3); // ret

	EXIT_NOT_IMPLEMENTED(i != thunk_size);
	std::memcpy(code, bytes, sizeof(bytes));
	Common::VirtualMemory::FlushInstructionCache(reinterpret_cast<uint64_t>(code), thunk_size);
	return reinterpret_cast<uint64_t>(code);
}

static uint64_t RegisterStubbedImport(uint32_t index, const Program* program,
                                      const RelocationInfo& ri) {
	const auto program_name = program != nullptr ? Common::PathToString(program->file_name) : "";

	for (auto& record: g_stubbed_imports) {
		if (record.patch_vaddr == ri.vaddr) {
			record.index   = index;
			record.name    = ri.name;
			record.type    = ri.type;
			record.bind    = ri.bind;
			record.program = program_name;
			return record.thunk_vaddr;
		}
	}

	StubbedImportRecord record {};
	record.index       = index;
	record.patch_vaddr = ri.vaddr;
	record.name        = ri.name;
	record.type        = ri.type;
	record.bind        = ri.bind;
	record.program     = program_name;
	g_stubbed_imports.push_back(record);
	const auto record_id                     = g_stubbed_imports.size() - 1;
	const auto thunk                         = AllocateUnresolvedImportThunk(record_id);
	g_stubbed_imports[record_id].thunk_vaddr = thunk;
	return thunk;
}

static KYTY_SYSV_ABI uint64_t ResolveImportStubWithId(uint64_t record_id) {
	if (record_id < g_stubbed_imports.size()) {
		auto& record = g_stubbed_imports[record_id];
		auto  nid    = record.name;
		auto  pos    = Common::FindIndex(nid, "[");
		if (Common::IndexValid(nid, pos)) {
			nid = Common::Left(nid, pos);
		}

		SymbolRecord resolved {};
		if (!nid.empty() &&
		    Common::Singleton<RuntimeLinker>::Instance()->ResolveLoadedSymbolByNid(nid, record.type,
		                                                                           &resolved) &&
		    resolved.vaddr != 0 && resolved.vaddr != record.thunk_vaddr) {
			LOGF("Late-resolved import: %s -> %s [0x%016" PRIx64 "]\n", record.name.c_str(),
			     resolved.name.c_str(), resolved.vaddr);

			if (record.patch_vaddr != 0) {
				PatchGuestMemory64(record.patch_vaddr, resolved.vaddr);
			}

			return resolved.vaddr;
		}
	}

	const auto log_index = g_unresolved_stub_call_log_count.fetch_add(1);
	if (log_index < 1024) {
		if (record_id < g_stubbed_imports.size()) {
			const auto& record = g_stubbed_imports[record_id];
			printf("Unresolved import stub called: %s\n", record.name.c_str());
			LOGF("Unresolved import stub called [%u]: patch_vaddr=0x%016" PRIx64
			     " jmprela_index=%" PRIu32 " symbol=%s type=%s bind=%s program=%s\n",
			     log_index, record.patch_vaddr, record.index, record.name.c_str(),
			     Common::EnumName(record.type).c_str(), Common::EnumName(record.bind).c_str(),
			     record.program.c_str());
		} else {
			printf("Unresolved import stub called: <bad-record>\n");
			LOGF("Unresolved import stub called [%u]: record_id=%" PRIu64 " symbol=<bad-record>\n",
			     log_index, record_id);
		}
	}
	return 0;
}

constexpr uint64_t SYSTEM_RESERVED  = 0x800000000u;
constexpr uint64_t CODE_BASE_INCR   = 0x010000000u;
constexpr uint64_t INVALID_OFFSET   = 0x040000000u;
constexpr uint64_t CODE_BASE_OFFSET = 0x100000000u;
constexpr uint64_t INVALID_MEMORY   = SYSTEM_RESERVED + INVALID_OFFSET;

static uint64_t g_desired_base_addr = SYSTEM_RESERVED + CODE_BASE_OFFSET;
static uint64_t g_invalid_memory    = 0;

static Program*              g_tls_main_program        = nullptr;
static thread_local Program* g_tls_cached_main_program = nullptr;
static thread_local uint8_t* g_tls_cached_main_tcb     = nullptr;

// Guest function-entry tracer. The title has no symbols and no frame pointers, so the only way to
// watch its control flow is to trap on chosen entry points. SIGTRAP is not wired up here but SIGILL
// already is, so the trap is a one-byte invalid opcode rather than int3: 0x06 (PUSH ES) does not
// decode in 64-bit mode. One byte matters, because it lands exactly on the `push rbp` that opens
// every one of these functions, and that instruction is trivial to emulate on the way out - which
// avoids needing single-step support to resume.
constexpr uint8_t GUEST_TRAP_OPCODE = 0x06;

struct GuestBreakpoint {
	uint64_t addr     = 0;
	uint8_t  original = 0;
};

static GuestBreakpoint g_guest_breakpoints[16];
static uint32_t        g_guest_breakpoint_count = 0;

static void InstallGuestBreakpoints() {
	const char* spec = std::getenv("KYTY_BP");
	if (spec == nullptr || spec[0] == '\0') {
		return;
	}

	for (const char* p = spec; *p != '\0' && g_guest_breakpoint_count < 16;) {
		char*      end  = nullptr;
		const auto addr = std::strtoull(p, &end, 16);
		if (end == p) {
			break;
		}
		p = (*end == ',' ? end + 1 : end);

		auto* code = reinterpret_cast<uint8_t*>(addr);
		if (addr == 0) {
			continue;
		}
		if (!Common::VirtualMemory::Protect(addr & ~static_cast<uint64_t>(0xfff), 0x1000,
		                                    Common::VirtualMemory::Mode::ExecuteReadWrite)) {
			printf("guest-bp: cannot make 0x%016llx writable\n", static_cast<unsigned long long>(addr));
			continue;
		}
		// Two entry forms are supported, because both are trivial to perform on the way out and
		// between them they cover ordinary functions and the jump-table dispatchers worth watching:
		//   55                  push rbp
		//   8b 87 <disp32>      mov eax, [rdi+disp32]   (a state-machine dispatcher reading its
		//                                                state field - the value is logged)
		const bool push_rbp = (code[0] == 0x55);
		const bool load_eax = (code[0] == 0x8b && code[1] == 0x87);
		const bool cmp_rdi  = (code[0] == 0x83 && code[1] == 0xbf);
		const bool cmp_rdib = (code[0] == 0x80 && code[1] == 0xbf);
		const bool call_ind = (code[0] == 0xff && code[1] == 0x90);
		if (!push_rbp && !load_eax && !cmp_rdi && !cmp_rdib && !call_ind) {
			printf("guest-bp: 0x%016llx has an unsupported entry form (found 0x%02x), skipped\n",
			       static_cast<unsigned long long>(addr), static_cast<unsigned>(code[0]));
			continue;
		}

		g_guest_breakpoints[g_guest_breakpoint_count++] = {addr, code[0]};
		*code                                           = GUEST_TRAP_OPCODE;
		printf("guest-bp: armed 0x%016llx\n", static_cast<unsigned long long>(addr));
	}
	fflush(stdout);
}

static KYTY_SYSV_ABI void RunEntry(uint64_t addr, EntryParams* params, atexit_func_t atexit_func,
                                   void* stack_top) {
	InstallGuestBreakpoints();
#if defined(__x86_64__) || defined(_M_X64)
	auto* func = reinterpret_cast<entry_func_t>(addr);

	if (stack_top != nullptr) {
		const auto guest_rsp =
		    reinterpret_cast<uintptr_t>(stack_top) & ~static_cast<uintptr_t>(0x0f);
		const auto guest_rbp = guest_rsp - 4u * sizeof(uint64_t);

		auto* guest_root_frame = reinterpret_cast<uintptr_t*>(guest_rbp);
		guest_root_frame[0]    = 0;
		guest_root_frame[1]    = 0;

#if defined(__APPLE__)
		// Clang on macOS can allocate plain "r" inputs to r12/r13, which the template
		// clobbers before consuming them. Pin the inputs to registers the SysV guest
		// preserves without changing register allocation on Windows or Linux.
		register entry_func_t func_reg asm("rbx")      = func;
		register uintptr_t    guest_rsp_reg asm("r14") = guest_rsp;
		register uintptr_t    guest_rbp_reg asm("r15") = guest_rbp;
#endif

#if defined(__APPLE__)
		asm volatile(
		    "pushq %%r12\n\t"
		    "pushq %%r13\n\t"
		    "movq %%rsp, %%r12\n\t"
		    "movq %%rbp, %%r13\n\t"
		    "movq %[guest_rsp], %%rsp\n\t"
		    "movq %[guest_rbp], %%rbp\n\t"
		    "callq *%[func]\n\t"
		    "movq %%r13, %%rbp\n\t"
		    "movq %%r12, %%rsp\n\t"
		    "popq %%r13\n\t"
		    "popq %%r12\n\t"
		    :
		    : [func] "r"(func_reg), "D"(params),
		      "S"(atexit_func), [guest_rsp] "r"(guest_rsp_reg), [guest_rbp] "r"(guest_rbp_reg)
		    : "cc", "memory", "rax", "rcx", "rdx", "r8", "r9", "r10", "r11", "xmm0", "xmm1", "xmm2",
		      "xmm3", "xmm4", "xmm5", "xmm6", "xmm7", "xmm8", "xmm9", "xmm10", "xmm11", "xmm12",
		      "xmm13", "xmm14", "xmm15");
#elif KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
		// Windows stack probes use the TEB stack limits during the guest stack switch.
		// bounds, which describe the host stack and are invalid while RSP is in guest memory.
		register entry_func_t func_reg asm("rbx")     = func;
		register uintptr_t    guest_rsp_reg asm("r8") = guest_rsp;
		register uintptr_t    guest_rbp_reg asm("r9") = guest_rbp;
		asm volatile("pushq %%r12\n\t"
		             "pushq %%r13\n\t"
		             "pushq %%r14\n\t"
		             "pushq %%r15\n\t"
		             "movq %%gs:0x08, %%r14\n\t"
		             "movq %%gs:0x10, %%r15\n\t"
		             "xorq %%rcx, %%rcx\n\t"
		             "movq %%rcx, %%gs:0x08\n\t"
		             "movq %%rcx, %%gs:0x10\n\t"
		             "movq %%rsp, %%r12\n\t"
		             "movq %%rbp, %%r13\n\t"
		             "movq %[guest_rsp], %%rsp\n\t"
		             "movq %[guest_rbp], %%rbp\n\t"
		             "callq *%[func]\n\t"
		             "movq %%r13, %%rbp\n\t"
		             "movq %%r12, %%rsp\n\t"
		             "movq %%r14, %%gs:0x08\n\t"
		             "movq %%r15, %%gs:0x10\n\t"
		             "popq %%r15\n\t"
		             "popq %%r14\n\t"
		             "popq %%r13\n\t"
		             "popq %%r12\n\t"
		             : [guest_rsp] "+r"(guest_rsp_reg), [guest_rbp] "+r"(guest_rbp_reg)
		             : [func] "r"(func_reg), "D"(params), "S"(atexit_func)
		             : "cc", "memory", "rax", "rcx", "rdx", "r10", "r11", "xmm0", "xmm1", "xmm2",
		               "xmm3", "xmm4", "xmm5", "xmm6", "xmm7", "xmm8", "xmm9", "xmm10", "xmm11",
		               "xmm12", "xmm13", "xmm14", "xmm15");
#else
		// Clobbers prevent inputs from being allocated to r12/r13.
		asm volatile("movq %%rsp, %%r12\n\t"
		             "movq %%rbp, %%r13\n\t"
		             "movq %[guest_rsp], %%rsp\n\t"
		             "movq %[guest_rbp], %%rbp\n\t"
		             "callq *%[func]\n\t"
		             "movq %%r13, %%rbp\n\t"
		             "movq %%r12, %%rsp\n\t"
		             :
		             : [func] "r"(func), "D"(params),
		               "S"(atexit_func), [guest_rsp] "r"(guest_rsp), [guest_rbp] "r"(guest_rbp)
		             : "cc", "memory", "rax", "rcx", "rdx", "r8", "r9", "r10", "r11", "r12", "r13",
		               "xmm0", "xmm1", "xmm2", "xmm3", "xmm4", "xmm5", "xmm6", "xmm7", "xmm8",
		               "xmm9", "xmm10", "xmm11", "xmm12", "xmm13", "xmm14", "xmm15");
#endif
		return;
	}

	uintptr_t guest_root_frame[2] = {};

#if defined(__APPLE__)
	register entry_func_t func_reg asm("rbx")      = func;
	register uintptr_t    guest_rbp_reg asm("r14") = reinterpret_cast<uintptr_t>(guest_root_frame);
#endif

#if defined(__APPLE__) || KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	asm volatile("pushq %%r12\n\t"
	             "pushq %%r13\n\t"
	             "movq %%rbp, %%r12\n\t"
	             "movq %[guest_rbp], %%rbp\n\t"
	             "callq *%[func]\n\t"
	             "movq %%r12, %%rbp\n\t"
	             "popq %%r13\n\t"
	             "popq %%r12\n\t"
	             :
#if defined(__APPLE__)
	             : [func] "r"(func_reg), "D"(params),
	               "S"(atexit_func), [guest_rbp] "r"(guest_rbp_reg)
#else
	             : [func] "r"(func), "D"(params),
	               "S"(atexit_func), [guest_rbp] "r"(guest_root_frame)
#endif
	             : "cc", "memory", "rax", "rcx", "rdx", "r8", "r9", "r10", "r11", "xmm0", "xmm1",
	               "xmm2", "xmm3", "xmm4", "xmm5", "xmm6", "xmm7", "xmm8", "xmm9", "xmm10", "xmm11",
	               "xmm12", "xmm13", "xmm14", "xmm15");
#else
	// Keep inputs out of r12.
	asm volatile("movq %%rbp, %%r12\n\t"
	             "movq %[guest_rbp], %%rbp\n\t"
	             "callq *%[func]\n\t"
	             "movq %%r12, %%rbp\n\t"
	             :
	             : [func] "r"(func), "D"(params),
	               "S"(atexit_func), [guest_rbp] "r"(guest_root_frame)
	             : "cc", "memory", "rax", "rcx", "rdx", "r8", "r9", "r10", "r11", "r12", "xmm0",
	               "xmm1", "xmm2", "xmm3", "xmm4", "xmm5", "xmm6", "xmm7", "xmm8", "xmm9", "xmm10",
	               "xmm11", "xmm12", "xmm13", "xmm14", "xmm15");
#endif
#else
	(void)stack_top;
	reinterpret_cast<entry_func_t>(addr)(params, atexit_func);
#endif
}

#if defined(KYTY_VIRTUAL_MEMORY_ALLOCATION_TESTS)
struct MainEntryStackTestState {
	bool      called = false;
	uintptr_t rsp    = 0;
#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	uintptr_t teb_stack_base  = UINTPTR_MAX;
	uintptr_t teb_stack_limit = UINTPTR_MAX;
#endif
};

static KYTY_SYSV_ABI void TestMainEntryStackCallback(EntryParams* params,
                                                     atexit_func_t /*atexit_func*/) {
	auto* state = reinterpret_cast<MainEntryStackTestState*>(const_cast<char*>(params->argv[0]));
	asm volatile("movq %%rsp, %0" : "=r"(state->rsp) : : "memory");
#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	asm volatile("movq %%gs:0x08, %0\n\t"
	             "movq %%gs:0x10, %1\n\t"
	             : "=r"(state->teb_stack_base), "=r"(state->teb_stack_limit)
	             :
	             : "memory");
#endif
	state->called = true;
}

bool TestMainEntryUsesGuestStack() {
	constexpr uint64_t stack_size = 0x10000;
	const auto         stack_base = Libs::LibKernel::Memory::AllocateRuntimeMemory(
	    0, stack_size, Common::VirtualMemory::Mode::ReadWrite, "main_entry_stack_test");
	if (stack_base == 0) {
		return false;
	}

	MainEntryStackTestState state {};
	EntryParams             params {};
	params.argv[0] = reinterpret_cast<const char*>(&state);

#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	uintptr_t original_teb_stack_base  = 0;
	uintptr_t original_teb_stack_limit = 0;
	asm volatile("movq %%gs:0x08, %0\n\t"
	             "movq %%gs:0x10, %1\n\t"
	             : "=r"(original_teb_stack_base), "=r"(original_teb_stack_limit)
	             :
	             : "memory");
#endif

	RunEntry(reinterpret_cast<uint64_t>(TestMainEntryStackCallback), &params, nullptr,
	         reinterpret_cast<void*>(stack_base + stack_size));

#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	uintptr_t restored_teb_stack_base  = 0;
	uintptr_t restored_teb_stack_limit = 0;
	asm volatile("movq %%gs:0x08, %0\n\t"
	             "movq %%gs:0x10, %1\n\t"
	             : "=r"(restored_teb_stack_base), "=r"(restored_teb_stack_limit)
	             :
	             : "memory");
	const bool teb_ok = state.teb_stack_base == 0 && state.teb_stack_limit == 0 &&
	                    restored_teb_stack_base == original_teb_stack_base &&
	                    restored_teb_stack_limit == original_teb_stack_limit;
#else
	constexpr bool teb_ok = true;
#endif

	const bool rsp_ok = state.rsp >= stack_base && state.rsp < stack_base + stack_size;
	const bool freed  = Libs::LibKernel::Memory::FreeGuestMemory(stack_base, stack_size);
	return state.called && rsp_ok && teb_ok && freed;
}

bool TestModuleRelocationUsesWritableHostMapping() {
	constexpr uint64_t page_size = 0x4000;
	constexpr uint64_t value     = 0x4b59545950415443;
	const auto         base      = Libs::LibKernel::Memory::AllocateProgramMemory(
	    0, page_size, Common::VirtualMemory::Mode::ReadWrite, "host_only_patch_test");
	if (base == 0) {
		return false;
	}
	Libs::LibKernel::Memory::SetProgramMemoryProtection(base, page_size,
	                                                    Common::VirtualMemory::Mode::Read);

	Libs::LibKernel::Memory::VirtualQueryInfo before {};
	Libs::LibKernel::Memory::VirtualQueryInfo after {};
	const bool                                before_ok =
	    Libs::LibKernel::Memory::KernelVirtualQuery(reinterpret_cast<const void*>(base), 0, &before,
	                                                sizeof(before)) == 0;
	const bool changed  = PatchGuestMemory64(base, value);
	const bool after_ok = Libs::LibKernel::Memory::KernelVirtualQuery(
	                          reinterpret_cast<const void*>(base), 0, &after, sizeof(after)) == 0;
	const bool value_ok = *reinterpret_cast<const uint64_t*>(base) == value;
#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	MEMORY_BASIC_INFORMATION mbi {};
	const bool               host_mode_ok =
	    VirtualQuery(reinterpret_cast<const void*>(base), &mbi, sizeof(mbi)) != 0 &&
	    mbi.Protect == PAGE_READWRITE;
#else
	constexpr bool host_mode_ok = true;
#endif
	const bool freed = Libs::LibKernel::Memory::FreeGuestMemory(base, page_size);

	return before_ok && after_ok && changed && value_ok && host_mode_ok && freed &&
	       before.protection == after.protection;
}
#endif

static uint64_t GetAlignedSize(const Elf64_Phdr* p) {
	return (p->p_align != 0 ? (p->p_memsz + (p->p_align - 1)) & ~(p->p_align - 1) : p->p_memsz);
}

static Common::VirtualMemory::Mode GetMode(Elf64_Word flags);

static const Elf64_Phdr* FindRelroLoadSegment(const Elf64_Ehdr* ehdr, const Elf64_Phdr* phdr,
                                              const Elf64_Phdr& relro) {
	if (ehdr == nullptr || phdr == nullptr) {
		return nullptr;
	}

	for (Elf64_Half i = 0; i < ehdr->e_phnum; i++) {
		if (phdr[i].p_type == PT_LOAD &&
		    RuntimeLinkerBounds::IsRelroRangeInMappedLoadSegment(
		        relro.p_vaddr, relro.p_memsz, phdr[i].p_vaddr, GetAlignedSize(phdr + i))) {
			return phdr + i;
		}
	}

	return nullptr;
}

static bool AreRelroRangesValid(const Elf64_Ehdr* ehdr, const Elf64_Phdr* phdr) {
	if (ehdr == nullptr || phdr == nullptr) {
		return false;
	}

	for (Elf64_Half i = 0; i < ehdr->e_phnum; i++) {
		if (phdr[i].p_type == PT_GNU_RELRO && phdr[i].p_memsz != 0 &&
		    FindRelroLoadSegment(ehdr, phdr, phdr[i]) == nullptr) {
			return false;
		}
	}

	return true;
}

static bool SetRelroProtection(Program* program, bool read_only) {
	if (program == nullptr || program->elf == nullptr || program->base_vaddr == 0) {
		return false;
	}

	const auto* ehdr = program->elf->GetEhdr();
	const auto* phdr = program->elf->GetPhdr();
	if (ehdr == nullptr || phdr == nullptr) {
		return false;
	}

	for (Elf64_Half i = 0; i < ehdr->e_phnum; i++) {
		if (phdr[i].p_type != PT_GNU_RELRO || phdr[i].p_memsz == 0) {
			continue;
		}

		const auto* load = FindRelroLoadSegment(ehdr, phdr, phdr[i]);
		if (load == nullptr) {
			return false;
		}

		uint64_t relro_vaddr = 0;
		if (!RuntimeLinkerBounds::DecodeRelocatedRange(
		        program->base_vaddr, phdr[i].p_vaddr, phdr[i].p_memsz, &relro_vaddr)) {
			return false;
		}

		const auto mode = read_only ? Common::VirtualMemory::Mode::Read : GetMode(load->p_flags);
		if (!Libs::LibKernel::Memory::ProtectGuestMemory(relro_vaddr, phdr[i].p_memsz, mode)) {
			return false;
		}
	}

	return true;
}

static void DbgDumpSymbols(const std::string& folder, Elf64_Sym* symbols, uint64_t size,
                           const char* names) {
	auto folder_str = Common::FixDirectorySlash(folder);

	Common::File::CreateDirectories(folder_str);

	Common::File f;
	f.Create(folder_str + "symbols.txt");

	for (auto* sym = symbols;
	     reinterpret_cast<uint8_t*>(sym) < reinterpret_cast<uint8_t*>(symbols) + size; sym++) {
		f.Printf("----\n");
		f.Printf("st_name = %" PRIu32 ", %s\n", sym->st_name, names + sym->st_name);
		f.Printf("st_info = 0x%02" PRIx8 "\n", sym->st_info);
		f.Printf("st_other = 0x%02" PRIx8 "\n", sym->st_other);
		f.Printf("st_shndx = 0x%04" PRIx16 "\n", sym->st_shndx);
		f.Printf("st_value = 0x%016" PRIx64 "\n", sym->st_value);
		f.Printf("st_size = %" PRIu64 "\n", sym->st_size);
	}

	f.Close();
}

static void DbgDumpRela(const std::string& folder, Elf64_Rela* records, uint64_t size,
                        const char* /*names*/, const char* file_name) {
	auto folder_str = Common::FixDirectorySlash(folder);

	Common::File::CreateDirectories(folder_str);

	Common::File f;
	f.Create(folder_str + file_name);

	for (auto* r = records;
	     reinterpret_cast<uint8_t*>(r) < reinterpret_cast<uint8_t*>(records) + size; r++) {
		f.Printf("----\n"
		         "r_offset = 0x%016" PRIx64 "\n"
		         "r_info = 0x%016" PRIx64 "\n"
		         "r_addend = %" PRId64 "\n",
		         r->r_offset, r->r_info, r->r_addend);
	}

	f.Close();
}

static Common::VirtualMemory::Mode GetMode(Elf64_Word flags) {
	switch (flags) {
		case PF_R: return Common::VirtualMemory::Mode::Read;
		case PF_W: return Common::VirtualMemory::Mode::Write;
		case PF_R | PF_W: return Common::VirtualMemory::Mode::ReadWrite;
		case PF_X: return Common::VirtualMemory::Mode::Execute;
		case PF_X | PF_R: return Common::VirtualMemory::Mode::ExecuteRead;
		case PF_X | PF_W: return Common::VirtualMemory::Mode::ExecuteWrite;
		case PF_X | PF_W | PF_R: return Common::VirtualMemory::Mode::ExecuteReadWrite;

		default: return Common::VirtualMemory::Mode::NoAccess;
	}
}

struct FrameS {
	FrameS*   next;
	uintptr_t ret_addr;
};

static void KYTY_SYSV_ABI StackwalkX86(uint64_t rbp, void** stack, int* depth, uintptr_t stack_addr,
                                       size_t stack_size, uintptr_t code_addr, size_t code_size) {
	auto* frame = reinterpret_cast<FrameS*>(rbp);

	int d = *depth;
	int i = 0;

	for (; i < d; i++) {
		if (!(reinterpret_cast<uintptr_t>(frame) >= stack_addr &&
		      reinterpret_cast<uintptr_t>(frame) < stack_addr + stack_size)) {
			break;
		}

		if (!(frame->ret_addr >= code_addr && frame->ret_addr < code_addr + code_size)) {
			break;
		}

		stack[i] = reinterpret_cast<void*>(frame->ret_addr);

		frame = frame->next;
	}

	*depth = i;
}

static void KYTY_SYSV_ABI SysStackWalkX86(uint64_t rbp, uint64_t rsp, void** stack, int* depth) {
	if (rsp == 0 || rbp < rsp) {
		*depth = 0;
		return;
	}

	StackwalkX86(rbp, stack, depth, rsp, 1024u * 1024u, SYSTEM_RESERVED + CODE_BASE_OFFSET,
	             g_desired_base_addr - (SYSTEM_RESERVED + CODE_BASE_OFFSET));
}

void KYTY_SYSV_ABI SysStackWalkX86(uint64_t rbp, void** stack, int* depth) {
	SysStackWalkX86(rbp, rbp, stack, depth);
}

// Probe diagnostic ranges without raising another fault.
static bool IsReadableRange(uint64_t addr, uint64_t size) {
	if (addr == 0 || size == 0) {
		return false;
	}

	const uint64_t end = addr + size;
	if (end < addr) {
		return false;
	}

#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	uint64_t current = addr;
	while (current < end) {
		MEMORY_BASIC_INFORMATION mbi {};
		if (VirtualQuery(reinterpret_cast<const void*>(current), &mbi, sizeof(mbi)) == 0 ||
		    mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0) {
			return false;
		}
		const auto region_end = reinterpret_cast<uint64_t>(mbi.BaseAddress) + mbi.RegionSize;
		if (region_end <= current) {
			return false;
		}
		current = std::min(region_end, end);
	}
#elif defined(__APPLE__)
	// Walk the Mach regions covering the range and require read permission. The fatal
	// report dumps memory behind raw register values, and a fault inside the reporter
	// re-enters the signal handler and wedges the reporting thread.
	uint64_t current = addr;
	while (current < end) {
		mach_vm_address_t              region_addr = current;
		mach_vm_size_t                 region_size = 0;
		vm_region_basic_info_data_64_t info {};
		mach_msg_type_number_t         count       = VM_REGION_BASIC_INFO_COUNT_64;
		mach_port_t                    object_name = MACH_PORT_NULL;
		if (mach_vm_region(mach_task_self(), &region_addr, &region_size, VM_REGION_BASIC_INFO_64,
		                   reinterpret_cast<vm_region_info_t>(&info), &count,
		                   &object_name) != KERN_SUCCESS ||
		    region_addr > current || (info.protection & VM_PROT_READ) == 0) {
			return false;
		}
		current = region_addr + region_size;
	}
#elif KYTY_PLATFORM == KYTY_PLATFORM_LINUX
	const auto page_size = static_cast<uint64_t>(sysconf(_SC_PAGESIZE));
	if (page_size == 0) {
		return false;
	}

	for (uint64_t current = addr; current < end;) {
		uint8_t probe = 0;

		iovec local {&probe, sizeof(probe)};
		iovec remote {reinterpret_cast<void*>(current), sizeof(probe)};

		if (process_vm_readv(getpid(), &local, 1, &remote, 1, 0) !=
		    static_cast<ssize_t>(sizeof(probe))) {
			return false;
		}

		const uint64_t next = (current & ~(page_size - 1)) + page_size;
		if (next <= current) { // wrapped at the top of the address space
			break;
		}
		current = next;
	}
#else
	(void)end;
#endif
	return true;
}

static bool IsDumpableRange(uint64_t addr, uint64_t size) {
#if KYTY_PLATFORM == KYTY_PLATFORM_LINUX
	return IsReadableRange(addr, size);
#else
	(void)size;
	return addr != 0;
#endif
}

static bool KytyExceptionHandler(const Common::HostException::ExceptionInfo& exception_info) {
	const auto* info = &exception_info;

	if (info->type == Common::HostException::ExceptionType::IllegalInstruction) {
		for (uint32_t i = 0; i < g_guest_breakpoint_count; i++) {
			if (info->exception_address != g_guest_breakpoints[i].addr) {
				continue;
			}
			// The trap sits on the entry `push rbp`, so rsp still points at the return address the
			// call pushed. That is the only cheap way to identify the caller in a binary with no
			// frame pointers.
			uint64_t ret_addr = 0;
			if (info->rsp != 0) {
				::memcpy(&ret_addr, reinterpret_cast<const void*>(info->rsp), sizeof(ret_addr));
			}
			// KYTY_BP_PEEK=<hex offset> also reports the dword at rdi+offset. Handlers of this
			// title's state machines take the machine in rdi and keep the state id in a field far
			// beyond what a register dump shows, so reading it at entry is the only way to see the
			// state sequence.
			// KYTY_BP_PEEK accepts a comma-separated list of hex offsets, so one run can show every
			// field a predicate reads instead of needing one run per field.
			struct PeekList {
				int64_t  off[8] = {};
				uint32_t count  = 0;
			};
			static const PeekList peeks = [] {
				PeekList    list;
				const char* v = std::getenv("KYTY_BP_PEEK");
				for (const char* p = v; p != nullptr && *p != '\0' && list.count < 8;) {
					char*      end = nullptr;
					const auto off = std::strtoll(p, &end, 16);
					if (end == p) {
						break;
					}
					list.off[list.count++] = off;
					p                      = (*end == ',' ? end + 1 : end);
				}
				return list;
			}();
			const bool has_peek =
			    peeks.count > 0 && info->rdi > 0x10000ull && info->rdi < 0x10000000000ull;
			printf("guest-bp hit 0x%016" PRIx64 " ret=%016" PRIx64 " rdi=%016" PRIx64
			       " rsi=%016" PRIx64 " rdx=%016" PRIx64 " rcx=%016" PRIx64 " rbx=%016" PRIx64,
			       info->exception_address, ret_addr, info->rdi, info->rsi, info->rdx, info->rcx,
			       info->rbx);
			if (has_peek) {
				for (uint32_t p = 0; p < peeks.count; p++) {
					uint32_t value = 0;
					::memcpy(&value, reinterpret_cast<const void*>(info->rdi + peeks.off[p]),
					         sizeof(value));
					printf(" [+0x%llx]=%u", static_cast<unsigned long long>(peeks.off[p]), value);
				}
			}
			printf("\n");
			fflush(stdout);
#if defined(__APPLE__) && defined(__x86_64__)
			// Resume by performing whatever the trap byte displaced, then stepping over it.
			auto* uc = static_cast<ucontext_t*>(info->native_context);
			auto& ss = uc->uc_mcontext->__ss;
			if (g_guest_breakpoints[i].original == 0xff) {
				// call qword ptr [rax+disp32] (ff 90 <disp32>). Trapping the call site itself is the
				// only way to see an indirect target: perform the call by pushing the return address
				// and jumping to it, and report the resolved callee.
				int32_t disp = 0;
				::memcpy(&disp, reinterpret_cast<const void*>(info->exception_address + 2),
				         sizeof(disp));
				uint64_t target = 0;
				::memcpy(&target, reinterpret_cast<const void*>(info->rax + disp), sizeof(target));
				ss.__rsp -= sizeof(uint64_t);
				*reinterpret_cast<uint64_t*>(ss.__rsp) = info->exception_address + 6;
				ss.__rip                               = target;
				printf("guest-bp   indirect call [rax+0x%x] -> 0x%016llx\n", disp,
				       static_cast<unsigned long long>(target));
				fflush(stdout);
			} else if (g_guest_breakpoints[i].original == 0x83 ||
			    g_guest_breakpoints[i].original == 0x80) {
				const bool byte_form = (g_guest_breakpoints[i].original == 0x80);
				// cmp dword ptr [rdi+disp32], imm8 (83 bf <disp32> <imm8>). The displaced byte is
				// the 0x83, so disp32 sits at +2 and the imm8 at +6. Perform the compare and set
				// the flags the following conditional branch reads, then step over all 7 bytes.
				int32_t disp = 0;
				::memcpy(&disp, reinterpret_cast<const void*>(info->exception_address + 2),
				         sizeof(disp));
				const auto imm =
				    static_cast<int32_t>(*reinterpret_cast<const int8_t*>(
				        info->exception_address + 6));
				int32_t lhs = 0;
				if (byte_form) {
					uint8_t b = 0;
					::memcpy(&b, reinterpret_cast<const void*>(info->rdi + disp), sizeof(b));
					lhs = b;
				} else {
					::memcpy(&lhs, reinterpret_cast<const void*>(info->rdi + disp), sizeof(lhs));
				}
				const int64_t  wide   = static_cast<int64_t>(lhs) - static_cast<int64_t>(imm);
				const uint32_t result = static_cast<uint32_t>(wide);
				uint64_t       flags  = ss.__rflags & ~0x8d5ull; // CF PF AF ZF SF OF
				if (result == 0) {
					flags |= 0x40ull; // ZF
				}
				if ((result & 0x80000000u) != 0) {
					flags |= 0x80ull; // SF
				}
				if (static_cast<uint32_t>(lhs) < static_cast<uint32_t>(imm)) {
					flags |= 0x1ull; // CF
				}
				if (wide < INT32_MIN || wide > INT32_MAX) {
					flags |= 0x800ull; // OF
				}
				ss.__rflags = flags;
				ss.__rip    = info->exception_address + 7;
				printf("guest-bp   cmp%s [rdi+0x%x] = %d (vs %d)\n", byte_form ? "b" : "", disp,
				       lhs, imm);
				fflush(stdout);
			} else if (g_guest_breakpoints[i].original == 0x55) {
				ss.__rsp -= sizeof(uint64_t);
				*reinterpret_cast<uint64_t*>(ss.__rsp) = ss.__rbp;
				ss.__rip                               = info->exception_address + 1;
			} else {
				// mov eax, [rdi+disp32]: the displaced byte is the 0x8b, so the disp32 still sits at
				// +2. Perform the load and report it - for a dispatcher this is the state id.
				int32_t disp = 0;
				::memcpy(&disp, reinterpret_cast<const void*>(info->exception_address + 2),
				         sizeof(disp));
				uint32_t value = 0;
				::memcpy(&value, reinterpret_cast<const void*>(info->rdi + disp), sizeof(value));
				ss.__rax = value;
				ss.__rip = info->exception_address + 6;
				printf("guest-bp   state [rdi+0x%x] = %u\n", disp, value);
				fflush(stdout);
			}
			return true;
#endif
		}

		if (Loader::X64InstructionEmulator::TryEmulate(info->native_context)) {
			return true;
		}
	}

	if (info->type == Common::HostException::ExceptionType::AccessViolation) {
		using CoreAccess  = Common::HostException::AccessViolationType;
		using GpuAccess   = Libs::Graphics::PageFaultAccess;
		const auto access = [&]() {
			switch (info->access_violation_type) {
				case CoreAccess::Read: return GpuAccess::Read;
				case CoreAccess::Write: return GpuAccess::Write;
				case CoreAccess::Execute: return GpuAccess::Execute;
				case CoreAccess::Unknown:
					EXIT("unknown access type for page fault at 0x%016" PRIx64 "\n",
					     info->access_violation_vaddr);
			}
			EXIT("invalid access type for page fault at 0x%016" PRIx64 "\n",
			     info->access_violation_vaddr);
		}();
		if (Libs::LibKernel::Memory::HandleGpuFault(access, info->access_violation_vaddr)) {
			return true;
		}

		if (Libs::LibKernel::Memory::KernelHandleReservedRangeAccessViolation(
		        info->access_violation_vaddr)) {
			return true;
		}
	}

	LOGF("kyty_exception_handler: %016" PRIx64 "\n", info->exception_address);
#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	HMODULE owner_module = nullptr;
	if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
	                           GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                       reinterpret_cast<LPCSTR>(info->exception_address), &owner_module) != 0 &&
	    owner_module != nullptr) {
		char module_name[MAX_PATH] = {};
		if (GetModuleFileNameA(owner_module, module_name, MAX_PATH) != 0) {
			LOGF("exception module: %s\n", module_name);
		}
	}
#else
	Dl_info module_info {};
	if (::dladdr(reinterpret_cast<void*>(info->exception_address), &module_info) != 0 &&
	    module_info.dli_fname != nullptr) {
		LOGF("exception module: %s\n", module_info.dli_fname);
	}
#endif
	if (info->exception_address != 0) {
#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
		MEMORY_BASIC_INFORMATION mem_info = {};
		auto* dump_ptr = reinterpret_cast<const uint8_t*>(info->exception_address - 32);
		if (VirtualQuery(dump_ptr, &mem_info, sizeof(mem_info)) != 0 &&
		    mem_info.State == MEM_COMMIT && mem_info.Protect != PAGE_NOACCESS &&
		    (mem_info.Protect & PAGE_GUARD) == 0) {
			const auto dump_start = reinterpret_cast<uint64_t>(dump_ptr);
			const auto region_end =
			    reinterpret_cast<uint64_t>(mem_info.BaseAddress) + mem_info.RegionSize;
			const auto dump_size =
			    (dump_start + 64 <= region_end ? 64u
			                                   : static_cast<uint32_t>(region_end - dump_start));
			LOGF("code-32:");
			for (uint32_t i = 0; i < dump_size; i++) {
				LOGF(" %02" PRIx32, static_cast<uint32_t>(dump_ptr[i]));
			}
			LOGF("\n");
		} else {
			LOGF("code-32: unavailable\n");
		}
#else
		const auto fault_addr = info->exception_address;
		const auto dump_start = (fault_addr >= 32 ? fault_addr - 32 : fault_addr);
		if (IsReadableRange(dump_start, 64)) {
			auto* dump_ptr = reinterpret_cast<const uint8_t*>(dump_start);
			LOGF("code-32:");
			for (uint32_t i = 0; i < 64; i++) {
				LOGF(" %02" PRIx32, static_cast<uint32_t>(dump_ptr[i]));
			}
			LOGF("\n");
		} else {
			LOGF("code-32: unavailable\n");
		}
#endif
	} else {
		LOGF("code: unavailable\n");
	}
	// The on-disk eboot is a SELF container: its inner ELF program headers do not describe where
	// segment bytes actually sit in the file, so disassembling eboot.bin at a faulting vaddr decodes
	// unrelated bytes and invents phantom misaligned instructions. Dump the mapped, relocated image
	// instead - that is the only byte stream that corresponds to the addresses reported here.
	// KYTY_DUMP_IMAGE=<path> writes it once per run.
	if (const char* dump_path = std::getenv("KYTY_DUMP_IMAGE");
	    dump_path != nullptr && dump_path[0] != '\0' && info->exception_address != 0) {
		static bool dumped = false;
		if (!dumped) {
			dumped = true;
			auto* p = Common::Singleton<Loader::RuntimeLinker>::Instance()->FindProgramByAddr(
			    info->exception_address);
			if (p != nullptr && p->base_vaddr != 0 && p->base_size != 0) {
				if (FILE* f = ::fopen(dump_path, "wb"); f != nullptr) {
					const auto written =
					    ::fwrite(reinterpret_cast<const void*>(p->base_vaddr), 1, p->base_size, f);
					::fclose(f);
					LOGF("image dump: %s base=%016" PRIx64 " size=%016" PRIx64 " written=%zu\n",
					     dump_path, p->base_vaddr, p->base_size, written);
				}
			}
		}
	}

	LOGF("exception: type=%s, av_type=%s, av_addr=%016" PRIx64 ", native_code=%08" PRIx32 "\n",
	     Common::EnumName(info->type).c_str(),
	     Common::EnumName(info->access_violation_type).c_str(), info->access_violation_vaddr,
	     info->native_code);
	LOGF("regs: rax=%016" PRIx64 " rbx=%016" PRIx64 " rcx=%016" PRIx64 " rdx=%016" PRIx64 "\n",
	     info->rax, info->rbx, info->rcx, info->rdx);
	LOGF("regs: rsi=%016" PRIx64 " rdi=%016" PRIx64 " rbp=%016" PRIx64 " rsp=%016" PRIx64 "\n",
	     info->rsi, info->rdi, info->rbp, info->rsp);
	LOGF("regs: r8 =%016" PRIx64 " r9 =%016" PRIx64 " r10=%016" PRIx64 " r11=%016" PRIx64 "\n",
	     info->r8, info->r9, info->r10, info->r11);
	LOGF("regs: r12=%016" PRIx64 " r13=%016" PRIx64 " r14=%016" PRIx64 " r15=%016" PRIx64 "\n",
	     info->r12, info->r13, info->r14, info->r15);

	if (IsReadableRange(info->rsp, 16u * sizeof(uint64_t))) {
		auto* stack = reinterpret_cast<const uint64_t*>(info->rsp);
		LOGF("stack:");
		for (uint64_t i = 0; i < 16; i++) {
			LOGF(" [%02" PRIu64 "]=%016" PRIx64, i, stack[i]);
		}
		LOGF("\n");
	} else {
		LOGF("stack: unavailable\n");
	}

	// Title code is built without frame pointers, so an rbp chain walk produces nonsense here. The
	// only way to recover a guest call path is to dump a wide slice of the stack and filter it
	// offline for words that are preceded by a real call instruction. Emitted as a single write
	// because the log is shared with every other guest thread and per-word writes interleave.
	// Written as raw bytes rather than formatted text: this runs on the faulting thread from a signal
	// handler, so anything that allocates can deadlock against a heap lock the interrupted code was
	// already holding. An earlier std::string version of this did exactly that.
#if KYTY_PLATFORM != KYTY_PLATFORM_WINDOWS
	if (const char* dump_path = std::getenv("KYTY_DUMP_IMAGE");
	    dump_path != nullptr && dump_path[0] != '\0' && info->rsp != 0) {
		static bool stack_dumped = false;
		if (!stack_dumped) {
			stack_dumped = true;
			char path[1024] {};
			::snprintf(path, sizeof(path), "%s.stack", dump_path);
			if (int fd = ::open(path, O_WRONLY | O_CREAT | O_TRUNC, 0644); fd >= 0) {
				const uint64_t header[2] = {info->rsp, info->exception_address};
				(void)::write(fd, header, sizeof(header));
				// Chunked, and stop at the first short write: the live stack usually has far less
				// than a page left above rsp, and one oversized write would fault and yield nothing.
				for (uint64_t off = 0; off < 8192; off += 128) {
					if (::write(fd, reinterpret_cast<const void*>(info->rsp + off), 128) != 128) {
						break;
					}
				}
				::close(fd);
			}

			// Also capture the objects the faulting code was walking. Registers alone only show six
			// qwords each, which is not enough to reach a member at +0x168. Each region is written
			// as {address, length, bytes} so it can be located offline, and pointers are chased one
			// level so the payload behind a list node is captured too.
			::snprintf(path, sizeof(path), "%s.regions", dump_path);
			if (int fd = ::open(path, O_WRONLY | O_CREAT | O_TRUNC, 0644); fd >= 0) {
				const uint64_t roots[] = {info->rax, info->rbx, info->rcx, info->rdx,
				                          info->rsi, info->rdi, info->r8,  info->r9,
				                          info->r10, info->r11, info->r12, info->r13,
				                          info->r14, info->r15};
				const auto     plausible = [](uint64_t a) {
                    return a > 0x10000ull && a < 0x10000000000ull && (a & 7u) == 0;
				};
				const auto dump_region = [&](uint64_t base) {
					constexpr uint64_t LEN    = 0x200;
					const uint64_t     hdr[2] = {base, LEN};
					(void)::write(fd, hdr, sizeof(hdr));
					for (uint64_t off = 0; off < LEN; off += 64) {
						if (::write(fd, reinterpret_cast<const void*>(base + off), 64) != 64) {
							break;
						}
					}
				};
				for (uint64_t root : roots) {
					if (!plausible(root)) {
						continue;
					}
					dump_region(root);
					// One level of indirection: list nodes hold the interesting object at +8.
					for (uint64_t slot = 0; slot <= 8; slot += 8) {
						uint64_t child = 0;
						::memcpy(&child, reinterpret_cast<const void*>(root + slot), sizeof(child));
						if (plausible(child)) {
							dump_region(child);
						}
					}
				}
				::close(fd);
			}
		}
	}
#endif

	auto dump_guest_code = [](const char* name, uint64_t addr) {
		auto* p = Common::Singleton<Loader::RuntimeLinker>::Instance()->FindProgramByAddr(addr);
		if (p == nullptr || addr < p->base_vaddr) {
			return;
		}

#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
		MEMORY_BASIC_INFORMATION mbi {};
		auto* dump_ptr = reinterpret_cast<const uint8_t*>(addr >= 16 ? addr - 16 : addr);
		if (VirtualQuery(dump_ptr, &mbi, sizeof(mbi)) == 0 || mbi.State != MEM_COMMIT ||
		    (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0) {
			return;
		}
		const auto dump_start = reinterpret_cast<uint64_t>(dump_ptr);
		const auto region_end = reinterpret_cast<uint64_t>(mbi.BaseAddress) + mbi.RegionSize;
		const auto dump_size =
		    (dump_start + 32 <= region_end ? 32u : static_cast<uint32_t>(region_end - dump_start));
#else
		auto*      dump_ptr  = reinterpret_cast<const uint8_t*>(addr >= 16 ? addr - 16 : addr);
		const auto dump_size = 32u;
		if (!IsReadableRange(reinterpret_cast<uint64_t>(dump_ptr), dump_size)) {
			return;
		}
#endif

		LOGF("%s code: addr=%016" PRIx64 ", off=%016" PRIx64 ", module=%s:", name, addr,
		     addr - p->base_vaddr,
		     Common::FilenameWithoutDirectory(Common::PathToGenericString(p->file_name)).c_str());
		for (uint32_t i = 0; i < dump_size; i++) {
			LOGF(" %02" PRIx32, static_cast<uint32_t>(dump_ptr[i]));
		}
		LOGF("\n");
	};

	dump_guest_code("guest rax[0]", info->rax);
	dump_guest_code("guest rbx[0]", info->rbx);
	dump_guest_code("guest rcx[0]", info->rcx);
	dump_guest_code("guest rsi[0]", info->rsi);
	if (IsDumpableRange(info->rsp, 16u * sizeof(uint64_t))) {
		auto* stack = reinterpret_cast<const uint64_t*>(info->rsp);
		for (uint64_t i = 0; i < 16; i++) {
			char name[32] {};
			std::snprintf(name, sizeof(name), "stack[%" PRIu64 "]", i);
			dump_guest_code(name, stack[i]);
		}
	}

	if (info->type == Common::HostException::ExceptionType::AccessViolation) {
		if (info->rbp != 0) {
			void* stack[20];
			int   depth = 20;
			SysStackWalkX86(info->rbp, info->rsp, stack, &depth);

			LOGF("Stack trace [thread = %d]:\n", Common::Thread::GetThreadIdUnique());
			for (int i = 0; i < depth; i++) {
				auto  vaddr = reinterpret_cast<uint64_t>(stack[i]);
				auto* p =
				    Common::Singleton<Loader::RuntimeLinker>::Instance()->FindProgramByAddr(vaddr);
				LOGF("[%d] %016" PRIx64 ", off=%016" PRIx64 ", %s\n", i, vaddr,
				     (p == nullptr ? 0 : vaddr - p->base_vaddr),
				     (p == nullptr ? "???"
				                   : Common::FilenameWithoutDirectory(
				                         Common::PathToGenericString(p->file_name))
				                         .c_str()));
			}
		}

		auto dump_guest_qwords = [](const char* name, uint64_t addr) {
			if (addr == 0) {
				LOGF("%s = 0\n", name);
				return;
			}

#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
			MEMORY_BASIC_INFORMATION mbi {};
			if (VirtualQuery(reinterpret_cast<const void*>(addr), &mbi, sizeof(mbi)) == 0 ||
			    mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0) {
				LOGF("%s = %016" PRIx64 " (unmapped)\n", name, addr);
				return;
			}
#endif

			if (!IsReadableRange(addr, 8u * sizeof(uint64_t))) {
				LOGF("%s = %016" PRIx64 " (unmapped)\n", name, addr);
				return;
			}

			auto* q = reinterpret_cast<const uint64_t*>(addr);
			LOGF("%s = %016" PRIx64 ": %016" PRIx64 " %016" PRIx64 " %016" PRIx64 " %016" PRIx64
			     " %016" PRIx64 " %016" PRIx64 " %016" PRIx64 " %016" PRIx64 "\n",
			     name, addr, q[0], q[1], q[2], q[3], q[4], q[5], q[6], q[7]);
		};

		dump_guest_qwords("guest rbx", info->rbx);
		dump_guest_qwords("guest rax", info->rax);
		dump_guest_qwords("guest rcx", info->rcx);
		dump_guest_qwords("guest rsi", info->rsi);
		dump_guest_qwords("guest rdi", info->rdi);
		dump_guest_qwords("guest r8 ", info->r8);
		dump_guest_qwords("guest r9 ", info->r9);
		dump_guest_qwords("guest r10", info->r10);
		dump_guest_qwords("guest r11", info->r11);
		dump_guest_qwords("guest r12", info->r12);
		dump_guest_qwords("guest r13", info->r13);
		dump_guest_qwords("guest r14", info->r14);
		dump_guest_qwords("guest r15", info->r15);

		if (info->exception_address == 0x000000090064364e &&
		    IsDumpableRange(info->rbx, sizeof(uint64_t))) {
			auto* local = reinterpret_cast<const uint64_t*>(info->rbx);
			dump_guest_qwords("vorbis obj", local[0]);
			dump_guest_qwords("vorbis len", info->rcx);
		}

		// A fault outside the guest address space is a host bug, and EXITing here swallows the
		// diagnosis: under Guard Malloc the guard-page hit never reaches libgmalloc, which would
		// otherwise name the offending allocation. Returning false restores the default
		// disposition so the fault re-raises and the allocator reports it.
		// KYTY_HOST_FAULT_FATAL=1 keeps the old immediate exit.
		static const bool host_fault_fatal = [] {
			const char* v = std::getenv("KYTY_HOST_FAULT_FATAL");
			return v != nullptr && v[0] != '\0' && v[0] != '0';
		}();
		// Guest allocations live far below this: eboot maps at 0x9_0000_0000 and guest buffers sit
		// around 0x3-0x5_0000_0000. The emulator image itself is at 0x7000_0000_0000, so anything
		// up there is host memory.
		const bool guest_address = info->access_violation_vaddr < 0x10000000000ull;

		// Walk the frame-pointer chain here: backtrace() cannot pass _sigtramp under Rosetta, and a
		// Rosetta crash report carries no thread stacks, so this is the only way to see the call
		// path. Symbolize offline against image base 0x700000000000 for host frames and 0x900000000
		// for guest ones. This runs for guest faults too: guest code invoked as an HLE callback runs
		// on a host thread, so the chain names the emulator code that called into the title.
		const auto dump_frames = [&](const char* what) {
			printf("=== %s frames (rbp chain), rip=0x%016" PRIx64 " ===\n", what,
			       info->exception_address);
			auto frame = info->rbp;
			for (int i = 0; i < 24 && frame > 0x1000; i++) {
				if (!IsDumpableRange(frame, 2 * sizeof(uint64_t))) {
					break;
				}
				const auto* slots = reinterpret_cast<const uint64_t*>(frame);
				const auto  next  = slots[0];
				const auto  ret   = slots[1];
				if (ret == 0) {
					break;
				}
				printf("  [%02d] 0x%016" PRIx64 "\n", i, ret);
				if (next <= frame) {
					break;
				}
				frame = next;
			}
			fflush(stdout);
		};

		if (!guest_address && !host_fault_fatal) {
			printf("Access violation on a host address [%016" PRIx64
			       "] - deferring to the allocator\n",
			       info->access_violation_vaddr);
			dump_frames("host-fault");
			return false;
		}

		dump_frames("guest-fault");
		EXIT("Access violation: %s [%016" PRIx64 "] %s\n",
		     Common::EnumName(info->access_violation_type).c_str(), info->access_violation_vaddr,
		     (info->access_violation_vaddr == g_invalid_memory ? "(Unpatched object)" : ""));
		return false;
	}

	EXIT("Unknown exception!!! (%08" PRIx32 ")", info->native_code);
	return false;
}

static void EncodeId64(uint16_t in_id, std::string* out_id) {
	static const char* str = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-";
	if (in_id < 0x40u) {
		*out_id += str[in_id];
	} else {
		if (in_id < 0x1000u) {
			*out_id += str[static_cast<uint16_t>(in_id >> 6u) & 0x3fu];
			*out_id += str[in_id & 0x3fu];
		} else {
			*out_id += str[static_cast<uint16_t>(in_id >> 12u) & 0x3fu];
			*out_id += str[static_cast<uint16_t>(in_id >> 6u) & 0x3fu];
			*out_id += str[in_id & 0x3fu];
		}
	}
}

template <class T>
static void GetDynDataOs(Elf64* elf, T* out, Elf64_Sxword tag) {
	if (const auto* dyn = elf->GetDynValue(tag); dyn != nullptr) {
		*out = elf->GetDynamicData<T>(dyn->d_un.d_ptr);
	}
}

template <class T>
static void GetDynData(Elf64* elf, uint64_t base_vaddr, T* out, Elf64_Sxword tag) {
	if (const auto* dyn = elf->GetDynValue(tag); dyn != nullptr) {
		*out = reinterpret_cast<T>(base_vaddr + dyn->d_un.d_ptr);
	}
}

template <class T>
static void GetDynValue(Elf64* elf, T* out, Elf64_Sxword tag) {
	if (const auto* dyn = elf->GetDynValue(tag); dyn != nullptr) {
		*out = dyn->d_un.d_val;
	}
}

template <class T>
static void GetDynValues(Elf64* elf, T* out, Elf64_Sxword tag) {
	for (const auto* dyn: elf->GetDynList(tag)) {
		out->push_back(dyn->d_un.d_val);
	}
}

template <class T>
static void GetDynPtr(Elf64* elf, T* out, Elf64_Sxword tag) {
	if (const auto* dyn = elf->GetDynValue(tag); dyn != nullptr) {
		*out = dyn->d_un.d_ptr;
	}
}

static bool IsRelaTableInFileBackedSegment(const Elf64* elf, Elf64_Sxword table_tag,
	                                       uint64_t table_size, uint64_t entry_size,
	                                       bool os_dynamic_data) {
	EXIT_IF(elf == nullptr);

	const auto* table = elf->GetDynValue(table_tag);
	const auto* ehdr  = elf->GetEhdr();
	const auto* phdr  = elf->GetPhdr();
	if (table == nullptr || ehdr == nullptr || phdr == nullptr) {
		return false;
	}

	const Elf64_Word storage_type = os_dynamic_data ? PT_OS_DYNLIBDATA : PT_LOAD;
	for (Elf64_Half i = 0; i < ehdr->e_phnum; i++) {
		if (phdr[i].p_type != storage_type) {
			continue;
		}

		const uint64_t storage_vaddr = os_dynamic_data ? 0 : phdr[i].p_vaddr;
		if (RuntimeLinkerBounds::IsRelaTableRangeValid(
		        table->d_un.d_ptr, table_size, entry_size, storage_vaddr, phdr[i].p_filesz)) {
			return true;
		}
	}

	return false;
}

static bool AreRelocationTargetsInMappedSegments(const Elf64* elf, const Elf64_Rela* records,
                                                 uint64_t table_size) {
	if (table_size == 0) {
		return true;
	}

	const auto* ehdr = elf != nullptr ? elf->GetEhdr() : nullptr;
	const auto* phdr = elf != nullptr ? elf->GetPhdr() : nullptr;
	if (records == nullptr || ehdr == nullptr || phdr == nullptr ||
	    table_size % sizeof(Elf64_Rela) != 0) {
		return false;
	}

	const uint64_t record_count = table_size / sizeof(Elf64_Rela);
	for (uint64_t record_index = 0; record_index < record_count; record_index++) {
		bool target_is_mapped = false;
		for (Elf64_Half segment_index = 0; segment_index < ehdr->e_phnum; segment_index++) {
			if ((phdr[segment_index].p_type == PT_LOAD ||
			     phdr[segment_index].p_type == PT_OS_RELRO) &&
			    RuntimeLinkerBounds::IsRelocationTargetRangeValid(
			        records[record_index].r_offset, phdr[segment_index].p_vaddr,
			        phdr[segment_index].p_memsz)) {
				target_is_mapped = true;
				break;
			}
		}

		if (!target_is_mapped) {
			return false;
		}
	}

	return true;
}

static bool AreRelocationRecordsSupported(const Elf64_Rela* records, uint64_t table_size) {
	if (table_size == 0) {
		return true;
	}
	if (records == nullptr || table_size % sizeof(Elf64_Rela) != 0) {
		return false;
	}

	const uint64_t record_count = table_size / sizeof(Elf64_Rela);
	for (uint64_t record_index = 0; record_index < record_count; record_index++) {
		if (!RuntimeLinkerBounds::IsSupportedRelocationRecord(
		        records[record_index].GetType(), records[record_index].GetSymbol())) {
			return false;
		}
	}
	return true;
}

static bool AreRelocationSymbolsSupported(const Elf64_Rela* records, uint64_t table_size,
	                                       const Elf64_Sym* symbols,
	                                       uint64_t symbol_table_size,
	                                       uint64_t symbol_entry_size,
	                                       bool symbol_table_size_known,
	                                       const char* string_table,
	                                       uint64_t string_table_size,
	                                       bool string_table_size_known) {
	if (table_size == 0 || !symbol_table_size_known) {
		return true;
	}
	if (records == nullptr || table_size % sizeof(Elf64_Rela) != 0) {
		return false;
	}

	const uint64_t record_count = table_size / sizeof(Elf64_Rela);
	for (uint64_t record_index = 0; record_index < record_count; record_index++) {
		const uint32_t relocation_type = records[record_index].GetType();
		if (relocation_type != R_X86_64_64 && relocation_type != R_X86_64_GLOB_DAT &&
		    relocation_type != R_X86_64_JUMP_SLOT) {
			continue;
		}

		const uint32_t symbol_index = records[record_index].GetSymbol();
		if (symbols == nullptr ||
		    !RuntimeLinkerBounds::IsSymbolIndexValid(symbol_index, symbol_table_size,
		                                              symbol_entry_size)) {
			return false;
		}

		const auto& symbol = symbols[symbol_index];
		if (!RuntimeLinkerBounds::IsSupportedRelocationSymbolEntry(
		        relocation_type, symbol.GetBind(), symbol.GetType())) {
			return false;
		}
		if (!RuntimeLinkerBounds::IsRelocationSymbolNameValid(
		        symbol.GetBind(), string_table, string_table_size, string_table_size_known,
		        symbol.st_name)) {
			return false;
		}
	}
	return true;
}

static bool IsPltGotResolverInMappedSegment(const Elf64* elf, uint64_t pltgot_vaddr) {
	const auto* ehdr = elf != nullptr ? elf->GetEhdr() : nullptr;
	const auto* phdr = elf != nullptr ? elf->GetPhdr() : nullptr;
	if (ehdr == nullptr || phdr == nullptr) {
		return false;
	}

	for (Elf64_Half segment_index = 0; segment_index < ehdr->e_phnum; segment_index++) {
		if ((phdr[segment_index].p_type == PT_LOAD ||
		     phdr[segment_index].p_type == PT_OS_RELRO) &&
		    RuntimeLinkerBounds::IsPltGotResolverRangeValid(
		        pltgot_vaddr, phdr[segment_index].p_vaddr, phdr[segment_index].p_memsz)) {
			return true;
		}
	}

	return false;
}

static bool IsTableInFileBackedSegment(const Elf64* elf, Elf64_Sxword table_tag,
	                                   uint64_t table_size, bool os_dynamic_data) {
	EXIT_IF(elf == nullptr);

	const auto* table = elf->GetDynValue(table_tag);
	const auto* ehdr  = elf->GetEhdr();
	const auto* phdr  = elf->GetPhdr();
	if (table == nullptr || ehdr == nullptr || phdr == nullptr) {
		return false;
	}

	const Elf64_Word storage_type = os_dynamic_data ? PT_OS_DYNLIBDATA : PT_LOAD;
	for (Elf64_Half i = 0; i < ehdr->e_phnum; i++) {
		if (phdr[i].p_type != storage_type) {
			continue;
		}

		const uint64_t storage_vaddr = os_dynamic_data ? 0 : phdr[i].p_vaddr;
		if (RuntimeLinkerBounds::IsTableRangeValid(table->d_un.d_ptr, table_size, storage_vaddr,
		                                               phdr[i].p_filesz)) {
			return true;
		}
	}

	return false;
}

static bool IsLifecycleArrayInFileBackedSegment(const Elf64* elf, Elf64_Sxword table_tag,
                                                uint64_t table_size) {
	EXIT_IF(elf == nullptr);

	if (table_size == 0) {
		return true;
	}

	const auto* table = elf->GetDynValue(table_tag);
	const auto* ehdr  = elf->GetEhdr();
	const auto* phdr  = elf->GetPhdr();
	if (table == nullptr || ehdr == nullptr || phdr == nullptr) {
		return false;
	}

	for (Elf64_Half i = 0; i < ehdr->e_phnum; i++) {
		if (phdr[i].p_type == PT_LOAD &&
		    RuntimeLinkerBounds::IsLifecycleArrayRangeValid(
		        table->d_un.d_ptr, table_size, phdr[i].p_vaddr, phdr[i].p_filesz)) {
			return true;
		}
	}

	return false;
}

static bool IsFileBackedExecutableAddress(const Elf64* elf, uint64_t entry_vaddr) {
	EXIT_IF(elf == nullptr);

	const auto* ehdr = elf->GetEhdr();
	const auto* phdr = elf->GetPhdr();
	if (ehdr == nullptr || phdr == nullptr) {
		return false;
	}

	for (Elf64_Half i = 0; i < ehdr->e_phnum; i++) {
		if (phdr[i].p_type == PT_LOAD &&
		    RuntimeLinkerBounds::IsExecutableEntryInSegment(
		        entry_vaddr, phdr[i].p_vaddr, phdr[i].p_filesz,
		        (phdr[i].p_flags & PF_X) != 0)) {
			return true;
		}
	}

	return false;
}

static bool AreRegularDynamicFunctionDefinitionsValid(const Elf64* elf,
                                                      const Elf64_Sym* symbol_table,
                                                      uint64_t symbol_table_size) {
	if (elf == nullptr || symbol_table == nullptr ||
	    symbol_table_size % sizeof(Elf64_Sym) != 0) {
		return false;
	}

	const uint64_t symbol_count = symbol_table_size / sizeof(Elf64_Sym);
	for (uint64_t symbol_index = 0; symbol_index < symbol_count; symbol_index++) {
		const auto& symbol = symbol_table[symbol_index];
		if (RuntimeLinkerBounds::IsRegularDynamicFunctionDefinition(symbol.st_shndx,
		                                                            symbol.GetType()) &&
		    !IsFileBackedExecutableAddress(elf, symbol.st_value)) {
			return false;
		}
	}

	return true;
}

static bool AreRegularDynamicObjectDefinitionsValid(const Elf64* elf,
                                                    const Elf64_Sym* symbol_table,
                                                    uint64_t symbol_table_size) {
	const auto* ehdr = elf != nullptr ? elf->GetEhdr() : nullptr;
	const auto* phdr = elf != nullptr ? elf->GetPhdr() : nullptr;
	if (symbol_table == nullptr || ehdr == nullptr || phdr == nullptr ||
	    symbol_table_size % sizeof(Elf64_Sym) != 0) {
		return false;
	}

	const uint64_t symbol_count = symbol_table_size / sizeof(Elf64_Sym);
	for (uint64_t symbol_index = 0; symbol_index < symbol_count; symbol_index++) {
		const auto& symbol = symbol_table[symbol_index];
		if (!RuntimeLinkerBounds::IsRegularDynamicObjectDefinition(
		        symbol.st_shndx, symbol.GetType(), symbol.st_size)) {
			continue;
		}

		bool object_is_mapped = false;
		for (Elf64_Half segment_index = 0; segment_index < ehdr->e_phnum; segment_index++) {
			if (phdr[segment_index].p_type == PT_LOAD &&
			    RuntimeLinkerBounds::IsDynamicObjectRangeInSegment(
			        symbol.st_value, symbol.st_size, phdr[segment_index].p_vaddr,
			        phdr[segment_index].p_memsz)) {
				object_is_mapped = true;
				break;
			}
		}
		if (!object_is_mapped) {
			return false;
		}
	}

	return true;
}

static bool DecodeSysvHashMetadata(const Program* program, uint64_t declared_size,
                                   bool declared_size_known,
                                   RuntimeLinkerBounds::SysvHashLayout* out) {
	EXIT_IF(program == nullptr);
	EXIT_IF(program->elf == nullptr);

	const auto* elf   = program->elf.get();
	const auto* table = elf->GetDynValue(DT_HASH);
	const auto* ehdr  = elf->GetEhdr();
	const auto* phdr  = elf->GetPhdr();
	if (table == nullptr || ehdr == nullptr || phdr == nullptr || out == nullptr) {
		return false;
	}
	if (!RuntimeLinkerBounds::IsSysvHashAddressAligned(table->d_un.d_ptr)) {
		return false;
	}

	bool header_is_file_backed = false;
	for (Elf64_Half i = 0; i < ehdr->e_phnum; i++) {
		if (phdr[i].p_type == PT_LOAD &&
		    RuntimeLinkerBounds::IsTableRangeValid(
		        table->d_un.d_ptr, RuntimeLinkerBounds::SYSV_HASH_HEADER_SIZE,
		        phdr[i].p_vaddr, phdr[i].p_filesz)) {
			header_is_file_backed = true;
			break;
		}
	}
	if (!header_is_file_backed ||
	    table->d_un.d_ptr > std::numeric_limits<uint64_t>::max() - program->base_vaddr) {
		return false;
	}

	uint32_t header[2] {};
	std::memcpy(header,
	            reinterpret_cast<const void*>(program->base_vaddr + table->d_un.d_ptr),
	            sizeof(header));
	if (!RuntimeLinkerBounds::DecodeSysvHashLayout(
	        header[0], header[1], declared_size, declared_size_known, out)) {
		return false;
	}

	for (Elf64_Half i = 0; i < ehdr->e_phnum; i++) {
		if (phdr[i].p_type == PT_LOAD &&
		    RuntimeLinkerBounds::IsTableRangeValid(
		        table->d_un.d_ptr, out->table_size, phdr[i].p_vaddr, phdr[i].p_filesz)) {
			return true;
		}
	}

	return false;
}

static void KYTY_SYSV_ABI ProgramExitHandler() {
	Common::Singleton<RuntimeLinker>::Instance()->StopAllModules();

	LOGF("exit!!!\n");
}

static const char* GetDynamicString(const DynamicInfo& info, uint64_t string_offset) {
	return RuntimeLinkerBounds::GetDynamicString(
	    info.str_table, info.str_table_size, info.str_table_size_known, string_offset);
}

template <class T>
static void GetDynModules(Elf64* elf, T* out, const DynamicInfo& info, Elf64_Sxword tag) {
	std::vector<uint64_t> needed_modules;
	GetDynValues(elf, &needed_modules, tag);
	for (auto need: needed_modules) {
		ModuleId id {};
		// id.id            = static_cast<int>((need >> 48u) & 0xffffu);
		EncodeId64(static_cast<uint16_t>((need >> 48u) & 0xffffu), &id.id);
		id.version_major = static_cast<int>((need >> 40u) & 0xffu);
		id.version_minor = static_cast<int>((need >> 32u) & 0xffu);
		const char* name = GetDynamicString(
		    info, RuntimeLinkerBounds::GetPlatformStringOffset(need));
		EXIT_IF(name == nullptr);
		id.name = name;
		out->push_back(id);
	}
}

template <class T>
static void GetDynLibs(Elf64* elf, T* out, const DynamicInfo& info, Elf64_Sxword tag) {
	std::vector<uint64_t> needed_modules;
	GetDynValues(elf, &needed_modules, tag);
	for (auto need: needed_modules) {
		LibraryId id {};
		// id.id      = static_cast<int>((need >> 48u) & 0xffffu);
		EncodeId64(static_cast<uint16_t>((need >> 48u) & 0xffffu), &id.id);
		id.version = static_cast<int>((need >> 32u) & 0xffffu);
		const char* name = GetDynamicString(
		    info, RuntimeLinkerBounds::GetPlatformStringOffset(need));
		EXIT_IF(name == nullptr);
		id.name = name;
		out->push_back(id);
	}
}

static RelocationInfo GetRelocationInfo(Elf64_Rela* r, Program* program) {
	KYTY_PROFILER_FUNCTION();

	// KYTY_PROFILER_BLOCK("1");

	RelocationInfo ret;
	// SymbolRecord   sr {};

	// KYTY_PROFILER_END_BLOCK;

	// KYTY_PROFILER_BLOCK("2");

	auto         type    = r->GetType();
	auto         symbol  = r->GetSymbol();
	EXIT_IF(!RuntimeLinkerBounds::IsRelativeRelocationSymbolValid(type, symbol));
	Elf64_Sxword addend  = r->r_addend;
	auto*        symbols = program->dynamic_info->symbol_table;
	ret.base_vaddr       = program->base_vaddr;
	ret.vaddr            = ret.base_vaddr + r->r_offset;
	ret.bind_self        = false;

	// KYTY_PROFILER_END_BLOCK;

	// KYTY_PROFILER_BLOCK("3");

	switch (type) {
		case R_X86_64_GLOB_DAT:
		case R_X86_64_JUMP_SLOT: addend = 0; [[fallthrough]];
		case R_X86_64_64: {
			if (program->dynamic_info->symbol_table_size_known) {
				EXIT_IF(!RuntimeLinkerBounds::IsSymbolIndexValid(
				    symbol, program->dynamic_info->symbol_table_total_size,
				    program->dynamic_info->symbol_table_entry_size));
			}
			auto         sym          = symbols[symbol];
			auto         bind         = sym.GetBind();
			auto         sym_type     = sym.GetType();
			uint64_t     symbol_vaddr = 0;
			SymbolRecord sr {};
			switch (sym_type) {
				case STT_NOTYPE: ret.type = SymbolType::NoType; break;
				case STT_FUNC: ret.type = SymbolType::Func; break;
				case STT_OBJECT: ret.type = SymbolType::Object; break;
				default: EXIT("unknown symbol type: %d\n", (int)sym_type);
			}
			switch (bind) {
				case STB_LOCAL:
					EXIT_IF(!RuntimeLinkerBounds::DecodeDynamicSymbolAddress(
					    sym.st_shndx, sym.st_value, ret.base_vaddr, &symbol_vaddr));
					ret.bind     = BindType::Local;
					break;
				case STB_GLOBAL: ret.bind = BindType::Global; [[fallthrough]];
				case STB_WEAK: {
					ret.bind = (ret.bind == BindType::Unknown ? BindType::Weak : ret.bind);
					const char* symbol_name = GetDynamicString(*program->dynamic_info, sym.st_name);
					EXIT_IF(symbol_name == nullptr);
					ret.name = symbol_name;
					program->rt->Resolve(ret.name, ret.type, program, &sr, &ret.bind_self);
					symbol_vaddr = sr.vaddr;
				} break;
				default: EXIT("unknown bind: %d\n", (int)bind);
			}
			ret.resolved = RuntimeLinkerBounds::IsDynamicSymbolResolutionComplete(
			    sym.st_shndx, bind, symbol_vaddr);
			if (ret.resolved) {
				EXIT_IF(!RuntimeLinkerBounds::DecodeSymbolRelocationValue(
				    symbol_vaddr, addend, &ret.value));
			} else {
				ret.value = 0;
			}
			ret.name     = sr.name;
			ret.dbg_name = sr.dbg_name;
		} break;
		case R_X86_64_RELATIVE:
			EXIT_IF(!RuntimeLinkerBounds::DecodeRelativeRelocationValue(
			    ret.base_vaddr, addend, &ret.value));
			ret.resolved = true;
			break;
		case R_X86_64_DTPMOD64:
			ret.value    = reinterpret_cast<uint64_t>(program);
			ret.resolved = true;
			ret.type     = SymbolType::TlsModule;
			ret.bind     = BindType::Local;
			ret.dbg_name = Common::PathToString(program->file_name);
			break;
		default: EXIT("unknown type: %d\n", (int)type);
	}

	// KYTY_PROFILER_END_BLOCK;

	return ret;
}

static void RelocateRecord(uint32_t index, Elf64_Rela* r, Program* program, bool jmprela_table,
                           bool imports_only, std::vector<std::string>* unresolved) {
	KYTY_PROFILER_FUNCTION();

	auto ri = GetRelocationInfo(r, program);

	if (imports_only &&
	    (ri.bind_self || (ri.bind != BindType::Global && ri.bind != BindType::Weak))) {
		return;
	}

	[[maybe_unused]] bool patched        = false;
	bool                  stubbed_import = false;
	bool                  stubbed_func   = false;

	// KYTY_PROFILER_BLOCK("patch");

	if (ri.resolved) {
		patched = PatchGuestMemory64(ri.vaddr, ri.value);
	} else {
		uint64_t value = 0;
		bool     weak  = (ri.bind == BindType::Weak || !program->fail_if_global_not_resolved);
		if (ri.type == SymbolType::Object && weak) {
			value = g_invalid_memory;
		} else if (ri.type == SymbolType::Func && jmprela_table && weak) {
			value          = RegisterStubbedImport(index, program, ri);
			stubbed_import = true;
			stubbed_func   = true;
		} else if (ri.type == SymbolType::Func && !jmprela_table && weak) {
			value        = RegisterStubbedImport(index, program, ri);
			stubbed_func = true;
		} else if (ri.type == SymbolType::NoType && weak) {
			value = RuntimeLinker::ReadFromElf(program, ri.vaddr) + ri.base_vaddr;
		}

		if (value != 0) {
			patched = PatchGuestMemory64(ri.vaddr, value);
		} else {
			auto dbg_str = fmt::format("[{:016x}] <- {:016x}, {}, {}, {}, {}", ri.vaddr, ri.value,
			                           ri.name.c_str(), Common::EnumName(ri.type).c_str(),
			                           Common::EnumName(ri.bind).c_str(), ri.dbg_name.c_str());

			if (unresolved != nullptr) {
				unresolved->push_back(dbg_str);
			} else {
				EXIT("Can't resolve: %s\n", dbg_str.c_str());
			}

			if (ri.type == SymbolType::Object) {
				value = g_invalid_memory;
			} else if (ri.type == SymbolType::Func || ri.type == SymbolType::NoType) {
				value        = RegisterStubbedImport(index, program, ri);
				stubbed_func = true;
				if (jmprela_table) {
					stubbed_import = true;
				}
			}

			if (value != 0) {
				patched = PatchGuestMemory64(ri.vaddr, value);
			}
		}
	}

	// KYTY_PROFILER_END_BLOCK;

	// Only unresolved imports are logged below, but naming a guest function requires knowing which
	// HLE calls it makes, which needs the resolved ones too. KYTY_DUMP_IMPORTS=<path> writes the
	// full slot-address -> symbol table so a disassembly can be annotated offline.
	if (patched) {
		if (const char* imports_path = std::getenv("KYTY_DUMP_IMPORTS");
		    imports_path != nullptr && imports_path[0] != '\0') {
			static FILE* imports_file = ::fopen(imports_path, "w");
			if (imports_file != nullptr) {
				::fprintf(imports_file, "%016" PRIx64 " %s\n", ri.vaddr, ri.name.c_str());
				::fflush(imports_file);
			}
		}
	}

	if (patched && stubbed_import) {
		const auto thunk = RegisterStubbedImport(index, program, ri);
		LOGF("Relocate: unresolved PLT import patched to stub [%u] [%016" PRIx64 "] <- %016" PRIx64
		     ", %s, %s, %s, %s\n",
		     index, ri.vaddr, thunk, ri.name.c_str(), Common::EnumName(ri.type).c_str(),
		     Common::EnumName(ri.bind).c_str(), Common::PathToString(program->file_name).c_str());
	} else if (patched && stubbed_func) {
		const auto thunk = RegisterStubbedImport(index, program, ri);
		LOGF("Relocate: unresolved non-PLT function patched to stub [%u] [%016" PRIx64
		     "] <- %016" PRIx64 ", %s, %s, %s, %s\n",
		     index, ri.vaddr, thunk, ri.name.c_str(), Common::EnumName(ri.type).c_str(),
		     Common::EnumName(ri.bind).c_str(), Common::PathToString(program->file_name).c_str());
	}

	if (program->dbg_print_reloc) {
		if (/* !dbg_str.ContainsStr("libc_") && */ patched && !ri.bind_self &&
		    (ri.bind == BindType::Global || ri.bind == BindType::Weak ||
		     ri.type == SymbolType::TlsModule)) {
			auto dbg_str = fmt::format("[{:016x}] <- {:016x}, {}, {}, {}, {}", ri.vaddr, ri.value,
			                           ri.name.c_str(), Common::EnumName(ri.type).c_str(),
			                           Common::EnumName(ri.bind).c_str(), ri.dbg_name.c_str());

			LOGF("Relocate: %s\n", dbg_str.c_str());
		}
	}
}

static void RelocateRecords(Elf64_Rela* records, uint64_t size, Program* program,
                            bool jmprela_table, bool imports_only,
                            std::vector<std::string>* unresolved) {
	KYTY_PROFILER_FUNCTION();

	uint32_t index = 0;
	for (auto* r = records;
	     reinterpret_cast<uint8_t*>(r) < reinterpret_cast<uint8_t*>(records) + size; r++, index++) {
		RelocateRecord(index, r, program, jmprela_table, imports_only, unresolved);
	}
}

#if defined(__aarch64__) || defined(__arm64__)
// Native arm64 validation does not execute guest x86-64 PLT return stubs.  Keep
// the symbol linkable while retaining the x86 epilogue used by guest relocation
// handlers on the supported x86-64 host path.
static KYTY_SYSV_ABI void RelocateHandlerReturnStub() {}
#else
__attribute__((naked)) static KYTY_SYSV_ABI void RelocateHandlerReturnStub() {
	asm volatile("addq $8, %rsp\n\t"
	             "retq\n");
}
#endif

static KYTY_SYSV_ABI uint64_t RelocateHandler(RelocateHandlerStack s) {
	auto*       stack     = s.stack;
	auto*       program   = reinterpret_cast<Program*>(stack[-1]);
	auto        rel_index = stack[0];
	std::string name      = "<unknown function>";

	if (program != nullptr && program->dynamic_info != nullptr &&
	    program->dynamic_info->jmprela_table != nullptr &&
	    RuntimeLinkerBounds::IsRelaIndexValid(
	        rel_index, program->dynamic_info->jmprela_table_size)) {
		auto ri = GetRelocationInfo(program->dynamic_info->jmprela_table + rel_index, program);

		name = ri.name.c_str();
	}

	// Restore return address (for stack trace)
	stack[-1] = reinterpret_cast<uint64_t>(RelocateHandlerReturnStub);

	LOGF("=== Stubbed function, returning OK ===\n[%d]\t%s\n", Common::Thread::GetThreadIdUnique(),
	     name.c_str());
	return 0;
}

static KYTY_MS_ABI uint8_t* TlsMainGetAddr() {
	EXIT_IF(g_tls_main_program == nullptr);

	if (g_tls_cached_main_program == g_tls_main_program && g_tls_cached_main_tcb != nullptr) {
		return g_tls_cached_main_tcb;
	}

	g_tls_cached_main_program = g_tls_main_program;
	g_tls_cached_main_tcb =
	    RuntimeLinker::TlsGetAddr(g_tls_main_program) + g_tls_main_program->tls.tcb_offset;
	return g_tls_cached_main_tcb;
}

static void PatchProgram(Program* program, uint64_t address, uint64_t size) {
	EXIT_IF(program == nullptr);
	EXIT_IF(program->elf == nullptr);

	if (size >= 12) {
		// Replace guest stack-canary/errno stores through fs:[0x28] with nops.
		// Windows x64 cannot host guest FS directly, and an unpatched shared-library access faults
		// at address 0x28.
		const uint8_t fs_store_pattern[8] = {0x64, 0xc7, 0x04, 0x25, 0x28, 0x00, 0x00, 0x00};
		auto*         start_ptr           = reinterpret_cast<uint8_t*>(address);
		auto*         end_ptr             = start_ptr + size - 12;

		for (auto* ptr = start_ptr; ptr <= end_ptr; ptr++) {
			if (memcmp(ptr, fs_store_pattern, sizeof(fs_store_pattern)) == 0) {
				LOGF("Patch fs:[0x28] store at addr: [%016" PRIx64 "]\n",
				     reinterpret_cast<uint64_t>(ptr));
				if (ptr + 16 < start_ptr + size && ptr[12] == 0xcd && ptr[13] == 0x45 &&
				    ptr[14] == 0x90 && ptr[15] == 0x0f && ptr[16] == 0x0b) {
					ptr[0] = 0x5d; // pop rbp
					ptr[1] = 0xc3; // ret
					std::memset(ptr + 2, 0x90, 15);
				} else {
					std::memset(ptr, 0x90, 12);
				}
			}
		}
	}

	if (!program->elf->IsShared() && program->tls.handler_vaddr != 0) {
		// Replace:
		//   66 66 66
		//   mov <reg>, qword ptr fs:[0x00]
		// with:
		//   call <handler>
		//   mov <reg>,rax
		//   nop ...
		const uint8_t tls_pattern[5] = {0x64, 0x48, 0x8B, 0x00, 0x25};

		EXIT_IF(Jit::Call9::GetSize() != 9);

		auto* start_ptr = reinterpret_cast<uint8_t*>(address);
		auto* end_ptr   = start_ptr + size - Jit::Call9::GetSize();

		for (auto* ptr = start_ptr; ptr <= end_ptr; ptr++) {
			auto*  inst_ptr     = ptr;
			size_t prefix_count = 0;
			while (prefix_count < 3 && inst_ptr < start_ptr + size && *inst_ptr == 0x66) {
				inst_ptr++;
				prefix_count++;
			}

			const size_t inst_size = prefix_count + Jit::Call9::GetSize();
			if (inst_ptr + Jit::Call9::GetSize() > start_ptr + size) {
				break;
			}

			const uint8_t modrm = inst_ptr[3];
			if (memcmp(inst_ptr, tls_pattern, 3) == 0 && (modrm & 0xc7u) == 0x04u &&
			    inst_ptr[4] == tls_pattern[4] &&
			    *reinterpret_cast<const uint32_t*>(inst_ptr + 5) == 0) {
				LOGF("Patch tls at addr: [%016" PRIx64 "]\n", reinterpret_cast<uint64_t>(ptr));

				const auto reg = (modrm >> 3u) & 7u;
				EXIT_NOT_IMPLEMENTED(reg == 4u);

				auto* code = new (ptr) Jit::Call9;
				code->SetFunc(reg == 0
				                  ? program->tls.handler_vaddr
				                  : program->tls.handler_vaddr + Jit::TlsRegStub::GetOffset(reg));
				if (inst_size > Jit::Call9::GetSize()) {
					std::memset(ptr + Jit::Call9::GetSize(), 0x90,
					            inst_size - Jit::Call9::GetSize());
				}
				ptr += inst_size - 1;
			}
		}
	}
}

uint64_t RuntimeLinker::GetEntry() {
	// EXIT_NOT_IMPLEMENTED(!Common::Thread::IsMainThread());

	Common::LockGuard lock(m_mutex);

	for (const auto* p: m_programs) {
		if (p->elf != nullptr && !p->elf->IsShared()) {
			return p->elf->GetEntry() + p->base_vaddr;
		}
	}
	return 0;
}

uint64_t RuntimeLinker::GetProcParam() {
	// EXIT_NOT_IMPLEMENTED(!Common::Thread::IsMainThread());

	Common::LockGuard lock(m_mutex);

	for (const auto* p: m_programs) {
		if (p->elf != nullptr && !p->elf->IsShared()) {
			return p->proc_param_vaddr;
		}
	}
	return 0;
}

void RuntimeLinker::DbgDump(const std::string& folder) {
	KYTY_PROFILER_FUNCTION();

	EXIT_NOT_IMPLEMENTED(!Common::Thread::IsMainThread());

	Common::LockGuard lock(m_mutex);

	for (const auto* p: m_programs) {
		auto folder_str = Common::FixDirectorySlash(folder);
		folder_str += Common::FilenameWithoutDirectory(Common::PathToGenericString(p->file_name));

		EXIT_IF(p->elf == nullptr);

		p->elf->DbgDump(folder_str);

		if (p->dynamic_info != nullptr) {
			EXIT_NOT_IMPLEMENTED(p->dynamic_info->symbol_table_entry_size != 0 &&
			                     p->dynamic_info->symbol_table_entry_size != sizeof(Elf64_Sym));
			EXIT_NOT_IMPLEMENTED(p->dynamic_info->rela_table_entry_size != 0 &&
			                     p->dynamic_info->rela_table_entry_size != sizeof(Elf64_Rela));
			// EXIT_NOT_IMPLEMENTED(p->dynamic_info->jmprela_table == nullptr);
			// EXIT_NOT_IMPLEMENTED(p->dynamic_info->rela_table == nullptr);
			// EXIT_NOT_IMPLEMENTED(p->dynamic_info->symbol_table == nullptr);

			if (p->dynamic_info->symbol_table != nullptr) {
				DbgDumpSymbols(folder_str, p->dynamic_info->symbol_table,
				               p->dynamic_info->symbol_table_total_size,
				               p->dynamic_info->str_table);
			}
			if (p->dynamic_info->jmprela_table != nullptr) {
				DbgDumpRela(folder_str, p->dynamic_info->jmprela_table,
				            p->dynamic_info->jmprela_table_size, p->dynamic_info->str_table,
				            "jmprela_table.txt");
			}
			if (p->dynamic_info->rela_table != nullptr) {
				DbgDumpRela(folder_str, p->dynamic_info->rela_table,
				            p->dynamic_info->rela_table_total_size, p->dynamic_info->str_table,
				            "rela_table.txt");
			}
		}

		if (p->export_symbols != nullptr) {
			p->export_symbols->DbgDump(folder_str, "export_symbols.txt");
		}
		if (p->import_symbols != nullptr) {
			p->import_symbols->DbgDump(folder_str, "import_symbols.txt");
		}
	}
}

void RuntimeLinker::RelocateAll() {
	// EXIT_NOT_IMPLEMENTED(!Common::Thread::IsMainThread());

	Common::LockGuard lock(m_mutex);

	for (auto* p: m_programs) {
		Relocate(p);
	}

	m_relocated = true;
}

void RuntimeLinker::RelocateProgram(Program* program) {
	Common::LockGuard lock(m_mutex);

	EXIT_IF(program == nullptr);
	EXIT_IF(std::find(m_programs.begin(), m_programs.end(), program) == m_programs.end());

	Relocate(program);
}

bool RuntimeLinker::AddProgramLoadReference(Program* program) {
	Common::LockGuard lock(m_mutex);

	if (program == nullptr ||
	    std::find(m_programs.begin(), m_programs.end(), program) == m_programs.end()) {
		return false;
	}

	return RuntimeLinkerLifecycle::TryAddReference(&program->load_references);
}

RuntimeLinkerLifecycle::ModuleReleaseAction RuntimeLinker::PrepareProgramUnload(Program* program) {
	Common::LockGuard lock(m_mutex);

	if (program == nullptr ||
	    std::find(m_programs.begin(), m_programs.end(), program) == m_programs.end()) {
		return RuntimeLinkerLifecycle::ModuleReleaseAction::Untracked;
	}

	return RuntimeLinkerLifecycle::PrepareRelease(&program->load_references);
}

void RuntimeLinker::UnloadProgram(Program* program) {
	// EXIT_NOT_IMPLEMENTED(!Common::Thread::IsMainThread());

	Common::LockGuard lock(m_mutex);

	if (auto it = std::find(m_programs.begin(), m_programs.end(), program);
	    it != m_programs.end()) {
		DeleteProgram(*it);
		m_programs.erase(it);
	} else {
		EXIT("program not found");
	}

	if (m_relocated) {
		RelocateAll();
	}
}

RuntimeLinker::RuntimeLinker(): m_symbols(std::make_unique<SymbolDatabase>()) {
	EXIT_NOT_IMPLEMENTED(!Common::Thread::IsMainThread());
}

RuntimeLinker::~RuntimeLinker() {
	Clear();
}

Program* RuntimeLinker::LoadProgram(const std::filesystem::path& elf_name) {
	KYTY_PROFILER_FUNCTION();

	Common::LockGuard lock(m_mutex);

	static int32_t id_seq = 0;

	LOGF("Loading: %s\n", Common::PathToString(elf_name).c_str());

	auto  program_owner = std::make_unique<Program>();
	auto* program       = program_owner.get();

	program->rt        = this;
	program->file_name = elf_name;
	program->unique_id = ++id_seq;

	program->elf = std::make_unique<Elf64>();
	program->elf->Open(elf_name);

	if (program->elf->IsValid()) {
		LoadProgramToMemory(program);
		ParseProgramDynamicInfo(program);
		CreateSymbolDatabase(program);
	} else {
		EXIT("elf is not valid: %s\n", Common::PathToString(elf_name).c_str());
	}

	m_programs.push_back(program_owner.release());

	if (!program->elf->IsShared()) {
		program->fail_if_global_not_resolved = false;
		Libs::LibKernel::SetProgName(elf_name.filename().string());
	}

	if (Common::EndsWith(Common::ToLower(Common::DirectoryWithoutFilename(
	                         Common::PathToGenericString(elf_name))),
	                     "_module/")) {
		program->fail_if_global_not_resolved = false;
	}

	return program;
}

void RuntimeLinker::SaveMainProgram(const std::filesystem::path& elf_name) {
	EXIT_NOT_IMPLEMENTED(!Common::Thread::IsMainThread());

	Common::LockGuard lock(m_mutex);

	for (const auto* p: m_programs) {
		EXIT_IF(p->elf == nullptr);

		if (!p->elf->IsShared()) {
			p->elf->Save(elf_name);
			break;
		}
	}
}

void RuntimeLinker::SaveProgram(Program* program, const std::filesystem::path& elf_name) {
	EXIT_NOT_IMPLEMENTED(!Common::Thread::IsMainThread());

	Common::LockGuard lock(m_mutex);

	if (auto it = std::find(m_programs.begin(), m_programs.end(), program);
	    it != m_programs.end()) {
		EXIT_IF((*it)->elf == nullptr);

		(*it)->elf->Save(elf_name);
	} else {
		EXIT("program not found");
	}
}

void RuntimeLinker::Execute(const std::filesystem::path& game_patch) {
	KYTY_PROFILER_THREAD("Thread_Main");

	Libs::LibKernel::PthreadInitSelfForMainThread();
	auto* main_stack_top = Libs::LibKernel::PthreadCreateMainGuestStack();

#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	// Guest code has no Windows stack probes and may jump over the guard page. Module
	// initializers execute on the host stack too, so grow it before calling any guest code.
	size_t expanded_size = 0;
	while (expanded_size < static_cast<size_t>(768) * 1024) {
		sys_dbg_stack_info_t stack {};
		SysStackUsage(stack);
		*reinterpret_cast<uint32_t*>(stack.guard_addr) = 0;
		expanded_size += stack.guard_size;
	}
#endif

	PreloadAdjacentPrograms();
	RelocateAll();

	if (!game_patch.empty()) {
		GamePatch::Apply(game_patch, m_programs.empty() ? nullptr : m_programs.front());
	}
	StartAllModules();

	LOGF_COLOR(Log::Color::BrightYellow, "---\n--- Execute: %s\n---\n", "Main");

	if (auto entry = GetEntry(); entry != 0) {
		auto* params = reinterpret_cast<EntryParams*>(
		    (reinterpret_cast<uintptr_t>(main_stack_top) - 0x100u) & ~static_cast<uintptr_t>(0x0f));
		std::memset(params, 0, sizeof(EntryParams));
		params->argc    = 1;
		params->argv[0] = "Prosperismo";

		LOGF("stack_addr = %" PRIx64 "\n", reinterpret_cast<uint64_t>(params));

		RunEntry(entry, params, ProgramExitHandler,
		         reinterpret_cast<void*>(reinterpret_cast<uintptr_t>(params) - 0x1000u));
	}
}

void RuntimeLinker::Clear() {
	// EXIT_NOT_IMPLEMENTED(!Common::Thread::IsMainThread());

	Common::LockGuard lock(m_mutex);

	for (auto* p: m_programs) {
		DeleteProgram(p);
	}
	m_programs.clear();
	for (const auto page: g_unresolved_stub_thunk_pages) {
		EXIT_IF(!Libs::LibKernel::Memory::FreeGuestMemory(page, UNRESOLVED_STUB_PAGE_SIZE));
	}
	g_unresolved_stub_thunk_pages.clear();
	g_unresolved_stub_thunk_offset = 0;
	g_stubbed_imports.clear();
	g_unresolved_stub_call_log_count.store(0);
	if (g_invalid_memory != 0) {
		EXIT_IF(!Libs::LibKernel::Memory::FreeGuestMemory(g_invalid_memory, 4096));
		g_invalid_memory = 0;
	}
	g_tls_main_program        = nullptr;
	g_tls_cached_main_program = nullptr;
	g_tls_cached_main_tcb     = nullptr;
	g_desired_base_addr       = SYSTEM_RESERVED + CODE_BASE_OFFSET;
	m_symbols.reset();
	m_relocated = false;
}

void RuntimeLinker::Resolve(const std::string& name, SymbolType type, Program* program,
                            SymbolRecord* out_info, bool* bind_self) {
	KYTY_PROFILER_FUNCTION();

	Common::LockGuard lock(m_mutex);

	EXIT_IF(out_info == nullptr);

	auto ids = Common::Split(name, '#');

	if (bind_self != nullptr) {
		*bind_self = false;
	}

	if (ids.size() == 3) {
		const LibraryId* l = FindLibrary(*program, ids.at(1));
		const ModuleId*  m = FindModule(*program, ids.at(2));

		auto resolve_by_nid = [this, type](const std::string& nid, SymbolRecord* out) -> bool {
			EXIT_IF(out == nullptr);

			if (m_symbols != nullptr) {
				if (const auto* rec = m_symbols->FindByNid(nid, type); rec != nullptr) {
					*out = *rec;
					return true;
				}
			}

			for (auto* p: m_programs) {
				if (p != nullptr && p->export_symbols != nullptr) {
					if (const auto* rec = p->export_symbols->FindByNid(nid, type); rec != nullptr) {
						*out = *rec;
						return true;
					}
				}
			}

			return false;
		};

		if (l != nullptr && m != nullptr) {
			SymbolResolve sr {};
			sr.name                 = ids.at(0);
			sr.library              = l->name;
			sr.library_version      = l->version;
			sr.module               = m->name;
			sr.module_version_major = m->version_major;
			sr.module_version_minor = m->version_minor;
			sr.type                 = type;

			const SymbolRecord* rec = nullptr;

			if (m_symbols != nullptr) {
				rec = m_symbols->Find(sr);
			}

			if (rec == nullptr) {
				if (auto* p = FindProgram(*m, *l); p != nullptr && p->export_symbols != nullptr) {
					rec = p->export_symbols->Find(sr);
					if (bind_self != nullptr) {
						*bind_self = (p == program);
					}
				}
			}

			if (rec == nullptr) {
				if (resolve_by_nid(sr.name, out_info)) {
					LOGF("PS5 NID fallback: %s -> %s\n", sr.name.c_str(), out_info->name.c_str());
					return;
				}
			}

			if (rec != nullptr) {
				//*out_vaddr = rec->vaddr;
				*out_info = *rec;
			} else {
				out_info->vaddr    = 0;
				out_info->name     = SymbolDatabase::GenerateName(sr);
				out_info->dbg_name = "";
			}
		} else {
			if (resolve_by_nid(ids.at(0), out_info)) {
				LOGF("PS5 NID fallback: %s -> %s (missing lib/module metadata)\n",
				     ids.at(0).c_str(), out_info->name.c_str());
				return;
			}

			EXIT("l == nullptr || m == nullptr");
		}
	} else {
		out_info->vaddr    = 0;
		out_info->name     = name;
		out_info->dbg_name = "";
	}
}

bool RuntimeLinker::ResolveLoadedSymbolByNid(const std::string& nid, SymbolType type,
                                             SymbolRecord* out_info) {
	KYTY_PROFILER_FUNCTION();

	Common::LockGuard lock(m_mutex);

	EXIT_IF(out_info == nullptr);

	for (auto* p: m_programs) {
		if (p != nullptr && p->export_symbols != nullptr) {
			if (const auto* rec = p->export_symbols->FindByNid(nid, type); rec != nullptr) {
				*out_info = *rec;
				return true;
			}
		}
	}

	if (m_symbols != nullptr) {
		if (const auto* rec = m_symbols->FindByNid(nid, type); rec != nullptr) {
			*out_info = *rec;
			return true;
		}
	}

	return false;
}

uint64_t RuntimeLinker::ReadFromElf(Program* program, uint64_t vaddr) {
	EXIT_IF(program == nullptr);
	EXIT_IF(program->base_vaddr == 0 || program->base_size == 0);
	EXIT_IF(program->elf == nullptr);

	uint64_t ret = 0;

	const auto* ehdr = program->elf->GetEhdr();
	const auto* phdr = program->elf->GetPhdr();

	EXIT_IF(phdr == nullptr || ehdr == nullptr);

	for (Elf64_Half i = 0; i < ehdr->e_phnum; i++) {
		if (phdr[i].p_memsz != 0 && (phdr[i].p_type == PT_LOAD || phdr[i].p_type == PT_OS_RELRO)) {
			uint64_t segment_addr      = phdr[i].p_vaddr + program->base_vaddr;
			uint64_t segment_file_size = phdr[i].p_filesz;

			if (vaddr >= segment_addr && vaddr < segment_addr + segment_file_size) {
				program->elf->LoadSegment(reinterpret_cast<uint64_t>(&ret),
				                          phdr[i].p_offset + vaddr - segment_addr, sizeof(ret));
				break;
			}
		}
	}

	return ret;
}

Program* RuntimeLinker::FindProgramById(int32_t id) {
	Common::LockGuard lock(m_mutex);

	// Id 0 is reserved for main program
	if (id == 0 && !m_programs.empty()) {
		return m_programs.front();
	}

	for (auto* p: m_programs) {
		if (p->unique_id == id) {
			return p;
		}
	}

	return nullptr;
}

Program* RuntimeLinker::FindProgramByFileName(const std::filesystem::path& elf_name) {
	Common::LockGuard lock(m_mutex);

	auto fixed_name = Common::FixFilenameSlash(Common::PathToGenericString(elf_name));
	for (auto* p: m_programs) {
		if (Common::EqualNoCase(Common::FixFilenameSlash(Common::PathToGenericString(p->file_name)),
		                        fixed_name)) {
			return p;
		}
	}

	return nullptr;
}

Program* RuntimeLinker::FindProgramByAddr(uint64_t vaddr) {
	Common::LockGuard lock(m_mutex);

	for (auto* p: m_programs) {
		const auto* ehdr = p->elf->GetEhdr();
		const auto* phdr = p->elf->GetPhdr();

		EXIT_IF(phdr == nullptr || ehdr == nullptr);

		for (Elf64_Half i = 0; i < ehdr->e_phnum; i++) {
			if (phdr[i].p_memsz != 0 &&
			    (phdr[i].p_type == PT_LOAD || phdr[i].p_type == PT_OS_RELRO)) {
				uint64_t segment_addr = phdr[i].p_vaddr + p->base_vaddr;
				uint64_t segment_size = GetAlignedSize(phdr + i);

				if (vaddr >= segment_addr && vaddr < segment_addr + segment_size) {
					return p;
				}
			}
		}
	}

	return nullptr;
}

void RuntimeLinker::StackTrace(uint64_t frame_ptr) {
	void* stack[20];
	int   depth = 20;

	SysStackWalkX86(frame_ptr, stack, &depth);

	LOGF("Stack trace [thread = %d]:\n", Common::Thread::GetThreadIdUnique());

	for (int i = 0; i < depth; i++) {
		auto  vaddr = reinterpret_cast<uint64_t>(stack[i]);
		auto* p     = FindProgramByAddr(vaddr);
		LOGF("[%d] %016" PRIx64 ", off=%016" PRIx64 ", %s\n", i, vaddr,
		     (p == nullptr ? 0 : vaddr - p->base_vaddr),
		     (p == nullptr
		          ? "???"
		          : Common::FilenameWithoutDirectory(Common::PathToGenericString(p->file_name))
		                .c_str()));
	}
}

static std::string GetProgramModuleName(const Program* program) {
	EXIT_IF(program == nullptr);

	if (program->dynamic_info != nullptr && program->dynamic_info->so_name != nullptr &&
	    program->dynamic_info->so_name[0] != '\0') {
		return std::string(program->dynamic_info->so_name);
	}

	return Common::FilenameWithoutDirectory(Common::PathToGenericString(program->file_name));
}

static bool ModuleStartDependenciesSatisfied(const Program*               program,
                                             const std::vector<Program*>& programs,
                                             const std::vector<Program*>& started) {
	EXIT_IF(program == nullptr);
	EXIT_IF(program->dynamic_info == nullptr);

	for (const auto* needed: program->dynamic_info->needed) {
		if (needed == nullptr || needed[0] == '\0') {
			continue;
		}

		const auto needed_name = std::string(needed);

		for (auto* dependency: programs) {
			if (dependency == nullptr || dependency == program || dependency->elf == nullptr ||
			    !dependency->elf->IsShared()) {
				continue;
			}

			const auto dependency_name = GetProgramModuleName(dependency);
			if (Common::EqualNoCase(dependency_name, needed_name) ||
			    Common::EqualNoCase(Common::FilenameWithoutDirectory(
			                            Common::PathToGenericString(dependency->file_name)),
			                        needed_name)) {
				if (std::find(started.begin(), started.end(), dependency) == started.end()) {
					return false;
				}
				break;
			}
		}
	}

	return true;
}

void RuntimeLinker::StartAllModules() {
	Common::LockGuard lock(m_mutex);

	std::vector<Program*> started;

	for (;;) {
		bool progressed = false;

		for (auto* p: m_programs) {
			if (p->elf->IsShared() && p->dynamic_info->init_vaddr != 0 &&
			    std::find(started.begin(), started.end(), p) == started.end() &&
			    ModuleStartDependenciesSatisfied(p, m_programs, started)) {
				StartModule(p, 0, nullptr, nullptr);
				started.push_back(p);
				progressed = true;
			}
		}

		if (!progressed) {
			break;
		}
	}

	for (auto* p: m_programs) {
		if (p->elf->IsShared() && p->dynamic_info->init_vaddr != 0 &&
		    std::find(started.begin(), started.end(), p) == started.end()) {
			StartModule(p, 0, nullptr, nullptr);
			started.push_back(p);
		}
	}
}

void RuntimeLinker::StopAllModules() {
	Common::LockGuard lock(m_mutex);

	for (auto* p: m_programs) {
		if (p->elf->IsShared() && p->dynamic_info->fini_vaddr != 0) {
			StopModule(p, 0, nullptr, nullptr);
		}
	}
}

static bool IsAdjacentModuleFile(const std::string& name) {
	auto lower = Common::ToLower(name);
	return Common::EndsWith(lower, ".prx") || Common::EndsWith(lower, ".sprx");
}

static bool SkipAdjacentModuleFile(const std::string& name) {
	auto lower = Common::ToLower(name);
	return lower == "eboot.bin" || lower == "libkernel.prx" || lower == "libkernel_sys.prx";
}

void RuntimeLinker::PreloadAdjacentPrograms() {
	if (m_programs.empty()) {
		return;
	}

	std::vector<std::filesystem::path> module_paths;

	auto is_loaded = [this](const std::filesystem::path& path) {
		auto fixed_path = Common::FixFilenameSlash(Common::PathToGenericString(path));
		for (auto* program: m_programs) {
			if (Common::EqualNoCase(
			        Common::FixFilenameSlash(Common::PathToGenericString(program->file_name)),
			        fixed_path)) {
				return true;
			}
		}
		return false;
	};

	auto add_path = [&module_paths, &is_loaded](const std::filesystem::path& path) {
		if (is_loaded(path)) {
			return;
		}
		for (const auto& p: module_paths) {
			if (Common::EqualNoCase(Common::PathToGenericString(p),
			                        Common::PathToGenericString(path))) {
				return;
			}
		}
		module_paths.push_back(path);
	};

	auto add_dir = [&add_path](const std::filesystem::path& dir) {
		if (!Common::File::IsDirectoryExisting(dir)) {
			return;
		}
		for (const auto& entry: Common::File::GetDirEntries(dir)) {
			if (entry.is_file && IsAdjacentModuleFile(entry.name) &&
			    !SkipAdjacentModuleFile(entry.name)) {
				add_path(dir / entry.name);
			}
		}
	};

	auto root = m_programs.at(0)->file_name.parent_path();
	if (root.empty()) {
		return;
	}

	add_dir(root);
	add_dir(root / "sce_module");
	add_dir(root / "sce_modules");

	for (const auto& path: module_paths) {
		auto* program                        = LoadProgram(path);
		program->fail_if_global_not_resolved = false;
	}
}

int RuntimeLinker::StartModule(Program* program, size_t args, const void* argp,
                               module_func_t func) {
	EXIT_IF(program == nullptr);
	EXIT_IF(program->dynamic_info == nullptr);
	EXIT_IF(program->elf == nullptr);
	EXIT_IF(!program->elf->IsShared());

	EXIT_IF(std::find(m_programs.begin(), m_programs.end(), program) == m_programs.end());

	LOGF_COLOR(Log::Color::BrightYellow, "---\n--- Start module: %s\n---\n",
	           Common::PathToString(program->file_name).c_str());

	return RuntimeLinkerLifecycle::InvokeOptionalModuleLifecycleEntry(
	    program->dynamic_info->init_vaddr, [&](uint64_t entry_vaddr) {
		    return reinterpret_cast<module_ini_fini_func_t>(entry_vaddr + program->base_vaddr)(
		        args, argp, func);
	    });
}

int RuntimeLinker::StopModule(Program* program, size_t args, const void* argp, module_func_t func) {
	EXIT_IF(program == nullptr);
	EXIT_IF(program->dynamic_info == nullptr);
	EXIT_IF(program->elf == nullptr);
	EXIT_IF(!program->elf->IsShared());

	EXIT_IF(std::find(m_programs.begin(), m_programs.end(), program) == m_programs.end());

	LOGF_COLOR(Log::Color::BrightYellow, "---\n--- Stop module: %s\n---\n",
	           Common::PathToString(program->file_name).c_str());

	int result = RuntimeLinkerLifecycle::InvokeOptionalModuleLifecycleEntry(
	    program->dynamic_info->fini_vaddr, [&](uint64_t entry_vaddr) {
		    return reinterpret_cast<module_ini_fini_func_t>(entry_vaddr + program->base_vaddr)(
		        args, argp, func);
	    });

	Libs::LibKernel::PthreadDeleteStaticObjects(program);

	return result;
}

uint8_t* RuntimeLinker::TlsGetAddr(Program* program) {
	EXIT_IF(program == nullptr);

	Common::LockGuard lock(program->tls.mutex);

	auto& tls = program->tls.tlss[Common::Thread::GetThreadIdUnique()];

	if (tls.ptr == nullptr) {
		RuntimeLinkerBounds::TlsAllocationLayout allocation {};
		EXIT_IF(!RuntimeLinkerBounds::DecodeTlsAllocationLayout(program->tls.image_size,
		                                                       &allocation));
		EXIT_IF(program->tls.tcb_offset != allocation.tcb_offset);

		const auto tcb_offset = allocation.tcb_offset;
		const auto alloc_size = allocation.allocation_size;
		tls.ptr        = reinterpret_cast<uint8_t*>(Libs::LibKernel::Memory::AllocateRuntimeMemory(
		    0, alloc_size, Common::VirtualMemory::Mode::ReadWrite, "thread_local_storage"));
		tls.free_func  = nullptr;
		tls.vm_alloc   = true;
		tls.alloc_size = alloc_size;

		EXIT_IF(tls.ptr == nullptr);

		std::memset(tls.ptr, 0, alloc_size);

		if (!program->tls.init_image.empty()) {
			std::memcpy(tls.ptr, program->tls.init_image.data(), program->tls.init_image.size());
		} else {
			std::memcpy(tls.ptr, reinterpret_cast<void*>(program->tls.image_vaddr),
			            program->tls.init_size);
		}

		auto* tcb = reinterpret_cast<uint64_t*>(tls.ptr + tcb_offset);
		tcb[0]    = reinterpret_cast<uint64_t>(tcb);
	}

	return tls.ptr;
}

void RuntimeLinker::DeleteTls(Program* program, int thread_id) {
	EXIT_IF(program == nullptr);

	if (thread_id == Common::Thread::GetThreadIdUnique() && g_tls_cached_main_program == program) {
		g_tls_cached_main_program = nullptr;
		g_tls_cached_main_tcb     = nullptr;
	}

	Common::LockGuard lock(program->tls.mutex);

	if (auto it = program->tls.tlss.find(thread_id); it != program->tls.tlss.end()) {
		FreeTlsBlock(&it->second);
		program->tls.tlss.erase(it);
	}
}

static uint64_t CalcBaseSize(const Elf64_Ehdr* ehdr, const Elf64_Phdr* phdr) {
	uint64_t base_size = 0;
	for (Elf64_Half i = 0; i < ehdr->e_phnum; i++) {
		if (phdr[i].p_memsz != 0 && (phdr[i].p_type == PT_LOAD || phdr[i].p_type == PT_OS_RELRO)) {
			uint64_t last_addr = phdr[i].p_vaddr + GetAlignedSize(phdr + i);
			if (last_addr > base_size) {
				base_size = last_addr;
			}
		}
	}
	return base_size;
}

// NOLINTNEXTLINE(readability-function-cognitive-complexity)
void RuntimeLinker::LoadProgramToMemory(Program* program) {
	KYTY_PROFILER_FUNCTION();

	EXIT_IF(program == nullptr || program->base_vaddr != 0 || program->base_size != 0 ||
	        program->elf == nullptr);

	// static uint64_t desired_base_addr = DESIRED_BASE_ADDR;

	bool is_shared   = program->elf->IsShared();
	bool is_next_gen = program->elf->IsNextGen();

	EXIT_NOT_IMPLEMENTED(!is_shared && !is_next_gen);

	const auto* ehdr = program->elf->GetEhdr();
	const auto* phdr = program->elf->GetPhdr();

	EXIT_IF(phdr == nullptr || ehdr == nullptr);
	EXIT_IF(!AreRelroRangesValid(ehdr, phdr));

	program->base_size                 = CalcBaseSize(ehdr, phdr);
	constexpr uint64_t GUEST_PAGE_SIZE = 0x4000;
	EXIT_IF(program->base_size > UINT64_MAX - (GUEST_PAGE_SIZE - 1));
	program->base_size_aligned = AlignUp(program->base_size, GUEST_PAGE_SIZE);

	uint64_t tls_handler_size = is_shared ? 0 : Jit::SafeCall::GetSize();
	EXIT_IF(tls_handler_size > UINT64_MAX - program->base_size_aligned);
	program->mapped_size = program->base_size_aligned + tls_handler_size;

	program->base_vaddr = Libs::LibKernel::Memory::AllocateProgramMemory(
	    g_desired_base_addr, program->mapped_size, Common::VirtualMemory::Mode::ExecuteReadWrite,
	    Common::PathToString(program->file_name.filename()).c_str());

	if (!is_shared) {
		program->tls.handler_vaddr = program->base_vaddr + program->base_size_aligned;
	}

	g_desired_base_addr += CODE_BASE_INCR * (1 + program->mapped_size / CODE_BASE_INCR);

	EXIT_IF(program->base_vaddr == 0);
	EXIT_IF(program->base_size_aligned < program->base_size);
	LOGF("base_vaddr             = 0x%016" PRIx64 "\n"
	     "base_size              = 0x%016" PRIx64 "\n"
	     "base_size_aligned      = 0x%016" PRIx64 "\n"
	     "mapped_size            = 0x%016" PRIx64 "\n",
	     program->base_vaddr, program->base_size, program->base_size_aligned, program->mapped_size);
	if (!is_shared) {
		LOGF("tls_handler_size       = 0x%016" PRIx64 "\n", tls_handler_size);
	}

	if (!Common::HostException::InstallHandler(KytyExceptionHandler)) {
		EXIT("Failed to install the required vectored exception handler\n");
	}

	// program->elf->SetBaseVAddr(program->base_vaddr);

	for (Elf64_Half i = 0; i < ehdr->e_phnum; i++) {
		if (phdr[i].p_memsz != 0 && (phdr[i].p_type == PT_LOAD || phdr[i].p_type == PT_OS_RELRO)) {
			uint64_t segment_file_size   = phdr[i].p_filesz;
			uint64_t segment_memory_size = GetAlignedSize(phdr + i);
			uint64_t segment_addr        = 0;
			EXIT_IF(!RuntimeLinkerBounds::DecodeRelocatedRange(
			    program->base_vaddr, phdr[i].p_vaddr, segment_memory_size, &segment_addr));
			auto     mode                = GetMode(phdr[i].p_flags);

			LOGF("[%d] addr        = 0x%016" PRIx64 "\n"
			     "[%d] file_size   = %" PRIu64 "\n"
			     "[%d] memory_size = %" PRIu64 "\n"
			     "[%d] mode        = %s\n",
			     i, segment_addr, i, segment_file_size, i, segment_memory_size, i,
			     Common::EnumName(mode).c_str());

			program->elf->LoadSegment(segment_addr, phdr[i].p_offset, segment_file_size);

			bool skip_protect = (phdr[i].p_type == PT_LOAD && is_next_gen &&
			                     mode == Common::VirtualMemory::Mode::NoAccess);

			if (Common::VirtualMemory::IsExecute(mode)) {
				PatchProgram(program, segment_addr, segment_memory_size);
			}

			if (!skip_protect) {
				Libs::LibKernel::Memory::SetProgramMemoryProtection(segment_addr,
				                                                    segment_memory_size, mode);

				if (Common::VirtualMemory::IsExecute(mode)) {
					Common::VirtualMemory::FlushInstructionCache(segment_addr, segment_memory_size);
				}
			}
		}

		if (phdr[i].p_type == PT_TLS) {
			RuntimeLinkerBounds::TlsLayout layout {};
			EXIT_IF(!RuntimeLinkerBounds::DecodeTlsLayout(
			    phdr[i].p_offset, phdr[i].p_vaddr, phdr[i].p_filesz, phdr[i].p_memsz,
			    phdr[i].p_align, &layout));
			EXIT_IF(phdr[i].p_vaddr >= program->base_size);

			program->tls.image_vaddr = phdr[i].p_vaddr + program->base_vaddr;
			program->tls.init_size   = layout.init_size;
			program->tls.image_size  = layout.image_size;
			program->tls.tcb_offset  = layout.tcb_offset;

			LOGF("tls addr = 0x%016" PRIx64 "\n"
			     "tls init   = %" PRIu64 "\n"
			     "tls size   = %" PRIu64 "\n"
			     "tls offset = %" PRIu64 "\n",
			     program->tls.image_vaddr, program->tls.init_size, program->tls.image_size,
			     program->tls.tcb_offset);
		}

		if (phdr[i].p_type == PT_OS_PROCPARAM) {
			EXIT_IF(program->proc_param_vaddr != 0);
			EXIT_IF(phdr[i].p_vaddr >= program->base_size);

			program->proc_param_vaddr = phdr[i].p_vaddr + program->base_vaddr;
		}
	}

	if (!is_shared) {
		SetupTlsHandler(program);
	}

	LOGF("entry = 0x%016" PRIx64 "\n", program->elf->GetEntry() + program->base_vaddr);
}

void RuntimeLinker::DeleteProgram(Program* p) {
	auto program = std::unique_ptr<Program>(p);
	if (g_tls_main_program == program.get()) {
		g_tls_main_program = nullptr;
	}
	if (g_tls_cached_main_program == program.get()) {
		g_tls_cached_main_program = nullptr;
		g_tls_cached_main_tcb     = nullptr;
	}
	for (auto& record: g_stubbed_imports) {
		if (record.patch_vaddr >= program->base_vaddr &&
		    record.patch_vaddr < program->base_vaddr + program->mapped_size) {
			record.patch_vaddr = 0;
		}
	}

	if (program->base_vaddr != 0 || program->mapped_size != 0) {
		EXIT_IF(program->base_vaddr == 0 || program->mapped_size == 0);
		EXIT_IF(
		    !Libs::LibKernel::Memory::FreeGuestMemory(program->base_vaddr, program->mapped_size));
	}

	if (program->custom_call_plt_vaddr != 0 || program->custom_call_plt_num != 0) {
		const auto size = Jit::CallPlt::GetSize(program->custom_call_plt_num);
		EXIT_IF(!Libs::LibKernel::Memory::FreeGuestMemory(program->custom_call_plt_vaddr, size));
	}
}

void RuntimeLinker::ParseProgramDynamicInfo(Program* program) {
	KYTY_PROFILER_FUNCTION();

	EXIT_IF(program == nullptr);
	EXIT_IF(program->elf == nullptr);
	EXIT_IF(program->dynamic_info != nullptr);

	program->dynamic_info = std::make_unique<DynamicInfo>();

	auto* elf = program->elf.get();

	EXIT_NOT_IMPLEMENTED(elf->HasDynValue(DT_OS_HASH) && elf->HasDynValue(DT_HASH));
	const bool has_sysv_hash       = elf->HasDynValue(DT_HASH);
	const bool hash_size_is_known  = elf->HasDynValue(DT_OS_HASHSZ);
	RuntimeLinkerBounds::SysvHashLayout sysv_hash_layout {};
	GetDynValue(elf, &program->dynamic_info->hash_table_size, DT_OS_HASHSZ);
	if (has_sysv_hash) {
		EXIT_IF(!DecodeSysvHashMetadata(program, program->dynamic_info->hash_table_size,
		                                hash_size_is_known, &sysv_hash_layout));
		program->dynamic_info->hash_table_size = sysv_hash_layout.table_size;
	}
	GetDynDataOs(elf, &program->dynamic_info->hash_table, DT_OS_HASH);
	GetDynData(elf, program->base_vaddr, &program->dynamic_info->hash_table, DT_HASH);

	EXIT_NOT_IMPLEMENTED(elf->HasDynValue(DT_OS_STRTAB) && elf->HasDynValue(DT_STRTAB));
	EXIT_NOT_IMPLEMENTED(elf->HasDynValue(DT_OS_STRSZ) && elf->HasDynValue(DT_STRSZ));
	const bool standard_str_table = elf->HasDynValue(DT_STRTAB);
	const bool standard_str_size  = elf->HasDynValue(DT_STRSZ);
	EXIT_IF(!RuntimeLinkerBounds::IsStandardStringExtentMetadataComplete(
	    standard_str_table, standard_str_size));
	const bool os_str_table  = elf->HasDynValue(DT_OS_STRTAB);
	const bool has_str_table = os_str_table || standard_str_table;
	program->dynamic_info->str_table_size_known =
	    elf->HasDynValue(DT_OS_STRSZ) || standard_str_size;
	GetDynValue(elf, &program->dynamic_info->str_table_size, DT_OS_STRSZ);
	GetDynValue(elf, &program->dynamic_info->str_table_size, DT_STRSZ);
	if (has_str_table && program->dynamic_info->str_table_size_known) {
		EXIT_IF(!IsTableInFileBackedSegment(
		    elf, os_str_table ? DT_OS_STRTAB : DT_STRTAB,
		    program->dynamic_info->str_table_size, os_str_table));
	}
	GetDynDataOs(elf, &program->dynamic_info->str_table, DT_OS_STRTAB);
	GetDynData(elf, program->base_vaddr, &program->dynamic_info->str_table, DT_STRTAB);
	if (has_str_table && program->dynamic_info->str_table_size_known && !os_str_table) {
		EXIT_IF(!RuntimeLinkerBounds::IsDynamicStringTableShapeValid(
		    program->dynamic_info->str_table, program->dynamic_info->str_table_size));
	}

	EXIT_NOT_IMPLEMENTED(elf->HasDynValue(DT_OS_SYMTAB) && elf->HasDynValue(DT_SYMTAB));
	EXIT_NOT_IMPLEMENTED(elf->HasDynValue(DT_OS_SYMENT) && elf->HasDynValue(DT_SYMENT));
	const bool standard_symbol_table = elf->HasDynValue(DT_SYMTAB);
	EXIT_IF(!RuntimeLinkerBounds::IsStandardSymbolTableBoundAvailable(
	    standard_symbol_table, has_sysv_hash));
	if (const auto* symbol_table = elf->GetDynValue(DT_SYMTAB); symbol_table != nullptr) {
		EXIT_IF(!RuntimeLinkerBounds::IsDynamicSymbolTableAddressAligned(
		    symbol_table->d_un.d_ptr));
	}
	GetDynDataOs(elf, &program->dynamic_info->symbol_table, DT_OS_SYMTAB);
	GetDynData(elf, program->base_vaddr, &program->dynamic_info->symbol_table, DT_SYMTAB);
	program->dynamic_info->symbol_table_size_known = elf->HasDynValue(DT_OS_SYMTABSZ);
	GetDynValue(elf, &program->dynamic_info->symbol_table_total_size, DT_OS_SYMTABSZ);
	GetDynValue(elf, &program->dynamic_info->symbol_table_entry_size, DT_OS_SYMENT);
	GetDynValue(elf, &program->dynamic_info->symbol_table_entry_size, DT_SYMENT);
	if (has_sysv_hash) {
		uint64_t derived_symbol_table_size = 0;
		EXIT_IF(!RuntimeLinkerBounds::DecodeDynamicSymbolTableSize(
		    sysv_hash_layout.symbol_count, program->dynamic_info->symbol_table_entry_size,
		    program->dynamic_info->symbol_table_total_size,
		    program->dynamic_info->symbol_table_size_known, &derived_symbol_table_size));
		EXIT_IF(!IsTableInFileBackedSegment(elf, DT_SYMTAB, derived_symbol_table_size, false));
		program->dynamic_info->symbol_table_total_size = derived_symbol_table_size;
		program->dynamic_info->symbol_table_size_known = true;
		EXIT_IF(!RuntimeLinkerBounds::IsNullDynamicSymbolEntryValid(
		    program->dynamic_info->symbol_table, derived_symbol_table_size));
		EXIT_IF(!AreRegularDynamicFunctionDefinitionsValid(
		    elf, program->dynamic_info->symbol_table, derived_symbol_table_size));
		EXIT_IF(!AreRegularDynamicObjectDefinitionsValid(
		    elf, program->dynamic_info->symbol_table, derived_symbol_table_size));
	}

	GetDynPtr(elf, &program->dynamic_info->init_vaddr, DT_INIT);
	GetDynPtr(elf, &program->dynamic_info->fini_vaddr, DT_FINI);
	if (program->dynamic_info->init_vaddr != 0) {
		EXIT_IF(!IsFileBackedExecutableAddress(elf, program->dynamic_info->init_vaddr));
	}
	if (program->dynamic_info->fini_vaddr != 0) {
		EXIT_IF(!IsFileBackedExecutableAddress(elf, program->dynamic_info->fini_vaddr));
	}
	GetDynPtr(elf, &program->dynamic_info->init_array_vaddr, DT_INIT_ARRAY);
	GetDynPtr(elf, &program->dynamic_info->fini_array_vaddr, DT_FINI_ARRAY);
	GetDynPtr(elf, &program->dynamic_info->preinit_array_vaddr, DT_PREINIT_ARRAY);
	GetDynValue(elf, &program->dynamic_info->init_array_size, DT_INIT_ARRAYSZ);
	GetDynValue(elf, &program->dynamic_info->fini_array_size, DT_FINI_ARRAYSZ);
	GetDynValue(elf, &program->dynamic_info->preinit_array_size, DT_PREINIT_ARRAYSZ);
	EXIT_IF(!IsLifecycleArrayInFileBackedSegment(
	    elf, DT_INIT_ARRAY, program->dynamic_info->init_array_size));
	EXIT_IF(!IsLifecycleArrayInFileBackedSegment(
	    elf, DT_FINI_ARRAY, program->dynamic_info->fini_array_size));
	EXIT_IF(!IsLifecycleArrayInFileBackedSegment(
	    elf, DT_PREINIT_ARRAY, program->dynamic_info->preinit_array_size));

	EXIT_NOT_IMPLEMENTED(elf->HasDynValue(DT_OS_PLTGOT) && elf->HasDynValue(DT_PLTGOT));
	GetDynPtr(elf, &program->dynamic_info->pltgot_vaddr, DT_OS_PLTGOT);
	GetDynPtr(elf, &program->dynamic_info->pltgot_vaddr, DT_PLTGOT);
	if (program->dynamic_info->pltgot_vaddr != 0) {
		EXIT_IF(!IsPltGotResolverInMappedSegment(elf, program->dynamic_info->pltgot_vaddr));
	}

	Elf64_Sxword jmprel_type = 0;
	EXIT_NOT_IMPLEMENTED(elf->HasDynValue(DT_OS_PLTREL) && elf->HasDynValue(DT_PLTREL));
	GetDynValue(elf, &jmprel_type, DT_OS_PLTREL);
	GetDynValue(elf, &jmprel_type, DT_PLTREL);
	const bool os_jmprela  = elf->HasDynValue(DT_OS_JMPREL);
	const bool has_jmprela = os_jmprela || elf->HasDynValue(DT_JMPREL);
	const bool has_jmprela_size =
	    elf->HasDynValue(DT_OS_PLTRELSZ) || elf->HasDynValue(DT_PLTRELSZ);
	EXIT_IF(!RuntimeLinkerBounds::IsJmprelExtentMetadataComplete(has_jmprela,
	                                                            has_jmprela_size));

	EXIT_NOT_IMPLEMENTED(jmprel_type != DT_RELA);
	if (jmprel_type == DT_RELA) {
		EXIT_NOT_IMPLEMENTED(elf->HasDynValue(DT_OS_JMPREL) && elf->HasDynValue(DT_JMPREL));
		EXIT_NOT_IMPLEMENTED(elf->HasDynValue(DT_OS_PLTRELSZ) && elf->HasDynValue(DT_PLTRELSZ));
		GetDynValue(elf, &program->dynamic_info->jmprela_table_size, DT_OS_PLTRELSZ);
		GetDynValue(elf, &program->dynamic_info->jmprela_table_size, DT_PLTRELSZ);
	}

	EXIT_NOT_IMPLEMENTED(elf->HasDynValue(DT_OS_RELA) && elf->HasDynValue(DT_RELA));
	const bool os_rela       = elf->HasDynValue(DT_OS_RELA);
	const bool has_rela      = os_rela || elf->HasDynValue(DT_RELA);
	const bool has_rela_size = elf->HasDynValue(DT_OS_RELASZ) || elf->HasDynValue(DT_RELASZ);
	EXIT_IF(!RuntimeLinkerBounds::IsRelaExtentMetadataComplete(has_rela, has_rela_size));
	GetDynValue(elf, &program->dynamic_info->rela_table_total_size, DT_OS_RELASZ);
	GetDynValue(elf, &program->dynamic_info->rela_table_total_size, DT_RELASZ);
	GetDynValue(elf, &program->dynamic_info->rela_table_entry_size, DT_OS_RELAENT);
	GetDynValue(elf, &program->dynamic_info->rela_table_entry_size, DT_RELAENT);

	if (has_rela) {
		EXIT_IF(!IsRelaTableInFileBackedSegment(
		    elf, os_rela ? DT_OS_RELA : DT_RELA,
		    program->dynamic_info->rela_table_total_size,
		    program->dynamic_info->rela_table_entry_size, os_rela));
	}

	if (has_jmprela) {
		EXIT_IF(!IsRelaTableInFileBackedSegment(
		    elf, os_jmprela ? DT_OS_JMPREL : DT_JMPREL,
		    program->dynamic_info->jmprela_table_size,
		    program->dynamic_info->rela_table_entry_size, os_jmprela));
	}

	GetDynDataOs(elf, &program->dynamic_info->rela_table, DT_OS_RELA);
	GetDynData(elf, program->base_vaddr, &program->dynamic_info->rela_table, DT_RELA);
	GetDynDataOs(elf, &program->dynamic_info->jmprela_table, DT_OS_JMPREL);
	GetDynData(elf, program->base_vaddr, &program->dynamic_info->jmprela_table, DT_JMPREL);
	EXIT_IF(!AreRelocationRecordsSupported(program->dynamic_info->rela_table,
	                                       program->dynamic_info->rela_table_total_size));
	EXIT_IF(!AreRelocationRecordsSupported(program->dynamic_info->jmprela_table,
	                                       program->dynamic_info->jmprela_table_size));
	EXIT_IF(!AreRelocationSymbolsSupported(
	    program->dynamic_info->rela_table,
	    program->dynamic_info->rela_table_total_size,
	    program->dynamic_info->symbol_table,
	    program->dynamic_info->symbol_table_total_size,
	    program->dynamic_info->symbol_table_entry_size,
	    program->dynamic_info->symbol_table_size_known,
	    program->dynamic_info->str_table,
	    program->dynamic_info->str_table_size,
	    program->dynamic_info->str_table_size_known));
	EXIT_IF(!AreRelocationSymbolsSupported(
	    program->dynamic_info->jmprela_table,
	    program->dynamic_info->jmprela_table_size,
	    program->dynamic_info->symbol_table,
	    program->dynamic_info->symbol_table_total_size,
	    program->dynamic_info->symbol_table_entry_size,
	    program->dynamic_info->symbol_table_size_known,
	    program->dynamic_info->str_table,
	    program->dynamic_info->str_table_size,
	    program->dynamic_info->str_table_size_known));
	EXIT_IF(!AreRelocationTargetsInMappedSegments(
	    elf, program->dynamic_info->rela_table,
	    program->dynamic_info->rela_table_total_size));
	EXIT_IF(!AreRelocationTargetsInMappedSegments(
	    elf, program->dynamic_info->jmprela_table,
	    program->dynamic_info->jmprela_table_size));

	GetDynValue(elf, &program->dynamic_info->relative_count, DT_RELACOUNT);

	GetDynValue(elf, &program->dynamic_info->debug, DT_DEBUG);
	GetDynValue(elf, &program->dynamic_info->flags, DT_FLAGS);
	GetDynValue(elf, &program->dynamic_info->textrel, DT_TEXTREL);

	EXIT_NOT_IMPLEMENTED(program->dynamic_info->debug != 0);
	EXIT_NOT_IMPLEMENTED(program->dynamic_info->textrel != 0);

	std::vector<uint64_t> needed;
	GetDynValues(elf, &needed, DT_NEEDED);
	for (auto need: needed) {
		const char* needed_name = GetDynamicString(*program->dynamic_info, need);
		EXIT_IF(needed_name == nullptr);
		program->dynamic_info->needed.push_back(needed_name);
	}

	if (const auto* so_name = elf->GetDynValue(DT_SONAME); so_name != nullptr) {
		program->dynamic_info->so_name =
		    GetDynamicString(*program->dynamic_info, so_name->d_un.d_val);
		EXIT_IF(program->dynamic_info->so_name == nullptr);
	}

	EXIT_NOT_IMPLEMENTED(elf->HasDynValue(DT_OS_NEEDED_MODULE) &&
	                     elf->HasDynValue(DT_OS_NEEDED_MODULE_1));
	EXIT_NOT_IMPLEMENTED(elf->HasDynValue(DT_OS_MODULE_INFO) &&
	                     elf->HasDynValue(DT_OS_MODULE_INFO_1));
	EXIT_NOT_IMPLEMENTED(elf->HasDynValue(DT_OS_IMPORT_LIB) &&
	                     elf->HasDynValue(DT_OS_IMPORT_LIB_1));
	EXIT_NOT_IMPLEMENTED(elf->HasDynValue(DT_OS_EXPORT_LIB) &&
	                     elf->HasDynValue(DT_OS_EXPORT_LIB_1));
	GetDynModules(elf, &program->dynamic_info->import_modules, *program->dynamic_info,
	              DT_OS_NEEDED_MODULE);
	GetDynModules(elf, &program->dynamic_info->import_modules, *program->dynamic_info,
	              DT_OS_NEEDED_MODULE_1);
	GetDynModules(elf, &program->dynamic_info->export_modules, *program->dynamic_info,
	              DT_OS_MODULE_INFO);
	GetDynModules(elf, &program->dynamic_info->export_modules, *program->dynamic_info,
	              DT_OS_MODULE_INFO_1);
	GetDynLibs(elf, &program->dynamic_info->import_libs, *program->dynamic_info,
	           DT_OS_IMPORT_LIB);
	GetDynLibs(elf, &program->dynamic_info->import_libs, *program->dynamic_info,
	           DT_OS_IMPORT_LIB_1);
	GetDynLibs(elf, &program->dynamic_info->export_libs, *program->dynamic_info,
	           DT_OS_EXPORT_LIB);
	GetDynLibs(elf, &program->dynamic_info->export_libs, *program->dynamic_info,
	           DT_OS_EXPORT_LIB_1);
}

static void InstallRelocateHandler(Program* program) {
	KYTY_PROFILER_FUNCTION();

	constexpr uint64_t pltgot_size  = RuntimeLinkerBounds::ELF64_PLTGOT_RESOLVER_SIZE;
	uint64_t           pltgot_vaddr = 0;
	EXIT_IF(!RuntimeLinkerBounds::DecodeRelocatedRange(
	    program->base_vaddr, program->dynamic_info->pltgot_vaddr, pltgot_size, &pltgot_vaddr));
	void** pltgot = reinterpret_cast<void**>(pltgot_vaddr);

	Common::VirtualMemory::Mode old_mode {};
	EXIT_IF(!Libs::LibKernel::Memory::ProtectGuestMemory(
	    pltgot_vaddr, pltgot_size, Common::VirtualMemory::Mode::Write, &old_mode));

	pltgot[1] = program;
	pltgot[2] = reinterpret_cast<void*>(RelocateHandler);

	EXIT_IF(!Libs::LibKernel::Memory::ProtectGuestMemory(pltgot_vaddr, pltgot_size, old_mode));

	if (Common::VirtualMemory::IsExecute(old_mode)) {
		Common::VirtualMemory::FlushInstructionCache(pltgot_vaddr, pltgot_size);
	}

	// TODO(): check if this table already generated by compiler (sometimes it is missing)
	if (program->custom_call_plt_vaddr == 0) {
		program->custom_call_plt_num =
		    program->dynamic_info->jmprela_table_size / sizeof(Elf64_Rela);
		auto size                      = Jit::CallPlt::GetSize(program->custom_call_plt_num);
		program->custom_call_plt_vaddr = Libs::LibKernel::Memory::AllocateRuntimeMemory(
		    SYSTEM_RESERVED, size, Common::VirtualMemory::Mode::Write, "custom_call_plt");
		EXIT_NOT_IMPLEMENTED(program->custom_call_plt_vaddr == 0);
		auto* code = new (reinterpret_cast<void*>(program->custom_call_plt_vaddr))
		    Jit::CallPlt(program->custom_call_plt_num);
		code->SetPltGot(pltgot_vaddr);
		EXIT_IF(!Libs::LibKernel::Memory::ProtectGuestMemory(program->custom_call_plt_vaddr, size,
		                                                     Common::VirtualMemory::Mode::Execute));
		Common::VirtualMemory::FlushInstructionCache(program->custom_call_plt_vaddr, size);
	}
}

void RuntimeLinker::Relocate(Program* program) {
	KYTY_PROFILER_FUNCTION();

	EXIT_IF(program == nullptr);

	if (g_invalid_memory == 0) {
		g_invalid_memory = Libs::LibKernel::Memory::AllocateRuntimeMemory(
		    INVALID_MEMORY, 4096, Common::VirtualMemory::Mode::NoAccess, "invalid_memory", true);
		EXIT_NOT_IMPLEMENTED(g_invalid_memory == 0);
	}

	LOGF_COLOR(Log::Color::White, "--- Relocate program: %s ---\n",
	           Common::PathToString(program->file_name).c_str());

	EXIT_NOT_IMPLEMENTED(program->dynamic_info->symbol_table_entry_size != sizeof(Elf64_Sym));
	EXIT_NOT_IMPLEMENTED(program->dynamic_info->rela_table_entry_size != sizeof(Elf64_Rela));
	EXIT_NOT_IMPLEMENTED(program->dynamic_info->jmprela_table == nullptr);
	EXIT_NOT_IMPLEMENTED(program->dynamic_info->rela_table == nullptr);
	EXIT_NOT_IMPLEMENTED(program->dynamic_info->symbol_table == nullptr);
	EXIT_NOT_IMPLEMENTED(program->dynamic_info->pltgot_vaddr == 0);

	std::vector<std::string> unresolved;
	const bool               imports_only = program->relocated;
	EXIT_IF(!RuntimeLinkerLifecycle::RunRelocationProtectionLifecycle(
	    imports_only, [&]() { return SetRelroProtection(program, false); },
	    [&]() {
		    InstallRelocateHandler(program);
		    RelocateRecords(program->dynamic_info->rela_table,
		                    program->dynamic_info->rela_table_total_size, program, false,
		                    imports_only, &unresolved);
		    RelocateRecords(program->dynamic_info->jmprela_table,
		                    program->dynamic_info->jmprela_table_size, program, true, imports_only,
		                    &unresolved);
		    return true;
	    },
	    [&]() { return SetRelroProtection(program, true); }));
	program->relocated = true;

	if (program->tls.image_vaddr != 0 && program->tls.init_size != 0 &&
	    program->tls.init_image.empty()) {
		const auto* src = reinterpret_cast<const uint8_t*>(program->tls.image_vaddr);
		program->tls.init_image.assign(src, src + program->tls.init_size);
	}

	if (!unresolved.empty()) {
		LOGF("--- Stubbed unresolved imports: %zu ---\n", unresolved.size());
		for (const auto& symbol: unresolved) {
			LOGF("Stubbed: %s\n", symbol.c_str());
		}
	}
}

Program* RuntimeLinker::FindProgram(const ModuleId& m, const LibraryId& l) {
	Common::LockGuard lock(m_mutex);

	for (auto* p: m_programs) {
		const auto& export_libs    = p->dynamic_info->export_libs;
		const auto& export_modules = p->dynamic_info->export_modules;

		if (std::find(export_libs.begin(), export_libs.end(), l) != export_libs.end() &&
		    std::find(export_modules.begin(), export_modules.end(), m) != export_modules.end()) {
			return p;
		}
	}
	return nullptr;
}

const ModuleId* RuntimeLinker::FindModule(const Program& program, const std::string& id) {
	const auto& import_modules = program.dynamic_info->import_modules;

	if (auto it = std::find_if(import_modules.begin(), import_modules.end(),
	                           [&id](const auto& module) { return module.id == id; });
	    it != import_modules.end()) {
		return &(*it);
	}

	const auto& export_modules = program.dynamic_info->export_modules;

	if (auto it = std::find_if(export_modules.begin(), export_modules.end(),
	                           [&id](const auto& module) { return module.id == id; });
	    it != export_modules.end()) {
		return &(*it);
	}

	return nullptr;
}

const LibraryId* RuntimeLinker::FindLibrary(const Program& program, const std::string& id) {
	const auto& import_libs = program.dynamic_info->import_libs;

	if (auto it = std::find_if(import_libs.begin(), import_libs.end(),
	                           [&id](const auto& lib) { return lib.id == id; });
	    it != import_libs.end()) {
		return &(*it);
	}

	const auto& export_libs = program.dynamic_info->export_libs;

	if (auto it = std::find_if(export_libs.begin(), export_libs.end(),
	                           [&id](const auto& lib) { return lib.id == id; });
	    it != export_libs.end()) {
		return &(*it);
	}

	return nullptr;
}

void RuntimeLinker::CreateSymbolDatabase(Program* program) {
	KYTY_PROFILER_FUNCTION();

	EXIT_IF(program == nullptr);
	EXIT_IF(program->export_symbols != nullptr);
	EXIT_IF(program->import_symbols != nullptr);

	program->export_symbols = std::make_unique<SymbolDatabase>();
	program->import_symbols = std::make_unique<SymbolDatabase>();

	auto syms = [](Program* program, SymbolDatabase* symbols, bool is_export) {
		if (program->dynamic_info->symbol_table == nullptr ||
		    program->dynamic_info->str_table == nullptr) {
			return;
		}

		for (auto* sym = program->dynamic_info->symbol_table;
		     reinterpret_cast<uint8_t*>(sym) <
		     reinterpret_cast<uint8_t*>(program->dynamic_info->symbol_table) +
		         program->dynamic_info->symbol_table_total_size;
		     sym++) {
			const char* symbol_name = GetDynamicString(*program->dynamic_info, sym->st_name);
			EXIT_IF(symbol_name == nullptr);
			std::string id   = std::string(symbol_name);
			auto        bind = sym->GetBind();
			auto        type = sym->GetType();
			auto        ids  = Common::Split(id, '#');
			const bool  is_definition =
			    sym->st_shndx != RuntimeLinkerBounds::ELF64_SHN_UNDEF;

			if (ids.size() == 3) {
				const auto* l = FindLibrary(*program, ids.at(1));
				const auto* m = FindModule(*program, ids.at(2));

				if (l != nullptr && m != nullptr && (bind == STB_GLOBAL || bind == STB_WEAK) &&
				    (type == STT_FUNC || type == STT_OBJECT || type == STT_NOTYPE) &&
				    is_export == is_definition) {
					SymbolResolve sr {};
					sr.name                 = ids.at(0);
					sr.library              = l->name;
					sr.library_version      = l->version;
					sr.module               = m->name;
					sr.module_version_major = m->version_major;
					sr.module_version_minor = m->version_minor;
					switch (type) {
						case STT_NOTYPE: sr.type = SymbolType::NoType; break;
						case STT_FUNC: sr.type = SymbolType::Func; break;
						case STT_OBJECT: sr.type = SymbolType::Object; break;
						default: sr.type = SymbolType::Unknown; break;
					}
					uint64_t symbol_address = 0;
					if (is_export) {
						EXIT_IF(!RuntimeLinkerBounds::DecodeDynamicSymbolAddress(
						    sym->st_shndx, sym->st_value, program->base_vaddr,
						    &symbol_address));
					}
					symbols->Add(sr, symbol_address);
				}
			}
		}
	};

	syms(program, program->export_symbols.get(), true);
	syms(program, program->import_symbols.get(), false);
}

void RuntimeLinker::SetupTlsHandler(Program* program) {
	EXIT_IF(program == nullptr);
	EXIT_IF(g_tls_main_program != nullptr);
	EXIT_IF(program->elf == nullptr);
	EXIT_IF(program->elf->IsShared());
	EXIT_IF(program->tls.handler_vaddr == 0);

	g_tls_main_program = program;

	auto* code = new (reinterpret_cast<void*>(program->tls.handler_vaddr)) Jit::SafeCall;

	code->SetFunc(TlsMainGetAddr);

	for (uint8_t reg = 1; reg < 8; reg++) {
		if (reg == 4) {
			continue;
		}

		auto* stub = new (reinterpret_cast<void*>(program->tls.handler_vaddr +
		                                          Jit::TlsRegStub::GetOffset(reg))) Jit::TlsRegStub;
		stub->SetFunc(program->tls.handler_vaddr);
		stub->SetOutputReg(reg);
	}

	EXIT_IF(!Libs::LibKernel::Memory::ProtectGuestMemory(program->tls.handler_vaddr,
	                                                     Jit::SafeCall::GetSize(),
	                                                     Common::VirtualMemory::Mode::Execute));
	Common::VirtualMemory::FlushInstructionCache(program->tls.handler_vaddr,
	                                             Jit::SafeCall::GetSize());
}

void RuntimeLinker::DeleteTlss(int thread_id) {
	Common::LockGuard lock(m_mutex);

	for (auto* p: m_programs) {
		DeleteTls(p, thread_id);
	}
}

void RuntimeLinker::SetApplicationHeapApi(void* const api[10]) {
	Common::LockGuard lock(m_mutex);

	if (api == nullptr || api[0] == nullptr || api[1] == nullptr) {
		m_application_heap_malloc         = nullptr;
		m_application_heap_free           = nullptr;
		m_application_heap_posix_memalign = nullptr;
		return;
	}

	m_application_heap_malloc = reinterpret_cast<application_heap_malloc_func_t>(api[0]);
	m_application_heap_free   = reinterpret_cast<application_heap_free_func_t>(api[1]);
	m_application_heap_posix_memalign =
	    reinterpret_cast<application_heap_posix_memalign_func_t>(api[6]);
}

void* RuntimeLinker::ApplicationHeapMemalign(uint64_t alignment, uint64_t size) {
	Common::LockGuard lock(m_mutex);

	if (m_application_heap_posix_memalign != nullptr) {
		void* ptr = nullptr;
		return m_application_heap_posix_memalign(&ptr, alignment, size) == 0 ? ptr : nullptr;
	}

	return nullptr;
}

void* RuntimeLinker::ApplicationHeapMalloc(uint64_t size) {
	Common::LockGuard lock(m_mutex);

	return m_application_heap_malloc != nullptr ? m_application_heap_malloc(size) : nullptr;
}

} // namespace Loader
