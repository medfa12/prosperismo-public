// The precompiled header pulls in the Windows SDK. Guarding it keeps this
// translation unit buildable on a non-Windows host so the recovered maths can
// be unit-tested anywhere; MSVC always defines _WIN32, so the /Yu precompiled
// header is still honoured on the real build.
#ifdef _WIN32
#include "pch.h"
#endif

#include "FirstWaveSurface.h"

#include <algorithm>
#include <cmath>

namespace Prosperismo::FirstWave::Surface {
namespace {

// Published webgl-noise constants. Every one of these appears verbatim in the
// fw_flow_vl instruction stream; see docs/sony-shell/firstwave-decoded-passes.md.
constexpr float kMod289 = 289.0f;
constexpr float kPermuteScale = 34.0f;
constexpr float kTaylorInvSqrtA = 1.79284291400159f;
constexpr float kTaylorInvSqrtB = 0.85373472095314f;
constexpr float kCornerFalloff = 0.6f; // 3D variant; the 2D one uses 0.5
constexpr float kOutputScale = 42.0f;
constexpr float kF3 = 1.0f / 3.0f; // skew
constexpr float kG3 = 1.0f / 6.0f; // unskew
constexpr float kNsX = 2.0f / 7.0f;
constexpr float kNsY = 0.5f / 7.0f - 1.0f;

// The GPU form multiplies by reciprocals. Do NOT mirror that on the CPU:
// 196 * (1/49) evaluates to 3.9999... so floor() returns 3, the cell index
// escapes its 0..48 range, and taylorInvSqrt - a series valid only near
// |p| = 1 - is evaluated far outside its domain and returns a negative scale.
// The fault is silent: the field still looks like noise. Use exact division.
float Mod289(float x) noexcept {
  return x - std::floor(x / kMod289) * kMod289;
}

float Permute(float x) noexcept {
  return Mod289((x * kPermuteScale + 1.0f) * x);
}

float TaylorInvSqrt(float r) noexcept {
  return kTaylorInvSqrtA - kTaylorInvSqrtB * r;
}

// One simplex corner's gradient contribution. The permutation chain takes a
// distinct offset per axis; collapsing them into a single per-corner scalar
// selects wrong gradients and widens the output range past [-1, 1].
float CornerGradient(
    float ii, float jj, float kk,
    float offsetX, float offsetY, float offsetZ,
    float dx, float dy, float dz) noexcept {
  float const p =
      Permute(Permute(Permute(kk + offsetZ) + jj + offsetY) + ii + offsetX);
  float const cell = p - 49.0f * std::floor(p / 49.0f);
  float const xFloor = std::floor(cell / 7.0f);
  float const yFloor = std::floor(cell - 7.0f * xFloor);
  float const gx = xFloor * kNsX + kNsY;
  float const gy = yFloor * kNsX + kNsY;
  float const h = 1.0f - std::fabs(gx) - std::fabs(gy);
  // sh = -step(h, 0): fold the gradient back inside the octahedron.
  float const sh = h < 0.0f ? -1.0f : 0.0f;
  float const ax = gx + (std::floor(gx) * 2.0f + 1.0f) * sh;
  float const ay = gy + (std::floor(gy) * 2.0f + 1.0f) * sh;
  float const norm = TaylorInvSqrt(ax * ax + ay * ay + h * h);
  return (ax * dx + ay * dy + h * dz) * norm;
}

} // namespace

float SimplexNoise3(float x, float y, float z) noexcept {
  float const s = (x + y + z) * kF3;
  float const i = std::floor(x + s);
  float const j = std::floor(y + s);
  float const k = std::floor(z + s);
  float const t = (i + j + k) * kG3;
  float const x0 = x - (i - t);
  float const y0 = y - (j - t);
  float const z0 = z - (k - t);

  float i1{}, j1{}, k1{}, i2{}, j2{}, k2{};
  if (x0 >= y0) {
    if (y0 >= z0) {
      i1 = 1.0f; j1 = 0.0f; k1 = 0.0f; i2 = 1.0f; j2 = 1.0f; k2 = 0.0f;
    } else if (x0 >= z0) {
      i1 = 1.0f; j1 = 0.0f; k1 = 0.0f; i2 = 1.0f; j2 = 0.0f; k2 = 1.0f;
    } else {
      i1 = 0.0f; j1 = 0.0f; k1 = 1.0f; i2 = 1.0f; j2 = 0.0f; k2 = 1.0f;
    }
  } else if (y0 < z0) {
    i1 = 0.0f; j1 = 0.0f; k1 = 1.0f; i2 = 0.0f; j2 = 1.0f; k2 = 1.0f;
  } else if (x0 < z0) {
    i1 = 0.0f; j1 = 1.0f; k1 = 0.0f; i2 = 0.0f; j2 = 1.0f; k2 = 1.0f;
  } else {
    i1 = 0.0f; j1 = 1.0f; k1 = 0.0f; i2 = 1.0f; j2 = 1.0f; k2 = 0.0f;
  }

  float const ii = Mod289(i);
  float const jj = Mod289(j);
  float const kk = Mod289(k);

  struct Corner {
    float ox, oy, oz, dx, dy, dz;
  };
  Corner const corners[4] = {
      {0.0f, 0.0f, 0.0f, x0, y0, z0},
      {i1, j1, k1, x0 - i1 + kG3, y0 - j1 + kG3, z0 - k1 + kG3},
      {i2, j2, k2, x0 - i2 + 2.0f * kG3, y0 - j2 + 2.0f * kG3, z0 - k2 + 2.0f * kG3},
      {1.0f, 1.0f, 1.0f, x0 - 1.0f + 3.0f * kG3, y0 - 1.0f + 3.0f * kG3,
       z0 - 1.0f + 3.0f * kG3},
  };

  float total = 0.0f;
  for (Corner const &corner : corners) {
    float const m = std::max(
        kCornerFalloff -
            (corner.dx * corner.dx + corner.dy * corner.dy + corner.dz * corner.dz),
        0.0f);
    if (m <= 0.0f) {
      continue;
    }
    total += m * m * m * m *
             CornerGradient(ii, jj, kk, corner.ox, corner.oy, corner.oz,
                            corner.dx, corner.dy, corner.dz);
  }
  return kOutputScale * total;
}

float EnvelopeAmplitude(float timeSeconds) noexcept {
  float const e =
      std::min(std::max(1.0f - kEnvelopeRate * timeSeconds, 0.0f), 1.0f);
  return kEnvelopeCoefficient * e * e * e + kEnvelopeSteady;
}

float ControlPointDisplacement(
    float x,
    float y,
    float timeSeconds,
    FieldOptions const &options) noexcept {
  float const drift = kDriftRate * timeSeconds;
  float const n = SimplexNoise3(
      x * options.frequency + drift,
      y * options.frequency,
      timeSeconds * options.timeScale);
  float const envelope =
      options.useEnvelope ? EnvelopeAmplitude(timeSeconds) : 1.0f;
  // Squared: the firmware's displacement is one-sided.
  return options.amplitude * envelope * n * n;
}

void CubicBernstein(float t, float outBasis[kPatchSide]) noexcept {
  float const u = 1.0f - t;
  outBasis[0] = u * u * u;
  outBasis[1] = 3.0f * u * u * t;
  outBasis[2] = 3.0f * u * t * t;
  outBasis[3] = t * t * t;
}

float EvaluateBicubicPatch(
    float const *control,
    std::size_t controlCount,
    float u,
    float v) noexcept {
  if (control == nullptr || controlCount != kPatchControlPointCount) {
    return 0.0f;
  }
  float basisU[kPatchSide]{};
  float basisV[kPatchSide]{};
  CubicBernstein(u, basisU);
  CubicBernstein(v, basisV);
  float sum = 0.0f;
  for (std::size_t row = 0; row < kPatchSide; ++row) {
    for (std::size_t col = 0; col < kPatchSide; ++col) {
      sum += control[row * kPatchSide + col] * basisV[row] * basisU[col];
    }
  }
  return sum;
}

bool BuildControlLattice(
    float *destination,
    std::size_t destinationCount,
    float originX,
    float originY,
    float spacing,
    float timeSeconds,
    FieldOptions const &options) noexcept {
  if (destination == nullptr || destinationCount != kPatchControlPointCount) {
    return false;
  }
  for (std::size_t row = 0; row < kPatchSide; ++row) {
    for (std::size_t col = 0; col < kPatchSide; ++col) {
      destination[row * kPatchSide + col] = ControlPointDisplacement(
          originX + static_cast<float>(col) * spacing,
          originY + static_cast<float>(row) * spacing,
          timeSeconds,
          options);
    }
  }
  return true;
}

} // namespace Prosperismo::FirstWave::Surface
