#ifndef EMULATOR_SRC_GRAPHICS_HOST_GPU_RENDERER_TEXTURECACHE_H_
#define EMULATOR_SRC_GRAPHICS_HOST_GPU_RENDERER_TEXTURECACHE_H_

#include "common/abi.h"
#include "common/common.h"
#include "common/lruCache.h"
#include "graphics/host_gpu/pageManager.h"
#include "graphics/host_gpu/regionManager.h"
#include "graphics/host_gpu/renderer/cache/multiLevelPageTable.h"
#include "graphics/host_gpu/renderer/image/blitHelper.h"
#include "graphics/host_gpu/renderer/image/image.h"

#include <compare>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <span>
#include <utility>
#include <vector>

namespace Libs::Graphics {

struct GraphicContext;
class Buffer;
class BufferCache;
class CommandBuffer;
class CommandScheduler;
class ResourceMutex;
class RenderExecutor;
class StreamBuffer;
class TileManager;
struct TextureCacheTestAccess;

class TextureCache {
public:
	enum class BindingType : uint8_t { Texture, Storage, RenderTarget, DepthTarget, VideoOut };

	struct ImageDesc {
		ImageInfo     info;
		ImageViewInfo view_info;
		BindingType   type = BindingType::Texture;
	};

	struct RegionInfo {
		bool image_pages     = false;
		bool image_bytes     = false;
		bool gpu_image_bytes = false;
	};

	TextureCache(GraphicContext& graphics, CommandScheduler& scheduler, PageManager& page_manager,
	             BufferCache& buffer_cache, ResourceMutex& resource_mutex);
	~TextureCache();
	KYTY_CLASS_NO_COPY(TextureCache);

	[[nodiscard]] ImageId       FindImage(ImageDesc& desc, bool exact_format = false);
	[[nodiscard]] bool ApplyColorMetadataOperation(ImageId id, uint8_t mode,
	                                               const ImageSubresourceRange& range);
	[[nodiscard]] ImageId       FindImageFromRange(uint64_t address, uint64_t size,
	                                               bool ensure_valid = true);
	[[nodiscard]] vk::ImageView FindTexture(ImageId id, const ImageDesc& desc);
	[[nodiscard]] vk::ImageView FindRenderTarget(ImageId id, const ImageDesc& desc);
	[[nodiscard]] vk::ImageView FindDepthTarget(ImageId id, const ImageDesc& desc);
	[[nodiscard]] Image&        GetImage(ImageId id);
	[[nodiscard]] const Image&  GetImage(ImageId id) const;
	void                        MarkGpuWritten(ImageId id);
	struct DebugImageByteStats {
		bool     valid         = false;
		uint64_t size          = 0;
		uint64_t nonzero_bytes = 0;
		uint64_t hash          = 0;
		uint64_t rgb_nonzero_pixels   = 0;
		uint64_t alpha_nonzero_pixels = 0;
		uint32_t rgb_min_x            = UINT32_MAX;
		uint32_t rgb_min_y            = UINT32_MAX;
		uint32_t rgb_max_x            = 0;
		uint32_t rgb_max_y            = 0;
	};
	[[nodiscard]] DebugImageByteStats DebugDownloadByteStats(ImageId id);
	[[nodiscard]] std::vector<uint32_t> DebugDownloadBufferWords(vk::Buffer source,
	                                                             uint64_t source_offset,
	                                                             uint64_t size);
	bool                                DebugDumpImagePpm(ImageId id, const char* path);
	void DebugTraceImageWriter(uint64_t address, ImageId id, bool compute, bool skipped,
	                           uint64_t shader, uint32_t frame);
	void DebugFlushImageWriterTrace();

	[[nodiscard]] bool ClearImageFromBuffer(CommandBuffer& command, uint64_t address, uint64_t size,
	                                        uint32_t packed_clear);
	void               InvalidateMemory(uint64_t address, uint64_t size);
	[[nodiscard]] bool InvalidateMemoryFromGPU(uint64_t address, uint64_t size,
	                                           bool formatted_buffer_write = false);
	[[nodiscard]] RegionInfo QueryRegion(uint64_t address, uint64_t size);

	[[nodiscard]] bool IsMeta(uint64_t address);
	[[nodiscard]] bool IsMetaCleared(uint64_t address, uint32_t slice);
	[[nodiscard]] bool ClearMeta(uint64_t address);
	[[nodiscard]] bool TouchMeta(uint64_t address, uint32_t slice, bool is_clear);

	void UnmapMemory(uint64_t address, uint64_t size);
	void ProcessDownloadImages();
	void RunGarbageCollector();

private:
	enum class TransferDirection { Upload, Download };
	struct ColorTransferPlan;
	struct DownloadPlan;
	struct DebugImageWriterRecord {
		ImageId  id;
		bool     valid   = false;
		bool     compute = false;
		bool     skipped = false;
		uint64_t shader  = 0;
		uint32_t frame   = 0;
		uint64_t ordinal = 0;
	};

	struct Slot {
		std::shared_ptr<Image> image;
		uint32_t               generation = 1;
	};

	struct MetaDataInfo {
		uint32_t clear_mask = 0;
	};

	struct OverlapResult {
		ImageId image;
		int32_t mip   = -1;
		int32_t layer = -1;
	};

	using ImageOwnerIndex = MultiRangePageOwnerIndex<ImageId>;

	[[nodiscard]] Image&                 ResolveImage(ImageId id);
	[[nodiscard]] const Image&           ResolveImage(ImageId id) const;
	[[nodiscard]] std::shared_ptr<Image> ResolveOwner(ImageId id) const;
	[[nodiscard]] ImageId                InsertImage(const ImageInfo& info);
	[[nodiscard]] ImageId                GetNullImage(const ImageDesc& desc);
	void                                 RegisterImage(ImageId id);
	void                                 UnregisterImage(ImageId id);
	void                                 DeleteImage(ImageId id);
	void DeleteImages(std::span<const ImageId> ids, std::optional<ImageId> native_source = {});
	void RetainImage(CommandBuffer& command, ImageId id);
	void TouchImage(Image& image);
	void TrackImage(ImageId id);
	void TrackImageHead(ImageId id);
	void TrackImageTail(ImageId id);
	void UntrackImage(ImageId id);
	void UntrackImageHead(ImageId id);
	void UntrackImageTail(ImageId id);
	void TrackImageDownload(ImageId id);
	void TrackImageDownloadLocked(ImageId id, Image& image);
	[[nodiscard]] static bool SameBacking(const ImageInfo& cached, const ImageInfo& requested,
	                                      bool exact_format);
	[[nodiscard]] static BindingType UploadBinding(const Image& image);
	[[nodiscard]] bool               SafeToDownload(const Image& image);

	[[nodiscard]] std::vector<ImageId> FindImagesInRegion(uint64_t address, uint64_t size,
	                                                      bool page_overlap) const;
	[[nodiscard]] OverlapResult ResolveOverlap(const ImageInfo& requested, BindingType binding,
	                                           ImageId cached, ImageId merged);
	[[nodiscard]] ImageId       ResolveDepthOverlap(const ImageInfo& requested, BindingType binding,
	                                                ImageId cached);
	[[nodiscard]] ImageId       ExpandImage(const ImageInfo& info, ImageId source);
	void                        RefreshImage(ImageId id, const ImageDesc& desc);
	void                        InitializeImage(ImageId id, const ImageDesc& desc);
	[[nodiscard]] ColorTransferPlan BuildColorTransfer(const Image& image, BindingType binding,
	                                                   TransferDirection direction) const;
	[[nodiscard]] DownloadPlan      BuildDownload(const Image& image) const;
	void UploadImage(Image& image, const ImageDesc& desc, Buffer& source, uint64_t source_offset);
	void DownloadImageData(Image& image, Buffer& destination, uint64_t destination_offset,
	                       uint64_t destination_size, DownloadPlan plan);
	void DownloadDepth(Image& image, Buffer& destination, uint64_t destination_offset);
	void CommitGpuWrite(Image& image);
	void PrepareImageCopy(Image& image);
	void RefreshCopySource(ImageId id);
	[[nodiscard]] bool CopyD16(Image& destination, Image& source);
	void               CopyImage(ImageId destination, ImageId source);
	void               AssociateStencil(ImageId depth, GuestRange stencil);
	void               AssociateStencilLocked(ImageId depth, GuestRange stencil);
	void CopyImageMip(ImageId destination, ImageId source, uint32_t mip, uint32_t layer);
	void ValidateImageDesc(const ImageDesc& desc) const;

	void InvalidateCpuAliases(uint64_t address, uint64_t size);
	void ClearGpuModified(ImageId id);

	void                                        DownloadImage(ImageId id);
	[[nodiscard]] bool                          TryDownloadImage(ImageId id);
	[[nodiscard]] std::pair<uint8_t*, uint64_t> MapDownload(uint64_t size, uint64_t alignment);
	void QueueDownload(GuestRange range, StreamBuffer& download, uint8_t* mapped, uint64_t offset);

	GraphicContext&                                   m_graphics;
	CommandScheduler&                                 m_scheduler;
	TrackingSpinLock                                  m_lock;
	PageManager&                                      m_page_manager;
	BlitHelper                                        m_blit_helper;
	std::unique_ptr<TileManager>                      m_tiler;
	BufferCache&                                      m_buffer_cache;
	ResourceMutex&                                    m_resource_mutex;
	std::vector<Slot>                                 m_slots;
	std::vector<uint32_t>                             m_free_slots;
	ImageOwnerIndex                                   m_image_owner_index;
	std::map<vk::Format, ImageId>                     m_null_images;
	Common::LeastRecentlyUsedCache<ImageId, uint64_t> m_lru_cache;
	std::set<ImageId>                                 m_download_images;
	std::map<uint64_t, MetaDataInfo>                  m_surface_metas;
	uint64_t                                          m_total_used_memory  = 0;
	uint64_t                                          m_trigger_gc_memory  = 0;
	uint64_t                                          m_pressure_gc_memory = 1536ull * 1024 * 1024;
	uint64_t m_critical_gc_memory     = 3ull * 1024 * 1024 * 1024;
	uint64_t m_gc_tick                = 0;
	bool     m_readback_linear_images = false;
	DebugImageWriterRecord m_debug_image_writer_previous;
	uint64_t                m_debug_image_writer_next_ordinal = 0;

	friend struct TextureCacheTestAccess;
	friend class BufferCache;
	friend class RenderExecutor;
};

} // namespace Libs::Graphics

#endif // EMULATOR_SRC_GRAPHICS_HOST_GPU_RENDERER_TEXTURECACHE_H_
