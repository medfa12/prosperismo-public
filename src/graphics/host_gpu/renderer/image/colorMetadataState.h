#ifndef PROSPERISMO_GRAPHICS_HOST_GPU_RENDERER_IMAGE_COLORMETADATASTATE_H_
#define PROSPERISMO_GRAPHICS_HOST_GPU_RENDERER_IMAGE_COLORMETADATASTATE_H_

#include <cstdint>

namespace Libs::Graphics {

// Transient color-metadata requirements for one Sony render-target lifetime.
// Vulkan images are always expanded, but an uncompressed guest view is not
// allowed to consume those pixels until the matching CB metadata operation has
// completed. Addresses remain provenance, even after decompression.
struct ColorMetadataBinding {
	uint64_t cmask_address = 0;
	uint64_t fmask_address = 0;
	uint64_t dcc_address   = 0;
	bool     fast_clear    = false;
	bool     fmask_compressed = false;
	bool     dcc_compressed   = false;
	bool     invalid_fmask_configuration = false;
};

struct ColorMetadataOperation {
	bool recognized            = false;
	bool eliminates_fast_clear = false;
	bool decompresses_fmask    = false;
	bool decompresses_dcc      = false;
};

[[nodiscard]] constexpr ColorMetadataOperation DecodeColorMetadataOperation(uint8_t mode) {
	switch (mode) {
		// Sony SDK 10 CxCbControl::Mode::kEliminateFastClear.
		case 2: return {true, true, false, false};
		// kFmaskDecompress includes fast-clear elimination.
		case 5: return {true, true, true, false};
		// kDccDecompress includes both preceding operations.
		case 6: return {true, true, true, true};
		default: return {};
	}
}

class ColorMetadataState final {
public:
	constexpr ColorMetadataState() = default;
	explicit constexpr ColorMetadataState(const ColorMetadataBinding& binding) { Reset(binding); }

	constexpr void Reset(const ColorMetadataBinding& binding) {
		m_binding           = binding;
		m_fast_clear        = binding.fast_clear;
		m_fmask_compressed  = binding.fmask_compressed;
		m_dcc_compressed    = binding.dcc_compressed;
		m_invalid_binding   = binding.invalid_fmask_configuration ||
		                      (m_fast_clear && binding.cmask_address == 0) ||
		                      (m_fmask_compressed && binding.fmask_address == 0) ||
		                      (m_dcc_compressed && binding.dcc_address == 0);
	}

	[[nodiscard]] constexpr bool Apply(uint8_t mode) {
		const auto operation = DecodeColorMetadataOperation(mode);
		if (!operation.recognized) {
			return false;
		}
		if (operation.eliminates_fast_clear) {
			m_fast_clear = false;
		}
		if (operation.decompresses_fmask) {
			m_fmask_compressed = false;
		}
		if (operation.decompresses_dcc) {
			m_dcc_compressed = false;
		}
		return true;
	}

	[[nodiscard]] constexpr bool CanReadUncompressed() const {
		return !m_invalid_binding && !m_fast_clear && !m_fmask_compressed && !m_dcc_compressed;
	}
	[[nodiscard]] constexpr bool FastClearPending() const { return m_fast_clear; }
	[[nodiscard]] constexpr bool FmaskDecompressPending() const { return m_fmask_compressed; }
	[[nodiscard]] constexpr bool DccDecompressPending() const { return m_dcc_compressed; }
	[[nodiscard]] constexpr bool InvalidBinding() const { return m_invalid_binding; }
	[[nodiscard]] constexpr const ColorMetadataBinding& Binding() const { return m_binding; }

private:
	ColorMetadataBinding m_binding {};
	bool                 m_fast_clear       = false;
	bool                 m_fmask_compressed = false;
	bool                 m_dcc_compressed   = false;
	bool                 m_invalid_binding  = false;
};

// Production alias decision shared with the texture cache. Render-target
// identity remains exact; only sampled/storage views may consume an expanded
// DCC-backed image through a metadata-free descriptor, and only after all
// required transitions completed.
[[nodiscard]] constexpr bool CanUseExpandedColorAlias(bool sampled_or_storage,
                                                       bool requested_uncompressed,
                                                       bool cached_has_dcc,
                                                       const ColorMetadataState& state) {
	return sampled_or_storage && requested_uncompressed && cached_has_dcc &&
	       state.CanReadUncompressed();
}

} // namespace Libs::Graphics

#endif // PROSPERISMO_GRAPHICS_HOST_GPU_RENDERER_IMAGE_COLORMETADATASTATE_H_
