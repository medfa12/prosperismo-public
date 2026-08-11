#include "graphics/host_gpu/renderer/cache/gpuResourceManager.h"

#include "common/assert.h"

#include <atomic>
#include <cstdio>
#include <cstdlib>
#include "graphics/guest_gpu/command_processor/commandProcessor.h"
#include "graphics/guest_gpu/graphicsRun.h"
#include "graphics/host_gpu/renderer/commandScheduler.h"
namespace Libs::Graphics {

GpuResourceManager::GpuResourceManager(GraphicContext& graphics, CommandScheduler& scheduler)
    : m_scheduler(scheduler),
      m_buffer_cache(graphics, scheduler, m_page_manager, m_texture_cache, m_resource_mutex),
      m_texture_cache(graphics, scheduler, m_page_manager, m_buffer_cache, m_resource_mutex) {}

GpuResourceManager::~GpuResourceManager() = default;

bool GpuResourceManager::HandleFault(PageFaultAccess access, uint64_t fault_vaddr) noexcept {
	constexpr uint64_t fault_size = 8;

	// A fault that never clears its page's watcher re-faults on the same instruction forever,
	// which looks identical to "slow" from the outside. Reporting the repeat count for the
	// current address separates a livelock from ordinary invalidation churn.
	static const bool fault_trace = [] {
		const char* v = std::getenv("KYTY_FAULT_TRACE");
		return v != nullptr && v[0] != '\0' && v[0] != '0';
	}();
	if (fault_trace) {
		static std::atomic<uint64_t> fault_count {0};
		static std::atomic<uint64_t> last_vaddr {0};
		static std::atomic<uint64_t> same_vaddr {0};
		const auto                   n    = fault_count.fetch_add(1) + 1;
		const auto                   prev = last_vaddr.exchange(fault_vaddr);
		const auto repeats = (prev == fault_vaddr ? same_vaddr.fetch_add(1) + 1 : same_vaddr.exchange(0));
		if (n % 10000 == 0) {
			::printf("[fault] n=%llu addr=0x%016llx access=%u same_addr_streak=%llu\n",
			         static_cast<unsigned long long>(n),
			         static_cast<unsigned long long>(fault_vaddr),
			         static_cast<uint32_t>(access), static_cast<unsigned long long>(repeats));
			::fflush(stdout);
		}
	}

	if (!IsMapped(fault_vaddr, fault_size)) {
		return false;
	}
	if (CommandScheduler::InDeferredOperation()) {
		const auto image_overlap = m_texture_cache.QueryRegion(fault_vaddr, fault_size);
		if (access == PageFaultAccess::Write && !image_overlap.image_pages &&
		    m_buffer_cache.PrepareCleanHostWrite(fault_vaddr, fault_size)) {
			return true;
		}
		const bool cpu_modified   = m_buffer_cache.IsRegionCpuModified(fault_vaddr, fault_size);
		const bool gpu_modified   = m_buffer_cache.IsRegionGpuModified(fault_vaddr, fault_size);
		const bool buffer_overlap = m_buffer_cache.HasPageOverlap(fault_vaddr, fault_size);
		EXIT("unsupported guest-memory fault from an asynchronous GPU completion, "
		     "addr=0x%016" PRIx64 " access=%u cpu_modified=%u gpu_modified=%u "
		     "buffer_overlap=%u image_pages=%u image_bytes=%u gpu_image_bytes=%u\n",
		     fault_vaddr, static_cast<uint32_t>(access), static_cast<uint32_t>(cpu_modified),
		     static_cast<uint32_t>(gpu_modified), static_cast<uint32_t>(buffer_overlap),
		     static_cast<uint32_t>(image_overlap.image_pages),
		     static_cast<uint32_t>(image_overlap.image_bytes),
		     static_cast<uint32_t>(image_overlap.gpu_image_bytes));
	}
	bool       handled = false;
	const auto resolve = [this, access, fault_vaddr, &handled](CommandProcessor& cp) {
		cp.BeginReadbackTransaction();
		{
			ResourceMutex::FaultScope fault(m_resource_mutex);
			// Execute faults deliberately stay on the readback path. Routing them through
			// invalidation instead was measured to stall the guest at frame ~31, far worse than
			// the single livelocked thread it was meant to rescue.
			if (access == PageFaultAccess::Write) {
				m_buffer_cache.InvalidateMemory(fault_vaddr, fault_size);
				m_texture_cache.InvalidateMemory(fault_vaddr, fault_size);
			} else {
				m_buffer_cache.ReadMemory(fault_vaddr, fault_size);
			}
			handled = true;
		}
		cp.EndReadbackTransaction();
	};
	if (auto* cp = Gpu::CurrentCommandProcessor(); cp != nullptr) {
		resolve(*cp);
		return handled;
	}
	if (m_resource_mutex.IsOwnedByCurrentThread()) {
		EXIT("unsupported page fault from a pre-owned resource transaction, addr=0x%016" PRIx64
		     " access=%u\n",
		     fault_vaddr, static_cast<uint32_t>(access));
	}
	EXIT_IF(m_gpu == nullptr);
	m_gpu->SendCommandSyncWithProcessor(resolve);
	return handled;
}

bool GpuResourceManager::InvalidateMemory(uint64_t vaddr, uint64_t size) {
	if (!IsMapped(vaddr, size)) {
		return false;
	}
	if (CommandScheduler::InDeferredOperation()) {
		EXIT("unsupported memory invalidation from an asynchronous GPU completion, "
		     "addr=0x%016" PRIx64 " size=0x%016" PRIx64 "\n",
		     vaddr, size);
	}
	const auto resolve = [this, vaddr, size](CommandProcessor& cp) {
		cp.BeginReadbackTransaction();
		{
			ResourceMutex::FaultScope fault(m_resource_mutex);
			m_buffer_cache.InvalidateMemory(vaddr, size);
			m_texture_cache.InvalidateMemory(vaddr, size);
		}
		cp.EndReadbackTransaction();
	};
	if (auto* cp = Gpu::CurrentCommandProcessor(); cp != nullptr) {
		resolve(*cp);
		return true;
	}
	if (m_resource_mutex.IsOwnedByCurrentThread()) {
		EXIT("unsupported memory invalidation from a pre-owned resource transaction, "
		     "addr=0x%016" PRIx64 " size=0x%016" PRIx64 "\n",
		     vaddr, size);
	}
	EXIT_IF(m_gpu == nullptr);
	m_gpu->SendCommandSyncWithProcessor(resolve);
	return true;
}

bool GpuResourceManager::IsMapped(uint64_t vaddr, uint64_t size) const noexcept {
	if (vaddr == 0 || size == 0 || vaddr >= TRACKER_ADDRESS_SIZE ||
	    size > TRACKER_ADDRESS_SIZE - vaddr) {
		return false;
	}
	std::shared_lock lock(m_mapped_ranges_mutex);
	return m_mapped_ranges.Contains(vaddr, size);
}

void GpuResourceManager::MapMemory(uint64_t vaddr, uint64_t size) {
	{
		std::lock_guard lock(m_mapped_ranges_mutex);
		m_mapped_ranges.Add(vaddr, size);
	}
	m_page_manager.OnGpuMap(vaddr, size);
}

void GpuResourceManager::UnmapMemory(uint64_t vaddr, uint64_t size) {
	if (CommandScheduler::InDeferredOperation()) {
		EXIT("unsupported memory unmap from an asynchronous GPU completion, "
		     "addr=0x%016" PRIx64 " size=0x%016" PRIx64 "\n",
		     vaddr, size);
	}
	if (m_resource_mutex.IsOwnedByCurrentThread()) {
		EXIT("unsupported memory unmap from a pre-owned resource transaction, "
		     "addr=0x%016" PRIx64 " size=0x%016" PRIx64 "\n",
		     vaddr, size);
	}
	const auto unmap = [this, vaddr, size] {
		if (m_scheduler.Active()) {
			const auto tick = m_scheduler.CurrentTick();
			m_scheduler.FinishCurrent();
			m_scheduler.WaitPriorityOperations(tick);
		}
		m_buffer_cache.UnmapMemory(vaddr, size);
		m_texture_cache.UnmapMemory(vaddr, size);
		m_page_manager.OnGpuUnmap(vaddr, size);
		std::lock_guard lock(m_mapped_ranges_mutex);
		m_mapped_ranges.Subtract(vaddr, size);
	};
	if (m_gpu == nullptr) {
		unmap();
		return;
	}
	const bool gpu_mapped = [&] {
		std::shared_lock lock(m_mapped_ranges_mutex);
		return m_mapped_ranges.Intersects(vaddr, size);
	}();
	// Reserved or allocation-only spans can reach the generic unmap path without
	// ever being exposed to the GPU. Sending those no-op ranges through the GPU
	// command queue can deadlock when a WaitRegMem guest writer is holding the
	// memory-operation dependency needed by SendCommandSync.
	if (!gpu_mapped) {
		const bool cached = m_buffer_cache.HasPageOverlap(vaddr, size) ||
		                    m_texture_cache.QueryRegion(vaddr, size).image_pages;
		if (cached) {
			EXIT("gpu-unmapped range still holds cached resources: addr=0x%016" PRIx64
			     " size=0x%016" PRIx64 "\n",
				     vaddr, size);
		}
		// This range never entered either GPU cache, so there is no command-buffer
		// lifetime to retire. Calling the shared unmap closure here would still run
		// FinishCurrent() from the guest thread and race the GPU thread's recorder.
		m_page_manager.OnGpuUnmap(vaddr, size);
		std::lock_guard lock(m_mapped_ranges_mutex);
		m_mapped_ranges.Subtract(vaddr, size);
		return;
	}
	m_gpu->SendCommandSync(unmap);
}

void GpuResourceManager::RunGarbageCollector() {
	m_texture_cache.ProcessDownloadImages();
	m_texture_cache.RunGarbageCollector();
	m_buffer_cache.RunGarbageCollector();
}

} // namespace Libs::Graphics
