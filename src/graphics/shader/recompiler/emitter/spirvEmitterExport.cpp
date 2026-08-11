#include "graphics/shader/recompiler/emitter/spirvEmitterInternal.h"

namespace Libs::Graphics::ShaderRecompiler::Spirv::Emitter {

static uint8_t MrtOutputMode(const EmitterState& state, const IR::Instruction& inst) {
	if (inst.export_info.kind != IR::ExportTargetKind::Mrt || state.pixel_input_info == nullptr ||
	    inst.export_info.index >= std::size(state.pixel_input_info->target_output_mode)) {
		return 0;
	}
	return state.pixel_input_info->target_output_mode[inst.export_info.index];
}

uint32_t EmitExportComponentF32(EmitterState& state, const IR::Instruction& inst,
                                uint32_t component) {
	const bool enabled = ((inst.export_info.en >> component) & 1u) != 0;
	if (!enabled || component >= inst.src_count || component >= 4u) {
		return ConstantF32(state, component == 3u ? 0x3f800000u : 0u);
	}
	return EmitFloatLoad(state, inst.src[component]);
}

uint32_t EmitExportVec4F32(EmitterState& state, const IR::Instruction& inst) {
	if (inst.export_info.compr) {
		const auto output_mode   = MrtOutputMode(state, inst);
		uint32_t   components[4] = {
		    ConstantF32(state, 0u),
		    ConstantF32(state, 0u),
		    ConstantF32(state, 0u),
		    ConstantF32(state, 0x3f800000u),
		};
		for (uint32_t pair_index = 0; pair_index < 2u && pair_index < inst.src_count;
		     pair_index++) {
			const auto raw      = EmitValueLoad(state, inst.src[pair_index]);
			uint32_t   unpacked = 0;
			if (output_mode != 5u && output_mode != 6u) {
				unpacked = state.builder.AllocateId();
				state.builder.AddFunction({OpExtInst, state.vec2_float_type, unpacked,
				                           state.glsl_std450, GlslUnpackHalf2x16, raw});
			}
			uint32_t signed_raw = 0;
			if (output_mode == 6u) {
				signed_raw = state.builder.AllocateId();
				state.builder.AddFunction({OpBitcast, state.int_type, signed_raw, raw});
			}
			for (uint32_t lane = 0; lane < 2u; lane++) {
				const auto component = pair_index * 2u + lane;
				if (((inst.export_info.en >> component) & 1u) == 0) {
					continue;
				}
				if (output_mode == 5u || output_mode == 6u) {
					const auto extracted  = state.builder.AllocateId();
					const auto converted  = state.builder.AllocateId();
					const auto normalized = state.builder.AllocateId();
					if (output_mode == 5u) {
						state.builder.AddFunction({OpBitFieldUExtract, state.uint_type, extracted,
						                           raw, ConstantU32(state, lane * 16u),
						                           ConstantU32(state, 16u)});
						state.builder.AddFunction(
						    {OpConvertUToF, state.float_type, converted, extracted});
						state.builder.AddFunction({OpFDiv, state.float_type, normalized, converted,
						                           ConstantF32Value(state, 65535.0f)});
						components[component] = normalized;
					} else {
						state.builder.AddFunction({OpBitFieldSExtract, state.int_type, extracted,
						                           signed_raw, ConstantU32(state, lane * 16u),
						                           ConstantU32(state, 16u)});
						state.builder.AddFunction(
						    {OpConvertSToF, state.float_type, converted, extracted});
						state.builder.AddFunction({OpFDiv, state.float_type, normalized, converted,
						                           ConstantF32Value(state, 32767.0f)});
						components[component] = state.builder.AllocateId();
						state.builder.AddFunction(
						    {OpExtInst, state.float_type, components[component], state.glsl_std450,
						     GlslFMax, normalized, ConstantF32Value(state, -1.0f)});
					}
				} else {
					components[component] = state.builder.AllocateId();
					state.builder.AddFunction({OpCompositeExtract, state.float_type,
					                           components[component], unpacked, lane});
				}
			}
		}
		const auto vec = state.builder.AllocateId();
		state.builder.AddFunction({OpCompositeConstruct, state.vec4_float_type, vec, components[0],
		                           components[1], components[2], components[3]});
		return vec;
	}

	const auto x   = EmitExportComponentF32(state, inst, 0);
	const auto y   = EmitExportComponentF32(state, inst, 1);
	const auto z   = EmitExportComponentF32(state, inst, 2);
	const auto w   = EmitExportComponentF32(state, inst, 3);
	const auto vec = state.builder.AllocateId();
	state.builder.AddFunction({OpCompositeConstruct, state.vec4_float_type, vec, x, y, z, w});
	return vec;
}

uint32_t EmitExportComponentU32(EmitterState& state, const IR::Instruction& inst,
                                uint32_t component) {
	const bool enabled = ((inst.export_info.en >> component) & 1u) != 0;
	if (!enabled || component >= inst.src_count || component >= 4u) {
		return ConstantU32(state, component == 3u ? 1u : 0u);
	}
	return EmitValueLoad(state, inst.src[component]);
}

uint32_t EmitExportVec4U32(EmitterState& state, const IR::Instruction& inst) {
	uint32_t components[4] = {
	    ConstantU32(state, 0u),
	    ConstantU32(state, 0u),
	    ConstantU32(state, 0u),
	    ConstantU32(state, 1u),
	};

	if (inst.export_info.compr) {
		for (uint32_t pair_index = 0; pair_index < 2u && pair_index < inst.src_count;
		     pair_index++) {
			const auto raw = EmitValueLoad(state, inst.src[pair_index]);
			for (uint32_t lane = 0; lane < 2u; lane++) {
				const auto component = pair_index * 2u + lane;
				if (((inst.export_info.en >> component) & 1u) == 0) {
					continue;
				}
				components[component] = state.builder.AllocateId();
				state.builder.AddFunction(
				    {OpBitFieldUExtract, state.uint_type, components[component], raw,
				     ConstantU32(state, lane * 16u), ConstantU32(state, 16u)});
			}
		}
	} else {
		for (uint32_t component = 0; component < 4u; component++) {
			components[component] = EmitExportComponentU32(state, inst, component);
		}
	}

	const auto vec = state.builder.AllocateId();
	state.builder.AddFunction({OpCompositeConstruct, state.vec4_uint_type, vec, components[0],
	                           components[1], components[2], components[3]});
	return vec;
}

uint32_t EmitExportVec4I32(EmitterState& state, const IR::Instruction& inst) {
	uint32_t components[4] = {
	    ConstantI32(state, 0),
	    ConstantI32(state, 0),
	    ConstantI32(state, 0),
	    ConstantI32(state, 1),
	};

	if (inst.export_info.compr) {
		for (uint32_t pair_index = 0; pair_index < 2u && pair_index < inst.src_count;
		     pair_index++) {
			const auto raw        = EmitValueLoad(state, inst.src[pair_index]);
			const auto signed_raw = state.builder.AllocateId();
			state.builder.AddFunction({OpBitcast, state.int_type, signed_raw, raw});
			for (uint32_t lane = 0; lane < 2u; lane++) {
				const auto component = pair_index * 2u + lane;
				if (((inst.export_info.en >> component) & 1u) == 0) {
					continue;
				}
				components[component] = state.builder.AllocateId();
				state.builder.AddFunction(
				    {OpBitFieldSExtract, state.int_type, components[component], signed_raw,
				     ConstantU32(state, lane * 16u), ConstantU32(state, 16u)});
			}
		}
	} else {
		for (uint32_t component = 0; component < 4u; component++) {
			const bool enabled = ((inst.export_info.en >> component) & 1u) != 0;
			if (!enabled || component >= inst.src_count) {
				continue;
			}
			const auto raw        = EmitValueLoad(state, inst.src[component]);
			components[component] = state.builder.AllocateId();
			state.builder.AddFunction({OpBitcast, state.int_type, components[component], raw});
		}
	}

	const auto vec = state.builder.AllocateId();
	state.builder.AddFunction({OpCompositeConstruct, state.vec4_int_type, vec, components[0],
	                           components[1], components[2], components[3]});
	return vec;
}

static bool MrtUsesUintOutput(const EmitterState& state, const IR::Instruction& inst) {
	return MrtOutputMode(state, inst) == 7u;
}

static bool MrtUsesSintOutput(const EmitterState& state, const IR::Instruction& inst) {
	return MrtOutputMode(state, inst) == 8u;
}

uint32_t ApplyMrtExportMapping(EmitterState& state, const IR::Instruction& inst, uint32_t value,
                               uint32_t vector_type) {
	if (inst.export_info.kind != IR::ExportTargetKind::Mrt || state.pixel_input_info == nullptr ||
	    inst.export_info.index >= state.pixel_input_info->target_export_mapping.size()) {
		return value;
	}

	const auto mapping = state.pixel_input_info->target_export_mapping[inst.export_info.index];
	if (mapping.IsIdentity()) {
		return value;
	}

	const auto mapped = state.builder.AllocateId();
	state.builder.AddFunction({OpVectorShuffle, vector_type, mapped, value, value, mapping.Map(0),
	                           mapping.Map(1), mapping.Map(2), mapping.Map(3)});
	return mapped;
}

static void EmitAlphaToMaskDither(EmitterState& state, uint32_t exported_value) {
	if (state.stage != ShaderType::Pixel || state.pixel_input_info == nullptr ||
	    !state.pixel_input_info->HasAlphaToMaskDither() || state.sample_mask_variable == 0) {
		return;
	}

	const auto frag_coord = InputVariableForKind(state, IR::StageInputKind::FragCoord);
	if (frag_coord == 0) {
		return;
	}

	uint32_t parity[2] {};
	for (uint32_t component = 0; component < 2u; component++) {
		const auto pointer = state.builder.AllocateId();
		const auto value   = state.builder.AllocateId();
		const auto floored = state.builder.AllocateId();
		const auto pixel   = state.builder.AllocateId();
		parity[component]  = state.builder.AllocateId();
		state.builder.AddFunction({OpAccessChain, state.ptr_input_float, pointer, frag_coord,
		                           ConstantU32(state, component)});
		state.builder.AddFunction({OpLoad, state.float_type, value, pointer});
		state.builder.AddFunction(
		    {OpExtInst, state.float_type, floored, state.glsl_std450, GlslFloor, value});
		state.builder.AddFunction({OpConvertFToU, state.uint_type, pixel, floored});
		state.builder.AddFunction(
		    {OpBitwiseAnd, state.uint_type, parity[component], pixel, ConstantU32(state, 1u)});
	}

	const auto y_bit    = state.builder.AllocateId();
	const auto quadrant = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpShiftLeftLogical, state.uint_type, y_bit, parity[1], ConstantU32(state, 1u)});
	state.builder.AddFunction({OpBitwiseOr, state.uint_type, quadrant, parity[0], y_bit});

	const uint32_t thresholds[4] = {
	    state.pixel_input_info->alpha_to_mask_top_left_threshold,
	    state.pixel_input_info->alpha_to_mask_top_right_threshold,
	    state.pixel_input_info->alpha_to_mask_bottom_left_threshold,
	    state.pixel_input_info->alpha_to_mask_bottom_right_threshold,
	};
	const auto right_top        = state.builder.AllocateId();
	const auto right_bottom     = state.builder.AllocateId();
	const auto bottom           = state.builder.AllocateId();
	const auto top_threshold    = state.builder.AllocateId();
	const auto bottom_threshold = state.builder.AllocateId();
	const auto threshold        = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpIEqual, state.bool_type, right_top, quadrant, ConstantU32(state, 1u)});
	state.builder.AddFunction(
	    {OpIEqual, state.bool_type, right_bottom, quadrant, ConstantU32(state, 3u)});
	state.builder.AddFunction(
	    {OpUGreaterThanEqual, state.bool_type, bottom, quadrant, ConstantU32(state, 2u)});
	state.builder.AddFunction({OpSelect, state.uint_type, top_threshold, right_top,
	                           ConstantU32(state, thresholds[1]),
	                           ConstantU32(state, thresholds[0])});
	state.builder.AddFunction({OpSelect, state.uint_type, bottom_threshold, right_bottom,
	                           ConstantU32(state, thresholds[3]),
	                           ConstantU32(state, thresholds[2])});
	state.builder.AddFunction(
	    {OpSelect, state.uint_type, threshold, bottom, bottom_threshold, top_threshold});

	const auto alpha = state.builder.AllocateId();
	state.builder.AddFunction({OpCompositeExtract, state.float_type, alpha, exported_value, 3u});
	const auto clamped = state.builder.AllocateId();
	state.builder.AddFunction({OpExtInst, state.float_type, clamped, state.glsl_std450, GlslFClamp,
	                           alpha, ConstantF32Value(state, 0.0f),
	                           ConstantF32Value(state, 1.0f)});
	const auto scaled = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpFMul, state.float_type, scaled, clamped,
	     ConstantF32Value(
	         state, 8.0f * static_cast<float>(state.pixel_input_info->alpha_to_mask_num_samples))});
	const auto threshold_f = state.builder.AllocateId();
	state.builder.AddFunction({OpConvertUToF, state.float_type, threshold_f, threshold});
	const auto threshold_bias = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpFMul, state.float_type, threshold_bias, threshold_f, ConstantF32Value(state, 2.0f)});
	const auto round_bias = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpFAdd, state.float_type, round_bias, threshold_bias,
	     ConstantF32Value(state,
	                      static_cast<float>(state.pixel_input_info->alpha_to_mask_round_mode))});
	const auto rounded = state.builder.AllocateId();
	state.builder.AddFunction({OpFAdd, state.float_type, rounded, scaled, round_bias});
	const auto divided = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpFDiv, state.float_type, divided, rounded, ConstantF32Value(state, 8.0f)});
	const auto floored = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpExtInst, state.float_type, floored, state.glsl_std450, GlslFloor, divided});
	const auto count = state.builder.AllocateId();
	state.builder.AddFunction({OpConvertFToU, state.uint_type, count, floored});
	const auto over          = state.builder.AllocateId();
	const auto clamped_count = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpUGreaterThan, state.bool_type, over, count,
	     ConstantU32(state, state.pixel_input_info->alpha_to_mask_num_samples)});
	state.builder.AddFunction(
	    {OpSelect, state.uint_type, clamped_count, over,
	     ConstantU32(state, state.pixel_input_info->alpha_to_mask_num_samples), count});
	const auto shifted = state.builder.AllocateId();
	const auto mask    = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpShiftLeftLogical, state.uint_type, shifted, ConstantU32(state, 1u), clamped_count});
	state.builder.AddFunction({OpISub, state.uint_type, mask, shifted, ConstantU32(state, 1u)});

	const auto output = state.builder.AllocateId();
	state.builder.AddFunction({OpAccessChain, state.ptr_output_int, output,
	                           state.sample_mask_variable, ConstantU32(state, 0)});
	uint32_t combined = mask;
	if (const auto input = InputVariableForKind(state, IR::StageInputKind::SampleMask);
	    input != 0) {
		const auto input_ptr    = state.builder.AllocateId();
		const auto input_signed = state.builder.AllocateId();
		const auto input_mask   = state.builder.AllocateId();
		state.builder.AddFunction(
		    {OpAccessChain, state.ptr_input_int, input_ptr, input, ConstantU32(state, 0)});
		state.builder.AddFunction({OpLoad, state.int_type, input_signed, input_ptr});
		state.builder.AddFunction({OpBitcast, state.uint_type, input_mask, input_signed});
		const auto input_combined = state.builder.AllocateId();
		state.builder.AddFunction(
		    {OpBitwiseAnd, state.uint_type, input_combined, combined, input_mask});
		combined = input_combined;
	}
	const auto current_signed   = state.builder.AllocateId();
	const auto current_mask     = state.builder.AllocateId();
	const auto current_combined = state.builder.AllocateId();
	state.builder.AddFunction({OpLoad, state.int_type, current_signed, output});
	state.builder.AddFunction({OpBitcast, state.uint_type, current_mask, current_signed});
	state.builder.AddFunction(
	    {OpBitwiseAnd, state.uint_type, current_combined, combined, current_mask});
	combined                   = current_combined;
	const auto signed_combined = state.builder.AllocateId();
	state.builder.AddFunction({OpBitcast, state.int_type, signed_combined, combined});
	state.builder.AddFunction({OpStore, output, signed_combined});
}

bool ExportWritesData(const IR::Instruction& inst) {
	switch (inst.export_info.kind) {
		case IR::ExportTargetKind::Null:
		case IR::ExportTargetKind::Primitive: return false;
		case IR::ExportTargetKind::MrtZ: return (inst.export_info.en & 0x5u) != 0;
		default: return inst.export_info.en != 0;
	}
}

void EmitMrtZExport(EmitterState& state, const IR::Instruction& inst) {
	if ((inst.export_info.en & 0x1u) != 0 && state.depth_variable != 0) {
		state.builder.AddFunction(
		    {OpStore, state.depth_variable, EmitExportComponentF32(state, inst, 0)});
	}

	if ((inst.export_info.en & 0x4u) != 0 && state.sample_mask_variable != 0) {
		const auto raw =
		    inst.src_count > 2u ? EmitValueLoad(state, inst.src[2]) : ConstantU32(state, 0);
		const auto mask = state.builder.AllocateId();
		const auto ptr  = state.builder.AllocateId();
		state.builder.AddFunction({OpBitcast, state.int_type, mask, raw});
		state.builder.AddFunction({OpAccessChain, state.ptr_output_int, ptr,
		                           state.sample_mask_variable, ConstantU32(state, 0)});
		const auto current  = state.builder.AllocateId();
		const auto combined = state.builder.AllocateId();
		state.builder.AddFunction({OpLoad, state.int_type, current, ptr});
		state.builder.AddFunction({OpBitwiseAnd, state.int_type, combined, mask, current});
		state.builder.AddFunction({OpStore, ptr, combined});
	}
}

void EmitExport(EmitterState& state, const IR::Instruction& inst) {
	if (GeometryReplayActive(state)) {
		EmitGeometryReplayExport(state, inst);
		return;
	}

	if (inst.export_info.kind == IR::ExportTargetKind::Null ||
	    inst.export_info.kind == IR::ExportTargetKind::Primitive) {
		return;
	}

	if (!ExportWritesData(inst)) {
		return;
	}

	if (inst.export_info.kind == IR::ExportTargetKind::MrtZ) {
		EmitMrtZExport(state, inst);
		return;
	}

	const auto variable = OutputVariableForExport(state, inst.export_info);
	if (variable == 0) {
		return;
	}

	const auto uint_output = MrtUsesUintOutput(state, inst);
	const auto sint_output = MrtUsesSintOutput(state, inst);
	const auto vector_type = uint_output   ? state.vec4_uint_type
	                         : sint_output ? state.vec4_int_type
	                                       : state.vec4_float_type;
	const auto raw_value   = uint_output   ? EmitExportVec4U32(state, inst)
	                         : sint_output ? EmitExportVec4I32(state, inst)
	                                       : EmitExportVec4F32(state, inst);
	const auto value       = ApplyMrtExportMapping(state, inst, raw_value, vector_type);
	if (inst.export_info.kind == IR::ExportTargetKind::Position) {
		const auto pointer = state.builder.AllocateId();
		state.builder.AddFunction(
		    {OpAccessChain, state.ptr_output_vec4_float, pointer, variable, ConstantU32(state, 0)});
		state.builder.AddFunction({OpStore, pointer, value});
		return;
	}

	state.builder.AddFunction({OpStore, variable, value});
	if (inst.export_info.kind == IR::ExportTargetKind::Mrt && inst.export_info.index == 0u &&
	    !uint_output && !sint_output) {
		EmitAlphaToMaskDither(state, value);
	}
}

bool ExportUsesPixelValidMask(const EmitterState& state, const IR::Instruction& inst) {
	return state.stage == ShaderType::Pixel && inst.export_info.vm && state.needs_pixel_valid_mask;
}

bool GeometryReplayActive(const EmitterState& state) {
	return state.stage == ShaderType::Compute && state.program.geometry_replay &&
	       state.geometry_replay_variable != 0;
}

static IR::GeometryReplayLayout GeometryReplayLayoutFor(const EmitterState& state) {
	return {state.program.geometry_replay_vertex_slots,
	        state.program.geometry_replay_primitive_slots,
	        IR::GeometryReplayParameterCount(state.program)};
}

static uint32_t EmitReplayBinaryU32(EmitterState& state, uint32_t opcode, uint32_t a, uint32_t b) {
	const auto result = state.builder.AllocateId();
	state.builder.AddFunction({opcode, state.uint_type, result, a, b});
	return result;
}

static void EmitReplayStore(EmitterState& state, uint32_t index, uint32_t value) {
	const auto pointer = state.builder.AllocateId();
	state.builder.AddFunction({OpAccessChain, state.ptr_storage_buffer_uint, pointer,
	                           state.geometry_replay_variable, ConstantU32(state, 0), index});
	state.builder.AddFunction({OpStore, pointer, value});
}

static uint32_t EmitReplayLoad(EmitterState& state, uint32_t index) {
	const auto pointer = state.builder.AllocateId();
	const auto value   = state.builder.AllocateId();
	state.builder.AddFunction({OpAccessChain, state.ptr_storage_buffer_uint, pointer,
	                           state.geometry_replay_variable, ConstantU32(state, 0),
	                           ConstantU32(state, index)});
	state.builder.AddFunction({OpLoad, state.uint_type, value, pointer});
	return value;
}

// Loads a replay-buffer dword at a runtime-computed index.
static uint32_t EmitReplayLoadAt(EmitterState& state, uint32_t index_id) {
	const auto pointer = state.builder.AllocateId();
	const auto value   = state.builder.AllocateId();
	state.builder.AddFunction({OpAccessChain, state.ptr_storage_buffer_uint, pointer,
	                           state.geometry_replay_variable, ConstantU32(state, 0), index_id});
	state.builder.AddFunction({OpLoad, state.uint_type, value, pointer});
	return value;
}

// Dword index of this subgroup's replay block: header + workgroup * stride.
static uint32_t EmitReplayBlockBase(EmitterState& state, const IR::GeometryReplayLayout& layout) {
	const auto wg = EmitInputComponentU32(state, IR::StageInputKind::WorkgroupId, 0);
	const auto scaled =
	    EmitReplayBinaryU32(state, OpIMul, wg, ConstantU32(state, layout.BlockStride()));
	return EmitReplayBinaryU32(state, OpIAdd, scaled,
	                           ConstantU32(state, IR::GeometryReplayLayout::HeaderDwords));
}

static void EmitReplayVec4Store(EmitterState& state, const IR::Instruction& inst, uint32_t base,
                                uint32_t slot_offset) {
	const auto local = EmitLocalInvocationIndex(state);
	const auto slot  = EmitReplayBinaryU32(state, OpIMul, local, ConstantU32(state, 4u));
	auto       index = EmitReplayBinaryU32(state, OpIAdd, base, slot);
	if (slot_offset != 0) {
		index = EmitReplayBinaryU32(state, OpIAdd, index, ConstantU32(state, slot_offset));
	}
	const auto vec = EmitExportVec4F32(state, inst);
	for (uint32_t c = 0; c < 4u; c++) {
		const auto component = state.builder.AllocateId();
		const auto bits      = state.builder.AllocateId();
		state.builder.AddFunction({OpCompositeExtract, state.float_type, component, vec, c});
		state.builder.AddFunction({OpBitcast, state.uint_type, bits, component});
		const auto slot_index =
		    c == 0 ? index : EmitReplayBinaryU32(state, OpIAdd, index, ConstantU32(state, c));
		EmitReplayStore(state, slot_index, bits);
	}
}

void EmitGeometryReplayExport(EmitterState& state, const IR::Instruction& inst) {
	const auto layout = GeometryReplayLayoutFor(state);
	const auto kind   = inst.export_info.kind;
	if (kind == IR::ExportTargetKind::Position) {
		if (inst.export_info.index != 0 || inst.export_info.en == 0) {
			return;
		}
		const auto base = EmitReplayBlockBase(state, layout);
		EmitReplayVec4Store(state, inst, base, layout.PositionOffset());
		return;
	}
	if (kind == IR::ExportTargetKind::Parameter) {
		if (inst.export_info.index >= layout.parameter_count || inst.export_info.en == 0) {
			return;
		}
		const auto base = EmitReplayBlockBase(state, layout);
		EmitReplayVec4Store(state, inst, base, layout.ParameterOffset(inst.export_info.index));
		return;
	}
	if (kind == IR::ExportTargetKind::Primitive) {
		if ((inst.export_info.en & 0x1u) == 0 || inst.src_count == 0) {
			return;
		}
		const auto base  = EmitReplayBlockBase(state, layout);
		const auto local = EmitLocalInvocationIndex(state);
		auto       index = EmitReplayBinaryU32(state, OpIAdd, base, local);
		index =
		    EmitReplayBinaryU32(state, OpIAdd, index, ConstantU32(state, layout.PrimitiveOffset()));
		EmitReplayStore(state, index, EmitValueLoad(state, inst.src[0]));
		return;
	}
}

void EmitSendmsg(EmitterState& state, const IR::Instruction& inst) {
	// Only GS_ALLOC_REQ (message 9) carries replay counts; every other
	// message stays a no-op, as does replay-disabled compilation.
	if (!GeometryReplayActive(state) || inst.src_count < 2 ||
	    inst.src[0].kind != IR::OperandKind::ImmediateU32 || (inst.src[0].imm & 0xfu) != 9u) {
		return;
	}
	const auto layout = GeometryReplayLayoutFor(state);
	const auto local  = EmitLocalInvocationIndex(state);
	const auto first  = state.builder.AllocateId();
	state.builder.AddFunction({OpIEqual, state.bool_type, first, local, ConstantU32(state, 0)});
	const auto store_label = state.builder.AllocateId();
	const auto merge_label = state.builder.AllocateId();
	state.builder.AddFunction({OpSelectionMerge, merge_label, SelectionControlNone});
	state.builder.AddFunction({OpBranchConditional, first, store_label, merge_label});
	state.builder.AddFunction({OpLabel, store_label});
	// m0 = vertex_count | primitive_count << 12 at GS_ALLOC_REQ time.
	const auto m0    = EmitValueLoad(state, inst.src[1]);
	const auto verts = EmitReplayBinaryU32(state, OpBitwiseAnd, m0, ConstantU32(state, 0xfffu));
	const auto shifted =
	    EmitReplayBinaryU32(state, OpShiftRightLogical, m0, ConstantU32(state, 12u));
	const auto prims =
	    EmitReplayBinaryU32(state, OpBitwiseAnd, shifted, ConstantU32(state, 0xfffu));
	const auto base = EmitReplayBlockBase(state, layout);
	const auto counts =
	    EmitReplayBinaryU32(state, OpIAdd, base, ConstantU32(state, layout.CountsOffset()));
	EmitReplayStore(state, counts, verts);
	EmitReplayStore(state, EmitReplayBinaryU32(state, OpIAdd, counts, ConstantU32(state, 1u)),
	                prims);
	state.builder.AddFunction({OpBranch, merge_label});
	state.builder.AddFunction({OpLabel, merge_label});
}

// Seeds the launch contract for a merged ES/GS replay subgroup:
// v0/v5 = ESGS ring offset (thread * 4), v8 = global primitive index (single-index
// instanced draws), s3 = per-wave GS launch word per TryPackGsWaveLaunch.  The
// retained Astro ES writes its ring item through v5; v0 remains initialized for
// the earlier synthetic/native-primitive contract that uses v0.
void EmitGeometryReplayInputRegisters(EmitterState& state) {
	if (!GeometryReplayActive(state)) {
		return;
	}
	const auto local = EmitLocalInvocationIndex(state);

	const auto offset =
	    EmitReplayBinaryU32(state, OpShiftLeftLogical, local, ConstantU32(state, 2u));
	for (const uint32_t vgpr: {0u, 5u}) {
		const auto pointer = PointerForRegister(state, {IR::RegisterFile::Vector, vgpr});
		if (pointer != 0) {
			state.builder.AddFunction({OpStore, pointer, offset});
		}
	}

	const auto total   = EmitReplayLoad(state, 0u);
	const auto full    = EmitReplayLoad(state, 1u);
	const auto wg      = EmitInputComponentU32(state, IR::StageInputKind::WorkgroupId, 0);
	const auto wg_base = EmitReplayBinaryU32(state, OpIMul, wg, full);

	const auto v8_pointer = PointerForRegister(state, {IR::RegisterFile::Vector, 8});
	if (v8_pointer != 0) {
		const auto instance = EmitReplayBinaryU32(state, OpIAdd, wg_base, local);
		state.builder.AddFunction({OpStore, v8_pointer, instance});
	}

	const auto s3_pointer = PointerForRegister(state, {IR::RegisterFile::Scalar, 3});
	if (s3_pointer != 0) {
		const uint32_t wave_size     = state.wave_size != 0 ? state.wave_size : 64u;
		uint32_t       total_threads = 1;
		if (state.compute_input_info != nullptr) {
			total_threads = std::max<uint32_t>(state.compute_input_info->threads_num[0], 1u) *
			                std::max<uint32_t>(state.compute_input_info->threads_num[1], 1u) *
			                std::max<uint32_t>(state.compute_input_info->threads_num[2], 1u);
		}
		const uint32_t wave_count =
		    std::min<uint32_t>((total_threads + wave_size - 1u) / wave_size, 0xfu);
		// Primitives assigned to this subgroup: min(full, total - wg * full).
		const auto remaining = EmitReplayBinaryU32(state, OpISub, total, wg_base);
		const auto prims_sub = state.builder.AllocateId();
		state.builder.AddFunction(
		    {OpExtInst, state.uint_type, prims_sub, state.glsl_std450, GlslUMin, full, remaining});
		// Per-wave residual count clamped to [0, wave_size].
		const auto wave = EmitReplayBinaryU32(state, OpUDiv, local, ConstantU32(state, wave_size));
		const auto wave_start =
		    EmitReplayBinaryU32(state, OpIMul, wave, ConstantU32(state, wave_size));
		const auto has_work = state.builder.AllocateId();
		state.builder.AddFunction(
		    {OpUGreaterThan, state.bool_type, has_work, prims_sub, wave_start});
		const auto residual = EmitReplayBinaryU32(state, OpISub, prims_sub, wave_start);
		const auto capped   = state.builder.AllocateId();
		state.builder.AddFunction({OpExtInst, state.uint_type, capped, state.glsl_std450, GlslUMin,
		                           residual, ConstantU32(state, wave_size)});
		const auto count = state.builder.AllocateId();
		state.builder.AddFunction(
		    {OpSelect, state.uint_type, count, has_work, capped, ConstantU32(state, 0)});
		// s3 = verts[7:0] | prims[15:8] | wave[27:24] | wave_count[31:28].
		const auto prim_bits =
		    EmitReplayBinaryU32(state, OpShiftLeftLogical, count, ConstantU32(state, 8u));
		const auto wave_bits =
		    EmitReplayBinaryU32(state, OpShiftLeftLogical, wave, ConstantU32(state, 24u));
		auto packed = EmitReplayBinaryU32(state, OpBitwiseOr, count, prim_bits);
		packed      = EmitReplayBinaryU32(state, OpBitwiseOr, packed, wave_bits);
		packed =
		    EmitReplayBinaryU32(state, OpBitwiseOr, packed, ConstantU32(state, wave_count << 28u));
		state.builder.AddFunction({OpStore, s3_pointer, packed});
	}
}

// Vertex-stage half of the geometry replay: each vertex of the flat
// triangle-list draw resolves its replay-buffer slot from VertexIndex and
// seeds v0..v3 with the position dwords and v4.. with the parameter dwords,
// which the synthetic passthrough shader then exports normally. A sentinel
// primitive word (0x80000000) decodes to vertex slot 0 for every corner and
// therefore rasterizes as a zero-area triangle.
void EmitGeometryReplayVertexFetch(EmitterState& state) {
	if (state.stage != ShaderType::Vertex || !state.program.geometry_replay ||
	    state.geometry_replay_variable == 0) {
		return;
	}
	const auto layout = GeometryReplayLayoutFor(state);
	const auto vid    = EmitInputScalarU32(state, IR::StageInputKind::VertexIndex);
	const auto prim   = EmitReplayBinaryU32(state, OpUDiv, vid, ConstantU32(state, 3u));
	const auto corner = EmitReplayBinaryU32(state, OpUMod, vid, ConstantU32(state, 3u));
	const auto subgroup =
	    EmitReplayBinaryU32(state, OpUDiv, prim, ConstantU32(state, layout.primitive_slots));
	const auto slot =
	    EmitReplayBinaryU32(state, OpUMod, prim, ConstantU32(state, layout.primitive_slots));
	const auto scaled =
	    EmitReplayBinaryU32(state, OpIMul, subgroup, ConstantU32(state, layout.BlockStride()));
	const auto base = EmitReplayBinaryU32(
	    state, OpIAdd, scaled, ConstantU32(state, IR::GeometryReplayLayout::HeaderDwords));

	auto prim_index =
	    EmitReplayBinaryU32(state, OpIAdd, base, ConstantU32(state, layout.PrimitiveOffset()));
	prim_index      = EmitReplayBinaryU32(state, OpIAdd, prim_index, slot);
	const auto word = EmitReplayLoadAt(state, prim_index);

	// 10-bit vertex slot index for this corner; bit 31 marks a culled slot.
	const auto shift   = EmitReplayBinaryU32(state, OpIMul, corner, ConstantU32(state, 10u));
	const auto shifted = EmitReplayBinaryU32(state, OpShiftRightLogical, word, shift);
	const auto idx = EmitReplayBinaryU32(state, OpBitwiseAnd, shifted, ConstantU32(state, 0x3ffu));
	const auto vertex_dwords = EmitReplayBinaryU32(state, OpIMul, idx, ConstantU32(state, 4u));

	const auto seed_vec4 = [&](uint32_t block_offset, uint32_t first_vgpr) {
		auto index = EmitReplayBinaryU32(state, OpIAdd, base, ConstantU32(state, block_offset));
		index      = EmitReplayBinaryU32(state, OpIAdd, index, vertex_dwords);
		for (uint32_t c = 0; c < 4u; c++) {
			const auto pointer =
			    PointerForRegister(state, {IR::RegisterFile::Vector, first_vgpr + c});
			if (pointer == 0) {
				continue;
			}
			const auto component_index =
			    c == 0 ? index : EmitReplayBinaryU32(state, OpIAdd, index, ConstantU32(state, c));
			state.builder.AddFunction({OpStore, pointer, EmitReplayLoadAt(state, component_index)});
		}
	};

	seed_vec4(layout.PositionOffset(), 0u);
	for (uint32_t p = 0; p < layout.parameter_count; p++) {
		seed_vec4(layout.ParameterOffset(p), 4u + p * 4u);
	}
}

void EmitKillIfBoolFalse(EmitterState& state, uint32_t active) {
	const auto kill_label  = state.builder.AllocateId();
	const auto merge_label = state.builder.AllocateId();
	const auto inactive    = state.builder.AllocateId();
	state.builder.AddFunction({OpLogicalNot, state.bool_type, inactive, active});
	state.builder.AddFunction({OpSelectionMerge, merge_label, SelectionControlNone});
	state.builder.AddFunction({OpBranchConditional, inactive, kill_label, merge_label});
	state.builder.AddFunction({OpLabel, kill_label});
	state.builder.AddFunction({OpKill});
	state.builder.AddFunction({OpLabel, merge_label});
}

void EmitUpdatePixelValidMask(EmitterState& state) {
	if (state.pixel_valid_mask_variable == 0) {
		return;
	}

	const auto active = EmitExecActiveBool(state);
	const auto value  = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpSelect, state.uint_type, value, active, ConstantU32(state, 1), ConstantU32(state, 0)});
	state.builder.AddFunction({OpStore, state.pixel_valid_mask_variable, value});
}

void EmitKillIfPixelValidMaskInactive(EmitterState& state) {
	if (state.pixel_valid_mask_variable == 0) {
		return;
	}

	const auto mask_value = state.builder.AllocateId();
	const auto active     = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpLoad, state.uint_type, mask_value, state.pixel_valid_mask_variable});
	state.builder.AddFunction(
	    {OpINotEqual, state.bool_type, active, mask_value, ConstantU32(state, 0)});
	EmitKillIfBoolFalse(state, active);
}

} // namespace Libs::Graphics::ShaderRecompiler::Spirv::Emitter
