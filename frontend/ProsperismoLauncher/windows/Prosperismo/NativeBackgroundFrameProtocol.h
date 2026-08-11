#pragma once

#include <cstddef>
#include <cstdint>

namespace Prosperismo::NativeBackground {

inline constexpr wchar_t MappingName[] = L"Local\\ProsperismoShellBackground";
inline constexpr wchar_t FrameEventName[] = L"Local\\ProsperismoShellBackgroundFrame";
inline constexpr wchar_t ReadyEventName[] = L"Local\\ProsperismoShellBackgroundReady";
inline constexpr wchar_t ConsumedEventName[] = L"Local\\ProsperismoShellBackgroundConsumed";
inline constexpr wchar_t ControlMappingName[] = L"Local\\ProsperismoShellBackgroundControl";
inline constexpr wchar_t ControlChangedEventName[] = L"Local\\ProsperismoShellBackgroundControlChanged";
inline constexpr char Magic[8] = {'P', 'S', '5', 'B', 'G', 'R', 'A', '\0'};
inline constexpr char ControlMagic[8] = {'P', 'S', '5', 'B', 'G', 'C', 'T', '\0'};
inline constexpr uint32_t Version = 1;
inline constexpr uint32_t ControlVersion = 1;
inline constexpr uint32_t FormatBgra8Premultiplied = 1;
inline constexpr uint32_t LayerParticleOverlay = 2;
inline constexpr uint32_t LayerFirstWaveBase = 1;
inline constexpr uint32_t HomeLayerMask = LayerFirstWaveBase | LayerParticleOverlay;
inline constexpr uint32_t SettingsLayerMask = LayerFirstWaveBase;
inline constexpr uint32_t MaxDimension = 8192;

// The producer writes the inactive slot, publishes activeSlot, then increments
// sequence with an interlocked operation before signalling FrameEventName.
// Keeping the header on one cache line makes those publication fields aligned.
struct alignas(64) FrameHeader {
  char magic[8];
  uint32_t version;
  uint32_t width;
  uint32_t height;
  uint32_t stride;
  uint32_t format;
  uint32_t slotBytes;
  volatile long activeSlot;
  uint32_t reserved0;
  alignas(8) volatile long long sequence;
  uint64_t timestampQpc;
  uint8_t reserved1[8];
};

static_assert(sizeof(FrameHeader) == 64);
static_assert(offsetof(FrameHeader, sequence) == 40);

// Shell-owned presentation state consumed by the out-of-process renderer.
// Odd sequence values are being written; even values are stable snapshots.
struct alignas(64) BackgroundControlHeader {
  char magic[8];
  uint32_t version;
  uint32_t headerBytes;
  volatile long layerMask;
  uint32_t reserved0;
  alignas(8) volatile long long sequence;
  uint64_t timestampQpc;
  uint8_t reserved1[24];
};

static_assert(sizeof(BackgroundControlHeader) == 64);
static_assert(offsetof(BackgroundControlHeader, layerMask) == 16);
static_assert(offsetof(BackgroundControlHeader, sequence) == 24);
static_assert(offsetof(BackgroundControlHeader, timestampQpc) == 32);

} // namespace Prosperismo::NativeBackground
