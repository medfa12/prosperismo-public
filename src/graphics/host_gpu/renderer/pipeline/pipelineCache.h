#ifndef EMULATOR_SRC_GRAPHICS_HOST_GPU_RENDERER_PIPELINECACHE_H_
#define EMULATOR_SRC_GRAPHICS_HOST_GPU_RENDERER_PIPELINECACHE_H_

#include "common/abi.h"
#include "common/assert.h"
#include "common/common.h"
#include "common/threads.h"
#include "graphics/guest_gpu/gpu_defs.h"
#include "graphics/host_gpu/renderer/renderTarget.h"
#include "graphics/host_gpu/vulkanCommon.h"
#include "graphics/shader/shader.h"

#include <cstddef>
#include <memory>
#include <span>
#include <type_traits>
#include <unordered_map>

namespace Libs::Graphics {

struct GraphicContext;
struct RenderColorInfo;
struct RenderDepthInfo;
class RenderCommandBuffer;
class DescriptorCache;

[[nodiscard]] constexpr bool PipelineAlphaToCoverageEnabled(bool alpha_to_mask_enable,
                                                            bool shader_disable, bool dither_enable,
                                                            uint32_t alpha_to_mask_samples,
                                                            uint32_t samples) {
	return alpha_to_mask_enable && !shader_disable && samples > 1u &&
	       (!dither_enable || alpha_to_mask_samples <= 1u);
}

struct PipelineSampleShadingState {
	bool  representable    = true;
	bool  enable           = false;
	float minimum_fraction = 0.0f;
};

[[nodiscard]] constexpr PipelineSampleShadingState ResolvePipelineSampleShading(
    bool pixel_stage_active, uint32_t samples, bool shader_requires_per_sample,
    bool explicit_per_sample_rate, uint8_t log2_iteration_samples) {
	if (!pixel_stage_active) {
		return {};
	}
	if (samples == 0) {
		return {.representable = false};
	}

	uint32_t iteration_samples = 1;
	if (explicit_per_sample_rate) {
		if (log2_iteration_samples >= 31u) {
			return {.representable = false};
		}
		iteration_samples = 1u << log2_iteration_samples;
		if (iteration_samples > samples) {
			return {.representable = false};
		}
	}

	if (samples <= 1u || (!shader_requires_per_sample && !explicit_per_sample_rate)) {
		return {};
	}
	return {.representable = true,
	        .enable = true,
	        .minimum_fraction = shader_requires_per_sample
	                                ? 1.0f
	                                : static_cast<float>(iteration_samples) /
	                                      static_cast<float>(samples)};
}

enum class PipelineBlendFactorComponent : uint8_t { Color, Alpha };

[[nodiscard]] constexpr bool PipelineForceDestAlphaToOneEnabled(bool render_target_enable,
                                                                bool blend_enable,
                                                                bool blend_bypass) {
	return render_target_enable && blend_enable && !blend_bypass;
}

[[nodiscard]] constexpr uint8_t
ResolvePipelineForceDestAlphaBlendFactor(uint8_t factor, bool force_dest_alpha_to_one,
                                         PipelineBlendFactorComponent component) {
	if (!force_dest_alpha_to_one) {
		return factor;
	}

	using Prospero::BlendFactor;
	const auto value = static_cast<BlendFactor>(factor);
	switch (value) {
		case BlendFactor::kDstAlpha: return static_cast<uint8_t>(BlendFactor::kOne);
		case BlendFactor::kOneMinusDstAlpha: return static_cast<uint8_t>(BlendFactor::kZero);
		case BlendFactor::kSrcAlphaSaturate:
			return static_cast<uint8_t>(component == PipelineBlendFactorComponent::Color
			                                ? BlendFactor::kZero
			                                : BlendFactor::kOne);
		default: return factor;
	}
}

struct PipelineAttachmentBlendState {
	uint8_t color_src_factor = 0;
	uint8_t color_dst_factor = 0;
	uint8_t color_operation  = 0;
	uint8_t alpha_src_factor = 0;
	uint8_t alpha_dst_factor = 0;
	uint8_t alpha_operation  = 0;
};

[[nodiscard]] constexpr PipelineAttachmentBlendState ResolvePipelineAttachmentBlendState(
    uint8_t color_src_factor, uint8_t color_dst_factor, uint8_t color_operation,
    uint8_t alpha_src_factor, uint8_t alpha_dst_factor, uint8_t alpha_operation,
    bool separate_alpha, bool force_dest_alpha_to_one) {
	const uint8_t selected_alpha_src = separate_alpha ? alpha_src_factor : color_src_factor;
	const uint8_t selected_alpha_dst = separate_alpha ? alpha_dst_factor : color_dst_factor;
	return {
	    .color_src_factor = ResolvePipelineForceDestAlphaBlendFactor(
	        color_src_factor, force_dest_alpha_to_one, PipelineBlendFactorComponent::Color),
	    .color_dst_factor = ResolvePipelineForceDestAlphaBlendFactor(
	        color_dst_factor, force_dest_alpha_to_one, PipelineBlendFactorComponent::Color),
	    .color_operation = color_operation,
	    .alpha_src_factor = ResolvePipelineForceDestAlphaBlendFactor(
	        selected_alpha_src, force_dest_alpha_to_one, PipelineBlendFactorComponent::Alpha),
	    .alpha_dst_factor = ResolvePipelineForceDestAlphaBlendFactor(
	        selected_alpha_dst, force_dest_alpha_to_one, PipelineBlendFactorComponent::Alpha),
	    .alpha_operation = separate_alpha ? alpha_operation : color_operation,
	};
}

struct PipelineRasterOpState {
	bool        representable = true;
	bool        enable        = false;
	vk::LogicOp operation     = vk::LogicOp::eCopy;
};

struct PipelineProvokingVertexState {
	bool                        representable = true;
	vk::ProvokingVertexModeEXT mode          = vk::ProvokingVertexModeEXT::eFirstVertex;
};

struct PipelinePolygonModeState {
	bool            representable = true;
	vk::PolygonMode mode          = vk::PolygonMode::eFill;
};

struct PipelineConservativeRasterizationState {
	bool representable = true;
	vk::ConservativeRasterizationModeEXT mode =
	    vk::ConservativeRasterizationModeEXT::eDisabled;
};

[[nodiscard]] constexpr PipelineProvokingVertexState ResolvePipelineProvokingVertex(
    bool guest_last, bool host_last_supported) {
	if (guest_last && !host_last_supported) {
		return {.representable = false};
	}
	return {.representable = true,
	        .mode = guest_last ? vk::ProvokingVertexModeEXT::eLastVertex
	                           : vk::ProvokingVertexModeEXT::eFirstVertex};
}

[[nodiscard]] constexpr PipelinePolygonModeState ResolvePipelinePolygonMode(
    uint8_t enable, uint8_t front_mode, uint8_t back_mode, bool cull_front, bool cull_back,
    bool host_non_solid_supported) {
	if (enable == 0u) {
		return {};
	}
	if (enable != 1u) {
		return {.representable = false};
	}

	const bool front_visible = !cull_front;
	const bool back_visible  = !cull_back;
	if (!front_visible && !back_visible) {
		return {};
	}
	if (front_visible && back_visible && front_mode != back_mode) {
		return {.representable = false};
	}

	const uint8_t selected = front_visible ? front_mode : back_mode;
	vk::PolygonMode mode {};
	switch (selected) {
		case 0u: mode = vk::PolygonMode::ePoint; break;
		case 1u: mode = vk::PolygonMode::eLine; break;
		case 2u: mode = vk::PolygonMode::eFill; break;
		default: return {.representable = false};
	}
	if (mode != vk::PolygonMode::eFill && !host_non_solid_supported) {
		return {.representable = false};
	}
	return {.representable = true, .mode = mode};
}

[[nodiscard]] constexpr bool PipelineUsesPointOrLineRasterization(
    vk::PrimitiveTopology topology, vk::PolygonMode polygon_mode) {
	if (polygon_mode != vk::PolygonMode::eFill) {
		return true;
	}
	switch (topology) {
		case vk::PrimitiveTopology::ePointList:
		case vk::PrimitiveTopology::eLineList:
		case vk::PrimitiveTopology::eLineStrip:
		case vk::PrimitiveTopology::eLineListWithAdjacency:
		case vk::PrimitiveTopology::eLineStripWithAdjacency: return true;
		default: return false;
	}
}

[[nodiscard]] constexpr PipelineConservativeRasterizationState
ResolvePipelineConservativeRasterization(uint32_t mode, bool host_supported,
                                         bool host_underestimation_supported,
                                         bool host_point_line_supported,
                                         bool uses_point_or_line) {
	vk::ConservativeRasterizationModeEXT resolved {};
	switch (mode) {
		case 0u: return {};
		case 0x01u: resolved = vk::ConservativeRasterizationModeEXT::eOverestimate; break;
		case 0x20u:
			if (!host_underestimation_supported) {
				return {.representable = false};
			}
			resolved = vk::ConservativeRasterizationModeEXT::eUnderestimate;
			break;
		default: return {.representable = false};
	}
	if (!host_supported || (uses_point_or_line && !host_point_line_supported)) {
		return {.representable = false};
	}
	return {.representable = true, .mode = resolved};
}

[[nodiscard]] constexpr PipelineRasterOpState ResolvePipelineRasterOp(
    uint8_t rop3, bool logic_op_enabled, bool blend_enabled) {
	vk::LogicOp operation {};
	switch (rop3) {
		case 0x00: operation = vk::LogicOp::eClear; break;
		case 0x05: operation = vk::LogicOp::eNor; break;
		case 0x0a: operation = vk::LogicOp::eAndInverted; break;
		case 0x0f: operation = vk::LogicOp::eCopyInverted; break;
		case 0x44: operation = vk::LogicOp::eAndReverse; break;
		case 0x55: operation = vk::LogicOp::eInvert; break;
		case 0x5a: operation = vk::LogicOp::eXor; break;
		case 0x5f: operation = vk::LogicOp::eNand; break;
		case 0x88: operation = vk::LogicOp::eAnd; break;
		case 0x99: operation = vk::LogicOp::eEquivalent; break;
		case 0xaa: operation = vk::LogicOp::eNoOp; break;
		case 0xaf: operation = vk::LogicOp::eOrInverted; break;
		case 0xcc: return {};
		case 0xdd: operation = vk::LogicOp::eOrReverse; break;
		case 0xee: operation = vk::LogicOp::eOr; break;
		case 0xff: operation = vk::LogicOp::eSet; break;
		default: return {.representable = false};
	}
	if (!logic_op_enabled || blend_enabled) {
		return {.representable = false};
	}
	return {.representable = true, .enable = true, .operation = operation};
}

namespace HW {
class Context;
class Shader;
struct ComputeShaderInfo;
} // namespace HW

#pragma pack(push, 1)

struct PipelineStaticParameters {
	float                      viewport_scale[3]        = {};
	float                      viewport_offset[3]       = {};
	bool                       negative_one_to_one      = false;
	bool                       depth_clip_enable        = true;
	int                        scissor_ltrb[4]          = {0};
	vk::PrimitiveTopology      topology                 = vk::PrimitiveTopology::ePointList;
	bool                       primitive_restart_enable = false;
	bool                       provoking_vertex_last    = false;
	uint32_t                   samples                  = 1;
	bool                       sample_shading_enable    = false;
	float                      sample_shading_minimum   = 0.0f;
	bool                       alpha_to_coverage_enable = false;
	bool                       with_depth               = false;
	bool                       depth_test_enable        = false;
	bool                       depth_write_enable       = false;
	vk::CompareOp              depth_compare_op         = vk::CompareOp::eNever;
	bool                       depth_bounds_test_enable = false;
	float                      depth_min_bounds         = 0.0f;
	float                      depth_max_bounds         = 0.0f;
	bool                       stencil_test_enable      = false;
	bool                       depth_bias_enable        = false;
	float                      depth_bias_constant      = 0.0f;
	float                      depth_bias_clamp         = 0.0f;
	float                      depth_bias_slope         = 0.0f;
	PipelineStencilStaticState stencil_front;
	PipelineStencilStaticState stencil_back;
	uint32_t                   color_count                                        = 1;
	uint32_t                   color_mask[RENDER_COLOR_ATTACHMENTS_MAX]           = {};
	bool                       cull_front                                         = false;
	bool                       cull_back                                          = false;
	bool                       face                                               = false;
	vk::PolygonMode            polygon_mode                                       = vk::PolygonMode::eFill;
	vk::ConservativeRasterizationModeEXT conservative_rasterization_mode =
	    vk::ConservativeRasterizationModeEXT::eDisabled;
	uint8_t                    color_srcblend[RENDER_COLOR_ATTACHMENTS_MAX]       = {};
	uint8_t                    color_comb_fcn[RENDER_COLOR_ATTACHMENTS_MAX]       = {};
	uint8_t                    color_destblend[RENDER_COLOR_ATTACHMENTS_MAX]      = {};
	uint8_t                    alpha_srcblend[RENDER_COLOR_ATTACHMENTS_MAX]       = {};
	uint8_t                    alpha_comb_fcn[RENDER_COLOR_ATTACHMENTS_MAX]       = {};
	uint8_t                    alpha_destblend[RENDER_COLOR_ATTACHMENTS_MAX]      = {};
	bool                       separate_alpha_blend[RENDER_COLOR_ATTACHMENTS_MAX] = {};
	bool                       blend_enable[RENDER_COLOR_ATTACHMENTS_MAX]         = {};
	bool                       blend_bypass[RENDER_COLOR_ATTACHMENTS_MAX]         = {};
	bool                       logic_op_enable                                    = false;
	vk::LogicOp                logic_op                                           = vk::LogicOp::eCopy;
	float                      blend_color_red                                    = 0.0f;
	float                      blend_color_green                                  = 0.0f;
	float                      blend_color_blue                                   = 0.0f;
	float                      blend_color_alpha                                  = 0.0f;

	bool operator==(const PipelineStaticParameters& other) const noexcept;
};

#pragma pack(pop)

static_assert(std::is_trivially_copyable_v<PipelineStaticParameters>);
static_assert(std::is_standard_layout_v<PipelineStaticParameters>);
static_assert(alignof(PipelineStaticParameters) == 1);
static_assert(sizeof(PipelineStaticParameters) ==
              sizeof(float[3]) + sizeof(float[3]) + sizeof(bool) * 2 + sizeof(int[4]) +
	                  sizeof(vk::PrimitiveTopology) + sizeof(uint32_t) + sizeof(bool) * 7 +
	                  sizeof(float) + sizeof(vk::CompareOp) + sizeof(bool) + sizeof(float) * 2 +
	                  sizeof(bool) * 2 +
                  sizeof(float) * 3 +
                  sizeof(PipelineStencilStaticState) * 2 + sizeof(uint32_t) +
                  sizeof(uint32_t[RENDER_COLOR_ATTACHMENTS_MAX]) + sizeof(bool) * 3 +
                  sizeof(vk::PolygonMode) + sizeof(vk::ConservativeRasterizationModeEXT) +
                  sizeof(uint8_t[RENDER_COLOR_ATTACHMENTS_MAX]) * 6 +
                  sizeof(bool[RENDER_COLOR_ATTACHMENTS_MAX]) * 3 + sizeof(bool) +
                  sizeof(vk::LogicOp) + sizeof(float) * 4);

struct PipelineRenderingState {
	std::array<vk::Format, RENDER_COLOR_ATTACHMENTS_MAX> color_formats {};
	vk::Format                                           depth_format   = vk::Format::eUndefined;
	vk::Format                                           stencil_format = vk::Format::eUndefined;
	uint32_t                                             color_count    = 0;

	bool operator==(const PipelineRenderingState&) const = default;
};

class PipelineCache {
public:
	PipelineCache(GraphicContext& graphics, DescriptorCache& descriptor_cache)
	    : m_graphics(graphics), m_descriptor_cache(descriptor_cache) {
		EXIT_NOT_IMPLEMENTED(!Common::Thread::IsMainThread());
	}
	~PipelineCache();
	KYTY_CLASS_NO_COPY(PipelineCache);

	struct Pipeline {
		vk::PipelineLayout pipeline_layout = nullptr;
		vk::Pipeline       pipeline        = nullptr;
	};

	struct GraphicsPipeline: Pipeline {
		ShaderId vs_shader_id;
		ShaderId ps_shader_id;
	};

	struct ComputePipeline: Pipeline {
		ShaderId cs_shader_id;
	};

	GraphicsPipeline&
	CreateGraphicsPipeline(RenderColorInfo* colors, uint32_t color_count, RenderDepthInfo& depth,
	                       ShaderVertexInputInfo& vs_input_info, RenderCommandBuffer& command,
	                       ShaderPixelInputInfo* ps_input_info, vk::PrimitiveTopology topology,
	                       bool primitive_restart_enable, bool ps_active,
	                       std::span<const uint32_t> vs_spirv,
	                       std::span<const uint32_t> ps_spirv);
	ComputePipeline& CreateComputePipeline(ShaderComputeInputInfo&      input_info,
	                                       const HW::ComputeShaderInfo& cs_regs,
	                                       std::span<const uint32_t>    cs_spirv);

private:
	struct GraphicsPipelineKey {
		PipelineRenderingState   rendering;
		ShaderId                 vs_shader_id;
		ShaderId                 ps_shader_id;
		PipelineStaticParameters static_params;

		bool operator==(const GraphicsPipelineKey& other) const {
			return rendering == other.rendering && vs_shader_id == other.vs_shader_id &&
			       ps_shader_id == other.ps_shader_id && static_params == other.static_params;
		}
	};

	struct ComputePipelineKey {
		ShaderId cs_shader_id;

		bool operator==(const ComputePipelineKey& other) const {
			return cs_shader_id == other.cs_shader_id;
		}
	};

	struct PipelineKeyHash {
		static void Mix(std::size_t& hash, std::size_t value) {
			hash ^= value + static_cast<std::size_t>(0x9e3779b97f4a7c15ull) + (hash << 6u) +
			        (hash >> 2u);
		}

		static void MixShaderId(std::size_t& hash, const ShaderId& id) {
			Mix(hash, id.hash0);
			Mix(hash, id.crc32);
			Mix(hash, id.ids.size());
			for (auto value: id.ids) {
				Mix(hash, value);
			}
		}

		static void MixStaticParams(std::size_t& hash, const PipelineStaticParameters& params) {
			const auto* bytes = reinterpret_cast<const uint8_t*>(&params);
			for (std::size_t i = 0; i < sizeof(params); i++) {
				Mix(hash, bytes[i]);
			}
		}

		static void MixRendering(std::size_t& hash, const PipelineRenderingState& rendering) {
			Mix(hash, rendering.color_count);
			for (uint32_t i = 0; i < rendering.color_count; i++) {
				Mix(hash, static_cast<uint32_t>(rendering.color_formats[i]));
			}
			Mix(hash, static_cast<uint32_t>(rendering.depth_format));
			Mix(hash, static_cast<uint32_t>(rendering.stencil_format));
		}
	};

	struct GraphicsPipelineKeyHash {
		std::size_t operator()(const GraphicsPipelineKey& key) const {
			std::size_t hash = 0;
			PipelineKeyHash::MixRendering(hash, key.rendering);
			PipelineKeyHash::MixShaderId(hash, key.vs_shader_id);
			PipelineKeyHash::MixShaderId(hash, key.ps_shader_id);
			PipelineKeyHash::MixStaticParams(hash, key.static_params);
			return hash;
		}
	};

	struct ComputePipelineKeyHash {
		std::size_t operator()(const ComputePipelineKey& key) const {
			std::size_t hash = 0;
			PipelineKeyHash::MixShaderId(hash, key.cs_shader_id);
			return hash;
		}
	};

	GraphicContext&  m_graphics;
	DescriptorCache& m_descriptor_cache;
	std::unordered_map<GraphicsPipelineKey, std::unique_ptr<GraphicsPipeline>,
	                   GraphicsPipelineKeyHash>
	    m_graphics_pipelines;
	std::unordered_map<ComputePipelineKey, std::unique_ptr<ComputePipeline>, ComputePipelineKeyHash>
	              m_compute_pipelines;
	Common::Mutex m_mutex;
};

void LogPipelineTrace(const char* phase, uint32_t vs_hash0, uint32_t vs_crc32, uint32_t ps_hash0,
                      uint32_t ps_crc32);
void CreatePipelineInternal(
    GraphicContext& graphics, DescriptorCache& descriptor_cache,
    PipelineCache::GraphicsPipeline& pipeline, const PipelineRenderingState& rendering,
    const ShaderVertexInputInfo& vs_input_info, std::span<const uint32_t> vs_shader,
    const ShaderPixelInputInfo* ps_input_info, std::span<const uint32_t> ps_shader,
    const PipelineStaticParameters& static_params, uint32_t vs_hash0, uint32_t vs_crc32,
    uint32_t ps_hash0, uint32_t ps_crc32, bool ps_active);
void CreatePipelineInternal(GraphicContext& graphics, DescriptorCache& descriptor_cache,
                            PipelineCache::ComputePipeline& pipeline,
                            const ShaderComputeInputInfo&   input_info,
                            std::span<const uint32_t>       cs_shader);

} // namespace Libs::Graphics

#endif // EMULATOR_SRC_GRAPHICS_HOST_GPU_RENDERER_PIPELINECACHE_H_
