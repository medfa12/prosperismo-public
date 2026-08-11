#include "pch.h"

#include "ProsperismoLocalImage.h"
#include "codegen/react/components/ProsperismoShell/ProsperismoLocalImage.g.h"

#ifdef RNW_NEW_ARCH

#include <AutoDraw.h>
#include <d2d1_1.h>
#include <dxgiformat.h>
#include <strsafe.h>
#include <wincodec.h>
#include <winrt/Microsoft.ReactNative.Composition.Experimental.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace {

using CompositionContext =
    winrt::Microsoft::ReactNative::Composition::Experimental::ICompositionContext;
using DrawingSurface =
    winrt::Microsoft::ReactNative::Composition::Experimental::IDrawingSurfaceBrush;
using SpriteVisual =
    winrt::Microsoft::ReactNative::Composition::Experimental::ISpriteVisual;

void LogLocalImageFailure(
    wchar_t const *operation,
    std::wstring const &path,
    HRESULT error) noexcept {
  wchar_t localAppData[32768]{};
  DWORD length =
      GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, ARRAYSIZE(localAppData));
  if (length == 0 || length >= ARRAYSIZE(localAppData)) {
    return;
  }
  wchar_t directory[32768]{};
  wchar_t logPath[32768]{};
  if (FAILED(StringCchPrintfW(
          directory, ARRAYSIZE(directory), L"%s\\Prosperismo", localAppData)) ||
      FAILED(StringCchPrintfW(
          logPath, ARRAYSIZE(logPath), L"%s\\local-image.log", directory))) {
    return;
  }
  CreateDirectoryW(directory, nullptr);
  HANDLE file = CreateFileW(
      logPath,
      FILE_APPEND_DATA,
      FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
      nullptr,
      OPEN_ALWAYS,
      FILE_ATTRIBUTE_NORMAL,
      nullptr);
  if (file == INVALID_HANDLE_VALUE) {
    return;
  }
  wchar_t line[32768]{};
  if (SUCCEEDED(StringCchPrintfW(
          line,
          ARRAYSIZE(line),
          L"pid=%lu operation=%s error=0x%08lx path=%s\r\n",
          GetCurrentProcessId(),
          operation,
          static_cast<unsigned long>(error),
          path.c_str()))) {
    int bytesRequired =
        WideCharToMultiByte(CP_UTF8, 0, line, -1, nullptr, 0, nullptr, nullptr);
    if (bytesRequired > 1) {
      std::string utf8(static_cast<size_t>(bytesRequired), '\0');
      WideCharToMultiByte(
          CP_UTF8, 0, line, -1, utf8.data(), bytesRequired, nullptr, nullptr);
      DWORD written{};
      WriteFile(
          file,
          utf8.data(),
          static_cast<DWORD>(utf8.size() - 1),
          &written,
          nullptr);
    }
  }
  CloseHandle(file);
}

struct DecodedImage {
  uint32_t width{};
  uint32_t height{};
  std::vector<uint8_t> pixels;
};

void ApplyTint(
    DecodedImage &image,
    double red,
    double green,
    double blue) noexcept {
  double redScale = std::clamp(red, 0.0, 255.0) / 255.0;
  double greenScale = std::clamp(green, 0.0, 255.0) / 255.0;
  double blueScale = std::clamp(blue, 0.0, 255.0) / 255.0;
  if (redScale == 1.0 && greenScale == 1.0 && blueScale == 1.0) {
    return;
  }
  for (size_t offset = 0; offset < image.pixels.size(); offset += 4) {
    image.pixels[offset + 0] =
        static_cast<uint8_t>(std::round(image.pixels[offset + 0] * blueScale));
    image.pixels[offset + 1] =
        static_cast<uint8_t>(std::round(image.pixels[offset + 1] * greenScale));
    image.pixels[offset + 2] =
        static_cast<uint8_t>(std::round(image.pixels[offset + 2] * redScale));
  }
}

std::wstring Widen(std::string const &value) {
  if (value.empty()) {
    return {};
  }
  int required = MultiByteToWideChar(
      CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
  std::wstring wide(static_cast<size_t>(required), L'\0');
  MultiByteToWideChar(
      CP_UTF8, 0, value.data(), static_cast<int>(value.size()), wide.data(), required);
  return wide;
}

bool DecodePng(std::wstring const &path, DecodedImage &decoded) noexcept {
  decoded = {};
  if (path.empty()) {
    return false;
  }
  try {
    auto factory = winrt::create_instance<IWICImagingFactory>(
        CLSID_WICImagingFactory, CLSCTX_INPROC_SERVER);
    winrt::com_ptr<IWICBitmapDecoder> decoder;
    winrt::check_hresult(factory->CreateDecoderFromFilename(
        path.c_str(),
        nullptr,
        GENERIC_READ,
        WICDecodeMetadataCacheOnLoad,
        decoder.put()));
    winrt::com_ptr<IWICBitmapFrameDecode> frame;
    winrt::check_hresult(decoder->GetFrame(0, frame.put()));
    UINT width{}, height{};
    winrt::check_hresult(frame->GetSize(&width, &height));
    if (width == 0 || height == 0 || width > 8192 || height > 8192) {
      return false;
    }
    winrt::com_ptr<IWICFormatConverter> converter;
    winrt::check_hresult(factory->CreateFormatConverter(converter.put()));
    winrt::check_hresult(converter->Initialize(
        frame.get(),
        GUID_WICPixelFormat32bppPBGRA,
        WICBitmapDitherTypeNone,
        nullptr,
        0.0,
        WICBitmapPaletteTypeMedianCut));
    decoded.width = width;
    decoded.height = height;
    decoded.pixels.resize(static_cast<size_t>(width) * height * 4);
    winrt::check_hresult(converter->CopyPixels(
        nullptr,
        width * 4,
        static_cast<UINT>(decoded.pixels.size()),
        decoded.pixels.data()));
    return true;
  } catch (winrt::hresult_error const &error) {
    LogLocalImageFailure(L"decode", path, error.code());
    decoded = {};
    return false;
  } catch (...) {
    LogLocalImageFailure(L"decode", path, E_FAIL);
    decoded = {};
    return false;
  }
}

struct LocalImageViewState
    : winrt::implements<
          LocalImageViewState,
          winrt::Windows::Foundation::IInspectable>,
      ProsperismoShellSpecs::BaseProsperismoLocalImage<LocalImageViewState> {
  ~LocalImageViewState() noexcept {
    Stop();
  }

  void Initialize(winrt::Microsoft::ReactNative::ComponentView const &view) noexcept override {
    try {
      auto compositionView =
          view.try_as<winrt::Microsoft::ReactNative::Composition::ViewComponentView>();
      if (!compositionView) {
        return;
      }
      auto internalView = compositionView.try_as<
          winrt::Microsoft::ReactNative::Composition::Experimental::IInternalComponentView>();
      if (!internalView) {
        return;
      }
      m_context = internalView.CompositionContext();
      if (!m_context) {
        return;
      }
      m_visual = m_context.CreateSpriteVisual();
      m_visual.IsVisible(false);
      m_stopEvent.attach(CreateEventW(nullptr, TRUE, FALSE, nullptr));
      m_propsEvent.attach(CreateEventW(nullptr, FALSE, FALSE, nullptr));
      auto weak = get_weak();
      m_destroying = view.Destroying([weak](auto const &, auto const &) noexcept {
        if (auto self = weak.get()) {
          self->Stop();
        }
      });
      m_worker = std::thread([this] { Run(); });
    } catch (...) {
      m_context = nullptr;
      m_visual = nullptr;
    }
  }

  winrt::Microsoft::UI::Composition::Visual CreateVisual(
      winrt::Microsoft::ReactNative::ComponentView const &view) noexcept override {
    try {
      if (m_visual) {
        return winrt::Microsoft::ReactNative::Composition::Experimental::
            MicrosoftCompositionContextHelper::InnerVisual(m_visual);
      }
      if (auto compositionView =
              view.try_as<winrt::Microsoft::ReactNative::Composition::ComponentView>()) {
        return compositionView.Compositor().CreateSpriteVisual();
      }
    } catch (...) {
    }
    return nullptr;
  }

  void UpdateProps(
      winrt::Microsoft::ReactNative::ComponentView const &view,
      winrt::com_ptr<ProsperismoShellSpecs::ProsperismoLocalImageProps> const &newProps,
      winrt::com_ptr<ProsperismoShellSpecs::ProsperismoLocalImageProps> const &oldProps) noexcept override {
    ProsperismoShellSpecs::BaseProsperismoLocalImage<LocalImageViewState>::UpdateProps(
        view, newProps, oldProps);
    if (!newProps) {
      return;
    }
    if (m_visual) {
      m_visual.Size({
          static_cast<float>(std::max(newProps->displayWidth, 0.0)),
          static_cast<float>(std::max(newProps->displayHeight, 0.0))});
    }
    {
      std::lock_guard<std::mutex> lock(m_propsMutex);
      m_pendingPath = Widen(newProps->path);
      m_pendingContain = newProps->contain;
      m_pendingTintRed = newProps->tintRed;
      m_pendingTintGreen = newProps->tintGreen;
      m_pendingTintBlue = newProps->tintBlue;
      m_propsDirty = true;
    }
    if (m_propsEvent) {
      SetEvent(m_propsEvent.get());
    }
  }

 private:
  void Stop() noexcept {
    if (m_stopped.exchange(true)) {
      return;
    }
    if (m_stopEvent) {
      SetEvent(m_stopEvent.get());
    }
    if (m_worker.joinable()) {
      m_worker.join();
    }
    if (m_visual) {
      m_visual.IsVisible(false);
    }
  }

  void Run() noexcept {
    HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(apartment) && apartment != RPC_E_CHANGED_MODE) {
      return;
    }
    HANDLE waits[]{m_stopEvent.get(), m_propsEvent.get()};
    while (WaitForSingleObject(m_stopEvent.get(), 0) == WAIT_TIMEOUT) {
      auto result = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
      if (result == WAIT_OBJECT_0) {
        break;
      }

      std::wstring path;
      bool contain{};
      double tintRed{};
      double tintGreen{};
      double tintBlue{};
      {
        std::lock_guard<std::mutex> lock(m_propsMutex);
        if (!m_propsDirty) {
          continue;
        }
        path = m_pendingPath;
        contain = m_pendingContain;
        tintRed = m_pendingTintRed;
        tintGreen = m_pendingTintGreen;
        tintBlue = m_pendingTintBlue;
        m_propsDirty = false;
      }
      if (path == m_path && contain == m_contain &&
          tintRed == m_tintRed && tintGreen == m_tintGreen &&
          tintBlue == m_tintBlue) {
        continue;
      }
      m_path = std::move(path);
      m_contain = contain;
      m_tintRed = tintRed;
      m_tintGreen = tintGreen;
      m_tintBlue = tintBlue;
      DecodedImage image;
      if (!DecodePng(m_path, image)) {
        m_visual.IsVisible(false);
        continue;
      }
      ApplyTint(image, m_tintRed, m_tintGreen, m_tintBlue);
      m_visual.IsVisible(false);
      m_surface = nullptr;
      HRESULT drawResult = E_PENDING;
      for (int attempt = 0; attempt < 120; ++attempt) {
        drawResult = Draw(image);
        if (SUCCEEDED(drawResult)) {
          break;
        }
        if (WaitForSingleObject(m_stopEvent.get(), 16) == WAIT_OBJECT_0) {
          break;
        }
      }
      if (FAILED(drawResult)) {
        LogLocalImageFailure(L"draw", m_path, drawResult);
      }
    }
    if (SUCCEEDED(apartment)) {
      CoUninitialize();
    }
  }

  HRESULT Draw(DecodedImage const &image) noexcept {
    try {
      if (!m_surface) {
        m_surface = m_context.CreateDrawingSurfaceBrush(
            {static_cast<float>(image.width), static_cast<float>(image.height)},
            winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,
            winrt::Windows::Graphics::DirectX::DirectXAlphaMode::Premultiplied);
        m_surface.HorizontalAlignmentRatio(0.5f);
        m_surface.VerticalAlignmentRatio(0.5f);
        m_surface.Stretch(
            m_contain
                ? winrt::Microsoft::ReactNative::Composition::Experimental::CompositionStretch::Uniform
                : winrt::Microsoft::ReactNative::Composition::Experimental::CompositionStretch::UniformToFill);
        m_visual.Brush(m_surface);
      }

      POINT offset{};
      ::Microsoft::ReactNative::Composition::AutoDrawDrawingSurface draw(
          m_surface, 1.0f, &offset);
      auto target = draw.GetRenderTarget();
      if (!target) {
        return E_PENDING;
      }
      D2D1_BITMAP_PROPERTIES1 properties{};
      properties.pixelFormat = {
          DXGI_FORMAT_B8G8R8A8_UNORM,
          D2D1_ALPHA_MODE_PREMULTIPLIED};
      properties.dpiX = 96.0f;
      properties.dpiY = 96.0f;
      winrt::com_ptr<ID2D1Bitmap1> bitmap;
      winrt::check_hresult(target->CreateBitmap(
          {image.width, image.height},
          image.pixels.data(),
          image.width * 4,
          &properties,
          bitmap.put()));
      target->Clear(D2D1_COLOR_F{0.0f, 0.0f, 0.0f, 0.0f});
      D2D1_RECT_F destination{
          static_cast<float>(offset.x),
          static_cast<float>(offset.y),
          static_cast<float>(offset.x + image.width),
          static_cast<float>(offset.y + image.height)};
      target->DrawBitmap(
          bitmap.get(), &destination, 1.0f, D2D1_INTERPOLATION_MODE_LINEAR);
      m_visual.IsVisible(true);
      return S_OK;
    } catch (winrt::hresult_error const &error) {
      m_visual.IsVisible(false);
      return error.code();
    } catch (...) {
      m_visual.IsVisible(false);
      return E_FAIL;
    }
  }

  CompositionContext m_context{nullptr};
  SpriteVisual m_visual{nullptr};
  DrawingSurface m_surface{nullptr};
  winrt::handle m_stopEvent;
  winrt::handle m_propsEvent;
  winrt::event_token m_destroying{};
  std::thread m_worker;
  std::atomic_bool m_stopped{false};
  std::mutex m_propsMutex;
  std::wstring m_pendingPath;
  bool m_pendingContain{};
  double m_pendingTintRed{255.0};
  double m_pendingTintGreen{255.0};
  double m_pendingTintBlue{255.0};
  bool m_propsDirty{false};
  std::wstring m_path;
  bool m_contain{};
  double m_tintRed{255.0};
  double m_tintGreen{255.0};
  double m_tintBlue{255.0};
};

} // namespace

void RegisterProsperismoLocalImage(
    winrt::Microsoft::ReactNative::IReactPackageBuilder const &packageBuilder) noexcept {
  auto fabric = packageBuilder.try_as<
      winrt::Microsoft::ReactNative::IReactPackageBuilderFabric>();
  if (!fabric) {
    return;
  }
  ProsperismoShellSpecs::RegisterProsperismoLocalImageNativeComponent<LocalImageViewState>(
      packageBuilder,
      nullptr);
}

#else

void RegisterProsperismoLocalImage(
    winrt::Microsoft::ReactNative::IReactPackageBuilder const &) noexcept {}

#endif
