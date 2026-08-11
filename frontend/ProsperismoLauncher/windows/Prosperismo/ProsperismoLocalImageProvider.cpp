#include "pch.h"

#include "ProsperismoLocalImageProvider.h"

#ifdef RNW_NEW_ARCH

#include <shlwapi.h>
#include <winrt/Microsoft.ReactNative.Composition.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.Storage.Streams.h>

namespace {

struct LocalFileImageProvider
    : winrt::implements<
          LocalFileImageProvider,
          winrt::Microsoft::ReactNative::Composition::IUriImageProvider> {
  bool CanLoadImageUri(
      winrt::Microsoft::ReactNative::IReactContext,
      winrt::Windows::Foundation::Uri const &uri) const noexcept {
    return uri && uri.SchemeName() == L"file";
  }

  winrt::Windows::Foundation::IAsyncOperation<
      winrt::Microsoft::ReactNative::Composition::ImageResponse>
  GetImageResponseAsync(
      winrt::Microsoft::ReactNative::IReactContext const &,
      winrt::Microsoft::ReactNative::Composition::ImageSource const &source) {
    try {
      auto uri = source.Uri();
      if (!uri) {
        co_return winrt::Microsoft::ReactNative::Composition::ImageFailedResponse(
            L"Missing local image URI.");
      }

      std::wstring path(32768, L'\0');
      DWORD length = static_cast<DWORD>(path.size());
      HRESULT converted = PathCreateFromUrlW(
          uri.AbsoluteCanonicalUri().c_str(), path.data(), &length, 0);
      if (FAILED(converted) || length == 0) {
        co_return winrt::Microsoft::ReactNative::Composition::ImageFailedResponse(
            L"Could not convert local image URI to a Windows path.");
      }
      path.resize(length);

      auto file = co_await winrt::Windows::Storage::StorageFile::GetFileFromPathAsync(path);
      if (!file) {
        co_return winrt::Microsoft::ReactNative::Composition::ImageFailedResponse(
            L"Local image file was not found.");
      }
      co_return winrt::Microsoft::ReactNative::Composition::StreamImageResponse(
          co_await file.OpenReadAsync());
    } catch (winrt::hresult_error const &error) {
      co_return winrt::Microsoft::ReactNative::Composition::ImageFailedResponse(
          error.message());
    }
  }
};

} // namespace

void RegisterProsperismoLocalImageProvider(
    winrt::Microsoft::ReactNative::IReactPackageBuilder const &packageBuilder) noexcept {
  if (auto fabric =
          packageBuilder.try_as<winrt::Microsoft::ReactNative::IReactPackageBuilderFabric>()) {
    fabric.AddUriImageProvider(winrt::make<LocalFileImageProvider>());
  }
}

#else

void RegisterProsperismoLocalImageProvider(
    winrt::Microsoft::ReactNative::IReactPackageBuilder const &) noexcept {}

#endif
