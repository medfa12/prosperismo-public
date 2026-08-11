#include "graphics/shader/recompiler/emitter/spirvEmitterInternal.h"

namespace Libs::Graphics::ShaderRecompiler::Spirv::Emitter {

void EmitMoveU32(EmitterState& state, const IR::Instruction& inst) {
	const auto value = EmitValueLoad(state, inst.src[0]);
	EmitStoreU32(state, inst.dst, value);
}

void EmitSwapU32(EmitterState& state, const IR::Instruction& inst) {
	const auto source_value      = EmitValueLoad(state, inst.src[0]);
	const auto destination_value = EmitValueLoad(state, inst.dst);
	// V_SWAP_B32 is a true register exchange: both old values are loaded before
	// either active-lane write, so aliasing is harmless and inactive lanes retain
	// their prior values through EmitStoreU32.
	EmitStoreU32(state, inst.src[0], destination_value);
	EmitStoreU32(state, inst.dst, source_value);
}

void EmitSwapRelU32(EmitterState& state, const IR::Instruction& inst) {
	if (inst.dst.kind != IR::OperandKind::Register ||
	    inst.dst.reg.file != IR::RegisterFile::Vector || inst.src_count < 2u ||
	    inst.src[0].kind != IR::OperandKind::Register ||
	    inst.src[0].reg.file != IR::RegisterFile::Vector) {
		EmitSwapU32(state, inst);
		return;
	}

	const auto m0         = EmitValueLoad(state, inst.src[1]);
	const auto src_offset = state.builder.AllocateId();
	const auto dst_offset = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpBitwiseAnd, state.uint_type, src_offset, m0, ConstantU32(state, 0x3ffu)});
	const auto shifted_dst = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpShiftRightLogical, state.uint_type, shifted_dst, m0, ConstantU32(state, 16u)});
	state.builder.AddFunction(
	    {OpBitwiseAnd, state.uint_type, dst_offset, shifted_dst, ConstantU32(state, 0x3ffu)});

	// Indexed reads beyond the allocated VGPR range alias VGPR0.  Load both
	// values before any store so overlapping source/destination registers are
	// exchanged atomically from the instruction's point of view.
	const auto source_base          = inst.src[0].reg;
	const auto destination_base     = inst.dst.reg;
	const auto source_fallback      = EmitRegisterLoad(state, {IR::RegisterFile::Vector, 0});
	const auto destination_fallback = EmitRegisterLoad(state, {IR::RegisterFile::Vector, 0});
	uint32_t   source_value         = source_fallback;
	uint32_t   destination_value    = destination_fallback;
	const auto source_limit         = MoveRelRegisterLimit(state);
	const auto destination_limit    = MoveRelRegisterLimit(state);
	for (uint32_t reg = source_base.index; reg < source_limit; reg++) {
		const auto candidate = EmitRegisterLoad(state, {IR::RegisterFile::Vector, reg});
		const auto match     = state.builder.AllocateId();
		const auto next      = state.builder.AllocateId();
		state.builder.AddFunction({OpIEqual, state.bool_type, match, src_offset,
		                           ConstantU32(state, reg - source_base.index)});
		state.builder.AddFunction(
		    {OpSelect, state.uint_type, next, match, candidate, source_value});
		source_value = next;
	}
	for (uint32_t reg = destination_base.index; reg < destination_limit; reg++) {
		const auto candidate = EmitRegisterLoad(state, {IR::RegisterFile::Vector, reg});
		const auto match     = state.builder.AllocateId();
		const auto next      = state.builder.AllocateId();
		state.builder.AddFunction({OpIEqual, state.bool_type, match, dst_offset,
		                           ConstantU32(state, reg - destination_base.index)});
		state.builder.AddFunction(
		    {OpSelect, state.uint_type, next, match, candidate, destination_value});
		destination_value = next;
	}

	const auto exec_active = EmitExecActiveBool(state);
	for (uint32_t reg = source_base.index; reg < source_limit; reg++) {
		const auto pointer = PointerForRegister(state, {IR::RegisterFile::Vector, reg});
		if (pointer == 0) {
			continue;
		}
		const auto old_value = state.builder.AllocateId();
		const auto match     = state.builder.AllocateId();
		const auto write     = state.builder.AllocateId();
		const auto merged    = state.builder.AllocateId();
		state.builder.AddFunction({OpLoad, state.uint_type, old_value, pointer});
		state.builder.AddFunction({OpIEqual, state.bool_type, match, src_offset,
		                           ConstantU32(state, reg - source_base.index)});
		state.builder.AddFunction({OpLogicalAnd, state.bool_type, write, exec_active, match});
		state.builder.AddFunction(
		    {OpSelect, state.uint_type, merged, write, destination_value, old_value});
		state.builder.AddFunction({OpStore, pointer, merged});
	}
	for (uint32_t reg = destination_base.index; reg < destination_limit; reg++) {
		const auto pointer = PointerForRegister(state, {IR::RegisterFile::Vector, reg});
		if (pointer == 0) {
			continue;
		}
		const auto old_value = state.builder.AllocateId();
		const auto match     = state.builder.AllocateId();
		const auto write     = state.builder.AllocateId();
		const auto merged    = state.builder.AllocateId();
		state.builder.AddFunction({OpLoad, state.uint_type, old_value, pointer});
		state.builder.AddFunction({OpIEqual, state.bool_type, match, dst_offset,
		                           ConstantU32(state, reg - destination_base.index)});
		state.builder.AddFunction({OpLogicalAnd, state.bool_type, write, exec_active, match});
		state.builder.AddFunction(
		    {OpSelect, state.uint_type, merged, write, source_value, old_value});
		state.builder.AddFunction({OpStore, pointer, merged});
	}
}

void EmitMoveF32Bits(EmitterState& state, const IR::Instruction& inst) {
	const auto value = EmitFloatLoad(state, inst.src[0]);
	const auto bits  = state.builder.AllocateId();
	state.builder.AddFunction({OpBitcast, state.uint_type, bits, value});
	EmitStoreU32(state, inst.dst, bits);
}

uint32_t MoveRelRegisterLimit(const EmitterState& state) {
	return state.program.allocated_vgpr_count;
}

void EmitMoveRelSourceU32(EmitterState& state, const IR::Instruction& inst) {
	if (inst.src[0].kind != IR::OperandKind::Register ||
	    inst.src[0].reg.file != IR::RegisterFile::Vector) {
		EmitMoveU32(state, inst);
		return;
	}

	const auto base_reg = inst.src[0].reg;
	const auto m0 = inst.src_count > 1u ? EmitValueLoad(state, inst.src[1]) : ConstantU32(state, 0);
	auto       selected = EmitRegisterLoad(state, {IR::RegisterFile::Vector, 0});
	const auto limit    = MoveRelRegisterLimit(state);
	for (uint32_t reg = base_reg.index; reg < limit; reg++) {
		const auto candidate = EmitRegisterLoad(state, {IR::RegisterFile::Vector, reg});
		const auto match     = state.builder.AllocateId();
		const auto next      = state.builder.AllocateId();
		state.builder.AddFunction(
		    {OpIEqual, state.bool_type, match, m0, ConstantU32(state, reg - base_reg.index)});
		state.builder.AddFunction({OpSelect, state.uint_type, next, match, candidate, selected});
		selected = next;
	}
	EmitStoreU32(state, inst.dst, selected);
}

void EmitMoveRelDestU32(EmitterState& state, const IR::Instruction& inst) {
	if (inst.dst.kind != IR::OperandKind::Register ||
	    inst.dst.reg.file != IR::RegisterFile::Vector) {
		EmitMoveU32(state, inst);
		return;
	}

	const auto base_reg = inst.dst.reg;
	const auto value    = EmitValueLoad(state, inst.src[0]);
	const auto m0 = inst.src_count > 1u ? EmitValueLoad(state, inst.src[1]) : ConstantU32(state, 0);
	const auto exec_active = EmitExecActiveBool(state);
	const auto limit       = MoveRelRegisterLimit(state);
	for (uint32_t reg = base_reg.index; reg < limit; reg++) {
		const auto pointer = PointerForRegister(state, {IR::RegisterFile::Vector, reg});
		if (pointer == 0) {
			continue;
		}
		const auto old_value = state.builder.AllocateId();
		const auto match     = state.builder.AllocateId();
		const auto write     = state.builder.AllocateId();
		const auto merged    = state.builder.AllocateId();
		state.builder.AddFunction({OpLoad, state.uint_type, old_value, pointer});
		state.builder.AddFunction(
		    {OpIEqual, state.bool_type, match, m0, ConstantU32(state, reg - base_reg.index)});
		state.builder.AddFunction({OpLogicalAnd, state.bool_type, write, exec_active, match});
		state.builder.AddFunction({OpSelect, state.uint_type, merged, write, value, old_value});
		state.builder.AddFunction({OpStore, pointer, merged});
	}
}

void EmitMoveU64(EmitterState& state, const IR::Instruction& inst) {
	if (state.per_invocation_masks && inst.dst.kind == IR::OperandKind::Register &&
	    IsMaskRegisterFile(inst.dst.reg.file)) {
		EmitPerInvocationMask(state, inst.dst, EmitLaneMaskOperandActiveBool(state, inst.src[0]));
		return;
	}
	const auto low  = EmitSequentialValueLoad(state, inst.src[0], 0);
	const auto high = EmitSequentialValueLoad(state, inst.src[0], 1);
	EmitStoreU32(state, inst.dst, low);
	EmitStoreU32(state, OffsetRegisterOperand(inst.dst, 1), high);
}

uint32_t EmitWqmLaneU32(EmitterState& state, uint32_t src) {
	uint32_t ret = ConstantU32(state, 0);
	for (uint32_t i = 0; i < 8u; i++) {
		const auto mask     = 0x0fu << (i * 4u);
		const auto masked   = state.builder.AllocateId();
		const auto non_zero = state.builder.AllocateId();
		const auto expanded = state.builder.AllocateId();
		const auto combined = state.builder.AllocateId();
		state.builder.AddFunction(
		    {OpBitwiseAnd, state.uint_type, masked, src, ConstantU32(state, mask)});
		state.builder.AddFunction(
		    {OpINotEqual, state.bool_type, non_zero, masked, ConstantU32(state, 0)});
		state.builder.AddFunction({OpSelect, state.uint_type, expanded, non_zero,
		                           ConstantU32(state, mask), ConstantU32(state, 0)});
		state.builder.AddFunction({OpBitwiseOr, state.uint_type, combined, ret, expanded});
		ret = combined;
	}
	return ret;
}

void EmitWqmB32(EmitterState& state, const IR::Instruction& inst) {
	if (!state.per_invocation_masks && inst.dst.kind == IR::OperandKind::Register &&
	    inst.dst.reg.file == IR::RegisterFile::Scalar) {
		const auto src = EmitValueLoad(state, inst.src[0]);
		EmitStoreU32(state, inst.dst, EmitWqmLaneU32(state, src));
		return;
	}
	const auto ballot = state.builder.AllocateId();
	state.builder.AddFunction({OpGroupNonUniformBallot, state.vec4_uint_type, ballot,
	                           ConstantU32(state, ScopeSubgroup),
	                           EmitLaneMaskOperandActiveBool(state, inst.src[0])});
	const auto low = state.builder.AllocateId();
	state.builder.AddFunction({OpCompositeExtract, state.uint_type, low, ballot, 0});
	const auto wqm_low   = EmitWqmLaneU32(state, low);
	const auto lane      = EmitSubgroupLocalInvocationId(state);
	const auto low_half  = state.builder.AllocateId();
	const auto bit_index = state.builder.AllocateId();
	const auto bit       = state.builder.AllocateId();
	const auto hit       = state.builder.AllocateId();
	const auto hit_bool  = state.builder.AllocateId();
	const auto active    = state.builder.AllocateId();
	const auto result    = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpULessThan, state.bool_type, low_half, lane, ConstantU32(state, 32)});
	state.builder.AddFunction(
	    {OpBitwiseAnd, state.uint_type, bit_index, lane, ConstantU32(state, 31)});
	state.builder.AddFunction(
	    {OpShiftLeftLogical, state.uint_type, bit, ConstantU32(state, 1), bit_index});
	state.builder.AddFunction({OpBitwiseAnd, state.uint_type, hit, wqm_low, bit});
	state.builder.AddFunction({OpINotEqual, state.bool_type, hit_bool, hit, ConstantU32(state, 0)});
	state.builder.AddFunction({OpLogicalAnd, state.bool_type, active, low_half, hit_bool});
	state.builder.AddFunction(
	    {OpSelect, state.uint_type, result, active, ConstantU32(state, 1), ConstantU32(state, 0)});
	EmitStoreU32(state, inst.dst, result);
}

void EmitWqmB64(EmitterState& state, const IR::Instruction& inst) {
	if (!state.per_invocation_masks && inst.dst.kind == IR::OperandKind::Register &&
	    inst.dst.reg.file == IR::RegisterFile::Scalar) {
		const auto low      = EmitSequentialValueLoad(state, inst.src[0], 0);
		const auto high     = EmitSequentialValueLoad(state, inst.src[0], 1);
		const auto ret_low  = EmitWqmLaneU32(state, low);
		const auto ret_high = EmitWqmLaneU32(state, high);
		EmitStoreU32(state, inst.dst, ret_low);
		EmitStoreU32(state, OffsetRegisterOperand(inst.dst, 1), ret_high);
		return;
	}
	const auto ballot = state.builder.AllocateId();
	state.builder.AddFunction({OpGroupNonUniformBallot, state.vec4_uint_type, ballot,
	                           ConstantU32(state, ScopeSubgroup),
	                           EmitLaneMaskOperandActiveBool(state, inst.src[0])});
	const auto low  = state.builder.AllocateId();
	const auto high = state.builder.AllocateId();
	state.builder.AddFunction({OpCompositeExtract, state.uint_type, low, ballot, 0});
	state.builder.AddFunction({OpCompositeExtract, state.uint_type, high, ballot, 1});
	const auto wqm_low   = EmitWqmLaneU32(state, low);
	const auto wqm_high  = EmitWqmLaneU32(state, high);
	const auto lane      = EmitSubgroupLocalInvocationId(state);
	const auto high_half = state.builder.AllocateId();
	const auto mask      = state.builder.AllocateId();
	const auto bit_index = state.builder.AllocateId();
	const auto bit       = state.builder.AllocateId();
	const auto hit       = state.builder.AllocateId();
	const auto active    = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpUGreaterThanEqual, state.bool_type, high_half, lane, ConstantU32(state, 32)});
	state.builder.AddFunction({OpSelect, state.uint_type, mask, high_half, wqm_high, wqm_low});
	state.builder.AddFunction(
	    {OpBitwiseAnd, state.uint_type, bit_index, lane, ConstantU32(state, 31)});
	state.builder.AddFunction(
	    {OpShiftLeftLogical, state.uint_type, bit, ConstantU32(state, 1), bit_index});
	state.builder.AddFunction({OpBitwiseAnd, state.uint_type, hit, mask, bit});
	state.builder.AddFunction({OpINotEqual, state.bool_type, active, hit, ConstantU32(state, 0)});
	if (state.per_invocation_masks) {
		EmitPerInvocationMask(state, inst.dst, active);
	} else {
		const auto result = state.builder.AllocateId();
		state.builder.AddFunction({OpSelect, state.uint_type, result, active, ConstantU32(state, 1),
		                           ConstantU32(state, 0)});
		EmitStoreU32(state, inst.dst, result);
		EmitStoreU32(state, OffsetRegisterOperand(inst.dst, 1), ConstantU32(state, 0));
	}
}

uint32_t EmitQuadMaskU32(EmitterState& state, uint32_t src) {
	uint32_t ret = ConstantU32(state, 0);
	for (uint32_t quad = 0; quad < 8u; quad++) {
		const auto masked   = state.builder.AllocateId();
		const auto non_zero = state.builder.AllocateId();
		const auto bit      = state.builder.AllocateId();
		const auto combined = state.builder.AllocateId();
		state.builder.AddFunction(
		    {OpBitwiseAnd, state.uint_type, masked, src, ConstantU32(state, 0x0fu << (quad * 4u))});
		state.builder.AddFunction(
		    {OpINotEqual, state.bool_type, non_zero, masked, ConstantU32(state, 0)});
		state.builder.AddFunction({OpSelect, state.uint_type, bit, non_zero,
		                           ConstantU32(state, 1u << quad), ConstantU32(state, 0)});
		state.builder.AddFunction({OpBitwiseOr, state.uint_type, combined, ret, bit});
		ret = combined;
	}
	return ret;
}

void EmitQuadMaskB32(EmitterState& state, const IR::Instruction& inst) {
	uint32_t src = 0;
	if (state.per_invocation_masks && inst.src[0].kind == IR::OperandKind::Register &&
	    IsMaskRegisterFile(inst.src[0].reg.file)) {
		const auto ballot = state.builder.AllocateId();
		state.builder.AddFunction({OpGroupNonUniformBallot, state.vec4_uint_type, ballot,
		                           ConstantU32(state, ScopeSubgroup),
		                           EmitLaneMaskOperandActiveBool(state, inst.src[0])});
		src = state.builder.AllocateId();
		state.builder.AddFunction({OpCompositeExtract, state.uint_type, src, ballot, 0});
	} else {
		src = EmitValueLoad(state, inst.src[0]);
	}
	EmitStoreU32(state, inst.dst, EmitQuadMaskU32(state, src));
}

void EmitQuadMaskB64(EmitterState& state, const IR::Instruction& inst) {
	uint32_t low  = 0;
	uint32_t high = 0;
	if (state.per_invocation_masks && inst.src[0].kind == IR::OperandKind::Register &&
	    IsMaskRegisterFile(inst.src[0].reg.file)) {
		const auto ballot = state.builder.AllocateId();
		state.builder.AddFunction({OpGroupNonUniformBallot, state.vec4_uint_type, ballot,
		                           ConstantU32(state, ScopeSubgroup),
		                           EmitLaneMaskOperandActiveBool(state, inst.src[0])});
		low  = state.builder.AllocateId();
		high = state.builder.AllocateId();
		state.builder.AddFunction({OpCompositeExtract, state.uint_type, low, ballot, 0});
		state.builder.AddFunction({OpCompositeExtract, state.uint_type, high, ballot, 1});
	} else {
		low  = EmitSequentialValueLoad(state, inst.src[0], 0);
		high = EmitSequentialValueLoad(state, inst.src[0], 1);
	}
	const auto low_mask     = EmitQuadMaskU32(state, low);
	const auto high_mask    = EmitQuadMaskU32(state, high);
	const auto shifted_high = state.builder.AllocateId();
	const auto combined     = state.builder.AllocateId();
	state.builder.AddFunction(
	    {OpShiftLeftLogical, state.uint_type, shifted_high, high_mask, ConstantU32(state, 8)});
	state.builder.AddFunction({OpBitwiseOr, state.uint_type, combined, low_mask, shifted_high});
	EmitStoreU32(state, inst.dst, combined);
	EmitStoreU32(state, OffsetRegisterOperand(inst.dst, 1), ConstantU32(state, 0));
}

void EmitSaveexecPerInvocation(EmitterState& state, const IR::Instruction& inst,
                               bool single_register_dst) {
	const auto old_exec = EmitExecActiveBool(state);
	const auto src      = EmitLaneMaskOperandActiveBool(state, inst.src[0]);
	if (single_register_dst) {
		const auto saved_exec = state.builder.AllocateId();
		state.builder.AddFunction({OpSelect, state.uint_type, saved_exec, old_exec,
		                           ConstantU32(state, 1), ConstantU32(state, 0)});
		EmitStoreU32(state, inst.dst, saved_exec);
	} else {
		EmitPerInvocationMask(state, inst.dst, old_exec);
	}

	uint32_t lhs     = old_exec;
	uint32_t rhs     = src;
	bool     use_or  = false;
	bool     use_xor = false;
	switch (inst.saveexec_mode) {
		case IR::SaveexecMode::And: break;
		case IR::SaveexecMode::Nand: break;
		case IR::SaveexecMode::Andn1: rhs = EmitLogicalNotBool(state, src); break;
		case IR::SaveexecMode::Andn2: lhs = EmitLogicalNotBool(state, old_exec); break;
		case IR::SaveexecMode::Or: use_or = true; break;
		case IR::SaveexecMode::Orn1:
			rhs    = EmitLogicalNotBool(state, src);
			use_or = true;
			break;
		case IR::SaveexecMode::Nor: use_or = true; break;
		case IR::SaveexecMode::Xor: use_xor = true; break;
		case IR::SaveexecMode::Xnor: use_xor = true; break;
		case IR::SaveexecMode::Orn2:
			lhs    = EmitLogicalNotBool(state, old_exec);
			use_or = true;
			break;
	}
	uint32_t result = 0;
	if (use_xor) {
		result = state.builder.AllocateId();
		state.builder.AddFunction({OpLogicalNotEqual, state.bool_type, result, lhs, rhs});
	} else {
		result = use_or ? EmitLogicalOrBool(state, lhs, rhs) : EmitLogicalAndBool(state, lhs, rhs);
	}
	if (inst.saveexec_mode == IR::SaveexecMode::Nand ||
	    inst.saveexec_mode == IR::SaveexecMode::Nor ||
	    inst.saveexec_mode == IR::SaveexecMode::Xnor) {
		result = EmitLogicalNotBool(state, result);
	}
	EmitPerInvocationMask(state, MakeRegisterOperand(IR::RegisterFile::Exec, 0), result);
	EmitStoreSccBool(state, result);
}

void EmitSaveexecB32(EmitterState& state, const IR::Instruction& inst) {
	if (state.per_invocation_masks) {
		EmitSaveexecPerInvocation(state, inst, true);
		return;
	}
	const auto old_low = EmitRegisterLoad(state, {IR::RegisterFile::Exec, 0});
	const auto src_low = EmitValueLoad(state, inst.src[0]);

	EmitStoreU32(state, inst.dst, old_low);

	uint32_t lhs_low = old_low;
	uint32_t rhs_low = src_low;
	if (inst.saveexec_mode == IR::SaveexecMode::Andn1 ||
	    inst.saveexec_mode == IR::SaveexecMode::Orn1) {
		rhs_low = state.builder.AllocateId();
		state.builder.AddFunction({OpNot, state.uint_type, rhs_low, src_low});
	}
	if (inst.saveexec_mode == IR::SaveexecMode::Andn2 ||
	    inst.saveexec_mode == IR::SaveexecMode::Orn2) {
		lhs_low = state.builder.AllocateId();
		state.builder.AddFunction({OpNot, state.uint_type, lhs_low, old_low});
	}

	uint32_t opcode = OpBitwiseAnd;
	if (inst.saveexec_mode == IR::SaveexecMode::Or || inst.saveexec_mode == IR::SaveexecMode::Nor ||
	    inst.saveexec_mode == IR::SaveexecMode::Orn1 ||
	    inst.saveexec_mode == IR::SaveexecMode::Orn2) {
		opcode = OpBitwiseOr;
	} else if (inst.saveexec_mode == IR::SaveexecMode::Xor ||
	           inst.saveexec_mode == IR::SaveexecMode::Xnor) {
		opcode = OpBitwiseXor;
	}
	auto new_low = state.builder.AllocateId();
	state.builder.AddFunction({opcode, state.uint_type, new_low, lhs_low, rhs_low});
	if (inst.saveexec_mode == IR::SaveexecMode::Nand ||
	    inst.saveexec_mode == IR::SaveexecMode::Nor ||
	    inst.saveexec_mode == IR::SaveexecMode::Xnor) {
		const auto inverted_low = state.builder.AllocateId();
		state.builder.AddFunction({OpNot, state.uint_type, inverted_low, new_low});
		new_low = inverted_low;
	}
	EmitStoreU32(state, MakeRegisterOperand(IR::RegisterFile::Exec, 0), new_low);

	if (state.wave_size == 32u) {
		EmitStoreU32(state, MakeRegisterOperand(IR::RegisterFile::Exec, 1), ConstantU32(state, 0));
	}

	const auto cond = state.builder.AllocateId();
	const auto scc  = state.builder.AllocateId();
	state.builder.AddFunction({OpINotEqual, state.bool_type, cond, new_low, ConstantU32(state, 0)});
	state.builder.AddFunction(
	    {OpSelect, state.uint_type, scc, cond, ConstantU32(state, 1), ConstantU32(state, 0)});
	EmitStoreU32(state, SccOperand(), scc);
}

void EmitSaveexecB64(EmitterState& state, const IR::Instruction& inst) {
	if (state.per_invocation_masks) {
		EmitSaveexecPerInvocation(state, inst, false);
		return;
	}
	const auto old_low  = EmitRegisterLoad(state, {IR::RegisterFile::Exec, 0});
	const auto old_high = EmitRegisterLoad(state, {IR::RegisterFile::Exec, 1});
	const auto src_low  = EmitSequentialValueLoad(state, inst.src[0], 0);
	const auto src_high = EmitSequentialValueLoad(state, inst.src[0], 1);

	EmitStoreU32(state, inst.dst, old_low);
	EmitStoreU32(state, OffsetRegisterOperand(inst.dst, 1), old_high);

	uint32_t lhs_low  = old_low;
	uint32_t lhs_high = old_high;
	uint32_t rhs_low  = src_low;
	uint32_t rhs_high = src_high;
	uint32_t opcode   = OpBitwiseAnd;
	if (inst.saveexec_mode == IR::SaveexecMode::Andn1 ||
	    inst.saveexec_mode == IR::SaveexecMode::Orn1) {
		rhs_low  = state.builder.AllocateId();
		rhs_high = state.builder.AllocateId();
		state.builder.AddFunction({OpNot, state.uint_type, rhs_low, src_low});
		state.builder.AddFunction({OpNot, state.uint_type, rhs_high, src_high});
	}
	if (inst.saveexec_mode == IR::SaveexecMode::Andn2 ||
	    inst.saveexec_mode == IR::SaveexecMode::Orn2) {
		lhs_low  = state.builder.AllocateId();
		lhs_high = state.builder.AllocateId();
		state.builder.AddFunction({OpNot, state.uint_type, lhs_low, old_low});
		state.builder.AddFunction({OpNot, state.uint_type, lhs_high, old_high});
		if (inst.saveexec_mode == IR::SaveexecMode::Orn2) {
			opcode = OpBitwiseOr;
		}
	}
	if (inst.saveexec_mode == IR::SaveexecMode::Or ||
	    inst.saveexec_mode == IR::SaveexecMode::Orn1 ||
	    inst.saveexec_mode == IR::SaveexecMode::Nor) {
		opcode = OpBitwiseOr;
	}
	if (inst.saveexec_mode == IR::SaveexecMode::Xor ||
	    inst.saveexec_mode == IR::SaveexecMode::Xnor) {
		opcode = OpBitwiseXor;
	}

	auto new_low  = state.builder.AllocateId();
	auto new_high = state.builder.AllocateId();
	state.builder.AddFunction({opcode, state.uint_type, new_low, lhs_low, rhs_low});
	state.builder.AddFunction({opcode, state.uint_type, new_high, lhs_high, rhs_high});
	if (inst.saveexec_mode == IR::SaveexecMode::Nand ||
	    inst.saveexec_mode == IR::SaveexecMode::Nor ||
	    inst.saveexec_mode == IR::SaveexecMode::Xnor) {
		const auto inverted_low  = state.builder.AllocateId();
		const auto inverted_high = state.builder.AllocateId();
		state.builder.AddFunction({OpNot, state.uint_type, inverted_low, new_low});
		state.builder.AddFunction({OpNot, state.uint_type, inverted_high, new_high});
		new_low  = inverted_low;
		new_high = inverted_high;
	}
	EmitStoreU32(state, MakeRegisterOperand(IR::RegisterFile::Exec, 0), new_low);
	EmitStoreU32(state, MakeRegisterOperand(IR::RegisterFile::Exec, 1), new_high);

	const auto active_new_high = state.wave_size == 32u ? ConstantU32(state, 0) : new_high;
	const auto mask            = state.builder.AllocateId();
	const auto cond            = state.builder.AllocateId();
	const auto scc             = state.builder.AllocateId();
	state.builder.AddFunction({OpBitwiseOr, state.uint_type, mask, new_low, active_new_high});
	state.builder.AddFunction({OpINotEqual, state.bool_type, cond, mask, ConstantU32(state, 0)});
	state.builder.AddFunction(
	    {OpSelect, state.uint_type, scc, cond, ConstantU32(state, 1), ConstantU32(state, 0)});
	EmitStoreU32(state, SccOperand(), scc);
}

void EmitReadFirstLaneU32(EmitterState& state, const IR::Instruction& inst) {
	const auto src         = EmitValueLoad(state, inst.src[0]);
	const auto active      = EmitExecActiveBool(state);
	const auto ballot      = state.builder.AllocateId();
	const auto first_lane  = state.builder.AllocateId();
	const auto first_value = state.builder.AllocateId();
	state.builder.AddFunction({OpGroupNonUniformBallot, state.vec4_uint_type, ballot,
	                           ConstantU32(state, ScopeSubgroup), active});
	state.builder.AddFunction({OpGroupNonUniformBallotFindLSB, state.uint_type, first_lane,
	                           ConstantU32(state, ScopeSubgroup), ballot});
	state.builder.AddFunction({OpGroupNonUniformShuffle, state.uint_type, first_value,
	                           ConstantU32(state, ScopeSubgroup), src, first_lane});
	EmitStoreU32(state, inst.dst, first_value);
}

uint32_t EmitLaneIndex(EmitterState& state, const IR::Operand& operand) {
	const auto lane = state.builder.AllocateId();
	const auto mask = state.wave_size == 32u ? 31u : 63u;
	state.builder.AddFunction({OpBitwiseAnd, state.uint_type, lane, EmitValueLoad(state, operand),
	                           ConstantU32(state, mask)});
	return lane;
}

void EmitReadLaneU32(EmitterState& state, const IR::Instruction& inst) {
	const auto src   = EmitValueLoad(state, inst.src[0]);
	const auto lane  = EmitLaneIndex(state, inst.src[1]);
	const auto value = state.builder.AllocateId();
	state.builder.AddFunction({OpGroupNonUniformShuffle, state.uint_type, value,
	                           ConstantU32(state, ScopeSubgroup), src, lane});
	EmitStoreU32(state, inst.dst, value);
}

void EmitWriteLaneU32(EmitterState& state, const IR::Instruction& inst) {
	const auto value = EmitValueLoad(state, inst.src[0]);
	const auto lane  = EmitLaneIndex(state, inst.src[1]);
	const auto subid = EmitSubgroupLocalInvocationId(state);
	const auto hit   = state.builder.AllocateId();
	const auto pointer =
	    inst.dst.kind == IR::OperandKind::Register ? PointerForRegister(state, inst.dst.reg) : 0;
	const auto old    = pointer != 0 ? state.builder.AllocateId() : ConstantU32(state, 0);
	const auto merged = state.builder.AllocateId();
	if (pointer != 0) {
		state.builder.AddFunction({OpLoad, state.uint_type, old, pointer});
	}
	state.builder.AddFunction({OpIEqual, state.bool_type, hit, subid, lane});
	state.builder.AddFunction({OpSelect, state.uint_type, merged, hit, value, old});
	if (pointer != 0) {
		// RDNA2 V_WRITELANE_B32 ignores EXEC, unlike ordinary VGPR destination writes.
		state.builder.AddFunction({OpStore, pointer, merged});
	}
}

void EmitPermlaneB32(EmitterState& state, const IR::Instruction& inst, bool x16) {
	const auto subid     = EmitSubgroupLocalInvocationId(state);
	const auto row       = state.builder.AllocateId();
	const auto lane      = state.builder.AllocateId();
	const auto lane8     = state.builder.AllocateId();
	const auto shift     = state.builder.AllocateId();
	const auto upper     = state.builder.AllocateId();
	const auto selected  = state.builder.AllocateId();
	const auto index0    = state.builder.AllocateId();
	const auto index1    = state.builder.AllocateId();
	const auto target    = state.builder.AllocateId();
	const auto shuffled  = state.builder.AllocateId();
	const auto value     = EmitValueLoad(state, inst.src[0]);
	const auto src1      = EmitValueLoad(state, inst.src[1]);
	const auto src2      = EmitValueLoad(state, inst.src[2]);
	uint32_t   row_value = row;
	state.builder.AddFunction(
	    {OpBitwiseAnd, state.uint_type, row, subid, ConstantU32(state, 0xfffffff0u)});
	if (x16) {
		row_value = state.builder.AllocateId();
		state.builder.AddFunction(
		    {OpBitwiseXor, state.uint_type, row_value, row, ConstantU32(state, 16)});
	}
	state.builder.AddFunction({OpBitwiseAnd, state.uint_type, lane, subid, ConstantU32(state, 15)});
	state.builder.AddFunction({OpBitwiseAnd, state.uint_type, lane8, lane, ConstantU32(state, 7)});
	state.builder.AddFunction(
	    {OpShiftLeftLogical, state.uint_type, shift, lane8, ConstantU32(state, 2)});
	state.builder.AddFunction(
	    {OpUGreaterThanEqual, state.bool_type, upper, lane, ConstantU32(state, 8)});
	state.builder.AddFunction({OpSelect, state.uint_type, selected, upper, src2, src1});
	state.builder.AddFunction({OpShiftRightLogical, state.uint_type, index0, selected, shift});
	state.builder.AddFunction(
	    {OpBitwiseAnd, state.uint_type, index1, index0, ConstantU32(state, 15)});
	state.builder.AddFunction({OpBitwiseOr, state.uint_type, target, row_value, index1});
	state.builder.AddFunction({OpGroupNonUniformShuffle, state.uint_type, shuffled,
	                           ConstantU32(state, ScopeSubgroup), value, target});
	uint32_t ret = shuffled;
	if (!inst.dst.op_sel) {
		const auto source_active = EmitLaneIndexActiveBool(state, target);
		ret                      = state.builder.AllocateId();
		state.builder.AddFunction(
		    {OpSelect, state.uint_type, ret, source_active, shuffled, ConstantU32(state, 0)});
	}
	EmitStoreU32(state, inst.dst, ret);
}

void EmitControlNop(EmitterState& state, const IR::Instruction& inst) {
	(void)state;
	(void)inst;
}

void EmitWaitcnt(EmitterState& state, const IR::Instruction& inst) {
	(void)state;
	(void)inst;
}

void EmitBarrier(EmitterState& state, const IR::Instruction& inst) {
	(void)inst;
	// Sony specifies s_barrier as local-workgroup synchronization. Include every
	// shader-visible memory class that can cross that synchronization point: LDS,
	// uniform/storage buffers, and images.
	const auto semantics = MemorySemanticsAcquireRelease | MemorySemanticsUniformMemory |
	                       MemorySemanticsWorkgroupMemory | MemorySemanticsImageMemory;
	state.builder.AddFunction({OpControlBarrier, ConstantU32(state, ScopeWorkgroup),
	                           ConstantU32(state, ScopeWorkgroup), ConstantU32(state, semantics)});
}

} // namespace Libs::Graphics::ShaderRecompiler::Spirv::Emitter
