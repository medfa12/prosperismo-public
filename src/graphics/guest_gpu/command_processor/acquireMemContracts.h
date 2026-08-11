#ifndef EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_ACQUIREMEMCONTRACTS_H_
#define EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_ACQUIREMEMCONTRACTS_H_

#include <cstdint>

namespace Libs::Graphics {

enum class AcquireMemGl2Action { Unsupported, None, GlobalBarrier };

[[nodiscard]] constexpr AcquireMemGl2Action ResolveAcquireMemGl2Action(
	uint32_t packet_header, uint32_t gcr_cntl) noexcept {
	constexpr uint32_t Ps5PacketHeader      = 0xc0065800u;
	constexpr uint32_t InternalPacketHeader = 0xc0061050u;
	constexpr uint32_t Gl2OperationMask     = (1u << 5u) | (1u << 15u);

	if (packet_header != Ps5PacketHeader && packet_header != InternalPacketHeader) {
		return AcquireMemGl2Action::Unsupported;
	}
	return (gcr_cntl & Gl2OperationMask) != 0 ? AcquireMemGl2Action::GlobalBarrier
	                                          : AcquireMemGl2Action::None;
}

[[nodiscard]] constexpr bool AcquireMemCbDbRequiresGlobalBarrier(uint32_t control) noexcept {
	constexpr uint32_t RenderTargetWaitMask = 0x1ffu << 6u;
	constexpr uint32_t DataCacheFlushMask   = 0x3u << 25u;
	return (control & (RenderTargetWaitMask | DataCacheFlushMask)) != 0;
}

[[nodiscard]] constexpr bool AcquireMemShaderDataRequiresGlobalBarrier(
	uint32_t gcr_cntl) noexcept {
	constexpr uint32_t ShaderDataInvalidateMask = (1u << 7u) | (1u << 8u) | (1u << 9u);
	return (gcr_cntl & ShaderDataInvalidateMask) != 0;
}

} // namespace Libs::Graphics

#endif // EMULATOR_SRC_GRAPHICS_GUEST_GPU_COMMAND_PROCESSOR_ACQUIREMEMCONTRACTS_H_
