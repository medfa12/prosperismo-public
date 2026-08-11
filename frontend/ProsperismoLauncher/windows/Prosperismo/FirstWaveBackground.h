#pragma once

#include <cstddef>
#include <cstdint>

namespace Prosperismo::FirstWave {

// Linear colour, matching the values consumed by Sony's FirstWave constant
// buffer. Components are deliberately not constrained to [0, 1]: the reset
// palette contains negative background components and clamps only after the
// procedural light has been added.
struct LinearRgba {
  float r{};
  float g{};
  float b{};
  float a{};
};

struct Palette {
  LinearRgba background0;
  LinearRgba background1;
  LinearRgba backgroundLight;
  LinearRgba reflection;
  LinearRgba environment;
  LinearRgba edge;
};

// The themed shader variant accepts zero or more premultiplied RGBA8 overlays.
// Each overlay is composed over the procedural result in array order.
struct ThemeOverlay {
  std::uint32_t premultipliedRgba{};
};

struct Parameters {
  // Constants.worldProjectionMatrix[0][0] and [1][1]. They are explicit
  // because the shader reads the active camera matrix; firmware does not
  // contain one universal aspect-ratio-independent literal for them.
  float projection00{};
  float projection11{};
  float opacity{1.0f};
  float timeSeconds{};
  Palette palette;
  ThemeOverlay const *themeOverlays{};
  std::size_t themeOverlayCount{};
};

// Palette record selected by the native FirstWave reset path in firmware
// 12.40. Values retain Sony's signed components and are divided by 255 exactly
// as the native uploader does.
Palette Firmware1240ResetPalette() noexcept;

// Evaluates fw_background_p before the shader's final fp16 export. sampleX and
// sampleY are SV_Position coordinates, so raster callers normally pass pixel
// coordinates plus 0.5f.
LinearRgba EvaluateBackgroundPixel(
    float sampleX,
    float sampleY,
    std::uint32_t width,
    std::uint32_t height,
    Parameters const &parameters) noexcept;

// Renders the evaluator to premultiplied BGRA8 for a D2D composition surface.
// Returns false without touching the destination when dimensions, stride, or
// projection constants are invalid.
bool RenderBackgroundBgra8Premultiplied(
    std::uint8_t *destination,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t stride,
    Parameters const &parameters) noexcept;

} // namespace Prosperismo::FirstWave
