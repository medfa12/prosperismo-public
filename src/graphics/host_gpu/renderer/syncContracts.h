#ifndef EMULATOR_SRC_GRAPHICS_HOST_GPU_RENDERER_SYNCCONTRACTS_H_
#define EMULATOR_SRC_GRAPHICS_HOST_GPU_RENDERER_SYNCCONTRACTS_H_

#include <cstring>
#include <cstdint>
#include <utility>

namespace Libs::Graphics::Sync {

constexpr uint32_t END_OF_PIPE_INTERRUPT_CONTEXT_MASK = 0x07ffffffu;

[[nodiscard]] constexpr uint32_t ResolveEndOfPipeInterruptContext(uint64_t write_value,
                                                                  uint32_t context_id) noexcept {
	(void)write_value;
	return context_id & END_OF_PIPE_INTERRUPT_CONTEXT_MASK;
}

enum class EndOfPipeWriteSize : uint32_t { None = 0, Dword = 4, Qword = 8 };
enum class EndOfPipeWriteValueSource { Immediate, ReferenceClock };
enum class EndOfPipeSignalStage { Recorded, GpuComplete };
enum class EndOfPipeConditionalInterruptAction {
	Unsupported,
	Compare32LessOrEqual,
	Compare64LessOrEqual,
};
enum class EndOfPipeReferenceClockAction {
	Unsupported,
	Write,
	WriteBack,
	WriteAndInterrupt,
	WriteBackAndInterrupt,
};

[[nodiscard]] constexpr EndOfPipeConditionalInterruptAction
ResolveEndOfPipeConditionalInterruptAction(uint32_t interrupt_selector) noexcept {
	switch (interrupt_selector) {
		case 5u: return EndOfPipeConditionalInterruptAction::Compare32LessOrEqual;
		case 6u: return EndOfPipeConditionalInterruptAction::Compare64LessOrEqual;
		default: return EndOfPipeConditionalInterruptAction::Unsupported;
	}
}

struct EndOfPipeCompare32Payload {
	const void* address   = nullptr;
	uint32_t    reference = 0;
};

struct EndOfPipeCompare64Payload {
	const void* address   = nullptr;
	uint64_t    reference = 0;
};

template <typename Completion>
[[nodiscard]] inline bool CompleteEndOfPipeCompare64Interrupt(
    const EndOfPipeCompare64Payload& compare, EndOfPipeSignalStage stage,
    Completion&& completion) {
	if (stage != EndOfPipeSignalStage::Recorded &&
	    stage != EndOfPipeSignalStage::GpuComplete) {
		return false;
	}
	if (stage == EndOfPipeSignalStage::Recorded) {
		return true;
	}
	if (compare.address == nullptr) {
		return false;
	}
	uint64_t observed = 0;
	std::memcpy(&observed, compare.address, sizeof(observed));
	if (observed <= compare.reference) {
		std::forward<Completion>(completion)();
	}
	return true;
}

template <typename Completion>
[[nodiscard]] inline bool CompleteEndOfPipeCompare32Interrupt(
    const EndOfPipeCompare32Payload& compare, EndOfPipeSignalStage stage,
    Completion&& completion) {
	if (stage != EndOfPipeSignalStage::Recorded &&
	    stage != EndOfPipeSignalStage::GpuComplete) {
		return false;
	}
	if (stage == EndOfPipeSignalStage::Recorded) {
		return true;
	}
	if (compare.address == nullptr) {
		return false;
	}
	uint32_t observed = 0;
	std::memcpy(&observed, compare.address, sizeof(observed));
	if (observed <= compare.reference) {
		std::forward<Completion>(completion)();
	}
	return true;
}

[[nodiscard]] constexpr EndOfPipeReferenceClockAction ResolveEndOfPipeReferenceClockAction(
    uint32_t cache_action, bool with_interrupt) noexcept {
	switch (cache_action) {
		case 0x00: return with_interrupt ? EndOfPipeReferenceClockAction::WriteAndInterrupt
		                                 : EndOfPipeReferenceClockAction::Write;
		case 0x38: return with_interrupt ? EndOfPipeReferenceClockAction::WriteBackAndInterrupt
		                                 : EndOfPipeReferenceClockAction::WriteBack;
		default: return EndOfPipeReferenceClockAction::Unsupported;
	}
}

struct EndOfPipeWritePayload {
	void*                         destination = nullptr;
	uint64_t                      value       = 0;
	EndOfPipeWriteSize            size        = EndOfPipeWriteSize::None;
	EndOfPipeWriteValueSource     value_source = EndOfPipeWriteValueSource::Immediate;
};

[[nodiscard]] inline bool CommitEndOfPipeWrite(const EndOfPipeWritePayload& write) noexcept {
	if (write.value_source != EndOfPipeWriteValueSource::Immediate) {
		return false;
	}
	switch (write.size) {
		case EndOfPipeWriteSize::None: return write.destination == nullptr;
		case EndOfPipeWriteSize::Dword: {
			if (write.destination == nullptr) {
				return false;
			}
			const auto value = static_cast<uint32_t>(write.value);
			std::memcpy(write.destination, &value, sizeof(value));
			return true;
		}
		case EndOfPipeWriteSize::Qword:
			if (write.destination == nullptr) {
				return false;
			}
			std::memcpy(write.destination, &write.value, sizeof(write.value));
			return true;
	}
	return false;
}

template <typename ReferenceClock, typename Completion>
[[nodiscard]] inline bool CompleteEndOfPipeSignal(const EndOfPipeWritePayload& write,
                                                   EndOfPipeSignalStage stage,
                                                   ReferenceClock&& reference_clock,
                                                   Completion&& completion) {
	if (stage != EndOfPipeSignalStage::Recorded &&
	    stage != EndOfPipeSignalStage::GpuComplete) {
		return false;
	}
	if (stage == EndOfPipeSignalStage::Recorded) {
		return true;
	}
	auto resolved = write;
	switch (resolved.value_source) {
		case EndOfPipeWriteValueSource::Immediate: break;
		case EndOfPipeWriteValueSource::ReferenceClock:
			if (resolved.size != EndOfPipeWriteSize::Qword) {
				return false;
			}
			resolved.value        = std::forward<ReferenceClock>(reference_clock)();
			resolved.value_source = EndOfPipeWriteValueSource::Immediate;
			break;
	}
	if (!CommitEndOfPipeWrite(resolved)) {
		return false;
	}
	std::forward<Completion>(completion)();
	return true;
}

template <typename Completion>
[[nodiscard]] inline bool CompleteEndOfPipeSignal(const EndOfPipeWritePayload& write,
                                                   EndOfPipeSignalStage stage,
                                                   Completion&& completion) {
	if (write.value_source != EndOfPipeWriteValueSource::Immediate) {
		return false;
	}
	return CompleteEndOfPipeSignal(write, stage, [] { return uint64_t {0}; },
	                               std::forward<Completion>(completion));
}

} // namespace Libs::Graphics::Sync

#endif // EMULATOR_SRC_GRAPHICS_HOST_GPU_RENDERER_SYNCCONTRACTS_H_
