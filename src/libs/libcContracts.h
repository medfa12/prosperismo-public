#ifndef EMULATOR_INCLUDE_EMULATOR_LIBS_LIBC_CONTRACTS_H_
#define EMULATOR_INCLUDE_EMULATOR_LIBS_LIBC_CONTRACTS_H_

#include <cstdio>

namespace Libs::LibcInternal {

// POSIX fflush(NULL) flushes all open output streams. Prosperismo currently
// exposes only the host stdout object to guests, so other non-null handles are
// invalid rather than an invitation to dereference an arbitrary guest pointer.
[[nodiscard]] constexpr bool FflushStreamIsSupported(const FILE* stream,
                                                     const FILE* stdout_stream) noexcept {
	return stream == nullptr || stream == stdout_stream;
}

} // namespace Libs::LibcInternal

#endif // EMULATOR_INCLUDE_EMULATOR_LIBS_LIBC_CONTRACTS_H_
