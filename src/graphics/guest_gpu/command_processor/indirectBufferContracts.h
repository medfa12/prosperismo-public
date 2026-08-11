#ifndef EMULATOR_INCLUDE_EMULATOR_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_INDIRECT_BUFFER_CONTRACTS_H_
#define EMULATOR_INCLUDE_EMULATOR_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_INDIRECT_BUFFER_CONTRACTS_H_

#include "common/common.h"

namespace Libs::Graphics {

enum class IndirectBufferJumpAction {
	Call,
	Chain,
};

[[nodiscard]] constexpr IndirectBufferJumpAction ResolveIndirectBufferJumpAction(
    uint32_t control) {
	return (control & (1u << 20u)) != 0 ? IndirectBufferJumpAction::Chain
	                                      : IndirectBufferJumpAction::Call;
}

[[nodiscard]] constexpr IndirectBufferJumpAction ResolveConditionalBranchTargetAction() {
	return IndirectBufferJumpAction::Chain;
}

template <typename Cursor>
constexpr bool ReplaceIndirectBufferCursorForChain(IndirectBufferJumpAction action,
                                                    uint32_t* target, uint32_t size_dw,
                                                    Cursor& cursor) {
	if (action != IndirectBufferJumpAction::Chain) {
		return false;
	}
	cursor.next_packet         = target;
	cursor.remaining_dw        = size_dw;
	cursor.total_dw            = size_dw;
	cursor.deferred_advance_dw = 0;
	return true;
}

} // namespace Libs::Graphics

#endif // EMULATOR_INCLUDE_EMULATOR_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_INDIRECT_BUFFER_CONTRACTS_H_
