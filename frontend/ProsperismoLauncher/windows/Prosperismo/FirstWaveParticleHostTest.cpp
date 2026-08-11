// Host-buildable checks for the recovered FirstWave particle draw maths.
//
//   clang++ -std=c++20 -O2 -Wall -Wextra -I windows/Prosperismo \
//       windows/Prosperismo/FirstWaveParticleHostTest.cpp \
//       windows/Prosperismo/FirstWaveParticle.cpp -o /tmp/fwparticle && /tmp/fwparticle
//
// Not part of Prosperismo.vcxproj: a standalone program, built on demand.

#include "FirstWaveParticle.h"

#include <cmath>
#include <cstdio>
#include <set>
#include <utility>

namespace P = Prosperismo::FirstWave::Particle;

namespace {

int g_failures = 0;

void Check(bool condition, char const *what) {
  if (!condition) {
    std::printf("FAIL: %s\n", what);
    ++g_failures;
  }
}

void CheckClose(double actual, double expected, double tolerance, char const *what) {
  if (!(std::fabs(actual - expected) <= tolerance)) {
    std::printf("FAIL: %s (actual %.10g, expected %.10g)\n", what, actual, expected);
    ++g_failures;
  }
}

} // namespace

int main() {
  // -------------------------------------------------------------------------
  // Billboard expansion: six vertices per particle, two triangles over [-1,1].
  // -------------------------------------------------------------------------
  Check(P::kVerticesPerParticle == 6, "six vertices per particle");
  for (std::uint32_t v = 0; v < 30; ++v) {
    Check(P::ParticleIndexForVertex(v) == v / 6, "particle index is tid/6");
    Check(P::CornerIndexForVertex(v) == v % 6, "corner index is tid%6");
  }
  {
    // Every corner is a sign pair; the six entries cover the four distinct
    // corners with the shared diagonal appearing twice.
    std::set<std::pair<float, float>> distinct;
    for (auto const &c : P::kQuadCorners) {
      Check(std::fabs(c.x) == 1.0f && std::fabs(c.y) == 1.0f,
            "corner offsets are unit signs");
      distinct.emplace(c.x, c.y);
    }
    Check(distinct.size() == 4, "six vertices cover four distinct corners");
    // The strip is two triangles: vertices 1,2 repeat as 3,4.
    Check(P::kQuadCorners[1].x == P::kQuadCorners[3].x &&
              P::kQuadCorners[1].y == P::kQuadCorners[3].y,
          "second triangle reuses the shared edge");
    Check(P::kQuadCorners[2].x == P::kQuadCorners[4].x &&
              P::kQuadCorners[2].y == P::kQuadCorners[4].y,
          "second triangle reuses the shared edge");
  }
  // The radius at a corner is sqrt(2), well outside the 0.99 kill radius, so
  // the disc is inscribed and the quad's own corners are always discarded.
  Check(P::PointIsKilled(1.0f, 1.0f), "quad corners fall outside the disc");
  Check(!P::PointIsKilled(0.0f, 0.0f), "the quad centre survives");
  Check(P::PointIsKilled(0.99f, 0.0f), "the cutoff radius itself is killed");

  // -------------------------------------------------------------------------
  // Size lottery: the ISA's magic-number reductions agree with plain modulo.
  // -------------------------------------------------------------------------
  {
    std::uint32_t const probes[] = {0u,          1u,          2u,
                                    12345u,      65535u,      0x7ffffffeu,
                                    0x7fffffffu, 0x80000000u, 0xfffffffeu,
                                    0xffffffffu, 2147483646u, 2147483647u};
    for (std::uint32_t x : probes) {
      Check(P::ReduceLehmer(x) == x % P::kLehmerModulus,
            "the firmware's quotient magic reproduces mod 2^31-1");
    }
    for (std::uint32_t seed = 0; seed < 20000u; ++seed) {
      std::uint32_t const product = P::kLehmerMultiplier * seed;
      Check(P::SizeBucket(seed) == (product % P::kLehmerModulus) % P::kSizeBuckets,
            "the size bucket is (16807*seed mod 2^31-1) mod 1000");
    }
    Check(P::kLehmerMultiplier == 16807u, "Lehmer multiplier is 16807");
    Check(P::kLehmerModulus == 2147483647u, "Lehmer modulus is 2^31 - 1");
  }
  {
    // The lottery spans exactly the requested range, quantised to 1/1000.
    float lowest = 1e30f;
    float highest = -1e30f;
    std::set<int> buckets;
    for (std::uint32_t seed = 1; seed < 50000u; ++seed) {
      buckets.insert(static_cast<int>(P::SizeBucket(seed)));
      float const size = P::RandomSize(seed, 2.0f, 6.0f);
      lowest = std::min(lowest, size);
      highest = std::max(highest, size);
    }
    Check(buckets.size() == 1000, "all 1000 buckets are reachable");
    CheckClose(lowest, 2.0, 1e-5, "smallest size is the range minimum");
    CheckClose(highest, 2.0 + 999.0 / 1000.0 * 4.0, 1e-4,
               "largest size is one bucket short of the maximum");
    CheckClose(P::RandomSize(0u, 2.0f, 6.0f), 2.0, 1e-6,
               "a zero seed yields the range minimum");
  }

  // -------------------------------------------------------------------------
  // Minimum-screen-size clamp.
  // -------------------------------------------------------------------------
  CheckClose(P::MinimumSizeScale(1.0f, 0.5f), 1.0, 1e-6,
             "an axis already above the floor is left alone");
  CheckClose(P::MinimumSizeScale(0.25f, 1.0f), 1.0 / (0.25 + 1e-6), 1e-4,
             "an axis below the floor is scaled up to it");
  Check(P::MinimumSizeScale(0.0f, 0.0f) == 1.0f,
        "a degenerate axis does not divide by zero");
  CheckClose(P::MinimumLength(3.0f, 0.0f, 9.0f), 3.0, 1e-6,
             "with no gradient the floor is the particle size");
  CheckClose(P::MinimumLength(3.0f, 1.0f, 9.0f), 9.0, 1e-6,
             "with full gradient the floor is the constant");

  // -------------------------------------------------------------------------
  // particle_vv projection.
  // -------------------------------------------------------------------------
  {
    // 45-degree vertical field of view at 16:9, carrying an extra factor 1/2.
    CheckClose(P::kProjScaleY, 0.5 / std::tan(22.5 * M_PI / 180.0), 1e-6,
               "the y scale is half a cotangent of 22.5 degrees");
    CheckClose(P::kProjScaleX / P::kProjScaleY, 9.0 / 16.0, 1e-6,
               "the x/y ratio is exactly 9/16");
    // The runtime form in large_particle_vv reproduces the baked literals.
    CheckClose(P::ProjectionScaleY(45.0f), P::kProjScaleY, 1e-6,
               "the runtime fov path agrees with the baked y scale");
    CheckClose(P::ProjectionScaleX(45.0f, 16.0f / 9.0f), P::kProjScaleX, 1e-6,
               "the runtime fov path agrees with the baked x scale");
    // Depth: forward-Z, zero at the near plane, one at the far plane.
    CheckClose(P::NdcZ(P::kNearPlane), 0.0, 1e-5, "ndc z is 0 at the near plane");
    CheckClose(P::NdcZ(P::kFarPlane), 1.0, 1e-5, "ndc z is 1 at the far plane");
    Check(P::NdcZ(1.0f) > P::NdcZ(0.5f), "depth increases away from the eye");
    CheckClose(P::kDepthB / P::kDepthA, P::kNearPlane, 1e-6,
               "B/A recovers the near plane");
    CheckClose(P::kDepthA * P::kNearPlane / (P::kDepthA - 1.0f), P::kFarPlane, 1e-2,
               "A and the near plane recover the far plane");
  }

  // -------------------------------------------------------------------------
  // particle_p shape.
  // -------------------------------------------------------------------------
  {
    // With no stretch the anisotropic radius is the reciprocal of a unit
    // direction, i.e. 1.
    CheckClose(P::AnisotropicRadius(0.5f, 0.0f, 1.0f, 1.0f), 1.0, 1e-3,
               "an unstretched axis gives radius 1");
    CheckClose(P::AnisotropicRadius(0.0f, 0.4f, 1.0f, 1.0f), 1.0, 1e-3,
               "isotropic in both axes");
    // Stretching an axis by two halves the core radius along it.
    CheckClose(P::AnisotropicRadius(1.0f, 0.0f, 2.0f, 1.0f), 0.5, 1e-3,
               "a 2x stretch halves the radius along that axis");
    CheckClose(P::AnisotropicRadius(0.0f, 1.0f, 1.0f, 4.0f), 0.25, 1e-3,
               "a 4x stretch quarters the radius along that axis");
    // Sign does not matter: the firmware takes absolute values.
    CheckClose(P::AnisotropicRadius(1.0f, 0.0f, -2.0f, 1.0f),
               P::AnisotropicRadius(1.0f, 0.0f, 2.0f, 1.0f), 1e-6,
               "stretch is used as a magnitude");
    // The centre substitutes a zero direction, so the radius saturates.
    Check(P::AnisotropicRadius(0.0f, 0.0f, 2.0f, 2.0f) > 100.0f,
          "the exact centre falls back to the epsilon guard");
  }
  {
    // The gradient blend is a smoothstep that saturates one tenth of the way.
    CheckClose(P::GradientBlend(0.0f), 0.0, 1e-9, "no gradient, no blend");
    CheckClose(P::GradientBlend(0.1f), 1.0, 1e-6, "the blend saturates at 0.1");
    CheckClose(P::GradientBlend(2.0f), 1.0, 1e-9, "the blend stays saturated");
    CheckClose(P::PointInnerRadius(1.0f, 0.3f, 0.0f), 1.0, 1e-6,
               "without gradient the anisotropic radius wins");
    CheckClose(P::PointInnerRadius(1.0f, 0.3f, 1.0f), 0.3, 1e-6,
               "with gradient the stored radius wins");
  }
  {
    // The falloff: 1 at the core, 0 at the cutoff, monotonic between.
    float const inner = 0.5f;
    CheckClose(P::PointShape(0.0f, 0.0f, inner, 0.0f), 1.0, 1e-6,
               "full brightness at the centre");
    CheckClose(P::PointShape(inner, 0.0f, inner, 0.0f), 1.0, 1e-6,
               "still full brightness at the core radius");
    CheckClose(P::PointShape(P::kPointCutoffRadius, 0.0f, inner, 0.0f), 0.0, 1e-6,
               "zero at the cutoff radius");
    double previous = 2.0;
    for (float r = 0.5f; r <= 0.99f; r += 0.01f) {
      double const value = P::PointShape(r, 0.0f, inner, 0.0f);
      Check(value <= previous + 1e-6, "the falloff is monotonically decreasing");
      previous = value;
    }
    // The core radius is capped at 0.98 no matter how large it is asked to be.
    CheckClose(P::PointShape(0.985f, 0.0f, 5.0f, 0.0f),
               P::PointShape(0.985f, 0.0f, 0.98f, 0.0f), 1e-6,
               "the core radius saturates at 0.98");
    // The exponent sharpens the edge: a higher gradient darkens the midtones
    // but leaves the endpoints alone.
    CheckClose(P::PointShape(0.0f, 0.0f, inner, 3.0f), 1.0, 1e-6,
               "the exponent leaves the core at 1");
    Check(P::PointShape(0.8f, 0.0f, inner, 3.0f) < P::PointShape(0.8f, 0.0f, inner, 0.0f),
          "a higher gradient sharpens the edge");
    // Rotational symmetry.
    CheckClose(P::PointShape(0.6f, 0.3f, inner, 0.5f),
               P::PointShape(0.3f, 0.6f, inner, 0.5f), 1e-6,
               "the profile is radially symmetric");
  }
  {
    // Life fade: closed at both ends, open in the middle.
    CheckClose(P::PointLifeFade(0.0f, 1.0f), 0.0, 1e-9, "no light at birth");
    CheckClose(P::PointLifeFade(0.5f, 1.0f), 1.0, 1e-6, "full light at mid-life");
    CheckClose(P::PointLifeFade(1.0f, 1.0f), 0.0, 1e-9, "no light at the latch");
    Check(P::PointLifeFade(0.1f, 1.0f) > P::PointLifeFade(0.05f, 1.0f),
          "the fade-in rises");
    Check(P::PointLifeFade(0.9f, 1.0f) < P::PointLifeFade(0.8f, 1.0f),
          "the fade-out falls");
  }
  {
    P::PointFragment fragment{};
    fragment.cornerX = 0.0f;
    fragment.cornerY = 0.0f;
    fragment.stretchX = 1.0f;
    fragment.stretchY = 1.0f;
    fragment.blurBoundary = 0.5f;
    fragment.gradient = 0.0f;
    fragment.curLife = 0.5f;
    fragment.renLife = 1.0f;
    fragment.gradientBrightness = 1.0f;
    fragment.lightSum = 2.0f;
    CheckClose(P::PointIntensity(fragment), 2.0, 1e-5,
               "the centre is shape 1 times the light sum");
    fragment.cornerX = 1.0f;
    CheckClose(P::PointIntensity(fragment), 0.0, 0.0,
               "a killed fragment contributes nothing");
    fragment.cornerX = 0.0f;
    fragment.curLife = 0.0f;
    CheckClose(P::PointIntensity(fragment), 0.0, 1e-9,
               "a newborn particle contributes nothing");
  }

  // -------------------------------------------------------------------------
  // particle_p palette.
  // -------------------------------------------------------------------------
  {
    Check(P::kPalette.size() == 7, "the embedded palette has seven entries");
    // The first four are the PlayStation face-button hues; each is a clean
    // 8-bit value.
    for (std::size_t i = 0; i < 4; ++i) {
      auto const &c = P::kPalette[i];
      for (float channel : {c.r, c.g, c.b}) {
        double const byte = channel * 255.0;
        CheckClose(byte, std::round(byte), 1e-3,
                   "the symbol palette entries are exact 8-bit values");
      }
    }
    // Selection wraps with the documented moduli.
    for (std::int32_t id = 0; id < 12; ++id) {
      auto const symbol = P::PaletteColour(id, true, false);
      CheckClose(symbol.r, P::kPalette[static_cast<std::size_t>(id % 4)].r, 0.0,
                 "the symbol path selects id%4");
      auto const warm = P::PaletteColour(id, false, false);
      CheckClose(warm.r, P::kPalette[static_cast<std::size_t>(4 + id % 3)].r, 0.0,
                 "the warm path selects 4 + id%3");
    }
    // The override only touches the third warm entry, and only when armed.
    CheckClose(P::PaletteColour(2, false, true).g, P::kWarmOverride.g, 0.0,
               "the override replaces the third warm entry");
    CheckClose(P::PaletteColour(2, false, false).g, P::kPalette[6].g, 0.0,
               "without the flag the table entry stands");
    CheckClose(P::PaletteColour(1, false, true).g, P::kPalette[5].g, 0.0,
               "the override leaves the other warm entries alone");
    // The warm entries really are warm: red >= green >= blue.
    for (std::size_t i = 4; i < 7; ++i) {
      Check(P::kPalette[i].r > P::kPalette[i].g && P::kPalette[i].g > P::kPalette[i].b,
            "the warm palette runs red > green > blue");
    }
    Check(P::kWarmOverride.r > P::kWarmOverride.g &&
              P::kWarmOverride.g > P::kWarmOverride.b,
          "the override is warm too");
    Check(P::kPointOutputAlpha == 0.0f, "the point pass writes zero alpha");
  }

  // -------------------------------------------------------------------------
  // large_particle_p defocus edge.
  // -------------------------------------------------------------------------
  {
    // In focus: a razor-thin edge one ten-thousandth of the radius wide.
    CheckClose(P::DefocusWidth(0.0f, 0.0f), -P::kDefocusMinWidth, 1e-9,
               "in focus the softness width is the minimum");
    CheckClose(P::SoftDiscEdge(0.5f, 0.0f, 0.0f), 1.0, 1e-9,
               "in focus the disc is flat across its interior");
    CheckClose(P::SoftDiscEdge(1.0f, 0.0f, 0.0f), 0.0, 1e-9,
               "the edge reaches zero at radius one");
    Check(P::SoftDiscEdge(0.9999f, 0.0f, 0.0f) > 0.9f,
          "in focus the falloff is still near one just inside the rim");

    // Fully defocused: the profile becomes a full-radius ramp.
    float const width = P::DefocusWidth(1.0f, 0.0f);
    Check(width < -0.9f && width > -1.01f,
          "fully defocused the softness width approaches minus one");
    CheckClose(P::SoftDiscEdge(1.0f, 1.0f, 0.0f), 0.0, 1e-6,
               "defocused, the rim is still zero");
    CheckClose(P::SoftDiscEdge(0.0f, 1.0f, 0.0f), 1.0, 1e-6,
               "defocused, the centre is still one");
    Check(P::SoftDiscEdge(0.5f, 1.0f, 0.0f) < 0.9f,
          "defocused, the midpoint is well below one");
    Check(P::SoftDiscEdge(0.5f, 1.0f, 0.0f) < P::SoftDiscEdge(0.5f, 0.0f, 0.0f),
          "defocusing softens the disc");
    // Raising k narrows the softness again.
    Check(P::DefocusWidth(1.0f, 0.5f) > P::DefocusWidth(1.0f, 0.0f),
          "a larger k narrows the soft band");
    CheckClose(P::DefocusWidth(1.0f, 2.0f),
               static_cast<double>(P::kDefocusKCap) - P::kDefocusKBias -
                   P::kDefocusMinWidth,
               1e-9, "k is capped");
    // Monotonic in radius.
    double previous = 2.0;
    for (float r = 0.0f; r <= 1.0f; r += 0.05f) {
      double const value = P::SoftDiscEdge(r, 1.0f, 0.0f);
      Check(value <= previous + 1e-6, "the defocused profile decreases outward");
      previous = value;
    }
  }
  {
    CheckClose(P::DiscLifeFade(0.0f, 1.0f), 0.0, 1e-9, "disc fade-in starts at zero");
    CheckClose(P::DiscLifeFade(1.0f, 2.0f), 1.0, 1e-6, "disc fade-in completes at one");
    CheckClose(P::DiscLifeFade(1.0f, 1.0f), 0.0, 1e-9, "disc fade-out closes at the pair");
    CheckClose(P::DiscLifeFade(-1.0f, 5.0f), 0.0, 1e-9,
               "a negative life is clamped away by the max");
    CheckClose(P::DiscAlpha(0.5f, 0.5f, 0.5f, 0.5f), 0.0625, 1e-9,
               "disc alpha is the product of its four terms");
  }
  {
    Check(!P::DiscUsesTexture(0u), "bit 0 clear means untextured");
    Check(P::DiscUsesTexture(1u), "bit 0 set means textured");
    CheckClose(P::DiscTextureBlend(0u), 0.0, 1e-9, "a zero weight is all first texture");
    CheckClose(P::DiscTextureBlend(0x3ffu << 1), 1.0, 1e-4,
               "the maximum weight is all second texture");
    CheckClose(P::DiscTextureBlend(1u | (512u << 1)), 512.0 / 1023.0, 1e-4,
               "the weight is a 1023rd, independent of bit 0");
    CheckClose(1.0 / P::kTextureBlendScale, 1023.0, 1e-2,
               "the blend scale is exactly 1/1023");
  }

  // -------------------------------------------------------------------------
  // HSV round trip.
  // -------------------------------------------------------------------------
  {
    CheckClose(P::kSextantScale, 6.0 / (2.0 * M_PI), 1e-6,
               "the sextant scale is 6/2pi");
    CheckClose(P::kPiOverThree * 3.0, M_PI, 1e-6, "the hue step is pi/3");
    CheckClose(P::kTwoPi, 2.0 * M_PI, 1e-6, "the wrap constant is 2pi");

    for (int step = 0; step < 24; ++step) {
      float const hue = static_cast<float>(step) * P::kTwoPi / 24.0f;
      P::Colour const rgb = P::HsvRadiansToRgb(hue, 0.8f, 0.7f);
      P::Hsv const back = P::RgbToHsvRadians(rgb);
      CheckClose(back.value, 0.7, 1e-5, "value survives the round trip");
      CheckClose(back.saturation, 0.8, 1e-5, "saturation survives the round trip");
      double delta = std::fabs(back.hueRadians - hue);
      delta = std::min(delta, std::fabs(delta - 2.0 * M_PI));
      CheckClose(delta, 0.0, 1e-4, "hue survives the round trip");
    }
    // Primaries land where they should.
    P::Colour const red = P::HsvRadiansToRgb(0.0f, 1.0f, 1.0f);
    CheckClose(red.r, 1.0, 1e-6, "hue 0 is red");
    CheckClose(red.g, 0.0, 1e-6, "hue 0 is red");
    CheckClose(red.b, 0.0, 1e-6, "hue 0 is red");
    P::Colour const green = P::HsvRadiansToRgb(2.0f * P::kPiOverThree, 1.0f, 1.0f);
    CheckClose(green.g, 1.0, 1e-6, "hue 2pi/3 is green");
    P::Colour const blue = P::HsvRadiansToRgb(4.0f * P::kPiOverThree, 1.0f, 1.0f);
    CheckClose(blue.b, 1.0, 1e-6, "hue 4pi/3 is blue");
    // Zero saturation is grey at any hue.
    for (int step = 0; step < 6; ++step) {
      P::Colour const grey =
          P::HsvRadiansToRgb(static_cast<float>(step), 0.0f, 0.42f);
      CheckClose(grey.r, 0.42, 1e-6, "zero saturation is grey");
      CheckClose(grey.g, 0.42, 1e-6, "zero saturation is grey");
      CheckClose(grey.b, 0.42, 1e-6, "zero saturation is grey");
    }
    // Negative hues wrap rather than escaping the switch.
    P::Colour const wrapped = P::HsvRadiansToRgb(-P::kTwoPi + 0.25f, 0.5f, 0.5f);
    P::Colour const direct = P::HsvRadiansToRgb(0.25f, 0.5f, 0.5f);
    CheckClose(wrapped.r, direct.r, 1e-5, "negative hue wraps");
    CheckClose(wrapped.g, direct.g, 1e-5, "negative hue wraps");
    CheckClose(wrapped.b, direct.b, 1e-5, "negative hue wraps");
  }

  std::printf(g_failures == 0 ? "\nAll checks passed.\n" : "\n%d check(s) failed.\n",
              g_failures);
  return g_failures == 0 ? 0 : 1;
}
