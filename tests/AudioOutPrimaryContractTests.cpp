#include "kernel/semaphore.h"
#include "libs/audio.h"
#include "libs/errno.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <limits>

// Production provides this through libs.cpp; focused HLE tests do not initialize that stack.
namespace Kyty::Libs {
void PrintNameImpl(const char*, const char*, const char*) {}
} // namespace Kyty::Libs

namespace Libs::LibKernel {

uint64_t KYTY_SYSV_ABI KernelGetProcessTime() {
	static std::atomic_uint64_t now {1'000'000};
	return now.fetch_add(1'000, std::memory_order_relaxed);
}

namespace Semaphore {

int KYTY_SYSV_ABI KernelCreateSema(KernelSema*, const char*, uint32_t, int, int, void*) {
	return OK;
}

int KYTY_SYSV_ABI KernelWaitSema(KernelSema, int, KernelUseconds*) {
	return OK;
}

int KYTY_SYSV_ABI KernelSignalSema(KernelSema, int) {
	return OK;
}

} // namespace Semaphore
} // namespace Libs::LibKernel

namespace Libs::Audio::AudioOut {

struct AudioOutOutputParam {
	int         handle;
	const void* ptr;
};

} // namespace Libs::Audio::AudioOut

namespace {

namespace AudioOut = Libs::Audio::AudioOut;
namespace AudioIn  = Libs::Audio::AudioIn;

constexpr uint32_t AudioOutOutputsMax      = 33;
constexpr uint32_t AudioOutSamplesPerGrain = 256;

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "AudioOutPrimaryContractTests: failed: %s\n", text);
		std::abort();
	}
}

void TestOutputsCountUpperBound() {
	Libs::Audio::AudioSubsystem::Instance()->Init(nullptr);

	// Vibration ports intentionally have no SDL audio device, keeping this contract test offline.
	const int handle = AudioOut::AudioOutOpen(255, 10, 0, AudioOutSamplesPerGrain, 48000, 1);
	Check(handle > 0, "could not open a no-device vibration port");

	std::array<AudioOut::AudioOutOutputParam, AudioOutOutputsMax> boundary {};
	for (auto& output: boundary) {
		output.handle = handle;
	}
	Check(AudioOut::AudioOutOutputs(boundary.data(), boundary.size()) ==
	          static_cast<int>(AudioOutSamplesPerGrain),
	      "the firmware-supported 33-entry boundary was rejected");

	std::array<AudioOut::AudioOutOutputParam, AudioOutOutputsMax + 1> oversized {};
	for (auto& output: oversized) {
		output.handle = handle;
	}
	Check(AudioOut::AudioOutOutputs(oversized.data(), oversized.size()) ==
	          Libs::Audio::AUDIO_OUT_ERROR_PORT_FULL,
	      "a 34-entry output request returned the wrong error");
	Check(AudioOut::AudioOutOutputs(nullptr, std::numeric_limits<uint32_t>::max()) ==
	          Libs::Audio::AUDIO_OUT_ERROR_PORT_FULL,
	      "an overflowing output count was not rejected before pointer access");

	Check(AudioOut::AudioOutClose(handle) == OK, "could not close the vibration port");
	Libs::Audio::AudioSubsystem::Instance()->Destroy(nullptr);
}

void TestAudioInSilentState() {
	Libs::Audio::AudioSubsystem::Instance()->Init(nullptr);

	Check(AudioIn::AudioInGetSilentState(-1) == Libs::Audio::AUDIO_IN_ERROR_INVALID_HANDLE,
	      "an invalid input handle returned a silent-state mask");

	const int handle = AudioIn::AudioInOpen(255, 1, 0, 256, 48000, 2);
	Check(handle > 0, "could not open a headless audio-input port");
	Check(AudioIn::AudioInGetSilentState(handle) == AudioIn::AUDIO_IN_SILENT_STATE_DEVICE_NONE,
	      "a headless input port did not report DEVICE_NONE");
	Check(AudioIn::AudioInClose(handle) == OK, "could not close the audio-input port");
	Check(AudioIn::AudioInGetSilentState(handle) == Libs::Audio::AUDIO_IN_ERROR_INVALID_HANDLE,
	      "a closed input handle returned a silent-state mask");

	Libs::Audio::AudioSubsystem::Instance()->Destroy(nullptr);
}

} // namespace

int main() {
	TestOutputsCountUpperBound();
	TestAudioInSilentState();
	std::printf("AudioOutPrimaryContractTests: all cases passed\n");
	return 0;
}
