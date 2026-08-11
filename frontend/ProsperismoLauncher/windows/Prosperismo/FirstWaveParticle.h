#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

// The PS5 shell background particle *draw* maths, recovered from the four
// programs `particle_vv`, `particle_p`, `large_particle_vv` and
// `large_particle_p` in the 12.40 NPXS40087 eboot. See
// docs/sony-shell/particle-draw.md (and particle-system.md for the simulation) for the evidence and the caveats.
//
// Only self-contained arithmetic that the ISA states outright is ported here.
// Anything that depends on the runtime constant buffer (light lists, gradient
// axes, texture bindings, palette selector) is left as a caller-supplied
// parameter rather than guessed.
//
// Deliberately free of platform headers so it can be unit-tested on any host.

namespace Prosperismo::FirstWave::Particle {

// ---------------------------------------------------------------------------
// Billboard expansion (both vertex programs)
// ---------------------------------------------------------------------------

// Both vertex programs are NGG primitive shaders that emit six vertices per
// particle: `particle = tid / 6`, `corner = tid % 6`. The corner offsets come
// from a 48-byte table embedded in the program image itself (six float2, read
// through a V# with num_records = 48 and dword3 = 0x10005004). Both tables are
// byte-identical: two triangles spanning the [-1, 1] square.
inline constexpr std::size_t kVerticesPerParticle = 6;

struct Corner {
  float x{};
  float y{};
};

inline constexpr std::array<Corner, kVerticesPerParticle> kQuadCorners{{
    {-1.0f, -1.0f},
    {1.0f, -1.0f},
    {-1.0f, 1.0f},
    {1.0f, -1.0f},
    {-1.0f, 1.0f},
    {1.0f, 1.0f},
}};

std::uint32_t ParticleIndexForVertex(std::uint32_t vertexIndex) noexcept;
std::uint32_t CornerIndexForVertex(std::uint32_t vertexIndex) noexcept;
Corner CornerForVertex(std::uint32_t vertexIndex) noexcept;

// ---------------------------------------------------------------------------
// Per-particle size lottery (both vertex programs)
// ---------------------------------------------------------------------------

// Both vertex programs derive the sprite size from a per-particle 32-bit seed
// with a Lehmer step. The multiply is `v_mul_lo_u32`, i.e. it truncates to 32
// bits *before* the modulus, so this is not textbook MINSTD/Park-Miller — the
// firmware reduces the truncated product, not the full 64-bit one. Recorded as
// found.
inline constexpr std::uint32_t kLehmerMultiplier = 16807u;      // 0x41a7
inline constexpr std::uint32_t kLehmerModulus = 2147483647u;    // 2^31 - 1
inline constexpr std::uint32_t kSizeBuckets = 1000u;            // 0x3e8
inline constexpr float kBucketScale = 0.001f;                   // 0x3a83126f

// The exact reduction the ISA performs: a 64-bit multiply-add by 0x80000001
// to form the quotient, then `x - quotient * (2^31 - 1)`.
std::uint32_t ReduceLehmer(std::uint32_t product) noexcept;

// `bucket = ReduceLehmer(16807 * seed) % 1000`, matching the firmware's
// magic-number division by 1000.
std::uint32_t SizeBucket(std::uint32_t seed) noexcept;

// size = minSize + 0.001f * (bucket * (maxSize - minSize)), in the firmware's
// operation order. `particle_vv` passes the raw per-particle seed;
// `large_particle_vv` adds a constant-buffer bias to the seed first.
float RandomSize(std::uint32_t seed, float minSize, float maxSize) noexcept;

// ---------------------------------------------------------------------------
// Minimum-screen-size clamp (particle_vv)
// ---------------------------------------------------------------------------

// `particle_vv` refuses to let a quad axis shrink below a floor: each of the
// two screen-space quad axes is scaled by max(floor, len + eps) / (len + eps).
// The epsilon is the literal 1e-6 that appears throughout these programs.
inline constexpr float kLengthEpsilon = 9.999999975e-07f; // 0x358637bd

float MinimumSizeScale(float axisLength, float minimumLength) noexcept;

// The floor itself is a lerp from the particle's own size toward a constant,
// driven by the same gradient term that param0.w carries.
float MinimumLength(float size, float gradient, float gradientMinimum) noexcept;

// ---------------------------------------------------------------------------
// particle_vv projection
// ---------------------------------------------------------------------------

// `particle_vv` has its projection folded into literals rather than reading a
// matrix: clip.xy = (kProjScaleX * viewX, kProjScaleY * viewY), and
// clip.z = kDepthA * clip.w - kDepthB.
//
// kProjScaleY is 0.5 * cot(22.5 deg) to within 1.4e-7 relative, and
// kProjScaleX / kProjScaleY is 0.5625 = 9/16, so this is a 45-degree vertical
// field of view at 16:9 — carrying the same extra factor of 1/2 that
// `large_particle_vv` builds explicitly with a `div:2` output modifier.
inline constexpr float kProjScaleX = 0.6789979935f; // 0x3f2dd2d0
inline constexpr float kProjScaleY = 1.2071069479f; // 0x3f9a827b
inline constexpr float kDepthA = 1.0020020008f;     // -(0xbf80419a)
inline constexpr float kDepthB = 0.1001000032f;     // -(0x3dcd013b)

// Solving kDepthA = f / (f - n), kDepthB = f * n / (f - n) gives these.
inline constexpr float kNearPlane = 0.0999f;
inline constexpr float kFarPlane = 50.0f;

float ClipZ(float clipW) noexcept;
float NdcZ(float clipW) noexcept;

// `large_particle_vv` instead builds the scale at runtime from a vertical field
// of view in *degrees*: it multiplies by 1/720 and feeds `v_sin_f32`/
// `v_cos_f32`, whose argument is in revolutions, so the angle is fovY/2 in
// radians. The result is halved by a `div:2` modifier, exactly as above.
inline constexpr float kDegreesToRevolutionsHalf = 0.0013888889225f; // 0x3ab60b61

float ProjectionScaleY(float verticalFovDegrees) noexcept;
float ProjectionScaleX(float verticalFovDegrees, float aspect) noexcept;

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------

float Saturate(float value) noexcept;
// The Hermite polynomial the firmware open-codes everywhere: t*t*(3 - 2t).
// The argument is clamped first, as every firmware site does.
float SmoothStep(float t) noexcept;

// ---------------------------------------------------------------------------
// particle_p — the small, sharp, procedural points
// ---------------------------------------------------------------------------

// `particle_p` contains no image ops at all. Its shape is a disc in the quad's
// own [-1, 1] corner space: fragments at radius >= 0.99 are killed outright
// (the program jumps to an `exec = 0` / null-export / `s_endpgm` epilogue).
inline constexpr float kPointCutoffRadius = 0.99f;  // 0x3f7d70a4
inline constexpr float kPointInnerRadiusCap = 0.98f; // 0x3f7ae148

// The stretch the vertex program applied to each quad axis is undone per
// fragment: the effective core radius shrinks along whichever axis was
// stretched. `stretchX`/`stretchY` are param2.zw, the two MinimumSizeScale
// factors; the firmware takes their absolute values.
float AnisotropicRadius(float cornerX, float cornerY, float stretchX, float stretchY) noexcept;

// The core radius blends from the anisotropic value toward the particle's own
// `blurBoundary` as the gradient term rises. The blend weight is
// SmoothStep(saturate(10 * gradient)). `blurBoundary` is the compute stage's
// size curve, range [0.2, 1.0] — see particle-system.md — and it lands here as
// the radius of the disc's flat, unblurred top, which is what the name says.
inline constexpr float kGradientBlendGain = 10.0f; // 0x41200000
float GradientBlend(float gradient) noexcept;
float PointInnerRadius(float anisotropicRadius, float blurBoundary, float gradient) noexcept;

bool PointIsKilled(float cornerX, float cornerY) noexcept;

// The falloff itself:
//   r  = length(corner)
//   t  = saturate((r - 0.99) / (min(0.98, inner) - 0.99))
//   s  = t*t*(3 - 2t)
//   a  = pow(s, 1 + gradient)          <- via v_log_f32 / v_exp_f32
// The denominator is always negative, so t is 1 at the core and 0 at r = 0.99.
float PointShape(float cornerX, float cornerY, float innerRadius, float gradient) noexcept;

// Life fade. `particle_p` uses `curLife` and the latched `renLife` that
// `particle_vv` writes back at corner 0 when `renLife` is negative:
//   fade = smoothstep(saturate(2*curLife))
//        * smoothstep(saturate(2*(renLife - curLife)))
float PointLifeFade(float curLife, float renLife) noexcept;

struct PointFragment {
  float cornerX{};        // param2.x
  float cornerY{};        // param2.y
  float stretchX{};       // param2.z
  float stretchY{};       // param2.w
  float blurBoundary{};   // ParticleProperty.blurBoundary, record +0x0c
  float gradient{};       // param0.w
  float curLife{};        // ParticleProperty.curLife,  record +0x38
  float renLife{};        // ParticleProperty.renLife,   record +0x40
  float gradientBrightness{1.0f}; // lerp(1, cb[0x88], smoothstep(gradient ramp))
  float lightSum{1.0f};           // the accumulated diffuse+ambient+specular scalar
};

// The complete scalar the firmware multiplies the palette colour by. Returns 0
// for killed fragments so callers can branch on it if they prefer.
float PointIntensity(PointFragment const &fragment) noexcept;

// ---------------------------------------------------------------------------
// particle_p palette
// ---------------------------------------------------------------------------

// An 84-byte (7 x float3) table is embedded in `particle_p`'s image. A PS
// constant at cb0+0x14 selects which half is used: non-zero picks entries
// 0..3 indexed by (id % 4), zero picks entries 4..6 indexed by 4 + (id % 3).
inline constexpr std::size_t kPaletteSize = 7;

struct Colour {
  float r{};
  float g{};
  float b{};
};

inline constexpr std::array<Colour, kPaletteSize> kPalette{{
    {0.913725495f, 0.329411775f, 0.435294122f}, // 0
    {0.788235307f, 0.498039216f, 0.701960802f}, // 1
    {0.000000000f, 0.627451003f, 0.533333361f}, // 2
    {0.345098048f, 0.501960814f, 0.756862760f}, // 3
    {0.693767011f, 0.459286004f, 0.204933986f}, // 4
    {0.420054019f, 0.187301993f, 0.075132005f}, // 5
    {0.501960814f, 0.329411775f, 0.211764708f}, // 6
}};

// When cb[0x13c] == 6 and the warm index lands on entry 6, the firmware
// substitutes this literal instead of reading the table.
inline constexpr Colour kWarmOverride{0.420053989f, 0.254900008f, 0.142295003f};

// `useSymbolPalette` is (cb0[0x14] != 0); `overrideThirdWarm` is
// (cb[0x13c] == 6). `id` is the raw per-particle dword; the firmware's modulus
// is signed and truncating, hence the int32 parameter.
Colour PaletteColour(std::int32_t id, bool useSymbolPalette, bool overrideThirdWarm) noexcept;

// `particle_p` writes alpha = 0 (a literal 0 packed into the second half of the
// compressed MRT0 export), so the pass is purely additive.
inline constexpr float kPointOutputAlpha = 0.0f;

// ---------------------------------------------------------------------------
// large_particle_p — the big, soft, out-of-focus discs
// ---------------------------------------------------------------------------

// Same 0.99 kill radius, but the profile is a defocus edge rather than a
// power falloff. The softness width is negative by construction:
//
//   width = smoothstep(focusT) * (min(0.9998, k) - 0.9999) - 1.0001659e-4
//   edge  = smoothstep(saturate((r - 1) / width))
//
// With focusT = 0 the width is -1.0001659e-4 and the disc is effectively hard
// edged; as focusT rises toward 1 and k falls, the width approaches -1 and the
// profile becomes a full-radius Hermite ramp — the out-of-focus look.
inline constexpr float kDefocusKCap = 0.9998000264f;    // 0x3f7ff2e5
inline constexpr float kDefocusKBias = 0.9998999834f;   // 0x3f7ff972
inline constexpr float kDefocusMinWidth = 1.0001659e-04f; // -(0xb8d1c000)

float DefocusWidth(float focusT, float k) noexcept;
float SoftDiscEdge(float radius, float focusT, float k) noexcept;

// The large discs use a *different* pair of life fields: `curLife` (+0x38) and
// `maxLife` (+0x3c), not the `renLife` latch that `particle_p` reads, and there
// is no factor of two:
//   fade = smoothstep(saturate(max(0, curLife)))
//        * smoothstep(saturate(maxLife - curLife))
float DiscLifeFade(float curLife, float maxLife) noexcept;

// Final alpha: cb[0xe0] * edge * lifeFade * depthFade. The disc writes
// premultiplied colour (value scaled by alpha) *and* this alpha, unlike the
// additive points.
float DiscAlpha(float opacity, float edge, float lifeFade, float depthFade) noexcept;

// The two background textures are cross-faded by a 10-bit weight packed into
// bits [10:1] of the flags dword at cb[0x60]; bit 0 gates the textured path
// entirely. The scale literal is exactly 1/1023.
inline constexpr float kTextureBlendScale = 0.0009775171056f; // 0x3a802008
inline constexpr int kTextureBlendMax = 1023;

bool DiscUsesTexture(std::uint32_t flags) noexcept;
float DiscTextureBlend(std::uint32_t flags) noexcept;

// ---------------------------------------------------------------------------
// HSV, as `large_particle_p` open-codes it
// ---------------------------------------------------------------------------

// Hue is carried in *radians*: the firmware reduces modulo 2*pi with an
// explicit floor, scales by 6/(2*pi) = 0.9549296 into sextants, and selects
// with a cndmask cascade whose thresholds are 1.9999, 3.9999 and 4.9999.
inline constexpr float kSextantScale = 0.9549296498f;   // 0x3f747645
inline constexpr float kTwoPi = 6.2831854820f;          // 0x40c90fdb
inline constexpr float kPiOverThree = 1.0471975803f;    // 0x3f860a92

Colour HsvRadiansToRgb(float hueRadians, float saturation, float value) noexcept;

struct Hsv {
  float hueRadians{};
  float saturation{};
  float value{};
};

Hsv RgbToHsvRadians(Colour const &colour) noexcept;

} // namespace Prosperismo::FirstWave::Particle
