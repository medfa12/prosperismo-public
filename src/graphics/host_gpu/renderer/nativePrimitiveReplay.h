// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

#pragma once

#include <cstdint>
#include <limits>
#include <vector>

namespace Libs::Graphics {

enum class NativePrimitiveOutput : uint32_t { Points = 0, Lines = 1, Triangles = 2 };

struct NativePrimitiveLaunchState {
	uint32_t shader_stages             = 0;
	uint32_t primitive_group_size      = 0;
	uint32_t vertex_group_size         = 0;
	uint32_t primitive_amplification   = 0;
	uint32_t max_output_per_subgroup   = 0;
	uint32_t gs_max_vertices_per_input = 0;
	uint32_t gs_output_primitive       = 0;
	uint32_t gs_instance_count         = 0;
	uint32_t esgs_ring_item_size       = 0;
	uint32_t ge_user_vgpr_enable       = 0;
};

struct ClassicGeometryReplayLaunch {
	uint32_t wave_lane_count         = 0;
	uint32_t primitive_group_limit   = 0;
	uint32_t vertex_group_limit      = 0;
	uint32_t output_vertex_slots     = 0;
	uint32_t output_primitive_slots  = 0;
	NativePrimitiveOutput output_primitive = NativePrimitiveOutput::Points;
};

struct NativePrimitiveIndices {
	uint32_t vertex0 = 0;
	uint32_t vertex1 = 0;
	uint32_t vertex2 = 0;
};

// Live subgroup schedule derived from an exact point-list draw packet. The
// counts here are the per-wave launch values Sony's SGPR3 ABI consumes; the
// GE_CNTL/VGT_GS_ONCHIP_CNTL capacity fields only bound them.
struct GsSubgroupLaunchPlan {
	uint32_t subgroup_count               = 0;
	uint32_t primitives_per_full_subgroup = 0;
	uint32_t tail_subgroup_primitives     = 0; // equals the full count when the division is exact
	uint32_t waves_per_subgroup           = 0;
};

// SDK 12 defines GS user SGPR3 as four packed launch fields. Counts are live
// per-wave values synthesized from the draw; GE_CNTL's group fields are limits
// and must never be substituted for them.
[[nodiscard]] constexpr bool TryPackGsWaveLaunch(uint32_t vertex_count,
                                                 uint32_t primitive_count,
                                                 uint32_t wave_index,
                                                 uint32_t workgroup_wave_count,
                                                 uint32_t& packed) {
	packed = 0;
	if (vertex_count > 0xffu || primitive_count > 0xffu || wave_index > 0x0fu ||
	    workgroup_wave_count == 0 || workgroup_wave_count > 0x0fu ||
	    wave_index >= workgroup_wave_count) {
		return false;
	}
	packed = vertex_count | (primitive_count << 8u) | (wave_index << 24u) |
	         (workgroup_wave_count << 28u);
	return true;
}

// Sony's native-primitive ABI packs three subgroup-relative 10-bit indices in
// EXP target 20. Connectivity outside the dynamically allocated vertex count
// is invalid even when it fits in ten bits.
[[nodiscard]] constexpr bool TryPackPrimitiveConnectivity(
	const NativePrimitiveIndices& indices, uint32_t allocated_vertex_count, uint32_t& packed) {
	packed = 0;
	if (allocated_vertex_count == 0 || indices.vertex0 > 0x3ffu || indices.vertex1 > 0x3ffu ||
	    indices.vertex2 > 0x3ffu || indices.vertex0 >= allocated_vertex_count ||
	    indices.vertex1 >= allocated_vertex_count || indices.vertex2 >= allocated_vertex_count) {
		return false;
	}
	packed = indices.vertex0 | (indices.vertex1 << 10u) | (indices.vertex2 << 20u);
	return true;
}

[[nodiscard]] constexpr NativePrimitiveIndices UnpackPrimitiveConnectivity(uint32_t packed) {
	return {.vertex0 = packed & 0x3ffu,
	        .vertex1 = (packed >> 10u) & 0x3ffu,
	        .vertex2 = (packed >> 20u) & 0x3ffu};
}

[[nodiscard]] constexpr bool OwnsGsAllocation(uint32_t wave_index) { return wave_index == 0; }

// Sony SDK 10's CxVgtShaderStagesEn names GS_EN, HS_EN, PASSTHROUGH and the
// geometry wave-size bit.  A classic GS uses GS_EN without PASSTHROUGH.  SDK
// native_prim.pssl supplies the subgroup-relative output ABI; Sony's register
// NATVIS supplies GE_CNTL and output-limit field widths.  PRIM_AMP_FACTOR is
// the only field below corroborated by the gfx10.1 register table because SDK
// 10 does not expose that register in NATVIS.
[[nodiscard]] constexpr bool TryCreateClassicGeometryReplayLaunch(
	const NativePrimitiveLaunchState& state, ClassicGeometryReplayLaunch& launch) {
	constexpr uint32_t HsEnableMask      = 0x00000004u;
	constexpr uint32_t GsEnableMask      = 0x00000020u;
	constexpr uint32_t GsWave32Mask      = 0x00400000u;
	constexpr uint32_t PassthroughMask   = 0x02000000u;
	constexpr uint32_t MaximumGroupSize  = 0x100u;
	constexpr uint32_t MaximumOutput     = 0x3ffu;

	launch = {};
	if ((state.shader_stages & GsEnableMask) == 0 ||
	    (state.shader_stages & (HsEnableMask | PassthroughMask)) != 0 ||
	    (state.shader_stages & GsWave32Mask) != 0 ||
	    state.primitive_group_size == 0 ||
	    state.primitive_group_size > MaximumGroupSize ||
	    state.vertex_group_size == 0 || state.vertex_group_size > MaximumGroupSize ||
	    state.primitive_amplification == 0 ||
	    state.max_output_per_subgroup == 0 ||
	    state.max_output_per_subgroup > MaximumOutput ||
	    state.gs_max_vertices_per_input == 0 ||
	    state.gs_max_vertices_per_input > 0x7ffu ||
	    state.gs_instance_count != 0 || state.esgs_ring_item_size == 0 ||
	    state.ge_user_vgpr_enable != 0 ||
	    state.gs_output_primitive > static_cast<uint32_t>(NativePrimitiveOutput::Triangles)) {
		return false;
	}

	const auto vertex_slots = static_cast<uint64_t>(state.primitive_group_size) *
	                          state.gs_max_vertices_per_input;
	if (vertex_slots != state.max_output_per_subgroup) {
		return false;
	}

	const auto output = static_cast<NativePrimitiveOutput>(state.gs_output_primitive);
	uint32_t primitive_limit_per_input = state.gs_max_vertices_per_input;
	if (output == NativePrimitiveOutput::Lines) {
		if (primitive_limit_per_input < 2) {
			return false;
		}
		primitive_limit_per_input -= 1;
	} else if (output == NativePrimitiveOutput::Triangles) {
		if (primitive_limit_per_input < 3) {
			return false;
		}
		primitive_limit_per_input -= 2;
	}
	if (state.primitive_amplification > primitive_limit_per_input) {
		return false;
	}

	const auto primitive_slots = static_cast<uint64_t>(state.primitive_group_size) *
	                             state.primitive_amplification;
	if (primitive_slots > std::numeric_limits<uint32_t>::max()) {
		return false;
	}

	launch.wave_lane_count        = 64;
	launch.primitive_group_limit  = state.primitive_group_size;
	launch.vertex_group_limit     = state.vertex_group_size;
	launch.output_vertex_slots    = state.max_output_per_subgroup;
	launch.output_primitive_slots = static_cast<uint32_t>(primitive_slots);
	launch.output_primitive       = output;
	return true;
}

// Point-list GS input: one vertex per input primitive. Both measured Astro
// shapes are admitted -- the auto draw of one vertex and one instance, and the
// indexed draw of one index with 512 explicit instances. GE_CNTL's
// multiple-instances-per-wave bit decides whether instances may share a
// subgroup; without it a subgroup never crosses an instance boundary. Counts
// are live per-wave values and must satisfy the SGPR3 packing contract.
[[nodiscard]] constexpr bool TryPlanPointListGsSubgroups(const ClassicGeometryReplayLaunch& launch,
                                                         uint32_t index_count,
                                                         uint32_t instance_count,
                                                         bool     multiple_instances_per_wave,
                                                         GsSubgroupLaunchPlan& plan) {
	plan = {};
	if (index_count == 0 || instance_count == 0 || launch.wave_lane_count == 0 ||
	    launch.primitive_group_limit == 0 || launch.vertex_group_limit == 0) {
		return false;
	}

	// A point-list subgroup consumes one ES vertex per GS primitive, so both
	// capacity limits bound the same per-subgroup count.
	const uint32_t subgroup_primitive_limit =
	    launch.primitive_group_limit < launch.vertex_group_limit ? launch.primitive_group_limit
	                                                             : launch.vertex_group_limit;

	uint64_t subgroup_count       = 0;
	uint32_t full_primitives      = 0;
	uint32_t tail_primitives      = 0;
	if (!multiple_instances_per_wave && instance_count > 1) {
		// Subgroups cannot cross instance boundaries: each instance launches its
		// own subgroups over its index_count primitives.
		if (index_count > subgroup_primitive_limit) {
			return false; // splitting a single instance is not yet measured
		}
		subgroup_count  = instance_count;
		full_primitives = index_count;
		tail_primitives = index_count;
	} else {
		const uint64_t total_primitives =
		    static_cast<uint64_t>(index_count) * instance_count;
		full_primitives = subgroup_primitive_limit;
		if (total_primitives <= full_primitives) {
			subgroup_count  = 1;
			full_primitives = static_cast<uint32_t>(total_primitives);
			tail_primitives = full_primitives;
		} else {
			subgroup_count = (total_primitives + full_primitives - 1) / full_primitives;
			const auto remainder = static_cast<uint32_t>(total_primitives % full_primitives);
			tail_primitives      = remainder == 0 ? full_primitives : remainder;
		}
	}
	if (subgroup_count == 0 || subgroup_count > std::numeric_limits<uint32_t>::max()) {
		return false;
	}

	// Every subgroup must satisfy the SGPR3 launch packing for its owning wave
	// and fit the merged wave allocation.
	const uint32_t waves =
	    (full_primitives + launch.wave_lane_count - 1) / launch.wave_lane_count;
	uint32_t packed = 0;
	if (!TryPackGsWaveLaunch(full_primitives, full_primitives, 0, waves, packed) ||
	    !TryPackGsWaveLaunch(tail_primitives, tail_primitives, 0, waves, packed)) {
		return false;
	}

	plan.subgroup_count               = static_cast<uint32_t>(subgroup_count);
	plan.primitives_per_full_subgroup = full_primitives;
	plan.tail_subgroup_primitives     = tail_primitives;
	plan.waves_per_subgroup           = waves;
	return true;
}

// Classic export-ES epilogues tail-call the GS copy shader through
// s_setpc_b64 s[6:7]. Replay-as-compute merges both programs into one blob:
// the tail call becomes s_nop so execution falls through into the appended GS
// words. Both encodings are architectural GFX10 opcodes, not captured bytes.
inline constexpr uint32_t EsGsMergeSetpcWord = 0xBE802006u; // s_setpc_b64 s[6:7]
inline constexpr uint32_t EsGsMergeBarrierWord = 0xBF8A0000u; // s_barrier

// The ES span must be the live ES program ending in its terminal
// s_setpc_b64 s[6:7]; anything else is rejected rather than guessed at.
[[nodiscard]] inline bool TryMergeEsGsForReplay(const uint32_t* es_words, size_t es_count,
                                                const uint32_t* gs_words, size_t gs_count,
                                                std::vector<uint32_t>& merged) {
	merged.clear();
	if (es_words == nullptr || es_count == 0 || gs_words == nullptr || gs_count == 0 ||
	    es_words[es_count - 1] != EsGsMergeSetpcWord) {
		return false;
	}
	merged.reserve(es_count + gs_count);
	merged.assign(es_words, es_words + es_count);
	// Native ES->GS handoff has implicit LDS ordering.  Replay executes both
	// stages in one compute workgroup, so synthesize the equivalent barrier at
	// the tail-call boundary before the appended GS reads the ring.
	merged.back() = EsGsMergeBarrierWord;
	merged.insert(merged.end(), gs_words, gs_words + gs_count);
	return true;
}

} // namespace Libs::Graphics
