#include "pch.h"

#include "ProsperismoFocusRing.h"
#include "ShellFocusKit.h"
#include "codegen/react/components/ProsperismoShell/ProsperismoFocusRing.g.h"

#ifdef RNW_NEW_ARCH

#include <AutoDraw.h>
#include <d2d1_1.h>
#include <dxgiformat.h>
#include <winrt/Microsoft.ReactNative.Composition.Experimental.h>

#include <atomic>
#include <chrono>
#include <cmath>
#include <limits>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace {

using namespace Prosperismo::FocusKit;
using CompositionContext =
    winrt::Microsoft::ReactNative::Composition::Experimental::ICompositionContext;
using DrawingSurface =
    winrt::Microsoft::ReactNative::Composition::Experimental::IDrawingSurfaceBrush;
using SpriteVisual =
    winrt::Microsoft::ReactNative::Composition::Experimental::ISpriteVisual;

// FocusRenderManager's SecPerFrame cadence, as a wait timeout.
constexpr DWORD FocusFrameIntervalMs = 16;

// One process-wide focus clock, mirroring FocusClock.Elapsed: the noise orbit
// and shimmer phase are absolute UI time, not per-widget time.
double FocusClockSeconds() noexcept {
  static auto const origin = std::chrono::steady_clock::now();
  return std::chrono::duration<double>(std::chrono::steady_clock::now() - origin).count();
}

struct RingPropsSnapshot {
  bool active{};
  FocusRect target{};
  double radius{};
  double offsetX{};
  double offsetY{};
  double surfaceWidth{};
  double surfaceHeight{};
  double screenWidth{};
  double screenHeight{};
  int32_t pressedToken{};
  bool keyRepeating{};
  std::wstring noisePath;
};

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

struct FocusRingViewState
    : winrt::implements<FocusRingViewState, winrt::Windows::Foundation::IInspectable>,
      ProsperismoShellSpecs::BaseProsperismoFocusRing<FocusRingViewState> {

  ~FocusRingViewState() noexcept {
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
      m_visual.RelativeSizeWithOffset({0.0f, 0.0f}, {1.0f, 1.0f});
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
      // A missing composition bridge leaves the component a transparent no-op.
    }
    return nullptr;
  }

  void UpdateProps(
      winrt::Microsoft::ReactNative::ComponentView const &view,
      winrt::com_ptr<ProsperismoShellSpecs::ProsperismoFocusRingProps> const &newProps,
      winrt::com_ptr<ProsperismoShellSpecs::ProsperismoFocusRingProps> const &oldProps) noexcept override {
    ProsperismoShellSpecs::BaseProsperismoFocusRing<FocusRingViewState>::UpdateProps(
        view, newProps, oldProps);
    if (!newProps) {
      return;
    }
    {
      std::lock_guard<std::mutex> lock(m_propsMutex);
      m_pendingProps.active = newProps->active;
      m_pendingProps.target = {
          newProps->targetX, newProps->targetY, newProps->targetWidth, newProps->targetHeight};
      m_pendingProps.radius = newProps->radius;
      m_pendingProps.offsetX = newProps->offsetX;
      m_pendingProps.offsetY = newProps->offsetY;
      m_pendingProps.surfaceWidth = newProps->surfaceWidth;
      m_pendingProps.surfaceHeight = newProps->surfaceHeight;
      m_pendingProps.screenWidth = newProps->screenWidth;
      m_pendingProps.screenHeight = newProps->screenHeight;
      m_pendingProps.pressedToken = newProps->pressedToken;
      m_pendingProps.keyRepeating = newProps->keyRepeating;
      m_pendingProps.noisePath = Widen(newProps->noisePath);
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
    if (m_visual) {
      m_visual.IsVisible(false);
    }
    if (m_stopEvent) {
      SetEvent(m_stopEvent.get());
    }
    if (m_worker.joinable()) {
      m_worker.join();
    }
  }

  void ApplyPendingProps() noexcept {
    RingPropsSnapshot snapshot;
    {
      std::lock_guard<std::mutex> lock(m_propsMutex);
      if (!m_propsDirty) {
        return;
      }
      snapshot = m_pendingProps;
      m_propsDirty = false;
    }

    if (snapshot.noisePath != m_noisePath) {
      m_noisePath = snapshot.noisePath;
      m_noise.LoadFromPngFile(m_noisePath.c_str());
    }
    if (snapshot.pressedToken != m_lastPressedToken) {
      // Only a press starts the pulse; the release is already part of it.
      if (m_lastPressedToken != 0 || snapshot.pressedToken != 0) {
        m_timeline.SetPressed(true);
      }
      m_lastPressedToken = snapshot.pressedToken;
    }
    m_timeline.SetKeyRepeating(snapshot.keyRepeating);
    if (snapshot.active) {
      if (m_active) {
        m_timeline.Retarget(snapshot.target, snapshot.radius);
      } else {
        m_timeline.ShowAt(snapshot.target, snapshot.radius);
      }
    } else {
      m_timeline.Hide();
    }
    m_active = snapshot.active;
    m_offsetX = snapshot.offsetX;
    m_offsetY = snapshot.offsetY;
    m_surfaceWidth = snapshot.surfaceWidth;
    m_surfaceHeight = snapshot.surfaceHeight;
    m_screenWidth = snapshot.screenWidth;
    m_screenHeight = snapshot.screenHeight;
  }

  void Run() noexcept {
    HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(apartment) && apartment != RPC_E_CHANGED_MODE) {
      return;
    }
    auto previous = std::chrono::steady_clock::now();
    while (WaitForSingleObject(m_stopEvent.get(), 0) == WAIT_TIMEOUT) {
      ApplyPendingProps();

      auto now = std::chrono::steady_clock::now();
      double delta = std::chrono::duration<double>(now - previous).count();
      previous = now;
      if (delta > 0.25 || delta <= 0.0) {
        delta = FocusTimeline::SecPerFrame;
      }
      m_timeline.Advance(delta);

      Draw();

      HANDLE waits[]{m_stopEvent.get(), m_propsEvent.get()};
      auto timeout = m_timeline.IsVisible() ? FocusFrameIntervalMs : INFINITE;
      auto result = WaitForMultipleObjects(2, waits, FALSE, timeout);
      if (result == WAIT_OBJECT_0) {
        break;
      }
      if (timeout == INFINITE) {
        previous = std::chrono::steady_clock::now();
      }
    }
    if (SUCCEEDED(apartment)) {
      CoUninitialize();
    }
  }

  void Draw() noexcept {
    try {
      if (!m_context || !m_visual) {
        return;
      }
      if (!m_timeline.IsVisible()) {
        m_visual.IsVisible(false);
        return;
      }

      auto width = static_cast<uint32_t>(
          std::clamp(std::llround(m_surfaceWidth), 2ll, 4096ll));
      auto height = static_cast<uint32_t>(
          std::clamp(std::llround(m_surfaceHeight), 2ll, 4096ll));
      if (!m_surface || width != m_width || height != m_height) {
        m_width = width;
        m_height = height;
        m_surface = m_context.CreateDrawingSurfaceBrush(
            {static_cast<float>(m_width), static_cast<float>(m_height)},
            winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,
            winrt::Windows::Graphics::DirectX::DirectXAlphaMode::Premultiplied);
        m_surface.Stretch(
            winrt::Microsoft::ReactNative::Composition::Experimental::CompositionStretch::Fill);
        m_visual.Brush(m_surface);
      }

      POINT offset{};
      ::Microsoft::ReactNative::Composition::AutoDrawDrawingSurface draw(m_surface, 1.0f, &offset);
      auto target = draw.GetRenderTarget();
      if (!target) {
        return;
      }
      target->Clear(D2D1_COLOR_F{0.0f, 0.0f, 0.0f, 0.0f});
      target->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND_SOURCE_OVER);

      double clock = FocusClockSeconds();
      DrawAreaWash(target, offset, clock);
      DrawBand(target, offset, clock);
      target->SetTransform(D2D1::Matrix3x2F::Identity());
      m_visual.IsVisible(true);
    } catch (...) {
      // The highlight is decoration: a bad frame must never take the scene down.
      if (m_visual) {
        m_visual.IsVisible(false);
      }
    }
  }

  // ShellFocusRing.RenderAreaWash + ShellFocusWash.Render.
  void DrawAreaWash(ID2D1DeviceContext *target, POINT const &offset, double clock) noexcept {
    double alpha = m_timeline.AreaOpacity();
    auto rect = m_timeline.CurrentRect();
    if (alpha <= 0.004 || rect.width <= 1.0 || rect.height <= 1.0) {
      return;
    }
    double screenWidth = m_screenWidth > 0.0 ? m_screenWidth : rect.width;
    double screenHeight = m_screenHeight > 0.0 ? m_screenHeight : rect.height;
    // A large focused target gets no wash at all - only the line.
    if (!AreaPassApplies(rect.width, rect.height, screenWidth, screenHeight)) {
      return;
    }
    double effective = alpha *
        AreaOpacityScaleForSize(rect.width, rect.height, screenWidth, screenHeight);
    if (effective <= 0.004) {
      return;
    }
    double radius = std::max(
        0.0,
        std::min(m_timeline.CurrentRadius(), std::min(rect.width, rect.height) / 2.0));
    // EnableAreaEdgeFade defaults false in 4.03: the quad is the target itself.
    double fade = std::max(Palette::AreaEdgeFadeLength, Palette::EdgeFadeMinLength);

    double aspect = rect.width / rect.height;
    double targetMinHalf = std::min(rect.width, rect.height) / 2.0;
    if (targetMinHalf <= 0.0) {
      return;
    }
    double bodyRatio = 1.0;
    double radiusRatio = radius / targetMinHalf;
    double fadeRatio = fade / targetMinHalf;
    double moving = m_timeline.Moving();
    double pressing = m_timeline.Pressing();
    int animationFrame = Palette::AnimationFrame(clock);

    if (m_washCacheFrame != animationFrame ||
        std::abs(aspect - m_washCacheAspect) >= 0.01 ||
        std::abs(radiusRatio - m_washCacheRadiusRatio) >= 0.01 ||
        std::abs(fadeRatio - m_washCacheFadeRatio) >= 0.01 ||
        std::abs(moving - m_washCacheMoving) >= 0.005 ||
        std::abs(pressing - m_washCachePressing) >= 0.005) {
      if (!RenderWashBitmap(
              m_washBitmap, aspect, bodyRatio, radiusRatio, fadeRatio, clock, moving,
              pressing, m_noise)) {
        return;
      }
      m_washCacheFrame = animationFrame;
      m_washCacheAspect = aspect;
      m_washCacheRadiusRatio = radiusRatio;
      m_washCacheFadeRatio = fadeRatio;
      m_washCacheMoving = moving;
      m_washCachePressing = pressing;
    }
    DrawFocusBitmap(target, m_washBitmap, rect, offset, static_cast<float>(std::clamp(effective, 0.0, 1.0)));
  }

  // ShellFocusRing.RenderHighlight's line pass + ShellFocusBand.Render.
  void DrawBand(ID2D1DeviceContext *target, POINT const &offset, double clock) noexcept {
    double alpha = m_timeline.LineOpacity();
    if (alpha <= 0.004) {
      return;
    }
    auto rect = m_timeline.CurrentRect();
    if (rect.width <= 1.0 || rect.height <= 1.0) {
      return;
    }
    double band = m_timeline.BandWidth();
    double inflate =
        (FocusTimeline::LineThickness + FocusTimeline::LineOffset) * m_timeline.InOutScale();
    auto body = rect.Inflate(inflate);
    if (body.width <= 1.0 || body.height <= 1.0 || band <= 0.0) {
      return;
    }
    double radius = m_timeline.CurrentRadius() + inflate;
    radius = std::max(0.0, std::min(radius, std::min(body.width, body.height) / 2.0));

    // The band straddles the edge; the surface needs room both sides plus the
    // antialiasing ramp.
    double margin = (band * 0.5) + 1.0;
    auto surface = body.Inflate(margin);
    double aspect = surface.width / surface.height;
    double targetMinHalf = std::min(surface.width, surface.height) / 2.0;
    if (targetMinHalf <= 0.0) {
      return;
    }
    double bodyRatioX = body.width / surface.width;
    double bodyRatioY = body.height / surface.height;
    double radiusRatio = radius / targetMinHalf;
    double bandRatio = band / targetMinHalf;
    int noiseFrame = Palette::AnimationFrame(clock);

    if (m_bandCacheFrame != noiseFrame ||
        std::abs(aspect - m_bandCacheAspect) >= 0.01 ||
        std::abs(bodyRatioX - m_bandCacheBodyRatioX) >= 0.005 ||
        std::abs(bodyRatioY - m_bandCacheBodyRatioY) >= 0.005 ||
        std::abs(radiusRatio - m_bandCacheRadiusRatio) >= 0.01 ||
        std::abs(bandRatio - m_bandCacheBandRatio) >= 0.005) {
      if (!RenderBandBitmap(
              m_bandBitmap,
              aspect,
              bodyRatioX,
              bodyRatioY,
              radiusRatio,
              bandRatio,
              clock,
              m_noise)) {
        return;
      }
      m_bandCacheFrame = noiseFrame;
      m_bandCacheAspect = aspect;
      m_bandCacheBodyRatioX = bodyRatioX;
      m_bandCacheBodyRatioY = bodyRatioY;
      m_bandCacheRadiusRatio = radiusRatio;
      m_bandCacheBandRatio = bandRatio;
    }

    // The anisotropic stretch along the travel angle, applied about the body
    // centre in surface coordinates.
    double m11{}, m12{}, m21{}, m22{};
    m_timeline.WarpDistortionMatrix(m11, m12, m21, m22);
    auto centreX = static_cast<float>(body.CenterX() + m_offsetX + offset.x);
    auto centreY = static_cast<float>(body.CenterY() + m_offsetY + offset.y);
    auto distortion = D2D1::Matrix3x2F(
        static_cast<float>(m11), static_cast<float>(m12),
        static_cast<float>(m21), static_cast<float>(m22), 0.0f, 0.0f);
    auto transform = D2D1::Matrix3x2F::Translation(-centreX, -centreY) * distortion *
        D2D1::Matrix3x2F::Translation(centreX, centreY);
    target->SetTransform(transform);
    DrawFocusBitmap(
        target, m_bandBitmap, surface, offset, static_cast<float>(std::clamp(alpha, 0.0, 1.0)));
    target->SetTransform(D2D1::Matrix3x2F::Identity());
  }

  void DrawFocusBitmap(
      ID2D1DeviceContext *target,
      FocusBitmap const &bitmap,
      FocusRect const &destination,
      POINT const &offset,
      float opacity) noexcept {
    if (bitmap.width <= 0 || bitmap.height <= 0 || bitmap.pixels.empty()) {
      return;
    }
    D2D1_BITMAP_PROPERTIES1 properties{};
    properties.pixelFormat = {DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED};
    properties.dpiX = 96.0f;
    properties.dpiY = 96.0f;
    winrt::com_ptr<ID2D1Bitmap1> d2dBitmap;
    if (FAILED(target->CreateBitmap(
            {static_cast<UINT32>(bitmap.width), static_cast<UINT32>(bitmap.height)},
            bitmap.pixels.data(),
            static_cast<UINT32>(bitmap.width) * 4u,
            &properties,
            d2dBitmap.put()))) {
      return;
    }
    D2D1_RECT_F rect{
        static_cast<float>(destination.x + m_offsetX + offset.x),
        static_cast<float>(destination.y + m_offsetY + offset.y),
        static_cast<float>(destination.x + destination.width + m_offsetX + offset.x),
        static_cast<float>(destination.y + destination.height + m_offsetY + offset.y)};
    target->DrawBitmap(d2dBitmap.get(), &rect, opacity, D2D1_INTERPOLATION_MODE_LINEAR);
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
  RingPropsSnapshot m_pendingProps;
  bool m_propsDirty{false};

  // Worker-thread state.
  FocusTimeline m_timeline;
  FocusNoiseTexture m_noise;
  std::wstring m_noisePath{L"\uFFFF-unset"};
  int32_t m_lastPressedToken{};
  bool m_active{};
  double m_offsetX{};
  double m_offsetY{};
  double m_surfaceWidth{};
  double m_surfaceHeight{};
  double m_screenWidth{};
  double m_screenHeight{};
  uint32_t m_width{};
  uint32_t m_height{};

  FocusBitmap m_washBitmap;
  int m_washCacheFrame{-1};
  double m_washCacheAspect{std::numeric_limits<double>::quiet_NaN()};
  double m_washCacheRadiusRatio{std::numeric_limits<double>::quiet_NaN()};
  double m_washCacheFadeRatio{std::numeric_limits<double>::quiet_NaN()};
  double m_washCacheMoving{std::numeric_limits<double>::quiet_NaN()};
  double m_washCachePressing{std::numeric_limits<double>::quiet_NaN()};

  FocusBitmap m_bandBitmap;
  int m_bandCacheFrame{-1};
  double m_bandCacheAspect{std::numeric_limits<double>::quiet_NaN()};
  double m_bandCacheBodyRatioX{std::numeric_limits<double>::quiet_NaN()};
  double m_bandCacheBodyRatioY{std::numeric_limits<double>::quiet_NaN()};
  double m_bandCacheRadiusRatio{std::numeric_limits<double>::quiet_NaN()};
  double m_bandCacheBandRatio{std::numeric_limits<double>::quiet_NaN()};
};

} // namespace

void RegisterProsperismoFocusRing(
    winrt::Microsoft::ReactNative::IReactPackageBuilder const &packageBuilder) noexcept {
  auto fabric = packageBuilder.try_as<winrt::Microsoft::ReactNative::IReactPackageBuilderFabric>();
  if (!fabric) {
    return;
  }
  ProsperismoShellSpecs::RegisterProsperismoFocusRingNativeComponent<FocusRingViewState>(
      packageBuilder,
      [](winrt::Microsoft::ReactNative::Composition::IReactCompositionViewComponentBuilder const &builder) noexcept {
        builder.SetViewFeatures(
            winrt::Microsoft::ReactNative::Composition::ComponentViewFeatures::Default &
            ~winrt::Microsoft::ReactNative::Composition::ComponentViewFeatures::NativeBorder);
      });
}

#else

void RegisterProsperismoFocusRing(
    winrt::Microsoft::ReactNative::IReactPackageBuilder const &) noexcept {}

#endif
