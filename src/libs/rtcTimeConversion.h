#ifndef EMULATOR_INCLUDE_EMULATOR_LIBS_RTCTIMECONVERSION_H_
#define EMULATOR_INCLUDE_EMULATOR_LIBS_RTCTIMECONVERSION_H_

#include "libs/errno.h"

#include <cstdint>

namespace Libs::LibRtc::Rtc {

constexpr int      RTC_ERROR_INVALID_YEAR = -2135621624; /* 0x80B50008 */
constexpr uint64_t RTC_UNIX_EPOCH_TICKS   = 0xdcbffeff2bc000ull;

// RTC ticks cover dates back to year 1, but the supported time_t representation begins at the
// Unix epoch. Keep the output deterministic when the source date is outside that range.
[[nodiscard]] constexpr int RtcUnixSecondsFromTick(uint64_t tick, int64_t* seconds) noexcept {
	if (tick < RTC_UNIX_EPOCH_TICKS) {
		*seconds = 0;
		return RTC_ERROR_INVALID_YEAR;
	}
	*seconds = static_cast<int64_t>((tick - RTC_UNIX_EPOCH_TICKS) / 1000000ull);
	return OK;
}

} // namespace Libs::LibRtc::Rtc

#endif /* EMULATOR_INCLUDE_EMULATOR_LIBS_RTCTIMECONVERSION_H_ */
