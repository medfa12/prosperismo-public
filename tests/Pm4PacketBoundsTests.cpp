#include "graphics/guest_gpu/command_processor/acquireMemContracts.h"
#include "graphics/guest_gpu/command_processor/aaSampleMaskContracts.h"
#include "graphics/guest_gpu/command_processor/atomicMemContracts.h"
#include "graphics/guest_gpu/command_processor/condWriteContracts.h"
#include "graphics/guest_gpu/command_processor/copyDataContracts.h"
#include "graphics/guest_gpu/command_processor/psShaderSampleExclusionContracts.h"
#include "graphics/guest_gpu/command_processor/dmaDataContracts.h"
#include "graphics/guest_gpu/command_processor/eventWriteContracts.h"
#include "graphics/guest_gpu/command_processor/indirectBufferContracts.h"
#include "graphics/guest_gpu/command_processor/releaseMemContracts.h"
#include "graphics/guest_gpu/command_processor/rewindContracts.h"
#include "graphics/guest_gpu/command_processor/waitRegMemContracts.h"
#include "graphics/guest_gpu/command_processor/writeDataContracts.h"
#include "graphics/guest_gpu/pm4.h"

#include <array>
#include <cstdint>
#include <cstdio>
#include <cstdlib>

namespace {

namespace Pm4 = Libs::Graphics::Pm4;

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "Pm4PacketBoundsTests: failed: %s\n", text);
		std::abort();
	}
}

void TestDeclaredPacketMustFitSubmittedRegion() {
	constexpr std::array<uint32_t, 6> write_data_packet = {
	    KYTY_PM4(6u, Pm4::IT_WRITE_DATA, Pm4::R_ZERO), 0u, 0u, 0u, 0u, 0u};

	Check(Pm4::GetPacketSizeDwords(write_data_packet[0]) == write_data_packet.size(),
	      "synthetic WRITE_DATA packet has the wrong declared size");
	Check(Pm4::PacketFitsInDwords(write_data_packet[0], write_data_packet.size()),
	      "complete WRITE_DATA packet was rejected");
	Check(!Pm4::PacketFitsInDwords(write_data_packet[0], write_data_packet.size() - 1u),
	      "truncated WRITE_DATA packet was accepted");
	Check(!Pm4::PacketFitsInDwords(write_data_packet[0], 1u),
	      "header-only WRITE_DATA packet was accepted");
}

void TestSingleDwordNopStillFits() {
	constexpr uint32_t nop = KYTY_PM4(1u, Pm4::IT_NOP, Pm4::R_ZERO);
	Check(Pm4::PacketFitsInDwords(nop, 1u), "single-DWORD NOP was rejected");
	Check(!Pm4::PacketFitsInDwords(nop, 0u), "single-DWORD NOP fit an empty region");
}

void TestAaSampleMaskRegisterAndQuadrants() {
	using Libs::Graphics::AaSampleMaskForPixel;
	using Libs::Graphics::AaSampleMaskDefault;
	using Libs::Graphics::UpdateAaSampleMaskRegister;

	uint64_t mask = AaSampleMaskDefault;
	Check(UpdateAaSampleMaskRegister(mask, Pm4::PA_SC_AA_MASK_X0Y0_X1Y0, 0x22221111u) &&
	          mask == 0xffffffff22221111ull,
	      "the low AA sample-mask register was not retained");
	Check(UpdateAaSampleMaskRegister(mask, Pm4::PA_SC_AA_MASK_X0Y1_X1Y1, 0x44443333u) &&
	          mask == 0x4444333322221111ull,
	      "the high AA sample-mask register was not retained");
	Check(!UpdateAaSampleMaskRegister(mask, Pm4::PS_SHADER_SAMPLE_EXCLUSION_MASK, 0u) &&
	          mask == 0x4444333322221111ull,
	      "the independent shader sample-exclusion register changed the AA mask");

	Check(AaSampleMaskForPixel(mask, 0u, 0u) == 0x1111u &&
	          AaSampleMaskForPixel(mask, 1u, 0u) == 0x2222u &&
	          AaSampleMaskForPixel(mask, 0u, 1u) == 0x3333u &&
	          AaSampleMaskForPixel(mask, 1u, 1u) == 0x4444u,
	      "the four screen-aligned 2x2 AA masks were not selected in SDK order");
	Check(AaSampleMaskForPixel(mask, 2u, 2u) == 0x1111u &&
	          AaSampleMaskForPixel(mask, 3u, 3u) == 0x4444u,
	      "the AA sample-mask quadrant pattern did not repeat every two pixels");
}

void TestPsShaderSampleExclusionMask() {
	using Libs::Graphics::ApplyPsShaderSampleExclusion;
	using Libs::Graphics::UpdatePsShaderSampleExclusionMask;

	uint32_t register_value = 0u;
	Check(UpdatePsShaderSampleExclusionMask(
	          register_value, Libs::Graphics::Pm4::PS_SHADER_SAMPLE_EXCLUSION_MASK, 0x0005u) &&
	          register_value == 0x0005u,
	      "the sample-exclusion register write was not retained");
	Check(!UpdatePsShaderSampleExclusionMask(register_value,
	                                         Libs::Graphics::Pm4::PA_SC_AA_MASK_X0Y0_X1Y0, 0u) &&
	          register_value == 0x0005u,
	      "the sample-exclusion register decoder consumed an unrelated AA-mask write");

	Check(ApplyPsShaderSampleExclusion(0x000fu, 0x0005u) == 0x000au,
	      "excluded sample locations still triggered pixel shader coverage");
	Check(ApplyPsShaderSampleExclusion(0x000fu, 0u) == 0x000fu,
	      "the default sample-exclusion mask changed covered samples");
	Check(ApplyPsShaderSampleExclusion(0x000fu, 0x000fu) == 0u,
	      "all excluded sample locations still triggered a pixel shader invocation");
}

void TestWriteDataFixedAddressMode() {
	using Libs::Graphics::CommitWriteDataPayload;
	using Libs::Graphics::ResolveWriteDataAddressMode;
	using Libs::Graphics::WriteDataAddressMode;

	Check(ResolveWriteDataAddressMode(1u, 4u) == WriteDataAddressMode::Fixed,
	      "fixed-address GL2 ME WRITE_DATA was rejected");
	Check(ResolveWriteDataAddressMode(1u, 5u) == WriteDataAddressMode::Fixed,
	      "fixed-address GL2 PFP WRITE_DATA was rejected");
	Check(ResolveWriteDataAddressMode(1u, 6u) == WriteDataAddressMode::Unsupported,
	      "fixed-address GDS WRITE_DATA was accepted without a GDS implementation");

	constexpr std::array<uint32_t, 3> payload = {0x11111111u, 0x22222222u, 0x33333333u};
	std::array<uint32_t, 3> destination = {0xaaaaaaaau, 0xbbbbbbbbu, 0xccccccccu};
	Check(CommitWriteDataPayload(destination.data(), payload.data(), payload.size(),
	                             WriteDataAddressMode::Fixed),
	      "a valid fixed-address WRITE_DATA payload failed");
	Check(destination[0] == payload.back(),
	      "fixed-address WRITE_DATA did not leave the last payload word at the destination");
	Check(destination[1] == 0xbbbbbbbbu && destination[2] == 0xccccccccu,
	      "fixed-address WRITE_DATA modified adjacent memory");

	destination = {0xaaaaaaaau, 0xbbbbbbbbu, 0xccccccccu};
	Check(CommitWriteDataPayload(destination.data(), payload.data(), payload.size(),
	                             WriteDataAddressMode::Increment),
	      "the established incrementing WRITE_DATA payload failed");
	Check(destination == payload, "incrementing WRITE_DATA no longer advanced its destination");
}

void TestEventWriteDfsmFlushRequiresBarrier() {
	using Libs::Graphics::EventWriteRequiresDfsmFlushBarrier;

	Check(EventWriteRequiresDfsmFlushBarrier(0x12u),
	      "the DFSM flush EVENT_WRITE remained a no-op");
	Check(!EventWriteRequiresDfsmFlushBarrier(0x17u),
	      "a performance-counter EVENT_WRITE gained a rendering barrier");
}

void TestCopyDataImmediate64Gl2Action() {
	using Libs::Graphics::CopyDataImmediate64Action;
	using Libs::Graphics::ResolveCopyDataImmediate64Action;
	using Libs::Graphics::SplitCopyDataImmediate64;

	Check(ResolveCopyDataImmediate64Action(10u, 4u, 8u) ==
	          CopyDataImmediate64Action::TwoDwordFills,
	      "a 64-bit immediate GL2 ME COPY_DATA was rejected");
	Check(ResolveCopyDataImmediate64Action(11u, 4u, 8u) ==
	          CopyDataImmediate64Action::TwoDwordFills,
	      "a 64-bit immediate GL2 PFP COPY_DATA was rejected");
	Check(ResolveCopyDataImmediate64Action(10u, 6u, 8u) ==
	          CopyDataImmediate64Action::Unsupported,
	      "a 64-bit immediate GDS COPY_DATA was accepted without a paired GDS implementation");
	Check(ResolveCopyDataImmediate64Action(10u, 4u, 4u) ==
	          CopyDataImmediate64Action::Unsupported,
	      "the established 32-bit immediate COPY_DATA path was replaced");
	Check(ResolveCopyDataImmediate64Action(4u, 4u, 8u) ==
	          CopyDataImmediate64Action::Unsupported,
	      "the established 64-bit memory COPY_DATA path was replaced");

	constexpr auto words = SplitCopyDataImmediate64(0x11223344aabbccddull);
	Check(words[0] == 0xaabbccddu && words[1] == 0x11223344u,
	      "a 64-bit immediate COPY_DATA did not preserve little-endian dword order");
}

void TestAcquireMemGl2FlushAction() {
	using Libs::Graphics::AcquireMemGl2Action;
	using Libs::Graphics::ResolveAcquireMemGl2Action;

	constexpr uint32_t ps5_packet       = 0xc0065800u;
	constexpr uint32_t internal_packet  = 0xc0061050u;
	constexpr uint32_t flush_gl2        = (1u << 5u) | (3u << 14u);
	constexpr uint32_t order_gl2_first  = 2u << 16u;

	Check(ResolveAcquireMemGl2Action(ps5_packet, flush_gl2) ==
	          AcquireMemGl2Action::GlobalBarrier,
	      "a native PS5 ACQUIRE_MEM GL2 flush remained a no-op");
	Check(ResolveAcquireMemGl2Action(internal_packet, flush_gl2) ==
	          AcquireMemGl2Action::GlobalBarrier,
	      "an HLE-generated ACQUIRE_MEM GL2 flush remained a no-op");
	Check(ResolveAcquireMemGl2Action(ps5_packet, 0u) == AcquireMemGl2Action::None,
	      "an ACQUIRE_MEM without a GCR operation gained a barrier");
	Check(ResolveAcquireMemGl2Action(ps5_packet, order_gl2_first) == AcquireMemGl2Action::None,
	      "an ACQUIRE_MEM ordering selector became a cache operation");
	Check(ResolveAcquireMemGl2Action(0xc0055800u, flush_gl2) ==
	          AcquireMemGl2Action::Unsupported,
	      "the legacy ACQUIRE_MEM packet shape was reinterpreted as a PS5 packet");
}

void TestAcquireMemCbDbRequiresBarrier() {
	using Libs::Graphics::AcquireMemCbDbRequiresGlobalBarrier;

	Check(AcquireMemCbDbRequiresGlobalBarrier(1u << 6u),
	      "an ACQUIRE_MEM color-target wait remained a no-op");
	Check(AcquireMemCbDbRequiresGlobalBarrier(1u << 14u),
	      "an ACQUIRE_MEM depth-target wait remained a no-op");
	Check(AcquireMemCbDbRequiresGlobalBarrier((1u << 25u) | (1u << 26u)),
	      "an ACQUIRE_MEM CB/DB data-cache flush remained a no-op");
	Check(!AcquireMemCbDbRequiresGlobalBarrier(1u << 31u),
	      "the ACQUIRE_MEM engine selector became a CB/DB operation");
	Check(!AcquireMemCbDbRequiresGlobalBarrier(1u << 5u),
	      "an unrelated ACQUIRE_MEM control bit became a CB/DB operation");
}

void TestAcquireMemShaderDataRequiresBarrier() {
	using Libs::Graphics::AcquireMemShaderDataRequiresGlobalBarrier;

	Check(AcquireMemShaderDataRequiresGlobalBarrier(1u << 7u),
	      "an ACQUIRE_MEM GL0 scalar invalidation remained a no-op");
	Check(AcquireMemShaderDataRequiresGlobalBarrier(1u << 8u),
	      "an ACQUIRE_MEM GL0 vector invalidation remained a no-op");
	Check(AcquireMemShaderDataRequiresGlobalBarrier(1u << 9u),
	      "an ACQUIRE_MEM GL1 invalidation remained a no-op");
	Check(!AcquireMemShaderDataRequiresGlobalBarrier(1u << 0u),
	      "an instruction-cache invalidation entered the shader-data path");
	Check(!AcquireMemShaderDataRequiresGlobalBarrier(2u << 16u),
	      "an ACQUIRE_MEM ordering selector became a shader-data cache operation");
	Check(!AcquireMemShaderDataRequiresGlobalBarrier(1u << 15u),
	      "an ACQUIRE_MEM GL2 writeback entered the shader-data path");
}

void TestDmaDataNowhereDestination() {
	using Libs::Graphics::DmaDataDestinationAction;
	using Libs::Graphics::ResolveDmaDataDestinationAction;

	Check(ResolveDmaDataDestinationAction(2u) == DmaDataDestinationAction::Discard,
	      "DMA_DATA kNowhere still terminates instead of discarding the destination write");
	Check(ResolveDmaDataDestinationAction(3u) == DmaDataDestinationAction::Memory,
	      "DMA_DATA GL2 destination stopped selecting memory");
	Check(ResolveDmaDataDestinationAction(1u) == DmaDataDestinationAction::Gds,
	      "DMA_DATA GDS destination stopped selecting GDS");
	Check(ResolveDmaDataDestinationAction(4u) == DmaDataDestinationAction::Unsupported,
	      "an unknown DMA_DATA destination selector was accepted");
}

void TestIndirectBufferChainReplacesCurrentCursor() {
	using Libs::Graphics::IndirectBufferJumpAction;
	using Libs::Graphics::ReplaceIndirectBufferCursorForChain;
	using Libs::Graphics::ResolveIndirectBufferJumpAction;

	struct Cursor {
		uint32_t* next_packet;
		uint32_t  remaining_dw;
		uint32_t  total_dw;
		uint32_t  deferred_advance_dw;
	};

	std::array<uint32_t, 4> parent = {};
	std::array<uint32_t, 3> target = {};
	Cursor cursor {parent.data() + 1, 3u, 4u, 2u};

	const auto chain = ResolveIndirectBufferJumpAction(1u << 20u);
	Check(chain == IndirectBufferJumpAction::Chain,
	      "INDIRECT_BUFFER chain control decoded as a call");
	Check(ReplaceIndirectBufferCursorForChain(chain, target.data(), target.size(), cursor),
	      "INDIRECT_BUFFER chain retained its parent return cursor");
	Check(cursor.next_packet == target.data() && cursor.remaining_dw == target.size() &&
	          cursor.total_dw == target.size() && cursor.deferred_advance_dw == 0u,
	      "INDIRECT_BUFFER chain did not replace the complete fetch cursor");

	cursor = {parent.data() + 1, 3u, 4u, 2u};
	const auto call = ResolveIndirectBufferJumpAction(0u);
	Check(call == IndirectBufferJumpAction::Call,
	      "INDIRECT_BUFFER call control decoded as a chain");
	Check(!ReplaceIndirectBufferCursorForChain(call, target.data(), target.size(), cursor),
	      "INDIRECT_BUFFER call discarded its parent return cursor");
	Check(cursor.next_packet == parent.data() + 1 && cursor.remaining_dw == 3u &&
	          cursor.total_dw == 4u && cursor.deferred_advance_dw == 2u,
	      "INDIRECT_BUFFER call mutated its parent cursor");
}

void TestConditionalBranchReplacesCurrentCursor() {
	using Libs::Graphics::IndirectBufferJumpAction;
	using Libs::Graphics::ReplaceIndirectBufferCursorForChain;
	using Libs::Graphics::ResolveConditionalBranchTargetAction;

	struct Cursor {
		uint32_t* next_packet;
		uint32_t  remaining_dw;
		uint32_t  total_dw;
		uint32_t  deferred_advance_dw;
	};

	std::array<uint32_t, 4> parent = {};
	std::array<uint32_t, 3> target = {};
	Cursor cursor {parent.data() + 1, 3u, 4u, 2u};

	const auto branch = ResolveConditionalBranchTargetAction();
	Check(branch == IndirectBufferJumpAction::Chain,
	      "a selected conditional branch target retained call/return behavior");
	Check(ReplaceIndirectBufferCursorForChain(branch, target.data(), target.size(), cursor),
	      "a selected conditional branch target retained its parent return cursor");
	Check(cursor.next_packet == target.data() && cursor.remaining_dw == target.size() &&
	          cursor.total_dw == target.size() && cursor.deferred_advance_dw == 0u,
	      "a selected conditional branch target did not replace the complete fetch cursor");
}

void TestRewindPendingSuspendsUntilValid() {
	using Libs::Graphics::ResolveRewindAction;
	using Libs::Graphics::RewindAction;

	Check(ResolveRewindAction(0u) == RewindAction::Suspend,
	      "a pending REWIND packet did not suspend command fetch");
	Check(ResolveRewindAction(0x7fffffffu) == RewindAction::Suspend,
	      "reserved REWIND control bits released a pending packet");
	Check(ResolveRewindAction(1u << 31u) == RewindAction::Continue,
	      "a valid REWIND packet remained suspended");
	Check(ResolveRewindAction(0xffffffffu) == RewindAction::Continue,
	      "reserved REWIND control bits masked a valid packet");
}

void TestReleaseMemPacketIdentity() {
	using Libs::Graphics::IsReleaseMemPacketHeader;

	constexpr uint32_t custom = KYTY_PM4(8u, Pm4::IT_NOP, Pm4::R_RELEASE_MEM);
	constexpr uint32_t native = KYTY_PM4(8u, Pm4::IT_RELEASE_MEM, Pm4::R_ZERO);

	Check(IsReleaseMemPacketHeader(custom), "the HLE RELEASE_MEM wrapper was rejected");
	Check(IsReleaseMemPacketHeader(native), "the native RELEASE_MEM packet remained undispatchable");
	Check(IsReleaseMemPacketHeader(custom | 1u),
	      "a predicated HLE RELEASE_MEM wrapper was rejected");
	Check(IsReleaseMemPacketHeader(native | 1u),
	      "a predicated native RELEASE_MEM packet was rejected");
	Check(!IsReleaseMemPacketHeader(KYTY_PM4(7u, Pm4::IT_RELEASE_MEM, Pm4::R_ZERO)),
	      "a short native RELEASE_MEM packet was accepted");
	Check(!IsReleaseMemPacketHeader(KYTY_PM4(8u, Pm4::IT_EVENT_WRITE_EOP, Pm4::R_ZERO)),
	      "an unrelated EOP packet was classified as RELEASE_MEM");
}

void TestNativeWaitRegMem32PacketDecoding() {
	using Libs::Graphics::DecodeWaitRegMem32Packet;
	using Libs::Graphics::WaitRegMem32Packet;

	constexpr uint32_t control             = 0x04000110u;
	constexpr uint32_t address_low         = 0x55667788u;
	constexpr uint32_t address_high        = 0x00001234u;
	constexpr uint32_t reference           = 0xaabbccddu;
	constexpr uint32_t mask                = 0x00ff00ffu;
	constexpr uint32_t poll_interval       = 0x1234u;

	constexpr std::array<uint32_t, 6> custom_payload = {
	    address_low, address_high, mask, reference, control, poll_interval};
	constexpr std::array<uint32_t, 6> native_payload = {
	    control, address_low, address_high, reference, mask, poll_interval};

	WaitRegMem32Packet custom {};
	WaitRegMem32Packet native {};
	Check(DecodeWaitRegMem32Packet(KYTY_PM4(7u, Pm4::IT_NOP, Pm4::R_WAIT_MEM_32),
	                               custom_payload.data(), custom),
	      "the HLE WAIT_REG_MEM wrapper was rejected");
	Check(DecodeWaitRegMem32Packet(KYTY_PM4(7u, Pm4::IT_WAIT_REG_MEM, Pm4::R_ZERO),
	                               native_payload.data(), native),
	      "the native WAIT_REG_MEM packet remained undispatchable");
	Check(native.address == (address_low | (static_cast<uint64_t>(address_high) << 32u)) &&
	          native.reference == reference && native.mask == mask && native.control == control &&
	          native.poll_interval == poll_interval,
	      "the native WAIT_REG_MEM payload order decoded incorrectly");
	Check(custom.address == native.address && custom.reference == native.reference &&
	          custom.mask == native.mask && custom.control == native.control &&
	          custom.poll_interval == native.poll_interval,
	      "native WAIT_REG_MEM fields did not match the established HLE state path");
	Check(!DecodeWaitRegMem32Packet(KYTY_PM4(6u, Pm4::IT_WAIT_REG_MEM, Pm4::R_ZERO),
	                                native_payload.data(), native),
	      "a short native WAIT_REG_MEM packet was accepted");
	Check(!DecodeWaitRegMem32Packet(KYTY_PM4(7u, Pm4::IT_COND_WRITE, Pm4::R_ZERO),
	                                native_payload.data(), native),
	      "an unrelated seven-DWORD packet was classified as WAIT_REG_MEM");
}

void TestNativeAtomicMemAdd32WaitForConfirm() {
	using Libs::Graphics::AtomicMemCommand;
	using Libs::Graphics::AtomicMemOperation;
	using Libs::Graphics::AtomicMemPacket;
	using Libs::Graphics::AtomicMemResult;
	using Libs::Graphics::DecodeAtomicMemPacket;
	using Libs::Graphics::ExecuteAtomicMemPacket;

	alignas(4) uint32_t destination = 0xfffffffeu;
	const auto address = reinterpret_cast<uint64_t>(&destination);
	std::array<uint32_t, 8> payload = {
	    static_cast<uint32_t>(AtomicMemOperation::Add32) |
	        (static_cast<uint32_t>(AtomicMemCommand::WaitForConfirm) << 8u) | (2u << 25u) |
	        (3u << 30u),
	    static_cast<uint32_t>(address), static_cast<uint32_t>(address >> 32u), 5u, 0u, 0u, 0u, 0u};

	AtomicMemPacket packet {};
	Check(DecodeAtomicMemPacket(KYTY_PM4(9u, Pm4::IT_ATOMIC_MEM, Pm4::R_ZERO), payload.data(),
	                           packet),
	      "the native ATOMIC_MEM add32 wait-for-confirm packet remained unsupported");
	Check(packet.address == address && packet.source == 5u &&
	          packet.operation == AtomicMemOperation::Add32 &&
	          packet.command == AtomicMemCommand::WaitForConfirm,
	      "the native ATOMIC_MEM add32 fields decoded incorrectly");
	Check(ExecuteAtomicMemPacket(packet, destination) == AtomicMemResult::Completed &&
	          destination == 3u,
	      "ATOMIC_MEM add32 did not atomically add with 32-bit wraparound");

	destination = 11u;
	payload[0]  = 81u | (2u << 8u);
	Check(!DecodeAtomicMemPacket(KYTY_PM4(9u, Pm4::IT_ATOMIC_MEM, Pm4::R_ZERO), payload.data(),
	                            packet) &&
	          destination == 11u,
	      "an unsupported ATOMIC_MEM operation was accepted or mutated memory");
	payload[0] = static_cast<uint32_t>(AtomicMemOperation::Add32) | (3u << 8u);
	Check(!DecodeAtomicMemPacket(KYTY_PM4(9u, Pm4::IT_ATOMIC_MEM, Pm4::R_ZERO), payload.data(),
	                            packet),
	      "the unsupported fire-and-forget ATOMIC_MEM command was accepted");
	payload[0] = static_cast<uint32_t>(AtomicMemOperation::Add32) |
	             (static_cast<uint32_t>(AtomicMemCommand::WaitForConfirm) << 8u);
	Check(!DecodeAtomicMemPacket(KYTY_PM4(8u, Pm4::IT_ATOMIC_MEM, Pm4::R_ZERO), payload.data(),
	                            packet),
	      "a short ATOMIC_MEM packet was accepted");
	Check(!DecodeAtomicMemPacket(KYTY_PM4(9u, Pm4::IT_COND_WRITE, Pm4::R_ZERO), payload.data(),
	                            packet),
	      "an unrelated nine-DWORD packet was classified as ATOMIC_MEM");
}

void TestNativeAtomicMemSub32WaitForConfirm() {
	using Libs::Graphics::AtomicMemCommand;
	using Libs::Graphics::AtomicMemOperation;
	using Libs::Graphics::AtomicMemPacket;
	using Libs::Graphics::AtomicMemResult;
	using Libs::Graphics::DecodeAtomicMemPacket;
	using Libs::Graphics::ExecuteAtomicMemPacket;

	alignas(4) uint32_t destination = 3u;
	const auto address = reinterpret_cast<uint64_t>(&destination);
	std::array<uint32_t, 8> payload = {
	    static_cast<uint32_t>(AtomicMemOperation::Sub32) |
	        (static_cast<uint32_t>(AtomicMemCommand::WaitForConfirm) << 8u),
	    static_cast<uint32_t>(address), static_cast<uint32_t>(address >> 32u), 5u, 0u, 0u, 0u, 0u};

	AtomicMemPacket packet {};
	Check(DecodeAtomicMemPacket(KYTY_PM4(9u, Pm4::IT_ATOMIC_MEM, Pm4::R_ZERO), payload.data(),
	                           packet),
	      "the native ATOMIC_MEM sub32 wait-for-confirm packet remained unsupported");
	Check(packet.address == address && packet.source == 5u &&
	          packet.operation == AtomicMemOperation::Sub32 &&
	          packet.command == AtomicMemCommand::WaitForConfirm,
	      "the native ATOMIC_MEM sub32 fields decoded incorrectly");
	Check(ExecuteAtomicMemPacket(packet, destination) == AtomicMemResult::Completed &&
	          destination == 0xfffffffeu,
	      "ATOMIC_MEM sub32 did not atomically subtract with 32-bit wraparound");
}

void TestNativeAtomicMemAdd64WaitForConfirm() {
	using Libs::Graphics::AtomicMemCommand;
	using Libs::Graphics::AtomicMemOperation;
	using Libs::Graphics::AtomicMemPacket;
	using Libs::Graphics::AtomicMemResult;
	using Libs::Graphics::DecodeAtomicMemPacket;
	using Libs::Graphics::ExecuteAtomicMemPacket;

	alignas(8) uint64_t destination = 0xfffffffffffffffeull;
	const auto address = reinterpret_cast<uint64_t>(&destination);
	constexpr uint64_t source = 0x0000000100000005ull;
	std::array<uint32_t, 8> payload = {
	    static_cast<uint32_t>(AtomicMemOperation::Add64) |
	        (static_cast<uint32_t>(AtomicMemCommand::WaitForConfirm) << 8u),
	    static_cast<uint32_t>(address), static_cast<uint32_t>(address >> 32u),
	    static_cast<uint32_t>(source), static_cast<uint32_t>(source >> 32u), 0u, 0u, 0u};

	AtomicMemPacket packet {};
	Check(DecodeAtomicMemPacket(KYTY_PM4(9u, Pm4::IT_ATOMIC_MEM, Pm4::R_ZERO), payload.data(),
	                           packet),
	      "the native ATOMIC_MEM add64 wait-for-confirm packet remained unsupported");
	Check(packet.address == address && packet.source == source &&
	          packet.operation == AtomicMemOperation::Add64 &&
	          packet.command == AtomicMemCommand::WaitForConfirm,
	      "the native ATOMIC_MEM add64 fields decoded incorrectly");
	Check(ExecuteAtomicMemPacket(packet, destination) == AtomicMemResult::Completed &&
	          destination == 0x0000000100000003ull,
	      "ATOMIC_MEM add64 did not atomically add with 64-bit wraparound");

	destination = 11u;
	payload[0]  = 47u | (static_cast<uint32_t>(AtomicMemCommand::WaitForConfirm) << 8u);
	Check(!DecodeAtomicMemPacket(KYTY_PM4(9u, Pm4::IT_ATOMIC_MEM, Pm4::R_ZERO), payload.data(),
	                            packet) &&
	          destination == 11u,
	      "a returning ATOMIC_MEM operation was accepted or mutated 64-bit memory");
}

void TestNativeAtomicMemSub64WaitForConfirm() {
	using Libs::Graphics::AtomicMemCommand;
	using Libs::Graphics::AtomicMemOperation;
	using Libs::Graphics::AtomicMemPacket;
	using Libs::Graphics::AtomicMemResult;
	using Libs::Graphics::DecodeAtomicMemPacket;
	using Libs::Graphics::ExecuteAtomicMemPacket;

	alignas(8) uint64_t destination = 3u;
	const auto address = reinterpret_cast<uint64_t>(&destination);
	constexpr uint64_t source = 0x0000000100000005ull;
	std::array<uint32_t, 8> payload = {
	    static_cast<uint32_t>(AtomicMemOperation::Sub64) |
	        (static_cast<uint32_t>(AtomicMemCommand::WaitForConfirm) << 8u),
	    static_cast<uint32_t>(address), static_cast<uint32_t>(address >> 32u),
	    static_cast<uint32_t>(source), static_cast<uint32_t>(source >> 32u), 0u, 0u, 0u};

	AtomicMemPacket packet {};
	Check(DecodeAtomicMemPacket(KYTY_PM4(9u, Pm4::IT_ATOMIC_MEM, Pm4::R_ZERO), payload.data(),
	                           packet),
	      "the native ATOMIC_MEM sub64 wait-for-confirm packet remained unsupported");
	Check(packet.address == address && packet.source == source &&
	          packet.operation == AtomicMemOperation::Sub64 &&
	          packet.command == AtomicMemCommand::WaitForConfirm,
	      "the native ATOMIC_MEM sub64 fields decoded incorrectly");
	Check(ExecuteAtomicMemPacket(packet, destination) == AtomicMemResult::Completed &&
	          destination == 0xfffffffefffffffeull,
	      "ATOMIC_MEM sub64 did not atomically subtract with 64-bit wraparound");
}

void TestNativeAtomicMemCompareSwap32WaitForConfirm() {
	using Libs::Graphics::AtomicMemCommand;
	using Libs::Graphics::AtomicMemOperation;
	using Libs::Graphics::AtomicMemPacket;
	using Libs::Graphics::AtomicMemResult;
	using Libs::Graphics::DecodeAtomicMemPacket;
	using Libs::Graphics::ExecuteAtomicMemPacket;

	alignas(4) uint32_t destination = 0x55667788u;
	const auto address = reinterpret_cast<uint64_t>(&destination);
	constexpr uint64_t source  = 0xdeadbeef11223344ull;
	constexpr uint64_t compare = 0xcafebabe55667788ull;
	std::array<uint32_t, 8> payload = {
	    static_cast<uint32_t>(AtomicMemOperation::CompareSwap32) |
	        (static_cast<uint32_t>(AtomicMemCommand::WaitForConfirm) << 8u),
	    static_cast<uint32_t>(address), static_cast<uint32_t>(address >> 32u),
	    static_cast<uint32_t>(source), static_cast<uint32_t>(source >> 32u),
	    static_cast<uint32_t>(compare), static_cast<uint32_t>(compare >> 32u), 0u};

	AtomicMemPacket packet {};
	Check(DecodeAtomicMemPacket(KYTY_PM4(9u, Pm4::IT_ATOMIC_MEM, Pm4::R_ZERO), payload.data(),
	                           packet),
	      "the native ATOMIC_MEM compare-swap32 wait-for-confirm packet remained unsupported");
	Check(packet.address == address && packet.source == source && packet.compare == compare &&
	          packet.operation == AtomicMemOperation::CompareSwap32 &&
	          packet.command == AtomicMemCommand::WaitForConfirm,
	      "the native ATOMIC_MEM compare-swap32 fields decoded incorrectly");
	Check(ExecuteAtomicMemPacket(packet, destination) == AtomicMemResult::Completed &&
	          destination == static_cast<uint32_t>(source),
	      "a matching ATOMIC_MEM compare-swap32 did not store its source");

	destination = 0xaabbccddu;
	Check(ExecuteAtomicMemPacket(packet, destination) == AtomicMemResult::Completed &&
	          destination == 0xaabbccddu,
	      "a failing ATOMIC_MEM compare-swap32 modified its destination");
	payload[0] = 8u | (static_cast<uint32_t>(AtomicMemCommand::WaitForConfirm) << 8u);
	Check(!DecodeAtomicMemPacket(KYTY_PM4(9u, Pm4::IT_ATOMIC_MEM, Pm4::R_ZERO), payload.data(),
	                            packet) &&
	          destination == 0xaabbccddu,
	      "a returning compare-swap operation was accepted or mutated memory");
}

void TestNativeAtomicMemCompareSwap64WaitForConfirm() {
	using Libs::Graphics::AtomicMemCommand;
	using Libs::Graphics::AtomicMemOperation;
	using Libs::Graphics::AtomicMemPacket;
	using Libs::Graphics::AtomicMemResult;
	using Libs::Graphics::DecodeAtomicMemPacket;
	using Libs::Graphics::ExecuteAtomicMemPacket;

	alignas(8) uint64_t destination = 0x1122334455667788ull;
	const auto address = reinterpret_cast<uint64_t>(&destination);
	constexpr uint64_t source  = 0xaabbccddeeff0011ull;
	constexpr uint64_t compare = 0x1122334455667788ull;
	std::array<uint32_t, 8> payload = {
	    static_cast<uint32_t>(AtomicMemOperation::CompareSwap64) |
	        (static_cast<uint32_t>(AtomicMemCommand::WaitForConfirm) << 8u),
	    static_cast<uint32_t>(address), static_cast<uint32_t>(address >> 32u),
	    static_cast<uint32_t>(source), static_cast<uint32_t>(source >> 32u),
	    static_cast<uint32_t>(compare), static_cast<uint32_t>(compare >> 32u), 0u};

	AtomicMemPacket packet {};
	Check(DecodeAtomicMemPacket(KYTY_PM4(9u, Pm4::IT_ATOMIC_MEM, Pm4::R_ZERO), payload.data(),
	                           packet),
	      "the native ATOMIC_MEM compare-swap64 wait-for-confirm packet remained unsupported");
	Check(packet.address == address && packet.source == source && packet.compare == compare &&
	          packet.operation == AtomicMemOperation::CompareSwap64 &&
	          packet.command == AtomicMemCommand::WaitForConfirm,
	      "the native ATOMIC_MEM compare-swap64 fields decoded incorrectly");
	Check(ExecuteAtomicMemPacket(packet, destination) == AtomicMemResult::Completed &&
	          destination == source,
	      "a matching ATOMIC_MEM compare-swap64 did not store its full-width source");

	destination = 0x8877665544332211ull;
	Check(ExecuteAtomicMemPacket(packet, destination) == AtomicMemResult::Completed &&
	          destination == 0x8877665544332211ull,
	      "a failing ATOMIC_MEM compare-swap64 modified its destination");
	payload[0] = 40u | (static_cast<uint32_t>(AtomicMemCommand::WaitForConfirm) << 8u);
	Check(!DecodeAtomicMemPacket(KYTY_PM4(9u, Pm4::IT_ATOMIC_MEM, Pm4::R_ZERO), payload.data(),
	                            packet) &&
	          destination == 0x8877665544332211ull,
	      "a returning 64-bit compare-swap operation was accepted or mutated memory");
}

void TestCondWriteGl2ComparisonAndMutation() {
	using Libs::Graphics::CondWriteResult;
	using Libs::Graphics::ExecuteCondWriteGl2;

	volatile uint32_t destination = 0xaaaaaaaau;
	auto result = ExecuteCondWriteGl2(3u, 0xabcd1234u, 0x1234u, 0xffffu, 0x55667788u,
	                                  destination);
	Check(result == CondWriteResult::Written,
	      "native COND_WRITE GL2 comparison remained unsupported");
	Check(destination == 0x55667788u, "a passing COND_WRITE did not update its destination");

	constexpr std::array passing_comparisons = {
	    std::array<uint32_t, 3> {0u, 9u, 4u}, std::array<uint32_t, 3> {1u, 3u, 4u},
	    std::array<uint32_t, 3> {2u, 4u, 4u}, std::array<uint32_t, 3> {4u, 3u, 4u},
	    std::array<uint32_t, 3> {5u, 4u, 4u}, std::array<uint32_t, 3> {6u, 5u, 4u}};
	for (const auto& comparison: passing_comparisons) {
		destination = 0xaaaaaaaau;
		result      = ExecuteCondWriteGl2(comparison[0], comparison[1], comparison[2], 0xffffffffu,
		                                  0x55667788u, destination);
		Check(result == CondWriteResult::Written && destination == 0x55667788u,
		      "a documented COND_WRITE comparison failed");
	}

	destination = 0xaaaaaaaau;
	result      = ExecuteCondWriteGl2(3u, 0xabcd1234u, 0x4321u, 0xffffu, 0x55667788u,
	                                  destination);
	Check(result == CondWriteResult::Skipped, "a failing COND_WRITE comparison was not skipped");
	Check(destination == 0xaaaaaaaau, "a failing COND_WRITE mutated its destination");

	result = ExecuteCondWriteGl2(7u, 0u, 0u, 0xffffffffu, 0x55667788u, destination);
	Check(result == CondWriteResult::Unsupported, "an unknown COND_WRITE comparison was accepted");
	Check(destination == 0xaaaaaaaau, "an unknown COND_WRITE comparison mutated its destination");
}

} // namespace

int main() {
	TestDeclaredPacketMustFitSubmittedRegion();
	TestSingleDwordNopStillFits();
	TestAaSampleMaskRegisterAndQuadrants();
	TestPsShaderSampleExclusionMask();
	TestWriteDataFixedAddressMode();
	TestEventWriteDfsmFlushRequiresBarrier();
	TestCopyDataImmediate64Gl2Action();
	TestAcquireMemGl2FlushAction();
	TestAcquireMemCbDbRequiresBarrier();
	TestAcquireMemShaderDataRequiresBarrier();
	TestDmaDataNowhereDestination();
	TestIndirectBufferChainReplacesCurrentCursor();
	TestConditionalBranchReplacesCurrentCursor();
	TestRewindPendingSuspendsUntilValid();
	TestReleaseMemPacketIdentity();
	TestNativeWaitRegMem32PacketDecoding();
	TestNativeAtomicMemAdd32WaitForConfirm();
	TestNativeAtomicMemSub32WaitForConfirm();
	TestNativeAtomicMemAdd64WaitForConfirm();
	TestNativeAtomicMemSub64WaitForConfirm();
	TestNativeAtomicMemCompareSwap32WaitForConfirm();
	TestNativeAtomicMemCompareSwap64WaitForConfirm();
	TestCondWriteGl2ComparisonAndMutation();
	std::printf("Pm4PacketBoundsTests: all cases passed\n");
	return 0;
}
