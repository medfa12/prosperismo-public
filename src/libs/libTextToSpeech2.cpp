#include "common/abi.h"
#include "libs/errno.h"
#include "libs/libs.h"
#include "libs/textToSpeech2.h"
#include "loader/symbolDatabase.h"

namespace Libs {

LIB_VERSION("TextToSpeech2", 1, "TextToSpeech2", 1, 1);

namespace TextToSpeech2 {

static State g_state;

static int KYTY_SYSV_ABI TextToSpeech2Initialize(const void* parameter) {
	PRINT_NAME();
	return g_state.Initialize(parameter != nullptr);
}

static int KYTY_SYSV_ABI TextToSpeech2Terminate() {
	PRINT_NAME();
	return g_state.Terminate();
}

static int KYTY_SYSV_ABI TextToSpeech2Open(const void* parameter) {
	PRINT_NAME();
	return g_state.Open(parameter != nullptr);
}

static int KYTY_SYSV_ABI TextToSpeech2Close() {
	PRINT_NAME();
	return g_state.Close();
}

static int KYTY_SYSV_ABI TextToSpeech2Speak(const void* parameter) {
	PRINT_NAME();
	// Speech synthesis is a host feature boundary. Accepted speech completes
	// immediately, while the SDK lifecycle and argument contract remain exact.
	return g_state.RequireOpen(parameter != nullptr);
}

static int KYTY_SYSV_ABI TextToSpeech2GetSpeechStatus(int32_t* status) {
	PRINT_NAME();
	if (status == nullptr) {
		return ERROR_INVALID_ARGUMENT;
	}
	const int result = g_state.RequireOpen();
	if (result == OK) {
		*status = 0; // SCE_TEXT_TO_SPEECH2_SPEECH_STATUS_NOT_PROCESSING
	}
	return result;
}

static int KYTY_SYSV_ABI TextToSpeech2Cancel() {
	PRINT_NAME();
	return g_state.RequireOpen();
}

static int KYTY_SYSV_ABI TextToSpeech2RegisterTextConversionItem(const void* item) {
	PRINT_NAME();
	return g_state.RequireOpen(item != nullptr);
}

static int KYTY_SYSV_ABI TextToSpeech2GetSystemStatus(int32_t* status) {
	PRINT_NAME();
	if (status == nullptr) {
		return ERROR_INVALID_ARGUMENT;
	}
	const int result = g_state.RequireInitialized();
	if (result == OK) {
		// Windows does not expose the console's system accessibility setting.
		*status = 0; // SCE_TEXT_TO_SPEECH2_SYSTEM_STATUS_DISABLED
	}
	return result;
}

} // namespace TextToSpeech2

LIB_DEFINE(InitTextToSpeech2_1) {
	LIB_FUNC("UOjiprYwVNw", TextToSpeech2::TextToSpeech2Initialize);
	LIB_FUNC("SoWHuVW0gpU", TextToSpeech2::TextToSpeech2Terminate);
	LIB_FUNC("X0HZNbSiqyg", TextToSpeech2::TextToSpeech2Open);
	LIB_FUNC("t4e879M-cSw", TextToSpeech2::TextToSpeech2Close);
	LIB_FUNC("8ntsRd07EQA", TextToSpeech2::TextToSpeech2Speak);
	LIB_FUNC("08JSg9p6bgQ", TextToSpeech2::TextToSpeech2GetSpeechStatus);
	LIB_FUNC("2jiIxUmcsGo", TextToSpeech2::TextToSpeech2Cancel);
	LIB_FUNC("LazJT1ZrQys", TextToSpeech2::TextToSpeech2RegisterTextConversionItem);
	LIB_FUNC("+352WTlGCQI", TextToSpeech2::TextToSpeech2GetSystemStatus);
}

} // namespace Libs
