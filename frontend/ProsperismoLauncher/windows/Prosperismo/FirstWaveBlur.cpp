// The precompiled header pulls in the Windows SDK. Guarding it keeps this
// translation unit buildable on a non-Windows host; MSVC always defines
// _WIN32, so the /Yu precompiled header is still honoured on the real build.
#ifdef _WIN32
#include "pch.h"
#endif

#include "FirstWaveBlur.h"

#include <algorithm>
#include <cmath>
#include <cstdlib>

namespace Prosperismo::FirstWave::Blur {

float SchlickFresnel(float oneMinusCosine) noexcept {
  float const base = std::fabs(oneMinusCosine);
  float power = 1.0f;
  for (int i = 0; i < kFresnelExponent; ++i) {
    power *= base;
  }
  return kFresnelOneMinusF0 * power + kFresnelF0;
}

float TapWeight(int k) noexcept {
  int const magnitude = std::abs(k);
  if (magnitude > static_cast<int>(kRadiusTaps)) {
    return 0.0f;
  }
  return kWeights[static_cast<std::size_t>(magnitude)];
}

float TapOffset(int k, float width) noexcept {
  if (std::abs(k) > static_cast<int>(kRadiusTaps)) {
    return 0.0f;
  }
  return width * static_cast<float>(k) / kNativeWidthTexels;
}

float ModulatedWidth(
    float u,
    float v,
    RadialParameters const &parameters) noexcept {
  float const du = u - parameters.centreU;
  float const dv = v - parameters.centreV;
  float const distance = std::sqrt(du * du + dv * dv);
  float const beyond = std::max(0.0f, distance - parameters.innerRadius);
  float const falloff = std::min(std::max(kFalloffScale * beyond, 0.0f), 1.0f);
  return parameters.maxWidth * (1.0f - falloff);
}

bool IsBlurred(float width) noexcept {
  // The firmware branches on 0.384 > width, so equality takes the blur path.
  return !(kMinimumWidth > width);
}

std::array<float, kTapCount> Kernel() noexcept {
  std::array<float, kTapCount> kernel{};
  for (int k = -static_cast<int>(kRadiusTaps); k <= static_cast<int>(kRadiusTaps);
       ++k) {
    kernel[static_cast<std::size_t>(k + static_cast<int>(kRadiusTaps))] =
        TapWeight(k);
  }
  return kernel;
}

} // namespace Prosperismo::FirstWave::Blur
