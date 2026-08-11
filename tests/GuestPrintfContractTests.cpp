#include "libs/guestPrintf.h"
#include "libs/vaContext.h"

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace {

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "GuestPrintfContractTests: failed: %s\n", text);
		std::abort();
	}
}

int CallGuestSnprintf(char* destination, size_t destination_size, const char* format,
	                 const char* argument) {
	alignas(16) Libs::VaContext context {};
	context.reg_save_area.gp[0] = reinterpret_cast<uint64_t>(destination);
	context.reg_save_area.gp[1] = destination_size;
	context.reg_save_area.gp[2] = reinterpret_cast<uint64_t>(format);
	context.reg_save_area.gp[3] = reinterpret_cast<uint64_t>(argument);
	context.va_list.gp_offset = offsetof(Libs::VaRegSave, gp);
	context.va_list.fp_offset = offsetof(Libs::VaRegSave, fp);
	context.va_list.overflow_arg_area = nullptr;
	context.va_list.reg_save_area = &context.reg_save_area;
	return Libs::GetGuestSnprintfCtxFunc()(&context);
}

void TestTruncationReturnsRequiredLength() {
	char output[4] = {'x', 'x', 'x', 'x'};
	const auto result = CallGuestSnprintf(output, sizeof(output), "%s", "abcdef");
	Check(result == 6, "snprintf did not return the required untruncated length");
	Check(std::memcmp(output, "abc\0", 4) == 0,
	      "snprintf did not preserve the prefix and terminator after truncation");
}

void TestZeroSizedDestinationStillMeasures() {
	char output = 'x';
	const auto result = CallGuestSnprintf(&output, 0, "%s", "abcdef");
	Check(result == 6, "zero-sized snprintf did not return the required length");
	Check(output == 'x', "zero-sized snprintf modified the destination");
}

void TestCompleteDestination() {
	char output[7] = {};
	const auto result = CallGuestSnprintf(output, sizeof(output), "%s", "abcdef");
	Check(result == 6 && std::strcmp(output, "abcdef") == 0,
	      "snprintf changed the complete-output contract");
}

} // namespace

int main() {
	TestTruncationReturnsRequiredLength();
	TestZeroSizedDestinationStillMeasures();
	TestCompleteDestination();
	std::puts("GuestPrintfContractTests: all cases passed");
	return 0;
}
