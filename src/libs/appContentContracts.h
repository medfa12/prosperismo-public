#ifndef EMULATOR_INCLUDE_EMULATOR_LIBS_APPCONTENTCONTRACTS_H_
#define EMULATOR_INCLUDE_EMULATOR_LIBS_APPCONTENTCONTRACTS_H_

#include <cstddef>
#include <cstdint>

namespace Libs::AppContent {

constexpr int32_t APP_CONTENT_ERROR_PARAMETER = -2133262334; // 0x80d90002

template <size_t Size>
[[nodiscard]] constexpr bool ReservedBytesAreZero(const char (&bytes)[Size]) noexcept {
	for (const char value: bytes) {
		if (value != 0) {
			return false;
		}
	}
	return true;
}

} // namespace Libs::AppContent

#endif /* EMULATOR_INCLUDE_EMULATOR_LIBS_APPCONTENTCONTRACTS_H_ */
