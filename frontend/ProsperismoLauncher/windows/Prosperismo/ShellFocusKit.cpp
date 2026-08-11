#include "pch.h"

#include "ShellFocusKit.h"

#include <wincodec.h>
#include <winrt/base.h>

#include <algorithm>
#include <cmath>

namespace Prosperismo::FocusKit {

namespace {

double Clamp01(double value) noexcept {
  if (std::isnan(value)) {
    return 0.0;
  }
  return value < 0.0 ? 0.0 : (value > 1.0 ? 1.0 : value);
}

double Lerp(double a, double b, double t) noexcept {
  return a + ((b - a) * t);
}

bool IsFiniteRect(FocusRect const &rect) noexcept {
  return std::isfinite(rect.x) && std::isfinite(rect.y) &&
      std::isfinite(rect.width) && std::isfinite(rect.height) &&
      rect.width > 0.0 && rect.height > 0.0;
}

bool RectsClose(FocusRect const &a, FocusRect const &b) noexcept {
  return std::abs(a.x - b.x) < 0.5 && std::abs(a.y - b.y) < 0.5 &&
      std::abs(a.width - b.width) < 0.5 && std::abs(a.height - b.height) < 0.5;
}

// FocusRenderManager.DefaultColorTable, quoted as the source writes them.
FocusColor FromUnit(double r, double g, double b) noexcept {
  auto channel = [](double v) noexcept {
    double scaled = std::round(v * 255.0);
    return static_cast<uint8_t>(scaled < 0.0 ? 0.0 : (scaled > 255.0 ? 255.0 : scaled));
  };
  return {channel(r), channel(g), channel(b)};
}

FocusColor const kColorTable[] = {
    FromUnit(0.8, 1.0, 1.0),
    FromUnit(0.78039217, 0.8901961, 1.0),
    FromUnit(0.8980392, 0.8980392, 1.0),
    FromUnit(11.0 / 15.0, 0.76862746, 79.0 / 85.0),
    FromUnit(47.0 / 51.0, 0.78039217, 0.8745098),
    FromUnit(1.0, 0.8745098, 0.7490196),
    FromUnit(1.0, 0.8, 0.8),
};
constexpr int kColorTableCount = 7;

double CubicSegment(double t, double a, double b, double c, double d) noexcept {
  return Clamp01(a + (t * (b + (t * (c + (t * d))))));
}

} // namespace

// ---- Ps5FocusField ---------------------------------------------------------

double RoundedBoxDistance(
    double px, double py, double halfWidth, double halfHeight, double radius) noexcept {
  double r = std::max(0.0, std::min(radius, std::min(halfWidth, halfHeight)));
  double qx = std::abs(px) - halfWidth + r;
  double qy = std::abs(py) - halfHeight + r;
  double outsideX = std::max(qx, 0.0);
  double outsideY = std::max(qy, 0.0);
  double outside = std::sqrt((outsideX * outsideX) + (outsideY * outsideY));
  double inside = std::min(std::max(qx, qy), 0.0);
  return outside + inside - r;
}

double FocusSmoothStep(double edge0, double edge1, double x) noexcept {
  if (edge1 <= edge0) {
    return x < edge0 ? 0.0 : 1.0;
  }
  double t = (x - edge0) / (edge1 - edge0);
  t = t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);
  return t * t * (3.0 - (2.0 * t));
}

double AreaCoverage(
    double px, double py, double halfWidth, double halfHeight, double radius,
    double edgeFadeLength) noexcept {
  double sd = RoundedBoxDistance(px, py, halfWidth, halfHeight, radius);
  double fade = std::max(edgeFadeLength, 0.0001);
  double t = Clamp01(sd / fade);
  return 1.0 - (t * t * (3.0 - (2.0 * t)));
}

double ApplyAlphaGamma(double alpha, double gamma) noexcept {
  if (alpha <= 0.0) {
    return 0.0;
  }
  if (gamma <= 0.0 || std::abs(gamma - 1.0) < 1e-6) {
    return alpha > 1.0 ? 1.0 : alpha;
  }
  double shaped = std::pow(alpha > 1.0 ? 1.0 : alpha, 1.0 / gamma);
  return shaped > 1.0 ? 1.0 : shaped;
}

bool AreaPassApplies(
    double targetWidth, double targetHeight, double screenWidth, double screenHeight) noexcept {
  if (screenWidth <= 0.0 || screenHeight <= 0.0) {
    return false;
  }
  double coverage = (targetWidth / screenWidth) * (targetHeight / screenHeight);
  return coverage < Palette::AreaRenderingThreshold;
}

double AreaOpacityScaleForSize(
    double targetWidth, double targetHeight, double screenWidth, double screenHeight) noexcept {
  if (screenWidth <= 0.0 || screenHeight <= 0.0) {
    return 1.0;
  }
  double coverage = (targetWidth / screenWidth) * (targetHeight / screenHeight);
  double scale = 1.0 - (coverage * Palette::AreaOpacityDecreaseRateBySize);
  double floor = Palette::AreaOpacityMinimumDecreaseValueBySize;
  return scale < floor ? floor : (scale > 1.0 ? 1.0 : scale);
}

// ---- ShellFocusPalette -----------------------------------------------------

namespace Palette {

int AnimationFrame(double seconds) noexcept {
  return static_cast<int>(std::floor(seconds * 60.0));
}

void NoiseOffset(double seconds, double &x, double &y) noexcept {
  double a = seconds * NoiseMoveFrequency;
  x = std::sin(a);
  y = std::cos(a);
}

namespace {
double ShimmerChannel(double t) noexcept {
  double phase = std::fmod(t * ShimmerSpeed, ShimmerFrequency);
  if (phase < 0.0) {
    phase += ShimmerFrequency;
  }
  double v = std::max(phase - ShimmerFrequency + 1.0, -1.0);
  constexpr double Pi = 3.14159265358979323846;
  return std::cos(v * Pi);
}
} // namespace

void Shimmer(double seconds, double &x, double &y) noexcept {
  x = ShimmerChannel(seconds);
  y = ShimmerChannel(seconds + 0.5);
}

double ShimmerAcross(double seconds, double diagonal) noexcept {
  double a{}, b{};
  Shimmer(seconds, a, b);
  double t = Clamp01(diagonal);
  return (a + ((b - a) * t)) * 0.5;
}

double DiagonalRamp(double stX, double stY) noexcept {
  return Clamp01(0.5 + (0.25 * (stY - stX)));
}

void NoiseUv(double stX, double stY, double seconds, double &u, double &v) noexcept {
  double cx{}, cy{};
  NoiseOffset(seconds, cx, cy);
  u = ((stX / NoiseScale) + cx) * 0.5 + 0.5;
  v = ((stY / NoiseScale) + cy) * 0.5 + 0.5;
}

double LineTableCoordinate(double noise) noexcept {
  return std::pow(Clamp01(noise), LineNoiseExponent);
}

double LineToneCurve(double value) noexcept {
  double x = Clamp01(value);
  if (x <= 0.2) {
    return CubicSegment(x, 0.0, 0.06742977958332985, 10.114466937499461, -21.008079177080553);
  }
  if (x <= 0.9) {
    return CubicSegment(x - 0.2, 0.25, 1.592247053333448, -2.490380568748873, 2.2032464762493706);
  }
  return CubicSegment(x - 0.9, 0.9, 1.3444865771716008, 2.1364370313748053, -55.81302803090816);
}

FocusColor Sample(double t) noexcept {
  double x = Clamp01(t) * (kColorTableCount - 1);
  int i = static_cast<int>(std::floor(x));
  if (i >= kColorTableCount - 1) {
    return kColorTable[kColorTableCount - 1];
  }
  double f = x - i;
  FocusColor const &a = kColorTable[i];
  FocusColor const &b = kColorTable[i + 1];
  return {
      static_cast<uint8_t>(std::round(a.r + ((b.r - a.r) * f))),
      static_cast<uint8_t>(std::round(a.g + ((b.g - a.g) * f))),
      static_cast<uint8_t>(std::round(a.b + ((b.b - a.b) * f)))};
}

FocusColor ConvertForActiveOutput(FocusColor color) noexcept {
  wchar_t value[6]{};
  DWORD length = GetEnvironmentVariableW(
      L"PROSPERISMO_PS5_FOCUS_PAPER_WHITE", value, static_cast<DWORD>(std::size(value)));
  bool enabled =
      (length == 1 && value[0] == L'1') ||
      (length == 4 && CompareStringOrdinal(value, 4, L"true", 4, TRUE) == CSTR_EQUAL);
  if (!enabled) {
    return color;
  }

  double red = std::pow(color.r / 255.0, 2.35);
  double green = std::pow(color.g / 255.0, 2.35);
  double blue = std::pow(color.b / 255.0, 2.35);

  double outRed = (0.6398802 * red) + (0.3273893 * green) + (0.03271094 * blue);
  double outGreen = (0.06480824 * red) + (0.9353735 * green) - (0.0001436224 * blue);
  double outBlue = (0.01050037 * red) + (0.07885341 * green) + (0.9105756 * blue);

  constexpr double PaperWhiteScale = 0.025;
  constexpr double EncodeGamma = 1.0 / 2.2;
  return FromUnit(
      std::pow(std::max(0.0, outRed * PaperWhiteScale), EncodeGamma),
      std::pow(std::max(0.0, outGreen * PaperWhiteScale), EncodeGamma),
      std::pow(std::max(0.0, outBlue * PaperWhiteScale), EncodeGamma));
}

} // namespace Palette

// ---- FocusNoiseTexture -----------------------------------------------------

bool FocusNoiseTexture::LoadFromPngFile(wchar_t const *path) noexcept {
  m_samples.clear();
  m_width = 0;
  m_height = 0;
  if (!path || !path[0]) {
    return false;
  }
  try {
    auto factory = winrt::create_instance<IWICImagingFactory>(
        CLSID_WICImagingFactory, CLSCTX_INPROC_SERVER);
    winrt::com_ptr<IWICBitmapDecoder> decoder;
    winrt::check_hresult(factory->CreateDecoderFromFilename(
        path, nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, decoder.put()));
    winrt::com_ptr<IWICBitmapFrameDecode> frame;
    winrt::check_hresult(decoder->GetFrame(0, frame.put()));
    winrt::com_ptr<IWICFormatConverter> converter;
    winrt::check_hresult(factory->CreateFormatConverter(converter.put()));
    winrt::check_hresult(converter->Initialize(
        frame.get(),
        GUID_WICPixelFormat32bppBGRA,
        WICBitmapDitherTypeNone,
        nullptr,
        0.0,
        WICBitmapPaletteTypeCustom));
    UINT width{}, height{};
    winrt::check_hresult(converter->GetSize(&width, &height));
    if (width == 0 || height == 0 || width > 4096 || height > 4096) {
      return false;
    }
    std::vector<uint8_t> bgra(static_cast<size_t>(width) * height * 4);
    winrt::check_hresult(converter->CopyPixels(
        nullptr, width * 4, static_cast<UINT>(bgra.size()), bgra.data()));

    m_samples.resize(static_cast<size_t>(width) * height);
    for (UINT y = 0; y < height; ++y) {
      for (UINT x = 0; x < width; ++x) {
        uint8_t const *pixel = bgra.data() + ((static_cast<size_t>(y) * width + x) * 4);
        // Rec.709 luminance, exactly as Ps5FocusNoiseTexture computes it.
        m_samples[(static_cast<size_t>(y) * width) + x] =
            ((pixel[2] * 0.2126) + (pixel[1] * 0.7152) + (pixel[0] * 0.0722)) / 255.0;
      }
    }
    m_width = static_cast<int>(width);
    m_height = static_cast<int>(height);
    return true;
  } catch (...) {
    m_samples.clear();
    m_width = 0;
    m_height = 0;
    return false;
  }
}

double FocusNoiseTexture::Sample(double u, double v) const noexcept {
  if (m_samples.empty() || m_width <= 0 || m_height <= 0) {
    return 0.5;
  }
  // PSM's normalized Linear + ClampToEdge sample: texel centres at (n+.5)/size.
  u = Clamp01(u);
  v = Clamp01(v);
  double fx = (u * m_width) - 0.5;
  double fy = (v * m_height) - 0.5;
  int floorX = static_cast<int>(std::floor(fx));
  int floorY = static_cast<int>(std::floor(fy));
  int x0 = std::clamp(floorX, 0, m_width - 1);
  int y0 = std::clamp(floorY, 0, m_height - 1);
  int x1 = std::clamp(floorX + 1, 0, m_width - 1);
  int y1 = std::clamp(floorY + 1, 0, m_height - 1);
  double tx = fx - std::floor(fx);
  double ty = fy - std::floor(fy);
  double a = Lerp(m_samples[(static_cast<size_t>(y0) * m_width) + x0],
                  m_samples[(static_cast<size_t>(y0) * m_width) + x1], tx);
  double b = Lerp(m_samples[(static_cast<size_t>(y1) * m_width) + x0],
                  m_samples[(static_cast<size_t>(y1) * m_width) + x1], tx);
  return Lerp(a, b, ty);
}

// ---- FocusTimeline ---------------------------------------------------------

double FocusTimeline::InOutAnimationCurve(double t) noexcept {
  return t > 0.0 ? 1.0 - std::pow(1.0 - (t * 0.5), 10.0) : 0.0;
}

double FocusTimeline::MovingAnimationCurve(double t) noexcept {
  return t > 0.0 ? 1.0 - std::pow(1.0 - (t * 0.5), 5.0) : 0.0;
}

double FocusTimeline::WarpAnimationCurve(double t, double momentum) noexcept {
  return 1.0 - ((1.0 - momentum) * std::pow(1.0 - (t * 0.5), 10.0));
}

double FocusTimeline::MomentumFor(double distance) noexcept {
  double t = (distance - MomentumNearDistance) / (MomentumFarDistance - MomentumNearDistance);
  double mapped = Lerp(MomentumMinimum, MomentumMaximum, t);
  return std::clamp(mapped, 0.0, MomentumMaximum);
}

double FocusTimeline::WarpProgress() const noexcept {
  // The console's one-frame lead: (elapsed + SecPerFrame) / 0.25.
  return Clamp01((m_warpElapsed + SecPerFrame) / WarpAnimationDuration);
}

double FocusTimeline::Showing() const noexcept {
  switch (m_state) {
    case FocusState::Shown:
      return 1.0;
    case FocusState::Showing: {
      double t = (m_showElapsed - InMotionDelay) / InMotionDuration;
      return InOutAnimationCurve(Clamp01(t));
    }
    case FocusState::Hiding:
      return 1.0 - InOutAnimationCurve(Clamp01(m_showElapsed / OutMotionDuration));
    default:
      return 0.0;
  }
}

double FocusTimeline::Moving() const noexcept {
  if (m_moveElapsed >= MovingDuration) {
    return 0.0;
  }
  return 1.0 - MovingAnimationCurve(Clamp01(m_moveElapsed / MovingDuration));
}

double FocusTimeline::Pressing() const noexcept {
  if (m_pressElapsed >= PressingDuration * 2.0) {
    return 0.0;
  }
  if (m_pressElapsed < PressingDuration) {
    return PressingAnimationCurve(Clamp01(m_pressElapsed / PressingDuration));
  }
  double t = (m_pressElapsed - PressingDuration) / PressingDuration;
  return 1.0 - PressingAnimationCurve(Clamp01(t));
}

double FocusTimeline::FadeRatio() const noexcept {
  if (m_state != FocusState::Hiding) {
    return 1.0;
  }
  double rate = m_keyRepeating ? DefaultKeyRepeatFadeOutRate : 1.0;
  return Clamp01(m_fadeElapsed * rate / DefaultFadeOutTime);
}

double FocusTimeline::BaseOpacity() const noexcept {
  switch (m_state) {
    case FocusState::Showing:
      return FadeRatio();
    case FocusState::Shown:
      return 1.0;
    case FocusState::Hiding:
      return 1.0 - FadeRatio();
    default:
      return 0.0;
  }
}

double FocusTimeline::AreaOpacity() const noexcept {
  return std::max(0.0, BaseOpacity() * Showing());
}

double FocusTimeline::LineOpacity() const noexcept {
  // The 1 - 4*Moving law: the band is dark for roughly the first 45% of a move.
  return std::max(0.0, BaseOpacity() * Showing() * (1.0 - (MovingLineOpacityRate * Moving())));
}

double FocusTimeline::InOutScale() const noexcept {
  double n = std::max(std::max(m_to.width, m_to.height), 1.0);
  double scale = std::min(1.0 + (MaxInOutExtendingLength / n), LineScaleRatioOnHiding);
  return Lerp(scale, 1.0, Showing());
}

double FocusTimeline::BandWidth() const noexcept {
  double w = LineThickness + Lerp(LineThickness, 0.0, Showing());
  if (Pressing() > 0.0) {
    w += w + LineOffset;
  }
  return w;
}

double FocusTimeline::WarpStretch() const noexcept {
  double width = std::max(m_to.width, 1.0);
  double ratio = std::max(m_to.height, 1.0) / width;
  double strain = ratio < WarpStrainAspectFloor ? 0.0 : WarpStrain;
  return std::min(MaxWarpStretch, strain * Moving() * m_distance);
}

FocusRect FocusTimeline::CurrentRect() const noexcept {
  if (!IsWarping()) {
    return m_to;
  }
  double k = WarpAnimationCurve(WarpProgress(), m_momentum);
  double cx = Lerp(m_from.CenterX(), m_to.CenterX(), k);
  double cy = Lerp(m_from.CenterY(), m_to.CenterY(), k);
  double w = std::max(0.0, Lerp(m_from.width, m_to.width, k));
  double h = std::max(0.0, Lerp(m_from.height, m_to.height, k));
  return {cx - (w / 2.0), cy - (h / 2.0), w, h};
}

double FocusTimeline::CurrentRadius() const noexcept {
  if (!IsWarping()) {
    return m_toRadius;
  }
  // The radius rides MovingAnimationCurve - a different curve from the rect.
  return Lerp(m_fromRadius, std::max(m_toRadius, 0.0), MovingAnimationCurve(WarpProgress()));
}

void FocusTimeline::WarpDistortionMatrix(
    double &m11, double &m12, double &m21, double &m22) const noexcept {
  double s = WarpStretch();
  if (!(s > 0.0)) {
    m11 = 1.0;
    m12 = 0.0;
    m21 = 0.0;
    m22 = 1.0;
    return;
  }
  double c = std::cos(m_angle);
  double n = std::sin(m_angle);
  double a = 1.0;
  double b = 1.0 / (1.0 - s);
  double off = n * c * (a - b);
  m11 = ((c * c) * a) + ((n * n) * b);
  m12 = off;
  m21 = off;
  m22 = ((n * n) * a) + ((c * c) * b);
}

void FocusTimeline::Retarget(FocusRect const &target, double radius) noexcept {
  if (!IsFiniteRect(target)) {
    return;
  }
  if (!IsVisible()) {
    ShowAt(target, radius);
    return;
  }
  if (RectsClose(m_to, target) && std::abs(m_toRadius - radius) < 0.5) {
    return;
  }
  StartWarp(CurrentRect(), CurrentRadius(), target, radius);
  if (m_state == FocusState::Hiding) {
    m_state = FocusState::Showing;
    m_showElapsed = InMotionDelay;
  }
}

void FocusTimeline::ShowAt(FocusRect const &rect, double radius) noexcept {
  if (!IsFiniteRect(rect)) {
    return;
  }
  m_from = rect;
  m_to = rect;
  m_fromRadius = radius;
  m_toRadius = radius;
  m_warpElapsed = WarpAnimationDuration;
  m_moveElapsed = MovingDuration;
  m_momentum = 0.0;
  m_distance = 0.0;
  m_angle = 0.0;
  if (m_state == FocusState::Hidden || m_state == FocusState::Hiding) {
    m_state = FocusState::Showing;
    m_showElapsed = 0.0;
    m_fadeElapsed = 0.0;
  }
}

void FocusTimeline::Hide() noexcept {
  if (m_state == FocusState::Hidden || m_state == FocusState::Hiding) {
    return;
  }
  m_state = FocusState::Hiding;
  m_showElapsed = 0.0;
  m_fadeElapsed = 0.0;
}

void FocusTimeline::Reset() noexcept {
  m_state = FocusState::Hidden;
  m_showElapsed = 0.0;
  m_fadeElapsed = 0.0;
  m_warpElapsed = WarpAnimationDuration;
  m_moveElapsed = MovingDuration;
  m_pressElapsed = 1.0e300;
}

void FocusTimeline::SetPressed(bool pressed) noexcept {
  if (pressed) {
    m_pressElapsed = 0.0;
  }
}

void FocusTimeline::Advance(double seconds) noexcept {
  if (!(seconds > 0.0) || std::isnan(seconds)) {
    return;
  }
  m_clock += seconds;
  if (m_warpElapsed < WarpAnimationDuration) {
    m_warpElapsed = std::min(WarpAnimationDuration, m_warpElapsed + seconds);
  }
  if (m_moveElapsed < MovingDuration) {
    m_moveElapsed = std::min(MovingDuration, m_moveElapsed + seconds);
  }
  if (m_pressElapsed < PressingDuration * 2.0) {
    m_pressElapsed += seconds;
  }
  switch (m_state) {
    case FocusState::Showing:
      m_showElapsed += seconds;
      if (m_showElapsed >= InMotionDelay + InMotionDuration) {
        m_state = FocusState::Shown;
      }
      break;
    case FocusState::Hiding:
      m_showElapsed += seconds;
      m_fadeElapsed += seconds;
      if (m_showElapsed >= OutMotionDuration) {
        m_state = FocusState::Hidden;
      }
      break;
    default:
      break;
  }
}

void FocusTimeline::StartWarp(
    FocusRect const &fromRect, double fromRadius, FocusRect const &target, double radius) noexcept {
  m_from = fromRect;
  m_fromRadius = fromRadius;
  m_to = target;
  m_toRadius = radius;
  double dx = target.CenterX() - fromRect.CenterX();
  double dy = target.CenterY() - fromRect.CenterY();
  double d = std::sqrt((dx * dx) + (dy * dy));
  m_momentum = MomentumFor(d);
  m_distance = std::min(1.0, d / WarpDistanceReference);
  constexpr double Pi = 3.14159265358979323846;
  m_angle = std::atan2(dy, dx) + Pi;
  m_warpElapsed = 0.0;
  m_moveElapsed = 0.0;
}

// ---- Rasterizers -----------------------------------------------------------

bool RenderBandBitmap(
    FocusBitmap &out,
    double aspect,
    double bodyRatioX,
    double bodyRatioY,
    double radiusRatio,
    double bandRatio,
    double clock,
    FocusNoiseTexture const &noise) noexcept {
  constexpr int Grid = 192;
  int w = Grid;
  int h = Grid;
  if (aspect > 1.0) {
    h = std::max(16, static_cast<int>(std::round(Grid / aspect)));
  } else if (aspect > 0.0) {
    w = std::max(16, static_cast<int>(std::round(Grid * aspect)));
  }

  out.width = w;
  out.height = h;
  out.pixels.assign(static_cast<size_t>(w) * h * 4, 0);

  double halfW = (w / 2.0) * bodyRatioX;
  double halfH = (h / 2.0) * bodyRatioY;
  double minHalf = std::min(w, h) / 2.0;
  double radius = radiusRatio * minHalf;
  double band = std::max(bandRatio * minHalf, 0.5);
  double half = band * 0.5;

  for (int y = 0; y < h; ++y) {
    uint8_t *row = out.pixels.data() + (static_cast<size_t>(y) * w * 4);
    double py = (y + 0.5) - (h / 2.0);
    for (int x = 0; x < w; ++x) {
      size_t o = static_cast<size_t>(x) * 4;
      double px = (x + 0.5) - (w / 2.0);

      double sd = RoundedBoxDistance(px, py, halfW, halfH, radius);
      // Distance from the band's centreline - width measured perpendicular.
      double d = std::abs(sd);
      double coverage = 1.0 - FocusSmoothStep(half, half + 1.0, d);
      if (coverage <= 0.0) {
        continue;
      }

      double stX = halfW > 0.0 ? px / halfW : 0.0;
      double stY = halfH > 0.0 ? py / halfH : 0.0;
      double u{}, v{};
      Palette::NoiseUv(stX, stY, clock, u, v);
      double noiseValue = noise.Sample(u, v);
      double tableCoordinate = Palette::LineTableCoordinate(noiseValue);
      FocusColor tint = Palette::ConvertForActiveOutput(Palette::Sample(tableCoordinate));

      // The noise-derived coordinate drives both RGB and the tone curve; the
      // tone scalar is lerped up from LineMinOpacity and only then multiplied
      // by the rounded-box coverage.
      double tone = Palette::LineToneCurve(tableCoordinate);
      double noiseAlpha = Palette::LineMinOpacity + ((1.0 - Palette::LineMinOpacity) * tone);
      double shaped = ApplyAlphaGamma(coverage * noiseAlpha, Palette::LineAlphaGamma);

      auto a = static_cast<uint8_t>(std::clamp(shaped * 255.0, 0.0, 255.0));
      row[o + 0] = static_cast<uint8_t>(tint.b * a / 255);
      row[o + 1] = static_cast<uint8_t>(tint.g * a / 255);
      row[o + 2] = static_cast<uint8_t>(tint.r * a / 255);
      row[o + 3] = a;
    }
  }
  return true;
}

bool RenderWashBitmap(
    FocusBitmap &out,
    double aspect,
    double bodyRatio,
    double radiusRatio,
    double fadeRatio,
    double clock,
    double moving,
    double pressing,
    FocusNoiseTexture const &noise) noexcept {
  constexpr int Grid = 96;
  int w = Grid;
  int h = Grid;
  if (aspect > 1.0) {
    h = std::max(8, static_cast<int>(std::round(Grid / aspect)));
  } else if (aspect > 0.0) {
    w = std::max(8, static_cast<int>(std::round(Grid * aspect)));
  }

  out.width = w;
  out.height = h;
  out.pixels.assign(static_cast<size_t>(w) * h * 4, 0);

  double halfW = (w / 2.0) * bodyRatio;
  double halfH = (h / 2.0) * bodyRatio;
  double targetMinHalf = std::min(w, h) / 2.0;
  double radius = radiusRatio * targetMinHalf;
  double fade = std::max(fadeRatio * targetMinHalf, 0.5);

  for (int y = 0; y < h; ++y) {
    uint8_t *row = out.pixels.data() + (static_cast<size_t>(y) * w * 4);
    double py = (y + 0.5) - (h / 2.0);
    for (int x = 0; x < w; ++x) {
      size_t o = static_cast<size_t>(x) * 4;
      double px = (x + 0.5) - (w / 2.0);

      double signedDistance = RoundedBoxDistance(px, py, halfW, halfH, radius);
      if (signedDistance > fade) {
        continue;
      }
      double coverage = AreaCoverage(px, py, halfW, halfH, radius, fade);

      double stX = halfW > 0.0 ? px / halfW : 0.0;
      double stY = halfH > 0.0 ? py / halfH : 0.0;
      double diagonal = Palette::DiagonalRamp(stX, stY);

      // AreaFocus rests on the diagonal ShimmerParam interpolation; travel
      // blends toward image_focus_noise by Moving*0.5; a press pulls the
      // result toward the shader's literal 0.15.
      double shimmer = std::max(Palette::ShimmerAcross(clock, diagonal), 0.0);
      double u{}, v{};
      Palette::NoiseUv(stX, stY, clock, u, v);
      double noiseValue = noise.Sample(u, v);
      double morph = Clamp01(moving * 0.5);
      double intensity = shimmer + ((noiseValue - shimmer) * morph);
      intensity += Clamp01(pressing) * (Palette::PressingIntensity - intensity);
      intensity *= coverage;

      double shaped = ApplyAlphaGamma(intensity, Palette::AreaAlphaGamma);
      if (shaped < Palette::AreaMinOpacity) {
        shaped = Palette::AreaMinOpacity;
      }

      FocusColor tint =
          Palette::ConvertForActiveOutput(Palette::Sample(Clamp01(intensity)));
      auto a = static_cast<uint8_t>(std::clamp(shaped * 255.0, 0.0, 255.0));
      row[o + 0] = static_cast<uint8_t>(tint.b * a / 255);
      row[o + 1] = static_cast<uint8_t>(tint.g * a / 255);
      row[o + 2] = static_cast<uint8_t>(tint.r * a / 255);
      row[o + 3] = a;
    }
  }
  return true;
}

} // namespace Prosperismo::FocusKit
