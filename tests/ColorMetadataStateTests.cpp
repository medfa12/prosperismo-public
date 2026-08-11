#include "graphics/host_gpu/renderer/image/colorMetadataState.h"

#include <cstdio>
#include <cstdlib>

namespace {

using Libs::Graphics::ColorMetadataBinding;
using Libs::Graphics::ColorMetadataState;
using Libs::Graphics::CanUseExpandedColorAlias;
using Libs::Graphics::DecodeColorMetadataOperation;

void Check(bool value, const char* message) {
	if (!value) {
		std::fprintf(stderr, "ColorMetadataStateTests: failed: %s\n", message);
		std::abort();
	}
}

ColorMetadataBinding CompressedBinding() {
	return {
	    .cmask_address    = 0x10000,
	    .fmask_address    = 0x20000,
	    .dcc_address      = 0x30000,
	    .fast_clear       = true,
	    .fmask_compressed = true,
	    .dcc_compressed   = true,
	};
}

} // namespace

int main() {
	const auto eliminate = DecodeColorMetadataOperation(2);
	const auto fmask     = DecodeColorMetadataOperation(5);
	const auto dcc       = DecodeColorMetadataOperation(6);
	Check(eliminate.recognized && eliminate.eliminates_fast_clear &&
	          !eliminate.decompresses_fmask && !eliminate.decompresses_dcc,
	      "mode 2 effects");
	Check(fmask.recognized && fmask.eliminates_fast_clear && fmask.decompresses_fmask &&
	          !fmask.decompresses_dcc,
	      "mode 5 composition");
	Check(dcc.recognized && dcc.eliminates_fast_clear && dcc.decompresses_fmask &&
	          dcc.decompresses_dcc,
	      "mode 6 composition");
	Check(!DecodeColorMetadataOperation(4).recognized, "unknown mode fails closed");

	ColorMetadataState fast_clear_only({.cmask_address = 0x10000, .fast_clear = true});
	Check(!CanUseExpandedColorAlias(true, true, true, fast_clear_only),
	      "fast-clear lifetime rejects raw alias before mode 2");
	Check(fast_clear_only.Apply(2) &&
	          CanUseExpandedColorAlias(true, true, true, fast_clear_only),
	      "mode 2 publishes a fast-clear-only lifetime");

	ColorMetadataState fmask_only({.cmask_address = 0x10000,
	                               .fmask_address = 0x20000,
	                               .fast_clear = true,
	                               .fmask_compressed = true});
	Check(fmask_only.Apply(5) && CanUseExpandedColorAlias(true, true, true, fmask_only),
	      "mode 5 implicitly eliminates fast clear and decompresses FMASK");

	ColorMetadataState state(CompressedBinding());
	Check(!state.CanReadUncompressed(), "compressed lifetime rejects raw alias");
	Check(!CanUseExpandedColorAlias(true, true, true, state),
	      "sampled/storage raw alias remains separate before mode 6");
	Check(state.Apply(2), "mode 2 accepted");
	Check(!state.FastClearPending() && state.FmaskDecompressPending() &&
	          state.DccDecompressPending() && !state.CanReadUncompressed(),
	      "mode 2 only eliminates fast clear");
	Check(state.Apply(5), "mode 5 accepted");
	Check(!state.FmaskDecompressPending() && state.DccDecompressPending() &&
	          !state.CanReadUncompressed(),
	      "mode 5 retains DCC requirement");
	Check(state.Apply(6) && state.CanReadUncompressed(),
	      "mode 6 publishes expanded pixels to raw views");
	Check(CanUseExpandedColorAlias(true, true, true, state),
	      "sampled/storage raw alias reuses exact DCC host image after mode 6");
	Check(!CanUseExpandedColorAlias(false, true, true, state),
	      "render-target binding never uses decompressed alias exception");
	Check(!CanUseExpandedColorAlias(true, false, true, state),
	      "compressed request retains exact metadata identity");
	Check(!CanUseExpandedColorAlias(true, true, false, state),
	      "metadata-free cached image does not need alias exception");

	state.Reset(CompressedBinding());
	Check(!state.CanReadUncompressed(), "normal CB write resets compressed lifetime");
	Check(!CanUseExpandedColorAlias(true, true, true, state),
	      "subsequent normal CB write closes raw alias");
	Check(!state.Apply(7) && !state.CanReadUncompressed(),
	      "unknown operation cannot publish pixels");

	auto invalid = CompressedBinding();
	invalid.dcc_address = 0;
	state.Reset(invalid);
	Check(state.InvalidBinding(), "missing active metadata address is invalid");
	Check(state.Apply(6) && !state.CanReadUncompressed(),
	      "invalid binding remains fail closed after operation");

	state.Reset({.fmask_address = 0x20000,
	             .invalid_fmask_configuration = true});
	Check(state.InvalidBinding() && state.Apply(5) && !state.CanReadUncompressed(),
	      "invalid Sony FMASK mode remains fail closed");

	std::puts("ColorMetadataStateTests: all cases passed");
	return 0;
}
