#include "pch.h"

#include "FirstWaveBackground.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace Prosperismo::FirstWave {
namespace {

// Literal values decoded from the 12.40 fw_background_p instruction stream.
constexpr float kPhaseCyclesPerSecond = 0.047746479511260986f;
constexpr float kHashTime0 = 23189.0f;
constexpr float kHashTime1 = 13181.0f;
constexpr float kDirectionScale = 1.7000000476837158f;
constexpr float kFoldOffset = -1.350000023841858f;
constexpr float kLightOrbitScale = -0.33000001311302185f;
constexpr float kLightOrbitOffset = -0.6600000262260437f;
constexpr float kHashToFloat = 4.656612768993984e-12f;
constexpr float kGaussianExp2Scale = -14.426950454711914f;
constexpr float kTwoPi = 6.28318530717958647692f;
constexpr float kByteToFloat = 0.003921568859368563f;

constexpr std::uint32_t kHashMultiplier0 = 17387u;
constexpr std::uint32_t kHashAddend0 = 789221u;
constexpr std::uint32_t kHashMultiplier1 = 15731u;
constexpr std::uint32_t kHashAddend1 = 1300237u;
constexpr std::uint32_t kHashFinalAddend = 1376312589u;

float Saturate(float value) noexcept {
  return std::min(1.0f, std::max(0.0f, value));
}

float Fract(float value) noexcept {
  return value - std::floor(value);
}

float HashNoise(
    std::uint32_t seed,
    std::uint32_t multiplier,
    std::uint32_t addend) noexcept {
  // Unsigned overflow is intentional and reproduces the shader's 32-bit
  // v_mul_lo_u32/v_add_nc_u32 chain.
  seed ^= seed << 13u;
  auto const polynomial = seed * seed * multiplier + addend;
  auto const hash = seed * polynomial + kHashFinalAddend;
  auto const positive = hash & 0x7fffffffu;
  return static_cast<float>(static_cast<std::int32_t>(positive)) * kHashToFloat;
}

LinearRgba ApplyThemeOverlays(
    LinearRgba colour,
    ThemeOverlay const *overlays,
    std::size_t count) noexcept {
  if (!overlays) {
    return colour;
  }

  for (std::size_t index = 0; index < count; ++index) {
    auto const packed = overlays[index].premultipliedRgba;
    auto const sourceR = static_cast<float>(packed & 0xffu) * kByteToFloat;
    auto const sourceG = static_cast<float>((packed >> 8u) & 0xffu) * kByteToFloat;
    auto const sourceB = static_cast<float>((packed >> 16u) & 0xffu) * kByteToFloat;
    auto const sourceA = static_cast<float>((packed >> 24u) & 0xffu) * kByteToFloat;
    auto const destinationWeight = 1.0f - sourceA;
    colour.r = colour.r * destinationWeight + sourceR;
    colour.g = colour.g * destinationWeight + sourceG;
    colour.b = colour.b * destinationWeight + sourceB;
  }
  return colour;
}

std::uint8_t ToUnorm8(float value) noexcept {
  auto const rounded = std::lround(Saturate(value) * 255.0f);
  return static_cast<std::uint8_t>(std::clamp(rounded, 0l, 255l));
}

struct PreparedParameters {
  float width;
  float height;
  float inverseProjection00;
  float inverseProjection11;
  std::uint32_t timeSeed0;
  std::uint32_t timeSeed1;
  float lightX;
  float lightY;
};

PreparedParameters Prepare(
    std::uint32_t width,
    std::uint32_t height,
    Parameters const &parameters) noexcept {
  auto const timeFraction = Fract(parameters.timeSeconds);
  auto const orbitAngle =
      kTwoPi * kPhaseCyclesPerSecond * parameters.timeSeconds;
  return {
      static_cast<float>(width),
      static_cast<float>(height),
      1.0f / parameters.projection00,
      1.0f / parameters.projection11,
      static_cast<std::uint32_t>(
          static_cast<std::int32_t>(kHashTime0 * timeFraction)),
      static_cast<std::uint32_t>(
          static_cast<std::int32_t>(kHashTime1 * timeFraction)),
      kLightOrbitScale * std::cos(orbitAngle) + kLightOrbitOffset,
      kLightOrbitScale * std::sin(orbitAngle) + kLightOrbitOffset,
  };
}

LinearRgba EvaluatePrepared(
    float sampleX,
    float sampleY,
    Parameters const &parameters,
    PreparedParameters const &prepared) noexcept {
  auto const projectedX =
      ((2.0f * sampleX / prepared.width) - 1.0f) *
      prepared.inverseProjection00;
  auto const projectedY =
      -((2.0f * sampleY / prepared.height) - 1.0f) *
      prepared.inverseProjection11;
  auto const inverseLength =
      1.0f / std::sqrt(1.0f + projectedX * projectedX + projectedY * projectedY);
  auto const directionX = Saturate(0.5f * projectedX * inverseLength + 0.5f);
  auto const directionY = Saturate(0.5f * projectedY * inverseLength + 0.5f);

  // SV_Position is converted with the shader's truncating float-to-uint
  // operations before both hash phases are added.
  auto const pixelIndex =
      static_cast<std::uint32_t>(prepared.width * sampleY) +
      static_cast<std::uint32_t>(sampleX);
  auto const noise0 = HashNoise(
      pixelIndex + prepared.timeSeed0, kHashMultiplier0, kHashAddend0);
  auto const noise1 = HashNoise(
      pixelIndex + prepared.timeSeed1, kHashMultiplier1, kHashAddend1);

  auto const foldY = kDirectionScale * directionY + noise0 + kFoldOffset;
  auto const foldX = directionX + noise1;
  auto const deltaX = foldX + prepared.lightX;
  auto const deltaY = prepared.lightY - foldY;
  auto const radiusSquared = deltaX * deltaX + deltaY * deltaY;

  // v_exp_f32 is base-2. -14.42695045 therefore yields exp(-10*r^2),
  // not exp(-14.42695045*r^2).
  auto const lightAmount = std::exp2(kGaussianExp2Scale * radiusSquared);
  auto const blend = 1.0f + foldY;
  auto const &background0 = parameters.palette.background0;
  auto const &background1 = parameters.palette.background1;
  auto const &light = parameters.palette.backgroundLight;

  LinearRgba colour{
      Saturate(background0.r + blend * (background1.r - background0.r) +
               light.r * lightAmount),
      Saturate(background0.g + blend * (background1.g - background0.g) +
               light.g * lightAmount),
      Saturate(background0.b + blend * (background1.b - background0.b) +
               light.b * lightAmount),
      1.0f,
  };
  colour = ApplyThemeOverlays(
      colour, parameters.themeOverlays, parameters.themeOverlayCount);

  colour.r *= parameters.opacity;
  colour.g *= parameters.opacity;
  colour.b *= parameters.opacity;
  colour.a = parameters.opacity;
  return colour;
}

} // namespace

Palette Firmware1240ResetPalette() noexcept {
  constexpr float scale = 1.0f / 255.0f;
  return {
      {-20.0f * scale, -20.0f * scale, -10.0f * scale, 1.0f},
      {81.0f * scale, 160.0f * scale, 245.0f * scale, 1.0f},
      {22.0f * scale, 57.0f * scale, 79.0f * scale, 1.0f},
      {90.0f * scale, 60.0f * scale, 230.0f * scale, 1.0f},
      {15.0f * scale, 15.0f * scale, 15.0f * scale, 1.0f},
      {123.0f * scale, 123.0f * scale, 123.0f * scale, 1.0f},
  };
}

LinearRgba EvaluateBackgroundPixel(
    float sampleX,
    float sampleY,
    std::uint32_t width,
    std::uint32_t height,
    Parameters const &parameters) noexcept {
  if (width == 0 || height == 0 || parameters.projection00 == 0.0f ||
      parameters.projection11 == 0.0f) {
    return {};
  }
  return EvaluatePrepared(
      sampleX, sampleY, parameters, Prepare(width, height, parameters));
}

bool RenderBackgroundBgra8Premultiplied(
    std::uint8_t *destination,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t stride,
    Parameters const &parameters) noexcept {
  if (!destination || width == 0 || height == 0 ||
      width > std::numeric_limits<std::uint32_t>::max() / 4u ||
      stride < width * 4u || parameters.projection00 == 0.0f ||
      parameters.projection11 == 0.0f || !std::isfinite(parameters.projection00) ||
      !std::isfinite(parameters.projection11)) {
    return false;
  }

  auto const prepared = Prepare(width, height, parameters);
  for (std::uint32_t y = 0; y < height; ++y) {
    auto *row = destination + static_cast<std::size_t>(y) * stride;
    for (std::uint32_t x = 0; x < width; ++x) {
      auto const colour = EvaluatePrepared(
          static_cast<float>(x) + 0.5f,
          static_cast<float>(y) + 0.5f,
          parameters,
          prepared);
      auto *pixel = row + static_cast<std::size_t>(x) * 4u;
      pixel[0] = ToUnorm8(colour.b);
      pixel[1] = ToUnorm8(colour.g);
      pixel[2] = ToUnorm8(colour.r);
      pixel[3] = ToUnorm8(colour.a);
    }
  }
  return true;
}

} // namespace Prosperismo::FirstWave
