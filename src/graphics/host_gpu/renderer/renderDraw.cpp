#include "graphics/host_gpu/renderer/renderDraw.h"

#include "common/assert.h"
#include "common/common.h"
#include "common/emulatorConfig.h"
#include "common/file.h"
#include "common/logging/log.h"
#include "common/profiler.h"
#include "common/stringUtils.h"
#include "common/threads.h"
#include "graphics/guest_gpu/gpu_defs.h"
#include "graphics/guest_gpu/graphicsRun.h"
#include "graphics/guest_gpu/hardwareContext.h"
#include "graphics/guest_gpu/tile.h"
#include "graphics/host_gpu/graphicContext.h"
#include "graphics/host_gpu/renderer/colorRenderTarget.h"
#include "graphics/host_gpu/renderer/debug.h"
#include "graphics/host_gpu/renderer/depthRenderTarget.h"
#include "graphics/host_gpu/renderer/nativePrimitiveReplay.h"
#include "graphics/host_gpu/renderer/pipeline/descriptorCache.h"
#include "graphics/host_gpu/renderer/pipeline/pipelineCache.h"
#include "graphics/host_gpu/renderer/pipeline/shaderResourceBarrier.h"
#include "graphics/host_gpu/renderer/pipeline/shaderSubgroup.h"
#include "graphics/host_gpu/renderer/render.h"
#include "graphics/host_gpu/renderer/renderContext.h"
#include "graphics/host_gpu/vulkanCommon.h"
#include "graphics/shader/recompiler/ir/ResourceMaterialization.h"
#include "graphics/shader/recompiler/ir/ShaderIR.h"
#include "graphics/shader/shader.h"
#include "kernel/eventQueue.h"
#include "kernel/memory.h"
#include "kernel/pthread.h"
#include "libs/errno.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cmath>
#include <cstring>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <limits>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <span>
#include <unordered_map>
#include <vector>

namespace Libs::Graphics {

int32_t ResolveVertexOffset(uint32_t index_offset, const ShaderVertexInputInfo& vs_input_info) {
	if (index_offset != 0 || !vs_input_info.fetch_embedded) {
		return static_cast<int32_t>(index_offset);
	}

	EXIT_IF(!vs_input_info.stage);
	const auto& program   = *vs_input_info.stage.program;
	const auto& resources = *vs_input_info.stage.resources;
	if (program.info.vertex_offset_sgpr >= static_cast<int32_t>(program.user_data_base)) {
		const auto index =
		    static_cast<uint32_t>(program.info.vertex_offset_sgpr) - program.user_data_base;
		if (index < resources.user_data.size()) {
			return static_cast<int32_t>(resources.user_data[index]);
		}
	}

	return 0;
}

static std::atomic<uint32_t> g_draw_state_log_count       = 0;
static std::atomic<uint32_t> g_draw_input_log_count       = 0;
static std::atomic<uint32_t> g_mrt_state_log_count        = 0;
static std::atomic<uint32_t> g_shader_stage_log_count     = 0;
static std::atomic<uint32_t> g_framebuffer_skip_log_count = 0;

static const char* RenderColorTypeName(RenderColorType type) {
	switch (type) {
		case RenderColorType::NoColorOutput: return "NoColorOutput";
		case RenderColorType::RenderTexture: return "RenderTexture";
		default: return "Unknown";
	}
}

static bool IsDualSourceBlendFactor(uint32_t factor) {
	return ShaderUsesDualSourceBlendFactor(factor);
}

static void LogFramebufferSkip(const char* draw_name, const RenderColorInfo& color,
                               const RenderDepthInfo& depth, const RenderCommandBuffer& buffer,
                               uint32_t index_count, uint32_t flags) {
	const auto& ctx  = buffer.GetRegisters();
	const auto& ucfg = buffer.GetUserConfig();
	if (!graphics_debug_dump_enabled()) {
		return;
	}

	auto log_id = g_framebuffer_skip_log_count.fetch_add(1, std::memory_order_relaxed);
	if (log_id >= 128) {
		return;
	}

	LOGF(
	    "DrawFramebufferSkip[%u]: %s color=%s color_addr=0x%010" PRIx64 " color_size=0x%016" PRIx64
	    " color_image=%s depth_format=%s depth_image=%s depth_vaddr_num=%d target_mask=0x%08" PRIx32
	    " prim=%u index_count=%u flags=0x%08" PRIx32 "\n",
	    log_id, draw_name, RenderColorTypeName(color.type), color.base_addr, color.buffer_size,
	    color.image_id ? "yes" : "no", VulkanToString(depth.format).c_str(),
	    depth.image_id ? "yes" : "no", depth.vaddr_num, ctx.GetRenderTargetMask(),
	    ucfg.GetPrimType(), index_count, flags);
}

static void LogMrtState(const char* draw_name, const RenderCommandBuffer& buffer,
                        const ShaderPixelInputInfo& ps_input_info) {
	const auto& ctx            = buffer.GetRegisters();
	const auto& sh_regs        = ctx.GetShaderRegisters();
	const auto  rt_mask        = ctx.GetRenderTargetMask();
	const auto  cb_shader_mask = sh_regs.m_cbShaderMask;
	const auto& bc0            = ctx.GetBlendControl(0);

	bool interesting = rt_mask != 0x0f || (cb_shader_mask & ~0x0fu) != 0 ||
	                   IsDualSourceBlendFactor(bc0.color_srcblend) ||
	                   IsDualSourceBlendFactor(bc0.color_destblend) ||
	                   (bc0.separate_alpha_blend && (IsDualSourceBlendFactor(bc0.alpha_srcblend) ||
	                                                 IsDualSourceBlendFactor(bc0.alpha_destblend)));

	for (uint32_t i = 1; i < 8; i++) {
		const auto& rt = ctx.GetRenderTarget(i);
		if (rt.base.addr != 0 || ps_input_info.target_output_mode[i] != 0 ||
		    ((rt_mask >> (i * 4u)) & 0x0fu) != 0 || ((cb_shader_mask >> (i * 4u)) & 0x0fu) != 0) {
			interesting = true;
		}
	}

	if (!interesting) {
		return;
	}

	auto log_id = g_mrt_state_log_count.fetch_add(1);
	if (log_id >= 32) {
		return;
	}

	LOGF("MrtState[%u]: %s rt_mask=0x%08" PRIx32 " cb_shader_mask=0x%08" PRIx32
	     " blend0=%s src=%u dst=%u alpha_src=%u alpha_dst=%u sep_alpha=%s\n",
	     log_id, draw_name, rt_mask, cb_shader_mask, bc0.enable ? "true" : "false",
	     bc0.color_srcblend, bc0.color_destblend, bc0.alpha_srcblend, bc0.alpha_destblend,
	     bc0.separate_alpha_blend ? "true" : "false");

	for (uint32_t i = 0; i < 8; i++) {
		const auto& rt  = ctx.GetRenderTarget(i);
		const auto& bc  = ctx.GetBlendControl(i);
		const auto  ctm = (rt_mask >> (i * 4u)) & 0x0fu;
		const auto  csm = (cb_shader_mask >> (i * 4u)) & 0x0fu;

		if (rt.base.addr == 0 && ps_input_info.target_output_mode[i] == 0 && ctm == 0 && csm == 0 &&
		    !bc.enable) {
			continue;
		}

		LOGF("MrtState[%u]: slot=%u addr=0x%010" PRIx64
		     " target_mask=0x%x shader_mask=0x%x out_mode=%u"
		     " fmt=0x%08" PRIx32 " nfmt=0x%08" PRIx32 " order=0x%08" PRIx32
		     " width=%u height=%u tile=%u"
		     " blend=%s src=%u dst=%u alpha_src=%u alpha_dst=%u\n",
		     log_id, i, rt.base.addr, ctm, csm, ps_input_info.target_output_mode[i], rt.info.format,
		     rt.info.channel_type, rt.info.channel_order, rt.attrib2.width + 1,
		     rt.attrib2.height + 1, rt.attrib3.tile_mode, bc.enable ? "true" : "false",
		     bc.color_srcblend, bc.color_destblend, bc.alpha_srcblend, bc.alpha_destblend);
	}
}

static void LogDrawTargetState(const char* draw_name, const RenderColorInfo& color,
                               const RenderDepthInfo& depth, const RenderCommandBuffer& buffer,
                               const ShaderPixelInputInfo& ps_input_info, uint32_t index_count,
                               uint32_t flags) {
	const auto& ctx  = buffer.GetRegisters();
	const auto& ucfg = buffer.GetUserConfig();
	if (color.type == RenderColorType::NoColorOutput) {
		return;
	}

	auto log_id = g_draw_state_log_count.fetch_add(1);
	if (log_id >= 192) {
		return;
	}

	const auto& cc             = ctx.GetColorControl();
	const auto& bc             = ctx.GetBlendControl(color.target_slot);
	const auto& dc             = ctx.GetDepthControl();
	const auto& vp             = ctx.GetScreenViewport();
	const auto& vp0            = vp.viewports[0];
	const auto& ps_resources   = ps_input_info.stage.program->info;
	const auto  sampled_images = std::count_if(
	    ps_resources.images.begin(), ps_resources.images.end(), [](const auto& image) {
		    return image.kind == ShaderRecompiler::IR::ResourceKind::Image ||
		           image.kind == ShaderRecompiler::IR::ResourceKind::ImageUint;
	    });

	vk::Extent2D extent = color.image_id ? color.extent : vk::Extent2D {};
	auto         sc     = calc_final_scissor(vp, ctx.GetScanModeControl(), extent);

	LOGF(
	    "DrawTargetState[%u]: frame=%d %s target=%s addr=0x%010" PRIx64
	    " extent=%ux%u prim=%u index_count=%u flags=0x%08" PRIx32 " color_mask=0x%08" PRIx32
	    " clear=%s clear_rgba=(%.3f,%.3f,%.3f,%.3f) cc_mode=%u cc_op=0x%02x"
	    " blend=%s src=%u dst=%u comb=%u ps_tex=%d sampled=%d storage=%d ps_kill=%s target_mode0=%u"
	    " depth_test=%s depth_write=%s depth_func=%u depth_clear=%s viewport=(%.1f,%.1f %.1fx%.1f) "
	    "scissor=(%d,%d)-(%d,%d)\n",
	    log_id, buffer.GetContext().GetGpu().GetFrameNum(), draw_name,
	    RenderColorTypeName(color.type), color.base_addr, extent.width, extent.height,
	    ucfg.GetPrimType(), index_count, flags, ctx.GetRenderTargetMask(),
	    color.color_clear_enable ? "true" : "false", color.color_clear_value.float32[0],
	    color.color_clear_value.float32[1], color.color_clear_value.float32[2],
	    color.color_clear_value.float32[3], cc.mode, cc.op, bc.enable ? "true" : "false",
	    bc.color_srcblend, bc.color_destblend, bc.color_comb_fcn,
	    static_cast<int>(ps_resources.images.size()), static_cast<int>(sampled_images),
	    static_cast<int>(ps_resources.images.size() - sampled_images),
	    ps_input_info.ps_pixel_kill_enable ? "true" : "false", ps_input_info.target_output_mode[0],
	    dc.z_enable ? "true" : "false", dc.z_write_enable ? "true" : "false", dc.zfunc,
	    depth.depth_clear_enable ? "true" : "false", vp0.xoffset - vp0.xscale,
	    vp0.yoffset - vp0.yscale, vp0.xscale * 2.0f, vp0.yscale * 2.0f, sc.left, sc.top, sc.right,
	    sc.bottom);

	LogMrtState(draw_name, buffer, ps_input_info);
}

static void LogDrawInputState(const RenderCommandBuffer& buffer, const RenderColorInfo& color,
                              const ShaderVertexInputInfo& vs_input_info,
                              uint32_t index_type_and_size, uint32_t index_count,
                              const void* index_addr) {
	auto log_id = g_draw_input_log_count.fetch_add(1);
	if (log_id >= 64) {
		return;
	}

	LOGF("DrawInputState[%u]: frame=%d target=%s addr=0x%010" PRIx64
	     " index_type=%u index_count=%u index_addr=0x%016" PRIx64
	     " vs_resources=%d vs_buffers=%d\n",
	     log_id, buffer.GetContext().GetGpu().GetFrameNum(), RenderColorTypeName(color.type),
	     color.base_addr, index_type_and_size, index_count, reinterpret_cast<uint64_t>(index_addr),
	     vs_input_info.resources_num, vs_input_info.buffers_num);

	for (int bi = 0; bi < vs_input_info.buffers_num; bi++) {
		const auto& b = vs_input_info.buffers[bi];
		LOGF("DrawInputState[%u]: vb[%d] addr=0x%010" PRIx64
		     " stride=%u records=%u fetch_index=%u attr_num=%d\n",
		     log_id, bi, b.addr, b.stride, b.num_records, b.fetch_index, b.attr_num);

		const auto* bytes = reinterpret_cast<const uint8_t*>(b.addr);
		if (bytes != nullptr && b.stride != 0) {
			const uint32_t records = std::min<uint32_t>(b.num_records, 4u);
			for (uint32_t rec = 0; rec < records; rec++) {
				const auto* rec_bytes = bytes + static_cast<uint64_t>(rec) * b.stride;
				const auto  dword_num = std::min<uint32_t>(b.stride / 4u, 12u);
				uint32_t    raw[12]   = {};
				float       flt[12]   = {};
				for (uint32_t i = 0; i < dword_num; i++) {
					std::memcpy(&raw[i], rec_bytes + i * 4u, sizeof(raw[i]));
					std::memcpy(&flt[i], rec_bytes + i * 4u, sizeof(flt[i]));
				}
				LOGF("DrawInputState[%u]: vb[%d].rec[%u] stride=%u dwords=%u raw=%08" PRIx32
				     " %08" PRIx32 " %08" PRIx32 " %08" PRIx32 " %08" PRIx32 " %08" PRIx32
				     " %08" PRIx32 " %08" PRIx32 " %08" PRIx32
				     " f=(%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f)\n",
				     log_id, bi, rec, b.stride, dword_num, raw[0], raw[1], raw[2], raw[3], raw[4],
				     raw[5], raw[6], raw[7], raw[8], flt[0], flt[1], flt[2], flt[3], flt[4], flt[5],
				     flt[6], flt[7], flt[8]);

				for (int ai = 0; ai < b.attr_num; ai++) {
					const auto  res_index = b.attr_indices[ai];
					const auto& r         = vs_input_info.resources[res_index];
					const auto& rd        = vs_input_info.resources_dst[res_index];
					const auto  offset    = b.attr_offsets[ai];
					if (offset + 4u <= b.stride &&
					    r.Format() ==
					        Prospero::GpuEnumValue(Prospero::BufferFormat::k8_8_8_8UNorm)) {
						uint32_t packed = 0;
						std::memcpy(&packed, rec_bytes + offset, sizeof(packed));
						const auto r8 = (packed >> 0u) & 0xffu;
						const auto g8 = (packed >> 8u) & 0xffu;
						const auto b8 = (packed >> 16u) & 0xffu;
						const auto a8 = (packed >> 24u) & 0xffu;
						LOGF("DrawInputState[%u]: vb[%d].rec[%u].attr[%d] dst=v%d fmt=56 "
						     "rgba8=%02" PRIx32 "%02" PRIx32 "%02" PRIx32 "%02" PRIx32
						     " rgba=(%.3f,%.3f,%.3f,%.3f)\n",
						     log_id, bi, rec, ai, rd.register_start, r8, g8, b8, a8,
						     static_cast<double>(r8) / 255.0, static_cast<double>(g8) / 255.0,
						     static_cast<double>(b8) / 255.0, static_cast<double>(a8) / 255.0);
					}
				}
			}
		}

		for (int ai = 0; ai < b.attr_num; ai++) {
			const auto  res_index = b.attr_indices[ai];
			const auto& r         = vs_input_info.resources[res_index];
			const auto& rd        = vs_input_info.resources_dst[res_index];
			LOGF("DrawInputState[%u]: attr[%d] res=%d offset=%u dst=v%d regs=%d fetch_index=%u "
			     "sharp=%08" PRIx32 " %08" PRIx32 " %08" PRIx32 " %08" PRIx32 "\n",
			     log_id, ai, res_index, b.attr_offsets[ai], rd.register_start, rd.registers_num,
			     rd.fetch_index, r.fields[0], r.fields[1], r.fields[2], r.fields[3]);
		}
	}
}

static PipelineDynamicParameters BuildGraphicsDynamicParams(const RenderCommandBuffer& buffer,
                                                            const RenderColorInfo*     colors,
                                                            uint32_t                   color_count,
                                                            const RenderDepthInfo&     depth) {
	EXIT_IF(colors == nullptr);
	const auto& ctx = buffer.GetRegisters();

	PipelineDynamicParameters ret {};
	ret.color_write_count   = color_count;
	ret.stencil_test_enable = depth.stencil_test_enable;

	const auto&  vp = ctx.GetScreenViewport();
	vk::Extent2D framebuffer_extent {};
	if (color_count > 0 && colors[0].image_id) {
		framebuffer_extent = colors[0].extent;
	} else if (depth.image_id) {
		framebuffer_extent = vk::Extent2D{depth.width, depth.height};
	}

	const auto final_scissor = calc_final_scissor(vp, ctx.GetScanModeControl(), framebuffer_extent);

	ret.viewport_scale[0]  = vp.viewports[0].xscale;
	ret.viewport_scale[1]  = vp.viewports[0].yscale;
	ret.viewport_scale[2]  = vp.viewports[0].zscale;
	const auto vertex_window_offset = ResolveVertexWindowOffset(
	    vp.viewports[0].xoffset, vp.viewports[0].yoffset, vp.window_offset_x, vp.window_offset_y,
	    ctx.GetModeControl().vtx_window_offset_enable);
	ret.viewport_offset[0] = vertex_window_offset.x;
	ret.viewport_offset[1] = vertex_window_offset.y;
	ret.viewport_offset[2] = vp.viewports[0].zoffset;
	ret.scissor_ltrb[0]    = final_scissor.left;
	ret.scissor_ltrb[1]    = final_scissor.top;
	ret.scissor_ltrb[2]    = final_scissor.right;
	ret.scissor_ltrb[3]    = final_scissor.bottom;
	ret.line_width         = ctx.GetLineWidth();
	ret.stencil_front      = depth.stencil_dynamic_front;
	ret.stencil_back       = depth.stencil_dynamic_back;

	// CB_COLOR_CONTROL.operation controls special CB operations, not the normal color component
	// write mask. Use CB_TARGET_MASK here so scanout passes with mode=Disable do not go black.
	for (uint32_t i = 0; i < RENDER_COLOR_ATTACHMENTS_MAX; i++) {
		ret.color_write_enable[i] =
		    (i < color_count &&
		     render_target_mask_slot(ctx.GetRenderTargetMask(), colors[i].target_slot) != 0);
	}

	return ret;
}

static void SetDynamicParams(const RenderCommandBuffer& buffer, vk::CommandBuffer vk_buffer,
                             const PipelineDynamicParameters& dynamic_params,
                             const VulkanInstance& graphics) {
	KYTY_PROFILER_FUNCTION();

	vk::Viewport viewport {};
	viewport.x        = dynamic_params.viewport_offset[0] - dynamic_params.viewport_scale[0];
	viewport.y        = dynamic_params.viewport_offset[1] - dynamic_params.viewport_scale[1];
	viewport.width    = dynamic_params.viewport_scale[0] * 2.0f;
	viewport.height   = dynamic_params.viewport_scale[1] * 2.0f;
	viewport.minDepth = dynamic_params.viewport_offset[2];
	viewport.maxDepth = dynamic_params.viewport_scale[2] + dynamic_params.viewport_offset[2];
	vk_buffer.setViewport(0, 1, &viewport);

	vk::Rect2D scissor {};
	scissor.offset = vk::Offset2D{dynamic_params.scissor_ltrb[0], dynamic_params.scissor_ltrb[1]};
	scissor.extent = vk::Extent2D{
	    static_cast<uint32_t>(dynamic_params.scissor_ltrb[2] - dynamic_params.scissor_ltrb[0]),
	    static_cast<uint32_t>(dynamic_params.scissor_ltrb[3] - dynamic_params.scissor_ltrb[1])};
	vk_buffer.setScissor(0, 1, &scissor);

	const auto& limits = graphics.physical_device_properties.limits;
	const auto  line_width =
	    ResolvePipelineLineWidth(dynamic_params.line_width, graphics.wide_lines_enabled,
	                             limits.lineWidthRange[0], limits.lineWidthRange[1]);
	if (!line_width.representable) {
		EXIT("Render: guest line width %f is not representable by the host\n",
		     dynamic_params.line_width);
	}
	vk_buffer.setLineWidth(line_width.width);

	if (dynamic_params.stencil_test_enable) {
		vk_buffer.setStencilCompareMask(vk::StencilFaceFlagBits::eFront,
		                                dynamic_params.stencil_front.compareMask);
		vk_buffer.setStencilCompareMask(vk::StencilFaceFlagBits::eBack,
		                                dynamic_params.stencil_back.compareMask);
		vk_buffer.setStencilWriteMask(vk::StencilFaceFlagBits::eFront,
		                              dynamic_params.stencil_front.writeMask);
		vk_buffer.setStencilWriteMask(vk::StencilFaceFlagBits::eBack,
		                              dynamic_params.stencil_back.writeMask);
		vk_buffer.setStencilReference(vk::StencilFaceFlagBits::eFront,
		                              dynamic_params.stencil_front.reference);
		vk_buffer.setStencilReference(vk::StencilFaceFlagBits::eBack,
		                              dynamic_params.stencil_back.reference);
	}

#if defined(__APPLE__)
	// MoltenVK has no VK_EXT_color_write_enable; the pipeline is created without the
	// eColorWriteEnableEXT dynamic state and relies on the static colorWriteMask instead.
#else
	vk::Bool32 enable[RENDER_COLOR_ATTACHMENTS_MAX] = {};
	for (uint32_t i = 0; i < dynamic_params.color_write_count; i++) {
		enable[i] = (dynamic_params.color_write_enable[i] ? VK_TRUE : VK_FALSE);
	}
	if (dynamic_params.color_write_count != 0) {
		vk_buffer.setColorWriteEnableEXT(dynamic_params.color_write_count, enable);
	}
#endif
}

static bool DrawHasValidVertexShader(const HW::Shader& sh_ctx) {

	const auto& vs = sh_ctx.GetVs();
	return vs.gs_regs.chksum != 0 && ShaderAddressValid(vs.es_regs.data_addr);
}

static bool PixelShaderHasDepthOrCoverageSideEffects(const HW::ShaderRegisters& sh_regs) {
	const auto& db = sh_regs.db_shader_control;
	return sh_regs.shader_z_format != 0 || db.shader_kill_enable || db.shader_z_export_enable ||
	       db.shader_mask_export_enable || db.shader_dual_export_enable ||
	       db.shader_execute_on_noop;
}

static void CaptureUnsupportedNativeGePrograms(
	const HW::VertexShaderInfo& vertex_info, uint32_t stages, const HW::GeControl& ge_cntl,
	const HW::ShaderRegisters& sh_regs, const ClassicGeometryReplayLaunch& launch) {
	const auto* folder_value = std::getenv("PROSPERISMO_DUMP_NATIVE_GE_SHADERS");
	if (folder_value == nullptr || folder_value[0] == '\0') {
		return;
	}

	static std::mutex                              capture_mutex;
	static std::set<std::pair<uint64_t, uint64_t>> captured;
	std::lock_guard lock(capture_mutex);
	const auto pair = std::pair {vertex_info.es_regs.data_addr, vertex_info.gs_regs.data_addr};
	if (captured.contains(pair) || captured.size() >= 32) {
		return;
	}

	std::span<const uint32_t> es_code;
	std::span<const uint32_t> gs_code;
	if (!ShaderGetGuestCode(pair.first, es_code) || !ShaderGetGuestCode(pair.second, gs_code)) {
		LOGF("Native GE capture could not resolve code: es=0x%016" PRIx64
		     " gs=0x%016" PRIx64 "\n",
		     pair.first, pair.second);
		return;
	}

	std::error_code error;
	const auto      folder = std::filesystem::path(folder_value);
	std::filesystem::create_directories(folder, error);
	if (error) {
		LOGF("Native GE capture could not create %s: %s\n", folder_value,
		     error.message().c_str());
		return;
	}

	const auto stem = fmt::format("native-ge-es-{:016x}-gs-{:016x}", pair.first, pair.second);
	auto write_binary = [&](const char* stage, std::span<const uint32_t> words) {
		std::ofstream output(folder / fmt::format("{}.{}.bin", stem, stage),
		                     std::ios::binary | std::ios::trunc);
		output.write(reinterpret_cast<const char*>(words.data()),
		             static_cast<std::streamsize>(words.size_bytes()));
		return output.good();
	};
	std::ofstream metadata(folder / fmt::format("{}.state.txt", stem), std::ios::trunc);
	metadata << fmt::format(
	    "stages=0x{:08x}\nprim_group={}\nvert_group={}\nngg=0x{:08x}\n"
	    "max_out={}\ngs_max_vert={}\ngs_out_prim={}\ngs_instance={}\n"
	    "esgs_ring_itemsize={}\nwave_lanes={}\noutput_vertices={}\noutput_primitives={}\n"
	    "es_words={}\ngs_words={}\n",
	    stages, ge_cntl.primitive_group_size, ge_cntl.vertex_group_size,
	    sh_regs.m_geNggSubgrpCntl, sh_regs.m_geMaxOutputPerSubgroup,
	    sh_regs.m_vgtGsMaxVertOut, sh_regs.m_vgtGsOutPrimType, sh_regs.m_vgtGsInstanceCnt,
	    sh_regs.m_vgtEsgsRingItemsize, launch.wave_lane_count, launch.output_vertex_slots,
	    launch.output_primitive_slots, es_code.size(), gs_code.size());
	if (!write_binary("es", es_code) || !write_binary("gs", gs_code) || !metadata.good()) {
		LOGF("Native GE capture failed while writing %s\n", stem.c_str());
		return;
	}

	captured.insert(pair);
	LOGF("Captured unsupported native GE programs: %s (%zu/%zu words)\n", stem.c_str(),
	     es_code.size(), gs_code.size());
}

static bool ShouldSkipGeShader(const RenderCommandBuffer& buffer) {
	const auto& ctx         = buffer.GetRegisters();
	const auto& ucfg        = buffer.GetUserConfig();
	const auto& sh_ctx      = buffer.GetShaders();
	const auto& sh_regs     = ctx.GetShaderRegisters();
	const auto& ge_cntl     = ucfg.GetGeControl();
	const auto& ge_user     = ucfg.GetGeUserVgprEn();
	const auto& vertex_info = sh_ctx.GetVs();
	const auto  stages      = ctx.GetShaderStages();

	const auto is_known_gs_out_prim_type = [](uint32_t value) {
		switch (static_cast<Prospero::GsOutputPrimitiveType>(value)) {
			case Prospero::GsOutputPrimitiveType::kPoints:
			case Prospero::GsOutputPrimitiveType::kLines:
			case Prospero::GsOutputPrimitiveType::kTriangles:
			case Prospero::GsOutputPrimitiveType::k2dRectangle:
			case Prospero::GsOutputPrimitiveType::kRectList: return true;
		}

		return false;
	};

	const bool ps5_ngg_vertex_path = stages == 0x02002000 && vertex_info.es_regs.data_addr != 0 &&
	                                 vertex_info.gs_regs.chksum != 0 &&
	                                 sh_regs.m_vgtGsMaxVertOut == 0x00000000 &&
	                                 is_known_gs_out_prim_type(sh_regs.m_vgtGsOutPrimType);

	const bool unsupported_stage_mask = (stages != 0 && stages != 0x02002000);
	const bool unsupported_gs_stage = (vertex_info.es_regs.data_addr != 0 &&
	                                   vertex_info.gs_regs.data_addr != 0 && !ps5_ngg_vertex_path);
	const bool ge_group_size =
	    ge_cntl.primitive_group_size > 0x0040 || ge_cntl.vertex_group_size > 0x0040;
	const bool ge_shader_regs =
	    (sh_regs.m_geNggSubgrpCntl != 0x00000000 && sh_regs.m_geNggSubgrpCntl != 0x00000001) ||
	    sh_regs.m_vgtGsMaxVertOut != 0x00000000 ||
	    !is_known_gs_out_prim_type(sh_regs.m_vgtGsOutPrimType) ||
	    sh_regs.m_geMaxOutputPerSubgroup > 0x00000040;
	ClassicGeometryReplayLaunch classic_launch {};
	const bool classic_geometry_contract = TryCreateClassicGeometryReplayLaunch(
	    {.shader_stages             = stages,
	     .primitive_group_size      = ge_cntl.primitive_group_size,
	     .vertex_group_size         = ge_cntl.vertex_group_size,
	     .primitive_amplification   = sh_regs.m_geNggSubgrpCntl & 0x1ffu,
	     .max_output_per_subgroup   = sh_regs.m_geMaxOutputPerSubgroup,
	     .gs_max_vertices_per_input = sh_regs.m_vgtGsMaxVertOut,
	     .gs_output_primitive       = sh_regs.m_vgtGsOutPrimType,
	     .gs_instance_count         = sh_regs.m_vgtGsInstanceCnt,
	     .esgs_ring_item_size       = sh_regs.m_vgtEsgsRingItemsize,
	     .ge_user_vgpr_enable       = static_cast<uint32_t>(ge_user.vgpr1) |
	                                  (static_cast<uint32_t>(ge_user.vgpr2) << 1u) |
	                                  (static_cast<uint32_t>(ge_user.vgpr3) << 2u)},
	    classic_launch);
	if (classic_geometry_contract) {
		CaptureUnsupportedNativeGePrograms(vertex_info, stages, ge_cntl, sh_regs, classic_launch);
	}

	if (unsupported_stage_mask || unsupported_gs_stage || ge_group_size || ge_shader_regs) {
		const auto log_id = g_shader_stage_log_count.fetch_add(1);
		if (log_id < 32) {
			LOGF("Skipping unsupported GE shader draw: stages=0x%08" PRIx32
			     " prim_group=0x%04" PRIx16 " vert_group=0x%04" PRIx16 " ngg=0x%08" PRIx32
			     " max_out=0x%08" PRIx32 " gs_max_vert=0x%08" PRIx32 " gs_out_prim=0x%08" PRIx32
			     " es=0x%016" PRIx64 " gs=0x%016" PRIx64
			     " classic_gs=%s output_vertices=%u output_primitives=%u\n",
			     stages, ge_cntl.primitive_group_size, ge_cntl.vertex_group_size,
			     sh_regs.m_geNggSubgrpCntl, sh_regs.m_geMaxOutputPerSubgroup,
			     sh_regs.m_vgtGsMaxVertOut, sh_regs.m_vgtGsOutPrimType,
			     vertex_info.es_regs.data_addr, vertex_info.gs_regs.data_addr,
			     classic_geometry_contract ? "true" : "false",
			     classic_launch.output_vertex_slots, classic_launch.output_primitive_slots);
		}
		return true;
	}

	return false;
}

struct DrawRenderState {
	RenderDepthInfo           depth_info;
	RenderColorInfo           color_info[RENDER_COLOR_ATTACHMENTS_MAX] = {};
	uint32_t                  color_count                              = 0;
	bool                      ps_active                                = true;
	RenderState               rendering;
	ShaderVertexInputInfo     vs_input_info;
	ShaderPixelInputInfo      ps_input_info;
	std::span<const uint32_t> vs_shader;
	std::span<const uint32_t> ps_shader;
};

struct DrawCallInfo {
	const char*          name           = nullptr;
	CommandBufferDebugOp debug_op       = CommandBufferDebugOp::DrawIndex;
	uint32_t             index_count    = 0;
	uint32_t             flags          = 0;
	uint32_t             instance_count = 0;
	uint32_t             first_instance = 0;
};

RenderState RenderExecutor::AcquireRenderTargets(CommandBuffer& buffer, RenderColorInfo* colors,
                                                 uint32_t color_count, RenderDepthInfo& depth) {
	EXIT_IF(colors == nullptr || color_count > RENDER_COLOR_ATTACHMENTS_MAX);
	auto&       cache = m_context.GetTextureCache();
	RenderState state {};
	state.width                 = std::numeric_limits<uint32_t>::max();
	state.height                = std::numeric_limits<uint32_t>::max();
	state.num_layers            = std::numeric_limits<uint32_t>::max();
	state.num_color_attachments = color_count;
	uint32_t attachment_samples = 0;
	for (uint32_t i = 0; i < color_count; i++) {
		auto& target = colors[i];
		EXIT_IF(!target.image_id);
		const auto old_image = cache.ResolveOwner(target.image_id);
		if (old_image == nullptr || (!old_image->registered && !old_image->info.data.Empty()) ||
		    old_image->binding.needs_rebind) {
			if (old_image != nullptr) {
				old_image->binding = {};
			}
			target.image_id = cache.FindImage(target.desc);
			BindRenderTarget(target.image_id);
		}
		target.image_view = cache.FindRenderTarget(target.image_id, target.desc);
		auto& image       = cache.GetImage(target.image_id);
		EXIT_IF(image.backing.samples != target.samples || target.image_view == nullptr);
		if (attachment_samples == 0) {
			attachment_samples = target.samples;
		} else if (attachment_samples != target.samples) {
			EXIT("mixed color attachment sample counts are unsupported: %u and %u\n",
			     attachment_samples, target.samples);
		}
		const auto& view   = target.desc.view_info;
		const auto  layout = image.binding.is_bound ? vk::ImageLayout::eGeneral
		                                            : vk::ImageLayout::eColorAttachmentOptimal;
		image.Transit(layout,
		              vk::AccessFlagBits2::eColorAttachmentRead |
		                  vk::AccessFlagBits2::eColorAttachmentWrite,
		              ImageSubresourceRange {view.base_level, view.level_count, view.base_layer,
		                                     view.layer_count},
		              buffer.Handle());
		state.width             = std::min(state.width, target.extent.width);
		state.height            = std::min(state.height, target.extent.height);
		state.num_layers        = std::min(state.num_layers, view.layer_count);
		auto& attachment        = state.color_attachments[i];
		attachment.image_view   = target.image_view;
		attachment.image_layout = layout;
		attachment.clear_value  = target.color_clear_value.uint32;
		attachment.is_clear     = target.color_clear_enable;
	}
	if (depth.image_id) {
		const auto owner = cache.ResolveOwner(depth.image_id);
		if (owner == nullptr || !owner->registered || owner->binding.needs_rebind) {
			EXIT("depth target changed after render-state discovery\n");
		}
		depth.image_view = cache.FindDepthTarget(depth.image_id, depth.desc);
		if (depth.htile && depth.depth_clear_enable && !cache.ClearMeta(depth.htile_buffer_vaddr)) {
			EXIT("failed to acquire HTile metadata for a depth clear\n");
		}
		depth.depth_meta_clear_enable =
		    depth.htile &&
		    cache.IsMetaCleared(depth.htile_buffer_vaddr, depth.desc.view_info.base_layer);
		depth.depth_load_clear_enable = depth.depth_clear_enable || depth.depth_meta_clear_enable;
		if (depth.depth_meta_clear_enable &&
		    !cache.TouchMeta(depth.htile_buffer_vaddr, depth.desc.view_info.base_layer, false)) {
			EXIT("failed to consume HTile clear state\n");
		}
		auto& image = cache.GetImage(depth.image_id);
		EXIT_IF(depth.image_view == nullptr || image.backing.samples != depth.samples);
		if (attachment_samples == 0) {
			attachment_samples = depth.samples;
		} else if (attachment_samples != depth.samples) {
			EXIT("mixed color/depth sample counts are unsupported: %u and %u\n", attachment_samples,
			     depth.samples);
		}
		const auto layout = depth_attachment_layout(depth);
		const auto writes = depth.AttachmentWriteAspects();
		auto       access = vk::AccessFlags2 {vk::AccessFlagBits2::eDepthStencilAttachmentRead};
		if (writes) {
			access |= vk::AccessFlagBits2::eDepthStencilAttachmentWrite;
		}
		const auto& view = depth.desc.view_info;
		image.Transit(layout, access,
		              ImageSubresourceRange {view.base_level, view.level_count, view.base_layer,
		                                     view.layer_count},
		              buffer.Handle());
		state.width               = std::min(state.width, depth.width);
		state.height              = std::min(state.height, depth.height);
		state.num_layers          = std::min(state.num_layers, view.layer_count);
		const auto aspects        = ImageViewOps::DepthAspectMask(depth.format);
		auto&      attachment     = state.depth_stencil_attachment;
		attachment.image_view     = depth.image_view;
		attachment.image_layout   = layout;
		attachment.clear_value[0] = std::bit_cast<uint32_t>(depth.depth_clear_value);
		attachment.clear_value[1] = depth.stencil_clear_value;
		attachment.has_depth      = static_cast<bool>(aspects & vk::ImageAspectFlagBits::eDepth);
		attachment.depth_clear    = depth.depth_load_clear_enable;
		attachment.has_stencil    = static_cast<bool>(aspects & vk::ImageAspectFlagBits::eStencil);
		attachment.stencil_clear  = depth.stencil_clear_enable;
	}
	if (attachment_samples == 0 ||
	    vulkan_sample_count(attachment_samples) == vk::SampleCountFlagBits {}) {
		EXIT("render state has no valid attachments\n");
	}
	if (state.num_layers == std::numeric_limits<uint32_t>::max()) {
		state.num_layers = 1;
	}
	EXIT_IF(state.width == 0 || state.height == 0 || state.num_layers == 0 ||
	        state.width == std::numeric_limits<uint32_t>::max() ||
	        state.height == std::numeric_limits<uint32_t>::max());
	return state;
}

static bool DrawHasActivePixelShader(const RenderCommandBuffer& buffer,
                                     const DrawRenderState& state, const DrawCallInfo& draw) {
	EXIT_IF(draw.name == nullptr);
	const auto& ctx    = buffer.GetRegisters();
	const auto& sh_ctx = buffer.GetShaders();

	const bool with_depth = (state.depth_info.format != vk::Format::eUndefined &&
	                         static_cast<bool>(state.depth_info.image_id));
	if (state.color_count != 0 || !with_depth) {
		return true;
	}

	const auto& sh_regs = ctx.GetShaderRegisters();
	const auto& ps      = sh_ctx.GetPs();
	return ShaderAddressValid(ps.ps_regs.data_addr) &&
	       PixelShaderHasDepthOrCoverageSideEffects(sh_regs);
}

bool RenderExecutor::ExecuteColorMetadataOperation(uint64_t submit_id,
                                                   RenderCommandBuffer& buffer,
                                                   uint32_t render_target_slice_offset) {
	const auto mode      = buffer.GetRegisters().GetColorControl().mode;
	const auto operation = DecodeColorMetadataOperation(mode);
	if (!operation.recognized) {
		return false;
	}

	// Sony metadata operations target every bound CB, independently of
	// CB_TARGET_MASK. The shader is only a vehicle for the color block.
	auto& cache = m_context.GetTextureCache();
	for (uint32_t slot = 0; slot < RENDER_COLOR_ATTACHMENTS_MAX; slot++) {
		if (buffer.GetRegisters().GetRenderTarget(slot).base.addr == 0) {
			continue;
		}
		RenderColorInfo target {};
		ResolveRenderColorTarget(submit_id, buffer, target, render_target_slice_offset, slot, true,
		                         true);
		if (target.image_id) {
			(void)cache.ApplyColorMetadataOperation(
			    target.image_id, mode,
			    {target.desc.view_info.base_level, target.desc.view_info.level_count,
			     target.desc.view_info.base_layer, target.desc.view_info.layer_count});
		}
	}
	return true;
}

struct DrawEmitInfo {
	bool     indexed           = false;
	bool     draw_prim7_as_ngg = false;
	uint32_t draw_vertex_count = 0;
	int32_t  vertex_offset     = 0;
	uint32_t first_vertex      = 0;
};

struct DrawIndexBufferSource {
	bool          enabled                  = false;
	bool          primitive_restart_enable = false;
	uint64_t      address                  = 0;
	const void*   host_data                = nullptr;
	uint64_t      size                     = 0;
	vk::IndexType type                     = vk::IndexType::eUint16;
};

struct PreparedIndexBuffer {
	std::shared_ptr<void> owner;
	vk::Buffer            buffer   = nullptr;
	uint64_t              address  = 0;
	uint64_t              size     = 0;
	vk::DeviceSize        offset                   = 0;
	vk::IndexType         type                     = vk::IndexType::eUint16;
	bool                  streamed                 = false;
	bool                  primitive_restart_enable = false;
};

static uint64_t VertexBufferDescriptorSize(const ShaderVertexInputBuffer& buffer) {
	return (buffer.stride != 0 ? static_cast<uint64_t>(buffer.stride) * buffer.num_records
	                           : buffer.num_records);
}

struct VertexBufferRange {
	uint64_t      base_address  = 0;
	uint64_t      requested_end = 0;
	uint64_t      acquired_end  = 0;
	BufferBinding binding;

	[[nodiscard]] uint64_t RequestedSize() const { return requested_end - base_address; }
};

static std::vector<BufferBinding> AcquireVertexBuffers(RenderCommandBuffer&         buffer,
                                                       const ShaderVertexInputInfo& vs_input_info) {
	// Collect the non-empty guest vertex ranges.
	std::vector<VertexBufferRange> ranges;
	ranges.reserve(vs_input_info.buffers_num);
	for (int i = 0; i < vs_input_info.buffers_num; i++) {
		const auto& vertex = vs_input_info.buffers[i];
		const auto  size   = VertexBufferDescriptorSize(vertex);
		if (size == 0) {
			continue;
		}
		if (vertex.addr == 0 || size > UINT64_MAX - vertex.addr) {
			EXIT("invalid vertex buffer range: addr=0x%016" PRIx64 " size=0x%016" PRIx64 "\n",
			     vertex.addr, size);
		}
		ranges.push_back({vertex.addr, vertex.addr + size});
	}

	std::ranges::sort(ranges, [](const VertexBufferRange& left, const VertexBufferRange& right) {
		return left.base_address < right.base_address;
	});

	// Merge overlapping or touching ranges before acquiring host buffers.
	std::vector<VertexBufferRange> merged_ranges;
	merged_ranges.reserve(ranges.size());
	for (const auto& range: ranges) {
		if (!merged_ranges.empty() && merged_ranges.back().requested_end >= range.base_address) {
			merged_ranges.back().requested_end =
			    std::max(merged_ranges.back().requested_end, range.requested_end);
			continue;
		}
		merged_ranges.push_back(range);
	}

	auto& cache = buffer.GetContext().GetBufferCache();
	for (auto& range: merged_ranges) {
		// PPSA20298
		const auto size =
		    Libs::LibKernel::Memory::ClampRangeSize(range.base_address, range.RequestedSize());
		range.acquired_end = range.base_address + size;
		range.binding      = cache.ObtainBuffer(buffer, range.base_address, size);
	}

	// Rebuild slot bindings, offsetting non-empty slots into their acquired merged range.
	std::vector<BufferBinding> bindings;
	bindings.reserve(vs_input_info.buffers_num);
	for (int i = 0; i < vs_input_info.buffers_num; i++) {
		const auto& vertex = vs_input_info.buffers[i];
		const auto  size   = VertexBufferDescriptorSize(vertex);
		if (size == 0) {
			auto owner = cache.ObtainNullBuffer();
			bindings.push_back({owner, owner->Handle(), 0});
			continue;
		}

		const auto range = std::ranges::find_if(merged_ranges, [&](const VertexBufferRange& value) {
			return vertex.addr >= value.base_address && vertex.addr < value.acquired_end;
		});
		if (range == merged_ranges.end()) {
			EXIT("vertex buffer address is outside the acquired range: addr=0x%016" PRIx64 "\n",
			     vertex.addr);
		}

		auto binding = range->binding;
		binding.offset += vertex.addr - range->base_address;
		bindings.push_back(std::move(binding));
	}
	return bindings;
}

static void SetDrawDebugPhase(RenderCommandBuffer& buffer, uint64_t submit_id,
                              const DrawCallInfo& draw, uint32_t phase) {
	EXIT_IF(draw.name == nullptr);

	buffer.SetDebugInfo(static_cast<uint32_t>(draw.debug_op), submit_id, phase, draw.index_count,
	                    draw.flags, draw.instance_count, draw.first_instance);
}

static bool GetDrawTopology(const HW::UserConfig& ucfg, bool auto_draw, bool use_ngg_rectlist_draw,
                            vk::PrimitiveTopology& topology) {

	topology = vk::PrimitiveTopology::ePointList;

	switch (static_cast<Prospero::PrimitiveType>(ucfg.GetPrimType())) {
		case Prospero::PrimitiveType::kNone: return false;
		case Prospero::PrimitiveType::kPointList:
			topology = vk::PrimitiveTopology::ePointList;
			break;
		case Prospero::PrimitiveType::kLineList: topology = vk::PrimitiveTopology::eLineList; break;
		case Prospero::PrimitiveType::kLineStrip:
			topology = vk::PrimitiveTopology::eLineStrip;
			break;
		case Prospero::PrimitiveType::kTriList:
			topology = vk::PrimitiveTopology::eTriangleList;
			break;
		case Prospero::PrimitiveType::kTriFan:
			topology = vk::PrimitiveTopology::eTriangleFan;
			break;
		case Prospero::PrimitiveType::kTriStrip:
			topology = vk::PrimitiveTopology::eTriangleStrip;
			break;
		case Prospero::PrimitiveType::kRectList:
			topology = (auto_draw && use_ngg_rectlist_draw ? vk::PrimitiveTopology::eTriangleStrip
			                                               : vk::PrimitiveTopology::eTriangleList);
			break;
		case Prospero::PrimitiveType::kRectListLegacy:
			if (!auto_draw) {
				EXIT("unknown primitive type: %u\n", ucfg.GetPrimType());
			}
			topology = vk::PrimitiveTopology::eTriangleStrip;
			break;
		case Prospero::PrimitiveType::kQuadListLegacy:
			topology = vk::PrimitiveTopology::eTriangleFan;
			break;
		default: EXIT("unknown primitive type: %u\n", ucfg.GetPrimType());
	}

	return true;
}

bool RenderExecutor::PrepareDrawRenderState(uint64_t submit_id, RenderCommandBuffer& buffer,
                                            const DrawCallInfo& draw,
                                            uint32_t            render_target_slice_offset,
                                            bool log_setup_phases, DrawRenderState& state) {
	EXIT_IF(draw.name == nullptr);
	auto& ctx = buffer.GetRegisters();

	if (ResolveColorTargets(submit_id, buffer, render_target_slice_offset)) {
		return false;
	}
	if (log_setup_phases) {
		LogDrawPhase(draw.name, "ResolveRenderColorTarget");
	}
	const bool dual_source =
	    ctx.GetShaderRegisters().db_shader_control.shader_dual_export_enable;
	for (uint32_t slot = 0; slot < RENDER_COLOR_ATTACHMENTS_MAX; slot++) {
		if (!ShaderDualSourceUsesPhysicalColorTarget(dual_source, slot)) {
			continue;
		}
		if (slot == 0 || (render_target_mask_slot(ctx.GetRenderTargetMask(), slot) != 0 &&
		                  ctx.GetRenderTarget(slot).base.addr != 0)) {
			ResolveRenderColorTarget(submit_id, buffer, state.color_info[state.color_count],
			                         render_target_slice_offset, slot);
			if (state.color_info[state.color_count].image_id) {
				state.color_count++;
			}
		}
	}
	if (log_setup_phases) {
		LogDrawPhase(draw.name, "ResolveRenderDepthTarget");
	}
	ResolveRenderDepthTarget(submit_id, buffer, state.depth_info);

	// KYTY_M42_DROP_SMALL_DEPTH (ported from Stepz97/ps5emu 4ee7a43, "wall 42"): Vulkan requires
	// renderArea to fit inside EVERY attachment, so AcquireRenderTargets clamps it to the
	// smallest one via std::min. GCN/RDNA has no such rule — colour writes are bounded by the
	// viewport and scissor, and a depth buffer smaller than the colour target is legal so long as
	// depth is not sampled. Astro Bot's menu pass binds a 960x540 depth buffer alongside the
	// 1920x1080 menu target, so the clamp shrinks renderArea to a quarter and clips the pass to
	// the top-left corner — the black quadrant. When depth provably cannot affect the result (no
	// test, no write, no bounds test, no live stencil, no clear) the attachment carries no
	// information, and dropping it restores full coverage. Clearing image_id covers both
	// consumers of depth_info: AcquireRenderTargets stops shrinking renderArea, and the pipeline
	// key stops declaring a depth format, so the two stay consistent. HTile-backed depth is
	// excluded because depth_meta_clear_enable is only resolved later, inside
	// AcquireRenderTargets, so a pending fast-clear cannot be ruled out here.
	if (const char* drop = std::getenv("KYTY_M42_DROP_SMALL_DEPTH");
	    drop != nullptr && *drop == '1' && state.depth_info.image_id && state.color_count > 0) {
		uint32_t max_w = 0;
		uint32_t max_h = 0;
		for (uint32_t i = 0; i < state.color_count; i++) {
			max_w = std::max(max_w, state.color_info[i].extent.width);
			max_h = std::max(max_h, state.color_info[i].extent.height);
		}
		const bool undersized = state.depth_info.width < max_w || state.depth_info.height < max_h;
		// A stencil test that is enabled but compares eAlways and keeps every op cannot reject a
		// fragment, so the attachment carries no information even with the enable bit set.
		const bool stencil_inert =
		    !state.depth_info.stencil_test_enable ||
		    (!stencil_face_accesses_attachment(state.depth_info.stencil_static_front,
		                                       state.depth_info.stencil_dynamic_front) &&
		     !stencil_face_accesses_attachment(state.depth_info.stencil_static_back,
		                                       state.depth_info.stencil_dynamic_back));
		const bool inert =
		    !state.depth_info.depth_test_enable && !state.depth_info.depth_write_enable &&
		    !state.depth_info.depth_bounds_test_enable && stencil_inert &&
		    !state.depth_info.depth_clear_enable && !state.depth_info.depth_load_clear_enable &&
		    !state.depth_info.stencil_clear_enable &&
		    !state.depth_info.AttachmentWriteAspects() && !state.depth_info.htile;
		if (undersized && inert) {
			static std::atomic<uint32_t> dropped {0};
			const auto                   n = dropped.fetch_add(1);
			if (n < 8) {
				LOGF("m42-drop-depth: dropping inert %ux%u depth beside %ux%u colour\n",
				     state.depth_info.width, state.depth_info.height, max_w, max_h);
			}
			state.depth_info = {};
		}
	}

	const bool with_depth = (state.depth_info.format != vk::Format::eUndefined &&
	                         static_cast<bool>(state.depth_info.image_id));
	if (state.color_count == 0 && !with_depth) {
		LogFramebufferSkip(draw.name, state.color_info[0], state.depth_info, buffer,
		                   draw.index_count, draw.flags);
		return false;
	}
	state.ps_active = DrawHasActivePixelShader(buffer, state, draw);

	return true;
}

static void RefreshShaders(RenderCommandBuffer& buffer, const DrawCallInfo& draw, bool log_phases,
                           DrawRenderState& state) {
	EXIT_IF(draw.name == nullptr);
	auto& ctx    = buffer.GetRegisters();
	auto& sh_ctx = buffer.GetShaders();

	const auto& vertex_shader_info = sh_ctx.GetVs();
	const auto& pixel_shader_info  = sh_ctx.GetPs();
	const auto& shader_regs        = ctx.GetShaderRegisters();

	state.vs_shader     = {};
	state.ps_shader     = {};
	state.ps_input_info = {};
	std::array<Prospero::ColorComponentMapping, RENDER_COLOR_ATTACHMENTS_MAX>
	    target_export_mapping {};
	for (uint32_t i = 0; i < state.color_count; i++) {
		target_export_mapping[state.color_info[i].target_slot] = state.color_info[i].export_mapping;
	}
	const auto lane_mask_mode = SelectGraphicsLaneMaskMode(64u);

	if (log_phases) {
		LogDrawPhase(draw.name, "ShaderCompileInfoVS");
	}
	if (!ShaderCompileInfoVS(vertex_shader_info, shader_regs, lane_mask_mode, state.vs_input_info,
	                         state.vs_shader)) {
		EXIT("ShaderCompileInfoVS failed for draw %s\n", draw.name);
	}

	if (!state.ps_active) {
		return;
	}
	if (log_phases) {
		LogDrawPhase(draw.name, "ShaderCompileInfoPS");
	}
	if (!ShaderCompileInfoPS(pixel_shader_info, shader_regs, lane_mask_mode, state.vs_input_info,
	                         target_export_mapping, state.ps_input_info, state.ps_shader,
	                         ctx.GetModeControl().persp_corr_dis, ctx.GetAaSampleMask(),
	                         ctx.GetPsShaderSampleExclusionMask(),
	                         ctx.GetAlphaToMaskControl().raw,
	                         ctx.GetEqaaControl().alpha_to_mask_num_samples,
	                         ctx.GetRenderTarget(0).info.round_mode)) {
		EXIT("ShaderCompileInfoPS failed for draw %s\n", draw.name);
	}
}

static std::vector<BufferBinding> PrepareVertexBuffers(uint64_t                     submit_id,
                                                       RenderCommandBuffer&         buffer,
                                                       const DrawCallInfo&          draw,
                                                       const ShaderVertexInputInfo& vs_input_info) {
	EXIT_IF(draw.name == nullptr);
	(void)submit_id;

	LogDrawPhase(draw.name, "PrepareVertexBuffers");
	return AcquireVertexBuffers(buffer, vs_input_info);
}

static void RebindVertexBuffers(RenderCommandBuffer&         buffer,
                                const ShaderVertexInputInfo& vs_input_info,
                                std::vector<BufferBinding>&  bindings) {
	bindings = AcquireVertexBuffers(buffer, vs_input_info);
}

static PreparedIndexBuffer PrepareIndexBuffer(RenderCommandBuffer&         buffer,
                                              const DrawIndexBufferSource& source) {
	PreparedIndexBuffer prepared;
	if (!source.enabled) {
		return prepared;
	}
	EXIT_IF(source.size == 0);
	prepared.address                  = source.address;
	prepared.size                     = source.size;
	prepared.type                     = source.type;
	prepared.primitive_restart_enable = source.primitive_restart_enable;
	if (source.host_data != nullptr) {
		auto binding =
		    buffer.GetContext().GetBufferCache().UploadTransient(source.host_data, source.size, 16);
		prepared.owner    = std::move(binding.owner);
		prepared.buffer   = binding.buffer;
		prepared.offset   = binding.offset;
		prepared.streamed = true;
	} else {
		auto binding =
		    buffer.GetContext().GetBufferCache().ObtainBuffer(buffer, source.address, source.size);
		prepared.owner  = std::move(binding.owner);
		prepared.buffer = binding.buffer;
		prepared.offset = binding.offset;
	}
	return prepared;
}

static void RebindIndexBuffer(RenderCommandBuffer& buffer, PreparedIndexBuffer& prepared) {
	if (prepared.size == 0 || prepared.streamed) {
		return;
	}
	auto binding =
	    buffer.GetContext().GetBufferCache().ObtainBuffer(buffer, prepared.address, prepared.size);
	prepared.owner  = std::move(binding.owner);
	prepared.buffer = binding.buffer;
	prepared.offset = binding.offset;
}

static void CommitVertexBuffers(RenderCommandBuffer& buffer, vk::CommandBuffer vk_buffer,
                                std::vector<BufferBinding>& bindings) {
	for (uint32_t slot = 0; slot < bindings.size(); slot++) {
		auto& binding = bindings[slot];
		if (binding.owner != nullptr) {
			buffer.RetainResourceUntilFence(binding.owner);
		}
		EXIT_IF(binding.buffer == nullptr);
		vk_buffer.bindVertexBuffers(slot, 1, &binding.buffer, &binding.offset);
	}
}

static void CommitIndexBuffer(RenderCommandBuffer& buffer, vk::CommandBuffer vk_buffer,
                              const PreparedIndexBuffer& prepared) {
	if (prepared.size == 0) {
		return;
	}
	if (prepared.owner != nullptr) {
		buffer.RetainResourceUntilFence(prepared.owner);
	}
	EXIT_IF(prepared.buffer == nullptr);
	vk_buffer.bindIndexBuffer(prepared.buffer, prepared.offset, prepared.type);
}

static void LogDrawStateIfNeeded(const RenderCommandBuffer& buffer, const DrawCallInfo& draw,
                                 const DrawRenderState& state, bool always_log,
                                 bool force_legacy_rect_log, uint32_t index_type_and_size,
                                 const void* index_addr) {
	EXIT_IF(draw.name == nullptr);

	if (!graphics_debug_dump_enabled()) {
		return;
	}

	if (!always_log && !force_legacy_rect_log) {
		return;
	}

	if (state.ps_active) {
		LogDrawTargetState(draw.name, state.color_info[0], state.depth_info, buffer,
		                   state.ps_input_info, draw.index_count, draw.flags);
	}
	LogDrawInputState(buffer, state.color_info[0], state.vs_input_info, index_type_and_size,
	                  draw.index_count, index_addr);
	// LogDrawTextureState(draw.name, state.color_info[0], state.ps_input_info);
}

static bool IsHostExpandedRectListDrawSupported(const ShaderVertexInputInfo& vs_input_info,
                                                const DrawCallInfo&          draw,
                                                const DrawEmitInfo&          emit) {
	if (!emit.draw_prim7_as_ngg) {
		return true;
	}

	if (vs_input_info.buffers_num != 0) {
		return false;
	}

	return draw.index_count == 3 || draw.index_count == emit.draw_vertex_count;
}

static void EmitDrawPrimitives(const HW::UserConfig& ucfg, vk::CommandBuffer vk_buffer,
                               const ShaderVertexInputInfo& vs_input_info, const DrawCallInfo& draw,
                               const DrawEmitInfo& emit) {
	EXIT_IF(draw.name == nullptr);

	switch (static_cast<Prospero::PrimitiveType>(ucfg.GetPrimType())) {
		case Prospero::PrimitiveType::kPointList:
		case Prospero::PrimitiveType::kLineList:
		case Prospero::PrimitiveType::kLineStrip:
		case Prospero::PrimitiveType::kTriList:
		case Prospero::PrimitiveType::kTriFan:
		case Prospero::PrimitiveType::kTriStrip:
			if (emit.indexed) {
				vk_buffer.drawIndexed(draw.index_count, draw.instance_count, 0, emit.vertex_offset,
				                      draw.first_instance);
			} else {
				vk_buffer.draw(draw.index_count, draw.instance_count, emit.first_vertex,
				               draw.first_instance);
			}
			break;
		case Prospero::PrimitiveType::kRectList:
			if (emit.indexed) {
				vk_buffer.drawIndexed(draw.index_count, draw.instance_count, 0, emit.vertex_offset,
				                      draw.first_instance);
			} else {
				EXIT_NOT_IMPLEMENTED(
				    !IsHostExpandedRectListDrawSupported(vs_input_info, draw, emit));
				vk_buffer.draw(emit.draw_vertex_count, draw.instance_count, emit.first_vertex,
				               draw.first_instance);
			}
			break;
		case Prospero::PrimitiveType::kRectListLegacy:
			if (emit.indexed) {
				EXIT("unknown primitive type: %u\n", ucfg.GetPrimType());
			}
			// Sarah
			EXIT_NOT_IMPLEMENTED(!(draw.index_count == 3 && vs_input_info.buffers_num == 0));
			vk_buffer.draw(4, draw.instance_count, emit.first_vertex, draw.first_instance);
			break;
		case Prospero::PrimitiveType::kQuadListLegacy:
			EXIT_NOT_IMPLEMENTED((draw.index_count & 0x3u) != 0);
			for (uint32_t i = 0; i < draw.index_count; i += 4) {
				if (emit.indexed) {
					vk_buffer.drawIndexed(4, draw.instance_count, i, emit.vertex_offset,
					                      draw.first_instance);
				} else {
					vk_buffer.draw(4, draw.instance_count, i + emit.first_vertex,
					               draw.first_instance);
				}
			}
			break;
		default: EXIT("unknown primitive type: %u\n", ucfg.GetPrimType());
	}
}

void RenderExecutor::ExecutePreparedDraw(uint64_t submit_id, RenderCommandBuffer& buffer,
                                         const DrawCallInfo& draw, DrawRenderState& state,
                                         vk::PrimitiveTopology topology, const DrawEmitInfo& emit,
                                         const DrawIndexBufferSource& index_source,
                                         bool log_pipeline_phase, bool set_bind_debug,
                                         bool set_auto_debug) {
	EXIT_IF(draw.name == nullptr);
	auto& ucfg = buffer.GetUserConfig();

	LogDrawPhase(draw.name, "PrepareBindings");
	auto bindings        = PrepareGraphicsBindings(buffer, state.vs_input_info.stage,
	                                               state.ps_input_info.stage, state.ps_active);
	auto vertex_bindings = PrepareVertexBuffers(submit_id, buffer, draw, state.vs_input_info);
	auto index_binding   = PrepareIndexBuffer(buffer, index_source);
	RebindVertexBuffers(buffer, state.vs_input_info, vertex_bindings);
	RebindIndexBuffer(buffer, index_binding);
	state.rendering =
	    AcquireRenderTargets(buffer, state.color_info, state.color_count, state.depth_info);
	if (state.color_count != 0) {
		m_context.GetTextureCache().DebugTraceImageWriter(
		    state.color_info[0].base_addr, state.color_info[0].image_id, false, false,
		    buffer.GetShaders().GetPs().ps_regs.data_addr,
		    static_cast<uint32_t>(m_context.GetGpu().GetFrameNum()));
	}

	if (log_pipeline_phase) {
		LogDrawPhase(draw.name, "CreatePipeline");
	}
	auto& pipeline = m_context.GetPipelineCache().CreateGraphicsPipeline(
	    state.color_info, state.color_count, state.depth_info, state.vs_input_info, buffer,
	    &state.ps_input_info, topology, index_binding.primitive_restart_enable, state.ps_active,
	    state.vs_shader, state.ps_shader);

	// Resource preparation above may synchronously finish and restart the scheduler. From this
	// point onward, every operation targets the current command buffer and cannot touch guest
	// memory.
	auto vk_buffer = buffer.Handle();
	if (set_bind_debug) {
		SetDrawDebugPhase(buffer, submit_id, draw, 0x100u);
	}
	if (set_auto_debug) {
		SetDrawDebugPhase(buffer, submit_id, draw, 0x200u);
	}
	CommitVertexBuffers(buffer, vk_buffer, vertex_bindings);
	CommitBindings(buffer, vk::PipelineBindPoint::eGraphics, pipeline.pipeline_layout,
	               bindings.vertex);
	if (bindings.pixel.has_value()) {
		if (set_auto_debug) {
			SetDrawDebugPhase(buffer, submit_id, draw, 0x300u);
		}
		CommitBindings(buffer, vk::PipelineBindPoint::eGraphics, pipeline.pipeline_layout,
		               *bindings.pixel);
	}
	CommitIndexBuffer(buffer, vk_buffer, index_binding);

	const auto dynamic_params =
	    BuildGraphicsDynamicParams(buffer, state.color_info, state.color_count, state.depth_info);
	SetDynamicParams(buffer, vk_buffer, dynamic_params, m_context.GetGraphics());

	LogDrawPhase(draw.name, "BeginRendering");
	if (set_auto_debug) {
		SetDrawDebugPhase(buffer, submit_id, draw, 0x400u);
	}
	m_context.GetCommandScheduler().BeginRendering(state.rendering);
	vk_buffer.bindPipeline(vk::PipelineBindPoint::eGraphics, pipeline.pipeline);
	if (set_auto_debug) {
		SetDrawDebugPhase(buffer, submit_id, draw, 0x500u);
	}
	EmitDrawPrimitives(ucfg, vk_buffer, state.vs_input_info, draw, emit);

	if (set_auto_debug) {
		SetDrawDebugPhase(buffer, submit_id, draw, 0x600u);
	}
	vk::PipelineStageFlags shader_write_stages = {};
	if (HasShaderBufferWrites(state.vs_input_info.stage)) {
		shader_write_stages |= vk::PipelineStageFlagBits::eVertexShader;
	}
	if (state.ps_active && HasShaderBufferWrites(state.ps_input_info.stage)) {
		shader_write_stages |= vk::PipelineStageFlagBits::eFragmentShader;
	}
	if (shader_write_stages) {
		m_context.GetCommandScheduler().EndRendering();
		ShaderWriteBarrier(vk_buffer, shader_write_stages);
	}
	LogDrawPhase(draw.name, "DrawComplete");
	if (set_auto_debug) {
		SetDrawDebugPhase(buffer, submit_id, draw, 0x700u);
	}
}

// Replays a skipped classic-GS draw: the merged ES/GS program runs as a
// compute pass that writes positions, parameters and connectivity into the
// geometry replay buffer, then a flat triangle-list draw with a synthetic
// vertex shader fetches from it. Every contract mismatch fails closed so the
// draw stays skipped instead of guessing.
// NOLINTNEXTLINE(readability-function-cognitive-complexity)
bool RenderExecutor::TryExecuteGeometryReplayDraw(uint64_t submit_id, RenderCommandBuffer& buffer,
                                                  uint32_t index_count, uint32_t instance_count,
                                                  uint32_t render_target_slice_offset) {
	const auto& ctx         = buffer.GetRegisters();
	const auto& ucfg        = buffer.GetUserConfig();
	const auto& sh_ctx      = buffer.GetShaders();
	const auto& sh_regs     = ctx.GetShaderRegisters();
	const auto& ge_cntl     = ucfg.GetGeControl();
	const auto& ge_user     = ucfg.GetGeUserVgprEn();
	const auto& vertex_info = sh_ctx.GetVs();

	if (instance_count == 0) {
		instance_count = 1;
	}
	// The replay preamble seeds v8 with the global primitive index, which only
	// matches the ES instance-index convention for single-index draws.
	if (index_count != 1 ||
	    ucfg.GetPrimType() != Prospero::GpuEnumValue(Prospero::PrimitiveType::kPointList)) {
		return false;
	}

	ClassicGeometryReplayLaunch launch {};
	if (!TryCreateClassicGeometryReplayLaunch(
	        {.shader_stages             = ctx.GetShaderStages(),
	         .primitive_group_size      = ge_cntl.primitive_group_size,
	         .vertex_group_size         = ge_cntl.vertex_group_size,
	         .primitive_amplification   = sh_regs.m_geNggSubgrpCntl & 0x1ffu,
	         .max_output_per_subgroup   = sh_regs.m_geMaxOutputPerSubgroup,
	         .gs_max_vertices_per_input = sh_regs.m_vgtGsMaxVertOut,
	         .gs_output_primitive       = sh_regs.m_vgtGsOutPrimType,
	         .gs_instance_count         = sh_regs.m_vgtGsInstanceCnt,
	         .esgs_ring_item_size       = sh_regs.m_vgtEsgsRingItemsize,
	         .ge_user_vgpr_enable       = static_cast<uint32_t>(ge_user.vgpr1) |
	                                      (static_cast<uint32_t>(ge_user.vgpr2) << 1u) |
	                                      (static_cast<uint32_t>(ge_user.vgpr3) << 2u)},
	        launch) ||
	    launch.output_primitive != NativePrimitiveOutput::Triangles) {
		return false;
	}

	GsSubgroupLaunchPlan plan {};
	if (!TryPlanPointListGsSubgroups(launch, index_count, instance_count,
	                                 ge_cntl.MultipleInstancesPerWave(), plan)) {
		return false;
	}

	std::span<const uint32_t> es_code;
	std::span<const uint32_t> gs_code;
	if (!ShaderGetGuestCode(vertex_info.es_regs.data_addr, es_code) ||
	    !ShaderGetGuestCode(vertex_info.gs_regs.data_addr, gs_code)) {
		return false;
	}
	// The live ES program is straight-line code ending in the tail call into
	// the GS; trim at the first terminal s_setpc_b64 s[6:7].
	size_t es_live = 0;
	for (size_t i = 0; i < es_code.size(); i++) {
		if (es_code[i] == EsGsMergeSetpcWord) {
			es_live = i + 1;
			break;
		}
	}
	std::vector<uint32_t> merged;
	if (es_live == 0 ||
	    !TryMergeEsGsForReplay(es_code.data(), es_live, gs_code.data(), gs_code.size(), merged)) {
		return false;
	}

	const auto lds_dwords = static_cast<uint32_t>(vertex_info.gs_regs.rsrc2.lds_size) * 128u;
	const uint32_t slots  = std::max(launch.output_vertex_slots, launch.output_primitive_slots);
	const uint32_t waves  = (slots + launch.wave_lane_count - 1u) / launch.wave_lane_count;
	if (lds_dwords == 0 || waves == 0 || waves > 0xfu) {
		return false;
	}

	ShaderGeometryReplayCompileInfo replay {};
	replay.es_addr         = vertex_info.es_regs.data_addr;
	replay.gs_addr         = vertex_info.gs_regs.data_addr;
	replay.gs_hash         = vertex_info.gs_regs.chksum;
	replay.vertex_slots    = launch.output_vertex_slots;
	replay.primitive_slots = launch.output_primitive_slots;
	replay.lds_dword_count = lds_dwords;
	replay.threads_num     = waves * launch.wave_lane_count;

	ShaderComputeInputInfo    cs_info {};
	std::span<const uint32_t> cs_spirv;
	if (!ShaderCompileGeometryReplayCS(merged, vertex_info, replay, cs_info, cs_spirv) ||
	    replay.parameter_count == 0 || replay.parameter_count > 2) {
		return false;
	}
	const auto& replay_program   = *cs_info.stage.program;
	const auto& replay_resources = *cs_info.stage.resources;
	for (uint32_t i = 0; i < replay_program.info.buffers.size(); i++) {
		ShaderBufferResource descriptor {};
		const auto&          value = replay_resources.buffers[i];
		if (value.dword_count < std::size(descriptor.fields)) {
			return false;
		}
		std::copy_n(value.dwords.begin(), std::size(descriptor.fields), descriptor.fields);
		const uint64_t address = descriptor.Base48();
		const uint64_t stride  = descriptor.Stride();
		const uint64_t records = descriptor.NumRecords();
		if (address == 0 || records == 0) {
			continue;
		}
		if (stride != 0 && records > UINT64_MAX / stride) {
			return false;
		}
		const uint64_t size = stride != 0 ? stride * records : records;
		uint8_t        byte = 0;
		if (!Libs::LibKernel::Memory::TryReadBacking(address, &byte, 1) ||
		    !Libs::LibKernel::Memory::TryReadBacking(address + size - 1, &byte, 1)) {
			static std::atomic<uint32_t> invalid_resource_log_count {0};
			if (invalid_resource_log_count.fetch_add(1, std::memory_order_relaxed) < 32) {
				LOGF("GeometryReplay: skipping unmapped buffer[%u] addr=0x%016" PRIx64
				     " size=0x%016" PRIx64 " source=%u read=%d written=%d\n",
				     i, address, size, replay_program.info.buffers[i].source,
				     replay_program.info.buffers[i].read, replay_program.info.buffers[i].written);
			}
			return false;
		}
	}

	const ShaderRecompiler::IR::GeometryReplayLayout layout {replay.vertex_slots,
	                                                         replay.primitive_slots,
	                                                         replay.parameter_count};
	const uint64_t used_dwords = ShaderRecompiler::IR::GeometryReplayLayout::HeaderDwords +
	                             static_cast<uint64_t>(plan.subgroup_count) * layout.BlockStride();
	if (used_dwords * 4ull > BufferCache::GEOMETRY_REPLAY_BUFFER_SIZE) {
		return false;
	}

	const DrawCallInfo draw {"GeometryReplay", CommandBufferDebugOp::DrawIndexAuto,
	                         plan.subgroup_count * layout.primitive_slots * 3u,
	                         0,
	                         1,
	                         0};

	DrawRenderState state {};
	if (!PrepareDrawRenderState(submit_id, buffer, draw, render_target_slice_offset, false,
	                            state)) {
		ResetBindings();
		return false;
	}

	if (!ShaderCompileGeometryReplayVS(replay, SelectGraphicsLaneMaskMode(64u),
	                                   state.vs_input_info, state.vs_shader)) {
		ResetBindings();
		return false;
	}
	if (state.ps_active) {
		std::array<Prospero::ColorComponentMapping, RENDER_COLOR_ATTACHMENTS_MAX>
		    target_export_mapping {};
		for (uint32_t i = 0; i < state.color_count; i++) {
			target_export_mapping[state.color_info[i].target_slot] =
			    state.color_info[i].export_mapping;
		}
		if (!ShaderCompileInfoPS(sh_ctx.GetPs(), sh_regs, SelectGraphicsLaneMaskMode(64u),
		                         state.vs_input_info, target_export_mapping, state.ps_input_info,
		                         state.ps_shader, ctx.GetModeControl().persp_corr_dis,
	                         ctx.GetAaSampleMask(), ctx.GetPsShaderSampleExclusionMask(),
	                         ctx.GetAlphaToMaskControl().raw,
	                         ctx.GetEqaaControl().alpha_to_mask_num_samples,
	                         ctx.GetRenderTarget(0).info.round_mode)) {
			EXIT("ShaderCompileInfoPS failed for draw %s\n", draw.name);
		}
	}

	// Compute pass: reset the used range to the culled-primitive sentinel,
	// write the launch header, then run one workgroup per subgroup.
	buffer.EndRendering();
	auto       vk_buffer     = buffer.Handle();
	const auto replay_handle = m_context.GetBufferCache().GetGeometryReplayBuffer().Handle();
	vk_buffer.fillBuffer(replay_handle, 0, used_dwords * 4ull, 0x80000000u);
	const std::array<uint32_t, ShaderRecompiler::IR::GeometryReplayLayout::HeaderDwords> header {
	    index_count * instance_count, plan.primitives_per_full_subgroup, layout.vertex_slots,
	    layout.primitive_slots};
	vk_buffer.updateBuffer(replay_handle, 0, sizeof(header), header.data());
	VulkanMemoryBarrier fill_barrier {};
	fill_barrier.sType         = vk::StructureType::eMemoryBarrier;
	fill_barrier.srcAccessMask = vk::AccessFlagBits::eTransferWrite;
	fill_barrier.dstAccessMask = vk::AccessFlagBits::eShaderRead | vk::AccessFlagBits::eShaderWrite;
	vk_buffer.pipelineBarrier(vk::PipelineStageFlagBits::eTransfer,
	                          vk::PipelineStageFlagBits::eComputeShader, vk::DependencyFlags {}, 1,
	                          &fill_barrier, 0, nullptr, 0, nullptr);

	HW::ComputeShaderInfo cs_regs {};
	cs_regs.cs_regs.data_addr    = vertex_info.gs_regs.data_addr;
	cs_regs.cs_regs.num_thread_x = replay.threads_num;
	cs_regs.cs_regs.num_thread_y = 1;
	cs_regs.cs_regs.num_thread_z = 1;
	auto& cs_pipeline =
	    m_context.GetPipelineCache().CreateComputePipeline(cs_info, cs_regs, cs_spirv);
	auto cs_bindings = PrepareBindings(buffer, cs_info.stage, vk::ShaderStageFlagBits::eCompute,
	                                   DescriptorCache::Stage::Compute);
	RebindBuffers(buffer, cs_bindings);
	RebindImages(buffer, cs_bindings);
	CommitBindings(buffer, vk::PipelineBindPoint::eCompute, cs_pipeline.pipeline_layout,
	               cs_bindings);
	vk_buffer.bindPipeline(vk::PipelineBindPoint::eCompute, cs_pipeline.pipeline);
	vk_buffer.dispatch(plan.subgroup_count, 1, 1);
	ShaderWriteBarrier(vk_buffer, vk::PipelineStageFlagBits::eComputeShader);

	static std::atomic<uint32_t> log_count {0};
	if (log_count.fetch_add(1, std::memory_order_relaxed) < 32) {
		LOGF("GeometryReplay: es=0x%016" PRIx64 " gs=0x%016" PRIx64
		     " instances=%u subgroups=%u prims/full=%u verts=%u prim_slots=%u params=%u "
		     "threads=%u lds=%u\n",
		     replay.es_addr, replay.gs_addr, instance_count, plan.subgroup_count,
		     plan.primitives_per_full_subgroup, replay.vertex_slots, replay.primitive_slots,
		     replay.parameter_count, replay.threads_num, replay.lds_dword_count);
	}

	const DrawEmitInfo          emit {};
	const DrawIndexBufferSource index_source {};
	ExecutePreparedDraw(submit_id, buffer, draw, state, vk::PrimitiveTopology::eTriangleList, emit,
	                    index_source, false, false, false);
	// Falsification probe: read the replay draw's color target back and count
	// nonzero RGB pixels, so replay output can be separated from later writers.
	if (const auto* probe = std::getenv("PROSPERISMO_TRACE_REPLAY_RT");
	    probe != nullptr && std::strcmp(probe, "1") == 0 && state.color_count != 0 &&
	    state.color_info[0].image_id) {
		static std::atomic<uint32_t> probe_count {0};
		const auto sample = probe_count.fetch_add(1, std::memory_order_relaxed);
		if (sample < 4 || sample % 512u == 0) {
			auto& texture_cache = m_context.GetTextureCache();
			const auto stats =
			    texture_cache.DebugDownloadByteStats(state.color_info[0].image_id);
			LOGF("GeometryReplayTarget: sample=%u addr=0x%010" PRIx64 " extent=%ux%u fmt=%d "
			     "size=%" PRIu64 " nonzero_bytes=%" PRIu64 " hash=0x%016" PRIx64
			     " valid=%s rgb_pixels=%" PRIu64 " alpha_pixels=%" PRIu64
			     " rgb_bbox=%u,%u-%u,%u\n",
			     sample, state.color_info[0].base_addr, state.color_info[0].extent.width,
			     state.color_info[0].extent.height, static_cast<int>(state.color_info[0].format),
			     stats.size, stats.nonzero_bytes, stats.hash, stats.valid ? "true" : "false",
			     stats.rgb_nonzero_pixels, stats.alpha_nonzero_pixels, stats.rgb_min_x,
			     stats.rgb_min_y, stats.rgb_max_x, stats.rgb_max_y);
			const uint64_t block_words =
			    ShaderRecompiler::IR::GeometryReplayLayout::HeaderDwords + layout.BlockStride();
			const auto words = texture_cache.DebugDownloadBufferWords(
			    replay_handle, 0, block_words * 4ull);
			if (words.size() == block_words) {
				const auto* base = words.data() + ShaderRecompiler::IR::GeometryReplayLayout::HeaderDwords;
				uint32_t live_prims = 0;
				for (uint32_t i = 0; i < layout.primitive_slots; i++) {
					live_prims += base[layout.PrimitiveOffset() + i] != 0x80000000u ? 1u : 0u;
				}
				uint32_t nonzero_pos = 0;
				for (uint32_t i = 0; i < layout.vertex_slots; i++) {
					const auto* p = base + layout.PositionOffset() + i * 4u;
					nonzero_pos += (p[0] | p[1] | p[2] | p[3]) != 0 ? 1u : 0u;
				}
				auto f = [&](uint32_t w) {
					float v = 0;
					std::memcpy(&v, &w, 4);
					return v;
				};
				LOGF("GeometryReplayBuffer: sample=%u header=%u,%u,%u,%u counts=0x%08x,0x%08x "
				     "live_prims=%u/%u nonzero_pos=%u/%u prim0=0x%08x prim1=0x%08x "
				     "pos0=%g,%g,%g,%g pos1=%g,%g,%g,%g\n",
				     sample, words[0], words[1], words[2], words[3],
				     base[layout.CountsOffset()], base[layout.CountsOffset() + 1u], live_prims,
				     layout.primitive_slots, nonzero_pos, layout.vertex_slots,
				     base[layout.PrimitiveOffset()], base[layout.PrimitiveOffset() + 1u],
				     f(base[0]), f(base[1]), f(base[2]), f(base[3]), f(base[4]), f(base[5]),
				     f(base[6]), f(base[7]));
			}
			// Trace the guest-side gate: V#0 from the GS SRT table, and the
			// float at record offset 0x2c that the GS requires to be > 0.
			{
				const uint64_t table = static_cast<uint64_t>(vertex_info.gs_user_sgpr.value[0]) |
				                       (static_cast<uint64_t>(vertex_info.gs_user_sgpr.value[1])
				                        << 32u);
				uint32_t vsharp[4] = {};
				bool     ok        = table != 0;
				for (uint32_t i = 0; ok && i < 4; i++) {
					ok = ShaderReadMappedGuestDword(nullptr, table + i * 4ull, &vsharp[i]);
				}
				if (ok) {
					const uint64_t rec_base =
					    static_cast<uint64_t>(vsharp[0]) |
					    ((static_cast<uint64_t>(vsharp[1]) & 0xffffull) << 32u);
					const uint32_t stride      = (vsharp[1] >> 16u) & 0x3fffu;
					const uint32_t num_records = vsharp[2];
					auto           read_f32    = [&](uint64_t addr) {
                        uint32_t w = 0;
                        float    v = 0;
                        if (ShaderReadMappedGuestDword(nullptr, addr, &w)) {
                            std::memcpy(&v, &w, 4);
                        }
                        return v;
					};
					LOGF("GeometryReplayGate: sample=%u srt=0x%016" PRIx64 " base=0x%010" PRIx64
					     " stride=%u records=%u gate[0..7]=%g,%g,%g,%g,%g,%g,%g,%g\n",
					     sample, table, rec_base, stride, num_records,
					     read_f32(rec_base + 0x2c), read_f32(rec_base + stride + 0x2c),
					     read_f32(rec_base + 2ull * stride + 0x2c),
					     read_f32(rec_base + 3ull * stride + 0x2c),
					     read_f32(rec_base + 4ull * stride + 0x2c),
					     read_f32(rec_base + 5ull * stride + 0x2c),
					     read_f32(rec_base + 6ull * stride + 0x2c),
					     read_f32(rec_base + 7ull * stride + 0x2c));
					// The CS reads this data through the buffer cache; a stale or
					// diverged GPU copy would explain a dead gate that looks live
					// from the CPU. Read the same records back through the cache.
					if (stride != 0 && num_records != 0) {
						auto&          buffer_cache = m_context.GetBufferCache();
						const uint64_t span = std::min<uint64_t>(num_records, 8u) * stride;
						const bool     gpu_dirty =
						    buffer_cache.IsRegionGpuModified(rec_base, span);
						const auto binding =
						    buffer_cache.ObtainBuffer(buffer, rec_base, span, false, true);
						const uint64_t aligned = span + 64u;
						const auto gpu_words = m_context.GetTextureCache().DebugDownloadBufferWords(
						    binding.buffer, binding.offset, (aligned / 4u) * 4u);
						auto gpu_f32 = [&](uint64_t byte_offset) {
							float v = 0;
							if (byte_offset / 4u < gpu_words.size()) {
								std::memcpy(&v, &gpu_words[byte_offset / 4u], 4);
							}
							return v;
						};
						LOGF("GeometryReplayGateGpu: sample=%u gpu_dirty=%d words=%zu "
						     "gate[0..2]=%g,%g,%g rec0=%08x,%08x,%08x,%08x,%08x,%08x\n",
						     sample, gpu_dirty ? 1 : 0, gpu_words.size(), gpu_f32(0x2c),
						     gpu_f32(stride + 0x2c), gpu_f32(2ull * stride + 0x2c),
						     gpu_words.size() > 5 ? gpu_words[0] : 0u,
						     gpu_words.size() > 5 ? gpu_words[1] : 0u,
						     gpu_words.size() > 5 ? gpu_words[2] : 0u,
						     gpu_words.size() > 5 ? gpu_words[3] : 0u,
						     gpu_words.size() > 5 ? gpu_words[4] : 0u,
						     gpu_words.size() > 5 ? gpu_words[5] : 0u);
					}
				} else {
					LOGF("GeometryReplayGate: sample=%u srt=0x%016" PRIx64 " unreadable\n", sample,
					     table);
				}
			}
		}
	}
	ResetBindings();
	return true;
}

void RenderExecutor::DrawIndex(uint64_t submit_id, RenderCommandBuffer& buffer,
                               uint32_t index_type_and_size, uint32_t index_count,
                               const void* index_addr, uint32_t flags, uint32_t type,
                               uint32_t instance_count, uint32_t render_target_slice_offset,
                               int32_t vertex_offset_add, uint32_t first_instance) {
	KYTY_PROFILER_FUNCTION();

	EXIT_IF(buffer.IsInvalid());
	auto& ucfg   = buffer.GetUserConfig();
	auto& sh_ctx = buffer.GetShaders();

	buffer.SetDebugInfo(static_cast<uint32_t>(CommandBufferDebugOp::DrawIndex), submit_id,
	                    index_count, flags, type, instance_count,
	                    reinterpret_cast<uint64_t>(index_addr));

	Common::LockGuard lock(m_context.GetMutex());
	if (index_count == 0) {
		return;
	}

	if (ExecuteColorMetadataOperation(submit_id, buffer, render_target_slice_offset)) {
		ResetBindings();
		return;
	}

	if (!DrawHasValidVertexShader(sh_ctx)) {
		return;
	}

	if (ShouldSkipGeShader(buffer)) {
		static_cast<void>(TryExecuteGeometryReplayDraw(submit_id, buffer, index_count,
		                                               instance_count,
		                                               render_target_slice_offset));
		return;
	}

	if (graphics_debug_dump_enabled()) {
		sh_print("GraphicsRenderDrawIndex():Shader:", sh_ctx);
		uc_print("GraphicsRenderDrawIndex():UserConfig:", ucfg);
		hw_print(buffer);

		LOGF("GraphicsRenderDrawIndex():Parameters:\n"
		     "\t index_type_and_size = 0x%08" PRIx32 "\n"
		     "\t index_count         = 0x%08" PRIx32 "\n"
		     "\t index_addr          = 0x%016" PRIx64 "\n"
		     "\t flags               = 0x%08" PRIx32 "\n"
		     "\t type                = 0x%08" PRIx32 "\n"
		     "\t instance_count      = 0x%08" PRIx32 "\n"
		     "\t rt_slice_offset     = 0x%08" PRIx32 "\n"
		     "\t vertex_offset_add   = 0x%08" PRIx32 "\n"
		     "\t first_instance      = 0x%08" PRIx32 "\n",
		     index_type_and_size, index_count, reinterpret_cast<uint64_t>(index_addr), flags, type,
		     instance_count, render_target_slice_offset, static_cast<uint32_t>(vertex_offset_add),
		     first_instance);
	}

	uc_check(ucfg);

	hw_check(buffer);

	vk::PrimitiveTopology topology = vk::PrimitiveTopology::ePointList;
	if (!GetDrawTopology(ucfg, false, false, topology)) {
		return;
	}

	vk::IndexType index_type           = vk::IndexType::eUint16;
	uint64_t      index_size           = 0;
	uint32_t      guest_index_bits     = 16;
	uint32_t      host_index_bits      = 16;
	bool          expand_index8_to_u16 = false;

	switch (static_cast<Prospero::IndexType>(index_type_and_size)) {
		case Prospero::IndexType::kIndex16:
			index_type = vk::IndexType::eUint16;
			index_size = 2 * static_cast<uint64_t>(index_count);
			guest_index_bits = 16;
			host_index_bits  = 16;
			break;
		case Prospero::IndexType::kIndex32:
			index_type = vk::IndexType::eUint32;
			index_size = 4 * static_cast<uint64_t>(index_count);
			guest_index_bits = 32;
			host_index_bits  = 32;
			break;
		// Some games use it - need vulkan extension
		case Prospero::IndexType::kIndex8:
			index_type           = vk::IndexType::eUint16;
			index_size           = static_cast<uint64_t>(index_count);
			guest_index_bits     = 8;
			host_index_bits      = 16;
			expand_index8_to_u16 = true;
			break;
		default: EXIT("unknown index_type_and_size: %u\n", index_type_and_size);
	}

	EXIT_NOT_IMPLEMENTED(flags != 0);
	EXIT_NOT_IMPLEMENTED(type != 1);
	if (instance_count == 0) {
		instance_count = 1;
	}

	const DrawCallInfo    draw {"DrawIndex",    CommandBufferDebugOp::DrawIndex,
	                            index_count,    flags,
	                            instance_count, first_instance};
	const auto& reset_control = ucfg.GetPrimitiveResetControl();
	const auto  restart = ResolvePrimitiveRestart(
	    reset_control.enable, reset_control.match_all_bits,
	    buffer.GetRegisters().GetPrimitiveResetIndex(), guest_index_bits, host_index_bits, topology);

	std::vector<uint16_t> expanded_indices;
	std::vector<uint16_t> remapped_indices16;
	std::vector<uint32_t> remapped_indices32;
	if (expand_index8_to_u16) {
		EXIT_NOT_IMPLEMENTED(index_addr == nullptr);
		const auto* src = static_cast<const uint8_t*>(index_addr);
		expanded_indices.resize(index_count);
		for (uint32_t i = 0; i < index_count; i++) {
			expanded_indices[i] =
			    static_cast<uint16_t>(TranslatePrimitiveRestartIndex(restart, src[i]));
		}
	} else if (restart.enable && restart.remap) {
		EXIT_NOT_IMPLEMENTED(index_addr == nullptr);
		const auto* src = static_cast<const uint8_t*>(index_addr);
		if (guest_index_bits == 16u) {
			remapped_indices16.resize(index_count);
			for (uint32_t i = 0; i < index_count; i++) {
				uint16_t value = 0;
				std::memcpy(&value, src + static_cast<uint64_t>(i) * sizeof(value), sizeof(value));
				remapped_indices16[i] =
				    static_cast<uint16_t>(TranslatePrimitiveRestartIndex(restart, value));
			}
		} else {
			remapped_indices32.resize(index_count);
			for (uint32_t i = 0; i < index_count; i++) {
				uint32_t value = 0;
				std::memcpy(&value, src + static_cast<uint64_t>(i) * sizeof(value), sizeof(value));
				remapped_indices32[i] = TranslatePrimitiveRestartIndex(restart, value);
			}
		}
	}

	DrawIndexBufferSource index_source {};
	index_source.enabled                  = true;
	index_source.primitive_restart_enable = restart.enable;
	index_source.address                  = reinterpret_cast<uint64_t>(index_addr);
	index_source.size                     = index_size;
	index_source.type                     = index_type;
	if (!expanded_indices.empty()) {
		index_source.host_data = expanded_indices.data();
		index_source.size      = expanded_indices.size() * sizeof(uint16_t);
	} else if (!remapped_indices16.empty()) {
		index_source.host_data = remapped_indices16.data();
		index_source.size      = remapped_indices16.size() * sizeof(uint16_t);
	} else if (!remapped_indices32.empty()) {
		index_source.host_data = remapped_indices32.data();
		index_source.size      = remapped_indices32.size() * sizeof(uint32_t);
	}

	DrawRenderState state {};
	if (!PrepareDrawRenderState(submit_id, buffer, draw, render_target_slice_offset, true, state)) {
		ResetBindings();
		return;
	}

	RefreshShaders(buffer, draw, true, state);

	LogDrawStateIfNeeded(buffer, draw, state, true, false, index_type_and_size, index_addr);

	const auto vertex_offset =
	    ResolveVertexOffset(ucfg.GetIndexOffset(), state.vs_input_info) + vertex_offset_add;

	DrawEmitInfo emit {};
	emit.indexed       = true;
	emit.vertex_offset = vertex_offset;

	ExecutePreparedDraw(submit_id, buffer, draw, state, topology, emit, index_source, true, true,
	                    false);
	ResetBindings();
}

// NOLINTNEXTLINE(readability-function-cognitive-complexity)
void RenderExecutor::DrawAuto(uint64_t submit_id, RenderCommandBuffer& buffer, uint32_t index_count,
                              uint32_t flags, uint32_t render_target_slice_offset,
                              uint32_t instance_count, uint32_t first_vertex,
                              uint32_t first_instance) {
	KYTY_PROFILER_FUNCTION();

	EXIT_IF(buffer.IsInvalid());
	auto& ucfg   = buffer.GetUserConfig();
	auto& sh_ctx = buffer.GetShaders();

	buffer.SetDebugInfo(static_cast<uint32_t>(CommandBufferDebugOp::DrawIndexAuto), submit_id,
	                    index_count, flags, first_vertex, instance_count, first_instance);

	Common::LockGuard lock(m_context.GetMutex());
	if (index_count == 0) {
		return;
	}

	if (ExecuteColorMetadataOperation(submit_id, buffer, render_target_slice_offset)) {
		ResetBindings();
		return;
	}

	if (!DrawHasValidVertexShader(sh_ctx)) {
		return;
	}

	if (ShouldSkipGeShader(buffer)) {
		static_cast<void>(TryExecuteGeometryReplayDraw(submit_id, buffer, index_count,
		                                               instance_count,
		                                               render_target_slice_offset));
		return;
	}

	if (graphics_debug_dump_enabled()) {
		sh_print("GraphicsRenderDrawIndexAuto():Shader:", sh_ctx);
		uc_print("GraphicsRenderDrawIndexAuto():UserConfig:", ucfg);
		hw_print(buffer);

		LOGF("GraphicsRenderDrawIndexAuto():Parameters:\n"
		     "\t index_count         = 0x%08" PRIx32 "\n"
		     "\t flags               = 0x%08" PRIx32 "\n"
		     "\t rt_slice_offset     = 0x%08" PRIx32 "\n"
		     "\t instance_count      = 0x%08" PRIx32 "\n"
		     "\t first_vertex        = 0x%08" PRIx32 "\n"
		     "\t first_instance      = 0x%08" PRIx32 "\n",
		     index_count, flags, render_target_slice_offset, instance_count, first_vertex,
		     first_instance);
	}

	uc_check(ucfg);

	hw_check(buffer);

	EXIT_NOT_IMPLEMENTED(flags != 0);
	if (instance_count == 0) {
		instance_count = 1;
	}

	const DrawCallInfo draw {"DrawIndexAuto", CommandBufferDebugOp::DrawIndexAuto,
	                         index_count,     flags,
	                         instance_count,  first_instance};

	DrawRenderState state {};
	if (!PrepareDrawRenderState(submit_id, buffer, draw, render_target_slice_offset, false,
	                            state)) {
		ResetBindings();
		return;
	}

	vk::PrimitiveTopology topology              = vk::PrimitiveTopology::ePointList;
	const bool            use_ngg_rectlist_draw = Config::NggRectlistDrawEnabled();

	if (!GetDrawTopology(ucfg, true, use_ngg_rectlist_draw, topology)) {
		ResetBindings();
		return;
	}
	const bool draw_prim7_as_ngg =
	    (use_ngg_rectlist_draw &&
	     ucfg.GetPrimType() == Prospero::GpuEnumValue(Prospero::PrimitiveType::kRectList));

	RefreshShaders(buffer, draw, false, state);

	if (draw_prim7_as_ngg && state.vs_input_info.buffers_num == 0 &&
	    state.vs_input_info.param_export_mask == 0 && state.ps_input_info.input_num != 0) {
		if (state.color_count != 0) {
			m_context.GetTextureCache().DebugTraceImageWriter(
			    state.color_info[0].base_addr, state.color_info[0].image_id, false, true,
			    sh_ctx.GetVs().gs_regs.data_addr,
			    static_cast<uint32_t>(m_context.GetGpu().GetFrameNum()));
		}
		if (graphics_debug_dump_enabled()) {
			LOGF("DrawIndexAuto: skipping rect-list draw with no VS param exports and PS inputs: "
			     "ps_inputs=%u ps=0x%016" PRIx64 " es=0x%016" PRIx64 " gs=0x%016" PRIx64 "\n",
			     state.ps_input_info.input_num, sh_ctx.GetPs().ps_regs.chksum,
			     sh_ctx.GetVs().es_regs.data_addr, sh_ctx.GetVs().gs_regs.data_addr);
		}
		ResetBindings();
		return;
	}

	LogDrawStateIfNeeded(buffer, draw, state, false,
	                     ucfg.GetPrimType() ==
	                         Prospero::GpuEnumValue(Prospero::PrimitiveType::kRectListLegacy),
	                     0, nullptr);

	const uint32_t draw_vertex_count = (draw_prim7_as_ngg ? 4u : index_count);
	const auto     vertex_offset = ResolveVertexOffset(ucfg.GetIndexOffset(), state.vs_input_info) +
	                               static_cast<int32_t>(first_vertex);
	DrawEmitInfo   emit {};
	emit.draw_prim7_as_ngg = draw_prim7_as_ngg;
	emit.draw_vertex_count = draw_vertex_count;
	emit.first_vertex      = static_cast<uint32_t>(vertex_offset);

	DrawIndexBufferSource index_source {};
	ExecutePreparedDraw(submit_id, buffer, draw, state, topology, emit, index_source, false, false,
	                    true);
	ResetBindings();
}

bool RenderExecutor::ResolveColorTargets(uint64_t submit_id, RenderCommandBuffer& buffer,
                                         uint32_t render_target_slice_offset) {
	const auto& hw = buffer.GetRegisters();
	if (hw.GetColorControl().mode != 3) {
		return false;
	}

	const auto& src_rt = hw.GetRenderTarget(0);
	const auto& dst_rt = hw.GetRenderTarget(1);
	if (src_rt.base.addr == 0 || dst_rt.base.addr == 0) {
		return false;
	}

	RenderColorInfo src {};
	RenderColorInfo dst {};
	ResolveRenderColorTarget(submit_id, buffer, src, render_target_slice_offset, 0, true, true);
	ResolveRenderColorTarget(submit_id, buffer, dst, render_target_slice_offset, 1, true, true);
	if (!src.image_id || !dst.image_id || src.type == RenderColorType::NoColorOutput ||
	    dst.type == RenderColorType::NoColorOutput) {
		return false;
	}
	if (src.base_addr == dst.base_addr && src.base_mip_level == dst.base_mip_level &&
	    src.base_array_layer == dst.base_array_layer) {
		return true;
	}

	auto& cache = m_context.GetTextureCache();
	cache.MarkGpuWritten(dst.image_id);
	auto& source      = cache.GetImage(src.image_id);
	auto& destination = cache.GetImage(dst.image_id);
	destination.Resolve(source, {src.base_mip_level, 1, src.base_array_layer, 1},
	                    {dst.base_mip_level, 1, dst.base_array_layer, 1});
	return true;
}

} // namespace Libs::Graphics
