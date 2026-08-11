#ifndef EMULATOR_SRC_GRAPHICS_HOST_GPU_RENDERER_RENDERDRAW_H_
#define EMULATOR_SRC_GRAPHICS_HOST_GPU_RENDERER_RENDERDRAW_H_

#include "graphics/host_gpu/renderer/renderTarget.h"

#include <cmath>
#include <cstdint>
#include <type_traits>

namespace Libs::Graphics {

struct RenderColorInfo;
struct ShaderVertexInputInfo;

#pragma pack(push, 1)

struct PipelineDynamicParameters {
	bool stencil_test_enable = false;

	float viewport_scale[3]  = {};
	float viewport_offset[3] = {};
	int   scissor_ltrb[4]    = {0};

	float    line_width                                       = 1.0f;
	uint32_t color_write_count                                = 1;
	bool     color_write_enable[RENDER_COLOR_ATTACHMENTS_MAX] = {true, true};

	PipelineStencilDynamicState stencil_front;
	PipelineStencilDynamicState stencil_back;
};

#pragma pack(pop)

static_assert(std::is_trivially_copyable_v<PipelineDynamicParameters>);
static_assert(std::is_standard_layout_v<PipelineDynamicParameters>);
static_assert(alignof(PipelineDynamicParameters) == 1);
static_assert(sizeof(PipelineDynamicParameters) ==
              sizeof(bool) + sizeof(float[3]) + sizeof(float[3]) + sizeof(int[4]) + sizeof(float) +
                  sizeof(uint32_t) + sizeof(bool[RENDER_COLOR_ATTACHMENTS_MAX]) +
                  sizeof(PipelineStencilDynamicState) * 2);

struct PipelineVertexWindowOffsetState {
	float x = 0.0f;
	float y = 0.0f;
};

[[nodiscard]] constexpr PipelineVertexWindowOffsetState ResolveVertexWindowOffset(
    float viewport_x, float viewport_y, int window_x, int window_y, bool enable) {
	return {.x = viewport_x + (enable ? static_cast<float>(window_x) : 0.0f),
	        .y = viewport_y + (enable ? static_cast<float>(window_y) : 0.0f)};
}

struct PipelineLineWidthState {
	bool  representable = true;
	float width         = 1.0f;
};

[[nodiscard]] inline PipelineLineWidthState ResolvePipelineLineWidth(
    float width, bool wide_lines_enabled, float range_min, float range_max) {
	if (!std::isfinite(width) || width <= 0.0f) {
		return {.representable = false};
	}
	if (width == 1.0f) {
		return {};
	}
	if (!wide_lines_enabled || !std::isfinite(range_min) || !std::isfinite(range_max) ||
	    range_min > range_max || width < range_min || width > range_max) {
		return {.representable = false};
	}
	return {.representable = true, .width = width};
}

struct PipelinePrimitiveRestartState {
	bool     enable      = false;
	bool     remap       = false;
	uint32_t guest_token = 0;
	uint32_t host_token  = 0;
};

[[nodiscard]] constexpr bool PrimitiveRestartTopologySupported(vk::PrimitiveTopology topology) {
	switch (topology) {
		case vk::PrimitiveTopology::eLineStrip:
		case vk::PrimitiveTopology::eTriangleStrip:
		case vk::PrimitiveTopology::eTriangleFan:
		case vk::PrimitiveTopology::eLineStripWithAdjacency:
		case vk::PrimitiveTopology::eTriangleStripWithAdjacency: return true;
		default: return false;
	}
}

[[nodiscard]] constexpr uint32_t PrimitiveRestartIndexMask(uint32_t bits) {
	return (bits == 32u ? UINT32_MAX : (bits == 0u || bits > 32u ? 0u : (1u << bits) - 1u));
}

[[nodiscard]] constexpr PipelinePrimitiveRestartState ResolvePrimitiveRestart(
    bool enabled, bool match_all_bits, uint32_t reset_index, uint32_t guest_index_bits,
    uint32_t host_index_bits, vk::PrimitiveTopology topology) {
	PipelinePrimitiveRestartState state {};
	const uint32_t guest_mask = PrimitiveRestartIndexMask(guest_index_bits);
	const uint32_t host_mask  = PrimitiveRestartIndexMask(host_index_bits);
	if (guest_mask == 0u || host_mask == 0u) {
		return state;
	}
	state.guest_token = reset_index & guest_mask;
	state.host_token  = host_mask;
	if (!enabled || !PrimitiveRestartTopologySupported(topology) ||
	    (match_all_bits && (reset_index & ~guest_mask) != 0u)) {
		return state;
	}
	state.enable = true;
	state.remap  = state.guest_token != state.host_token;
	return state;
}

[[nodiscard]] constexpr uint32_t TranslatePrimitiveRestartIndex(
    const PipelinePrimitiveRestartState& state, uint32_t value) {
	return (state.enable && value == state.guest_token ? state.host_token : value);
}

[[nodiscard]] int32_t ResolveVertexOffset(uint32_t                     index_offset,
                                          const ShaderVertexInputInfo& vs_input_info);

} // namespace Libs::Graphics

#endif // EMULATOR_SRC_GRAPHICS_HOST_GPU_RENDERER_RENDERDRAW_H_
