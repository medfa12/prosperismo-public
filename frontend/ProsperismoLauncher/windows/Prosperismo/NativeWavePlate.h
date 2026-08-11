#pragma once

#include <cstdint>

namespace Prosperismo::NativeWave {

// Direct C++ translation of SharpEmu's Ps5NativeWavePlateEvaluator.  This is
// Sony's 4.03 NPXS40087 Plane2 / wave_bg_p HomeScreen route (record 2), not a
// colour approximation and not the separate 12.40 FirstWave fallback.
bool RenderHomePlateBgra8Premultiplied(
    std::uint8_t* destination,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t stride,
    std::int64_t frame) noexcept;

} // namespace Prosperismo::NativeWave
