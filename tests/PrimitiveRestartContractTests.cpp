#include "graphics/host_gpu/renderer/renderDraw.h"

#include <cstdio>
#include <cstdlib>

namespace {

using Libs::Graphics::ResolvePrimitiveRestart;
using Libs::Graphics::TranslatePrimitiveRestartIndex;

void Check(bool value, const char *message) {
  if (!value) {
    std::fprintf(stderr, "PrimitiveRestartContractTests: failed: %s\n",
                 message);
    std::abort();
  }
}

void TestTopologyAndEnableState() {
	Check(!ResolvePrimitiveRestart(false, false, 7u, 16u, 16u,
                                 vk::PrimitiveTopology::eTriangleStrip)
             .enable,
        "disabled guest primitive restart enabled the host pipeline");
  Check(!ResolvePrimitiveRestart(true, false, 7u, 16u, 16u,
                                 vk::PrimitiveTopology::eTriangleList)
             .enable,
        "primitive restart was enabled for an unsupported list topology");
  Check(ResolvePrimitiveRestart(true, false, 7u, 16u, 16u,
                                vk::PrimitiveTopology::eTriangleFan)
            .enable,
        "triangle-fan primitive restart was discarded");
}

void TestResetTokenTranslation() {
  auto state = ResolvePrimitiveRestart(true, false, 0xffffffffu, 16u, 16u,
                                       vk::PrimitiveTopology::eTriangleStrip);
  Check(state.enable && !state.remap && state.guest_token == 0xffffu &&
            state.host_token == 0xffffu,
        "the default 16-bit reset token was not preserved");

	state = ResolvePrimitiveRestart(true, false, 7u, 16u, 16u,
	                                vk::PrimitiveTopology::eLineStrip);
	Check(state.enable && state.remap && state.guest_token == 7u &&
	          state.host_token == 0xffffu,
	      "a custom 16-bit reset token was not scheduled for remapping");
	Check(TranslatePrimitiveRestartIndex(state, 7u) == 0xffffu &&
	          TranslatePrimitiveRestartIndex(state, 8u) == 8u,
	      "custom reset-token translation changed the wrong index");

  state = ResolvePrimitiveRestart(true, false, 0xffu, 8u, 16u,
                                  vk::PrimitiveTopology::eTriangleStrip);
  Check(state.enable && state.remap && state.guest_token == 0xffu &&
            state.host_token == 0xffffu,
        "an expanded 8-bit reset token was not remapped to the host token");

  state = ResolvePrimitiveRestart(
      true, false, 0xffffffffu, 32u, 32u,
      vk::PrimitiveTopology::eTriangleStripWithAdjacency);
	Check(state.enable && !state.remap && state.host_token == 0xffffffffu,
	      "the native 32-bit reset token was changed");
	Check(TranslatePrimitiveRestartIndex(state, 0xffffffffu) == 0xffffffffu,
	      "the native restart token was not preserved");
}

void TestMatchAllBits() {
  Check(!ResolvePrimitiveRestart(true, true, 0x10000u, 16u, 16u,
                                 vk::PrimitiveTopology::eTriangleStrip)
             .enable,
        "an unmatchable full-width reset token enabled primitive restart");
  const auto state = ResolvePrimitiveRestart(
      true, false, 0x10007u, 16u, 16u, vk::PrimitiveTopology::eTriangleStrip);
  Check(state.enable && state.remap && state.guest_token == 7u,
        "relevant-bit matching did not truncate the reset token to the guest "
        "index width");
}

} // namespace

int main() {
  TestTopologyAndEnableState();
  TestResetTokenTranslation();
  TestMatchAllBits();
  std::puts("PrimitiveRestartContractTests: all cases passed");
  return 0;
}
