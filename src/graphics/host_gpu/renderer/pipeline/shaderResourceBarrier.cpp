#include "graphics/host_gpu/renderer/pipeline/shaderResourceBarrier.h"

#include "common/assert.h"
#include "graphics/shader/shader.h"
#include "graphics/shader/shaderBindings.h"

#include <cstring>

namespace Libs::Graphics {

vk::PipelineStageFlags ShaderPipelineStages(vk::ShaderStageFlags stages) {
	vk::PipelineStageFlags result = {};
	if (stages & vk::ShaderStageFlagBits::eVertex) {
		result |= vk::PipelineStageFlagBits::eVertexShader;
	}
	if (stages & vk::ShaderStageFlagBits::eFragment) {
		result |= vk::PipelineStageFlagBits::eFragmentShader;
	}
	if (stages & vk::ShaderStageFlagBits::eCompute) {
		result |= vk::PipelineStageFlagBits::eComputeShader;
	}
	EXIT_IF(!result);
	return result;
}

// A guest compute pass may author vertex/index data and the indirect argument
// words of the draw that follows it, so the shader-write dependency must reach
// DrawIndirect/IndirectCommandRead as well as the vertex-input reads.
vk::PipelineStageFlags ShaderWriteDestinationStages() {
	return vk::PipelineStageFlagBits::eDrawIndirect | vk::PipelineStageFlagBits::eComputeShader |
	       vk::PipelineStageFlagBits::eVertexInput | vk::PipelineStageFlagBits::eVertexShader |
	       vk::PipelineStageFlagBits::eFragmentShader | vk::PipelineStageFlagBits::eTransfer |
	       vk::PipelineStageFlagBits::eColorAttachmentOutput;
}

VulkanMemoryBarrier MakeShaderWriteDependency() {
	VulkanMemoryBarrier barrier {};
	barrier.sType         = vk::StructureType::eMemoryBarrier;
	barrier.srcAccessMask = vk::AccessFlagBits::eShaderWrite;
	barrier.dstAccessMask = vk::AccessFlagBits::eShaderRead | vk::AccessFlagBits::eShaderWrite |
	                        vk::AccessFlagBits::eIndirectCommandRead |
	                        vk::AccessFlagBits::eVertexAttributeRead |
	                        vk::AccessFlagBits::eIndexRead | vk::AccessFlagBits::eUniformRead |
	                        vk::AccessFlagBits::eTransferRead | vk::AccessFlagBits::eTransferWrite |
	                        vk::AccessFlagBits::eColorAttachmentRead |
	                        vk::AccessFlagBits::eColorAttachmentWrite;
	return barrier;
}

vk::BufferMemoryBarrier MakeGdsDependency(vk::Buffer buffer) {
	EXIT_IF(buffer == nullptr);

	vk::BufferMemoryBarrier barrier {};
	barrier.sType         = vk::StructureType::eBufferMemoryBarrier;
	barrier.srcAccessMask = vk::AccessFlagBits::eHostWrite | vk::AccessFlagBits::eTransferWrite |
	                        vk::AccessFlagBits::eShaderWrite;
	barrier.dstAccessMask = vk::AccessFlagBits::eShaderRead | vk::AccessFlagBits::eShaderWrite;
	barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
	barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
	barrier.buffer              = buffer;
	barrier.offset              = 0;
	barrier.size                = VK_WHOLE_SIZE;
	return barrier;
}

std::vector<ShaderBufferWriteRange>
CollectShaderBufferWrites(const ShaderRecompiler::IR::Program&          program,
                          const ShaderRecompiler::IR::ResourceSnapshot& resources) {
	EXIT_IF(resources.buffers.size() != program.info.buffers.size());
	std::vector<ShaderBufferWriteRange> writes;
	for (uint32_t i = 0; i < program.info.buffers.size(); i++) {
		if (!program.info.buffers[i].written) {
			continue;
		}
		const auto& value = resources.buffers[i];
		EXIT_IF(value.dword_count < 4);
		ShaderBufferResource descriptor;
		std::memcpy(descriptor.fields, value.dwords.data(), sizeof(descriptor.fields));
		const auto address = descriptor.Base48();
		const auto records = static_cast<uint64_t>(descriptor.NumRecords());
		const auto stride  = static_cast<uint64_t>(descriptor.Stride());
		if (stride != 0 && records > UINT64_MAX / stride) {
			EXIT("shader resource barrier buffer footprint overflow\n");
		}
		const auto size = stride == 0 ? records : stride * records;
		if (address != 0 && size != 0) {
			writes.push_back({address, size});
		}
	}
	return writes;
}

bool HasShaderBufferWrites(const ShaderStageRuntime& runtime) {
	EXIT_IF(!runtime);
	return !CollectShaderBufferWrites(*runtime.program, *runtime.resources).empty();
}

void ShaderWriteBarrier(vk::CommandBuffer vk_buffer, vk::PipelineStageFlags source_stages) {
	EXIT_IF(vk_buffer == nullptr || !source_stages);
	const auto barrier = MakeShaderWriteDependency();
	vk_buffer.pipelineBarrier(source_stages, ShaderWriteDestinationStages(),
	                          vk::DependencyFlags {}, 1, &barrier, 0, nullptr, 0, nullptr);
}

} // namespace Libs::Graphics
