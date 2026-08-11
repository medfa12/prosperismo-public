#pragma once

// Faithful C++ translation of SharpEmu's recovered PS5 focus highlight:
//   ShellFocusRing.cs   -> FocusTimeline (ShellFocusRingTimeline, verbatim laws)
//   Ps5FocusField.cs    -> field functions (SDF, smoothstep, gamma, gates)
//   ShellFocusPalette.cs-> palette (7-stop table, tone curve, noise UV, shimmer)
//   Ps5FocusNoiseTexture.cs -> FocusNoiseTexture (image_focus_noise, clamp-linear)
//   ShellFocusBand.cs   -> RenderBandBitmap (192-grid distance-field band)
//   ShellFocusWash.cs   -> RenderWashBitmap (96-grid area wash)
// The recovered constants and curve shapes are Sony's; do not "tune" them.

#include <cstdint>
#include <vector>

namespace Prosperismo::FocusKit {

struct FocusRect {
  double x{};
  double y{};
  double width{};
  double height{};

  double CenterX() const noexcept { return x + width / 2.0; }
  double CenterY() const noexcept { return y + height / 2.0; }
  FocusRect Inflate(double amount) const noexcept {
    return {x - amount, y - amount, width + amount * 2.0, height + amount * 2.0};
  }
};

// ---- Ps5FocusField ---------------------------------------------------------

double RoundedBoxDistance(double px, double py, double halfWidth, double halfHeight, double radius) noexcept;
double FocusSmoothStep(double edge0, double edge1, double x) noexcept;
double AreaCoverage(double px, double py, double halfWidth, double halfHeight, double radius, double edgeFadeLength) noexcept;
double ApplyAlphaGamma(double alpha, double gamma) noexcept;
bool AreaPassApplies(double targetWidth, double targetHeight, double screenWidth, double screenHeight) noexcept;
double AreaOpacityScaleForSize(double targetWidth, double targetHeight, double screenWidth, double screenHeight) noexcept;

// ---- ShellFocusPalette -----------------------------------------------------

struct FocusColor {
  uint8_t r{};
  uint8_t g{};
  uint8_t b{};
};

namespace Palette {
constexpr double LineThickness = 3.0;
constexpr double LineOffset = 3.0;
constexpr double AreaEdgeFadeLength = 5.0;
constexpr double EdgeFadeMinLength = 10.0;
constexpr double LineScaleRatioOnHiding = 1.2;
constexpr double LineMinOpacity = 0.065;
constexpr double AreaMinOpacity = 0.0;
constexpr double LineAlphaGamma = 1.0;
constexpr double AreaAlphaGamma = 0.8;
constexpr double NoiseScale = 5.0;
constexpr double AreaRenderingThreshold = 0.4;
constexpr double AreaOpacityDecreaseRateBySize = 30.0;
constexpr double AreaOpacityMinimumDecreaseValueBySize = 0.5;
constexpr double NoiseMoveFrequency = 0.25;
constexpr double ShimmerSpeed = 1.0;
constexpr double ShimmerFrequency = 5.0;
constexpr double LineNoiseExponent = 1.5;
constexpr double PressingIntensity = 0.15;

// FocusRenderManager advances CPU focus surfaces on SecPerFrame (60 Hz).
int AnimationFrame(double seconds) noexcept;
void NoiseOffset(double seconds, double &x, double &y) noexcept;
void Shimmer(double seconds, double &x, double &y) noexcept;
double ShimmerAcross(double seconds, double diagonal) noexcept;
double DiagonalRamp(double stX, double stY) noexcept;
void NoiseUv(double stX, double stY, double seconds, double &u, double &v) noexcept;
double LineTableCoordinate(double noise) noexcept;
double LineToneCurve(double value) noexcept;
FocusColor Sample(double t) noexcept;
FocusColor ConvertForActiveOutput(FocusColor color) noexcept;
} // namespace Palette

// ---- Ps5FocusNoiseTexture --------------------------------------------------

// Sony's image_focus_noise (64x64 indexed PNG from Sce.PlayStation.PUI_UI3.rco),
// decoded to a single-channel Rec.709 luminance field and sampled with PSM's
// Linear + ClampToEdge texture state. Missing texture samples as the 0.5 constant.
class FocusNoiseTexture {
 public:
  bool LoadFromPngFile(wchar_t const *path) noexcept;
  bool IsLoaded() const noexcept { return m_width > 0 && m_height > 0; }
  double Sample(double u, double v) const noexcept;

 private:
  std::vector<double> m_samples;
  int m_width{};
  int m_height{};
};

// ---- ShellFocusRingTimeline ------------------------------------------------

enum class FocusState { Hidden, Showing, Shown, Hiding };

class FocusTimeline {
 public:
  static constexpr double SecPerFrame = 0.01666667;
  static constexpr double InMotionDuration = 0.3;
  static constexpr double InMotionDelay = 0.2;
  static constexpr double OutMotionDuration = 0.3;
  static constexpr double MovingDuration = 0.3;
  static constexpr double PressingDuration = 0.3;
  static constexpr double WarpAnimationDuration = 0.25;
  static constexpr double DefaultFadeOutTime = 0.2;
  static constexpr double DefaultKeyRepeatFadeOutRate = 2.0;
  static constexpr double LineThickness = 3.0;
  static constexpr double LineOffset = 3.0;
  static constexpr double LineScaleRatioOnHiding = 1.2;
  static constexpr double MaxInOutExtendingLength = 80.0;
  static constexpr double MomentumNearDistance = 100.0;
  static constexpr double MomentumFarDistance = 1000.0;
  static constexpr double MomentumMinimum = 0.5;
  static constexpr double MomentumMaximum = 0.9;
  static constexpr double WarpDistanceReference = 1920.0;
  static constexpr double WarpStrain = 0.75;
  static constexpr double WarpStrainAspectFloor = 0.25;
  static constexpr double MaxWarpStretch = 0.2;
  static constexpr double MovingLineOpacityRate = 4.0;

  static double InOutAnimationCurve(double t) noexcept;
  static double PressingAnimationCurve(double t) noexcept { return InOutAnimationCurve(t); }
  static double MovingAnimationCurve(double t) noexcept;
  static double WarpAnimationCurve(double t, double momentum) noexcept;
  static double MomentumFor(double distance) noexcept;

  FocusState State() const noexcept { return m_state; }
  bool IsVisible() const noexcept { return m_state != FocusState::Hidden; }
  bool IsWarping() const noexcept { return m_warpElapsed < WarpAnimationDuration; }
  double Clock() const noexcept { return m_clock; }
  double TravelAngle() const noexcept { return m_angle; }
  double WarpProgress() const noexcept;
  double Showing() const noexcept;
  double Moving() const noexcept;
  double Pressing() const noexcept;
  double FadeRatio() const noexcept;
  double BaseOpacity() const noexcept;
  double AreaOpacity() const noexcept;
  double LineOpacity() const noexcept;
  double InOutScale() const noexcept;
  double BandWidth() const noexcept;
  double WarpStretch() const noexcept;
  FocusRect CurrentRect() const noexcept;
  double CurrentRadius() const noexcept;
  // Row-major symmetric 2x2 anisotropic scale of WarpStretch along TravelAngle.
  void WarpDistortionMatrix(double &m11, double &m12, double &m21, double &m22) const noexcept;

  void Retarget(FocusRect const &target, double radius) noexcept;
  void ShowAt(FocusRect const &rect, double radius) noexcept;
  void Hide() noexcept;
  void Reset() noexcept;
  void SetKeyRepeating(bool repeating) noexcept { m_keyRepeating = repeating; }
  void SetPressed(bool pressed) noexcept;
  void Advance(double seconds) noexcept;

 private:
  void StartWarp(FocusRect const &fromRect, double fromRadius, FocusRect const &target, double radius) noexcept;

  FocusState m_state{FocusState::Hidden};
  FocusRect m_from{};
  FocusRect m_to{};
  double m_fromRadius{};
  double m_toRadius{};
  double m_showElapsed{};
  double m_warpElapsed{WarpAnimationDuration};
  double m_moveElapsed{MovingDuration};
  double m_fadeElapsed{};
  double m_pressElapsed{1.0e300};
  double m_momentum{};
  double m_distance{};
  double m_angle{};
  double m_clock{};
  bool m_keyRepeating{false};
};

// ---- CPU rasterizers -------------------------------------------------------

struct FocusBitmap {
  int width{};
  int height{};
  // Premultiplied BGRA, width*4 stride.
  std::vector<uint8_t> pixels;
};

// ShellFocusBand.Build: 192-grid distance-field band, noise -> pow(1.5) table
// coordinate for both tint and the three-piece tone curve.
bool RenderBandBitmap(
    FocusBitmap &out,
    double aspect,
    double bodyRatioX,
    double bodyRatioY,
    double radiusRatio,
    double bandRatio,
    double clock,
    FocusNoiseTexture const &noise) noexcept;

// ShellFocusWash.Build: 96-grid area field; resting diagonal shimmer morphed
// toward the noise by Moving*0.5, pressing pulled toward 0.15.
bool RenderWashBitmap(
    FocusBitmap &out,
    double aspect,
    double bodyRatio,
    double radiusRatio,
    double fadeRatio,
    double clock,
    double moving,
    double pressing,
    FocusNoiseTexture const &noise) noexcept;

} // namespace Prosperismo::FocusKit
