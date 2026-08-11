#include "kernel/eventFlag.h"

#include "common/assert.h"
#include "common/common.h"
#include "common/logging/log.h"
#include "common/stringUtils.h"
#include "common/threads.h"
#include "common/timer.h"
#include "libs/errno.h"
#include "libs/libs.h"

#include <mutex>
#include <optional>
#include <unordered_map>
#include <unordered_set>

namespace Libs::LibKernel::EventFlag {

LIB_NAME("libkernel", "libkernel");

class KernelEventFlagPrivate {
public:
	enum class Result { Ok, AlreadyWaiting, TimedOut, Canceled, Deleted };

	enum class ClearMode { None, All, Bits };

	enum class WaitMode { And, Or };

	KernelEventFlagPrivate(const std::string& name, bool single, bool /*fifo*/, uint64_t bits)
	    : m_name(name), m_single_thread(single), m_bits(bits) {}
	virtual ~KernelEventFlagPrivate();

	KYTY_CLASS_NO_COPY(KernelEventFlagPrivate);

	void   Set(uint64_t bits);
	void   Clear(uint64_t bits);
	void   Cancel(uint64_t bits, int* num_waiting_threads);
	Result Wait(uint64_t bits, WaitMode wait_mode, ClearMode clear_mode, uint64_t* result,
	            uint32_t* ptr_micros);

	Result Poll(uint64_t bits, WaitMode wait_mode, ClearMode clear_mode, uint64_t* result) {
		uint32_t micros = 0;
		return Wait(bits, wait_mode, clear_mode, result, &micros);
	}

	const std::string& Name() const { return m_name; }
	int                AddReference() { return ++m_open_references; }
	int                ReleaseReference() { return --m_open_references; }

private:
	enum class Status { Set, Canceled, Deleted };

	Common::Mutex   m_mutex;
	Common::CondVar m_cond_var;
	Status          m_status          = Status::Set;
	int             m_waiting_threads = 0;
	std::string     m_name;
	bool            m_reported_long_wait = false;
	bool            m_single_thread = false;
	uint64_t        m_bits          = 0;
	int             m_open_references = 1;
};

namespace {

std::mutex                                             g_event_flag_registry_mutex;
std::unordered_map<std::string, KernelEventFlag>       g_named_event_flags;
std::unordered_set<KernelEventFlag>                    g_event_flags;

void RemovePublishedName(KernelEventFlag event_flag) {
	const auto found = g_named_event_flags.find(event_flag->Name());
	if (found != g_named_event_flags.end() && found->second == event_flag) {
		g_named_event_flags.erase(found);
	}
}

struct EventFlagWaitMode {
	KernelEventFlagPrivate::WaitMode  wait  = KernelEventFlagPrivate::WaitMode::And;
	KernelEventFlagPrivate::ClearMode clear = KernelEventFlagPrivate::ClearMode::None;
};

std::optional<EventFlagWaitMode> DecodeEventFlagWaitMode(uint32_t wait_mode) {
	if ((wait_mode & ~0x33u) != 0) {
		return std::nullopt;
	}

	EventFlagWaitMode mode;

	switch (wait_mode & 0xfu) {
		case 0x01: mode.wait = KernelEventFlagPrivate::WaitMode::And; break;
		case 0x02: mode.wait = KernelEventFlagPrivate::WaitMode::Or; break;
		default: return std::nullopt;
	}

	switch (wait_mode & 0xf0u) {
		case 0x00: mode.clear = KernelEventFlagPrivate::ClearMode::None; break;
		case 0x10: mode.clear = KernelEventFlagPrivate::ClearMode::All; break;
		case 0x20: mode.clear = KernelEventFlagPrivate::ClearMode::Bits; break;
		default: return std::nullopt;
	}

	return mode;
}

} // namespace

KernelEventFlagPrivate::~KernelEventFlagPrivate() {
	Common::LockGuard lock(m_mutex);

	while (m_status != Status::Set) {
		m_mutex.Unlock();
		Common::Thread::SleepMicro(10);
		m_mutex.Lock();
	}

	m_status = Status::Deleted;

	m_cond_var.SignalAll();

	while (m_waiting_threads > 0) {
		m_mutex.Unlock();
		Common::Thread::SleepMicro(10);
		m_mutex.Lock();
	}
}

KernelEventFlagPrivate::Result KernelEventFlagPrivate::Wait(uint64_t bits, WaitMode wait_mode,
                                                            ClearMode clear_mode, uint64_t* result,
                                                            uint32_t* ptr_micros) {
	Common::LockGuard lock(m_mutex);

	uint32_t micros     = 0;
	bool     infinitely = true;
	if (ptr_micros != nullptr) {
		micros     = *ptr_micros;
		infinitely = false;
	}

	uint32_t      elapsed = 0;
	Common::Timer t;
	t.Start();

	auto update_timeout = [&]() {
		if (ptr_micros != nullptr) {
			*ptr_micros = (elapsed >= micros ? 0 : micros - elapsed);
		}
	};

	if (m_single_thread && m_waiting_threads > 0) {
		return Result::AlreadyWaiting;
	}

	while (!((wait_mode == WaitMode::And && (m_bits & bits) == bits) ||
	         (wait_mode == WaitMode::Or && (m_bits & bits) != 0))) {
		if ((elapsed >= micros && !infinitely)) {
			if (result != nullptr) {
				*result = m_bits;
			}
			update_timeout();
			return Result::TimedOut;
		}

		m_waiting_threads++;

		if (infinitely) {
			// Sliced rather than a bare Wait so an indefinite wait can report itself. The predicate
			// is re-tested by the enclosing loop either way, so this is semantically identical - but
			// a flag whose bits are never set is otherwise completely silent, and that is exactly
			// the shape of a stalled boot.
			constexpr uint32_t WAIT_SLICE_MICROS  = 1000000;
			constexpr double   REPORT_AFTER_SECS  = 5.0;
			m_cond_var.WaitFor(&m_mutex, WAIT_SLICE_MICROS);
			if (!m_reported_long_wait && t.GetTimeS() > REPORT_AFTER_SECS) {
				m_reported_long_wait = true;
				printf("eventflag: \"%s\" waited %.1fs unsatisfied, want=0x%016llx mode=%s "
				       "have=0x%016llx\n",
				       m_name.c_str(), t.GetTimeS(), static_cast<unsigned long long>(bits),
				       wait_mode == WaitMode::And ? "AND" : "OR",
				       static_cast<unsigned long long>(m_bits));
				fflush(stdout);
			}
		} else {
			m_cond_var.WaitFor(&m_mutex, micros - elapsed);
		}

		m_waiting_threads--;

		elapsed = static_cast<uint32_t>(t.GetTimeS() * 1000000.0);

		switch (m_status) {
			case Status::Canceled:
				if (result != nullptr) {
					*result = m_bits;
				}
				update_timeout();
				return Result::Canceled;
			case Status::Deleted:
				if (result != nullptr) {
					*result = m_bits;
				}
				update_timeout();
				return Result::Deleted;
			case Status::Set: break;
		}
	}

	if (result != nullptr) {
		*result = m_bits;
	}
	update_timeout();

	switch (clear_mode) {
		case ClearMode::All: m_bits = 0; break;
		case ClearMode::Bits: m_bits &= ~bits; break;
		case ClearMode::None: break;
	}

	return Result::Ok;
}

void KernelEventFlagPrivate::Set(uint64_t bits) {
	Common::LockGuard lock(m_mutex);

	EXIT_NOT_IMPLEMENTED(m_status == Status::Deleted);

	while (m_status != Status::Set) {
		m_mutex.Unlock();
		Common::Thread::SleepMicro(10);
		m_mutex.Lock();
	}

	m_bits |= bits;

	m_cond_var.SignalAll();
}

void KernelEventFlagPrivate::Clear(uint64_t bits) {
	Common::LockGuard lock(m_mutex);

	EXIT_NOT_IMPLEMENTED(m_status == Status::Deleted);

	while (m_status != Status::Set) {
		m_mutex.Unlock();
		Common::Thread::SleepMicro(10);
		m_mutex.Lock();
	}

	m_bits &= bits;
}

void KernelEventFlagPrivate::Cancel(uint64_t bits, int* num_waiting_threads) {
	Common::LockGuard lock(m_mutex);

	EXIT_NOT_IMPLEMENTED(m_status == Status::Deleted);

	while (m_status != Status::Set) {
		m_mutex.Unlock();
		Common::Thread::SleepMicro(10);
		m_mutex.Lock();
	}

	if (num_waiting_threads != nullptr) {
		*num_waiting_threads = m_waiting_threads;
	}

	m_status = Status::Canceled;
	m_bits   = bits;

	m_cond_var.SignalAll();

	while (m_waiting_threads > 0) {
		m_mutex.Unlock();
		Common::Thread::SleepMicro(10);
		m_mutex.Lock();
	}

	m_status = Status::Set;
}

int KYTY_SYSV_ABI KernelCreateEventFlag(KernelEventFlag* ef, const char* name, uint32_t attr,
                                        uint64_t init_pattern, const void* param) {
	PRINT_NAME();

	if (ef == nullptr || name == nullptr) {
		return KERNEL_ERROR_EINVAL;
	}

	// Firmware modules use bit 0x100 on 89 of 169 statically resolved 4.03
	// sceKernelCreateEventFlag call sites. SDK public headers do not name the bit,
	// but rejecting it breaks Sony's own callers. The remaining accepted bits are
	// the documented FIFO/priority and single/multi nibbles.
	if (param != nullptr || (attr & ~0x133u) != 0) {
		return KERNEL_ERROR_EINVAL;
	}

	bool single = true;
	bool fifo   = true;

	switch (attr & 0x0fu) {
		case 0x00:
		case 0x01: fifo = true; break;
		case 0x02: fifo = false; break;
		default: return KERNEL_ERROR_EINVAL;
	}

	switch (attr & 0xf0u) {
		case 0x00:
		case 0x10: single = true; break;
		case 0x20: single = false; break;
		default: return KERNEL_ERROR_EINVAL;
	}

	auto* event_flag = new KernelEventFlagPrivate(std::string(name), single, fifo, init_pattern);
	{
		std::lock_guard lock(g_event_flag_registry_mutex);
		g_event_flags.insert(event_flag);
		// The kernel permits a second object with the same label. Name lookup keeps
		// resolving the first live object until it is closed or deleted.
		g_named_event_flags.try_emplace(name, event_flag);
	}
	*ef = event_flag;

	LOGF("\tEventFlag create: %s\n", name);

	return OK;
}

int KYTY_SYSV_ABI KernelOpenEventFlag(KernelEventFlag* ef, const char* name) {
	PRINT_NAME();

	if (ef == nullptr || name == nullptr) {
		return KERNEL_ERROR_EINVAL;
	}

	std::lock_guard lock(g_event_flag_registry_mutex);
	const auto      found = g_named_event_flags.find(name);
	if (found == g_named_event_flags.end() || !g_event_flags.contains(found->second)) {
		return KERNEL_ERROR_ESRCH;
	}

	found->second->AddReference();
	*ef = found->second;
	return OK;
}

int KYTY_SYSV_ABI KernelCloseEventFlag(KernelEventFlag ef) {
	PRINT_NAME();

	if (ef == nullptr) {
		return KERNEL_ERROR_ESRCH;
	}

	bool destroy = false;
	{
		std::lock_guard lock(g_event_flag_registry_mutex);
		if (!g_event_flags.contains(ef)) {
			return KERNEL_ERROR_ESRCH;
		}
		if (ef->ReleaseReference() == 0) {
			RemovePublishedName(ef);
			g_event_flags.erase(ef);
			destroy = true;
		}
	}

	if (destroy) {
		delete ef;
	}
	return OK;
}

int KYTY_SYSV_ABI KernelDeleteEventFlag(KernelEventFlag ef) {
	PRINT_NAME();

	if (ef == nullptr) {
		return KERNEL_ERROR_ESRCH;
	}

	{
		std::lock_guard lock(g_event_flag_registry_mutex);
		if (!g_event_flags.contains(ef)) {
			return KERNEL_ERROR_ESRCH;
		}
		RemovePublishedName(ef);
		g_event_flags.erase(ef);
	}

	delete ef;

	return OK;
}

int KYTY_SYSV_ABI KernelWaitEventFlag(KernelEventFlag ef, uint64_t bit_pattern, uint32_t wait_mode,
                                      uint64_t* result_pat, KernelUseconds* timeout) {
	PRINT_NAME();

	if (ef == nullptr) {
		return KERNEL_ERROR_ESRCH;
	}

	if (bit_pattern == 0) {
		return KERNEL_ERROR_EINVAL;
	}

	const auto mode = DecodeEventFlagWaitMode(wait_mode);
	if (!mode.has_value()) {
		return KERNEL_ERROR_EINVAL;
	}

	auto result = ef->Wait(bit_pattern, mode->wait, mode->clear, result_pat, timeout);

	int ret = OK;

	switch (result) {
		case KernelEventFlagPrivate::Result::Ok: ret = OK; break;
		case KernelEventFlagPrivate::Result::AlreadyWaiting: ret = KERNEL_ERROR_EPERM; break;
		case KernelEventFlagPrivate::Result::TimedOut: ret = KERNEL_ERROR_ETIMEDOUT; break;
		case KernelEventFlagPrivate::Result::Canceled: ret = KERNEL_ERROR_ECANCELED; break;
		case KernelEventFlagPrivate::Result::Deleted: ret = KERNEL_ERROR_EACCES; break;
	}

	return ret;
}

int KYTY_SYSV_ABI KernelPollEventFlag(KernelEventFlag ef, uint64_t bit_pattern, uint32_t wait_mode,
                                      uint64_t* result_pat) {
	PRINT_NAME();

	if (ef == nullptr) {
		return KERNEL_ERROR_ESRCH;
	}

	if (bit_pattern == 0) {
		return KERNEL_ERROR_EINVAL;
	}

	const auto mode = DecodeEventFlagWaitMode(wait_mode);
	if (!mode.has_value()) {
		return KERNEL_ERROR_EINVAL;
	}

	auto result = ef->Poll(bit_pattern, mode->wait, mode->clear, result_pat);

	int ret = OK;

	switch (result) {
		case KernelEventFlagPrivate::Result::Ok: ret = OK; break;
		case KernelEventFlagPrivate::Result::AlreadyWaiting: ret = KERNEL_ERROR_EPERM; break;
		case KernelEventFlagPrivate::Result::TimedOut:
		case KernelEventFlagPrivate::Result::Canceled:
		case KernelEventFlagPrivate::Result::Deleted: ret = KERNEL_ERROR_EBUSY; break;
	}

	return ret;
}

int KYTY_SYSV_ABI KernelSetEventFlag(KernelEventFlag ef, uint64_t bit_pattern) {
	PRINT_NAME();

	if (ef == nullptr) {
		return KERNEL_ERROR_ESRCH;
	}

	// KYTY_EVENTFLAG_TRACE=1 names every flag actually signalled. Pairing this with the
	// unsatisfied-wait report above distinguishes "nobody ever signals this flag" from
	// "it is signalled but the waiter wants bits that are never set together".
	static const bool trace = [] {
		const char* v = std::getenv("KYTY_EVENTFLAG_TRACE");
		return v != nullptr && v[0] != '\0' && v[0] != '0';
	}();
	if (trace) {
		printf("eventflag set: \"%s\" bits=0x%016llx\n", ef->Name().c_str(),
		       static_cast<unsigned long long>(bit_pattern));
		fflush(stdout);
	}

	ef->Set(bit_pattern);

	return OK;
}

int KYTY_SYSV_ABI KernelClearEventFlag(KernelEventFlag ef, uint64_t bit_pattern) {
	PRINT_NAME();

	if (ef == nullptr) {
		return KERNEL_ERROR_ESRCH;
	}

	ef->Clear(bit_pattern);

	return OK;
}

int KYTY_SYSV_ABI KernelCancelEventFlag(KernelEventFlag ef, uint64_t set_pattern,
                                        int* num_wait_threads) {
	PRINT_NAME();

	if (ef == nullptr) {
		return KERNEL_ERROR_ESRCH;
	}

	ef->Cancel(set_pattern, num_wait_threads);

	return OK;
}

} // namespace Libs::LibKernel::EventFlag
