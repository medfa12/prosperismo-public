// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

#include "cliOptions.h"

#include "common/file.h"
#include "common/magicEnum.h"
#include "common/stringUtils.h"

#include <charconv>
#include <system_error>

namespace Emulator::Cli {
namespace {

bool NextArg(int argc, char* argv[], int& index, std::string& out) {
	if (index + 1 >= argc) {
		return false;
	}

	index++;
	out = argv[index];
	return true;
}

bool ParseBool(const std::string& value, bool& out) {
	if (Common::EqualNoCase(value, "true") || value == "1" || Common::EqualNoCase(value, "yes") ||
	    Common::EqualNoCase(value, "on")) {
		out = true;
		return true;
	}

	if (Common::EqualNoCase(value, "false") || value == "0" || Common::EqualNoCase(value, "no") ||
	    Common::EqualNoCase(value, "off")) {
		out = false;
		return true;
	}

	return false;
}

template <typename E>
bool ParseEnum(const std::string& value, E& out) {
	auto enum_value = magic_enum::enum_cast<E>(value.c_str());
	if (!enum_value.has_value()) {
		return false;
	}

	out = enum_value.value();
	return true;
}

bool ParseUint32(const std::string& value, uint32_t& out) {
	uint32_t parsed = 0;
	const auto result = std::from_chars(value.data(), value.data() + value.size(), parsed, 10);
	if (value.empty() || result.ec != std::errc {} || result.ptr != value.data() + value.size()) {
		return false;
	}

	out = parsed;
	return true;
}

bool ParseRangedUint32(const std::string& option, const std::string& value, uint32_t minimum,
	                    uint32_t maximum, uint32_t& out, std::string& error) {
	uint32_t parsed = 0;
	if (!ParseUint32(value, parsed)) {
		error = "invalid unsigned integer for " + option + ": " + value;
		return false;
	}
	if (parsed < minimum || parsed > maximum) {
		error = option + " must be between " + std::to_string(minimum) + " and " +
		        std::to_string(maximum) + ": " + value;
		return false;
	}

	out = parsed;
	return true;
}

bool RequirePath(const std::string& option, const std::string& value, std::filesystem::path& out,
	             std::string& error) {
	if (value.empty()) {
		error = option + " cannot be empty";
		return false;
	}
	out = value;
	return true;
}

} // namespace

bool Parse(int argc, char* argv[], RunOptions& options, bool& show_help, std::string& error) {
	show_help = false;
	error.clear();

	for (int i = 1; i < argc; i++) {
		std::string arg = argv[i];
		std::string value;

		if (arg == "--help" || arg == "-h") {
			show_help = true;
			continue;
		}

		if (arg == "--rd") {
			options.config.renderdoc_enabled = true;
			continue;
		}

		if (!Common::StartsWith(arg, "--")) {
			error = "game input must be provided with --game";
			return false;
		}

		if (!NextArg(argc, argv, i, value)) {
			error = "missing value for " + arg;
			return false;
		}

		if (arg == "--game") {
			if (!options.app0_dir.empty()) {
				error = "--game can only be specified once";
				return false;
			}

			value = Common::FixFilenameSlash(value);
			if (Common::File::IsDirectoryExisting(value)) {
				options.app0_dir = value;
				options.elf      = "/app0/eboot.bin";
			} else if (Common::File::IsFileExisting(value)) {
				options.app0_dir = Common::DirectoryWithoutFilename(value);
				if (options.app0_dir.empty()) {
					options.app0_dir = ".";
				}
				options.elf = "/app0/" + Common::FilenameWithoutDirectory(value);
			} else {
				error = "--game must point to an existing directory or ELF: " + value;
				return false;
			}
		} else if (arg == "--game-patch") {
			if (!options.game_patch.empty()) {
				error = "--game-patch can only be specified once";
				return false;
			}
			value = Common::FixFilenameSlash(value);
			if (!Common::File::IsFileExisting(value)) {
				error = "--game-patch must point to an existing file: " + value;
				return false;
			}
			options.game_patch = value;
		} else if (arg == "--screen-width") {
			if (!ParseRangedUint32(arg, value, Config::SCREEN_DIMENSION_MIN,
			                       Config::SCREEN_DIMENSION_MAX, options.config.screen_width, error)) {
				return false;
			}
		} else if (arg == "--screen-height") {
			if (!ParseRangedUint32(arg, value, Config::SCREEN_DIMENSION_MIN,
			                       Config::SCREEN_DIMENSION_MAX, options.config.screen_height, error)) {
				return false;
			}
		} else if (arg == "--vblank-frequency") {
			if (!ParseRangedUint32(arg, value, Config::VBLANK_FREQUENCY_MIN,
			                       Config::VBLANK_FREQUENCY_MAX, options.config.vblank_frequency,
			                       error)) {
				return false;
			}
		} else if (arg == "--vulkan-validation") {
			if (!ParseBool(value, options.config.vulkan_validation_enabled)) {
				error = "invalid boolean for " + arg + ": " + value;
				return false;
			}
		} else if (arg == "--shader-validation") {
			if (!ParseBool(value, options.config.shader_validation_enabled)) {
				error = "invalid boolean for " + arg + ": " + value;
				return false;
			}
		} else if (arg == "--shader-optimization-type") {
			if (!ParseEnum(value, options.config.shader_optimization_type)) {
				error = "invalid shader optimization type: " + value;
				return false;
			}
		} else if (arg == "--shader-log-direction") {
			if (!ParseEnum(value, options.config.shader_log_direction)) {
				error = "invalid shader log direction: " + value;
				return false;
			}
		} else if (arg == "--shader-log-folder") {
			if (!RequirePath(arg, value, options.config.shader_log_folder, error)) {
				return false;
			}
		} else if (arg == "--command-buffer-dump") {
			if (!ParseBool(value, options.config.command_buffer_dump_enabled)) {
				error = "invalid boolean for " + arg + ": " + value;
				return false;
			}
		} else if (arg == "--command-buffer-dump-folder") {
			if (!RequirePath(arg, value, options.config.command_buffer_dump_folder, error)) {
				return false;
			}
		} else if (arg == "--graphics-debug-dump") {
			if (!ParseBool(value, options.config.graphics_debug_dump_enabled)) {
				error = "invalid boolean for " + arg + ": " + value;
				return false;
			}
		} else if (arg == "--printf-direction") {
			if (!ParseEnum(value, options.config.printf_direction)) {
				error = "invalid printf direction: " + value;
				return false;
			}
		} else if (arg == "--printf-output-file") {
			if (!RequirePath(arg, value, options.config.printf_output_file, error)) {
				return false;
			}
		} else if (arg == "--profiler-direction") {
			if (!ParseEnum(value, options.config.profiler_direction)) {
				error = "invalid profiler direction: " + value;
				return false;
			}
		} else if (arg == "--spirv-debug-printf") {
			if (!ParseBool(value, options.config.spirv_debug_printf_enabled)) {
				error = "invalid boolean for " + arg + ": " + value;
				return false;
			}
		} else if (arg == "--ngg-rectlist-draw") {
			if (!ParseBool(value, options.config.ngg_rectlist_draw_enabled)) {
				error = "invalid boolean for " + arg + ": " + value;
				return false;
			}
		} else if (arg == "--readback-linear-images") {
			if (!ParseBool(value, options.config.readback_linear_images)) {
				error = "invalid boolean for " + arg + ": " + value;
				return false;
			}
		} else {
			error = "unknown option: " + arg;
			return false;
		}
	}

	if (!Config::Validate(options.config, &error)) {
		return false;
	}

	if (show_help) {
		return true;
	}
	if (options.app0_dir.empty() || options.elf.empty()) {
		error = "--game is required";
		return false;
	}

	return true;
}

} // namespace Emulator::Cli
