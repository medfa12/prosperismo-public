#ifndef EMULATOR_INCLUDE_EMULATOR_LIBS_TEXTTOSPEECH2_H_
#define EMULATOR_INCLUDE_EMULATOR_LIBS_TEXTTOSPEECH2_H_

#include <cstdint>
#include <mutex>

namespace Libs::TextToSpeech2 {

constexpr int ERROR_INVALID_ARGUMENT    = -2120155135; /* 0x81A10001 */
constexpr int ERROR_ALREADY_INITIALIZED = -2120155134; /* 0x81A10002 */
constexpr int ERROR_NOT_INITIALIZED     = -2120155132; /* 0x81A10004 */
constexpr int ERROR_ALREADY_OPENED      = -2120155131; /* 0x81A10005 */
constexpr int ERROR_NOT_OPENED          = -2120155130; /* 0x81A10006 */

class State {
public:
	int Initialize(bool parameter_valid) {
		if (!parameter_valid) {
			return ERROR_INVALID_ARGUMENT;
		}
		std::lock_guard lock(m_mutex);
		if (m_initialized) {
			return ERROR_ALREADY_INITIALIZED;
		}
		m_initialized = true;
		m_opened      = false;
		return 0;
	}

	int Terminate() {
		std::lock_guard lock(m_mutex);
		if (!m_initialized) {
			return ERROR_NOT_INITIALIZED;
		}
		m_opened      = false;
		m_initialized = false;
		return 0;
	}

	int Open(bool parameter_valid) {
		if (!parameter_valid) {
			return ERROR_INVALID_ARGUMENT;
		}
		std::lock_guard lock(m_mutex);
		if (!m_initialized) {
			return ERROR_NOT_INITIALIZED;
		}
		if (m_opened) {
			return ERROR_ALREADY_OPENED;
		}
		m_opened = true;
		return 0;
	}

	int Close() {
		std::lock_guard lock(m_mutex);
		if (!m_initialized) {
			return ERROR_NOT_INITIALIZED;
		}
		if (!m_opened) {
			return ERROR_NOT_OPENED;
		}
		m_opened = false;
		return 0;
	}

	int RequireOpen(bool parameter_valid = true) {
		if (!parameter_valid) {
			return ERROR_INVALID_ARGUMENT;
		}
		std::lock_guard lock(m_mutex);
		return !m_initialized ? ERROR_NOT_INITIALIZED : !m_opened ? ERROR_NOT_OPENED : 0;
	}

	int RequireInitialized() {
		std::lock_guard lock(m_mutex);
		return m_initialized ? 0 : ERROR_NOT_INITIALIZED;
	}

private:
	std::mutex m_mutex;
	bool       m_initialized = false;
	bool       m_opened      = false;
};

} // namespace Libs::TextToSpeech2

#endif /* EMULATOR_INCLUDE_EMULATOR_LIBS_TEXTTOSPEECH2_H_ */
