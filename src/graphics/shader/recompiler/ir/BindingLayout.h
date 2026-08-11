#ifndef EMULATOR_INCLUDE_EMULATOR_GRAPHICS_SHADER_RECOMPILER_BINDINGLAYOUT_H_
#define EMULATOR_INCLUDE_EMULATOR_GRAPHICS_SHADER_RECOMPILER_BINDINGLAYOUT_H_

#include "graphics/shader/recompiler/ir/ShaderIR.h"

namespace Libs::Graphics::ShaderRecompiler::IR {

struct BindingLayoutOptions {
	uint32_t descriptor_set       = 0;
	uint32_t push_constant_offset = 0;
	uint32_t max_push_dwords      = 32;
};

bool AllocateBindings(Program& program, const BindingLayoutOptions& options, std::string* error);

const DescriptorBinding* FindBinding(const BindingLayout& layout, DescriptorBindingKind kind);

// Returns the physical descriptor-array width after specialized dynamic-storage
// resources have been expanded into one statically indexed view per mip.
uint32_t DescriptorCount(const Program& program, const DescriptorBinding& binding);

// Resolves a logical resource ordinal to the first physical descriptor occupied by it.
bool DescriptorArrayIndex(const Program& program, const DescriptorBinding& binding,
                          uint32_t resource, uint32_t& index);

} // namespace Libs::Graphics::ShaderRecompiler::IR

#endif /* EMULATOR_INCLUDE_EMULATOR_GRAPHICS_SHADER_RECOMPILER_BINDINGLAYOUT_H_ */
