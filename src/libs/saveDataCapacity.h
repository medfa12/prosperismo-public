#ifndef EMULATOR_INCLUDE_EMULATOR_LIBS_SAVEDATACAPACITY_H_
#define EMULATOR_INCLUDE_EMULATOR_LIBS_SAVEDATACAPACITY_H_

#include <cstdint>

namespace Libs::SaveData {

// Prospero SDK 10.00 save_data_defs.h: SCE_SAVE_DATA_BLOCK_SIZE2.
constexpr uint64_t SAVE_DATA_BLOCK_SIZE = 65'536;

[[nodiscard]] constexpr uint64_t SaveDataBytesToBlocks(uint64_t bytes) {
	return bytes / SAVE_DATA_BLOCK_SIZE + (bytes % SAVE_DATA_BLOCK_SIZE != 0 ? 1u : 0u);
}

[[nodiscard]] constexpr uint64_t SaveDataFreeBlocks(uint64_t total_blocks,
                                                    uint64_t used_bytes) {
	const auto used_blocks = SaveDataBytesToBlocks(used_bytes);
	return total_blocks > used_blocks ? total_blocks - used_blocks : 0;
}

} // namespace Libs::SaveData

#endif /* EMULATOR_INCLUDE_EMULATOR_LIBS_SAVEDATACAPACITY_H_ */
