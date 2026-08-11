#ifndef EMULATOR_INCLUDE_EMULATOR_LIBS_WRITETHROTTLING_H_
#define EMULATOR_INCLUDE_EMULATOR_LIBS_WRITETHROTTLING_H_

#include <array>
#include <cstdint>

namespace Libs::LibKernelWriteThrottling {

// PS5 9.00 libkernel_sys.sprx export YFC3dBBipj8 writes exactly 0x20 bytes:
// a qword at +0, a dword at +8, and zero-filled reserved storage through +0x1f.
struct WriteThrottlingResult {
	uint64_t                state = 0;
	uint32_t                flags = 0;
	std::array<uint8_t, 20> reserved {};
};

static_assert(sizeof(WriteThrottlingResult) == 0x20);

} // namespace Libs::LibKernelWriteThrottling

#endif /* EMULATOR_INCLUDE_EMULATOR_LIBS_WRITETHROTTLING_H_ */
