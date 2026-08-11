#ifndef EMULATOR_INCLUDE_EMULATOR_LIBS_SYSTEM_SERVICE_H_
#define EMULATOR_INCLUDE_EMULATOR_LIBS_SYSTEM_SERVICE_H_

#include "libs/errno.h"

#include <bit>
#include <cstdint>

namespace Libs::SystemService {

struct SystemServiceHdrToneMapLuminance {
	float max_full_frame_tone_map_luminance;
	float max_tone_map_luminance;
	float min_tone_map_luminance;
};

// The report API reserves its argument for a future structure. The documented contract and
// the 3.20/12.40 firmware exports both reject every non-null value before entering the fatal
// termination path. See docs/system-service-contracts.md for provenance.
[[nodiscard]] constexpr int ValidateAbnormalTerminationInfo(const void* info) {
	return info == nullptr ? OK : SYSTEM_SERVICE_ERROR_PARAMETER;
}

// Complete 4.03 libSceSystemService.sprx export mPpPxv5CZt4 (vaddr 0x2f20,
// st_size 0x4f6) validates three display settings against firmware tables. Its
// successful fallback writes these exact IEEE-754 words. Windows has no ShellCore
// display-setting service, so this is the native fallback rather than a guessed host
// monitor profile.
inline void PopulateHdrToneMapLuminance(SystemServiceHdrToneMapLuminance* luminance) {
	luminance->max_full_frame_tone_map_luminance = std::bit_cast<float>(0x441f546au);
	luminance->max_tone_map_luminance            = std::bit_cast<float>(0x44754958u);
	luminance->min_tone_map_luminance            = std::bit_cast<float>(0x3dffddecu);
}

} // namespace Libs::SystemService

#endif /* EMULATOR_INCLUDE_EMULATOR_LIBS_SYSTEM_SERVICE_H_ */
