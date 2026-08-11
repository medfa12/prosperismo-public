#include "graphics/guest_gpu/pm4.h"
#include "kernel/eventFlag.h"
#include "kernel/uuid.h"
#include "libs/agc.h"
#include "libs/errno.h"
#include "libs/netSocketContract.h"
#include "libs/saveDataCapacity.h"
#include "libs/saveDataMountSlots.h"
#include "libs/saveDataValidation.h"
#include "libs/rtcTimeConversion.h"
#include "libs/systemService.h"
#include "libs/textToSpeech2.h"
#include "libs/writeThrottling.h"

#include <array>
#include <bit>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <type_traits>

// Contract tests exercise HLE behavior without initializing the emulator logging/timer stack.
// Production provides this out of line in libs/libs.cpp; keep the test double deliberately inert.
namespace Kyty::Libs {
void PrintNameImpl(const char*, const char*, const char*) {}
} // namespace Kyty::Libs

using AcbDmaDataAbi = uint32_t*(KYTY_SYSV_ABI*)(Libs::Graphics::Gen5::CommandBuffer*, uint8_t,
                                                uint8_t, uint64_t, uint8_t, uint8_t, uint64_t,
                                                uint32_t, uint8_t, uint8_t);
static_assert(std::is_same_v<decltype(&Libs::Graphics::Gen5::GraphicsAcbDmaData), AcbDmaDataAbi>,
              "sceAgcAcbDmaData must match Sony's async command-buffer ABI");

namespace {

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "Prosperismo HLE contract test failed: %s\n", text);
		std::abort();
	}
}

uint16_t ClockSequence(const Libs::LibKernel::KernelUuid& uuid) {
	return static_cast<uint16_t>(((uuid.clock_seq_hi_and_reserved & 0x3fu) << 8u) |
	                             uuid.clock_seq_low);
}

void TestFirmwareHdrFallback() {
	Libs::SystemService::SystemServiceHdrToneMapLuminance luminance {};
	Libs::SystemService::PopulateHdrToneMapLuminance(&luminance);

	Check(std::bit_cast<uint32_t>(luminance.max_full_frame_tone_map_luminance) == 0x441f546au,
	      "full-frame luminance differs from firmware fallback");
	Check(std::bit_cast<uint32_t>(luminance.max_tone_map_luminance) == 0x44754958u,
	      "maximum luminance differs from firmware fallback");
	Check(std::bit_cast<uint32_t>(luminance.min_tone_map_luminance) == 0x3dffddecu,
	      "minimum luminance differs from firmware fallback");
}

void TestAgcSingleDwordNopContract() {
	using namespace Libs::Graphics;

	constexpr uint32_t packet = KYTY_PM4(1u, Pm4::IT_NOP, Pm4::R_ZERO);
	Check(((packet >> 30u) & 0x3u) == 3u, "single-DWORD AGC NOP is not a type-3 packet");
	Check(((packet >> 8u) & 0xffu) == Pm4::IT_NOP,
	      "single-DWORD AGC NOP has the wrong opcode");
	Check(KYTY_PM4_R(packet) == Pm4::R_ZERO,
	      "single-DWORD AGC NOP has the wrong packet subtype");
	Check(KYTY_PM4_LEN(packet) == 1u,
	      "single-DWORD AGC NOP is not consumed as one DWORD");
	Check(KYTY_PM4_LEN(KYTY_PM4(2u, Pm4::IT_NOP, Pm4::R_ZERO)) == 2u,
	      "ordinary AGC NOP length decoding regressed");
}

void TestAbnormalTerminationReservedInfoContract() {
	using namespace Libs::SystemService;

	Check(ValidateAbnormalTerminationInfo(nullptr) == OK,
	      "abnormal-termination null reserved argument was rejected");
	const uint8_t reserved_info = 0;
	Check(ValidateAbnormalTerminationInfo(&reserved_info) == SYSTEM_SERVICE_ERROR_PARAMETER,
	      "abnormal-termination non-null reserved argument was accepted");
}

void TestUuidVersionOneLayout() {
	constexpr uint64_t timestamp = 0x0123'4567'89ab'cdefull;
	Libs::LibKernel::KernelUuidGenerator generator({0x02, 0x11, 0x22, 0x33, 0x44, 0x55}, 0x1234);

	const auto first = generator.GenerateAtTimestamp(timestamp);
	Check(first.time_low == 0x89abcdefu, "UUID time-low field is wrong");
	Check(first.time_mid == 0x4567u, "UUID time-mid field is wrong");
	Check(first.time_hi_and_version == 0x1123u, "UUID is not a version-1 identifier");
	Check((first.clock_seq_hi_and_reserved & 0xc0u) == 0x80u,
	      "UUID does not carry the RFC 4122 variant");
	Check((first.node[0] & 0x01u) != 0, "random UUID node lacks multicast marker");
	Check(ClockSequence(first) == 0x1234u, "UUID clock sequence is wrong");

	const auto second = generator.GenerateAtTimestamp(timestamp);
	Check(ClockSequence(second) == 0x1235u,
	      "UUID clock sequence did not advance when the clock repeated");

	Check(Libs::LibKernel::KernelUuidCreate(nullptr) == Libs::LibKernel::KERNEL_ERROR_EINVAL,
	      "UUID null-output contract is wrong");
	Libs::LibKernel::KernelUuid generated {};
	Check(Libs::LibKernel::KernelUuidCreate(&generated) == OK, "UUID creation failed");
	Check((generated.time_hi_and_version & 0xf000u) == 0x1000u,
	      "runtime UUID is not version 1");
	Check((generated.clock_seq_hi_and_reserved & 0xc0u) == 0x80u,
	      "runtime UUID variant is wrong");
}

void TestNamedEventFlagLifetimeAndExtendedAttribute() {
	using namespace Libs::LibKernel::EventFlag;
	constexpr char name[] = "prosperismo-hle-contract";

	KernelEventFlag created = nullptr;
	Check(KernelCreateEventFlag(&created, name, 0x120u, 0, nullptr) == OK,
	      "firmware-used event-flag attribute 0x100 was rejected");
	Check(created != nullptr, "event flag create returned a null handle");

	KernelEventFlag opened = nullptr;
	Check(KernelOpenEventFlag(&opened, name) == OK, "named event flag did not open");
	Check(opened == created, "named event flag open fabricated a private object");
	Check(KernelCloseEventFlag(opened) == OK, "opened event flag did not close");
	Check(KernelSetEventFlag(created, 1) == OK,
	      "closing one reference destroyed the shared event flag");
	Check(KernelCloseEventFlag(created) == OK, "creator event flag did not close");

	opened = nullptr;
	Check(KernelOpenEventFlag(&opened, name) == Libs::LibKernel::KERNEL_ERROR_ESRCH,
	      "closed event flag remained published by name");

	Check(KernelCreateEventFlag(&created, name, 0x20u, 0, nullptr) == OK,
	      "event flag name could not be reused after last close");
	Check(KernelDeleteEventFlag(created) == OK, "event flag cleanup failed");
}

void TestEventFlagWaitModeValidation() {
	using namespace Libs::LibKernel::EventFlag;

	KernelEventFlag event_flag = nullptr;
	Check(KernelCreateEventFlag(&event_flag, "wait-mode-validation", 0x20u, 1, nullptr) == OK,
	      "event flag for wait-mode validation was not created");

	constexpr std::array<uint32_t, 5> invalid_modes {0x00u, 0x03u, 0x31u, 0x41u, 0x101u};
	for (const auto mode: invalid_modes) {
		uint64_t                           result  = 0xfeedfacecafebeefull;
		Libs::LibKernel::KernelUseconds timeout = 1234;
		Check(KernelWaitEventFlag(event_flag, 1, mode, &result, &timeout) ==
		          Libs::LibKernel::KERNEL_ERROR_EINVAL,
		      "event-flag wait accepted an invalid wait mode");
		Check(result == 0xfeedfacecafebeefull && timeout == 1234,
		      "invalid event-flag wait mode changed outputs");

		result = 0xfeedfacecafebeefull;
		Check(KernelPollEventFlag(event_flag, 1, mode, &result) ==
		          Libs::LibKernel::KERNEL_ERROR_EINVAL,
		      "event-flag poll accepted an invalid wait mode");
		Check(result == 0xfeedfacecafebeefull,
		      "invalid event-flag poll mode changed the result pattern");
	}

	uint64_t result = 0;
	Check(KernelPollEventFlag(event_flag, 1, 0x01u, &result) == OK && result == 1,
	      "valid event-flag wait mode no longer polls successfully");
	Check(KernelDeleteEventFlag(event_flag) == OK, "event-flag validation cleanup failed");
}

void TestProsperoSaveDataCapacityAndMountIdentity() {
	using namespace Libs::SaveData;

	Check(SaveDataBytesToBlocks(0) == 0, "empty save data consumed a block");
	Check(SaveDataBytesToBlocks(SAVE_DATA_BLOCK_SIZE) == 1,
	      "one exact Prospero block was rounded incorrectly");
	Check(SaveDataBytesToBlocks(SAVE_DATA_BLOCK_SIZE + 1) == 2,
	      "Prospero block usage did not round up");
	Check(SaveDataFreeBlocks(48, SAVE_DATA_BLOCK_SIZE + 1) == 46,
	      "free save-data blocks report used blocks instead of remaining blocks");
	Check(SaveDataFreeBlocks(1, SAVE_DATA_BLOCK_SIZE + 1) == 0,
	      "save-data free-block subtraction did not saturate");

	SaveDataMountSlots slots;
	const auto         slot = slots.FindAvailable("WORLD_A");
	Check(slot == 0, "first save-data mount did not select slot zero");
	slots.Mount(static_cast<size_t>(slot), "WORLD_A", "host/world-a", 48);
	const auto* mounted = slots.Get(static_cast<size_t>(slot));
	Check(mounted != nullptr && mounted->directory == "WORLD_A" &&
	          mounted->host_path == "host/world-a" && mounted->blocks == 48,
	      "live mount lost its host directory or allocated block count");
	Check(slots.FindAvailable("WORLD_A") == SaveDataMountSlots::BUSY,
	      "duplicate live save-data directory was accepted");
}

void TestSaveDataSearchConditionValidation() {
	using namespace Libs::SaveData;

	uint8_t search_reserved[32] {};
	Check(SaveDataReservedBytesAreZero(search_reserved),
	      "zeroed save-data search reserve area was rejected");
	for (auto& byte: search_reserved) {
		byte = 1;
		Check(!SaveDataReservedBytesAreZero(search_reserved),
		      "nonzero save-data search reserve byte was accepted");
		byte = 0;
	}
}

void TestFirmwareWriteThrottlingResultLayout() {
	using Libs::LibKernelWriteThrottling::WriteThrottlingResult;
	const WriteThrottlingResult result {};
	Check(sizeof(result) == 0x20, "write-throttling result has the wrong firmware size");
	Check(result.state == 0 && result.flags == 0,
	      "Windows write-throttling fallback is not neutral");
	for (const auto byte: result.reserved) {
		Check(byte == 0, "write-throttling reserved output was not initialized");
	}
}

void TestTextToSpeech2SdkLifecycle() {
	using namespace Libs::TextToSpeech2;
	State state;
	Check(state.Initialize(false) == ERROR_INVALID_ARGUMENT,
	      "text-to-speech accepted a null initialization parameter");
	Check(state.Open(true) == ERROR_NOT_INITIALIZED,
	      "text-to-speech opened before initialization");
	Check(state.Initialize(true) == 0, "text-to-speech initialization failed");
	Check(state.Initialize(true) == ERROR_ALREADY_INITIALIZED,
	      "text-to-speech duplicate initialization was accepted");
	Check(state.Open(true) == 0, "text-to-speech open failed");
	Check(state.Open(true) == ERROR_ALREADY_OPENED,
	      "text-to-speech duplicate open was accepted");
	Check(state.RequireOpen(false) == ERROR_INVALID_ARGUMENT,
	      "text-to-speech accepted a null speech parameter");
	Check(state.RequireOpen() == 0, "text-to-speech rejected an opened operation");
	Check(state.Close() == 0, "text-to-speech close failed");
	Check(state.Close() == ERROR_NOT_OPENED, "text-to-speech duplicate close was accepted");
	Check(state.Terminate() == 0, "text-to-speech termination failed");
	Check(state.Terminate() == ERROR_NOT_INITIALIZED,
	      "text-to-speech duplicate termination was accepted");
}

void TestNetIpv4DatagramSocketContract() {
	using namespace Libs::Network::Net;

	Check(ResolveHostSocketAction(2, 2, 0) == HostSocketAction::CreateIpv4Datagram,
	      "firmware IPv4 datagram socket was not admitted to the host backend");
	Check(ResolveHostSocketAction(28, 2, 0) == HostSocketAction::UnsupportedFamily,
	      "unimplemented IPv6 socket escaped the narrow host contract");
	Check(ResolveHostSocketAction(2, 1, 0) == HostSocketAction::UnsupportedType,
	      "unimplemented stream socket escaped the narrow host contract");
	Check(ResolveHostSocketAction(2, 2, 17) == HostSocketAction::UnsupportedProtocol,
	      "datagram socket accepted a nonzero protocol");

	Check(ResolveHostSocketOption(0xffff, 0x1002) == HostSocketOption::ReceiveBuffer,
	      "firmware receive-buffer option was not translated");
	Check(ResolveHostSocketOption(0xffff, 0x0080) == HostSocketOption::Linger,
	      "firmware linger option was not translated");
	Check(ResolveHostSocketOption(0xffff, 0x1001) == HostSocketOption::SendBuffer,
	      "firmware send-buffer option was not translated");
	Check(ResolveHostSocketOption(0xffff, 0x0020) == HostSocketOption::Broadcast,
	      "firmware broadcast option was not translated");
	Check(ResolveHostSocketOption(0, 2) == HostSocketOption::Ipv4HeaderIncluded,
	      "firmware IPv4-header option was not translated");
	Check(ResolveHostSocketOption(0xffff, 0x4000) == HostSocketOption::Unsupported,
	      "unknown socket option escaped the narrow translation");
	Check(HostSocketMessageFlagsSupported(0), "flags-zero UDP message was rejected");
	Check(!HostSocketMessageFlagsSupported(0x80),
	      "untranslated nonblocking message flag was admitted");
}

void TestRtcTimeTLowerBound() {
	using namespace Libs::LibRtc::Rtc;

	struct Vector {
		uint64_t tick;
		int      result;
		int64_t  seconds;
	};
	constexpr std::array vectors {
	    Vector {0, RTC_ERROR_INVALID_YEAR, 0},
	    Vector {RTC_UNIX_EPOCH_TICKS - 1, RTC_ERROR_INVALID_YEAR, 0},
	    Vector {RTC_UNIX_EPOCH_TICKS, OK, 0},
	    Vector {RTC_UNIX_EPOCH_TICKS + 999999, OK, 0},
	    Vector {RTC_UNIX_EPOCH_TICKS + 1000000, OK, 1},
	};

	for (const auto& vector: vectors) {
		int64_t seconds = -1;
		Check(RtcUnixSecondsFromTick(vector.tick, &seconds) == vector.result &&
		          seconds == vector.seconds,
		      "RTC time_t lower-bound conversion diverged from firmware");
	}
}

} // namespace

int main() {
	TestFirmwareHdrFallback();
	TestAgcSingleDwordNopContract();
	TestAbnormalTerminationReservedInfoContract();
	TestUuidVersionOneLayout();
	TestNamedEventFlagLifetimeAndExtendedAttribute();
	TestEventFlagWaitModeValidation();
	TestProsperoSaveDataCapacityAndMountIdentity();
	TestSaveDataSearchConditionValidation();
	TestFirmwareWriteThrottlingResultLayout();
	TestTextToSpeech2SdkLifecycle();
	TestNetIpv4DatagramSocketContract();
	TestRtcTimeTLowerBound();
	return 0;
}
