#ifndef EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_WRITEDATACONTRACTS_H_
#define EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_WRITEDATACONTRACTS_H_

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace Libs::Graphics {

enum class WriteDataAddressMode { Unsupported, Increment, Fixed };

[[nodiscard]] constexpr WriteDataAddressMode ResolveWriteDataAddressMode(
    uint32_t increment, uint32_t destination_selector) noexcept {
	if (increment == 0u) {
		return WriteDataAddressMode::Increment;
	}
	if (increment == 1u && (destination_selector == 4u || destination_selector == 5u)) {
		return WriteDataAddressMode::Fixed;
	}
	return WriteDataAddressMode::Unsupported;
}

[[nodiscard]] inline bool CommitWriteDataPayload(uint32_t* destination, const uint32_t* source,
                                                 size_t count, WriteDataAddressMode mode) noexcept {
	if (count == 0) {
		return true;
	}
	if (destination == nullptr || source == nullptr) {
		return false;
	}
	switch (mode) {
		case WriteDataAddressMode::Increment:
			std::memcpy(destination, source, count * sizeof(uint32_t));
			return true;
		case WriteDataAddressMode::Fixed: {
			auto* fixed_destination = static_cast<volatile uint32_t*>(destination);
			for (size_t i = 0; i < count; i++) {
				*fixed_destination = source[i];
			}
			return true;
		}
		case WriteDataAddressMode::Unsupported: return false;
	}
	return false;
}

} // namespace Libs::Graphics

#endif // EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_WRITEDATACONTRACTS_H_
