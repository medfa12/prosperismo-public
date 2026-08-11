#pragma once

#include <winrt/Microsoft.ReactNative.h>

// RNW 0.83's Fabric fallback passes file:/// URIs directly to
// StorageFile::GetFileFromPathAsync, which expects a native Windows path.
// Register a provider that performs the URI-to-path conversion first.
void RegisterProsperismoLocalImageProvider(
    winrt::Microsoft::ReactNative::IReactPackageBuilder const &packageBuilder) noexcept;
