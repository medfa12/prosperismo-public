// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

#include "cliOptions.h"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <string>
#include <vector>

namespace {

void Check(bool condition, const char* message) {
	if (!condition) {
		std::fprintf(stderr, "CliOptionsTests: %s\n", message);
		std::exit(1);
	}
}

struct ParseResult {
	bool                 ok        = false;
	bool                 show_help = false;
	Emulator::RunOptions options;
	std::string          error;
};

ParseResult Parse(std::vector<std::string> arguments) {
	arguments.insert(arguments.begin(), "prosperismo_emulator");
	std::vector<char*> argv;
	argv.reserve(arguments.size());
	for (auto& argument: arguments) {
		argv.push_back(argument.data());
	}

	ParseResult result;
	result.ok = Emulator::Cli::Parse(static_cast<int>(argv.size()), argv.data(), result.options,
	                                 result.show_help, result.error);
	return result;
}

std::filesystem::path MakeGameDirectory() {
	const auto suffix = std::chrono::steady_clock::now().time_since_epoch().count();
	const auto path =
	    std::filesystem::temp_directory_path() / ("prosperismo-cli-options-" + std::to_string(suffix));
	std::filesystem::create_directories(path);
	return path;
}

std::vector<std::string> WithGame(const std::filesystem::path& game,
	                              std::vector<std::string> arguments) {
	arguments.emplace_back("--game");
	arguments.push_back(game.string());
	return arguments;
}

void TestCompleteKytyContract(const std::filesystem::path& game) {
	auto result = Parse(WithGame(
	    game,
	    {"--screen-width", "1920", "--screen-height", "1080", "--vblank-frequency", "120",
	     "--vulkan-validation", "true", "--shader-validation", "true",
	     "--shader-optimization-type", "Size", "--shader-log-direction", "File",
	     "--shader-log-folder", "Shader Logs", "--command-buffer-dump", "true",
	     "--command-buffer-dump-folder", "Command Buffers", "--graphics-debug-dump", "true",
	     "--printf-direction", "Silent", "--printf-output-file", "guest.txt",
	     "--profiler-direction", "Network", "--spirv-debug-printf", "true",
	     "--ngg-rectlist-draw", "false", "--readback-linear-images", "true", "--rd"}));

	Check(result.ok && !result.show_help && result.error.empty(),
	      "complete Kyty command line was rejected");
	const auto& cfg = result.options.config;
	Check(cfg.screen_width == 1920 && cfg.screen_height == 1080 && cfg.vblank_frequency == 120,
	      "display settings did not parse exactly");
	Check(cfg.vulkan_validation_enabled && cfg.shader_validation_enabled,
	      "validation settings did not parse");
	Check(cfg.shader_optimization_type == Config::ShaderOptimizationType::Size &&
	          cfg.shader_log_direction == Config::ShaderLogDirection::File &&
	          cfg.shader_log_folder == "Shader Logs",
	      "shader settings did not parse");
	Check(cfg.command_buffer_dump_enabled && cfg.command_buffer_dump_folder == "Command Buffers" &&
	          cfg.graphics_debug_dump_enabled,
	      "command-buffer settings did not parse");
	Check(cfg.printf_direction == Config::OutputDirection::Silent &&
	          cfg.printf_output_file == "guest.txt" &&
	          cfg.profiler_direction == Config::ProfilerDirection::Network,
	      "output settings did not parse");
	Check(cfg.spirv_debug_printf_enabled && !cfg.ngg_rectlist_draw_enabled &&
	          cfg.readback_linear_images && cfg.renderdoc_enabled,
	      "native extension settings did not parse");
}

void TestNumericValidation(const std::filesystem::path& game) {
	for (const auto& [option, value]: std::vector<std::pair<std::string, std::string>> {
	         {"--screen-width", "0"},       {"--screen-width", "16385"},
	         {"--screen-width", "-1"},      {"--screen-width", "1280px"},
	         {"--screen-height", "0"},      {"--screen-height", "4294967296"},
	         {"--vblank-frequency", "29"}, {"--vblank-frequency", "361"},
	         {"--vblank-frequency", "60.0"}}) {
		auto result = Parse(WithGame(game, {option, value}));
		Check(!result.ok && !result.error.empty(), "invalid numeric setting was accepted");
	}

	auto minimum = Parse(WithGame(
	    game, {"--screen-width", "1", "--screen-height", "1", "--vblank-frequency", "30"}));
	auto maximum =
	    Parse(WithGame(game, {"--screen-width", "16384", "--screen-height", "16384",
	                          "--vblank-frequency", "360"}));
	Check(minimum.ok && maximum.ok, "documented numeric boundaries were rejected");
}

void TestPathAndEnumValidation(const std::filesystem::path& game) {
	for (const auto& arguments: std::vector<std::vector<std::string>> {
	         {"--shader-log-folder", ""},
	         {"--command-buffer-dump-folder", ""},
	         {"--printf-output-file", ""},
	         {"--shader-optimization-type", "Fast"},
	         {"--shader-log-direction", "file"},
	         {"--profiler-direction", "Local"}}) {
		auto result = Parse(WithGame(game, arguments));
		Check(!result.ok && !result.error.empty(), "invalid path or enum setting was accepted");
	}
}

void TestCommandStructure(const std::filesystem::path& game) {
	auto help = Parse({"--help"});
	Check(help.ok && help.show_help, "help should not require a game");

	auto missing_game = Parse({"--screen-width", "1280"});
	Check(!missing_game.ok && missing_game.error == "--game is required",
	      "missing game did not produce a precise error");

	auto duplicate = Parse(
	    {"--game", game.string(), "--game", game.string()});
	Check(!duplicate.ok, "duplicate --game was accepted");
}

void TestConfigValidation() {
	Config::ConfigOptions cfg;
	std::string           error;
	Check(Config::Validate(cfg, &error), "default native config is invalid");
	cfg.screen_width = 0;
	Check(!Config::Validate(cfg, &error) && !error.empty(),
	      "direct config validation accepted a zero width");
}

} // namespace

int main() {
	const auto game = MakeGameDirectory();
	TestCompleteKytyContract(game);
	TestNumericValidation(game);
	TestPathAndEnumValidation(game);
	TestCommandStructure(game);
	TestConfigValidation();
	std::filesystem::remove_all(game);
	std::printf("CliOptionsTests: all checks passed\n");
	return 0;
}
