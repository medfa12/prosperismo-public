// Host-buildable cross-validation for the recovered FirstWave surface maths.
//
// FirstWaveSurface.{h,cpp} carry no platform headers, so the recovered model
// can be checked on any machine without a Windows toolchain or a GPU. This
// file is deliberately NOT part of Prosperismo.vcxproj: it is a standalone
// program, built on demand.
//
//   clang++ -std=c++20 -O2 -Wall -Wextra \
//       -I windows/Prosperismo \
//       windows/Prosperismo/FirstWaveSurfaceHostTest.cpp \
//       windows/Prosperismo/FirstWaveSurface.cpp -o /tmp/fwsurface && /tmp/fwsurface
//
// It asserts the properties recovered in
// docs/sony-shell/firstwave-decoded-passes.md, and prints probe values that
// the TypeScript reference (src/bigPicture/shellWaveField.ts, exercised by
// __tests__/shellWaveField.test.ts) reproduces to within float precision.

#include "FirstWaveSurface.h"

#include <cmath>
#include <cstdio>
#include <initializer_list>

namespace Surface = Prosperismo::FirstWave::Surface;

namespace {

int g_failures = 0;

void Check(bool condition, char const *what) {
  if (!condition) {
    std::printf("FAIL: %s\n", what);
    ++g_failures;
  }
}

void CheckClose(float actual, float expected, float tolerance, char const *what) {
  if (!(std::fabs(actual - expected) <= tolerance)) {
    std::printf("FAIL: %s (actual %.9g, expected %.9g)\n", what, actual, expected);
    ++g_failures;
  }
}

} // namespace

int main() {
  // The entrance envelope: 0.56 decaying cubically to 0.16 over ten seconds.
  CheckClose(Surface::EnvelopeAmplitude(0.0f), 0.56f, 1e-6f, "envelope at t=0");
  CheckClose(Surface::EnvelopeAmplitude(5.0f), 0.4f * 0.125f + 0.16f, 1e-6f,
             "envelope at t=5 is cubic");
  CheckClose(Surface::EnvelopeAmplitude(10.0f), 0.16f, 1e-6f, "envelope settles");
  CheckClose(Surface::EnvelopeAmplitude(30.0f), 0.16f, 1e-6f, "envelope stays settled");
  CheckClose(Surface::EnvelopeAmplitude(-5.0f), 0.56f, 1e-6f, "envelope clamps below 0");

  // Envelope is monotonically non-increasing.
  {
    float previous = 1e9f;
    for (float t = 0.0f; t <= 12.0f; t += 0.5f) {
      float const value = Surface::EnvelopeAmplitude(t);
      Check(value <= previous + 1e-6f, "envelope decays monotonically");
      previous = value;
    }
  }

  // Noise is deterministic, finite, and bounded. A constant field would mean
  // the decode is wrong, so require real variation too.
  {
    float minimum = 1e9f;
    float maximum = -1e9f;
    for (int i = 0; i < 4000; ++i) {
      float const value = Surface::SimplexNoise3(
          static_cast<float>(i) * 0.137f,
          static_cast<float>(i) * 0.071f - 5.0f,
          static_cast<float>(i) * 0.031f);
      Check(std::isfinite(value), "noise is finite");
      minimum = std::fmin(minimum, value);
      maximum = std::fmax(maximum, value);
    }
    Check(minimum > -1.6f, "noise lower bound");
    Check(maximum < 1.6f, "noise upper bound");
    Check(maximum - minimum > 0.5f, "noise actually varies");
  }
  Check(Surface::SimplexNoise3(1.5f, -2.25f, 0.75f) ==
            Surface::SimplexNoise3(1.5f, -2.25f, 0.75f),
        "noise is deterministic");

  // Displacement is one-sided: the firmware squares the scaled noise.
  for (int i = 0; i < 500; ++i) {
    float const value = Surface::ControlPointDisplacement(
        static_cast<float>(i) * 0.31f,
        static_cast<float>(i) * 0.17f,
        static_cast<float>(i) * 0.05f);
    Check(value >= 0.0f, "displacement is never negative");
  }

  // Time drift keeps the surface moving after the envelope has settled.
  {
    float const a = Surface::ControlPointDisplacement(2.0f, 3.0f, 20.0f);
    float const b = Surface::ControlPointDisplacement(2.0f, 3.0f, 26.0f);
    CheckClose(Surface::EnvelopeAmplitude(20.0f), Surface::EnvelopeAmplitude(26.0f),
               1e-6f, "envelope is identical at t=20 and t=26");
    Check(std::fabs(a - b) > 1e-6f, "drift still animates a settled surface");
  }

  // Bernstein basis is a partition of unity and interpolates endpoints.
  {
    float basis[Surface::kPatchSide]{};
    for (float t : {0.0f, 0.25f, 0.5f, 0.75f, 1.0f}) {
      Surface::CubicBernstein(t, basis);
      CheckClose(basis[0] + basis[1] + basis[2] + basis[3], 1.0f, 1e-6f,
                 "Bernstein partition of unity");
    }
    Surface::CubicBernstein(0.0f, basis);
    CheckClose(basis[0], 1.0f, 1e-6f, "Bernstein b0 at t=0");
    Surface::CubicBernstein(1.0f, basis);
    CheckClose(basis[3], 1.0f, 1e-6f, "Bernstein b3 at t=1");
  }

  // Bicubic patch: constant lattice reproduces exactly, corners interpolate,
  // and the surface stays inside the control hull.
  {
    float flat[Surface::kPatchControlPointCount];
    for (float &value : flat) {
      value = 2.5f;
    }
    CheckClose(Surface::EvaluateBicubicPatch(flat, Surface::kPatchControlPointCount,
                                             0.3f, 0.7f),
               2.5f, 1e-6f, "constant lattice");

    float corners[Surface::kPatchControlPointCount]{};
    corners[0] = 1.0f;
    corners[15] = 5.0f;
    CheckClose(Surface::EvaluateBicubicPatch(corners, Surface::kPatchControlPointCount,
                                             0.0f, 0.0f),
               1.0f, 1e-6f, "patch interpolates first corner");
    CheckClose(Surface::EvaluateBicubicPatch(corners, Surface::kPatchControlPointCount,
                                             1.0f, 1.0f),
               5.0f, 1e-6f, "patch interpolates last corner");

    float hull[Surface::kPatchControlPointCount];
    float low = 1e9f;
    float high = -1e9f;
    for (std::size_t i = 0; i < Surface::kPatchControlPointCount; ++i) {
      hull[i] = static_cast<float>(static_cast<int>(i % 5) - 2);
      low = std::fmin(low, hull[i]);
      high = std::fmax(high, hull[i]);
    }
    for (float u = 0.0f; u <= 1.0f; u += 0.25f) {
      for (float v = 0.0f; v <= 1.0f; v += 0.25f) {
        float const h = Surface::EvaluateBicubicPatch(
            hull, Surface::kPatchControlPointCount, u, v);
        Check(h >= low - 1e-5f && h <= high + 1e-5f, "patch stays in control hull");
      }
    }
  }

  // Defensive contracts.
  Check(Surface::EvaluateBicubicPatch(nullptr, Surface::kPatchControlPointCount,
                                      0.5f, 0.5f) == 0.0f,
        "null control returns 0");
  {
    float three[3]{};
    Check(Surface::EvaluateBicubicPatch(three, 3, 0.5f, 0.5f) == 0.0f,
          "wrong control count returns 0");
    Check(!Surface::BuildControlLattice(three, 3, 0.0f, 0.0f, 1.0f, 0.0f),
          "wrong lattice count is rejected");
    Check(!Surface::BuildControlLattice(nullptr, Surface::kPatchControlPointCount,
                                        0.0f, 0.0f, 1.0f, 0.0f),
          "null lattice is rejected");
  }

  // Lattice fills all sixteen control points and evolves with time.
  {
    float a[Surface::kPatchControlPointCount]{};
    float b[Surface::kPatchControlPointCount]{};
    Check(Surface::BuildControlLattice(a, Surface::kPatchControlPointCount, 0.0f,
                                       0.0f, 1.0f, 0.0f),
          "lattice builds at t=0");
    Check(Surface::BuildControlLattice(b, Surface::kPatchControlPointCount, 0.0f,
                                       0.0f, 1.0f, 6.0f),
          "lattice builds at t=6");
    bool differs = false;
    for (std::size_t i = 0; i < Surface::kPatchControlPointCount; ++i) {
      differs = differs || std::fabs(a[i] - b[i]) > 1e-6f;
    }
    Check(differs, "lattice evolves with time");
  }

  // Tessellation: uniform 12x12, six factors per patch in a 24-byte record.
  {
    CheckClose(Surface::kTessellationFactor, 12.0f, 0.0f, "tessellation factor is 12");
    Check(Surface::kOuterTessellationFactors + Surface::kInnerTessellationFactors == 6,
          "quad domain has six factors");
    Check(Surface::kTessellationFactorStrideBytes ==
              (Surface::kOuterTessellationFactors +
               Surface::kInnerTessellationFactors) * sizeof(float),
          "six floats occupy the 24-byte stride");
    Check(Surface::kTessellatedVerticesPerEdge ==
              static_cast<std::size_t>(Surface::kTessellationFactor) + 1,
          "12 spans give 13 vertices per edge");

    // Evaluating the patch across the tessellated grid must stay finite and
    // inside the control hull at every generated vertex.
    float lattice[Surface::kPatchControlPointCount]{};
    Check(Surface::BuildControlLattice(lattice, Surface::kPatchControlPointCount,
                                       0.0f, 0.0f, 1.0f, 3.0f),
          "lattice for tessellation check");
    float low = 1e9f;
    float high = -1e9f;
    for (float value : lattice) {
      low = std::fmin(low, value);
      high = std::fmax(high, value);
    }
    std::size_t const edge = Surface::kTessellatedVerticesPerEdge;
    for (std::size_t iu = 0; iu < edge; ++iu) {
      for (std::size_t iv = 0; iv < edge; ++iv) {
        float const u = static_cast<float>(iu) / Surface::kTessellationFactor;
        float const v = static_cast<float>(iv) / Surface::kTessellationFactor;
        float const h = Surface::EvaluateBicubicPatch(
            lattice, Surface::kPatchControlPointCount, u, v);
        Check(std::isfinite(h), "tessellated vertex is finite");
        Check(h >= low - 1e-5f && h <= high + 1e-5f,
              "tessellated vertex stays in the control hull");
      }
    }
  }

  // Probes cross-checked against the TypeScript reference.
  std::printf("ENV %.9g %.9g %.9g %.9g\n", Surface::EnvelopeAmplitude(0.0f),
              Surface::EnvelopeAmplitude(5.0f), Surface::EnvelopeAmplitude(10.0f),
              Surface::EnvelopeAmplitude(30.0f));
  float const probes[][3] = {{1.5f, -2.25f, 0.75f},
                             {3.1f, 4.2f, 0.0f},
                             {0.0f, 0.0f, 0.0f},
                             {12.34f, -7.89f, 3.5f},
                             {-4.2f, 8.8f, -1.25f}};
  for (auto const &probe : probes) {
    std::printf("N %.9g\n", Surface::SimplexNoise3(probe[0], probe[1], probe[2]));
  }

  std::printf(g_failures == 0 ? "\nAll checks passed.\n" : "\n%d check(s) failed.\n",
              g_failures);
  return g_failures == 0 ? 0 : 1;
}
