#include "libs/audio.h"
#include "libs/avPlayerContracts.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <string>

// Production provides this through libs.cpp; this focused test does not initialize that stack.
namespace Kyty::Libs {
void PrintNameImpl(const char*, const char*, const char*) {}
} // namespace Kyty::Libs

// Source opening is outside this no-media contract test. The full AvPlayer translation unit still
// references the mount resolver, so keep that boundary deterministic without initializing it.
namespace Libs::LibKernel::FileSystem {
std::filesystem::path GetRealFilename(const std::string& mounted_file_name) {
	return mounted_file_name;
}
} // namespace Libs::LibKernel::FileSystem

namespace {

namespace AvPlayer = Libs::Audio::AvPlayer;

constexpr int32_t AvPlayerErrorInvalidParams = -2140536831;
constexpr size_t  AvPlayerInitDataExSize      = 0x230;

using Allocate          = KYTY_SYSV_ABI void* (*)(void*, uint32_t, uint32_t);
using Deallocate        = KYTY_SYSV_ABI void (*)(void*, void*);
using AllocateTexture   = KYTY_SYSV_ABI void* (*)(void*, uint32_t, uint32_t);
using DeallocateTexture = KYTY_SYSV_ABI void (*)(void*, void*);

struct MemoryReplacementFixture {
	void*             object_pointer;
	Allocate          allocate;
	Deallocate        deallocate;
	AllocateTexture   allocate_texture;
	DeallocateTexture deallocate_texture;
};

struct alignas(size_t) InitDataExFixture {
	size_t                   this_size;
	MemoryReplacementFixture memory_replacement;
	std::array<std::byte,
	           AvPlayerInitDataExSize - sizeof(size_t) - sizeof(MemoryReplacementFixture)>
	    remaining;
};

static_assert(sizeof(MemoryReplacementFixture) == 0x28);
static_assert(sizeof(InitDataExFixture) == AvPlayerInitDataExSize);

KYTY_SYSV_ABI void* UnusedAllocate(void*, uint32_t, uint32_t) {
	return nullptr;
}

KYTY_SYSV_ABI void UnusedDeallocate(void*, void*) {}

void SetRequiredAllocatorCallbacks(InitDataExFixture* init) {
	init->memory_replacement.allocate           = UnusedAllocate;
	init->memory_replacement.deallocate         = UnusedDeallocate;
	init->memory_replacement.allocate_texture   = UnusedAllocate;
	init->memory_replacement.deallocate_texture = UnusedDeallocate;
}

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "AvPlayerInitContractTests: failed: %s\n", text);
		std::abort();
	}
}

void CheckRejectedSize(size_t this_size) {
	InitDataExFixture init {};
	init.this_size = this_size;
	SetRequiredAllocatorCallbacks(&init);

	auto* const sentinel = reinterpret_cast<AvPlayer::AvPlayerInternal*>(uintptr_t {0x12345678});
	auto*       handle   = sentinel;

	const int rc = AvPlayer::AvPlayerInitEx(&init, &handle);
	Check(rc == AvPlayerErrorInvalidParams, "mismatched thisSize returned the wrong error");
	Check(handle == sentinel, "mismatched thisSize modified the output handle");
}

void TestCurrentInitDataExSizeRemainsAccepted() {
	InitDataExFixture init {};
	init.this_size = AvPlayerInitDataExSize;
	SetRequiredAllocatorCallbacks(&init);

	AvPlayer::AvPlayerInternal* handle = nullptr;
	Check(AvPlayer::AvPlayerInitEx(&init, &handle) == 0, "current thisSize was rejected");
	Check(handle != nullptr, "current thisSize did not produce a handle");
	Check(AvPlayer::AvPlayerClose(handle) == 0, "created player could not be closed");
}

void TestMismatchedInitDataExSizesAreRejectedWithoutOutput() {
	CheckRejectedSize(0);
	CheckRejectedSize(AvPlayerInitDataExSize - 1u);
	CheckRejectedSize(AvPlayerInitDataExSize + 1u);
}

void TestStoppedPlayerHasNoVisibleStreams() {
	Check(AvPlayer::VisibleStreamCount(true, 3) == 3,
	      "available streams did not preserve the container count");
	Check(AvPlayer::VisibleStreamCount(false, 3) == 0,
	      "stopped player retained a stale stream count");
}

} // namespace

int main() {
	TestCurrentInitDataExSizeRemainsAccepted();
	TestMismatchedInitDataExSizesAreRejectedWithoutOutput();
	TestStoppedPlayerHasNoVisibleStreams();
	std::printf("AvPlayerInitContractTests: all cases passed\n");
	return 0;
}
