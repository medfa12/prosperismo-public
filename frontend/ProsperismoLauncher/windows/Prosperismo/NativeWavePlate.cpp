#if !defined(PROSPERISMO_WAVEPLATE_STANDALONE)
#include "pch.h"
#endif

#include "NativeWavePlate.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <memory>
#include <numbers>
#include <vector>

namespace Prosperismo::NativeWave {
namespace {

constexpr float kReferenceWidth = 1920.0f;
constexpr float kReferenceHeight = 1080.0f;

// Plane2 record 2, selected by BackgroundLayerState.HomeScreen (4) through
// the native owner table. These are the fallback values in SharpEmu's
// firmware-backed evaluator and are byte-for-byte the authored 4.03 record.
constexpr std::array<float, 28> kHomeRecord{
    0.035f, 0.21f, 0.58f, 0.0f,
    0.0f, 0.15f, 0.50f, 0.5f,
    0.0f, 0.14f, 0.55f, 1.0f,
    0.0f, 0.44f, 0.90f, 0.0f,
    -150.0f, 50.0f, 400.0f, 0.0f,
    -100.0f, 45.0f, 0.2f, 0.15f,
    1.4f, 1.0f, 0.0f, 0.0f,
};

constexpr std::array<std::uint8_t, 256> kPermutation{
    151,160,137,91,90,15,131,13,201,95,96,53,194,233,7,225,
    140,36,103,30,69,142,8,99,37,240,21,10,23,190,6,148,
    247,120,234,75,0,26,197,62,94,252,219,203,117,35,11,32,
    57,177,33,88,237,149,56,87,174,20,125,136,171,168,68,175,
    74,165,71,134,139,48,27,166,77,146,158,231,83,111,229,122,
    60,211,133,230,220,105,92,41,55,46,245,40,244,102,143,54,
    65,25,63,161,1,216,80,73,209,76,132,187,208,89,18,169,
    200,196,135,130,116,188,159,86,164,100,109,198,173,186,3,64,
    52,217,226,250,124,123,5,202,38,147,118,126,255,82,85,212,
    207,206,59,227,47,16,58,17,182,189,28,42,223,183,170,213,
    119,248,152,2,44,154,163,70,221,153,101,155,167,43,172,9,
    129,22,39,253,19,98,108,110,79,113,224,232,178,185,112,104,
    218,246,97,228,251,34,242,193,238,210,144,12,191,179,162,241,
    81,51,145,235,249,14,239,107,49,192,214,31,181,199,106,157,
    184,84,204,176,115,121,50,45,127,4,150,254,138,236,205,93,
    222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180,
};

struct Rgb { float r; float g; float b; };
struct RampStop { float r; float g; float b; float position; };

float Clamp01(float value) noexcept { return std::clamp(value, 0.0f, 1.0f); }

float Hermite(float p0, float p1, float m0, float m1, float t, float t2, float t3) noexcept {
  return Clamp01(p0 + (t * m0) + (t2 * ((3.0f * (p1 - p0)) - (2.0f * m0) - m1)) +
      (t3 * ((2.0f * (p0 - p1)) + m0 + m1)));
}

Rgb SampleRamp(float t, std::array<RampStop, 3> const& ramp) noexcept {
  auto const segment = t < ramp[1].position ? 0u : 1u;
  auto const& p0 = ramp[segment];
  auto const& p1 = ramp[segment + 1];
  auto const local = (t - p0.position) / (p1.position - p0.position);
  auto const& before = segment == 0 ? p0 : ramp[segment - 1];
  auto const& after = segment + 2 >= ramp.size() ? p1 : ramp[segment + 2];
  auto const m0r = segment == 0 ? p1.r - p0.r : (p1.r - before.r) * 0.5f;
  auto const m0g = segment == 0 ? p1.g - p0.g : (p1.g - before.g) * 0.5f;
  auto const m0b = segment == 0 ? p1.b - p0.b : (p1.b - before.b) * 0.5f;
  auto const m1r = segment + 1 == ramp.size() - 1 ? p1.r - p0.r : (after.r - p0.r) * 0.5f;
  auto const m1g = segment + 1 == ramp.size() - 1 ? p1.g - p0.g : (after.g - p0.g) * 0.5f;
  auto const m1b = segment + 1 == ramp.size() - 1 ? p1.b - p0.b : (after.b - p0.b) * 0.5f;
  auto const t2 = local * local;
  auto const t3 = t2 * local;
  return {Hermite(p0.r, p1.r, m0r, m1r, local, t2, t3),
          Hermite(p0.g, p1.g, m0g, m1g, local, t2, t3),
          Hermite(p0.b, p1.b, m0b, m1b, local, t2, t3)};
}

std::uint8_t ToUnormByte(float value) noexcept {
  return static_cast<std::uint8_t>(std::clamp(std::lround(Clamp01(value) * 255.0f), 0l, 255l));
}

class FrameRenderer final {
 public:
  FrameRenderer(std::uint32_t width, std::uint32_t height)
      : m_width(width), m_height(height), m_baseRgb(static_cast<size_t>(width) * height * 3u),
        m_noise0(static_cast<size_t>(width) * height), m_noise1(static_cast<size_t>(width) * height) {
    BuildInvariantTerms();
  }

  void Render(std::int64_t frame, std::uint8_t* destination, std::uint32_t stride) const noexcept {
    auto const phase = static_cast<int>((frame % 256 + 256) % 256);
    auto const pixels = static_cast<size_t>(m_width) * m_height;
    for (size_t pixel = 0; pixel < pixels; ++pixel) {
      auto const grain = m_ditherAmplitude *
          static_cast<float>(kPermutation[(m_noise0[pixel] + phase) & 0xff] +
                             kPermutation[(m_noise1[pixel] + phase) & 0xff]) / 510.0f;
      auto const x = static_cast<std::uint32_t>(pixel % m_width);
      auto const y = static_cast<std::uint32_t>(pixel / m_width);
      auto* target = destination + static_cast<size_t>(y) * stride + static_cast<size_t>(x) * 4u;
      auto const source = pixel * 3u;
      // Plane2 writes UNORM; it is not additionally transformed to sRGB.
      target[0] = ToUnormByte(m_baseRgb[source + 2] + grain);
      target[1] = ToUnormByte(m_baseRgb[source + 1] + grain);
      target[2] = ToUnormByte(m_baseRgb[source] + grain);
      target[3] = 255;
    }
  }

 private:
  void BuildInvariantTerms() noexcept {
    std::array<RampStop, 3> const ramp{{
        {kHomeRecord[0], kHomeRecord[1], kHomeRecord[2], kHomeRecord[3]},
        {kHomeRecord[4], kHomeRecord[5], kHomeRecord[6], kHomeRecord[7]},
        {kHomeRecord[8], kHomeRecord[9], kHomeRecord[10], kHomeRecord[11]},
    }};
    auto const axis = std::cos(kHomeRecord[21] * (std::numbers::pi_v<float> / 360.0f));
    auto const radius = std::abs(kHomeRecord[20]);
    auto const extentX = 2.0f * radius * axis;
    auto const extentY = -2.0f * axis * radius * kReferenceHeight / kReferenceWidth;
    auto const lightRatio = radius / (radius + kHomeRecord[18]);
    auto const centerX = lightRatio * kHomeRecord[16] * 0.5f;
    auto const centerY = lightRatio * kHomeRecord[17] * 0.5f;
    auto const exponent = std::pow(2.0f, (10.0f * kHomeRecord[22]) + 2.0f);
    m_ditherAmplitude = kHomeRecord[24] / 255.0f;

    for (std::uint32_t y = 0; y < m_height; ++y) {
      auto const py = (static_cast<float>(y) + 0.5f) * kReferenceHeight / m_height;
      auto const v = Clamp01(py / kReferenceHeight);
      auto const base = SampleRamp(v, ramp);
      for (std::uint32_t x = 0; x < m_width; ++x) {
        auto const px = (static_cast<float>(x) + 0.5f) * kReferenceWidth / m_width;
        auto const u = Clamp01(px / kReferenceWidth);
        auto const worldX = extentX * (u - 0.5f);
        auto const worldY = extentY * (v - 0.5f);
        auto lx = worldX - kHomeRecord[16];
        auto ly = worldY - kHomeRecord[17];
        auto lz = kHomeRecord[20] - kHomeRecord[18];
        auto const lightInverse = 1.0f / std::sqrt(lx * lx + ly * ly + lz * lz);
        lx *= lightInverse; ly *= lightInverse; lz *= lightInverse;
        auto vx = worldX; auto vy = worldY; auto vz = kHomeRecord[20];
        auto const viewInverse = 1.0f / std::sqrt(vx * vx + vy * vy + vz * vz);
        vx *= viewInverse; vy *= viewInverse; vz *= viewInverse;
        auto const specular = std::pow(std::max(0.0f, -((vx * lx) + (vy * ly) + (vz * lz))), exponent) * kHomeRecord[23];
        auto const pixel = static_cast<size_t>(y) * m_width + x;
        auto const source = pixel * 3u;
        m_baseRgb[source] = base.r + (kHomeRecord[12] * kHomeRecord[25] * specular);
        m_baseRgb[source + 1] = base.g + (kHomeRecord[13] * kHomeRecord[25] * specular);
        m_baseRgb[source + 2] = base.b + (kHomeRecord[14] * kHomeRecord[25] * specular);
        auto const dx = worldX - centerX;
        auto const dy = worldY - centerY;
        auto const first = static_cast<int>(extentX * std::sqrt(dx * dx + dy * dy)) & 0xff;
        auto const n = kPermutation[first];
        m_noise0[pixel] = static_cast<std::uint8_t>(static_cast<int>(py + (px * n)) & 0xff);
        m_noise1[pixel] = static_cast<std::uint8_t>(static_cast<int>(px + (py * n)) & 0xff);
      }
    }
  }

  std::uint32_t m_width;
  std::uint32_t m_height;
  float m_ditherAmplitude{};
  std::vector<float> m_baseRgb;
  std::vector<std::uint8_t> m_noise0;
  std::vector<std::uint8_t> m_noise1;
};

} // namespace

bool RenderHomePlateBgra8Premultiplied(
    std::uint8_t* destination,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t stride,
    std::int64_t frame) noexcept {
  if (!destination || width == 0 || height == 0 || width > std::numeric_limits<std::uint32_t>::max() / 4u || stride < width * 4u) {
    return false;
  }
  try {
    // The evaluator precomputes Plane2's ramp/projection/specular terms just
    // as the SharpEmu source does. The native background owns one worker
    // thread, so its thread-local renderer cannot cross a Composition thread.
    thread_local std::unique_ptr<FrameRenderer> renderer;
    thread_local std::uint32_t rendererWidth{};
    thread_local std::uint32_t rendererHeight{};
    if (!renderer || rendererWidth != width || rendererHeight != height) {
      renderer = std::make_unique<FrameRenderer>(width, height);
      rendererWidth = width;
      rendererHeight = height;
    }
    renderer->Render(frame, destination, stride);
    return true;
  } catch (...) {
    return false;
  }
}

} // namespace Prosperismo::NativeWave
