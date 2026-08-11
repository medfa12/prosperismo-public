#include "graphics/host_gpu/renderer/cache/textureCache.h"

#include "common/assert.h"
#include "common/emulatorConfig.h"
#include "common/logging/log.h"
#include "common/profiler.h"
#include "graphics/guest_gpu/gpu_format.h"
#include "graphics/guest_gpu/tile.h"
#include "graphics/host_gpu/graphicContext.h"
#include "graphics/host_gpu/renderer/cache/bufferCache.h"
#include "graphics/host_gpu/renderer/cache/resourceMutex.h"
#include "graphics/host_gpu/renderer/commandScheduler.h"
#include "graphics/host_gpu/renderer/image/imageView.h"
#include "graphics/host_gpu/renderer/image/textureCommon.h"
#include "graphics/host_gpu/renderer/image/tiler.h"
#include "graphics/host_gpu/renderer/render.h"
#include "kernel/memory.h"

#include <algorithm>
#include <array>
#include <bit>
#include <cinttypes>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <mutex>
#include <set>
#include <string>
#include <tuple>
#include <vector>
#include <vulkan/vulkan_format_traits.hpp>

namespace Libs::Graphics {

namespace {

constexpr uint64_t NumFramesBeforeRemoval = 32;

thread_local const TextureCache* g_locked_cache = nullptr;

class CacheLock final {
public:
	CacheLock(const TextureCache& owner, TrackingSpinLock& lock): m_lock(lock) {
		if (g_locked_cache != nullptr) {
			EXIT("TextureCache: recursive cache lock\n");
		}
		g_locked_cache = &owner;
		m_lock.lock();
	}
	~CacheLock() {
		m_lock.unlock();
		g_locked_cache = nullptr;
	}

private:
	TrackingSpinLock& m_lock;
};

} // namespace

TextureCache::TextureCache(GraphicContext& graphics, CommandScheduler& scheduler,
                           PageManager& page_manager, BufferCache& buffer_cache,
                           ResourceMutex& resource_mutex)
    : m_graphics(graphics), m_scheduler(scheduler), m_page_manager(page_manager),
      m_blit_helper(graphics, scheduler),
      m_tiler(std::make_unique<TileManager>(graphics, scheduler,
                                            buffer_cache.GetUtilityBuffer(MemoryUsage::Stream))),
      m_buffer_cache(buffer_cache), m_resource_mutex(resource_mutex),
      m_readback_linear_images(Config::ReadbackLinearImagesEnabled()) {
	if (m_graphics.CanReportMemoryUsage()) {
		constexpr int64_t GiB = 1024ll * 1024 * 1024;
		const auto        budget =
		    static_cast<int64_t>(std::min<uint64_t>(m_graphics.GetTotalMemoryBudget(), INT64_MAX));
		const auto threshold = std::min<int64_t>(budget, 8 * GiB);
		m_pressure_gc_memory = static_cast<uint64_t>(
		    std::max<int64_t>(std::min(budget - 6 * threshold / 10, budget - GiB), GiB + GiB / 2));
		m_critical_gc_memory = static_cast<uint64_t>(
		    std::max<int64_t>(std::min(budget - 2 * threshold / 10, budget - GiB / 2), 3 * GiB));
		m_trigger_gc_memory = static_cast<uint64_t>(std::max<int64_t>((budget - threshold) / 2, 0));
	}
}

TextureCache::~TextureCache() {
	for (uint32_t index = 0; index < m_slots.size(); index++) {
		if (m_slots[index].image != nullptr && m_slots[index].image->registered) {
			UnregisterImage({index, m_slots[index].generation});
		}
		m_slots[index].image.reset();
	}
}

bool TextureCache::SameBacking(const ImageInfo& cached, const ImageInfo& requested,
                               bool exact_format) {
	if (cached.data.address != requested.data.address) {
		return false;
	}
	if (cached.data.size != requested.data.size) {
		return false;
	}
	if (cached.extent != requested.extent) {
		return false;
	}
	if (cached.samples != requested.samples) {
		return false;
	}
	if (cached.bytes_per_block != requested.bytes_per_block) {
		return false;
	}
	if (cached.tile_mode != requested.tile_mode) {
		return false;
	}
	if (!ImageMetadataIdentityMatches(cached.metadata, requested.metadata)) {
		return false;
	}
	if (!ImageViewOps::FormatsCompatible(cached.pixel_format, requested.pixel_format)) {
		return false;
	}
	if (cached.type != requested.type && requested.extent != vk::Extent3D {1, 1, 1}) {
		return false;
	}
	if (exact_format && cached.pixel_format != requested.pixel_format) {
		return false;
	}
	return true;
}

TextureCache::BindingType TextureCache::UploadBinding(const Image& image) {
	if (image.info.IsDepth()) {
		return BindingType::DepthTarget;
	}
	if (image.usage.render_target) {
		return BindingType::RenderTarget;
	}
	if (image.usage.video_out) {
		return BindingType::VideoOut;
	}
	return image.usage.storage ? BindingType::Storage : BindingType::Texture;
}

bool TextureCache::SafeToDownload(const Image& image) {
	if (!image.SafeToDownload()) {
		return false;
	}
	const auto range = image.info.data;
	return !m_buffer_cache.HasGpuDirtyBytes(range.address, range.size);
}

Image& TextureCache::ResolveImage(ImageId id) {
	if (!id || id.index >= m_slots.size()) {
		EXIT("TextureCache: invalid image id\n");
	}
	auto& slot = m_slots[id.index];
	if (slot.generation != id.generation || slot.image == nullptr) {
		EXIT("TextureCache: stale image id\n");
	}
	return *slot.image;
}

const Image& TextureCache::ResolveImage(ImageId id) const {
	if (!id || id.index >= m_slots.size()) {
		EXIT("TextureCache: invalid image id\n");
	}
	const auto& slot = m_slots[id.index];
	if (slot.generation != id.generation || slot.image == nullptr) {
		EXIT("TextureCache: stale image id\n");
	}
	return *slot.image;
}

std::shared_ptr<Image> TextureCache::ResolveOwner(ImageId id) const {
	if (!id || id.index >= m_slots.size()) {
		return {};
	}
	const auto& slot = m_slots[id.index];
	return slot.generation == id.generation ? slot.image : nullptr;
}

ImageId TextureCache::InsertImage(const ImageInfo& info) {
	uint32_t index = 0;
	if (m_free_slots.empty()) {
		index = static_cast<uint32_t>(m_slots.size());
		m_slots.emplace_back();
	} else {
		index = m_free_slots.back();
		m_free_slots.pop_back();
	}
	auto& slot = m_slots[index];
	if (slot.image != nullptr) {
		EXIT("TextureCache: occupied free image slot\n");
	}
	slot.image = std::make_shared<Image>(m_graphics, m_scheduler, info);
	const ImageId id {index, slot.generation};
	if (!info.data.Empty()) {
		RegisterImage(id);
	}
	return id;
}

void TextureCache::RegisterImage(ImageId id) {
	auto& image = ResolveImage(id);
	if (image.registered || image.info.data.Empty()) {
		EXIT("TextureCache: invalid image registration\n");
	}
	std::vector<ImageOwnerIndex::ByteRange> ranges;
	ranges.push_back({image.info.data.address, image.info.data.size});
	if (!m_image_owner_index.Register(id, ranges)) {
		EXIT("TextureCache: duplicate or invalid image registration\n");
	}
	image.registered = true;
	image.lru_id     = m_lru_cache.Insert(id, m_gc_tick);
	m_total_used_memory += image.AccountedSize();
}

void TextureCache::UnregisterImage(ImageId id) {
	auto& image = ResolveImage(id);
	if (!image.registered) {
		return;
	}
	UntrackImage(id);
	std::vector<ImageOwnerIndex::ByteRange> releases;
	if (!m_image_owner_index.Unregister(id, releases)) {
		EXIT("TextureCache: image missing from owner index\n");
	}
	m_lru_cache.Free(image.lru_id);
	const auto accounted = image.AccountedSize();
	if (accounted > m_total_used_memory) {
		EXIT("TextureCache: image accounting underflow\n");
	}
	m_total_used_memory -= accounted;
	image.registered = false;
}

void TextureCache::DeleteImage(ImageId id) {
	auto owner = ResolveOwner(id);
	if (owner == nullptr || !owner->registered) {
		return;
	}
	if (!owner->depth_id) {
		std::vector<ImageId> associations;
		for (uint32_t index = 0; index < m_slots.size(); index++) {
			const auto& slot = m_slots[index];
			if (slot.image != nullptr && slot.image->depth_id == id) {
				associations.push_back({index, slot.generation});
			}
		}
		for (const auto association: associations) {
			ClearGpuModified(association);
			DeleteImage(association);
		}
	}
	if (owner->IsGpuModified()) {
		EXIT("TextureCache: deleting a GPU-modified image without resolving its contents\n");
	}
	m_download_images.erase(id);
	if (owner->info.metadata.kind == ImageMetadataKind::Htile) {
		m_surface_metas.erase(owner->info.metadata.range.address);
	}
	UnregisterImage(id);
	const auto erase_slot = [this, id, retained = owner] {
		auto& slot = m_slots[id.index];
		if (slot.generation != id.generation || slot.image != retained) {
			EXIT("TextureCache: retired image slot changed before deferred erasure\n");
		}
		slot.image.reset();
		if (++slot.generation == 0) {
			slot.generation = 1;
		}
		m_free_slots.push_back(id.index);
	};
	if (m_scheduler.Active()) {
		m_scheduler.DeferOperation(erase_slot);
	} else {
		erase_slot();
	}
}

void TextureCache::DeleteImages(std::span<const ImageId> ids,
                                std::optional<ImageId>   native_source) {
	std::set<std::pair<uint32_t, uint32_t>> unique;
	for (const auto id: ids) {
		if (!id || !unique.emplace(id.index, id.generation).second) {
			continue;
		}
		auto owner = ResolveOwner(id);
		if (owner == nullptr) {
			continue;
		}
		if (native_source == id) {
			ClearGpuModified(id);
		} else if (owner->IsGpuModified()) {
			DownloadImage(id);
			ClearGpuModified(id);
		}
		DeleteImage(id);
	}
}

void TextureCache::RetainImage(CommandBuffer& command, ImageId id) {
	auto owner = ResolveOwner(id);
	if (owner == nullptr) {
		EXIT("TextureCache: retaining a stale image\n");
	}
	if (owner->depth_id) {
		auto depth = ResolveOwner(owner->depth_id);
		if (depth == nullptr) {
			EXIT("TextureCache: stencil association points to a stale depth image\n");
		}
		command.RetainResourceUntilFence(std::move(depth));
	}
	command.RetainResourceUntilFence(std::move(owner));
}

void TextureCache::TouchImage(Image& image) {
	if (image.registered) {
		m_lru_cache.Touch(image.lru_id, m_gc_tick);
	}
}

void TextureCache::TrackImage(ImageId id) {
	auto& image = ResolveImage(id);
	if (!image.registered) {
		return;
	}
	const auto image_begin = image.info.data.address;
	const auto image_end   = image.info.data.End();
	if (image_begin == image.track_addr && image_end == image.track_addr_end) {
		return;
	}
	if (!image.IsTracked()) {
		image.track_addr     = image_begin;
		image.track_addr_end = image_end;
		m_page_manager.UpdatePageWatchers<true>(image_begin, image.info.data.size);
		return;
	}
	if (image_begin < image.track_addr) {
		TrackImageHead(id);
	}
	if (image.track_addr_end < image_end) {
		TrackImageTail(id);
	}
}

void TextureCache::TrackImageHead(ImageId id) {
	auto& image = ResolveImage(id);
	if (!image.registered) {
		return;
	}
	const auto image_begin = image.info.data.address;
	if (image_begin == image.track_addr) {
		return;
	}
	if (!image.IsTracked() || image_begin > image.track_addr) {
		EXIT("TextureCache: invalid image head tracking range\n");
	}
	const auto size  = image.track_addr - image_begin;
	image.track_addr = image_begin;
	m_page_manager.UpdatePageWatchers<true>(image_begin, size);
}

void TextureCache::TrackImageTail(ImageId id) {
	auto& image = ResolveImage(id);
	if (!image.registered) {
		return;
	}
	const auto image_end = image.info.data.End();
	if (image_end == image.track_addr_end) {
		return;
	}
	if (!image.IsTracked() || image.track_addr_end > image_end) {
		EXIT("TextureCache: invalid image tail tracking range\n");
	}
	const auto address   = image.track_addr_end;
	const auto size      = image_end - address;
	image.track_addr_end = image_end;
	m_page_manager.UpdatePageWatchers<true>(address, size);
}

void TextureCache::UntrackImage(ImageId id) {
	auto& image = ResolveImage(id);
	if (!image.IsTracked()) {
		return;
	}
	const auto address   = image.track_addr;
	const auto size      = image.track_addr_end - image.track_addr;
	image.track_addr     = 0;
	image.track_addr_end = 0;
	if (size != 0) {
		m_page_manager.UpdatePageWatchers<false>(address, size);
	}
}

void TextureCache::UntrackImageHead(ImageId id) {
	auto&      image = ResolveImage(id);
	const auto begin = image.info.data.address;
	if (!image.IsTracked() || begin < image.track_addr) {
		return;
	}
	const auto address = (begin + TRACKER_PAGE_SIZE) & ~(TRACKER_PAGE_SIZE - 1);
	const auto size    = address - begin;
	image.track_addr   = address;
	if (image.track_addr == image.track_addr_end) {
		image.MarkMaybeCpuDirty();
		if (image.NeedsMaybeCpuHash()) {
			image.SetMaybeCpuHash(image.HashGuestEdges());
		}
		UntrackImage(id);
	}
	if (size != 0) {
		m_page_manager.UpdatePageWatchers<false>(begin, size);
	}
}

void TextureCache::UntrackImageTail(ImageId id) {
	auto&      image = ResolveImage(id);
	const auto end   = image.info.data.End();
	if (!image.IsTracked() || image.track_addr_end < end) {
		return;
	}
	const auto address   = end & ~(TRACKER_PAGE_SIZE - 1);
	const auto size      = end - address;
	image.track_addr_end = address;
	if (image.track_addr == image.track_addr_end) {
		image.MarkMaybeCpuDirty();
		if (image.NeedsMaybeCpuHash()) {
			image.SetMaybeCpuHash(image.HashGuestEdges());
		}
		UntrackImage(id);
	}
	if (size != 0) {
		m_page_manager.UpdatePageWatchers<false>(address, size);
	}
}

void TextureCache::TrackImageDownload(ImageId id) {
	std::lock_guard transaction(m_resource_mutex);
	CacheLock       lock(*this, m_lock);
	auto&           image = ResolveImage(id);
	TrackImageDownloadLocked(id, image);
}

void TextureCache::TrackImageDownloadLocked(ImageId id, Image& image) {
	if (m_readback_linear_images && !image.info.IsTiled() && !image.info.data.Empty()) {
		if (!image.IsGpuModified()) {
			EXIT("TextureCache: cannot enroll a non-GPU-owned image for download\n");
		}
		m_download_images.insert(id);
	}
}

Image& TextureCache::GetImage(ImageId id) {
	auto& image = ResolveImage(id);
	TouchImage(image);
	return image;
}

const Image& TextureCache::GetImage(ImageId id) const {
	return ResolveImage(id);
}

std::vector<ImageId> TextureCache::FindImagesInRegion(uint64_t address, uint64_t size,
                                                      bool page_overlap) const {
	return page_overlap ? m_image_owner_index.QueryCandidates(address, size)
	                    : m_image_owner_index.Query(address, size);
}

ImageId TextureCache::GetNullImage(const ImageDesc& desc) {
	auto&      command = m_scheduler.Current();
	const auto format  = desc.info.pixel_format;
	if (const auto found = m_null_images.find(format); found != m_null_images.end()) {
		RetainImage(command, found->second);
		return found->second;
	}
	ImageInfo info {};
	info.pixel_format    = desc.info.pixel_format;
	info.guest_format    = desc.info.guest_format;
	info.type            = Prospero::ImageType::kColor2D;
	info.extent          = vk::Extent3D{1, 1, 1};
	info.resources       = {1, 1};
	info.pitch           = 1;
	info.bytes_per_block = std::max(desc.info.bytes_per_block, 1u);
	info.samples         = 1;
	info.tile_mode       = Prospero::GpuEnumValue(Prospero::TileMode::kLinear);
	info.mip_layout[0]   = {0, info.bytes_per_block, 1, 1};
	const auto id        = InsertImage(info);
	m_null_images.emplace(format, id);
	RetainImage(command, id);
	return id;
}

void TextureCache::ValidateImageDesc(const ImageDesc& desc) const {
	ImageOps::Validate(desc.info);
	if (desc.view_info.format == vk::Format::eUndefined || desc.view_info.level_count == 0 ||
	    desc.view_info.layer_count == 0 ||
	    desc.view_info.base_level >= desc.info.resources.levels ||
	    desc.view_info.level_count > desc.info.resources.levels - desc.view_info.base_level ||
	    (!desc.info.IsVolume() &&
	     (desc.view_info.base_layer >= desc.info.resources.layers ||
	      desc.view_info.layer_count > desc.info.resources.layers - desc.view_info.base_layer))) {
		EXIT("TextureCache: invalid image view description\n");
	}
	if (desc.type == BindingType::DepthTarget && !IsSupportedDepthTargetFormat(desc.info)) {
		EXIT("TextureCache: unsupported depth image description\n");
	}
	if (desc.type == BindingType::VideoOut && !IsSupportedVideoOutFormat(desc.info)) {
		EXIT("TextureCache: unsupported video-out image description\n");
	}
	if (desc.type == BindingType::VideoOut &&
	    desc.info.metadata.compression == VideoOutCompression::Unsupported) {
		EXIT("TextureCache: unsupported compressed video-out description\n");
	}
}

void TextureCache::PrepareImageCopy(Image& image) {
	if (image.IsCpuDirty()) {
		image.RefreshComplete();
	}
}

void TextureCache::RefreshCopySource(ImageId id) {
	auto& image = ResolveImage(id);
	RefreshImage(id, ImageDesc {.info = image.info, .view_info = {}, .type = UploadBinding(image)});
	if (image.IsDefinitelyCpuDirty()) {
		EXIT("TextureCache: image copy source remained CPU-dirty after refresh\n");
	}
}

bool TextureCache::CopyD16(Image& destination, Image& source) {
	const bool source_depth      = source.info.IsDepth();
	const bool destination_depth = destination.info.IsDepth();
	if (source_depth == destination_depth) {
		return false;
	}
	auto&      depth          = source_depth ? source : destination;
	auto&      color          = source_depth ? destination : source;
	const auto transfer_bytes = DepthAspectTransferBytes(depth.backing.format);
	if (depth.info.bytes_per_block != sizeof(uint16_t) ||
	    color.info.bytes_per_block != sizeof(uint16_t) || transfer_bytes != sizeof(uint32_t)) {
		return false;
	}
	EXIT_IF(source.backing.samples != 1 || destination.backing.samples != 1 ||
	        source.info.resources.levels != 1 || destination.info.resources.levels != 1 ||
	        source.info.extent != destination.info.extent ||
	        source.info.resources.layers != destination.info.resources.layers);

	const auto     layers = depth.info.resources.layers;
	const uint64_t depth_slice =
	    static_cast<uint64_t>(depth.info.pitch) * depth.info.extent.height * transfer_bytes;
	const uint64_t color_slice =
	    static_cast<uint64_t>(color.info.pitch) * color.info.extent.height * sizeof(uint16_t);
	EXIT_IF(layers == 0 || depth_slice > UINT64_MAX / layers || color_slice > UINT64_MAX / layers);
	const auto                       depth_size = depth_slice * layers;
	const auto                       color_size = color_slice * layers;
	std::vector<vk::BufferImageCopy> depth_copies(layers);
	std::vector<vk::BufferImageCopy> color_copies(layers);
	for (uint32_t layer = 0; layer < layers; layer++) {
		depth_copies[layer].bufferOffset      = depth_slice * layer;
		depth_copies[layer].bufferRowLength   = depth.info.pitch;
		depth_copies[layer].bufferImageHeight = depth.info.extent.height;
		depth_copies[layer].imageSubresource  = {vk::ImageAspectFlagBits::eDepth, 0, layer, 1};
		depth_copies[layer].imageExtent       = depth.info.extent;
		color_copies[layer].bufferOffset      = color_slice * layer;
		color_copies[layer].bufferRowLength   = color.info.pitch;
		color_copies[layer].bufferImageHeight = color.info.extent.height;
		color_copies[layer].imageSubresource  = {vk::ImageAspectFlagBits::eColor, 0, layer, 1};
		color_copies[layer].imageExtent       = color.info.extent;
	}

	auto                         depth_buffer = m_tiler->GetScratchBuffer(depth_size);
	auto                         color_buffer = m_tiler->GetScratchBuffer(color_size);
	const TileManager::D16Layout promote_layout {
	    .width               = depth.info.extent.width,
	    .height              = depth.info.extent.height,
	    .layers              = layers,
	    .source_row_stride   = static_cast<uint64_t>(color.info.pitch) * sizeof(uint16_t),
	    .target_row_stride   = static_cast<uint64_t>(depth.info.pitch) * transfer_bytes,
	    .source_slice_stride = color_slice,
	    .target_slice_stride = depth_slice,
	};
	const bool d32 = DepthAspectTransferFormat(depth.backing.format) == vk::Format::eD32Sfloat;
	if (source_depth) {
		source.Download(depth_copies, depth_buffer.buffer, depth_buffer.offset, depth_buffer.size);
		m_tiler->ConvertD16(depth_buffer, color_buffer, TileManager::D16Direction::Demote, d32,
		                    {.width               = promote_layout.width,
		                     .height              = promote_layout.height,
		                     .layers              = promote_layout.layers,
		                     .source_row_stride   = promote_layout.target_row_stride,
		                     .target_row_stride   = promote_layout.source_row_stride,
		                     .source_slice_stride = promote_layout.target_slice_stride,
		                     .target_slice_stride = promote_layout.source_slice_stride});
		destination.Upload(color_copies, color_buffer.buffer, color_buffer.offset,
		                   color_buffer.size);
	} else {
		source.Download(color_copies, color_buffer.buffer, color_buffer.offset, color_buffer.size);
		m_tiler->ConvertD16(color_buffer, depth_buffer, TileManager::D16Direction::Promote, d32,
		                    promote_layout);
		destination.Upload(depth_copies, depth_buffer.buffer, depth_buffer.offset,
		                   depth_buffer.size);
	}
	return true;
}

void TextureCache::CopyImage(ImageId destination_id, ImageId source_id) {
	RefreshCopySource(source_id);
	auto& destination = ResolveImage(destination_id);
	auto& source      = ResolveImage(source_id);
	TrackImage(destination_id);
	if (source.backing.samples != destination.backing.samples) {
		EXIT("TextureCache: cannot issue an unequal-sample image copy\n");
	}
	PrepareImageCopy(destination);
	if (source.IsBufferModified()) {
		if (source.info.data == destination.info.data) {
			destination.MarkBufferModified();
		}
		return;
	}
	const bool source_depth = source.info.IsDepth();
	const bool dest_depth   = destination.info.IsDepth();
	const bool direct_copy =
	    source.backing.format == destination.backing.format ||
	    (!source_depth && !dest_depth &&
	     vk::blockSize(source.backing.format) == vk::blockSize(destination.backing.format));
	if (direct_copy) {
		destination.CopyImage(source);
	} else if (!CopyD16(destination, source)) {
		if (source.backing.samples != 1 || destination.backing.samples != 1) {
			EXIT("TextureCache: cross-format multisample image copy is unsupported\n");
		}
		auto& copy_buffer = m_buffer_cache.GetUtilityBuffer(MemoryUsage::DeviceLocal);
		destination.CopyImageWithBuffer(source, copy_buffer);
	}
	RetainImage(m_scheduler.Current(), source_id);
	RetainImage(m_scheduler.Current(), destination_id);
	if (source.IsGpuModified()) {
		destination.MarkGpuModified();
	}
	destination.ClearBufferModified();
}

void TextureCache::CopyImageMip(ImageId destination_id, ImageId source_id, uint32_t mip,
                                uint32_t layer) {
	RefreshCopySource(source_id);
	auto& destination = ResolveImage(destination_id);
	auto& source      = ResolveImage(source_id);
	TrackImage(destination_id);
	if (source.IsBufferModified() || source.backing.samples != destination.backing.samples) {
		EXIT("TextureCache: invalid mip-copy ownership or sample count\n");
	}
	destination.CopyMip(source, mip, layer);
	RetainImage(m_scheduler.Current(), source_id);
	RetainImage(m_scheduler.Current(), destination_id);
	if (source.IsGpuModified()) {
		destination.MarkGpuModified();
	}
}

ImageId TextureCache::ResolveDepthOverlap(const ImageInfo& requested, BindingType binding,
                                          ImageId cached_id) {
	auto& cached = ResolveImage(cached_id);
	if (!cached.info.IsDepth() && !requested.IsDepth()) {
		return {};
	}
	const bool stencil_match = requested.HasStencil() == cached.info.HasStencil();
	const bool bpp_match     = requested.bytes_per_block == cached.info.bytes_per_block;
	bool       recreate      = cached.info.resources < requested.resources;
	switch (binding) {
		case BindingType::Texture: recreate |= requested.IsDepth() && !cached.info.IsDepth(); break;
		case BindingType::Storage: recreate |= cached.info.IsDepth(); break;
		case BindingType::RenderTarget: recreate |= cached.info.IsDepth(); break;
		case BindingType::DepthTarget:
			recreate |= !cached.info.IsDepth();
			recreate |= cached.info.IsDepth() && !(stencil_match && bpp_match);
			break;
		case BindingType::VideoOut: recreate |= cached.info.IsDepth(); break;
	}
	if (!recreate) {
		return cached_id;
	}
	RefreshImage(cached_id,
	             ImageDesc {.info = cached.info, .view_info = {}, .type = UploadBinding(cached)});
	auto info                 = requested;
	info.resources            = std::max(requested.resources, cached.info.resources);
	info.htile_clear_mask     = 0;
	const auto replacement_id = InsertImage(info);
	auto&      replacement    = ResolveImage(replacement_id);
	replacement.usage         = cached.usage;
	if (cached.binding.is_bound || cached.binding.is_target) {
		cached.binding.needs_rebind = true;
	}
	bool copied = false;
	if (cached.backing.samples == replacement.backing.samples) {
		const bool copy_supported =
		    cached.backing.samples == 1 || cached.backing.format == replacement.backing.format ||
		    (!cached.info.IsDepth() && !replacement.info.IsDepth() &&
		     ImageViewOps::FormatsCompatible(cached.backing.format, replacement.backing.format));
		if (copy_supported) {
			CopyImage(replacement_id, cached_id);
			copied = true;
		} else {
			LOGF_COLOR(Log::Color::BrightYellow,
			           "TextureCache: unsupported cross-format multisample depth copy\n");
		}
	} else if (cached.backing.samples == 1 && replacement.backing.samples > 1 &&
	           replacement.info.IsDepth()) {
		RefreshCopySource(cached_id);
		if (cached.IsBufferModified() || cached.IsDefinitelyCpuDirty()) {
			EXIT("TextureCache: multisample depth conversion source is not native-current\n");
		}
		PrepareImageCopy(replacement);
		m_blit_helper.ReinterpretColorAsMsDepth(cached, replacement);
		auto& command = m_scheduler.Current();
		RetainImage(command, cached_id);
		RetainImage(command, replacement_id);
		CommitGpuWrite(replacement);
		copied = true;
	} else {
		LOGF_COLOR(Log::Color::BrightYellow,
		           "TextureCache: unsupported unequal-sample depth overlap copy (%u -> %u)\n",
		           cached.backing.samples, replacement.backing.samples);
	}
	if (copied) {
		DeleteImages(std::array {cached_id}, cached_id);
	} else {
		ClearGpuModified(cached_id);
		DeleteImage(cached_id);
	}
	return replacement_id;
}

TextureCache::OverlapResult TextureCache::ResolveOverlap(const ImageInfo& requested,
                                                         BindingType binding, ImageId cached_id,
                                                         ImageId merged_id) {
	auto owner = ResolveOwner(cached_id);
	if (owner == nullptr) {
		return {merged_id};
	}
	auto&      cached       = *owner;
	const auto current_tick = m_scheduler.CurrentTick();
	const bool safe_to_delete =
	    current_tick - std::min(current_tick, cached.tick_accessed_last) > NumFramesBeforeRemoval;

	if (requested.data.address == cached.info.data.address) {
		const uint32_t requested_block = requested.bytes_per_block * requested.samples;
		const uint32_t cached_block    = cached.info.bytes_per_block * cached.info.samples;
		if (requested.BlockExtent() != cached.info.BlockExtent() ||
		    requested_block != cached_block) {
			if (safe_to_delete) {
				DeleteImages(std::array {cached_id}, cached_id);
			}
			return {merged_id};
		}

		if (const auto depth_id = ResolveDepthOverlap(requested, binding, cached_id)) {
			return {depth_id};
		}
		if (requested.IsBlock() && !cached.info.IsBlock()) {
			return {ExpandImage(requested, cached_id)};
		}
		if (requested.data.size == cached.info.data.size &&
		    (requested.IsVolume() || cached.info.IsVolume())) {
			return {ExpandImage(requested, cached_id)};
		}
		if (requested.tile_mode != cached.info.tile_mode) {
			if (safe_to_delete) {
				DeleteImages(std::array {cached_id}, cached_id);
			}
			return {merged_id};
		}
		const bool expanded_color_alias = CanUseExpandedColorAlias(
		    binding == BindingType::Texture || binding == BindingType::Storage,
		    requested.metadata.kind == ImageMetadataKind::None,
		    cached.info.metadata.kind == ImageMetadataKind::Dcc, cached.color_metadata);
		if (!ImageMetadataIdentityMatches(cached.info.metadata, requested.metadata) &&
		    !expanded_color_alias) {
			// The same pixel address can be reused with a different DCC/HTILE allocation.
			// Keep those host variants separate; treating either as the other can select stale
			// expanded pixels instead of the current Sony metadata lifetime.
			return {merged_id};
		}
		if (requested.pixel_format != cached.info.pixel_format ||
		    requested.data.size <= cached.info.data.size) {
			const auto result_id = merged_id ? merged_id : cached_id;
			const auto result    = ResolveOwner(result_id);
			return {result != nullptr && ImageViewOps::FormatsCompatible(result->info.pixel_format,
			                                                             requested.pixel_format)
			            ? result_id
			            : ImageId {}};
		}
		if (requested.type == cached.info.type && requested.resources > cached.info.resources) {
			return {ExpandImage(requested, cached_id)};
		}
		EXIT("TextureCache: unresolvable equal-address image overlap, address=0x%016" PRIx64
		     " requested=%ux%u "
		     "cached=%ux%u requested_size=0x%016" PRIx64 " cached_size=0x%016" PRIx64
		     " type=%u/%u tile=%u/%u\n",
		     requested.data.address, requested.resources.levels, requested.resources.layers,
		     cached.info.resources.levels, cached.info.resources.layers, requested.data.size,
		     cached.info.data.size, static_cast<uint32_t>(requested.type),
		     static_cast<uint32_t>(cached.info.type), requested.tile_mode, cached.info.tile_mode);
	}

	if (requested.data.address > cached.info.data.address) {
		const int32_t mip = requested.MipOf(cached.info);
		if (mip >= 0) {
			const int32_t layer = requested.SliceOf(cached.info, mip);
			if (layer >= 0) {
				return {cached_id, mip, layer};
			}
		}
		if (safe_to_delete) {
			DeleteImages(std::array {cached_id}, cached_id);
		}
		return {};
	}

	const int32_t mip = cached.info.MipOf(requested);
	if (mip >= 0) {
		const int32_t layer = cached.info.SliceOf(requested, mip);
		if (layer >= 0) {
			if (cached.binding.is_target) {
				cached.binding.needs_rebind = true;
				if (merged_id) {
					ResolveImage(merged_id).binding.is_target = true;
				}
				DeleteImages(std::array {cached_id}, cached_id);
				return {merged_id};
			}
			if (merged_id) {
				CopyImageMip(merged_id, cached_id, static_cast<uint32_t>(mip),
				             static_cast<uint32_t>(layer));
				DeleteImages(std::array {cached_id}, cached_id);
			}
		}
	}
	return {merged_id};
}

ImageId TextureCache::ExpandImage(const ImageInfo& info, ImageId source_id) {
	RefreshCopySource(source_id);
	const auto expanded_id = InsertImage(info);
	auto&      expanded    = ResolveImage(expanded_id);
	auto&      source      = ResolveImage(source_id);
	expanded.usage         = source.usage;
	if (source.binding.is_bound || source.binding.is_target) {
		source.binding.needs_rebind = true;
	}
	InitializeImage(expanded_id,
	                ImageDesc {.info = info, .view_info = {}, .type = UploadBinding(source)});
	CopyImage(expanded_id, source_id);
	DeleteImages(std::array {source_id}, source_id);
	return expanded_id;
}

struct TextureCache::ColorTransferPlan {
	TextureUploadLayout              layout;
	std::vector<vk::BufferImageCopy> regions;
	std::vector<GpuTileInfo>         tiles;
	bool                             tiled       = false;
	bool                             swap_bgra16 = false;
	bool                             valid       = false;
};

struct TextureCache::DownloadPlan {
	ColorTransferPlan color;
	bool              depth = false;
	bool              valid = false;
};

TextureCache::ColorTransferPlan
TextureCache::BuildColorTransfer(const Image& image, BindingType binding,
                                 TransferDirection direction) const {
	const auto& info             = image.info;
	uint32_t    format           = info.guest_format;
	uint32_t    layers           = info.TransferLayers();
	bool        volume           = info.IsVolume();
	bool        layered          = info.IsLayered();
	bool        allow_depth_tile = direction == TransferDirection::Upload;
	const char* owner =
	    direction == TransferDirection::Upload ? "TextureCache" : "TextureCache readback";

	ColorTransferPlan plan;
	if (direction == TransferDirection::Upload) {
		switch (binding) {
			case BindingType::Texture: break;
			case BindingType::Storage: owner = "StorageTextureCache"; break;
			case BindingType::RenderTarget:
				if (info.resources.layers == 0 || info.data.size % info.resources.layers != 0 ||
				    info.samples != 1 || image.backing.samples != 1) {
					EXIT("TextureCache: invalid color-attachment upload\n");
				}
				format           = ImageOps::RenderTargetTransferFormat(info.bytes_per_block);
				allow_depth_tile = false;
				plan.swap_bgra16 = info.bgra16;
				owner            = "RenderTarget";
				break;
			case BindingType::VideoOut:
				if (info.resources.layers == 0 || info.data.size % info.resources.layers != 0 ||
				    info.samples != 1 || image.backing.samples != 1 ||
				    info.metadata.compression != VideoOutCompression::Uncompressed) {
					EXIT("TextureCache: invalid color-attachment upload\n");
				}
				format           = info.guest_format;
				layers           = info.resources.layers;
				volume           = false;
				layered          = layers > 1;
				allow_depth_tile = false;
				plan.swap_bgra16 = info.bgra16;
				owner            = "VideoOut";
				break;
			case BindingType::DepthTarget: return plan;
		}
	} else {
		if (binding == BindingType::DepthTarget) {
			return plan;
		}
		format           = binding == BindingType::RenderTarget
		                       ? ImageOps::RenderTargetTransferFormat(info.bytes_per_block)
		                       : info.guest_format;
		allow_depth_tile = binding == BindingType::Storage;
		plan.swap_bgra16 = info.bgra16;
	}

	plan.layout = TextureCalcUploadLayout(format, info.extent.width, info.extent.height,
	                                      info.resources.levels, layers, info.pitch, info.tile_mode,
	                                      info.data.size, allow_depth_tile, volume, owner);
	plan.regions = TextureBuildImageCopies(plan.layout, info.extent.width, info.extent.height,
	                                       layers, info.resources.levels, layered, volume);
	plan.tiled   = static_cast<Prospero::TileMode>(plan.layout.tile) != Prospero::TileMode::kLinear;
	if (plan.tiled) {
		// CMASK/FMASK decoding is intentionally deferred.
		if (plan.layout.tile_family == TileBlockFamily::Depth64KB &&
		    Prospero::IsFmaskTextureFormat(format)) {
			return plan;
		}
		if (!TextureBuildGpuTileInfos(info.data.size, plan.regions, plan.layout, format, layers,
		                              info.resources.levels, plan.tiles)) {
			return plan;
		}
	}
	plan.valid = true;
	return plan;
}

TextureCache::DownloadPlan TextureCache::BuildDownload(const Image& image) const {
	const auto&  info = image.info;
	DownloadPlan plan {.depth = info.IsDepth()};
	if (info.samples != 1 || image.backing.samples != 1) {
		return plan;
	}
	if (plan.depth) {
		plan.valid = IsSupportedDepthPlaneReadback(info) && info.resources.layers != 0 &&
		             info.data.size % info.resources.layers == 0 &&
		             Prospero::NumBytesPerElement(info.guest_format) == info.bytes_per_block;
		return plan;
	}
	if (info.metadata.compression != VideoOutCompression::Uncompressed) {
		return plan;
	}
	plan.color = BuildColorTransfer(image, UploadBinding(image), TransferDirection::Download);
	plan.valid = plan.color.valid;
	return plan;
}

void TextureCache::UploadImage(Image& image, const ImageDesc& desc, Buffer& source,
                               uint64_t source_offset) {
	const auto& info   = image.info;
	const auto  upload = [&](std::vector<vk::BufferImageCopy>& copies, TileManager::Result linear) {
		for (auto& copy: copies) {
			copy.bufferOffset += linear.offset;
		}
		image.Upload(copies, linear.buffer, linear.offset, linear.size);
	};

	if (desc.type != BindingType::DepthTarget) {
		auto plan = BuildColorTransfer(image, desc.type, TransferDirection::Upload);
		EXIT_NOT_IMPLEMENTED(!plan.valid);
		TileManager::Result linear {source.Handle(), source_offset, info.data.size};
		if (plan.tiled) {
			linear = m_tiler->Detile(source.Handle(), source_offset, info.data.size, info.data.size,
			                         plan.tiles);
		}
		if (plan.swap_bgra16) {
			linear = m_tiler->SwapBgra16(linear);
		}
		upload(plan.regions, linear);
		return;
	}

	if (desc.type != BindingType::DepthTarget || info.samples != 1 || image.backing.samples != 1 ||
	    info.resources.layers == 0 || info.data.size % info.resources.layers != 0 ||
	    Prospero::NumBytesPerElement(info.guest_format) != info.bytes_per_block) {
		EXIT("TextureCache: invalid depth upload\n");
	}
	TileBlockLayout block {};
	EXIT_NOT_IMPLEMENTED(
	    !TileGetBlockLayout(TileBlockFamily::Depth64KB, info.bytes_per_block, block));
	const auto                       layers          = info.resources.layers;
	const auto                       full_slice_size = info.data.size / layers;
	std::vector<GpuTileInfo>         tiles;
	std::vector<vk::BufferImageCopy> copies(layers);
	tiles.reserve(layers);
	for (uint32_t layer = 0; layer < layers; layer++) {
		const uint64_t offset  = full_slice_size * layer;
		auto&          copy    = copies[layer];
		copy.bufferOffset      = offset;
		copy.bufferRowLength   = info.pitch;
		copy.bufferImageHeight = info.extent.height;
		copy.imageSubresource  = {vk::ImageAspectFlagBits::eDepth, 0, layer, 1};
		copy.imageExtent       = vk::Extent3D{info.extent.width, info.extent.height, 1};
		if (static_cast<Prospero::TileMode>(info.tile_mode) != Prospero::TileMode::kLinear) {
			tiles.push_back({block.family, block.bytes_per_element, offset, full_slice_size, offset,
			                 full_slice_size, 0, info.extent.width, info.extent.height, 1,
			                 info.pitch});
			tiles.back().surface_z = layer;
		}
	}
	TileManager::Result linear {source.Handle(), source_offset, source.Size() - source_offset};
	if (!tiles.empty()) {
		linear =
		    m_tiler->Detile(source.Handle(), source_offset, info.data.size, info.data.size, tiles);
	}
	const auto transfer_bytes = DepthAspectTransferBytes(info.pixel_format);
	if (transfer_bytes != info.bytes_per_block) {
		const uint64_t texels_per_slice = static_cast<uint64_t>(info.pitch) * info.extent.height;
		EXIT_NOT_IMPLEMENTED(info.bytes_per_block != sizeof(uint16_t) ||
		                     transfer_bytes != sizeof(uint32_t) || texels_per_slice > UINT32_MAX ||
		                     texels_per_slice > UINT64_MAX / transfer_bytes);
		const uint64_t transfer_slice = texels_per_slice * transfer_bytes;
		EXIT_NOT_IMPLEMENTED(transfer_slice > UINT64_MAX / layers);
		auto promoted = m_tiler->GetScratchBuffer(transfer_slice * layers);
		m_tiler->ConvertD16(
		    linear, promoted, TileManager::D16Direction::Promote,
		    info.pixel_format == vk::Format::eD32SfloatS8Uint,
		    {.width               = info.extent.width,
		     .height              = info.extent.height,
		     .layers              = layers,
		     .source_row_stride   = static_cast<uint64_t>(info.pitch) * sizeof(uint16_t),
		     .target_row_stride   = static_cast<uint64_t>(info.pitch) * sizeof(uint32_t),
		     .source_slice_stride = full_slice_size,
		     .target_slice_stride = transfer_slice});
		linear = promoted;
		for (uint32_t layer = 0; layer < layers; layer++) {
			copies[layer].bufferOffset = transfer_slice * layer;
		}
	}
	upload(copies, linear);
}

void TextureCache::InitializeImage(ImageId id, const ImageDesc& desc) {
	auto& image = ResolveImage(id);
	if (image.info.data.Empty()) {
		return;
	}
	TrackImage(id);
	if (image.info.metadata.compression != VideoOutCompression::Uncompressed) {
		if (image.IsCpuDirty()) {
			image.RefreshComplete();
		}
		return;
	}
	if (image.info.samples > 1) {
		return;
	}
	bool       data_imported = false;
	const bool upload        = image.IsBufferModified() || image.IsCpuDirty();
	if (upload) {
		const auto source =
		    m_buffer_cache.ObtainBufferForImage(image.info.data.address, image.info.data.size);
		if (source.buffer == nullptr) {
			EXIT("TextureCache: failed to obtain image upload source\n");
		}
		data_imported = true;
		UploadImage(image, desc, *source.buffer, source.offset);
	}
	if (data_imported) {
		image.ClearBufferModified();
	}
	if (image.IsCpuDirty()) {
		image.RefreshComplete();
	}
}

void TextureCache::RefreshImage(ImageId id, const ImageDesc& desc) {
	TrackImage(id);
	auto& image = ResolveImage(id);
	if (image.IsMaybeCpuDirty()) {
		const auto hash = image.HashGuestEdges();
		if (image.NeedsMaybeCpuHash()) {
			image.SetMaybeCpuHash(hash);
			return;
		}
		(void)image.ResolveMaybeCpuHash(hash);
	}
	bool cpu_dirty = image.IsBufferModified() || image.IsDefinitelyCpuDirty();
	if (image.info.metadata.compression != VideoOutCompression::Uncompressed) {
		if (cpu_dirty) {
			EXIT("TextureCache: compressed guest image refresh is unsupported\n");
		}
		return;
	}
	if (!cpu_dirty) {
		return;
	}
	InitializeImage(id, desc);
}

void TextureCache::AssociateStencil(ImageId depth_id, GuestRange stencil) {
	std::lock_guard transaction(m_resource_mutex);
	CacheLock       lock(*this, m_lock);
	AssociateStencilLocked(depth_id, stencil);
}

void TextureCache::AssociateStencilLocked(ImageId depth_id, GuestRange stencil) {
	if (!stencil.Valid()) {
		EXIT("TextureCache: invalid stencil association range\n");
	}
	auto& depth = ResolveImage(depth_id);
	if (!depth.info.IsDepth() || !depth.info.HasStencil()) {
		EXIT("TextureCache: stencil association requires a depth/stencil image\n");
	}

	ImageId association {};
	for (const auto id: FindImagesInRegion(stencil.address, stencil.size, false)) {
		const auto owner = ResolveOwner(id);
		if (owner != nullptr && owner->info.data.address == stencil.address) {
			association = id;
		}
	}
	if (!association) {
		ImageInfo info {};
		info.data   = stencil;
		info.extent = depth.info.extent;
		association = InsertImage(info);
	}
	auto& record = ResolveImage(association);
	TouchImage(record);
	record.AssociateDepth(depth_id);
}

ImageId TextureCache::FindImage(ImageDesc& desc, bool exact_format) {
	auto& command = m_scheduler.Current();
	if (command.IsInvalid()) {
		EXIT("TextureCache: image lookup requires a valid command buffer\n");
	}
	ValidateImageDesc(desc);
	if (desc.info.data.Empty()) {
		CacheLock lock(*this, m_lock);
		return GetNullImage(desc);
	}

	ImageId result {};
	bool    inserted_new = false;
	{
		std::lock_guard            transaction(m_resource_mutex);
		CacheLock                  lock(*this, m_lock);
		const std::vector<ImageId> candidates =
		    FindImagesInRegion(desc.info.data.address, desc.info.data.size, false);

		for (const auto id: candidates) {
			const auto owner = ResolveOwner(id);
			if (owner == nullptr) {
				continue;
			}
			if (SameBacking(owner->info, desc.info, exact_format)) {
				result = id;
			}
		}

		int32_t view_mip   = -1;
		int32_t view_layer = -1;
		if (!result) {
			for (const auto candidate: candidates) {
				view_mip         = -1;
				view_layer       = -1;
				const auto owner = ResolveOwner(candidate);
				if (owner == nullptr) {
					continue;
				}
				const auto& merged_info = result ? ResolveImage(result).info : desc.info;
				const auto  overlap     = ResolveOverlap(merged_info, desc.type, candidate, result);
				if (overlap.image) {
					result     = overlap.image;
					view_mip   = overlap.mip;
					view_layer = overlap.layer;
				}
			}
		}

		if (result) {
			auto& resolved = ResolveImage(result);
			if (exact_format && resolved.info.pixel_format != desc.info.pixel_format) {
				result = {};
			} else if (resolved.info.resources < desc.info.resources) {
				result = ExpandImage(desc.info, result);
			}
		}
		if (!result) {
			result         = InsertImage(desc.info);
			inserted_new   = true;
			auto& inserted = ResolveImage(result);
			if (m_buffer_cache.HasGpuDirtyBytes(inserted.info.data.address,
			                                    inserted.info.data.size)) {
				inserted.MarkBufferModified();
			}
		}
		if (inserted_new) {
			InitializeImage(result, desc);
		} else {
			RefreshImage(result, desc);
		}

		auto& image = ResolveImage(result);
		if (desc.type == BindingType::VideoOut &&
		    desc.info.metadata.compression != VideoOutCompression::Uncompressed) {
			const bool guest_dirty = image.IsBufferModified() || image.IsCpuDirty();
			const bool native_current =
			    (image.usage.render_target || image.IsGpuModified()) && !guest_dirty;
			if (!native_current) {
				EXIT("TextureCache: compressed video-out read requires clean native GPU "
				     "contents\n");
			}
		}
		if (view_mip >= 0) {
			desc.view_info.base_level = static_cast<uint32_t>(view_mip);
		}
		if (view_layer >= 0) {
			desc.view_info.base_layer = static_cast<uint32_t>(view_layer);
		}
		image.tick_accessed_last = m_scheduler.CurrentTick();
		TouchImage(image);
		RetainImage(command, result);
	}
	return result;
}

ImageId TextureCache::FindImageFromRange(uint64_t address, uint64_t size, bool ensure_valid) {
	if (!GuestRange {address, size}.Valid()) {
		return {};
	}
	CacheLock            lock(*this, m_lock);
	std::vector<ImageId> matches;
	for (const auto id: FindImagesInRegion(address, size, false)) {
		auto owner = ResolveOwner(id);
		if (owner == nullptr || owner->info.data.address != address) {
			continue;
		}
		if (ensure_valid && owner->depth_id) {
			owner = ResolveOwner(owner->depth_id);
		}
		if (owner == nullptr || (ensure_valid && !owner->SafeToDownload())) {
			continue;
		}
		matches.push_back(id);
	}
	ImageId selected {};
	if (matches.size() == 1) {
		selected = matches.front();
	} else {
		for (const auto id: matches) {
			const auto& image = ResolveImage(id);
			if (image.info.data.size == size) {
				selected = id;
				break;
			}
		}
	}
	if (selected) {
		if (ensure_valid) {
			const auto owner = ResolveOwner(selected);
			if (owner != nullptr && owner->depth_id) {
				selected = owner->depth_id;
			}
		}
		RetainImage(m_scheduler.Current(), selected);
	}
	return selected;
}

vk::ImageView TextureCache::FindTexture(ImageId id, const ImageDesc& desc) {
	std::lock_guard transaction(m_resource_mutex);
	CacheLock       lock(*this, m_lock);
	auto&           image = ResolveImage(id);
	TouchImage(image);
	if (!image.info.data.Empty()) {
		if (!image.registered || image.depth_id || image.binding.needs_rebind) {
			EXIT("TextureCache: texture requires rediscovery before final acquisition\n");
		}
		RefreshImage(id, desc);
	}
	switch (desc.type) {
		case BindingType::Texture: break;
		case BindingType::Storage:
			if (image.info.data.Empty()) {
				image.MarkGpuModified();
			} else {
				if (!image.registered || image.depth_id) {
					EXIT("TextureCache: cannot acquire an unavailable storage image\n");
				}
				CommitGpuWrite(image);
			}
			TrackImageDownloadLocked(id, image);
			break;
		default: EXIT("TextureCache: invalid texture binding\n");
	}
	const auto view = image.FindView(desc.view_info);
	RetainImage(m_scheduler.Current(), id);
	return view;
}

vk::ImageView TextureCache::FindRenderTarget(ImageId id, const ImageDesc& desc) {
	if (desc.type != BindingType::RenderTarget) {
		EXIT("TextureCache: invalid color-target binding\n");
	}
	std::lock_guard transaction(m_resource_mutex);
	CacheLock       lock(*this, m_lock);
	auto&           image = ResolveImage(id);
	if (!image.registered || image.depth_id || image.binding.needs_rebind) {
		EXIT("TextureCache: color target requires rediscovery before final acquisition\n");
	}
	TouchImage(image);
	RefreshImage(id, desc);
	// A normal CB write starts a new compressed lifetime. Metadata operations
	// deliberately bypass FindRenderTarget and therefore do not reset this state.
	image.color_metadata.Reset(desc.info.color_metadata);
	CommitGpuWrite(image);
	image.usage.render_target = true;
	TrackImageDownloadLocked(id, image);
	const auto view = image.FindView(desc.view_info);
	RetainImage(m_scheduler.Current(), id);
	return view;
}

vk::ImageView TextureCache::FindDepthTarget(ImageId id, const ImageDesc& desc) {
	if (desc.type != BindingType::DepthTarget) {
		EXIT("TextureCache: invalid depth-target binding\n");
	}
	std::lock_guard transaction(m_resource_mutex);
	CacheLock       lock(*this, m_lock);
	auto&           image = ResolveImage(id);
	if (!image.registered || image.depth_id || image.binding.needs_rebind) {
		EXIT("TextureCache: depth target requires rediscovery before final acquisition\n");
	}
	TouchImage(image);
	RefreshImage(id, desc);
	if (desc.info.HasMetadata()) {
		image.info.metadata = desc.info.metadata;
		m_surface_metas.try_emplace(desc.info.metadata.range.address,
		                            MetaDataInfo {.clear_mask = image.info.htile_clear_mask});
	}
	CommitGpuWrite(image);
	image.usage.depth_target = true;
	if (desc.info.HasStencil()) {
		AssociateStencilLocked(id, desc.info.stencil);
	}
	const auto view = image.FindView(desc.view_info);
	RetainImage(m_scheduler.Current(), id);
	return view;
}

void TextureCache::MarkGpuWritten(ImageId id) {
	std::lock_guard transaction(m_resource_mutex);
	CacheLock       lock(*this, m_lock);
	auto&           image = ResolveImage(id);
	if (!image.registered || image.depth_id) {
		EXIT("TextureCache: cannot mark an unavailable image GPU-written\n");
	}
	TrackImage(id);
	CommitGpuWrite(image);
}

bool TextureCache::ApplyColorMetadataOperation(ImageId id, uint8_t mode,
                                               const ImageSubresourceRange& range) {
	std::lock_guard transaction(m_resource_mutex);
	CacheLock       lock(*this, m_lock);
	auto&           image = ResolveImage(id);
	// A native render target already contains expanded semantic pixels. Guest
	// memory containing DCC/CMASK/FMASK bytes does not; decoding that metadata is
	// still unsupported and must remain fail-closed.
	if (!image.registered || image.depth_id || image.info.IsDepth() || !image.IsGpuModified()) {
		return false;
	}
	// State is currently tracked per image. A partial Sony mip/slice operation
	// must not publish untouched subresources through an uncompressed alias.
	// Single-mip/single-layer postprocess targets take the exact path; partial
	// operations remain conservatively unavailable until per-subresource state exists.
	if (range.base_level != 0 || range.level_count != image.info.resources.levels ||
	    range.base_layer != 0 || range.layer_count != image.info.resources.layers) {
		return false;
	}
	return image.color_metadata.Apply(mode);
}

void TextureCache::CommitGpuWrite(Image& image) {
	if (image.depth_id || image.backing.image == nullptr) {
		EXIT("TextureCache: stencil association cannot own image contents\n");
	}
	image.ClearBufferModified();
	if (image.IsCpuDirty()) {
		image.RefreshComplete();
	}
	image.MarkGpuModified();
}

bool TextureCache::ClearImageFromBuffer(CommandBuffer& command, uint64_t address, uint64_t size,
                                        uint32_t packed_clear) {
	if (command.IsInvalid() || !GuestRange {address, size}.Valid()) {
		EXIT("TextureCache: invalid image clear\n");
	}
	std::lock_guard      transaction(m_resource_mutex);
	CacheLock            lock(*this, m_lock);
	ImageId              selected {};
	vk::ImageAspectFlags aspect {};
	for (const auto id: FindImagesInRegion(address, size, false)) {
		auto owner = ResolveOwner(id);
		if (owner == nullptr) {
			continue;
		}
		vk::ImageAspectFlags candidate {};
		ImageId              candidate_id = id;
		if (owner->depth_id && owner->info.data.address == address &&
		    owner->info.data.size == size) {
			candidate    = vk::ImageAspectFlagBits::eStencil;
			candidate_id = owner->depth_id;
			owner        = ResolveOwner(candidate_id);
			if (owner == nullptr || owner->backing.image == nullptr || !owner->info.HasStencil()) {
				continue;
			}
		} else if (!owner->depth_id && owner->info.data.address == address &&
		           owner->info.data.size == size) {
			candidate = owner->info.IsDepth() ? vk::ImageAspectFlagBits::eDepth
			                                  : vk::ImageAspectFlagBits::eColor;
		}
		if (!candidate) {
			continue;
		}
		if (selected && selected != candidate_id) {
			return false;
		}
		selected = candidate_id;
		aspect   = candidate;
	}
	if (!selected) {
		return false;
	}
	auto&               image = ResolveImage(selected);
	vk::ClearColorValue color_clear {};
	float               depth_clear   = 0.0f;
	uint8_t             stencil_clear = 0;
	if (aspect == vk::ImageAspectFlagBits::eColor) {
		if (!DecodePackedColorClear(image.info.pixel_format, packed_clear, color_clear)) {
			return false;
		}
	} else {
		if ((aspect == vk::ImageAspectFlagBits::eDepth &&
		     !DecodePackedDepthClear(image.info.pixel_format, packed_clear, depth_clear)) ||
		    (aspect == vk::ImageAspectFlagBits::eStencil &&
		     !DecodePackedStencilClear(packed_clear, stencil_clear))) {
			return false;
		}
	}
	if (image.IsBufferModified() || image.IsCpuDirty()) {
		ImageDesc refresh {.info = image.info, .view_info = {}, .type = UploadBinding(image)};
		InitializeImage(selected, refresh);
		if (image.info.samples == 1 && (image.IsBufferModified() || image.IsCpuDirty())) {
			EXIT("TextureCache: image clear retained guest ownership\n");
		}
	}
	command.EndRendering();
	image.Transit(vk::ImageLayout::eTransferDstOptimal, vk::AccessFlagBits2::eTransferWrite, {},
	              command.Handle());
	const vk::ImageSubresourceRange range {aspect, 0, VK_REMAINING_MIP_LEVELS, 0,
	                                       image.backing.layers};
	if (aspect == vk::ImageAspectFlagBits::eColor) {
		command.Handle().clearColorImage(image.backing.image, vk::ImageLayout::eTransferDstOptimal,
		                                 &color_clear, 1, &range);
	} else {
		const vk::ClearDepthStencilValue clear {depth_clear, stencil_clear};
		command.Handle().clearDepthStencilImage(
		    image.backing.image, vk::ImageLayout::eTransferDstOptimal, &clear, 1, &range);
	}
	CommitGpuWrite(image);
	RetainImage(command, selected);
	return true;
}

void TextureCache::InvalidateMemory(uint64_t address, uint64_t size) {
	if (!GuestRange {address, size}.Valid()) {
		EXIT("TextureCache: invalid memory-invalidation range\n");
	}
	CacheLock lock(*this, m_lock);
	InvalidateCpuAliases(address, size);
}

void TextureCache::DownloadDepth(Image& image, Buffer& destination, uint64_t destination_offset) {
	const auto&    info             = image.info;
	const auto     layers           = info.resources.layers;
	const auto     full_slice_size  = info.data.size / layers;
	const auto     transfer_bytes   = DepthAspectTransferBytes(info.pixel_format);
	const uint64_t texels_per_slice = static_cast<uint64_t>(info.pitch) * info.extent.height;
	EXIT_NOT_IMPLEMENTED(transfer_bytes == 0 || texels_per_slice > UINT32_MAX ||
	                     texels_per_slice > UINT64_MAX / transfer_bytes ||
	                     texels_per_slice > UINT64_MAX / info.bytes_per_block);
	const uint64_t transfer_slice = texels_per_slice * transfer_bytes;
	const uint64_t guest_slice    = texels_per_slice * info.bytes_per_block;
	EXIT_NOT_IMPLEMENTED(transfer_slice > UINT64_MAX / layers);
	const uint64_t transfer_size = transfer_slice * layers;
	EXIT_NOT_IMPLEMENTED(guest_slice > full_slice_size);
	std::vector<vk::BufferImageCopy> copies(layers);
	for (uint32_t layer = 0; layer < layers; layer++) {
		auto& copy             = copies[layer];
		copy.bufferOffset      = full_slice_size * layer;
		copy.bufferRowLength   = info.pitch;
		copy.bufferImageHeight = info.extent.height;
		copy.imageSubresource  = {vk::ImageAspectFlagBits::eDepth, 0, layer, 1};
		copy.imageExtent       = vk::Extent3D{info.extent.width, info.extent.height, 1};
	}
	if (transfer_bytes == info.bytes_per_block) {
		if (!info.IsTiled()) {
			for (auto& copy: copies) {
				copy.bufferOffset += destination_offset;
			}
			image.Download(copies, destination.Handle(), destination_offset, info.data.size);
			return;
		}
		TileBlockLayout block {};
		EXIT_NOT_IMPLEMENTED(
		    !TileGetBlockLayout(TileBlockFamily::Depth64KB, info.bytes_per_block, block));
		std::vector<GpuTileInfo> tiles;
		tiles.reserve(layers);
		for (uint32_t layer = 0; layer < layers; layer++) {
			const uint64_t offset = full_slice_size * layer;
			tiles.push_back({block.family, block.bytes_per_element, offset, full_slice_size, offset,
			                 full_slice_size, 0, info.extent.width, info.extent.height, 1,
			                 info.pitch});
			tiles.back().surface_z = layer;
		}
		m_tiler->TileImage(image, copies, destination.Handle(), destination_offset, info.data.size,
		                   info.data.size, tiles);
		return;
	}
	EXIT_NOT_IMPLEMENTED(info.bytes_per_block != sizeof(uint16_t) ||
	                     transfer_bytes != sizeof(uint32_t));
	for (uint32_t layer = 0; layer < layers; layer++) {
		copies[layer].bufferOffset = transfer_slice * layer;
	}
	auto host_linear = m_tiler->GetScratchBuffer(transfer_size);
	image.Download(copies, host_linear.buffer, 0, host_linear.size);
	const bool tiled        = info.IsTiled();
	auto       guest_linear = tiled ? m_tiler->GetScratchBuffer(info.data.size)
	                                : TileManager::Result {destination.Handle(), destination_offset,
	                                                       destination.Size() - destination_offset};
	m_tiler->ConvertD16(host_linear, guest_linear, TileManager::D16Direction::Demote,
	                    DepthAspectTransferFormat(info.pixel_format) == vk::Format::eD32Sfloat,
	                    {.width             = info.extent.width,
	                     .height            = info.extent.height,
	                     .layers            = layers,
	                     .source_row_stride = static_cast<uint64_t>(info.pitch) * sizeof(uint32_t),
	                     .target_row_stride = static_cast<uint64_t>(info.pitch) * sizeof(uint16_t),
	                     .source_slice_stride = transfer_slice,
	                     .target_slice_stride = full_slice_size});
	if (!tiled) {
		return;
	}
	TileBlockLayout block {};
	EXIT_NOT_IMPLEMENTED(
	    !TileGetBlockLayout(TileBlockFamily::Depth64KB, info.bytes_per_block, block));
	std::vector<GpuTileInfo> tiles;
	tiles.reserve(layers);
	for (uint32_t layer = 0; layer < layers; layer++) {
		const uint64_t offset = full_slice_size * layer;
		tiles.push_back({block.family, block.bytes_per_element, offset, full_slice_size, offset,
		                 full_slice_size, 0, info.extent.width, info.extent.height, 1, info.pitch});
		tiles.back().surface_z = layer;
	}
	m_tiler->Tile(guest_linear.buffer, guest_linear.offset, info.data.size, destination.Handle(),
	              destination_offset, info.data.size, tiles);
}

void TextureCache::DownloadImageData(Image& image, Buffer& destination, uint64_t destination_offset,
                                     uint64_t destination_size, DownloadPlan plan) {
	if (!plan.valid) {
		EXIT("TextureCache: invalid image download plan\n");
	}
	if (plan.depth) {
		if (destination_size != image.info.data.size) {
			EXIT("TextureCache: partial depth image download is unsupported\n");
		}
		DownloadDepth(image, destination, destination_offset);
		return;
	}

	auto&      color     = plan.color;
	const auto transform = color.swap_bgra16 ? TileManager::ColorTransform::SwapBgra16
	                                         : TileManager::ColorTransform::None;
	if (!color.tiled) {
		if (transform == TileManager::ColorTransform::SwapBgra16) {
			auto linear = m_tiler->GetScratchBuffer(destination_size);
			image.Download(color.regions, linear.buffer, 0, linear.size);
			m_tiler->SwapBgra16(linear,
			                    {destination.Handle(), destination_offset, destination_size});
			return;
		}
		for (auto& copy: color.regions) {
			copy.bufferOffset += destination_offset;
		}
		image.Download(color.regions, destination.Handle(), destination_offset, destination_size);
		return;
	}

	m_tiler->TileImage(image, color.regions, destination.Handle(), destination_offset,
	                   destination_size, destination_size, color.tiles, transform);
}

bool BufferCache::SynchronizeBufferFromImage(Buffer& buffer, uint64_t vaddr, uint64_t size) {
	CacheLock            lock(m_texture_cache, m_texture_cache.m_lock);
	std::vector<ImageId> matches;
	for (const auto id: m_texture_cache.FindImagesInRegion(vaddr, size, false)) {
		auto owner = m_texture_cache.ResolveOwner(id);
		if (owner == nullptr || owner->info.data.address != vaddr) {
			continue;
		}
		if (owner->depth_id) {
			owner = m_texture_cache.ResolveOwner(owner->depth_id);
		}
		if (owner != nullptr && owner->SafeToDownload()) {
			matches.push_back(id);
		}
	}

	ImageId selected {};
	if (matches.size() == 1) {
		selected = matches.front();
	} else {
		for (const auto id: matches) {
			const auto& image = m_texture_cache.ResolveImage(id);
			if (image.info.data.size == size) {
				selected = id;
				break;
			}
		}
	}
	if (!selected) {
		return false;
	}
	if (const auto owner = m_texture_cache.ResolveOwner(selected);
	    owner != nullptr && owner->depth_id) {
		selected = owner->depth_id;
	}

	auto& image = m_texture_cache.ResolveImage(selected);
	if (!buffer.IsInBounds(image.info.data.address, 1)) {
		return false;
	}
	const auto buf_offset = buffer.Offset(image.info.data.address);
	const auto available  = buffer.Size() - buf_offset;
	uint32_t   levels     = 0;
	uint64_t   copy_size  = 0;
	if (image.info.IsVolume()) {
		// Volume mips contain strided block slices, so a mip's linear span cannot prove that
		// every retained slice fits. Keep volume synchronization whole-image only.
		if (!buffer.IsInBounds(image.info.data.address, image.info.data.size)) {
			return false;
		}
		levels    = image.info.resources.levels;
		copy_size = image.info.data.size;
	} else {
		for (; levels < image.info.resources.levels; ++levels) {
			const auto& mip = image.info.mip_layout[levels];
			if (mip.size == 0 || mip.offset > available || mip.size > available - mip.offset) {
				break;
			}
			copy_size = std::max(copy_size, mip.offset + mip.size);
		}
	}
	if (copy_size == 0) {
		return false;
	}
	auto plan = m_texture_cache.BuildDownload(image);
	if (!plan.valid) {
		return false;
	}
	if (plan.depth && copy_size != image.info.data.size) {
		return false;
	}
	if (!plan.depth && levels < image.info.resources.levels) {
		auto& color = plan.color;
		std::erase_if(color.regions, [levels](const vk::BufferImageCopy& region) {
			return region.imageSubresource.mipLevel >= levels;
		});
		if (color.regions.empty()) {
			return false;
		}
		if (color.tiled) {
			const auto binding = m_texture_cache.UploadBinding(image);
			const auto format =
			    binding == TextureCache::BindingType::RenderTarget
			        ? ImageOps::RenderTargetTransferFormat(image.info.bytes_per_block)
			        : image.info.guest_format;
			color.tiles.clear();
			if (!TextureBuildGpuTileInfos(copy_size, color.regions, color.layout, format,
			                              image.info.TransferLayers(), levels, color.tiles)) {
				return false;
			}
		}
	}
	m_texture_cache.DownloadImageData(image, buffer, buf_offset, copy_size, std::move(plan));
	m_texture_cache.RetainImage(m_scheduler.Current(), selected);
	return true;
}

std::pair<uint8_t*, uint64_t> TextureCache::MapDownload(uint64_t size, uint64_t alignment) {
	if (size == 0) {
		EXIT("TextureCache: cannot map an empty image download\n");
	}
	auto& download = m_buffer_cache.GetUtilityBuffer(MemoryUsage::Download);
	auto  mapping  = download.Map(size, std::max<uint64_t>(alignment, 4));
	if (mapping.first == nullptr) {
		EXIT("TextureCache: failed to map reusable download buffer\n");
	}
	download.Commit();
	return mapping;
}

void TextureCache::QueueDownload(GuestRange range, StreamBuffer& download, uint8_t* mapped,
                                 uint64_t offset) {
	vk::BufferMemoryBarrier barrier {};
	barrier.sType         = vk::StructureType::eBufferMemoryBarrier;
	barrier.srcAccessMask = vk::AccessFlagBits::eMemoryWrite | vk::AccessFlagBits::eTransferWrite |
	                        vk::AccessFlagBits::eShaderWrite;
	barrier.dstAccessMask = vk::AccessFlagBits::eHostRead;
	barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
	barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
	barrier.buffer              = download.Handle();
	barrier.offset              = offset;
	barrier.size                = range.size;
	m_scheduler.EndRendering();
	m_scheduler.Current().Handle().pipelineBarrier(vk::PipelineStageFlagBits::eAllCommands,
	                                               vk::PipelineStageFlagBits::eHost, {}, 0, nullptr,
	                                               1, &barrier, 0, nullptr);
	m_scheduler.DeferPriorityOperation([&download, range, mapped, offset] {
		download.Invalidate(offset, range.size);
		LibKernel::Memory::WriteBacking(range.address, mapped, range.size);
	});
}

bool TextureCache::TryDownloadImage(ImageId id) {
	auto& image = ResolveImage(id);
	if (image.depth_id) {
		return false;
	}
	auto plan = BuildDownload(image);
	if (!plan.valid || !SafeToDownload(image)) {
		return false;
	}
	const auto range      = image.info.data;
	auto [mapped, offset] = MapDownload(range.size, image.info.bytes_per_block);
	auto& download        = m_buffer_cache.GetUtilityBuffer(MemoryUsage::Download);
	if (!LibKernel::Memory::TryReadBacking(range.address, mapped, range.size)) {
		return false;
	}
	download.Flush(offset, range.size);

	DownloadImageData(image, download, offset, range.size, std::move(plan));

	QueueDownload(range, download, mapped, offset);
	return true;
}

void TextureCache::DownloadImage(ImageId id) {
	if (!TryDownloadImage(id)) {
		EXIT("TextureCache: unsupported image readback\n");
	}
	m_scheduler.FinishCurrent();
	m_scheduler.DrainPriorityOperations();
}

TextureCache::DebugImageByteStats TextureCache::DebugDownloadByteStats(ImageId id) {
	DebugImageByteStats stats {};
	auto measure = [&stats](const uint8_t* begin, uint64_t size) {
		stats.valid = true;
		stats.size  = size;
		stats.hash  = 1469598103934665603ull;
		for (uint64_t i = 0; i < size; i++) {
			stats.nonzero_bytes += begin[i] != 0 ? 1u : 0u;
			stats.hash ^= begin[i];
			stats.hash *= 1099511628211ull;
		}
	};
	auto&      image = GetImage(id);
	const auto range = image.info.data;
	if (!range.Valid()) {
		return stats;
	}
	if (image.backing.image == nullptr || image.backing.samples != 1 ||
	    image.backing.format == vk::Format::eUndefined) {
		return stats;
	}
	const auto block_extent = vk::blockExtent(image.backing.format);
	const auto block_bytes  = vk::blockSize(image.backing.format);
	if (block_extent[0] == 0 || block_extent[1] == 0 || block_bytes == 0) {
		return stats;
	}
	const uint64_t blocks_x =
	    (image.backing.extent.width + block_extent[0] - 1u) / block_extent[0];
	const uint64_t blocks_y =
	    (image.backing.extent.height + block_extent[1] - 1u) / block_extent[1];
	if (blocks_x == 0 || blocks_y > UINT64_MAX / blocks_x ||
	    blocks_x * blocks_y > UINT64_MAX / block_bytes) {
		return stats;
	}
	const uint64_t size   = blocks_x * blocks_y * block_bytes;
	auto [mapped, offset] = MapDownload(size, std::max<uint64_t>(block_bytes, 4u));
	auto& download        = m_buffer_cache.GetUtilityBuffer(MemoryUsage::Download);
	vk::BufferImageCopy copy {};
	copy.bufferOffset     = offset;
	copy.imageSubresource = {vk::ImageAspectFlagBits::eColor, 0, 0, 1};
	copy.imageExtent      = image.backing.extent;
	image.Download(std::span<const vk::BufferImageCopy>(&copy, 1), download.Handle(), offset, size);
	m_scheduler.FinishCurrent();
	download.Invalidate(offset, size);
	measure(mapped, size);
	if (image.backing.format == vk::Format::eR16G16B16A16Sfloat &&
	    size == static_cast<uint64_t>(image.backing.extent.width) *
	                image.backing.extent.height * 8u) {
		for (uint32_t y = 0; y < image.backing.extent.height; y++) {
			for (uint32_t x = 0; x < image.backing.extent.width; x++) {
				uint16_t pixel[4] {};
				std::memcpy(pixel,
				            mapped + (static_cast<uint64_t>(y) * image.backing.extent.width + x) * 8u,
				            sizeof(pixel));
				const bool rgb = (pixel[0] & 0x7fffu) != 0 || (pixel[1] & 0x7fffu) != 0 ||
				                 (pixel[2] & 0x7fffu) != 0;
				if (rgb) {
					stats.rgb_nonzero_pixels++;
					stats.rgb_min_x = std::min(stats.rgb_min_x, x);
					stats.rgb_min_y = std::min(stats.rgb_min_y, y);
					stats.rgb_max_x = std::max(stats.rgb_max_x, x);
					stats.rgb_max_y = std::max(stats.rgb_max_y, y);
				}
				stats.alpha_nonzero_pixels += (pixel[3] & 0x7fffu) != 0 ? 1u : 0u;
			}
		}
	}
	return stats;
}

// Debug-only: download an image and dump it as a binary PPM, converting the common
// scan-out formats to 8-bit RGB. Returns false when the image cannot be read back.
bool TextureCache::DebugDumpImagePpm(ImageId id, const char* path) {
	auto& image = GetImage(id);
	if (image.backing.image == nullptr || image.backing.samples != 1 ||
	    image.backing.format == vk::Format::eUndefined) {
		return false;
	}
	const auto format = image.backing.format;
	uint32_t   bpp    = 0;
	switch (format) {
		case vk::Format::eR8G8B8A8Unorm:
		case vk::Format::eR8G8B8A8Srgb:
		case vk::Format::eB8G8R8A8Unorm:
		case vk::Format::eB8G8R8A8Srgb:
		case vk::Format::eA2R10G10B10UnormPack32:
		case vk::Format::eA2B10G10R10UnormPack32: bpp = 4; break;
		case vk::Format::eR16G16B16A16Sfloat: bpp = 8; break;
		default: return false;
	}
	const uint32_t width  = image.backing.extent.width;
	const uint32_t height = image.backing.extent.height;
	const uint64_t size   = static_cast<uint64_t>(width) * height * bpp;
	if (size == 0) {
		return false;
	}
	auto [mapped, offset] = MapDownload(size, 8);
	auto& download        = m_buffer_cache.GetUtilityBuffer(MemoryUsage::Download);
	vk::BufferImageCopy copy {};
	copy.bufferOffset     = offset;
	copy.imageSubresource = {vk::ImageAspectFlagBits::eColor, 0, 0, 1};
	copy.imageExtent      = image.backing.extent;
	image.Download(std::span<const vk::BufferImageCopy>(&copy, 1), download.Handle(), offset, size);
	m_scheduler.FinishCurrent();
	download.Invalidate(offset, size);

	std::string temp_path = std::string(path) + ".tmp";
	FILE*       file      = fopen(temp_path.c_str(), "wb");
	if (file == nullptr) {
		return false;
	}
	fprintf(file, "P6\n%u %u\n255\n", width, height);
	const bool blue_first = format == vk::Format::eB8G8R8A8Unorm ||
	                        format == vk::Format::eB8G8R8A8Srgb ||
	                        format == vk::Format::eA2B10G10R10UnormPack32;
	const bool packed_10 = format == vk::Format::eA2R10G10B10UnormPack32 ||
	                       format == vk::Format::eA2B10G10R10UnormPack32;
	uint32_t max_r = 0, max_g = 0, max_b = 0, max_a = 0;
	uint64_t nonzero_rgb = 0;
	auto     f16_to_u8   = [](uint16_t h) -> uint8_t {
        const uint32_t exp  = (h >> 10u) & 0x1fu;
        const uint32_t frac = h & 0x3ffu;
        if ((h & 0x8000u) != 0 || exp == 0) {
            return 0;
        }
        float value = 0.0f;
        if (exp == 31) {
            value = 1.0f;
        } else {
            value = std::ldexp(1.0f + static_cast<float>(frac) / 1024.0f,
                               static_cast<int>(exp) - 15);
        }
        return static_cast<uint8_t>(std::clamp(value, 0.0f, 1.0f) * 255.0f + 0.5f);
	};
	std::vector<uint8_t> row(static_cast<size_t>(width) * 3u);
	for (uint32_t y = 0; y < height; y++) {
		const uint8_t* src = mapped + static_cast<uint64_t>(y) * width * bpp;
		for (uint32_t x = 0; x < width; x++) {
			uint8_t rgb[3] {};
			uint8_t alpha = 0;
			if (packed_10) {
				uint32_t value = 0;
				std::memcpy(&value, src + static_cast<uint64_t>(x) * 4u, sizeof(value));
				const auto low  = static_cast<uint8_t>((value & 0x3ffu) >> 2u);
				const auto mid  = static_cast<uint8_t>(((value >> 10u) & 0x3ffu) >> 2u);
				const auto high = static_cast<uint8_t>(((value >> 20u) & 0x3ffu) >> 2u);
				rgb[0]          = blue_first ? low : high;
				rgb[1]          = mid;
				rgb[2]          = blue_first ? high : low;
				alpha           = static_cast<uint8_t>(value >> 30u);
			} else if (bpp == 8) {
				uint16_t pixel[4] {};
				std::memcpy(pixel, src + static_cast<uint64_t>(x) * 8u, sizeof(pixel));
				rgb[0] = f16_to_u8(pixel[0]);
				rgb[1] = f16_to_u8(pixel[1]);
				rgb[2] = f16_to_u8(pixel[2]);
				alpha  = f16_to_u8(pixel[3]);
			} else {
				const uint8_t* pixel = src + static_cast<uint64_t>(x) * 4u;
				rgb[0]               = blue_first ? pixel[2] : pixel[0];
				rgb[1]               = pixel[1];
				rgb[2]               = blue_first ? pixel[0] : pixel[2];
				alpha                = pixel[3];
			}
			max_r = std::max<uint32_t>(max_r, rgb[0]);
			max_g = std::max<uint32_t>(max_g, rgb[1]);
			max_b = std::max<uint32_t>(max_b, rgb[2]);
			max_a = std::max<uint32_t>(max_a, alpha);
			nonzero_rgb += (rgb[0] | rgb[1] | rgb[2]) != 0 ? 1 : 0;
			std::memcpy(&row[static_cast<size_t>(x) * 3u], rgb, 3);
		}
		fwrite(row.data(), 1, row.size(), file);
	}
	fclose(file);
	std::remove(path);
	if (std::rename(temp_path.c_str(), path) != 0) {
		return false;
	}
	LOGF("scan-out dump: %s %ux%u fmt=%d addr=0x%010" PRIx64
	     " chmax=(%u,%u,%u) amax=%u nonzero_rgb_px=%" PRIu64 "\n",
	     path, width, height, static_cast<int>(format), image.info.data.address, max_r, max_g,
	     max_b, max_a, nonzero_rgb);
	return true;
}

void TextureCache::DebugTraceImageWriter(uint64_t address, ImageId id, bool compute, bool skipped,
                                         uint64_t shader, uint32_t frame) {
	static const uint64_t target_address = [] {
		const char* value = std::getenv("PROSPERISMO_TRACE_IMAGE_WRITERS");
		return value != nullptr ? std::strtoull(value, nullptr, 16) : 0ull;
	}();
	if (target_address == 0 || address != target_address || !id) {
		return;
	}
	static const uint32_t start_frame = [] {
		const char* value = std::getenv("PROSPERISMO_TRACE_IMAGE_WRITERS_START_FRAME");
		return value != nullptr ? static_cast<uint32_t>(std::strtoul(value, nullptr, 10)) : 0u;
	}();
	if (frame < start_frame) {
		return;
	}

	static const bool sample_all = [] {
		const char* value = std::getenv("PROSPERISMO_TRACE_IMAGE_WRITERS_ALL");
		return value != nullptr && value[0] != '\0' && value[0] != '0';
	}();
	// The default remains one sampled writer every 120 frames.  The opt-in all-writers
	// mode is intentionally evaluated at process start so a trace cannot change its
	// sampling policy halfway through a frame sequence.
	const bool sample_frame =
	    sample_all || frame == start_frame || (frame - start_frame) % 120u == 0;

	if (m_debug_image_writer_previous.valid) {
		const auto stats = DebugDownloadByteStats(m_debug_image_writer_previous.id);
		LOGF("ImageWriterOutput: addr=0x%010" PRIx64 " ordinal=%" PRIu64
		     " frame=%u kind=%s skipped=%s shader=0x%016" PRIx64
		     " size=%" PRIu64 " nonzero_bytes=%" PRIu64 " hash=0x%016" PRIx64
		     " valid=%s rgb_pixels=%" PRIu64 " alpha_pixels=%" PRIu64
		     " rgb_bbox=%u,%u-%u,%u next_frame=%u next_kind=%s next_skipped=%s "
		     "next_shader=0x%016" PRIx64 "\n",
		     target_address, m_debug_image_writer_previous.ordinal,
		     m_debug_image_writer_previous.frame,
		     m_debug_image_writer_previous.compute ? "compute" : "draw",
		     m_debug_image_writer_previous.skipped ? "true" : "false",
		     m_debug_image_writer_previous.shader, stats.size, stats.nonzero_bytes, stats.hash,
		     stats.valid ? "true" : "false", stats.rgb_nonzero_pixels,
		     stats.alpha_nonzero_pixels, stats.rgb_min_x, stats.rgb_min_y, stats.rgb_max_x,
		     stats.rgb_max_y, frame, compute ? "compute" : "draw",
		     skipped ? "true" : "false", shader);
		m_debug_image_writer_previous.valid = false;
	}
	if (!sample_frame) {
		return;
	}
	m_debug_image_writer_previous = DebugImageWriterRecord {
	    .id      = id,
	    .valid   = true,
	    .compute = compute,
	    .skipped = skipped,
	    .shader  = shader,
	    .frame   = frame,
	    .ordinal = m_debug_image_writer_next_ordinal++,
	};
}

void TextureCache::DebugFlushImageWriterTrace() {
	static const uint64_t target_address = [] {
		const char* value = std::getenv("PROSPERISMO_TRACE_IMAGE_WRITERS");
		return value != nullptr ? std::strtoull(value, nullptr, 16) : 0ull;
	}();
	if (target_address == 0 || !m_debug_image_writer_previous.valid) {
		return;
	}
	const auto stats = DebugDownloadByteStats(m_debug_image_writer_previous.id);
	LOGF("ImageWriterOutput: addr=0x%010" PRIx64 " ordinal=%" PRIu64
	     " frame=%u kind=%s skipped=%s shader=0x%016" PRIx64
	     " size=%" PRIu64 " nonzero_bytes=%" PRIu64 " hash=0x%016" PRIx64
	     " valid=%s rgb_pixels=%" PRIu64 " alpha_pixels=%" PRIu64
	     " rgb_bbox=%u,%u-%u,%u frame_end=true\n",
	     target_address, m_debug_image_writer_previous.ordinal,
	     m_debug_image_writer_previous.frame,
	     m_debug_image_writer_previous.compute ? "compute" : "draw",
	     m_debug_image_writer_previous.skipped ? "true" : "false",
	     m_debug_image_writer_previous.shader, stats.size, stats.nonzero_bytes, stats.hash,
	     stats.valid ? "true" : "false", stats.rgb_nonzero_pixels,
	     stats.alpha_nonzero_pixels, stats.rgb_min_x, stats.rgb_min_y, stats.rgb_max_x,
	     stats.rgb_max_y);
	m_debug_image_writer_previous.valid = false;
}

std::vector<uint32_t> TextureCache::DebugDownloadBufferWords(vk::Buffer source,
                                                             uint64_t source_offset,
                                                             uint64_t size) {
	std::vector<uint32_t> words;
	if (size == 0 || (size % 4u) != 0 || source == nullptr) {
		return words;
	}
	auto [mapped, offset] = MapDownload(size, 4);
	auto& download        = m_buffer_cache.GetUtilityBuffer(MemoryUsage::Download);
	m_scheduler.EndRendering();
	auto command = m_scheduler.Current().Handle();
	VulkanMemoryBarrier before {};
	before.sType         = vk::StructureType::eMemoryBarrier;
	before.srcAccessMask = vk::AccessFlagBits::eMemoryWrite | vk::AccessFlagBits::eShaderWrite;
	before.dstAccessMask = vk::AccessFlagBits::eTransferRead;
	command.pipelineBarrier(vk::PipelineStageFlagBits::eAllCommands,
	                        vk::PipelineStageFlagBits::eTransfer, {}, 1, &before, 0, nullptr, 0,
	                        nullptr);
	const vk::BufferCopy copy {source_offset, offset, size};
	command.copyBuffer(source, download.Handle(), 1, &copy);
	vk::BufferMemoryBarrier after {};
	after.sType               = vk::StructureType::eBufferMemoryBarrier;
	after.srcAccessMask       = vk::AccessFlagBits::eTransferWrite;
	after.dstAccessMask       = vk::AccessFlagBits::eHostRead;
	after.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
	after.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
	after.buffer              = download.Handle();
	after.offset              = offset;
	after.size                = size;
	command.pipelineBarrier(vk::PipelineStageFlagBits::eTransfer,
	                        vk::PipelineStageFlagBits::eHost, {}, 0, nullptr, 1, &after, 0,
	                        nullptr);
	m_scheduler.FinishCurrent();
	download.Invalidate(offset, size);
	words.resize(size / 4u);
	std::memcpy(words.data(), mapped, size);
	return words;
}

bool TextureCache::InvalidateMemoryFromGPU(uint64_t address, uint64_t size,
                                           bool formatted_buffer_write) {
	if (!GuestRange {address, size}.Valid()) {
		return false;
	}
	CacheLock lock(*this, m_lock);
	bool      found = false;
	for (const auto id: FindImagesInRegion(address, size, true)) {
		auto owner = ResolveOwner(id);
		if (owner == nullptr || owner->depth_id || !owner->Overlaps(address, size)) {
			continue;
		}
		if (owner->IsGpuModified()) {
			if (!formatted_buffer_write) {
				EXIT("TextureCache: buffer write aliases GPU-modified image\n");
			}
			ClearGpuModified(id);
		}
		owner->MarkBufferModified();
		found = true;
	}
	return found;
}

TextureCache::RegionInfo TextureCache::QueryRegion(uint64_t address, uint64_t size) {
	RegionInfo result {};
	if (!GuestRange {address, size}.Valid()) {
		return result;
	}
	CacheLock lock(*this, m_lock);
	for (const auto id: FindImagesInRegion(address, size, true)) {
		auto owner = ResolveOwner(id);
		if (owner == nullptr || owner->depth_id || !owner->Overlaps(address, size, true)) {
			continue;
		}
		result.image_pages = true;
		result.image_bytes |= owner->Overlaps(address, size);
		result.gpu_image_bytes |= owner->GpuOverlaps(address, size);
	}
	return result;
}

void TextureCache::InvalidateCpuAliases(uint64_t address, uint64_t size) {
	const auto page_begin = address & ~(TRACKER_PAGE_SIZE - 1);
	const auto page_end   = (address + size + TRACKER_PAGE_SIZE - 1) & ~(TRACKER_PAGE_SIZE - 1);
	for (const auto id: FindImagesInRegion(address, size, true)) {
		auto owner = ResolveOwner(id);
		if (owner == nullptr || owner->depth_id) {
			continue;
		}
		if (owner->Overlaps(address, size)) {
			owner->InvalidateCpuWrite(address, size);
			UntrackImage(id);
			continue;
		}
		const auto image_begin = owner->info.data.address;
		const auto image_end   = owner->info.data.End();
		if (page_end < image_end) {
			UntrackImageHead(id);
		} else if (image_begin < page_begin) {
			UntrackImageTail(id);
		} else {
			owner->MarkMaybeCpuDirty();
			if (owner->NeedsMaybeCpuHash()) {
				owner->SetMaybeCpuHash(owner->HashGuestEdges());
			}
			UntrackImage(id);
		}
	}
}

void TextureCache::ClearGpuModified(ImageId id) {
	auto owner = ResolveOwner(id);
	if (owner == nullptr || !owner->IsGpuModified()) {
		return;
	}
	owner->ClearGpuModified();
}

bool TextureCache::IsMeta(uint64_t address) {
	CacheLock lock(*this, m_lock);
	return m_surface_metas.contains(address);
}

bool TextureCache::IsMetaCleared(uint64_t address, uint32_t slice) {
	CacheLock  lock(*this, m_lock);
	const auto found = m_surface_metas.find(address);
	if (found == m_surface_metas.end()) {
		return false;
	}
	return (found->second.clear_mask & (1u << slice)) != 0;
}

bool TextureCache::ClearMeta(uint64_t address) {
	std::lock_guard transaction(m_resource_mutex);
	CacheLock       lock(*this, m_lock);
	const auto      found = m_surface_metas.find(address);
	if (found == m_surface_metas.end()) {
		return false;
	}
	found->second.clear_mask = UINT32_MAX;
	return true;
}

bool TextureCache::TouchMeta(uint64_t address, uint32_t slice, bool is_clear) {
	CacheLock  lock(*this, m_lock);
	const auto found = m_surface_metas.find(address);
	if (found == m_surface_metas.end()) {
		return false;
	}
	if (is_clear) {
		found->second.clear_mask |= 1u << slice;
	} else {
		found->second.clear_mask &= ~(1u << slice);
	}
	return true;
}

void TextureCache::UnmapMemory(uint64_t address, uint64_t size) {
	if (!GuestRange {address, size}.Valid()) {
		EXIT("TextureCache: invalid unmap range\n");
	}
	std::lock_guard transaction(m_resource_mutex);
	CacheLock       lock(*this, m_lock);
	auto            images = FindImagesInRegion(address, size, false);
	if (!images.empty()) {
		m_scheduler.Finish();
	}
	for (const auto id: images) {
		auto owner = ResolveOwner(id);
		if (owner == nullptr) {
			continue;
		}
		if (owner->IsGpuModified()) {
			ClearGpuModified(id);
		}
		DeleteImage(id);
	}
}

void TextureCache::RunGarbageCollector() {
	std::lock_guard transaction(m_resource_mutex);
	CacheLock       lock(*this, m_lock);
	const uint64_t  tick = m_gc_tick++;
	if (m_graphics.CanReportMemoryUsage()) {
		m_total_used_memory = m_graphics.GetDeviceMemoryUsage();
	}
	if (m_total_used_memory < m_trigger_gc_memory) {
		return;
	}
	const auto collect = [&](bool allow_aggressive) {
		bool           pressured  = m_total_used_memory >= m_pressure_gc_memory;
		bool           aggressive = allow_aggressive && m_total_used_memory >= m_critical_gc_memory;
		const uint64_t age       = std::min<uint64_t>(aggressive ? 160 : pressured ? 80 : 16, tick);
		size_t         deletions = aggressive ? 40 : pressured ? 20 : 10;
		std::vector<ImageId> candidates;
		candidates.reserve(deletions);
		// Deleting depth recursively deletes its stencil association, so finish LRU traversal
		// first.
		m_lru_cache.ForEachItemBelow(tick - age, [&](ImageId id) {
			candidates.push_back(id);
			return candidates.size() == deletions;
		});
		for (const auto id: candidates) {
			if (deletions == 0) {
				break;
			}
			--deletions;
			auto owner = ResolveOwner(id);
			if (owner == nullptr || !owner->registered || owner->depth_id) {
				continue;
			}
			if (owner->IsGpuModified()) {
				const bool safe = SafeToDownload(*owner);
				if (safe && owner->info.IsTiled()) {
					continue;
				}
				if (safe && !pressured) {
					continue;
				}
				if (safe && !TryDownloadImage(id)) {
					continue;
				}
				ClearGpuModified(id);
			}
			DeleteImage(id);
			if (m_total_used_memory < m_critical_gc_memory && aggressive) {
				deletions >>= 2;
				aggressive = false;
			}
			if (m_total_used_memory < m_pressure_gc_memory && pressured) {
				deletions >>= 1;
				pressured = false;
			}
		}
	};
	collect(false);
	if (m_total_used_memory >= m_critical_gc_memory) {
		collect(true);
	}
}

void TextureCache::ProcessDownloadImages() {
	std::lock_guard transaction(m_resource_mutex);
	CacheLock       lock(*this, m_lock);
	for (const auto id: m_download_images) {
		const auto owner = ResolveOwner(id);
		if (owner != nullptr && owner->registered && owner->IsGpuModified()) {
			(void)TryDownloadImage(id);
		}
	}
	m_download_images.clear();
}

} // namespace Libs::Graphics
