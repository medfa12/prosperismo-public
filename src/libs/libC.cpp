#include "common/abi.h"
#include "common/assert.h"
#include "common/common.h"
#include "common/logging/log.h"
#include "common/singleton.h"
#include "common/stringUtils.h"
#include "graphics/host_gpu/hostMemory.h"
#include "kernel/pthread.h"
#include "libs/errno.h"
#include "libs/guestPrintf.h"
#include "libs/libcContracts.h"
#include "libs/libs.h"
#include "libs/vaContext.h"
#include "loader/runtimeLinker.h"
#include "loader/symbolDatabase.h"

#include <algorithm>
#include <cctype>
#include <chrono>
#include <cinttypes>
#include <cmath>
#include <condition_variable>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <fmt/format.h>
#include <list>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace Libs {

namespace LibKernel {
void KernelDispatchPendingSignalForCurrentThread();
} // namespace LibKernel

namespace LibC {

LIB_VERSION("libc", 1, "libc", 1, 1);

static uint32_t g_need_flag = 1;

using cxa_destructor_func_t = KYTY_SYSV_ABI void (*)(void*);
using atexit_func_t         = KYTY_SYSV_ABI void (*)();

struct CxaDestructor {
	cxa_destructor_func_t destructor_func;
	void*                 destructor_object;
	void*                 module_id;
};

struct CContext {
	std::list<CxaDestructor> cxa;
	std::list<atexit_func_t> atexit;
};

struct InitEnvParams {
	int         argc;
	uint32_t    pad;
	const char* argv[3];
};

static int                g_argc = 0;
static const char* const* g_argv = nullptr;
static const char* const* g_envp = nullptr;

int GetArgc() {
	return g_argc;
}

const char** GetArgv() {
	return const_cast<const char**>(g_argv);
}

static KYTY_SYSV_ABI void exit(int code) {
	PRINT_NAME();

	::exit(code);
}

static void PrintAbortStringCandidate(const char* name, uint64_t addr) {
	if (!Graphics::HostMemoryIsReadable(addr)) {
		return;
	}

	const auto* ptr = reinterpret_cast<const char*>(static_cast<uintptr_t>(addr));
	char        buf[257] {};
	size_t      len = 0;

	for (; len < sizeof(buf) - 1 && Graphics::HostMemoryIsReadable(addr + len); len++) {
		const auto c = static_cast<unsigned char>(ptr[len]);
		if (c == 0) {
			break;
		}
		if (!std::isprint(c) && c != '\n' && c != '\r' && c != '\t') {
			return;
		}
		buf[len] = static_cast<char>(c);
	}

	if (len >= 4) {
		LOGF("\t %s string = \"%s\"\n", name, buf);
	}
}

static void PrintAbortWideStringCandidate(const char* name, uint64_t addr) {
	if (!Graphics::HostMemoryIsReadable(addr)) {
		return;
	}

	const auto* ptr = reinterpret_cast<const uint16_t*>(static_cast<uintptr_t>(addr));
	char        buf[257] {};
	size_t      len = 0;

	for (;
	     len < sizeof(buf) - 1 && Graphics::HostMemoryIsReadable(addr + len * sizeof(uint16_t) + 1);
	     len++) {
		const auto c = ptr[len];
		if (c == 0) {
			break;
		}
		if (c > 0x7f ||
		    (!std::isprint(static_cast<unsigned char>(c)) && c != '\n' && c != '\r' && c != '\t')) {
			return;
		}
		buf[len] = static_cast<char>(c);
	}

	if (len >= 4) {
		LOGF("\t %s u16 = \"%s\"\n", name, buf);
	}
}

static void PrintAbortBytesCandidate(const char* name, uint64_t addr) {
	if (!Graphics::HostMemoryIsReadable(addr)) {
		return;
	}

	uint8_t bytes[64] {};
	size_t  len = 0;

	for (; len < sizeof(bytes) && Graphics::HostMemoryIsReadable(addr + len); len++) {
		bytes[len] = *reinterpret_cast<const uint8_t*>(static_cast<uintptr_t>(addr + len));
	}

	if (len == 0) {
		return;
	}

	LOGF("\t %s bytes =", name);
	for (size_t i = 0; i < len; i++) {
		LOGF(" %02" PRIx8, bytes[i]);
	}
	LOGF("\n");
}

static void PrintAbortPointerCandidate(const char* name, uint64_t addr) {
	PrintAbortStringCandidate(name, addr);
	PrintAbortWideStringCandidate(name, addr);
	PrintAbortBytesCandidate(name, addr);
}

static bool LooksLikeAbortPointer(uint64_t addr) {
	return Graphics::HostMemoryIsReadable(addr) && addr >= 0x10000;
}

static void PrintAbortPointerArrayCandidate(const char* name, uint64_t addr) {
	if (!LooksLikeAbortPointer(addr)) {
		return;
	}

	for (int i = 0; i < 8; i++) {
		const auto slot_addr = addr + static_cast<uint64_t>(i) * sizeof(uint64_t);
		if (!Graphics::HostMemoryIsReadable(slot_addr)) {
			break;
		}

		const auto value = *reinterpret_cast<const uint64_t*>(static_cast<uintptr_t>(slot_addr));
		if (!LooksLikeAbortPointer(value)) {
			continue;
		}

		LOGF("\t %s[%d] = 0x%016" PRIx64 "\n", name, i, value);
		const auto child_name = fmt::format("{}[{}]", name, i);
		PrintAbortPointerCandidate(child_name.c_str(), value);
	}
}

[[noreturn]] static KYTY_SYSV_ABI void abort(uint64_t arg0, uint64_t arg1, uint64_t arg2,
                                             uint64_t arg3, uint64_t arg4, uint64_t arg5) {
	PRINT_NAME();

	uint64_t rbp = 0;
	uint64_t rsp = 0;
	// This assembly is x86-64-specific, not Windows-specific.
#if defined(__x86_64__) || defined(_M_X64)
	asm volatile("movq %%rbp, %0" : "=r"(rbp));
	asm volatile("movq %%rsp, %0" : "=r"(rsp));
#endif

	const auto ret = reinterpret_cast<uint64_t>(__builtin_return_address(0));

	LOGF("Guest abort diagnostics:\n"
	     "\t return = 0x%016" PRIx64 "\n"
	     "\t rbp    = 0x%016" PRIx64 "\n"
	     "\t rsp    = 0x%016" PRIx64 "\n"
	     "\t arg0   = 0x%016" PRIx64 "\n"
	     "\t arg1   = 0x%016" PRIx64 "\n"
	     "\t arg2   = 0x%016" PRIx64 "\n"
	     "\t arg3   = 0x%016" PRIx64 "\n"
	     "\t arg4   = 0x%016" PRIx64 "\n"
	     "\t arg5   = 0x%016" PRIx64 "\n",
	     ret, rbp, rsp, arg0, arg1, arg2, arg3, arg4, arg5);

	PrintAbortPointerCandidate("arg0", arg0);
	PrintAbortPointerCandidate("arg1", arg1);
	PrintAbortPointerCandidate("arg2", arg2);
	PrintAbortPointerCandidate("arg3", arg3);
	PrintAbortPointerCandidate("arg4", arg4);
	PrintAbortPointerCandidate("arg5", arg5);
	PrintAbortPointerArrayCandidate("arg0", arg0);
	PrintAbortPointerArrayCandidate("arg1", arg1);
	PrintAbortPointerArrayCandidate("arg2", arg2);
	PrintAbortPointerArrayCandidate("arg3", arg3);
	PrintAbortPointerArrayCandidate("arg4", arg4);
	PrintAbortPointerArrayCandidate("arg5", arg5);

	if (rbp != 0) {
		Common::Singleton<Loader::RuntimeLinker>::Instance()->StackTrace(rbp);
	}

	if (rsp != 0) {
		LOGF("Guest abort stack words:\n");
		auto* linker = Common::Singleton<Loader::RuntimeLinker>::Instance();
		for (int i = 0; i < 16; i++) {
			const auto addr = rsp + static_cast<uint64_t>(i) * sizeof(uint64_t);
			if (!Graphics::HostMemoryIsReadable(addr)) {
				break;
			}
			const auto value   = *reinterpret_cast<const uint64_t*>(static_cast<uintptr_t>(addr));
			auto*      program = linker->FindProgramByAddr(value);
			if (program != nullptr) {
				auto module_name = Common::PathToString(program->file_name.filename());
				LOGF("\t [%02d] 0x%016" PRIx64 " %s+0x%016" PRIx64 "\n", i, value,
				     module_name.c_str(), value - program->base_vaddr);
			} else {
				LOGF("\t [%02d] 0x%016" PRIx64 "\n", i, value);
			}
			PrintAbortPointerCandidate(fmt::format("stack[{:02d}]", i).c_str(), value);
			PrintAbortPointerArrayCandidate(fmt::format("stack[{:02d}]", i).c_str(), value);
		}
	}

	EXIT("Guest abort()\n");
	std::abort();
}

static KYTY_SYSV_ABI int* libc_error() {
	PRINT_NAME();

	return Posix::GetErrorAddr();
}

static KYTY_SYSV_ABI void init_env(const InitEnvParams* params) {
	PRINT_NAME();

	if (params == nullptr) {
		g_argc = 0;
		g_argv = nullptr;
		g_envp = nullptr;
		return;
	}

	constexpr int argv_capacity = static_cast<int>(sizeof(params->argv) / sizeof(params->argv[0]));

	EXIT_NOT_IMPLEMENTED(params->argc < 0 || params->argc >= argv_capacity);

	g_argc = params->argc;
	g_argv = params->argv;
	g_envp = params->argv + params->argc + 1;

	LOGF("\t argc = %d\n"
	     "\t argv = 0x%016" PRIx64 "\n"
	     "\t envp = 0x%016" PRIx64 "\n",
	     g_argc, reinterpret_cast<uint64_t>(g_argv), reinterpret_cast<uint64_t>(g_envp));

	for (int i = 0; i < g_argc; i++) {
		LOGF("\t argv[%d] = %s\n", i, g_argv[i] != nullptr ? g_argv[i] : "<null>");
	}
}

static KYTY_SYSV_ABI int atexit(atexit_func_t func) {
	PRINT_NAME();

	if (func != nullptr) {
		Common::Singleton<CContext>::Instance()->atexit.push_front(func);
	}

	return 0;
}

static KYTY_SYSV_ABI int libc_printf(VA_ARGS) {
	VA_CONTEXT(ctx); // NOLINT(cppcoreguidelines-pro-type-member-init,hicpp-member-init)

	PRINT_NAME();

	return GetGuestPrintfCtxFunc()(&ctx);
}

static KYTY_SYSV_ABI int puts(const char* s) {
	PRINT_NAME();

	return GetGuestPrintfStdFunc()("%s\n", s);
}

static KYTY_SYSV_ABI int setenv(const char* name, const char* value, int overwrite) {
	PRINT_NAME();

	LOGF("\t name      = %s\n"
	     "\t value     = %s\n"
	     "\t overwrite = %d\n",
	     (name != nullptr ? name : "<null>"), (value != nullptr ? value : "<null>"), overwrite);

	if (name == nullptr || value == nullptr || name[0] == '\0' ||
	    std::strchr(name, '=') != nullptr) {
		*Posix::GetErrorAddr() = Posix::POSIX_EINVAL;
		return -1;
	}

	if (overwrite == 0 && std::getenv(name) != nullptr) {
		return 0;
	}

#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	const auto ret = ::_putenv_s(name, value);
	if (ret != 0) {
		*Posix::GetErrorAddr() = ret;
		return -1;
	}
	return 0;
#else
	const auto ret = ::setenv(name, value, overwrite);
	if (ret != 0) {
		*Posix::GetErrorAddr() = errno;
		return -1;
	}
	return 0;
#endif
}

static KYTY_SYSV_ABI int64_t libc_time(int64_t* timer) {
	const auto now = static_cast<int64_t>(std::time(nullptr));
	if (timer != nullptr) {
		*timer = now;
	}
	return now;
}

static KYTY_SYSV_ABI double libc_difftime(int64_t time1, int64_t time0) {
	return std::difftime(static_cast<std::time_t>(time1), static_cast<std::time_t>(time0));
}

static KYTY_SYSV_ABI std::tm* libc_gmtime(const int64_t* timer) {
	if (timer == nullptr) {
		return nullptr;
	}

	thread_local std::tm result {};
	const auto           t = static_cast<std::time_t>(*timer);

#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	if (_gmtime64_s(&result, &t) != 0) {
		return nullptr;
	}
#else
	if (gmtime_r(&t, &result) == nullptr) {
		return nullptr;
	}
#endif

	return &result;
}

static KYTY_SYSV_ABI std::tm* libc_localtime(const int64_t* timer) {
	if (timer == nullptr) {
		return nullptr;
	}

	thread_local std::tm result {};
	const auto           t = static_cast<std::time_t>(*timer);

#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	if (_localtime64_s(&result, &t) != 0) {
		return nullptr;
	}
#else
	if (localtime_r(&t, &result) == nullptr) {
		return nullptr;
	}
#endif

	return &result;
}

static KYTY_SYSV_ABI int64_t libc_mktime(std::tm* timeptr) {
	if (timeptr == nullptr) {
		return -1;
	}

#if KYTY_PLATFORM == KYTY_PLATFORM_WINDOWS
	return static_cast<int64_t>(_mktime64(timeptr));
#else
	return static_cast<int64_t>(std::mktime(timeptr));
#endif
}

static KYTY_SYSV_ABI size_t libc_strftime(char* str, size_t count, const char* format,
                                          const std::tm* timeptr) {
	if (str == nullptr || format == nullptr || timeptr == nullptr) {
		return 0;
	}

#if KYTY_PLATFORM == KYTY_PLATFORM_LINUX && !defined(__APPLE__)
	// Guest-created tm values do not initialize glibc's tm_zone pointer.
	std::tm sanitized = *timeptr;
	sanitized.tm_zone = nullptr;

	return std::strftime(str, count, format, &sanitized);
#else
	return std::strftime(str, count, format, timeptr);
#endif
}

static KYTY_SYSV_ABI void catchReturnFromMain(int status) {
	PRINT_NAME();

	uint64_t ret_addr = reinterpret_cast<uint64_t>(__builtin_return_address(0));
	uint64_t rsp      = 0;
	uint64_t rbp      = 0;
#if defined(__x86_64__) || defined(_M_X64)
	asm volatile("movq %%rsp, %0\n\t"
	             "movq %%rbp, %1\n\t"
	             : "=r"(rsp), "=r"(rbp));
#endif

	LOGF("\t status     = %d\n"
	     "\t ret_addr   = 0x%016" PRIx64 "\n"
	     "\t rsp        = 0x%016" PRIx64 "\n"
	     "\t rbp        = 0x%016" PRIx64 "\n",
	     status, ret_addr, rsp, rbp);
	if (rsp != 0) {
		auto* stack = reinterpret_cast<const uint64_t*>(rsp);
		for (uint32_t i = 0; i < 12; i++) {
			LOGF("\t stack[%02" PRIu32 "] = 0x%016" PRIx64 "\n", i, stack[i]);
		}
	}

	::printf("return from main = %d\n", status);
}

enum class ThrdResult : int {
	Success  = 0,
	NoMem    = 1,
	TimedOut = 2,
	Busy     = 3,
	Error    = 4,
	Perm     = 5,
};

static int mtx_result(int result) {
	return (result == OK ? static_cast<int>(ThrdResult::Success)
	                     : static_cast<int>(ThrdResult::Error));
}

static KYTY_SYSV_ABI int MtxInit(LibKernel::PthreadMutex* mtx, int type) {
	PRINT_NAME();

	if (mtx == nullptr) {
		return static_cast<int>(ThrdResult::Error);
	}

	constexpr int mtx_recursive = 0x100;
	if ((type & mtx_recursive) == 0) {
		return mtx_result(LibKernel::PthreadMutexInit(mtx, nullptr));
	}

	LibKernel::PthreadMutexattr attr   = nullptr;
	int                         result = LibKernel::PthreadMutexattrInit(&attr);
	result = (result == OK ? LibKernel::PthreadMutexattrSettype(&attr, 2) : result);
	result = (result == OK ? LibKernel::PthreadMutexInit(mtx, &attr) : result);
	(void)LibKernel::PthreadMutexattrDestroy(&attr);

	return mtx_result(result);
}

static KYTY_SYSV_ABI int MtxInitWithName(LibKernel::PthreadMutex* mtx, int type,
                                         const char* /*name*/) {
	PRINT_NAME();

	return MtxInit(mtx, type);
}

static KYTY_SYSV_ABI int MtxInitWithDefaultNameOverride(LibKernel::PthreadMutex* mtx, int type,
                                                        const char* /*name*/) {
	PRINT_NAME();

	return MtxInit(mtx, type);
}

static KYTY_SYSV_ABI void MtxDestroy(LibKernel::PthreadMutex* mtx) {
	PRINT_NAME();

	if (mtx != nullptr) {
		(void)LibKernel::PthreadMutexDestroy(mtx);
	}
}

static KYTY_SYSV_ABI int MtxLock(LibKernel::PthreadMutex* mtx) {
	PRINT_NAME();

	return mtx_result(LibKernel::PthreadMutexLock(mtx));
}

static KYTY_SYSV_ABI int MtxTrylock(LibKernel::PthreadMutex* mtx) {
	PRINT_NAME();

	const auto result = LibKernel::PthreadMutexTrylock(mtx);
	if (result == OK) {
		return static_cast<int>(ThrdResult::Success);
	}
	return static_cast<int>(ThrdResult::Busy);
}

static KYTY_SYSV_ABI int MtxTimedlock(LibKernel::PthreadMutex* mtx, const void* /*xtime*/) {
	PRINT_NAME();

	return mtx_result(LibKernel::PthreadMutexLock(mtx));
}

static KYTY_SYSV_ABI int MtxUnlock(LibKernel::PthreadMutex* mtx) {
	PRINT_NAME();

	return mtx_result(LibKernel::PthreadMutexUnlock(mtx));
}

static KYTY_SYSV_ABI int MtxCurrentOwns(LibKernel::PthreadMutex* /*mtx*/) {
	PRINT_NAME();

	return 0;
}

using execute_once_func_t = KYTY_SYSV_ABI int (*)(void*, void*, void**);

static std::mutex              g_execute_once_mutex;
static std::condition_variable g_execute_once_cv;

static KYTY_SYSV_ABI int std_execute_once(int* flag, execute_once_func_t func, void* arg) {
	PRINT_NAME();

	if (flag == nullptr || func == nullptr) {
		return 0;
	}

	constexpr int once_init    = 0;
	constexpr int once_done    = 1;
	constexpr int once_running = 2;

	{
		std::unique_lock lock(g_execute_once_mutex);
		while (*flag == once_running) {
			g_execute_once_cv.wait_for(lock, std::chrono::microseconds(10000));
			if (*flag == once_running) {
				lock.unlock();
				LibKernel::KernelDispatchPendingSignalForCurrentThread();
				lock.lock();
			}
		}
		if (*flag == once_done) {
			return 1;
		}
		*flag = once_running;
	}

	void* callback_context = nullptr;
	int   result           = func(nullptr, arg, &callback_context);

	{
		std::lock_guard lock(g_execute_once_mutex);
		*flag = (result != 0 ? once_done : once_init);
	}
	g_execute_once_cv.notify_all();

	return result;
}

static KYTY_SYSV_ABI int cxa_atexit(cxa_destructor_func_t func, void* arg, void* d) {
	PRINT_NAME();

	auto* cc = Common::Singleton<CContext>::Instance();

	CxaDestructor c {};
	c.destructor_func   = func;
	c.destructor_object = arg;
	c.module_id         = d;

	cc->cxa.push_back(c);

	return 0;
}

void KYTY_SYSV_ABI cxa_finalize(void* d) {
	PRINT_NAME();

	auto* cc = Common::Singleton<CContext>::Instance();

	for (auto i = cc->cxa.rbegin(); i != cc->cxa.rend(); ++i) {
		auto& c = *i;
		if ((d == nullptr || c.module_id == d) && c.destructor_func != nullptr) {
			auto* func        = c.destructor_func;
			auto* object      = c.destructor_object;
			c.destructor_func = nullptr;
			func(object);
		}
	}
}

} // namespace LibC

namespace LibcInternalExt {

LIB_VERSION("LibcInternalExt", 1, "LibcInternal", 1, 1);

static uint64_t g_mspace_atomic_id_mask = 0;
static uint64_t g_mstate_table[64]      = {0};

using thread_atexit_destructor_t = KYTY_SYSV_ABI void (*)(void*);

struct ThreadAtexitDestructor {
	thread_atexit_destructor_t destructor;
	void*                      object;
};

static thread_local std::vector<ThreadAtexitDestructor> g_thread_atexit_destructors;

struct Info {
	uint64_t  size;
	uint32_t  unknown1;
	uint32_t  unknown2;
	uint64_t* mspace_atomic_id_mask;
	uint64_t* mstate_table;
};

void KYTY_SYSV_ABI LibcHeapGetTraceInfo(Info* info) {
	PRINT_NAME();

	EXIT_NOT_IMPLEMENTED(info->size != 32);

	info->mspace_atomic_id_mask = &g_mspace_atomic_id_mask;
	info->mstate_table          = g_mstate_table;
}

int KYTY_SYSV_ABI LibcInternalExtCxaThreadAtexit(thread_atexit_destructor_t destructor, void* object,
                                                 void* /*module_id*/) {
	PRINT_NAME();

	g_thread_atexit_destructors.push_back({destructor, object});

	return 0;
}

void RunThreadAtexitDestructors() {
	while (!g_thread_atexit_destructors.empty()) {
		auto destructor = g_thread_atexit_destructors.back();
		g_thread_atexit_destructors.pop_back();

		if (destructor.destructor != nullptr) {
			destructor.destructor(destructor.object);
		}
	}
}

LIB_DEFINE(InitLibcInternalExt_1) {
	LIB_FUNC("NWtTN10cJzE", LibcInternalExt::LibcHeapGetTraceInfo);
	LIB_FUNC("qBS714-Jr3g", LibcInternalExt::LibcInternalExtCxaThreadAtexit);
}

} // namespace LibcInternalExt

namespace LibcInternal {

LIB_VERSION("LibcInternal", 1, "LibcInternal", 1, 1);

static uint32_t g_need_flag = 1;

int KYTY_SYSV_ABI vprintf(const char* str, VaList* c) {
	PRINT_NAME();

	return GetGuestVprintfFunc()(str, c);
}

static KYTY_SYSV_ABI int snprintf(VA_ARGS) {
	VA_CONTEXT(ctx); // NOLINT(cppcoreguidelines-pro-type-member-init,hicpp-member-init)

	PRINT_NAME();

	return GetGuestSnprintfCtxFunc()(&ctx);
}

int KYTY_SYSV_ABI fflush(FILE* stream) {
	PRINT_NAME();

	if (!FflushStreamIsSupported(stream, stdout)) {
		*Posix::GetErrorAddr() = Posix::POSIX_EBADF;
		return EOF;
	}

	return ::fflush(stream);
}

void* KYTY_SYSV_ABI memset(void* s, int c, size_t n) {
	PRINT_NAME();

	return ::memset(s, c, n);
}

void* KYTY_SYSV_ABI memcpy(void* dest, const void* src, size_t n) {
	return ::memcpy(dest, src, n);
}

void* KYTY_SYSV_ABI memmove(void* dest, const void* src, size_t n) {
	return ::memmove(dest, src, n);
}

int KYTY_SYSV_ABI memcmp(const void* s1, const void* s2, size_t n) {
	return ::memcmp(s1, s2, n);
}

int KYTY_SYSV_ABI strcmp(const char* s1, const char* s2) {
	return ::strcmp(s1, s2);
}

int KYTY_SYSV_ABI strncmp(const char* s1, const char* s2, size_t n) {
	return ::strncmp(s1, s2, n);
}

size_t KYTY_SYSV_ABI strlen(const char* s) {
	return ::strlen(s);
}

char* KYTY_SYSV_ABI strcpy(char* dest, const char* src) {
	return ::strcpy(dest, src);
}

char* KYTY_SYSV_ABI strncpy(char* dest, const char* src, size_t count) {
	return ::strncpy(dest, src, count);
}

char* KYTY_SYSV_ABI strcat(char* dest, const char* src) {
	return ::strcat(dest, src);
}

const char* KYTY_SYSV_ABI strchr(const char* s, int c) {
	return ::strchr(s, c);
}

char* KYTY_SYSV_ABI strrchr(const char* s, int c) {
	return const_cast<char*>(::strrchr(s, c));
}

char* KYTY_SYSV_ABI strstr(const char* haystack, const char* needle) {
	static int log_count = 0;
	if (log_count++ < 8) {
		LOGF("LibcInternal::strstr(\"%s\", \"%s\")\n", (haystack != nullptr ? haystack : "<null>"),
		     (needle != nullptr ? needle : "<null>"));
	}
	return const_cast<char*>(::strstr(haystack, needle));
}

long KYTY_SYSV_ABI strtol(const char* str, char** endptr, int base) {
	static int log_count = 0;
	if (log_count++ < 16) {
		LOGF("LibcInternal::strtol(\"%s\", base=%d)\n", (str != nullptr ? str : "<null>"), base);
	}
	return ::strtol(str, endptr, base);
}

unsigned long KYTY_SYSV_ABI strtoul(const char* str, char** endptr, int base) {
	static int log_count = 0;
	if (log_count++ < 16) {
		LOGF("LibcInternal::strtoul(\"%s\", base=%d)\n", (str != nullptr ? str : "<null>"), base);
	}
	return ::strtoul(str, endptr, base);
}

int KYTY_SYSV_ABI atoi(const char* str) {
	return ::atoi(str);
}

float KYTY_SYSV_ABI sinf(float x) {
	return std::sin(x);
}

float KYTY_SYSV_ABI cosf(float x) {
	return std::cos(x);
}

void KYTY_SYSV_ABI sincosf(float x, float* sinp, float* cosp) {
	if (sinp != nullptr) {
		*sinp = std::sin(x);
	}
	if (cosp != nullptr) {
		*cosp = std::cos(x);
	}
}

double KYTY_SYSV_ABI sin(double x) {
	return std::sin(x);
}

double KYTY_SYSV_ABI cos(double x) {
	return std::cos(x);
}

void KYTY_SYSV_ABI sincos(double x, double* sinp, double* cosp) {
	if (sinp != nullptr) {
		*sinp = std::sin(x);
	}
	if (cosp != nullptr) {
		*cosp = std::cos(x);
	}
}

int KYTY_SYSV_ABI LibcHeapErrorReportForGame(uint64_t msp, uint64_t ptr, uint64_t error,
                                             uint64_t arg3, uint64_t arg4, uint64_t arg5) {
	PRINT_NAME();

	LOGF("\t temporary: heap error report ignored, msp=0x%016" PRIx64 ", ptr=0x%016" PRIx64
	     ", error=0x%016" PRIx64 ", args=(0x%016" PRIx64 ",0x%016" PRIx64 ",0x%016" PRIx64 ")\n",
	     msp, ptr, error, arg3, arg4, arg5);

	return 0;
}

LIB_DEFINE(InitLibcInternal_1) {
	LibcInternalExt::InitLibcInternalExt_1(s);

	LIB_OBJECT("ZT4ODD2Ts9o", &LibcInternal::g_need_flag);
	LIB_OBJECT("2sWzhYqFH4E", stdout);

	LIB_FUNC("GMpvxPFW924", LibcInternal::vprintf);
	LIB_FUNC("MUjC4lbHrK4", LibcInternal::fflush);
	LIB_FUNC("8zTFvBIAIN8", LibcInternal::memset);
	LIB_FUNC("Q3VBxCXhUHs", LibcInternal::memcpy);
	LIB_FUNC("+P6FRGH4LfA", LibcInternal::memmove);
	LIB_FUNC("DfivPArhucg", LibcInternal::memcmp);
	LIB_FUNC("aesyjrHVWy4", LibcInternal::strcmp);
	LIB_FUNC("Ovb2dSJOAuE", LibcInternal::strncmp);
	LIB_FUNC("j4ViWNHEgww", LibcInternal::strlen);
	LIB_FUNC("5Xa2ACNECdo", LibcInternal::strcpy);
	LIB_FUNC("6sJWiWSRuqk", LibcInternal::strncpy);
	LIB_FUNC("Ls4tzzhimqQ", LibcInternal::strcat);
	LIB_FUNC("ob5xAW4ln-0", LibcInternal::strchr);
	LIB_FUNC("9yDWMxEFdJU", LibcInternal::strrchr);
	LIB_FUNC("viiwFMaNamA", LibcInternal::strstr);
	LIB_FUNC("mXlxhmLNMPg", LibcInternal::strtol);
	LIB_FUNC("QxmSHBCuKTk", LibcInternal::strtoul);
	LIB_FUNC("zlfEH8FmyUA", LibcInternal::strtoul);
	LIB_FUNC("fPxypibz2MY", LibcInternal::atoi);
	LIB_FUNC("Q4rRL34CEeE", LibcInternal::sinf);
	LIB_FUNC("-P6FNMzk2Kc", LibcInternal::cosf);
	LIB_FUNC("pztV4AF18iI", LibcInternal::sincosf);
	LIB_FUNC("H8ya2H00jbI", LibcInternal::sin);
	LIB_FUNC("2WE3BTYVwKM", LibcInternal::cos);
	LIB_FUNC("jMB7EFyu30Y", LibcInternal::sincos);
	LIB_FUNC("eLdDw6l0-bU", LibcInternal::snprintf);

	LIB_FUNC("L1SBTkC+Cvw", LibC::abort);
	LIB_FUNC("tsvEmnenz48", LibC::cxa_atexit);
	LIB_FUNC("H2e8t5ScQGc", LibC::cxa_finalize);
	LIB_FUNC("DiGVep5yB5w", LibC::std_execute_once);

	LIB_FUNC("al3JzFI9MQ0", LibcInternal::LibcHeapErrorReportForGame);
}

} // namespace LibcInternal

LIB_USING(LibC);

// Plain libc forwards. These were imported by titles (Astro imports 144 such symbols) but never
// registered, so the loader patched each to a stub. Semantics match the host exactly and the guest
// ABI is SysV, which is also the host ABI here, so forwarding is correct rather than approximate.
// Deliberately excluded: malloc/free/realloc and friends. The guest may hand those pointers to the
// GPU, so they must come from guest address space, not the host heap - implement them against the
// guest allocator, not like this.
namespace LibCMath {

static KYTY_SYSV_ABI float  acosf_(float x) { return ::acosf(x); }
static KYTY_SYSV_ABI float  asinf_(float x) { return ::asinf(x); }
static KYTY_SYSV_ABI float  atanf_(float x) { return ::atanf(x); }
static KYTY_SYSV_ABI float  atan2f_(float y, float x) { return ::atan2f(y, x); }
static KYTY_SYSV_ABI float  tanf_(float x) { return ::tanf(x); }
static KYTY_SYSV_ABI float  expf_(float x) { return ::expf(x); }
static KYTY_SYSV_ABI double exp2_(double x) { return ::exp2(x); }
static KYTY_SYSV_ABI float  exp2f_(float x) { return ::exp2f(x); }
static KYTY_SYSV_ABI float  logf_(float x) { return ::logf(x); }
static KYTY_SYSV_ABI float  log10f_(float x) { return ::log10f(x); }
static KYTY_SYSV_ABI float  logbf_(float x) { return ::logbf(x); }
static KYTY_SYSV_ABI double pow_(double x, double y) { return ::pow(x, y); }
static KYTY_SYSV_ABI float  powf_(float x, float y) { return ::powf(x, y); }
static KYTY_SYSV_ABI float  fmodf_(float x, float y) { return ::fmodf(x, y); }
static KYTY_SYSV_ABI float  ldexpf_(float x, int e) { return ::ldexpf(x, e); }
static KYTY_SYSV_ABI float  scalbnf_(float x, int e) { return ::scalbnf(x, e); }
static KYTY_SYSV_ABI float  roundf_(float x) { return ::roundf(x); }
static KYTY_SYSV_ABI int    isfinitef_(float x) { return static_cast<int>(std::isfinite(x)); }
static KYTY_SYSV_ABI int    isinff_(float x) { return static_cast<int>(std::isinf(x)); }
static KYTY_SYSV_ABI int    isnanf_(float x) { return static_cast<int>(std::isnan(x)); }

} // namespace LibCMath

namespace LibCStr {

static KYTY_SYSV_ABI char*  strcpy_(char* d, const char* s2) { return ::strcpy(d, s2); }
static KYTY_SYSV_ABI char*  strncat_(char* d, const char* s2, size_t n) { return ::strncat(d, s2, n); }
static KYTY_SYSV_ABI size_t strnlen_(const char* s2, size_t n) { return ::strnlen(s2, n); }
// strnstr is a BSD extension present on Darwin but not in the Linux or MinGW runtimes. Provide
// the same behaviour there: locate the first occurrence of the needle within at most len bytes
// of the haystack, never reading past that bound, and treat an empty needle as matching at
// position 0.
#if !defined(__APPLE__)
static const char* KytyStrnstr(const char* haystack, const char* needle, size_t len) {
	const auto needle_len = ::strnlen(needle, len);
	if (needle_len == 0) {
		return haystack;
	}
	if (needle_len > len) {
		return nullptr;
	}
	for (size_t i = 0; i + needle_len <= len; i++) {
		if (haystack[i] == '\0') {
			return nullptr;
		}
		if (::strncmp(haystack + i, needle, needle_len) == 0) {
			return haystack + i;
		}
	}
	return nullptr;
}
#else
static const char* KytyStrnstr(const char* haystack, const char* needle, size_t len) {
	return ::strnstr(haystack, needle, len);
}
#endif

static KYTY_SYSV_ABI char*  strnstr_(const char* h, const char* n, size_t len) { return const_cast<char*>(KytyStrnstr(h, n, len)); }
static KYTY_SYSV_ABI char*  strtok_(char* s2, const char* d) { return ::strtok(s2, d); }
static KYTY_SYSV_ABI char*  strerror_(int e) { return ::strerror(e); }
static KYTY_SYSV_ABI void*  memchr_(const void* s2, int c, size_t n) { return const_cast<void*>(::memchr(s2, c, n)); }
static KYTY_SYSV_ABI double atof_(const char* s2) { return ::atof(s2); }
static KYTY_SYSV_ABI double strtod_(const char* s2, char** e) { return ::strtod(s2, e); }
static KYTY_SYSV_ABI long long strtoll_(const char* s2, char** e, int b) { return ::strtoll(s2, e, b); }
static KYTY_SYSV_ABI intmax_t strtoimax_(const char* s2, char** e, int b) { return ::strtoimax(s2, e, b); }
static KYTY_SYSV_ABI uintmax_t strtoumax_(const char* s2, char** e, int b) { return ::strtoumax(s2, e, b); }
static KYTY_SYSV_ABI size_t wcstombs_(char* d, const wchar_t* s2, size_t n) { return ::wcstombs(d, s2, n); }
static KYTY_SYSV_ABI int    rand_() { return ::rand(); }

} // namespace LibCStr

LIB_DEFINE(InitLibC_1) {
	LibcInternal::InitLibcInternal_1(s);

	LIB_OBJECT("P330P3dFF68", &LibC::g_need_flag);

	LIB_FUNC("QI-x0SL8jhw", LibCMath::acosf_);
	LIB_FUNC("GZWjF-YIFFk", LibCMath::asinf_);
	LIB_FUNC("weDug8QD-lE", LibCMath::atanf_);
	LIB_FUNC("EH-x713A99c", LibCMath::atan2f_);
	LIB_FUNC("ZE6RNL+eLbk", LibCMath::tanf_);
	LIB_FUNC("8zsu04XNsZ4", LibCMath::expf_);
	LIB_FUNC("dnaeGXbjP6E", LibCMath::exp2_);
	LIB_FUNC("wuAQt-j+p4o", LibCMath::exp2f_);
	LIB_FUNC("RQXLbdT2lc4", LibCMath::logf_);
	LIB_FUNC("lhpd6Wk6ccs", LibCMath::log10f_);
	LIB_FUNC("RWqyr1OKuw4", LibCMath::logbf_);
	LIB_FUNC("9LCjpWyQ5Zc", LibCMath::pow_);
	LIB_FUNC("1D0H2KNjshE", LibCMath::powf_);
	LIB_FUNC("88Vv-AzHVj8", LibCMath::fmodf_);
	LIB_FUNC("kn0yiYeExgA", LibCMath::ldexpf_);
	LIB_FUNC("9fs1btfLoUs", LibCMath::scalbnf_);
	LIB_FUNC("DDHG1a6+3q0", LibCMath::roundf_);
	LIB_FUNC("Q8pvJimUWis", LibCMath::isfinitef_);
	LIB_FUNC("rDMyAf1Jhug", LibCMath::isinff_);
	LIB_FUNC("lA94ZgT+vMM", LibCMath::isnanf_);
	LIB_FUNC("kiZSXIWd9vg", LibCStr::strcpy_);
	LIB_FUNC("kHg45qPC6f0", LibCStr::strncat_);
	LIB_FUNC("5jNubw4vlAA", LibCStr::strnlen_);
	LIB_FUNC("Xnrfb2-WhVw", LibCStr::strnstr_);
	LIB_FUNC("oVkZ8W8-Q8A", LibCStr::strtok_);
	LIB_FUNC("RIa6GnWp+iU", LibCStr::strerror_);
	LIB_FUNC("8u8lPzUEq+U", LibCStr::memchr_);
	LIB_FUNC("SRI6S9B+-a4", LibCStr::atof_);
	LIB_FUNC("2vDqwBlpF-o", LibCStr::strtod_);
	LIB_FUNC("VOBg+iNwB-4", LibCStr::strtoll_);
	LIB_FUNC("q5MWYCDfu3c", LibCStr::strtoimax_);
	LIB_FUNC("QNyUWGXmXNc", LibCStr::strtoumax_);
	LIB_FUNC("v7S7LhP2OJc", LibCStr::wcstombs_);
	LIB_FUNC("cpCOXWMgha0", LibCStr::rand_);
	LIB_FUNC("uMei1W9uyNo", LibC::exit);
	LIB_FUNC("L1SBTkC+Cvw", LibC::abort);
	LIB_FUNC("9BcDykPmo1I", LibC::libc_error);
	LIB_FUNC("bzQExy189ZI", LibC::init_env);
	LIB_FUNC("8G2LB+A3rzg", LibC::atexit);
	LIB_FUNC("hcuQgD53UxM", LibC::libc_printf);
	LIB_FUNC("YQ0navp+YIc", LibC::puts);
	LIB_FUNC("M4YYbSFfJ8g", LibC::setenv);
	LIB_FUNC("wLlFkwG9UcQ", LibC::libc_time);
	LIB_FUNC("-VVn74ZyhEs", LibC::libc_difftime);
	LIB_FUNC("1mecP7RgI2A", LibC::libc_gmtime);
	LIB_FUNC("efhK-YSUYYQ", LibC::libc_localtime);
	LIB_FUNC("n7AepwR0s34", LibC::libc_mktime);
	LIB_FUNC("Av3zjWi64Kw", LibC::libc_strftime);
	LIB_FUNC("XKRegsFpEpk", LibC::catchReturnFromMain);
	LIB_FUNC("tsvEmnenz48", LibC::cxa_atexit);
	LIB_FUNC("H2e8t5ScQGc", LibC::cxa_finalize);
	LIB_FUNC("DiGVep5yB5w", LibC::std_execute_once);
	LIB_FUNC("YaHc3GS7y7g", LibC::MtxInit);
	LIB_FUNC("tgioGpKtmbE", LibC::MtxInitWithName);
	LIB_FUNC("JHp7ogc1+HY", LibC::MtxInitWithDefaultNameOverride);
	LIB_FUNC("5Lf51jvohTQ", LibC::MtxDestroy);
	LIB_FUNC("iS4aWbUonl0", LibC::MtxLock);
	LIB_FUNC("k6pGNMwJB08", LibC::MtxTrylock);
	LIB_FUNC("hPzYSd5Nasc", LibC::MtxTimedlock);
	LIB_FUNC("gTuXQwP9rrs", LibC::MtxUnlock);
	LIB_FUNC("VYQwFs4CC4Y", LibC::MtxCurrentOwns);
}

} // namespace Libs
