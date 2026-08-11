#ifndef EMULATOR_INCLUDE_EMULATOR_KERNEL_UUID_H_
#define EMULATOR_INCLUDE_EMULATOR_KERNEL_UUID_H_

#include "common/abi.h"

#include <array>
#include <cstdint>
#include <mutex>

namespace Libs::LibKernel {

// Prospero SDK 10.00 target/include/kernel/uuid.h. Keep this field layout rather than
// treating the result as four unrelated random words: firmware creates RFC 4122/DCE
// version-1 identifiers and titles inspect the version and variant fields.
struct KernelUuid {
	uint32_t time_low;
	uint16_t time_mid;
	uint16_t time_hi_and_version;
	uint8_t  clock_seq_hi_and_reserved;
	uint8_t  clock_seq_low;
	uint8_t  node[6];
};

static_assert(sizeof(KernelUuid) == 16);

class KernelUuidGenerator {
public:
	KernelUuidGenerator();
	KernelUuidGenerator(std::array<uint8_t, 6> node, uint16_t clock_sequence);

	KernelUuid Generate();
	KernelUuid GenerateAtTimestamp(uint64_t timestamp_100ns);

private:
	std::mutex             m_mutex;
	std::array<uint8_t, 6> m_node {};
	uint64_t               m_last_timestamp = 0;
	uint16_t               m_clock_sequence = 0;
};

int KYTY_SYSV_ABI KernelUuidCreate(KernelUuid* uuid);

} // namespace Libs::LibKernel

#endif /* EMULATOR_INCLUDE_EMULATOR_KERNEL_UUID_H_ */
