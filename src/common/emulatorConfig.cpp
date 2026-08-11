#include "common/emulatorConfig.h"

#include "common/assert.h"

#include <algorithm>
#include <memory>

namespace Config {

static std::unique_ptr<ConfigOptions> g_config;

KYTY_SUBSYSTEM_INIT(Config) {
	EXIT_IF(g_config != nullptr);

	g_config = std::make_unique<ConfigOptions>();
}

KYTY_SUBSYSTEM_UNEXPECTED_SHUTDOWN(Config) {}

KYTY_SUBSYSTEM_DESTROY(Config) {}

void Load(const ConfigOptions& cfg) {
	EXIT_IF(g_config == nullptr);

	*g_config = cfg;
}

bool Validate(const ConfigOptions& cfg, std::string* error) {
	auto fail = [error](const char* message) {
		if (error != nullptr) {
			*error = message;
		}
		return false;
	};

	if (cfg.screen_width < SCREEN_DIMENSION_MIN || cfg.screen_width > SCREEN_DIMENSION_MAX) {
		return fail("screen width must be between 1 and 16384 pixels");
	}
	if (cfg.screen_height < SCREEN_DIMENSION_MIN || cfg.screen_height > SCREEN_DIMENSION_MAX) {
		return fail("screen height must be between 1 and 16384 pixels");
	}
	if (cfg.vblank_frequency < VBLANK_FREQUENCY_MIN ||
	    cfg.vblank_frequency > VBLANK_FREQUENCY_MAX) {
		return fail("vblank frequency must be between 30 and 360 Hz");
	}
	if (cfg.shader_log_folder.empty()) {
		return fail("shader log folder cannot be empty");
	}
	if (cfg.command_buffer_dump_folder.empty()) {
		return fail("command buffer dump folder cannot be empty");
	}
	if (cfg.printf_output_file.empty()) {
		return fail("printf output file cannot be empty");
	}

	return true;
}

uint32_t GetScreenWidth() {
	return g_config->screen_width;
}

uint32_t GetScreenHeight() {
	return g_config->screen_height;
}

uint32_t GetVblankFrequency() {
	return std::clamp(g_config->vblank_frequency, VBLANK_FREQUENCY_MIN, VBLANK_FREQUENCY_MAX);
}

bool VulkanValidationEnabled() {
	return g_config->vulkan_validation_enabled;
}

bool ShaderValidationEnabled() {
	return g_config->shader_validation_enabled;
}

ShaderOptimizationType GetShaderOptimizationType() {
	return g_config->shader_optimization_type;
}

ShaderLogDirection GetShaderLogDirection() {
	return g_config->shader_log_direction;
}

std::filesystem::path GetShaderLogFolder() {
	return g_config->shader_log_folder;
}

bool CommandBufferDumpEnabled() {
	return g_config->command_buffer_dump_enabled;
}

std::filesystem::path GetCommandBufferDumpFolder() {
	return g_config->command_buffer_dump_folder;
}

bool GraphicsDebugDumpEnabled() {
	return g_config->graphics_debug_dump_enabled;
}

OutputDirection GetPrintfDirection() {
	return g_config->printf_direction;
}

std::filesystem::path GetPrintfOutputFile() {
	return g_config->printf_output_file;
}

ProfilerDirection GetProfilerDirection() {
	return g_config->profiler_direction;
}

bool SpirvDebugPrintfEnabled() {
	return g_config->spirv_debug_printf_enabled;
}

bool RenderDocEnabled() {
	return g_config->renderdoc_enabled;
}

bool NggRectlistDrawEnabled() {
	return g_config->ngg_rectlist_draw_enabled;
}

bool ReadbackLinearImagesEnabled() {
	return g_config->readback_linear_images;
}

} // namespace Config
