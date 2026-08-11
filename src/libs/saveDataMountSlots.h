#ifndef EMULATOR_INCLUDE_EMULATOR_LIBS_SAVEDATAMOUNTSLOTS_H_
#define EMULATOR_INCLUDE_EMULATOR_LIBS_SAVEDATAMOUNTSLOTS_H_

#include <array>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

namespace Libs::SaveData {

class SaveDataMountSlots {
public:
	struct Entry {
		std::string directory;
		std::string host_path;
		uint64_t    blocks = 0;
	};

	static constexpr int    BUSY  = -2;
	static constexpr int    FULL  = -1;
	static constexpr size_t COUNT = 16;

	[[nodiscard]] int FindAvailable(std::string_view directory) const {
		int available = FULL;
		for (size_t index = 0; index < m_directories.size(); index++) {
			const auto& mounted = m_directories[index];
			if (mounted.has_value() && mounted->directory == directory) {
				return BUSY;
			}
			if (!mounted.has_value() && available == FULL) {
				available = static_cast<int>(index);
			}
		}
		return available;
	}

	void Mount(size_t slot, std::string_view directory, std::string_view host_path,
	           uint64_t blocks) {
		m_directories[slot] = Entry {std::string(directory), std::string(host_path), blocks};
	}

	void Release(size_t slot) {
		if (slot < m_directories.size()) {
			m_directories[slot].reset();
		}
	}

	[[nodiscard]] int Find(std::string_view mount_point) const {
		for (size_t index = 0; index < m_directories.size(); index++) {
			if (m_directories[index].has_value() && MountPoint(index) == mount_point) {
				return static_cast<int>(index);
			}
		}
		return FULL;
	}

	[[nodiscard]] const Entry* Get(size_t slot) const {
		if (slot >= m_directories.size() || !m_directories[slot].has_value()) {
			return nullptr;
		}
		return &*m_directories[slot];
	}

	[[nodiscard]] static std::string MountPoint(size_t slot) {
		return "/savedata" + std::to_string(slot);
	}

	[[nodiscard]] bool Empty() const {
		for (const auto& directory: m_directories) {
			if (directory.has_value()) {
				return false;
			}
		}
		return true;
	}

private:
	std::array<std::optional<Entry>, COUNT> m_directories;
};

} // namespace Libs::SaveData

#endif /* EMULATOR_INCLUDE_EMULATOR_LIBS_SAVEDATAMOUNTSLOTS_H_ */
