#include "common/hostException.h"

#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <iterator>
#include <memory>

#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
#include <windows.h> // IWYU pragma: keep
#elif defined(__APPLE__)
#include <csignal>
#include <execinfo.h>
#include <sys/ucontext.h>
#else
#include <csignal>
#include <initializer_list>
#include <ucontext.h> // IWYU pragma: keep
#include <unistd.h>
#endif

// IWYU pragma: no_include <errhandlingapi.h>
// IWYU pragma: no_include <excpt.h>
// IWYU pragma: no_include <minwinbase.h>
// IWYU pragma: no_include <minwindef.h>
// IWYU pragma: no_include <wtypes.h>

namespace Common::HostException {

#if !defined(__APPLE__)

static std::atomic<Handler> g_handler {nullptr};
static std::atomic_uint32_t g_install_state {0};
static thread_local bool    g_in_exception_filter = false;

static_assert(decltype(g_handler)::is_always_lock_free);
static_assert(decltype(g_install_state)::is_always_lock_free);

[[noreturn]] static void FailFast(const char* reason) noexcept {
	std::fputs("HostException fail-fast: ", stderr);
	std::fputs(reason != nullptr ? reason : "unspecified", stderr);
	std::fputc('\n', stderr);
	std::fflush(stderr);
#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	TerminateProcess(GetCurrentProcess(), static_cast<UINT>(EXCEPTION_NONCONTINUABLE_EXCEPTION));
#endif
	std::_Exit(321);
}

class FilterScope final {
public:
	FilterScope() noexcept {
		if (g_in_exception_filter) {
			FailFast("nested exception while resolving a host fault");
		}
		g_in_exception_filter = true;
	}

	~FilterScope() { g_in_exception_filter = false; }

	KYTY_CLASS_NO_COPY(FilterScope);
};

static Handler LoadInstalledHandler() noexcept {
	if (g_install_state.load(std::memory_order_acquire) == 0) {
		FailFast("host exception handler is not installed");
	}

	const auto handler = g_handler.load(std::memory_order_acquire);
	if (handler == nullptr) {
		FailFast("host exception callback is null");
	}
	return handler;
}
#endif

#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS

static LONG WINAPI ExceptionFilter(PEXCEPTION_POINTERS exception) {
	FilterScope filter_scope;

	auto* exception_record = exception->ExceptionRecord;

	if (exception_record->ExceptionCode == DBG_PRINTEXCEPTION_C ||
	    exception_record->ExceptionCode == DBG_PRINTEXCEPTION_WIDE_C) {
		return EXCEPTION_CONTINUE_SEARCH;
	}

	if (exception_record->ExceptionCode == 0x406D1388) {
		// Set a thread name.
		return EXCEPTION_CONTINUE_EXECUTION;
	}

	ExceptionInfo info {};
	info.exception_address = reinterpret_cast<uint64_t>(exception_record->ExceptionAddress);
	info.native_code       = exception_record->ExceptionCode;
	info.native_context    = exception->ContextRecord;

	if (exception_record->ExceptionCode == EXCEPTION_ACCESS_VIOLATION) {
		info.type = ExceptionType::AccessViolation;
		switch (exception_record->ExceptionInformation[0]) {
			case 0: info.access_violation_type = AccessViolationType::Read; break;
			case 1: info.access_violation_type = AccessViolationType::Write; break;
			case 8: info.access_violation_type = AccessViolationType::Execute; break;
			default: info.access_violation_type = AccessViolationType::Unknown; break;
		}
		info.access_violation_vaddr = exception_record->ExceptionInformation[1];
	} else if (exception_record->ExceptionCode == EXCEPTION_ILLEGAL_INSTRUCTION) {
		info.type = ExceptionType::IllegalInstruction;
	} else {
		printf("Unhandled win exception: code=0x%08" PRIx32 ", addr=0x%016" PRIx64
		       ", rip=0x%016" PRIx64 ", rsp=0x%016" PRIx64 ", rbp=0x%016" PRIx64 "\n",
		       static_cast<uint32_t>(exception_record->ExceptionCode),
		       reinterpret_cast<uint64_t>(exception_record->ExceptionAddress),
		       exception->ContextRecord->Rip, exception->ContextRecord->Rsp,
		       exception->ContextRecord->Rbp);
		return EXCEPTION_CONTINUE_SEARCH;
	}

	info.rax = exception->ContextRecord->Rax;
	info.rbx = exception->ContextRecord->Rbx;
	info.rcx = exception->ContextRecord->Rcx;
	info.rdx = exception->ContextRecord->Rdx;
	info.rsi = exception->ContextRecord->Rsi;
	info.rdi = exception->ContextRecord->Rdi;
	info.rbp = exception->ContextRecord->Rbp;
	info.rsp = exception->ContextRecord->Rsp;
	info.r8  = exception->ContextRecord->R8;
	info.r9  = exception->ContextRecord->R9;
	info.r10 = exception->ContextRecord->R10;
	info.r11 = exception->ContextRecord->R11;
	info.r12 = exception->ContextRecord->R12;
	info.r13 = exception->ContextRecord->R13;
	info.r14 = exception->ContextRecord->R14;
	info.r15 = exception->ContextRecord->R15;

	const auto handler = LoadInstalledHandler();

	return handler(info) ? EXCEPTION_CONTINUE_EXECUTION : EXCEPTION_CONTINUE_SEARCH;
}

// Windows delivers faults through SEH on the faulting thread's own stack, so there is no
// alternate signal stack to install and nothing for callers to do. Defined rather than
// omitted so the shared call site in InstallHandler links on every platform.
void InstallSignalStack() {}

#elif defined(__APPLE__)

static std::atomic<Handler> g_handler {nullptr};
static std::atomic_uint32_t g_install_state {0};
static thread_local bool    g_in_exception_filter = false;

// Each thread needs its own alternate signal stack for SA_ONSTACK to mean anything.
static thread_local std::unique_ptr<uint8_t[]> g_signal_stack;

void InstallSignalStack() {
	if (g_signal_stack != nullptr) {
		return;
	}
	constexpr size_t SIZE = 1u << 20u;
	g_signal_stack        = std::make_unique<uint8_t[]>(SIZE);
	stack_t ss {};
	ss.ss_sp    = g_signal_stack.get();
	ss.ss_size  = SIZE;
	ss.ss_flags = 0;
	::sigaltstack(&ss, nullptr);
}


static_assert(decltype(g_handler)::is_always_lock_free);
static_assert(decltype(g_install_state)::is_always_lock_free);

[[noreturn]] static void FailFast(const char* reason) noexcept {
	std::fputs("HostException fail-fast: ", stderr);
	std::fputs(reason != nullptr ? reason : "unspecified", stderr);
	std::fputc('\n', stderr);
	std::fflush(stderr);
	std::_Exit(321);
}

// Translate the host fault metadata into an access type.  The x86-64 page-fault
// error code uses bit 1 for writes and bit 4 for instruction fetches; arm64
// reports the equivalent information in ESR (WnR bit 6 and an instruction/data
// abort exception class).
#if defined(__aarch64__) || defined(__arm64__)
static AccessViolationType DecodeAccess(uint32_t esr) {
	const auto exception_class = (esr >> 26u) & 0x3fu;
	if (exception_class == 0x20u || exception_class == 0x21u) {
		return AccessViolationType::Execute;
	}
	if (exception_class == 0x24u || exception_class == 0x25u) {
		return (esr & (1u << 6u)) != 0 ? AccessViolationType::Write : AccessViolationType::Read;
	}
	return AccessViolationType::Read;
}
#else
static AccessViolationType DecodeAccess(uint64_t err) {
	if ((err & 0x10u) != 0) {
		return AccessViolationType::Execute;
	}
	if ((err & 0x2u) != 0) {
		return AccessViolationType::Write;
	}
	return AccessViolationType::Read;
}
#endif

// POSIX signal handler that mirrors the Windows vectored handler: build an ExceptionInfo
// from the mcontext and dispatch. A resolved fault (handler returns true) simply returns,
// re-executing the faulting instruction against the now-fixed protection. An unresolved
// fault restores the default disposition so the retry terminates the process.
static void SignalHandler(int sig, siginfo_t* si, void* uctx) {
	if (g_in_exception_filter) {
		FailFast("nested exception while resolving a host fault");
	}
	g_in_exception_filter = true;

	auto*       uc = static_cast<ucontext_t*>(uctx);
	const auto* mc = uc->uc_mcontext;
	#if defined(__aarch64__) || defined(__arm64__)
	const auto& es = mc->__es;
	const auto& ss = mc->__ss;
	#else
	const auto& ss = mc->__ss;
	#endif

	ExceptionInfo info {};
	#if defined(__aarch64__) || defined(__arm64__)
	info.exception_address = ss.__pc;
	#else
	info.exception_address = ss.__rip;
	#endif
	info.native_code       = static_cast<uint32_t>(si->si_code);
	info.native_context    = uctx;

	if (sig == SIGILL) {
		info.type = ExceptionType::IllegalInstruction;
	} else {
		info.type                   = ExceptionType::AccessViolation;
		#if defined(__aarch64__) || defined(__arm64__)
		info.access_violation_type  = DecodeAccess(es.__esr);
		#else
		info.access_violation_type  = DecodeAccess(mc->__es.__err);
		#endif
		info.access_violation_vaddr = reinterpret_cast<uint64_t>(si->si_addr);

		// The x86-64 error code is synthesized for a translated process, so the raw value is
		// worth seeing before trusting the decode.
		static const bool trace_err = [] {
			const char* v = std::getenv("KYTY_FAULT_ERR_TRACE");
			return v != nullptr && v[0] != '\0' && v[0] != '0';
		}();
		if (trace_err) {
			static uint64_t err_count = 0;
			if ((++err_count % 10000) == 1) {
				#if defined(__aarch64__) || defined(__arm64__)
				const uint64_t fault_detail = es.__esr;
				const uint64_t fault_pc     = ss.__pc;
				#else
				const uint64_t fault_detail = mc->__es.__err;
				const uint64_t fault_pc     = ss.__rip;
				#endif
				std::fprintf(stderr,
				             "[faulterr] n=%llu addr=0x%016llx err=0x%llx si_code=%d rip=0x%llx\n",
				             static_cast<unsigned long long>(err_count),
				             static_cast<unsigned long long>(info.access_violation_vaddr),
				             static_cast<unsigned long long>(fault_detail),
				             si->si_code,
				             static_cast<unsigned long long>(fault_pc));
				std::fflush(stderr);
			}
		}
	}

	#if defined(__aarch64__) || defined(__arm64__)
	// ExceptionInfo names these slots after x86 registers because the guest
	// emulator consumes them.  On native arm64, expose the corresponding
	// general-purpose registers and architectural frame/stack/program counters.
	info.rax = ss.__x[0];
	info.rbx = ss.__x[1];
	info.rcx = ss.__x[2];
	info.rdx = ss.__x[3];
	info.rsi = ss.__x[4];
	info.rdi = ss.__x[5];
	info.rbp = ss.__fp;
	info.rsp = ss.__sp;
	info.r8  = ss.__x[8];
	info.r9  = ss.__x[9];
	info.r10 = ss.__x[10];
	info.r11 = ss.__x[11];
	info.r12 = ss.__x[12];
	info.r13 = ss.__x[13];
	info.r14 = ss.__x[14];
	info.r15 = ss.__x[15];
	#else
	info.rax = ss.__rax;
	info.rbx = ss.__rbx;
	info.rcx = ss.__rcx;
	info.rdx = ss.__rdx;
	info.rsi = ss.__rsi;
	info.rdi = ss.__rdi;
	info.rbp = ss.__rbp;
	info.rsp = ss.__rsp;
	info.r8  = ss.__r8;
	info.r9  = ss.__r9;
	info.r10 = ss.__r10;
	info.r11 = ss.__r11;
	info.r12 = ss.__r12;
	info.r13 = ss.__r13;
	info.r14 = ss.__r14;
	info.r15 = ss.__r15;
	#endif

	const auto handler = g_handler.load(std::memory_order_acquire);
	if (handler == nullptr) {
		FailFast("host exception callback is null");
	}

	const bool resolved   = handler(info);
	g_in_exception_filter = false;

	if (resolved) {
		return; // retry the faulting instruction against the fixed mapping
	}

	// Unresolved: restore the default action so the re-executed instruction terminates.
	struct sigaction dfl {};
	dfl.sa_handler = SIG_DFL;
	sigemptyset(&dfl.sa_mask);
	sigaction(sig, &dfl, nullptr);
}

#else

// Linux installs its fault handler on the normal thread stack (without SA_ONSTACK), so callers
// share the cross-platform setup hook but have no per-thread signal-stack work to perform.
void InstallSignalStack() {}

// x86-64 page-fault error bits.
constexpr uint64_t PAGE_FAULT_ERROR_WRITE       = 0x02;
constexpr uint64_t PAGE_FAULT_ERROR_INSTRUCTION = 0x10;

// Let the kernel handle an unresolved fault on retry.
static void ChainToDefault(int signal_number) noexcept {
	struct sigaction restore {};
	restore.sa_handler = SIG_DFL;
	sigemptyset(&restore.sa_mask);
	restore.sa_flags = 0;
	::sigaction(signal_number, &restore, nullptr);
}

static void SignalHandler(int signal_number, siginfo_t* signal_info, void* native_context) {
	FilterScope filter_scope;

	auto* context = static_cast<ucontext_t*>(native_context);
	auto* gregs   = context->uc_mcontext.gregs;

	ExceptionInfo info {};
	info.exception_address = static_cast<uint64_t>(gregs[REG_RIP]);
	info.native_code       = static_cast<uint32_t>(signal_number);
	info.native_context    = context;

	if (signal_number == SIGSEGV || signal_number == SIGBUS) {
		info.type             = ExceptionType::AccessViolation;
		const auto error_code = static_cast<uint64_t>(gregs[REG_ERR]);
		if ((error_code & PAGE_FAULT_ERROR_INSTRUCTION) != 0) {
			info.access_violation_type = AccessViolationType::Execute;
		} else if ((error_code & PAGE_FAULT_ERROR_WRITE) != 0) {
			info.access_violation_type = AccessViolationType::Write;
		} else {
			info.access_violation_type = AccessViolationType::Read;
		}
		info.access_violation_vaddr = reinterpret_cast<uint64_t>(signal_info->si_addr);
	} else if (signal_number == SIGILL) {
		info.type = ExceptionType::IllegalInstruction;
	} else {
		ChainToDefault(signal_number);
		return;
	}

	info.rax = static_cast<uint64_t>(gregs[REG_RAX]);
	info.rbx = static_cast<uint64_t>(gregs[REG_RBX]);
	info.rcx = static_cast<uint64_t>(gregs[REG_RCX]);
	info.rdx = static_cast<uint64_t>(gregs[REG_RDX]);
	info.rsi = static_cast<uint64_t>(gregs[REG_RSI]);
	info.rdi = static_cast<uint64_t>(gregs[REG_RDI]);
	info.rbp = static_cast<uint64_t>(gregs[REG_RBP]);
	info.rsp = static_cast<uint64_t>(gregs[REG_RSP]);
	info.r8  = static_cast<uint64_t>(gregs[REG_R8]);
	info.r9  = static_cast<uint64_t>(gregs[REG_R9]);
	info.r10 = static_cast<uint64_t>(gregs[REG_R10]);
	info.r11 = static_cast<uint64_t>(gregs[REG_R11]);
	info.r12 = static_cast<uint64_t>(gregs[REG_R12]);
	info.r13 = static_cast<uint64_t>(gregs[REG_R13]);
	info.r14 = static_cast<uint64_t>(gregs[REG_R14]);
	info.r15 = static_cast<uint64_t>(gregs[REG_R15]);

	const auto handler = LoadInstalledHandler();

	if (handler(info)) {
		return;
	}

	ChainToDefault(signal_number);
}

#endif

bool InstallHandler(Handler handler) {
	if (handler == nullptr) {
		return false;
	}

	uint32_t expected_state = 0;
	if (!g_install_state.compare_exchange_strong(expected_state, 1, std::memory_order_acq_rel)) {
		return expected_state == 2 && g_handler.load(std::memory_order_acquire) == handler;
	}

	g_handler.store(handler, std::memory_order_release);

#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	if (AddVectoredExceptionHandler(1, ExceptionFilter) == nullptr) {
		g_handler.store(nullptr, std::memory_order_release);
		g_install_state.store(0, std::memory_order_release);
		printf("AddVectoredExceptionHandler() failed\n");
		return false;
	}
#elif defined(__APPLE__)
	struct sigaction sa {};
	sa.sa_sigaction = SignalHandler;
	// SA_ONSTACK is essential here, not a nicety. Guest code runs on stacks the *game* sizes —
	// Astro gives one fiber 2 KB — and this handler builds an ExceptionInfo, takes mutexes and
	// calls into the GPU resource manager. Run on the faulting stack it overruns it and corrupts
	// whatever sits below, including the kernel-saved context, so the resumed rip can land in the
	// middle of an instruction.
	sa.sa_flags     = SA_SIGINFO | SA_ONSTACK;
	sigemptyset(&sa.sa_mask);
	// The guest signal-dispatch path (KernelRaiseException) interrupts threads with
	// SIGUSR1; block it while a fault is being resolved so a stop-the-world request
	// cannot preempt the handler between the protection fix and the retry.
	sigaddset(&sa.sa_mask, SIGUSR1);

	// macOS raises SIGBUS for protection faults on some paths and SIGSEGV on others;
	// SIGILL covers instructions the host cannot execute (routed to the x64 emulator).
	InstallSignalStack();

	// The process self-aborts with nothing on stderr, nothing in the unified log, and an .ips that
	// carries no thread backtraces (normal for Rosetta). Catch SIGABRT ourselves purely to print
	// where it came from, then re-raise so the disposition is unchanged.
	{
		struct sigaction abrt {};
		abrt.sa_sigaction = [](int s, siginfo_t*, void* uctx) {
			// backtrace() cannot walk past _sigtramp under Rosetta, so follow the frame-pointer
			// chain out of the interrupted context by hand. Addresses are symbolized offline
			// against the image base (0x700000000000, zero slide).
			std::fputs("\n=== SIGABRT frames (rbp chain) ===\n", stderr);
			auto* uc = static_cast<ucontext_t*>(uctx);
			if (uc != nullptr && uc->uc_mcontext != nullptr) {
			#if defined(__aarch64__) || defined(__arm64__)
				const auto& ss = uc->uc_mcontext->__ss;
				std::fprintf(stderr, "pc=0x%llx fp=0x%llx sp=0x%llx\n",
				             (unsigned long long)ss.__pc, (unsigned long long)ss.__fp,
				             (unsigned long long)ss.__sp);
				auto frame = ss.__fp;
				for (int i = 0; i < 32 && frame > 0x1000; i++) {
					const auto* slots = reinterpret_cast<const uint64_t*>(frame);
					const auto  next  = slots[0];
					const auto  ret   = slots[1];
					if (ret == 0) {
						break;
					}
					std::fprintf(stderr, "  [%02d] 0x%016llx\n", i, (unsigned long long)ret);
					if (next <= frame) {
						break;
					}
					frame = next;
				}
			#else
				const auto& ss = uc->uc_mcontext->__ss;
				std::fprintf(stderr, "rip=0x%llx rbp=0x%llx rsp=0x%llx\n",
				             (unsigned long long)ss.__rip, (unsigned long long)ss.__rbp,
				             (unsigned long long)ss.__rsp);
				auto frame = ss.__rbp;
				for (int i = 0; i < 32 && frame > 0x1000; i++) {
					const auto* slots = reinterpret_cast<const uint64_t*>(frame);
					const auto  next  = slots[0];
					const auto  ret   = slots[1];
					if (ret == 0) {
						break;
					}
					std::fprintf(stderr, "  [%02d] 0x%016llx\n", i, (unsigned long long)ret);
					if (next <= frame) {
						break;
					}
					frame = next;
				}
			#endif
			}
			std::fflush(stderr);
			struct sigaction dfl {};
			dfl.sa_handler = SIG_DFL;
			sigemptyset(&dfl.sa_mask);
			::sigaction(s, &dfl, nullptr);
			::raise(s);
		};
		abrt.sa_flags = SA_SIGINFO | SA_ONSTACK;
		sigemptyset(&abrt.sa_mask);
		::sigaction(SIGABRT, &abrt, nullptr);
	}

	bool ok = sigaction(SIGSEGV, &sa, nullptr) == 0 && sigaction(SIGBUS, &sa, nullptr) == 0 &&
	          sigaction(SIGILL, &sa, nullptr) == 0;
	if (!ok) {
		g_handler.store(nullptr, std::memory_order_release);
		g_install_state.store(0, std::memory_order_release);
		printf("sigaction() failed to install the host fault handler\n");
		return false;
	}
#else
	struct sigaction action {};
	action.sa_sigaction = SignalHandler;
	sigemptyset(&action.sa_mask);
	// Fault resolution needs the normal thread stack.
	action.sa_flags = SA_SIGINFO | SA_RESTART;

	for (const int signal_number: {SIGSEGV, SIGBUS, SIGILL}) {
		if (::sigaction(signal_number, &action, nullptr) != 0) {
			g_handler.store(nullptr, std::memory_order_release);
			g_install_state.store(0, std::memory_order_release);
			printf("sigaction(%d) failed\n", signal_number);
			return false;
		}
	}
#endif

	g_install_state.store(2, std::memory_order_release);
	return true;
}

} // namespace Common::HostException
