#include "common/common.h"
#include "common/commonSubsystem.h"
#include "common/dateTime.h"
#include "common/debug.h"
#include "common/platform/sysDbg.h"
#include "common/threads.h"
#include "cliOptions.h"
#include "emulator.h"
#include "kytyGitVersion.h"

#include <cstdio>
#include <fmt/format.h>

using namespace Common;
using namespace Emulator;

static std::string GetBuildString() {
	Date date = Date::FromMacros(std::string(__DATE__));

#if KYTY_BUILD == KYTY_BUILD_DEBUG
	std::string type = "Debug";
#elif KYTY_BUILD == KYTY_BUILD_RELEASE
	std::string type = "Release";
#else
	std::string type = "????";
#endif

	std::string compiler =
	    Debug::GetCompiler() + "-" + Debug::GetLinker() + "-" + Debug::GetBitness();

	std::string str =
	    fmt::format("{}, {}, ver = {}, git = {}, date = {}", type.c_str(), compiler.c_str(),
	                KYTY_VERSION, KYTY_GIT_VERSION, date.ToString().c_str());

	return str;
}

static void PrintUsage() {
	::printf("%s\n", GetBuildString().c_str());
	::printf("prosperismo_emulator --game <dir|elf> [options]\n\n");
	::printf("Options:\n");
	::printf("  --game <dir|elf>                     Game directory or ELF to load.\n");
	::printf(
	    "  --game-patch <json>                  Validated patch plan to apply before entry.\n");
	::printf("  --screen-width <num>                 Window width (1-16384). Default: 1280.\n");
	::printf("  --screen-height <num>                Window height (1-16384). Default: 720.\n");
	::printf("  --vblank-frequency <num>             Virtual vblank frequency (30-360). Default: 60.\n");
	::printf("  --vulkan-validation <true|false>     Enable Vulkan validation.\n");
	::printf("  --shader-validation <true|false>     Enable shader validation.\n");
	::printf("  --shader-optimization-type <value>   None, Size, or Performance.\n");
	::printf("  --shader-log-direction <value>       Silent, Console, or File.\n");
	::printf("  --shader-log-folder <path>           Shader log output folder.\n");
	::printf("  --command-buffer-dump <true|false>   Enable command buffer dumps.\n");
	::printf("  --command-buffer-dump-folder <path>  Command buffer dump folder.\n");
	::printf("  --graphics-debug-dump <true|false>   Enable graphics debug dumps.\n");
	::printf("  --printf-direction <value>           Silent, Console, or File.\n");
	::printf("  --printf-output-file <path>          Guest printf output file.\n");
	::printf("  --profiler-direction <value>         None or Network.\n");
	::printf("  --spirv-debug-printf <true|false>    Enable SPIR-V debug printf.\n");
	::printf("  --ngg-rectlist-draw <true|false>     Draw rect-list auto draws using the NGG "
	         "4-vertex path.\n");
	::printf(
	    "  --readback-linear-images <true|false> Read back writable linear images on submit.\n");
	::printf("  --rd                                 Enable RenderDoc capture.\n");
}

int main(int argc, char* argv[]) {
	auto& slist = *SubsystemsList::Instance();

	slist.SetArgs(argc, argv);

	auto* core    = CommonSubsystem::Instance();
	auto* threads = ThreadsSubsystem::Instance();

	slist.Add(core, {});
	slist.Add(threads, {core});

	if (!slist.InitAll(false)) {
		::printf("Failed to initialize '%s' subsystem: %s\n", slist.GetFailName(),
		         slist.GetFailMsg());
		return 1;
	}

	RunOptions options;
	bool       show_help = false;
	std::string parse_error;

	if (argc < 2) {
		PrintUsage();
		slist.DestroyAll(false);
		return 0;
	}

	if (!Cli::Parse(argc, argv, options, show_help, parse_error)) {
		::printf("%s\n", parse_error.c_str());
		PrintUsage();
		slist.DestroyAll(false);
		return 1;
	}

	if (show_help) {
		PrintUsage();
		slist.DestroyAll(false);
		return 0;
	}

	Run(options);

	slist.DestroyAll(false);

	return 0;
}
