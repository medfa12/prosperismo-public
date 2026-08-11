#ifndef EMULATOR_INCLUDE_EMULATOR_LIBS_SAVEDATAVALIDATION_H_
#define EMULATOR_INCLUDE_EMULATOR_LIBS_SAVEDATAVALIDATION_H_

#include <cstddef>
#include <cstdint>

namespace Libs::SaveData {

template <size_t Size>
[[nodiscard]] constexpr bool SaveDataReservedBytesAreZero(const uint8_t (&reserved)[Size]) {
	for (const auto byte: reserved) {
		if (byte != 0) {
			return false;
		}
	}
	return true;
}

} // namespace Libs::SaveData

#endif /* EMULATOR_INCLUDE_EMULATOR_LIBS_SAVEDATAVALIDATION_H_ */
