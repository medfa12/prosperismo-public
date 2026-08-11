#pragma once

#include "NativeModules.h"

namespace winrt::Prosperismo {

/** Reports the best installed, redistribution-safe shell typeface. */
REACT_MODULE(ShellTypography, L"ShellTypography")
struct ShellTypography {
  REACT_CONSTANT_PROVIDER(GetConstants)
  void GetConstants(
      winrt::Microsoft::ReactNative::ReactConstantProvider &provider) noexcept;
};

} // namespace winrt::Prosperismo

