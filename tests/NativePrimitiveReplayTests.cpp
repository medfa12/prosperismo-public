// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

#include "graphics/host_gpu/renderer/nativePrimitiveReplay.h"

#include <cstdio>
#include <cstdlib>

using Libs::Graphics::ClassicGeometryReplayLaunch;
using Libs::Graphics::GsSubgroupLaunchPlan;
using Libs::Graphics::NativePrimitiveLaunchState;
using Libs::Graphics::NativePrimitiveIndices;
using Libs::Graphics::NativePrimitiveOutput;
using Libs::Graphics::OwnsGsAllocation;
using Libs::Graphics::TryPackGsWaveLaunch;
using Libs::Graphics::TryPackPrimitiveConnectivity;
using Libs::Graphics::TryCreateClassicGeometryReplayLaunch;
using Libs::Graphics::TryPlanPointListGsSubgroups;
using Libs::Graphics::UnpackPrimitiveConnectivity;

namespace {

void Check(bool condition, const char* message) {
	if (!condition) {
		std::fprintf(stderr, "NativePrimitiveReplayTests: %s\n", message);
		std::exit(1);
	}
}

NativePrimitiveLaunchState CapturedClassicGsState() {
	return {
	    .shader_stages             = 0x00002030,
	    .primitive_group_size      = 3,
	    .vertex_group_size         = 24,
	    .primitive_amplification   = 70,
	    .max_output_per_subgroup   = 216,
	    .gs_max_vertices_per_input = 72,
	    .gs_output_primitive       = 2,
	    .gs_instance_count         = 0,
	    .esgs_ring_item_size       = 4,
	    .ge_user_vgpr_enable       = 0,
	};
}

} // namespace

int main() {
	ClassicGeometryReplayLaunch launch {};
	auto state = CapturedClassicGsState();
	Check(TryCreateClassicGeometryReplayLaunch(state, launch),
	      "captured classic-GS launch was rejected");
	Check(launch.wave_lane_count == 64 && launch.primitive_group_limit == 3 &&
	          launch.vertex_group_limit == 24 && launch.output_vertex_slots == 216 &&
	          launch.output_primitive_slots == 210 &&
	          launch.output_primitive == NativePrimitiveOutput::Triangles,
	      "captured launch was decoded incorrectly");
	Check(launch.vertex_group_limit == 24,
	      "GE_CNTL's gfx10.1 minimum vertex clamp was not retained as a limit");

	uint32_t packed_s3 = 0;
	Check(TryPackGsWaveLaunch(19, 19, 0, 4, packed_s3) && packed_s3 == 0x40001313u,
	      "wave-zero GS launch was packed incorrectly");
	Check(TryPackGsWaveLaunch(19, 19, 2, 4, packed_s3) && packed_s3 == 0x42001313u,
	      "nonzero-wave GS launch was packed incorrectly");
	Check(!TryPackGsWaveLaunch(256, 1, 0, 4, packed_s3) &&
	          !TryPackGsWaveLaunch(1, 256, 0, 4, packed_s3) &&
	          !TryPackGsWaveLaunch(1, 1, 4, 4, packed_s3) &&
	          !TryPackGsWaveLaunch(1, 1, 0, 0, packed_s3),
	      "out-of-range GS launch fields were admitted");
	Check(OwnsGsAllocation(0) && !OwnsGsAllocation(1) && !OwnsGsAllocation(3),
	      "GS allocation ownership was not restricted to wave zero");

	uint32_t packed_primitive = 0;
	Check(TryPackPrimitiveConnectivity({.vertex0 = 0, .vertex1 = 2, .vertex2 = 1}, 3,
	                                   packed_primitive) &&
	          packed_primitive == 0x00100800u,
	      "target-20 triangle connectivity was packed incorrectly");
	const auto unpacked = UnpackPrimitiveConnectivity(packed_primitive);
	Check(unpacked.vertex0 == 0 && unpacked.vertex1 == 2 && unpacked.vertex2 == 1,
	      "target-20 triangle connectivity did not round-trip");
	Check(TryPackPrimitiveConnectivity(
	          {.vertex0 = 1023, .vertex1 = 1022, .vertex2 = 1021}, 1024, packed_primitive),
	      "valid ten-bit target-20 boundary was rejected");
	Check(!TryPackPrimitiveConnectivity({.vertex0 = 3, .vertex1 = 0, .vertex2 = 1}, 3,
	                                    packed_primitive) &&
	          !TryPackPrimitiveConnectivity({.vertex0 = 1024, .vertex1 = 0, .vertex2 = 1}, 2048,
	                                        packed_primitive),
	      "invalid target-20 connectivity was admitted");

	auto reject = [&](const NativePrimitiveLaunchState& candidate, const char* message) {
		ClassicGeometryReplayLaunch rejected {};
		Check(!TryCreateClassicGeometryReplayLaunch(candidate, rejected), message);
	};

	auto candidate = state;
	candidate.shader_stages &= ~0x20u;
	reject(candidate, "draw without GS_EN was admitted");
	candidate = state;
	candidate.shader_stages |= 0x4u;
	reject(candidate, "tessellated launch was admitted without HS semantics");
	candidate = state;
	candidate.shader_stages |= 0x02000000;
	reject(candidate, "NGG passthrough was admitted as classic GS");
	candidate = state;
	candidate.shader_stages |= 0x00400000;
	reject(candidate, "wave32 launch was admitted to the wave64 contract");
	candidate = state;
	candidate.max_output_per_subgroup--;
	reject(candidate, "inconsistent output vertex budget was admitted");
	candidate = state;
	candidate.primitive_amplification++;
	reject(candidate, "triangle-strip primitive budget overflow was admitted");
	candidate = state;
	candidate.gs_instance_count = 1;
	reject(candidate, "GS instancing was admitted without launch semantics");
	candidate = state;
	candidate.ge_user_vgpr_enable = 1;
	reject(candidate, "user launch VGPRs were admitted without ABI semantics");
	candidate = state;
	candidate.esgs_ring_item_size = 0;
	reject(candidate, "missing ES/GS ring contract was admitted");
	candidate = state;
	candidate.vertex_group_size = 257;
	reject(candidate, "out-of-range Sony GE_CNTL vertex group was admitted");
	candidate = state;
	candidate.gs_output_primitive = 3;
	reject(candidate, "unsupported rectangle output was admitted");

	candidate = state;
	candidate.gs_output_primitive = 1;
	candidate.primitive_amplification = 71;
	Check(TryCreateClassicGeometryReplayLaunch(candidate, launch) &&
	          launch.output_primitive == NativePrimitiveOutput::Lines &&
	          launch.output_primitive_slots == 213,
	      "general line-strip ceiling was not admitted");
	candidate.gs_output_primitive = 0;
	candidate.primitive_amplification = 72;
	Check(TryCreateClassicGeometryReplayLaunch(candidate, launch) &&
	          launch.output_primitive == NativePrimitiveOutput::Points &&
	          launch.output_primitive_slots == 216,
	      "general point-list ceiling was not admitted");

	// Both measured Astro launch shapes for the captured es=0x500704F00 /
	// gs=0x500705600 pair: GE_CNTL=0x00003003 (multiple instances per wave) with
	// prim_group=3.  Shape one is the auto point-list draw of one vertex and one
	// instance; shape two is the indexed point-list draw of one 16-bit index and
	// 512 explicit instances.
	ClassicGeometryReplayLaunch captured_launch {};
	Check(TryCreateClassicGeometryReplayLaunch(CapturedClassicGsState(), captured_launch),
	      "captured launch state was rejected before subgroup planning");
	GsSubgroupLaunchPlan plan {};
	Check(TryPlanPointListGsSubgroups(captured_launch, 1, 1, true, plan) &&
	          plan.subgroup_count == 1 && plan.primitives_per_full_subgroup == 1 &&
	          plan.tail_subgroup_primitives == 1 && plan.waves_per_subgroup == 1,
	      "measured 1x1 auto point-list shape was not planned as one live primitive");
	Check(TryPlanPointListGsSubgroups(captured_launch, 1, 512, true, plan) &&
	          plan.subgroup_count == 171 && plan.primitives_per_full_subgroup == 3 &&
	          plan.tail_subgroup_primitives == 2 && plan.waves_per_subgroup == 1,
	      "measured 1x512 indexed point-list shape was not packed three primitives "
	      "per subgroup with a two-primitive tail");
	Check(TryPlanPointListGsSubgroups(captured_launch, 1, 512, false, plan) &&
	          plan.subgroup_count == 512 && plan.primitives_per_full_subgroup == 1 &&
	          plan.tail_subgroup_primitives == 1 && plan.waves_per_subgroup == 1,
	      "instance-bounded subgroups were not planned when packing is disabled");
	Check(!TryPlanPointListGsSubgroups(captured_launch, 0, 1, true, plan) &&
	          !TryPlanPointListGsSubgroups(captured_launch, 1, 0, true, plan),
	      "empty point-list draw shapes were admitted");
	Check(!TryPlanPointListGsSubgroups(captured_launch, 4, 512, false, plan),
	      "an unmeasured split of a single instance across subgroups was admitted");
	ClassicGeometryReplayLaunch zero_capacity {};
	Check(!TryPlanPointListGsSubgroups(zero_capacity, 1, 1, true, plan),
	      "a launch without capacity limits was admitted");

	std::puts("NativePrimitiveReplayTests: ok");
	return 0;
}
