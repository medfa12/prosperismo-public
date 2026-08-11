#include "graphics/guest_gpu/command_processor/memSemaphoreContracts.h"
#include "graphics/guest_gpu/pm4.h"
#include "graphics/host_gpu/renderer/syncContracts.h"

#include <array>
#include <cstdio>
#include <cstdlib>

namespace {

void Check(bool value, const char *text) {
  if (!value) {
    std::fprintf(stderr, "Prosperismo graphics sync contract test failed: %s\n",
                 text);
    std::abort();
  }
}

void TestEndOfPipeInterruptContextIdentity() {
  using Libs::Graphics::Sync::ResolveEndOfPipeInterruptContext;

  constexpr uint64_t payload = 0x11223344a5a5a5a5ull;
  Check(ResolveEndOfPipeInterruptContext(payload, 0) == 0,
        "a zero interrupt context was replaced by the write payload");
  Check(ResolveEndOfPipeInterruptContext(payload, 0x0800002au) == 0x2au,
        "reserved interrupt-context bits were delivered to the event queue");
  Check(ResolveEndOfPipeInterruptContext(payload, 0x123456u) == 0x123456u,
        "a valid interrupt context was not preserved");
}

void TestConditionalEndOfPipeInterruptCompletesAtGpuBoundary() {
  using namespace Libs::Graphics::Sync;

  Check(ResolveEndOfPipeConditionalInterruptAction(5) ==
            EndOfPipeConditionalInterruptAction::Compare32LessOrEqual,
        "a 32-bit conditional EOP interrupt was rejected");
  Check(ResolveEndOfPipeConditionalInterruptAction(7) ==
            EndOfPipeConditionalInterruptAction::Unsupported,
        "an undefined conditional EOP interrupt was accepted");

  uint32_t observed = 7;
  uint32_t interrupt_count = 0;
  const EndOfPipeCompare32Payload compare {
      .address = &observed,
      .reference = 7,
  };
  Check(CompleteEndOfPipeCompare32Interrupt(
            compare, EndOfPipeSignalStage::Recorded, [&] { ++interrupt_count; }),
        "recording a valid 32-bit conditional EOP interrupt failed");
  Check(interrupt_count == 0,
        "a conditional EOP interrupt fired before GPU completion");

  observed = 0x80000000u;
  Check(CompleteEndOfPipeCompare32Interrupt(
            compare, EndOfPipeSignalStage::GpuComplete, [&] { ++interrupt_count; }),
        "GPU completion rejected a valid 32-bit conditional EOP interrupt");
  Check(interrupt_count == 0,
        "a conditional EOP interrupt did not use an unsigned comparison");

  observed = 7;
  Check(CompleteEndOfPipeCompare32Interrupt(
            compare, EndOfPipeSignalStage::GpuComplete, [&] { ++interrupt_count; }),
        "GPU completion rejected an equal 32-bit conditional EOP value");
  observed = 6;
  Check(CompleteEndOfPipeCompare32Interrupt(
            compare, EndOfPipeSignalStage::GpuComplete, [&] { ++interrupt_count; }),
        "GPU completion rejected a smaller 32-bit conditional EOP value");
  Check(interrupt_count == 2,
        "the conditional EOP interrupt did not use an inclusive unsigned comparison");
}

void TestConditional64EndOfPipeInterruptCompletesAtGpuBoundary() {
  using namespace Libs::Graphics::Sync;

  Check(ResolveEndOfPipeConditionalInterruptAction(6) ==
            EndOfPipeConditionalInterruptAction::Compare64LessOrEqual,
        "a 64-bit conditional EOP interrupt was rejected");

  constexpr uint64_t reference = 0x0000000100000007ull;
  uint64_t observed = reference;
  uint32_t interrupt_count = 0;
  const EndOfPipeCompare64Payload compare {
      .address = &observed,
      .reference = reference,
  };
  Check(CompleteEndOfPipeCompare64Interrupt(
            compare, EndOfPipeSignalStage::Recorded, [&] { ++interrupt_count; }),
        "recording a valid 64-bit conditional EOP interrupt failed");
  Check(interrupt_count == 0,
        "a 64-bit conditional EOP interrupt fired before GPU completion");

  observed = 0x8000000000000000ull;
  Check(CompleteEndOfPipeCompare64Interrupt(
            compare, EndOfPipeSignalStage::GpuComplete, [&] { ++interrupt_count; }),
        "GPU completion rejected a valid 64-bit conditional EOP interrupt");
  Check(interrupt_count == 0,
        "a 64-bit conditional EOP interrupt did not use an unsigned comparison");

  observed = reference;
  Check(CompleteEndOfPipeCompare64Interrupt(
            compare, EndOfPipeSignalStage::GpuComplete, [&] { ++interrupt_count; }),
        "GPU completion rejected an equal 64-bit conditional EOP value");
  observed = reference - 1;
  Check(CompleteEndOfPipeCompare64Interrupt(
            compare, EndOfPipeSignalStage::GpuComplete, [&] { ++interrupt_count; }),
        "GPU completion rejected a smaller 64-bit conditional EOP value");
  Check(interrupt_count == 2,
        "the 64-bit conditional EOP interrupt did not use an inclusive comparison");
}

void TestEndOfPipeWriteCompletesAtGpuBoundary() {
  using namespace Libs::Graphics::Sync;

  constexpr uint32_t initial = 0x11223344u;
  constexpr uint32_t value = 0xa5a5c3c3u;
  uint32_t label = initial;
  bool completion_called = false;
  uint32_t completion_observed = 0;
  const EndOfPipeWritePayload write {
      .destination = &label,
      .value = value,
      .size = EndOfPipeWriteSize::Dword,
  };

  Check(CompleteEndOfPipeSignal(write, EndOfPipeSignalStage::Recorded, [&] {
          completion_called = true;
          completion_observed = label;
        }),
        "recording an end-of-pipe label write rejected a valid payload");
  Check(label == initial,
        "an end-of-pipe label became visible before GPU completion");
  Check(!completion_called,
        "an end-of-pipe completion callback ran while the command was recorded");

  Check(CompleteEndOfPipeSignal(write, EndOfPipeSignalStage::GpuComplete, [&] {
          completion_called = true;
          completion_observed = label;
        }),
        "GPU completion rejected a valid end-of-pipe label write");
  Check(label == value, "GPU completion did not publish the end-of-pipe label");
  Check(completion_called && completion_observed == value,
        "the end-of-pipe callback ran before its label write became visible");

  constexpr uint64_t qword_initial = 0x0102030405060708ull;
  constexpr uint64_t qword_value = 0xfedcba9876543210ull;
  uint64_t qword_label = qword_initial;
  const EndOfPipeWritePayload qword_write {
      .destination = &qword_label,
      .value = qword_value,
      .size = EndOfPipeWriteSize::Qword,
  };
  Check(CompleteEndOfPipeSignal(qword_write, EndOfPipeSignalStage::Recorded, [] {}),
        "recording a qword end-of-pipe write rejected a valid payload");
  Check(qword_label == qword_initial,
        "a qword end-of-pipe label became visible before GPU completion");
  Check(CompleteEndOfPipeSignal(qword_write, EndOfPipeSignalStage::GpuComplete, [] {}),
        "GPU completion rejected a qword end-of-pipe label write");
  Check(qword_label == qword_value,
        "GPU completion did not publish the qword end-of-pipe label");
}

void TestReferenceClockIsSampledAtGpuCompletion() {
  using namespace Libs::Graphics::Sync;

  constexpr uint64_t initial = 0x0102030405060708ull;
  constexpr uint64_t recorded_clock = 0x1111222233334444ull;
  constexpr uint64_t completed_clock = 0xaaaabbbbccccddddull;
  uint64_t label = initial;
  uint64_t current_clock = recorded_clock;
  uint32_t sample_count = 0;
  const EndOfPipeWritePayload write {
      .destination = &label,
      .size = EndOfPipeWriteSize::Qword,
      .value_source = EndOfPipeWriteValueSource::ReferenceClock,
  };
  auto read_reference_clock = [&] {
    ++sample_count;
    return current_clock;
  };

  Check(CompleteEndOfPipeSignal(write, EndOfPipeSignalStage::Recorded,
                                read_reference_clock, [] {}),
        "recording a reference-clock write rejected a valid payload");
  Check(label == initial,
        "a reference-clock label became visible before GPU completion");
  Check(sample_count == 0,
        "the reference clock was sampled while the command was recorded");

  current_clock = completed_clock;
  Check(CompleteEndOfPipeSignal(write, EndOfPipeSignalStage::GpuComplete,
                                read_reference_clock, [] {}),
        "GPU completion rejected a reference-clock label write");
  Check(sample_count == 1,
        "GPU completion did not sample the reference clock exactly once");
  Check(label == completed_clock,
        "the reference clock was sampled before GPU completion");
}

void TestReferenceClockWriteConfirmInterrupt() {
  using namespace Libs::Graphics::Sync;

  Check(ResolveEndOfPipeReferenceClockAction(0x00, false) ==
            EndOfPipeReferenceClockAction::Write,
        "a reference-clock write without an interrupt changed action");
  Check(ResolveEndOfPipeReferenceClockAction(0x38, false) ==
            EndOfPipeReferenceClockAction::WriteBack,
        "a reference-clock writeback without an interrupt changed action");
  Check(ResolveEndOfPipeReferenceClockAction(0x00, true) ==
            EndOfPipeReferenceClockAction::WriteAndInterrupt,
        "a reference-clock write-confirm interrupt was rejected");
  Check(ResolveEndOfPipeReferenceClockAction(0x38, true) ==
            EndOfPipeReferenceClockAction::WriteBackAndInterrupt,
        "a reference-clock writeback-confirm interrupt was rejected");
  Check(ResolveEndOfPipeReferenceClockAction(0x01, true) ==
            EndOfPipeReferenceClockAction::Unsupported,
        "an unknown reference-clock cache action was accepted");

  constexpr uint64_t completed_clock = 0x123456789abcdef0ull;
  uint64_t label = 0;
  uint64_t callback_value = 0;
  const EndOfPipeWritePayload write {
      .destination = &label,
      .size = EndOfPipeWriteSize::Qword,
      .value_source = EndOfPipeWriteValueSource::ReferenceClock,
  };
  Check(CompleteEndOfPipeSignal(
          write, EndOfPipeSignalStage::GpuComplete,
          [] { return completed_clock; }, [&] { callback_value = label; }),
        "a reference-clock write-confirm completion was rejected");
  Check(label == completed_clock && callback_value == completed_clock,
        "the reference-clock interrupt completed before its label write");
}

void TestNativeMemSemaphorePacketAndAtomicState() {
  using namespace Libs::Graphics;

  constexpr uint64_t address = 0x0000123455667788ull;
  constexpr std::array<uint32_t, 3> signal_payload = {
      static_cast<uint32_t>(address), static_cast<uint32_t>(address >> 32u),
      (6u << 29u) | (1u << 20u) | (1u << 16u)};
  constexpr std::array<uint32_t, 3> wait_payload = {
      static_cast<uint32_t>(address), static_cast<uint32_t>(address >> 32u), 7u << 29u};

  MemSemaphorePacket packet {};
  Check(DecodeMemSemaphorePacket(
            KYTY_PM4(4u, Pm4::IT_MEM_SEMAPHORE, Pm4::R_ZERO),
            signal_payload.data(), packet),
        "the native MEM_SEMAPHORE signal packet remained unsupported");
  Check(packet.address == address && packet.operation == MemSemaphoreOperation::Signal &&
            packet.signal_type == MemSemaphoreSignalType::WriteOne && packet.wait_for_mailbox,
        "the native MEM_SEMAPHORE signal fields decoded incorrectly");
  Check(DecodeMemSemaphorePacket(
            KYTY_PM4(4u, Pm4::IT_MEM_SEMAPHORE, Pm4::R_ZERO), wait_payload.data(), packet) &&
            packet.operation == MemSemaphoreOperation::Wait,
        "the native MEM_SEMAPHORE wait selector decoded incorrectly");
  Check(!DecodeMemSemaphorePacket(
            KYTY_PM4(3u, Pm4::IT_MEM_SEMAPHORE, Pm4::R_ZERO), wait_payload.data(), packet),
        "a short MEM_SEMAPHORE packet was accepted");
  Check(!DecodeMemSemaphorePacket(
            KYTY_PM4(4u, Pm4::IT_WRITE_DATA, Pm4::R_ZERO), wait_payload.data(), packet),
        "an unrelated four-DWORD packet was classified as MEM_SEMAPHORE");

  alignas(8) uint64_t counter = 2;
  Check(TryConsumeMemSemaphore(counter) == MemSemaphoreWaitResult::Consumed && counter == 1,
        "a nonzero semaphore wait did not atomically consume one signal");
  Check(TryConsumeMemSemaphore(counter) == MemSemaphoreWaitResult::Consumed && counter == 0,
        "the final semaphore signal was not consumed");
  Check(TryConsumeMemSemaphore(counter) == MemSemaphoreWaitResult::Waiting && counter == 0,
        "a zero semaphore counter did not wait without mutation");

  counter = 5;
  Check(CompleteMemSemaphoreSignal(counter, MemSemaphoreSignalType::Increment,
                                   MemSemaphoreSignalStage::Recorded) &&
            counter == 5,
        "a semaphore signal became visible before GPU completion");
  Check(CompleteMemSemaphoreSignal(counter, MemSemaphoreSignalType::Increment,
                                   MemSemaphoreSignalStage::GpuComplete) &&
            counter == 6,
        "GPU completion did not increment the semaphore counter");
  Check(CompleteMemSemaphoreSignal(counter, MemSemaphoreSignalType::WriteOne,
                                   MemSemaphoreSignalStage::GpuComplete) &&
            counter == 1,
        "GPU completion did not set the semaphore counter to one");
  Check(!CompleteMemSemaphoreSignal(counter, static_cast<MemSemaphoreSignalType>(2u),
                                    MemSemaphoreSignalStage::Recorded) &&
            counter == 1,
        "an unknown semaphore signal type was accepted or mutated the counter");
}

} // namespace

int main() {
  TestEndOfPipeInterruptContextIdentity();
  TestConditionalEndOfPipeInterruptCompletesAtGpuBoundary();
  TestConditional64EndOfPipeInterruptCompletesAtGpuBoundary();
  TestEndOfPipeWriteCompletesAtGpuBoundary();
  TestReferenceClockIsSampledAtGpuCompletion();
  TestReferenceClockWriteConfirmInterrupt();
  TestNativeMemSemaphorePacketAndAtomicState();
  return 0;
}
