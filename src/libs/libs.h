#ifndef EMULATOR_INCLUDE_EMULATOR_LIBS_LIBS_H_
#define EMULATOR_INCLUDE_EMULATOR_LIBS_LIBS_H_

#include "common/abi.h"
#include "common/logging/log.h"
#include "common/stringUtils.h"
#include "common/threads.h"
#include "loader/timer.h" // IWYU pragma: keep

#include <cstdlib>

namespace Kyty::Libs {

// Each LIB_NAME() unit gets its own thread_local enable flag, so PRINT_NAME() tracing is normally
// visible only for the few units/threads that opt in explicitly. That makes the log misleading when
// diagnosing: a call site that never enabled tracing looks identical to one that was never reached.
// Setting KYTY_PRINT_NAME=1 in the environment starts every unit and thread enabled instead.
inline bool PrintNameDefault() {
	static const bool enabled = [] {
		const char* v = std::getenv("KYTY_PRINT_NAME");
		return v != nullptr && v[0] != '\0' && v[0] != '0';
	}();
	return enabled;
}

// Defined out of line in libs.cpp; see the comment there for why this must not be inlined.
void PrintNameImpl(const char* library, const char* module, const char* func);

} // namespace Kyty::Libs

// NOLINTNEXTLINE(cppcoreguidelines-macro-usage)
#define PRINT_NAME_ENABLED g_print_name

// NOLINTNEXTLINE(cppcoreguidelines-macro-usage)
#define PRINT_NAME_ENABLE(flag) PRINT_NAME_ENABLED = flag;

// NOLINTNEXTLINE(cppcoreguidelines-macro-usage)
#define LIB_DEFINE(name) void name(Loader::SymbolDatabase* s)
// NOLINTNEXTLINE(cppcoreguidelines-macro-usage)
#define LIB_NAME(l, m)                                                                             \
	[[maybe_unused]] static thread_local bool PRINT_NAME_ENABLED =                                  \
	    ::Kyty::Libs::PrintNameDefault();                                                          \
	static constexpr char                     g_library[]        = l;                              \
	static constexpr char                     g_module[]         = m;
// NOLINTNEXTLINE(cppcoreguidelines-macro-usage)
#define LIB_VERSION(l, lv, m, mv1, mv2)                                                            \
	LIB_NAME(l, m);                                                                                \
	static constexpr int g_library_version      = lv;                                              \
	static constexpr int g_module_version_major = mv1;                                             \
	static constexpr int g_module_version_minor = mv2;
// NOLINTNEXTLINE(cppcoreguidelines-macro-usage)
#define LIB_USING(n)                                                                               \
	using n::g_library;                                                                            \
	using n::g_library_version;                                                                    \
	using n::g_module;                                                                             \
	using n::g_module_version_major;                                                               \
	using n::g_module_version_minor;
// NOLINTNEXTLINE(cppcoreguidelines-macro-usage)
#define LIB_LOAD(name) name(s)
// NOLINTNEXTLINE(cppcoreguidelines-macro-usage)
#define LIB_ADD(n, f, t)                                                                           \
	{                                                                                              \
		Loader::SymbolResolve sr {};                                                               \
		sr.name                 = n;                                                               \
		sr.library              = g_library;                                                       \
		sr.library_version      = g_library_version;                                               \
		sr.module               = g_module;                                                        \
		sr.module_version_major = g_module_version_major;                                          \
		sr.module_version_minor = g_module_version_minor;                                          \
		sr.type                 = t;                                                               \
		auto        func        = reinterpret_cast<uint64_t>(f);                                   \
		const char* dbg_name    = "" #f;                                                           \
		s->Add(sr, func, dbg_name);                                                                \
	}
// NOLINTNEXTLINE(cppcoreguidelines-macro-usage)
#define LIB_OBJECT(n, f) LIB_ADD(n, f, Loader::SymbolType::Object)
// NOLINTNEXTLINE(cppcoreguidelines-macro-usage)
#define LIB_FUNC(n, f) LIB_ADD(n, f, Loader::SymbolType::Func)

// NOLINTNEXTLINE(cppcoreguidelines-macro-usage)
#define PRINT_NAME()                                                                               \
	do {                                                                                           \
		if (PRINT_NAME_ENABLED) {                                                                  \
			::Kyty::Libs::PrintNameImpl(g_library, g_module, __func__);                            \
		}                                                                                          \
	} while (false)

namespace Loader {
class SymbolDatabase;
} // namespace Loader

namespace Libs {

void InitAll(Loader::SymbolDatabase* s);

} // namespace Libs
#endif /* EMULATOR_INCLUDE_EMULATOR_LIBS_LIBS_H_ */
