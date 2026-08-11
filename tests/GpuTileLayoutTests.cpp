#include "graphics/guest_gpu/gpu_defs.h"
#include "graphics/guest_gpu/tile.h"
#include "graphics/host_gpu/renderer/image/textureCommon.h"
#include "graphics/host_gpu/renderer/image/tiler.h"

#include <array>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <vector>

namespace Libs::Graphics {
namespace {

void Check(bool value, const char* detail) {
	if (!value) {
		std::fprintf(stderr, "GpuTileLayoutTests: failed: %s\n", detail);
		std::abort();
	}
}

struct LinearBcVector {
	uint32_t                      format;
	uint32_t                      expected_pitch;
	uint32_t                      expected_total;
	std::array<TileSizeOffset, 4> levels;
};

void CheckLinearBlockCompressedPitch() {
	constexpr uint32_t linear = Prospero::GpuEnumValue(Prospero::TileMode::kLinear);
	constexpr uint32_t width = 65, height = 33, mip_count = 4;

	// These layout vectors were independently synthesized with both the 0.900 and 1.000
	// AgcGpuAddress tools. They describe the same geometry with the two BC element sizes.
	const std::array vectors {
	    LinearBcVector {
	        Prospero::GpuEnumValue(Prospero::BufferFormat::kBc1UNorm),
	        128,
	        4864,
	        {{{2304, 2560}, {1280, 1280}, {768, 512}, {512, 0}}},
	    },
	    LinearBcVector {
	        Prospero::GpuEnumValue(Prospero::BufferFormat::kBc3UNorm),
	        128,
	        7168,
	        {{{4608, 2560}, {1280, 1280}, {768, 512}, {512, 0}}},
	    },
	};

	for (const auto& vector: vectors) {
		const auto pitch = TileGetTexturePitch(vector.format, width, mip_count, linear);
		Check(pitch == vector.expected_pitch,
		      "linear BC base pitch did not preserve 256-byte element-block padding");

		TileSizeAlign                   total {};
		std::array<TileSizeOffset, 4> levels {};
		std::array<TilePaddedSize, 4> padded {};
		TileGetTextureSize(vector.format, width, height, pitch, mip_count, linear, &total,
		                   levels.data(), padded.data());
		Check(total.size == vector.expected_total && total.align == 256 &&
		          padded[0].width == vector.expected_pitch,
		      "linear BC mip-chain allocation changed");
		for (uint32_t mip = 0; mip < mip_count; ++mip) {
			Check(levels[mip].size == vector.levels[mip].size &&
			          levels[mip].offset == vector.levels[mip].offset,
			      "linear BC mip layout differs from the fixed vector");
		}
	}

	Check(TileGetTexturePitch(Prospero::GpuEnumValue(Prospero::BufferFormat::kBc1UNorm), 63, 1,
	                          linear) == 128 &&
	          TileGetTexturePitch(Prospero::GpuEnumValue(Prospero::BufferFormat::kBc3UNorm), 63, 1,
	                              linear) == 64,
	      "linear BC pitch ignored the compressed element size");
	Check(TileGetTexturePitch(Prospero::GpuEnumValue(Prospero::BufferFormat::kBc1UNorm), 128, 1,
	                          linear) == 128,
	      "already aligned linear BC pitch changed");
	Check(TileGetTexturePitch(Prospero::GpuEnumValue(Prospero::BufferFormat::k32Float), 65, 1,
	                          linear) == 128,
	      "linear uncompressed pitch changed");
}

void CheckLinearColorTargetMipLayout() {
	constexpr uint32_t linear = Prospero::GpuEnumValue(Prospero::TileMode::kLinear);
	constexpr uint32_t format = Prospero::GpuEnumValue(Prospero::BufferFormat::k32Float);
	constexpr uint32_t width = 65, height = 33, mip_count = 4;
	const auto         pitch = TileGetTexturePitch(format, width, mip_count, linear);
	Check(pitch == 128, "linear color-target mip chain did not derive its canonical base pitch");

	TileSizeAlign             total {};
	std::array<TileSizeOffset, 4> levels {};
	std::array<TilePaddedSize, 4> padded {};
	TileGetTextureSize(format, width, height, pitch, mip_count, linear, &total, levels.data(),
	                   padded.data());
	const std::array<TileSizeOffset, 4> expected_levels {
	    TileSizeOffset {16896, 7936},
	    TileSizeOffset {4352, 3584},
	    TileSizeOffset {2304, 1280},
	    TileSizeOffset {1280, 0},
	};
	const std::array<TilePaddedSize, 4> expected_padded {
	    TilePaddedSize {128, 33},
	    TilePaddedSize {64, 17},
	    TilePaddedSize {64, 9},
	    TilePaddedSize {64, 5},
	};
	Check(total.size == 24832 && total.align == 256,
	      "linear color-target mip chain footprint or alignment changed");
	for (uint32_t level = 0; level < mip_count; ++level) {
		Check(levels[level].size == expected_levels[level].size &&
		          levels[level].offset == expected_levels[level].offset &&
		          padded[level].width == expected_padded[level].width &&
		          padded[level].height == expected_padded[level].height,
		      "linear color-target mip level layout changed");
	}
}

void CheckExplicitRenderTargetPitch() {
	constexpr uint32_t width          = 1920;
	constexpr uint32_t height         = 1080;
	constexpr uint32_t explicit_pitch = 2048;
	constexpr uint32_t bytes          = 4;
	constexpr uint32_t expected_size  = 0x900000;
	constexpr uint32_t format         = Prospero::GpuEnumValue(Prospero::BufferFormat::k32Float);
	constexpr uint32_t tile           = Prospero::GpuEnumValue(Prospero::TileMode::kRenderTarget);
	Check(TileResolveRenderTargetPitch(width, 0, bytes) == 1920,
	      "automatic render-target pitch mapping changed");
	Check(TileResolveRenderTargetPitch(width, explicit_pitch, bytes) == explicit_pitch,
	      "valid explicit render-target pitch mapping changed");
	Check(TileResolveRenderTargetPitch(width, 1984, bytes) == 0,
	      "a non-128-pixel-aligned render-target pitch was accepted");
	Check(TileResolveRenderTargetPitch(width, 16512, bytes) == 0,
	      "a render-target pitch above 16384 pixels was accepted");

	TileSizeAlign render_target {};
	Check(TileGetRenderTargetSize(width, height, explicit_pitch, bytes, render_target),
	      "a valid explicit render-target pitch was rejected");
	Check(render_target.size == expected_size && render_target.align == 65536,
	      "explicit render-target pitch did not enlarge the tiled footprint");

	TileSizeAlign texture {};
	TilePaddedSize padded[1] {};
	TileGetTextureSize(format, width, height, explicit_pitch, 1, tile, &texture, nullptr, padded);
	Check(texture.size == expected_size && texture.align == 65536 &&
	          padded[0].width == explicit_pitch && padded[0].height == 1152,
	      "the texture layout discarded the explicit render-target pitch");

	TileSizeAlign automatic {};
	Check(TileGetRenderTargetSize(width, height, 1920, bytes, automatic) &&
	          automatic.size == 0x870000 && automatic.align == 65536,
	      "the automatic render-target footprint changed");
	TileSizeAlign wide_format {};
	Check(TileGetRenderTargetSize(width, height, explicit_pitch, 8, wide_format) &&
	          wide_format.size == 0x1100000 && wide_format.align == 65536,
	      "64-bpp explicit render-target footprint was incorrect");
}

void CheckExplicitRenderTargetUploadLayout() {
	constexpr uint32_t width          = 1920;
	constexpr uint32_t height         = 1080;
	constexpr uint32_t explicit_pitch = 2048;
	constexpr uint32_t tiled_size     = 0x900000;
	constexpr uint32_t linear_size    = 0x870000;
	constexpr uint32_t format         = Prospero::GpuEnumValue(Prospero::BufferFormat::k32Float);
	constexpr uint32_t tile           = Prospero::GpuEnumValue(Prospero::TileMode::kRenderTarget);

	const auto layout = TextureCalcUploadLayout(format, width, height, 1, 1, explicit_pitch, tile,
	                                            tiled_size, false, false, "GpuTileLayoutTests");
	Check(layout.pitch == explicit_pitch && layout.level_sizes[0].size == linear_size &&
	          layout.level_sizes[0].src_size == tiled_size &&
	          layout.padded_sizes[0].width == explicit_pitch &&
	          layout.padded_sizes[0].height == 1152,
	      "the upload layout discarded the explicit render-target pitch or footprint");

	const auto regions = TextureBuildImageCopies(layout, width, height, 1, 1, false, false);
	Check(regions.size() == 1 && regions[0].imageExtent == vk::Extent3D(width, height, 1) &&
	          regions[0].bufferRowLength == explicit_pitch,
	      "the upload copy did not preserve visible extent and explicit row pitch");
	std::vector<GpuTileInfo> tiles;
	Check(TextureBuildGpuTileInfos(tiled_size, regions, layout, format, 1, 1, tiles) &&
	          tiles.size() == 1 && tiles[0].width == width && tiles[0].height == height &&
	          tiles[0].pitch == explicit_pitch && tiles[0].tiled_width == explicit_pitch &&
	          tiles[0].tiled_height == 1152 && tiles[0].linear_size == linear_size &&
	          tiles[0].tiled_size == tiled_size,
	      "the GPU tiler mapping lost the explicit pitch or backing extent");
}

} // namespace
} // namespace Libs::Graphics

int main() {
	Libs::Graphics::CheckLinearBlockCompressedPitch();
	Libs::Graphics::CheckLinearColorTargetMipLayout();
	Libs::Graphics::CheckExplicitRenderTargetPitch();
	Libs::Graphics::CheckExplicitRenderTargetUploadLayout();
	std::printf("GpuTileLayoutTests: all cases passed\n");
	return 0;
}
