#include "kernel/uuid.h"

#include "libs/errno.h"

#include <chrono>
#include <cstddef>
#include <random>

namespace Libs::LibKernel {

namespace {

// Number of 100 ns intervals between 1582-10-15 (the UUID epoch) and 1970-01-01.
// The PS5 11.00 kernel generator contains this same 0x01b21dd213814000 constant.
constexpr uint64_t UUID_EPOCH_OFFSET = 0x01b21dd213814000ull;
constexpr uint64_t UUID_TIME_MASK    = 0x0fff'ffff'ffff'ffffull;
constexpr uint16_t UUID_SEQUENCE_MASK = 0x3fffu;

uint16_t RandomSequence() {
	std::random_device random;
	return static_cast<uint16_t>(random() & UUID_SEQUENCE_MASK);
}

std::array<uint8_t, 6> RandomNode() {
	std::random_device      random;
	std::array<uint8_t, 6> node {};
	for (auto& byte: node) {
		byte = static_cast<uint8_t>(random());
	}
	// RFC 4122 section 4.5: set the multicast bit when the node is random rather
	// than a hardware MAC address. Windows cannot expose the Prospero NIC node.
	node[0] |= 0x01u;
	return node;
}

uint64_t CurrentUuidTimestamp() {
	using HundredNanoseconds = std::chrono::duration<uint64_t, std::ratio<1, 10'000'000>>;
	const auto unix_intervals =
	    std::chrono::duration_cast<HundredNanoseconds>(std::chrono::system_clock::now().time_since_epoch())
	        .count();
	return (unix_intervals + UUID_EPOCH_OFFSET) & UUID_TIME_MASK;
}

} // namespace

KernelUuidGenerator::KernelUuidGenerator(): KernelUuidGenerator(RandomNode(), RandomSequence()) {}

KernelUuidGenerator::KernelUuidGenerator(std::array<uint8_t, 6> node, uint16_t clock_sequence)
    : m_node(node), m_clock_sequence(static_cast<uint16_t>(clock_sequence & UUID_SEQUENCE_MASK)) {
	// A supplied/random node is still a non-hardware node on this host.
	m_node[0] |= 0x01u;
}

KernelUuid KernelUuidGenerator::Generate() {
	return GenerateAtTimestamp(CurrentUuidTimestamp());
}

KernelUuid KernelUuidGenerator::GenerateAtTimestamp(uint64_t timestamp_100ns) {
	std::lock_guard lock(m_mutex);

	timestamp_100ns &= UUID_TIME_MASK;
	if (m_last_timestamp >= timestamp_100ns) {
		m_clock_sequence = static_cast<uint16_t>((m_clock_sequence + 1u) & UUID_SEQUENCE_MASK);
	}
	m_last_timestamp = timestamp_100ns;

	KernelUuid uuid {};
	uuid.time_low = static_cast<uint32_t>(timestamp_100ns);
	uuid.time_mid = static_cast<uint16_t>(timestamp_100ns >> 32u);
	uuid.time_hi_and_version =
	    static_cast<uint16_t>(((timestamp_100ns >> 48u) & 0x0fffu) | 0x1000u);
	uuid.clock_seq_hi_and_reserved =
	    static_cast<uint8_t>(((m_clock_sequence >> 8u) & 0x3fu) | 0x80u);
	uuid.clock_seq_low = static_cast<uint8_t>(m_clock_sequence);
	for (size_t index = 0; index < m_node.size(); index++) {
		uuid.node[index] = m_node[index];
	}
	return uuid;
}

int KYTY_SYSV_ABI KernelUuidCreate(KernelUuid* uuid) {
	if (uuid == nullptr) {
		return KERNEL_ERROR_EINVAL;
	}

	static KernelUuidGenerator generator;
	*uuid = generator.Generate();
	return OK;
}

} // namespace Libs::LibKernel
