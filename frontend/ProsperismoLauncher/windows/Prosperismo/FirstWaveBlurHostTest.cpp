// Host-buildable checks for the recovered FirstWave blur kernel.
//
//   clang++ -std=c++20 -O2 -Wall -Wextra -I windows/Prosperismo \
//       windows/Prosperismo/FirstWaveBlurHostTest.cpp \
//       windows/Prosperismo/FirstWaveBlur.cpp -o /tmp/fwblur && /tmp/fwblur
//
// Not part of Prosperismo.vcxproj: a standalone program, built on demand.

#include "FirstWaveBlur.h"

#include <cmath>
#include <cstdio>

namespace B = Prosperismo::FirstWave::Blur;

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
  // The kernel is normalized: the firmware weights sum to 1.
  {
    double sum = 0.0;
    for (float weight : B::Kernel()) {
      sum += weight;
    }
    CheckClose(sum, 1.0, 1e-7, "kernel sums to 1");
  }

  Check(B::Kernel().size() == B::kTapCount, "kernel has 13 taps");
  Check(B::kTapCount == 13, "tap count");

  // Symmetric, and monotonically decreasing away from the centre.
  for (int k = 1; k <= static_cast<int>(B::kRadiusTaps); ++k) {
    CheckClose(B::TapWeight(k), B::TapWeight(-k), 0.0, "weights are symmetric");
    Check(B::TapWeight(k) < B::TapWeight(k - 1), "weights decrease outward");
  }
  CheckClose(B::TapWeight(7), 0.0, 0.0, "outside the radius the weight is 0");
  CheckClose(B::TapWeight(-7), 0.0, 0.0, "outside the radius the weight is 0");

  // The weights fit a Gaussian: sigma implied by every tap agrees.
  {
    double const centre = B::TapWeight(0);
    double reference = 0.0;
    for (int k = 1; k <= static_cast<int>(B::kRadiusTaps); ++k) {
      double const sigma =
          std::sqrt(-(static_cast<double>(k) * k) /
                    (2.0 * std::log(B::TapWeight(k) / centre)));
      if (k == 1) {
        reference = sigma;
        CheckClose(sigma, 3.8462, 1e-3, "implied sigma is 3.8462 texels");
      } else {
        CheckClose(sigma, reference, 1e-3, "sigma is consistent across taps");
      }
    }
  }

  // Tap offsets are exactly k texels of the native 4K width, scaled by width.
  for (int k = -static_cast<int>(B::kRadiusTaps);
       k <= static_cast<int>(B::kRadiusTaps); ++k) {
    CheckClose(B::TapOffset(k, 1.0f), static_cast<double>(k) / 3840.0, 1e-9,
               "tap offset is k/3840 at unit width");
  }
  CheckClose(B::TapOffset(3, 2.0f), 2.0 * 3.0 / 3840.0, 1e-9,
             "offsets scale linearly with width");
  CheckClose(B::TapOffset(0, 5.0f), 0.0, 0.0, "centre tap has no offset");
  CheckClose(B::TapOffset(99, 1.0f), 0.0, 0.0, "out-of-range tap has no offset");

  // Radial mask: full width inside the radius, decaying to zero beyond it.
  {
    B::RadialParameters const parameters{0.5f, 0.5f, 0.25f, 1.0f};
    CheckClose(B::ModulatedWidth(0.5f, 0.5f, parameters), 1.0, 1e-6,
               "full width at the centre");
    CheckClose(B::ModulatedWidth(0.5f + 0.25f, 0.5f, parameters), 1.0, 1e-6,
               "still full width at the inner radius");
    // 1/8 of a UV unit past the radius saturates the falloff.
    CheckClose(B::ModulatedWidth(0.5f + 0.25f + 0.125f, 0.5f, parameters), 0.0,
               1e-6, "width reaches zero an eighth past the radius");
    Check(B::ModulatedWidth(0.5f + 0.30f, 0.5f, parameters) <
              B::ModulatedWidth(0.5f + 0.27f, 0.5f, parameters),
          "width decreases with distance");
    // Symmetric about the centre in both axes.
    CheckClose(B::ModulatedWidth(0.5f, 0.5f + 0.3f, parameters),
               B::ModulatedWidth(0.5f, 0.5f - 0.3f, parameters), 1e-6,
               "mask is radially symmetric");
  }

  // The early-out threshold decides blurred vs single-sample.
  Check(!B::IsBlurred(0.0f), "zero width is not blurred");
  Check(!B::IsBlurred(0.3f), "below threshold is not blurred");
  Check(B::IsBlurred(0.5f), "above threshold is blurred");
  Check(B::IsBlurred(B::kMinimumWidth), "equality takes the blur path");

  // Schlick Fresnel: exact partition, correct endpoints, monotonic, and an
  // index of refraction of exactly 50/49.
  {
    CheckClose(static_cast<double>(B::kFresnelF0) + B::kFresnelOneMinusF0, 1.0,
               1e-7, "F0 and (1-F0) partition exactly");
    Check(B::kFresnelExponent == 5, "Schlick exponent is 5");
    CheckClose(B::SchlickFresnel(0.0f), B::kFresnelF0, 1e-9,
               "head-on reflectance is F0");
    CheckClose(B::SchlickFresnel(1.0f), 1.0, 1e-6,
               "grazing reflectance saturates to 1");
    double previous = -1.0;
    for (float c = 0.0f; c <= 1.0f; c += 0.05f) {
      double const value = B::SchlickFresnel(c);
      Check(value >= previous, "Fresnel increases toward grazing");
      previous = value;
    }
    double const root = std::sqrt(static_cast<double>(B::kFresnelF0));
    CheckClose(root, 1.0 / 99.0, 1e-8, "sqrt(F0) is exactly 1/99");
    double const ior = (1.0 + root) / (1.0 - root);
    CheckClose(ior, 50.0 / 49.0, 1e-6, "implied IOR is exactly 50/49");
  }

  std::printf(g_failures == 0 ? "\nAll checks passed.\n" : "\n%d check(s) failed.\n",
              g_failures);
  return g_failures == 0 ? 0 : 1;
}
