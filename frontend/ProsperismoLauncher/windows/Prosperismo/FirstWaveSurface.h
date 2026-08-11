#pragma once

#include <cstddef>
#include <cstdint>

// The FirstWave *surface* (the ripples), recovered in
// docs/sony-shell/firstwave-decoded-passes.md. This is the geometry side of
// the background: fw_flow_vl displaces a 4x4 control lattice with a single 3D
// simplex noise evaluation, and fw_flow_dv evaluates the resulting bicubic
// patch. FirstWaveBackground.h remains the separate plate (fw_background_p).
//
// The simplex noise is the public webgl-noise algorithm by Stefan Gustavson
// and Ashima Arts (MIT licence); the firmware carries exactly its published
// constants. No Sony program is translated here — the recovered facts are the
// algorithm's identity, the single-evaluation count, the entrance envelope,
// and the one-sided (squared) displacement.
//
// Deliberately free of platform headers so it can be unit-tested on any host.

namespace Prosperismo::FirstWave::Surface {

// Recovered from the fw_flow_vl opening sequence. The amplitude envelope is
// amp = kEnvelopeCoefficient * e^3 + kEnvelopeSteady, with
// e = clamp(1 - kEnvelopeRate * t, 0, 1), so it starts at 0.56 and settles to
// 0.16 ten seconds after HOME appears.
inline constexpr float kEnvelopeRate = 0.1f;
inline constexpr float kEnvelopeCoefficient = 0.4f;
inline constexpr float kEnvelopeSteady = 0.16f;

// Constant drift added to a noise input coordinate as kDriftRate * time. This
// is what keeps the resting surface moving once the envelope has decayed.
inline constexpr float kDriftRate = 0.2f;

// Final world-space scale applied to all three components of the control
// point before it is written to LDS for the hull stage.
inline constexpr float kWorldScale = 2000.0f;

// A 4x4 bicubic patch: sixteen control points, matching the sixteen regularly
// strided control-point reads in fw_flow_dv.
inline constexpr std::size_t kPatchSide = 4;
inline constexpr std::size_t kPatchControlPointCount = kPatchSide * kPatchSide;

// fw_flow_h writes six tessellation factors per patch - four outer edges plus
// two inner, the quad-domain record, contiguous at a 24-byte stride - and
// every one of them is 12.0. The subdivision is uniform and fixed: not
// adaptive, not distance-based, and not driven by any constant-buffer value.
inline constexpr float kTessellationFactor = 12.0f;
inline constexpr std::size_t kOuterTessellationFactors = 4;
inline constexpr std::size_t kInnerTessellationFactors = 2;
inline constexpr std::size_t kTessellationFactorStrideBytes = 24;

// Vertices along one edge of a fully tessellated patch.
inline constexpr std::size_t kTessellatedVerticesPerEdge = 13; // 12 spans + 1

struct FieldOptions {
  // Spatial frequency of the noise lattice. NOT firmware-recovered.
  float frequency{0.85f};
  // Extra multiplier applied on top of the recovered envelope.
  float amplitude{1.0f};
  // Rate at which time advances along the third noise axis.
  float timeScale{0.12f};
  // Apply the recovered entrance envelope.
  bool useEnvelope{true};
};

// Gustavson/Ashima 3D simplex noise. Deterministic and roughly [-1, 1].
float SimplexNoise3(float x, float y, float z) noexcept;

// The recovered entrance envelope: 0.56 decaying cubically to 0.16 over ten
// seconds. Clamped, so times before 0 and after 10s are well defined.
float EnvelopeAmplitude(float timeSeconds) noexcept;

// Displacement of one control point. The firmware squares the scaled noise,
// so the result is one-sided: never negative. The surface bulges along the
// positive direction only, which is why it reads as swells rather than
// symmetric ripples.
float ControlPointDisplacement(
    float x,
    float y,
    float timeSeconds,
    FieldOptions const &options = {}) noexcept;

// Cubic Bernstein basis, ordered b0..b3.
void CubicBernstein(float t, float outBasis[kPatchSide]) noexcept;

// Evaluates a row-major 4x4 bicubic patch at (u, v). Returns 0 when control
// is null.
float EvaluateBicubicPatch(
    float const *control,
    std::size_t controlCount,
    float u,
    float v) noexcept;

// Fills the sixteen control heights for one patch at a given time. Returns
// false without touching the destination when destinationCount is wrong.
bool BuildControlLattice(
    float *destination,
    std::size_t destinationCount,
    float originX,
    float originY,
    float spacing,
    float timeSeconds,
    FieldOptions const &options = {}) noexcept;

} // namespace Prosperismo::FirstWave::Surface
