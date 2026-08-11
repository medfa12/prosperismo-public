#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

// The FirstWave separable blur — the pass that spreads the lit OIT highlight
// into the visible glow ("the rays"). Recovered from fw_blurh_p / fw_blurv_p
// in the 12.40 NPXS40087 eboot; see docs/sony-shell/firstwave-decoded-passes.md.
//
// The two firmware programs are byte-identical in structure (122 instructions,
// 14 image_sample each) and differ only in which axis the tap offset is
// applied to, so one kernel description serves both.
//
// Deliberately free of platform headers so it can be unit-tested on any host.

namespace Prosperismo::FirstWave::Blur {

// Schlick Fresnel, recovered from fw_oit_p: the glint the blur below spreads
// into visible rays. F = F0 + (1 - F0) * (1 - c)^5, with F0 = (1/99)^2, i.e.
// an index of refraction of exactly 50/49. The decoded literals form an exact
// partition (F0 + (1 - F0) = 1) rather than an approximate fit.
inline constexpr float kFresnelF0 = 0.0001020303098f;
inline constexpr float kFresnelOneMinusF0 = 0.9998979568f;
inline constexpr int kFresnelExponent = 5;

// Schlick's term for a cosine already reduced to (1 - c).
float SchlickFresnel(float oneMinusCosine) noexcept;

// Tap spacing is expressed in texels of the native 4K width; the firmware
// literals are exactly k / 3840 for k = 1..6.
inline constexpr float kNativeWidthTexels = 3840.0f;
inline constexpr std::size_t kRadiusTaps = 6;
inline constexpr std::size_t kTapCount = 2 * kRadiusTaps + 1; // 13

// Normalized Gaussian weights, centre first then outward. The firmware set
// sums to 1.0 and fits a Gaussian with sigma = 3.8462 texels exactly at every
// tap. Values are the decoded IEEE-754 literals.
inline constexpr std::array<float, kRadiusTaps + 1> kWeights{{
    0.11399816721677780f, // centre
    0.11020942032337189f, // +/-1
    0.09958209842443466f, // +/-2
    0.08409796655178070f, // +/-3
    0.06637910753488541f, // +/-4
    0.04896875098347664f, // +/-5
    0.03376356884837151f, // +/-6
}};

// The radial mask that modulates blur width, from the fw_blurh_p prologue:
//
//   d     = length(uv - centre)
//   t     = saturate(kFalloffScale * max(0, d - innerRadius))
//   width = maxWidth * (1 - t)
//
// Lanes whose modulated width falls below kMinimumWidth skip the 13-tap loop
// entirely and take a single unblurred sample. That early-out is why the glow
// is localised rather than covering the frame.
inline constexpr float kFalloffScale = 8.0f;
inline constexpr float kMinimumWidth = 0.383999974f;

struct RadialParameters {
  // BlurParameters.xy: mask centre, in the same UV space as the samples.
  float centreU{};
  float centreV{};
  // BlurParameters.z: radius inside which the blur is at full width.
  float innerRadius{};
  // BlurParameters.w: the maximum blur width.
  float maxWidth{};
};

// Weight for a tap at signed index k in [-6, 6]. Returns 0 outside that range.
float TapWeight(int k) noexcept;

// Signed texel offset for tap k, scaled by the modulated width. Apply to the
// U coordinate for the horizontal pass and to V for the vertical one.
float TapOffset(int k, float width) noexcept;

// The modulated blur width at a sample position.
float ModulatedWidth(float u, float v, RadialParameters const &parameters) noexcept;

// True when the sample takes the 13-tap path rather than a single unblurred
// fetch.
bool IsBlurred(float width) noexcept;

// Convenience: the full normalized kernel, centre at index kRadiusTaps.
std::array<float, kTapCount> Kernel() noexcept;

} // namespace Prosperismo::FirstWave::Blur
