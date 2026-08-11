#include "graphics/host_gpu/renderer/image/imageInfo.h"

#include <cstdio>
#include <cstdlib>

namespace {

using Libs::Graphics::ImageMetadataIdentityMatches;
using Libs::Graphics::ImageMetadataInfo;
using Libs::Graphics::ImageMetadataKind;

void Check(bool value, const char* message) {
	if (!value) {
		std::fprintf(stderr, "DepthMetadataContractTests: failed: %s\n", message);
		std::abort();
	}
}

void TestHtileRangeIdentity() {
	constexpr uint64_t      metadata_address = 0x106d48000ull;
	const ImageMetadataInfo one_slice {.range = {metadata_address, 0x8000},
	                                   .kind  = ImageMetadataKind::Htile};
	const ImageMetadataInfo same_range {.range = {metadata_address, 0x8000},
	                                    .kind  = ImageMetadataKind::Htile};
	const ImageMetadataInfo two_slices {.range = {metadata_address, 0x10000},
	                                    .kind  = ImageMetadataKind::Htile};
	const ImageMetadataInfo relocated {.range = {metadata_address + 0x8000, 0x8000},
	                                   .kind  = ImageMetadataKind::Htile};

	Check(ImageMetadataIdentityMatches(one_slice, same_range),
	      "an unchanged HTILE allocation did not retain its cache identity");
	Check(!ImageMetadataIdentityMatches(one_slice, two_slices),
	      "HTILE allocations with different complete footprints shared a cache identity");
	Check(!ImageMetadataIdentityMatches(one_slice, relocated),
	      "HTILE allocations at different addresses shared a cache identity");
}

void TestColorMetadataIdentityRemainsAddressBased() {
	constexpr uint64_t      metadata_address = 0x570520000ull;
	const ImageMetadataInfo descriptor {.range = {metadata_address, 0},
	                                    .kind  = ImageMetadataKind::Dcc};
	const ImageMetadataInfo target {.range = {metadata_address, 0x4000},
	                                .kind  = ImageMetadataKind::Dcc};

	Check(ImageMetadataIdentityMatches(descriptor, target),
	      "DCC identity stopped matching the independently encoded metadata address");
}

} // namespace

int main() {
	TestHtileRangeIdentity();
	TestColorMetadataIdentityRemainsAddressBased();
	std::puts("DepthMetadataContractTests: all cases passed");
	return 0;
}
