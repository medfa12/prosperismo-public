// The precompiled header pulls in the Windows SDK. Guarding it keeps this
// translation unit buildable on a non-Windows host; MSVC always defines
// _WIN32, so the /Yu precompiled header is still honoured on the real build.
#ifdef _WIN32
#include "pch.h"
#endif

#include "FirstWaveParticle.h"

#include <algorithm>
#include <cmath>

namespace Prosperismo::FirstWave::Particle {
namespace {

// The two magic-number divisions the vertex programs use, reproduced with the
// same operand widths the ISA uses rather than with C division, so the host
// test can confirm they agree with the obvious arithmetic.
constexpr std::uint64_t kLehmerQuotientMagic = 0x80000001ull;
constexpr std::uint32_t kThousandMagic = 0x10624dd3u;

float Length2(float x, float y) noexcept { return x * x + y * y; }

} // namespace

// ---------------------------------------------------------------------------
// Billboard expansion
// ---------------------------------------------------------------------------

std::uint32_t ParticleIndexForVertex(std::uint32_t vertexIndex) noexcept {
  return vertexIndex / static_cast<std::uint32_t>(kVerticesPerParticle);
}

std::uint32_t CornerIndexForVertex(std::uint32_t vertexIndex) noexcept {
  return vertexIndex % static_cast<std::uint32_t>(kVerticesPerParticle);
}

Corner CornerForVertex(std::uint32_t vertexIndex) noexcept {
  return kQuadCorners[CornerIndexForVertex(vertexIndex)];
}

// ---------------------------------------------------------------------------
// Size lottery
// ---------------------------------------------------------------------------

std::uint32_t ReduceLehmer(std::uint32_t product) noexcept {
  std::uint64_t const wide =
      kLehmerQuotientMagic * static_cast<std::uint64_t>(product) + kLehmerQuotientMagic;
  std::uint32_t const quotient = static_cast<std::uint32_t>(wide >> 32) >> 30;
  return product - quotient * kLehmerModulus;
}

std::uint32_t SizeBucket(std::uint32_t seed) noexcept {
  std::uint32_t const product = kLehmerMultiplier * seed; // 32-bit, truncating
  std::uint32_t const reduced = ReduceLehmer(product);
  std::uint32_t const high =
      static_cast<std::uint32_t>((static_cast<std::uint64_t>(kThousandMagic) * reduced) >> 32);
  std::uint32_t const quotient = high >> 6;
  return reduced - quotient * kSizeBuckets;
}

float RandomSize(std::uint32_t seed, float minSize, float maxSize) noexcept {
  float const bucket = static_cast<float>(SizeBucket(seed));
  return kBucketScale * (bucket * (maxSize - minSize)) + minSize;
}

// ---------------------------------------------------------------------------
// Minimum-screen-size clamp
// ---------------------------------------------------------------------------

float MinimumSizeScale(float axisLength, float minimumLength) noexcept {
  float const biased = axisLength + kLengthEpsilon;
  return std::max(minimumLength, biased) / biased;
}

float MinimumLength(float size, float gradient, float gradientMinimum) noexcept {
  return gradient * (gradientMinimum - size) + size;
}

// ---------------------------------------------------------------------------
// Projection
// ---------------------------------------------------------------------------

float ClipZ(float clipW) noexcept { return kDepthA * clipW - kDepthB; }

float NdcZ(float clipW) noexcept { return ClipZ(clipW) / clipW; }

float ProjectionScaleY(float verticalFovDegrees) noexcept {
  float const revolutions = kDegreesToRevolutionsHalf * verticalFovDegrees;
  float const radians = revolutions * kTwoPi;
  return 0.5f * std::cos(radians) / std::sin(radians);
}

float ProjectionScaleX(float verticalFovDegrees, float aspect) noexcept {
  return ProjectionScaleY(verticalFovDegrees) / aspect;
}

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------

float Saturate(float value) noexcept {
  return std::min(std::max(value, 0.0f), 1.0f);
}

float SmoothStep(float t) noexcept {
  float const c = Saturate(t);
  return c * c * (3.0f - 2.0f * c);
}

// ---------------------------------------------------------------------------
// particle_p
// ---------------------------------------------------------------------------

float AnisotropicRadius(
    float cornerX,
    float cornerY,
    float stretchX,
    float stretchY) noexcept {
  float const radius2 = Length2(cornerX, cornerY);
  float const radius = std::sqrt(radius2);
  float directionX = 0.0f;
  float directionY = 0.0f;
  // The firmware compares against the same 1e-6 literal and substitutes zero.
  if (!(kLengthEpsilon > radius)) {
    float const inverse = 1.0f / radius;
    directionX = cornerX * inverse;
    directionY = cornerY * inverse;
  }
  float const a = std::fabs(stretchX) * directionX;
  float const b = std::fabs(stretchY) * directionY;
  return 1.0f / std::sqrt(a * a + b * b + kLengthEpsilon);
}

float GradientBlend(float gradient) noexcept {
  return SmoothStep(Saturate(kGradientBlendGain * gradient));
}

float PointInnerRadius(
    float anisotropicRadius,
    float blurBoundary,
    float gradient) noexcept {
  float const blend = GradientBlend(gradient);
  return anisotropicRadius + (blurBoundary - anisotropicRadius) * blend;
}

bool PointIsKilled(float cornerX, float cornerY) noexcept {
  // The firmware kills on `0.99 <= r`, so exactly 0.99 is killed.
  return !(std::sqrt(Length2(cornerX, cornerY)) < kPointCutoffRadius);
}

float PointShape(
    float cornerX,
    float cornerY,
    float innerRadius,
    float gradient) noexcept {
  float const radius = std::sqrt(Length2(cornerX, cornerY));
  float const denominator = std::min(kPointInnerRadiusCap, innerRadius) - kPointCutoffRadius;
  float const ramp = SmoothStep((radius - kPointCutoffRadius) / denominator);
  if (!(ramp > 0.0f)) {
    // exp2(log2(0) * k) is zero for any positive k; short-circuit rather than
    // route an infinity through std::pow.
    return 0.0f;
  }
  return std::exp2(std::log2(ramp) * (1.0f + gradient));
}

float PointLifeFade(float curLife, float renLife) noexcept {
  float const fadeIn = SmoothStep(Saturate(2.0f * curLife));
  float const fadeOut = SmoothStep(Saturate(2.0f * (renLife - curLife)));
  return fadeIn * fadeOut;
}

float PointIntensity(PointFragment const &fragment) noexcept {
  if (PointIsKilled(fragment.cornerX, fragment.cornerY)) {
    return 0.0f;
  }
  float const anisotropic = AnisotropicRadius(
      fragment.cornerX, fragment.cornerY, fragment.stretchX, fragment.stretchY);
  float const inner =
      PointInnerRadius(anisotropic, fragment.blurBoundary, fragment.gradient);
  float const shape =
      PointShape(fragment.cornerX, fragment.cornerY, inner, fragment.gradient);
  return shape * fragment.gradientBrightness *
         PointLifeFade(fragment.curLife, fragment.renLife) * fragment.lightSum;
}

Colour PaletteColour(
    std::int32_t id,
    bool useSymbolPalette,
    bool overrideThirdWarm) noexcept {
  // Both paths use C's truncating remainder, matching the firmware's signed
  // magic-number division.
  if (useSymbolPalette) {
    std::int32_t const index = id % 4;
    if (index < 0) {
      // The firmware would form a negative byte offset into an 84-byte V#;
      // an out-of-range buffer load returns zero on this hardware.
      return Colour{};
    }
    return kPalette[static_cast<std::size_t>(index)];
  }
  std::int32_t const index = id % 3;
  if (index < 0) {
    return Colour{};
  }
  if (overrideThirdWarm && index == 2) {
    return kWarmOverride;
  }
  return kPalette[static_cast<std::size_t>(4 + index)];
}

// ---------------------------------------------------------------------------
// large_particle_p
// ---------------------------------------------------------------------------

float DefocusWidth(float focusT, float k) noexcept {
  return SmoothStep(focusT) * (std::min(kDefocusKCap, k) - kDefocusKBias) -
         kDefocusMinWidth;
}

float SoftDiscEdge(float radius, float focusT, float k) noexcept {
  float const width = DefocusWidth(focusT, k);
  return SmoothStep((radius - 1.0f) / width);
}

float DiscLifeFade(float curLife, float maxLife) noexcept {
  float const fadeIn = SmoothStep(Saturate(std::max(0.0f, curLife)));
  float const fadeOut = SmoothStep(Saturate(maxLife - curLife));
  return fadeIn * fadeOut;
}

float DiscAlpha(float opacity, float edge, float lifeFade, float depthFade) noexcept {
  return opacity * edge * lifeFade * depthFade;
}

bool DiscUsesTexture(std::uint32_t flags) noexcept { return (flags & 1u) != 0u; }

float DiscTextureBlend(std::uint32_t flags) noexcept {
  std::uint32_t const weight = (flags >> 1) & 0x3ffu;
  return static_cast<float>(weight) * kTextureBlendScale;
}

// ---------------------------------------------------------------------------
// HSV
// ---------------------------------------------------------------------------

Colour HsvRadiansToRgb(float hueRadians, float saturation, float value) noexcept {
  float const wrapped = hueRadians - kTwoPi * std::floor(hueRadians / kTwoPi);
  float const sextants = kSextantScale * wrapped;
  float const index = std::floor(sextants);
  float const fraction = sextants - index;

  float const p = value * (1.0f - saturation);
  float const q = value * (1.0f - saturation * fraction);
  float const t = value * (1.0f - saturation * (1.0f - fraction));

  int const i = static_cast<int>(index);
  switch (i) {
  case 0:
    return Colour{value, t, p};
  case 1:
    return Colour{q, value, p};
  case 2:
    return Colour{p, value, t};
  case 3:
    return Colour{p, q, value};
  case 4:
    return Colour{t, p, value};
  default:
    return Colour{value, p, q};
  }
}

Hsv RgbToHsvRadians(Colour const &colour) noexcept {
  float const maximum = std::max(colour.b, std::max(colour.r, colour.g));
  float const minimum = std::min(colour.b, std::min(colour.r, colour.g));
  float const chroma = maximum - minimum;

  Hsv result{};
  result.value = maximum;
  // The firmware guards with `max > 0` and substitutes zero.
  result.saturation = (maximum > 0.0f) ? (chroma / maximum) : 0.0f;

  if (!(maximum != minimum)) {
    result.hueRadians = 0.0f;
    return result;
  }
  if (maximum == colour.r) {
    float const base = kPiOverThree * ((colour.g - colour.b) / chroma);
    result.hueRadians = (colour.g >= colour.b) ? base : (base + kTwoPi);
  } else if (maximum == colour.g) {
    result.hueRadians = kPiOverThree * ((colour.b - colour.r) / chroma) + 2.0f * kPiOverThree;
  } else {
    result.hueRadians = kPiOverThree * ((colour.r - colour.g) / chroma) + 4.0f * kPiOverThree;
  }
  return result;
}

} // namespace Prosperismo::FirstWave::Particle
