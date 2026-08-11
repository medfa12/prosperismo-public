#include "common/abi.h"
#include "loader/symbolDatabase.h"

#define STB_IMAGE_IMPLEMENTATION
#include "stb_image.h"

#include <array>
#include <cstdint>
#include <cstdio>
#include <cstdlib>

namespace Kyty::Libs {
void PrintNameImpl(const char*, const char*, const char*) {}
} // namespace Kyty::Libs

namespace Libs {
void InitPngDec_1(Loader::SymbolDatabase* symbols);
} // namespace Libs

namespace {

constexpr int32_t PNG_DEC_ERROR_INVALID_WORK_MEMORY = static_cast<int32_t>(0x80690005u);
constexpr int32_t PNG_DEC_ERROR_INVALID_PARAM       = static_cast<int32_t>(0x80690003u);

struct CreateParam {
	uint32_t this_size;
	uint32_t attribute;
	uint32_t max_image_width;
};

struct DecodeParam {
	const void* png_mem_addr;
	void*       image_mem_addr;
	uint32_t    png_mem_size;
	uint32_t    image_mem_size;
	uint16_t    pixel_format;
	uint16_t    alpha_value;
	uint32_t    image_pitch;
};

struct ImageInfo {
	uint32_t image_width;
	uint32_t image_height;
	uint16_t color_space;
	uint16_t bit_depth;
	uint32_t image_flag;
};

using QueryApi  = int32_t(KYTY_SYSV_ABI*)(const CreateParam*);
using CreateApi = int32_t(KYTY_SYSV_ABI*)(const CreateParam*, void*, uint32_t, void**);
using DecodeApi = int32_t(KYTY_SYSV_ABI*)(void*, const DecodeParam*, ImageInfo*);
using DeleteApi = int32_t(KYTY_SYSV_ABI*)(void*);

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "Prosperismo PngDec contract test failed: %s\n", text);
		std::abort();
	}
}

std::array<uint8_t, 33> MakeHeader(uint32_t width, uint32_t height) {
	std::array<uint8_t, 33> png {0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
	                             0x00, 0x00, 0x00, 0x0d, 'I',  'H',  'D',  'R'};
	auto put_be32 = [&png](size_t offset, uint32_t value) {
		png[offset + 0] = static_cast<uint8_t>(value >> 24u);
		png[offset + 1] = static_cast<uint8_t>(value >> 16u);
		png[offset + 2] = static_cast<uint8_t>(value >> 8u);
		png[offset + 3] = static_cast<uint8_t>(value);
	};
	put_be32(16, width);
	put_be32(20, height);
	png[24] = 8;
	png[25] = 6;
	return png;
}

void TestCreationWidthConstrainsDecode() {
	Loader::SymbolDatabase symbols;
	Libs::InitPngDec_1(&symbols);

	const auto resolve = [&symbols](const char* nid) {
		const auto* record = symbols.FindByNid(nid, Loader::SymbolType::Func);
		Check(record != nullptr, "PngDec export is not registered");
		return record->vaddr;
	};
	const auto query  = reinterpret_cast<QueryApi>(resolve("-6srIGbLTIU"));
	const auto create = reinterpret_cast<CreateApi>(resolve("m0uW+8pFyaw"));
	const auto decode = reinterpret_cast<DecodeApi>(resolve("WC216DD3El4"));
	const auto destroy = reinterpret_cast<DeleteApi>(resolve("QbD+eENEwo8"));

	alignas(16) std::array<uint8_t, 64> work {};
	for (uint32_t invalid_width: {0u, 1000001u}) {
		const CreateParam invalid_param {sizeof(CreateParam), 0, invalid_width};
		Check(query(&invalid_param) == PNG_DEC_ERROR_INVALID_PARAM,
		      "out-of-domain maximum width was accepted by memory query");
		void* invalid_handle = reinterpret_cast<void*>(0x12345678ull);
		Check(create(&invalid_param, work.data(), static_cast<uint32_t>(work.size()),
		             &invalid_handle) == PNG_DEC_ERROR_INVALID_PARAM,
		      "out-of-domain maximum width was accepted by decoder creation");
		Check(invalid_handle == reinterpret_cast<void*>(0x12345678ull),
		      "failed decoder creation modified its output handle");
	}

	const CreateParam create_param {sizeof(CreateParam), 0, 1};
	const int32_t required_size = query(&create_param);
	Check(required_size > 0 && required_size <= 64, "unexpected focused decoder work size");

	void* handle = nullptr;
	Check(create(&create_param, work.data(), static_cast<uint32_t>(work.size()), &handle) == 0,
	      "decoder creation failed");

	const auto png = MakeHeader(2, 1);
	alignas(4) std::array<uint8_t, 8> output {};
	const DecodeParam decode_param {png.data(), output.data(), static_cast<uint32_t>(png.size()),
	                                static_cast<uint32_t>(output.size()), 0, 255, 0};
	ImageInfo image_info {0xaaaaaaaa, 0xbbbbbbbb, 0xcccc, 0xdddd, 0xeeeeeeee};
	Check(decode(handle, &decode_param, &image_info) == PNG_DEC_ERROR_INVALID_WORK_MEMORY,
	      "image wider than the decoder creation bound was accepted");
	Check(destroy(handle) == 0, "decoder deletion failed");
}

} // namespace

int main() {
	TestCreationWidthConstrainsDecode();
	return 0;
}
