#ifndef EMULATOR_INCLUDE_EMULATOR_LIBS_SYSMODULECONTRACTS_H_
#define EMULATOR_INCLUDE_EMULATOR_LIBS_SYSMODULECONTRACTS_H_

#include "libs/errno.h"

#include <cstdint>

namespace Libs::Sysmodule {

constexpr int32_t SYSMODULE_ERROR_INVALID_VALUE = -2141581312; // 0x805a1000

[[nodiscard]] constexpr int32_t ValidateReservedModuleId(uint16_t id) noexcept {
	return id != 0 ? OK : SYSMODULE_ERROR_INVALID_VALUE;
}

} // namespace Libs::Sysmodule

#endif /* EMULATOR_INCLUDE_EMULATOR_LIBS_SYSMODULECONTRACTS_H_ */
