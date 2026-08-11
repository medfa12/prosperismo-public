using Prosperismo.GUI;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class EmulatorLaunchContractTests
{
    [Fact]
    public void TypedNativeContractMatchesExactKytyArgumentOrder()
    {
        using var fixture = new PatchFixture("PPSA01325");
        var settings = new EmulatorSettings
        {
            ScreenResolution = EmulatorResolution.R1920X1080,
            VblankFrequency = 120,
            VulkanValidation = false,
            ShaderValidation = false,
            ShaderOptimization = ShaderOptimizationMode.Size,
            ShaderLogDirection = EmulatorOutputDirection.File,
            ShaderLogFolder = "/logs/shaders with spaces",
            CommandBufferDump = true,
            CommandBufferDumpFolder = "/logs/buffers with spaces",
            PrintfDirection = EmulatorOutputDirection.Console,
            PrintfOutputFile = "/logs/guest output.txt",
            ProfilerDirection = EmulatorProfilerDirection.Network,
            RenderDoc = true,
            NggRectlistDraw = false,
        };

        var arguments = EmulatorLaunchContract.BuildArguments(
            settings,
            "/games/Astro's Playroom/eboot.bin",
            fixture.PatchPath);

        Assert.Equal(
            [
                "--screen-width", "1920",
                "--screen-height", "1080",
                "--vblank-frequency", "120",
                "--vulkan-validation", "false",
                "--shader-validation", "false",
                "--shader-optimization-type", "Size",
                "--shader-log-direction", "File",
                "--shader-log-folder", "/logs/shaders with spaces",
                "--command-buffer-dump", "true",
                "--command-buffer-dump-folder", "/logs/buffers with spaces",
                "--printf-direction", "Console",
                "--printf-output-file", "/logs/guest output.txt",
                "--profiler-direction", "Network",
                "--spirv-debug-printf", "false",
                "--ngg-rectlist-draw", "false",
                "--rd",
                "--game", "/games/Astro's Playroom/eboot.bin",
                "--game-patch", fixture.PatchPath,
            ],
            arguments);
    }

    [Fact]
    public void TypedNativeContractUsesOriginalKytyDefaults()
    {
        var arguments = EmulatorLaunchContract.BuildArguments(
            new EmulatorSettings(),
            "/games/title/eboot.bin");

        Assert.Equal(
            [
                "--screen-width", "1280",
                "--screen-height", "720",
                "--vblank-frequency", "60",
                "--vulkan-validation", "true",
                "--shader-validation", "true",
                "--shader-optimization-type", "Performance",
                "--shader-log-direction", "Silent",
                "--shader-log-folder", "_Shaders",
                "--command-buffer-dump", "false",
                "--command-buffer-dump-folder", "_Buffers",
                "--printf-direction", "Silent",
                "--printf-output-file", "_prosperismo.txt",
                "--profiler-direction", "None",
                "--spirv-debug-printf", "false",
                "--ngg-rectlist-draw", "true",
                "--game", "/games/title/eboot.bin",
            ],
            arguments);
    }

    [Theory]
    [InlineData(ShaderOptimizationMode.None, "None")]
    [InlineData(ShaderOptimizationMode.Size, "Size")]
    [InlineData(ShaderOptimizationMode.Performance, "Performance")]
    public void MapsEveryShaderOptimizationParserSpelling(
        ShaderOptimizationMode mode,
        string expected)
    {
        var arguments = EmulatorLaunchContract.BuildArguments(
            new EmulatorSettings { ShaderOptimization = mode },
            "/games/title");

        AssertOption(arguments, "--shader-optimization-type", expected);
    }

    [Theory]
    [InlineData(EmulatorOutputDirection.Silent, "Silent")]
    [InlineData(EmulatorOutputDirection.Console, "Console")]
    [InlineData(EmulatorOutputDirection.File, "File")]
    public void MapsEveryOutputDirectionParserSpelling(
        EmulatorOutputDirection direction,
        string expected)
    {
        var arguments = EmulatorLaunchContract.BuildArguments(
            new EmulatorSettings
            {
                ShaderLogDirection = direction,
                PrintfDirection = direction,
            },
            "/games/title");

        AssertOption(arguments, "--shader-log-direction", expected);
        AssertOption(arguments, "--printf-direction", expected);
    }

    [Theory]
    [InlineData(EmulatorProfilerDirection.None, "None")]
    [InlineData(EmulatorProfilerDirection.Network, "Network")]
    public void MapsEveryProfilerDirectionParserSpelling(
        EmulatorProfilerDirection direction,
        string expected)
    {
        var arguments = EmulatorLaunchContract.BuildArguments(
            new EmulatorSettings { ProfilerDirection = direction },
            "/games/title");

        AssertOption(arguments, "--profiler-direction", expected);
    }

    [Fact]
    public void RejectsUnknownEnumValuesInsteadOfSendingNumericSpellings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EmulatorLaunchContract.BuildArguments(
                new EmulatorSettings { ShaderOptimization = (ShaderOptimizationMode)999 },
                "/games/title"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EmulatorLaunchContract.BuildArguments(
                new EmulatorSettings { ShaderLogDirection = (EmulatorOutputDirection)999 },
                "/games/title"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EmulatorLaunchContract.BuildArguments(
                new EmulatorSettings { ProfilerDirection = (EmulatorProfilerDirection)999 },
                "/games/title"));
    }

    [Fact]
    public void RejectsInvalidSettingsGameAndPatchPaths()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EmulatorLaunchContract.BuildArguments(
                new EmulatorSettings { VblankFrequency = 0 },
                "/games/title"));
        Assert.Throws<ArgumentException>(() =>
            EmulatorLaunchContract.BuildArguments(new EmulatorSettings(), "  "));
        Assert.Throws<FileNotFoundException>(() =>
            EmulatorLaunchContract.BuildArguments(
                new EmulatorSettings(),
                "/games/title",
                Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
    }

    [Fact]
    public void MacEnvironmentPreservesExistingPathsAndAddsNativeRuntime()
    {
        var existing = "/custom/vulkan";
        var executableDirectory = Path.Combine(Path.GetTempPath(), "prosperismo-native");
        var executable = Path.Combine(executableDirectory, "prosperismo_emulator");
        var icd = "/opt/homebrew/etc/vulkan/icd.d/MoltenVK_icd.json";
        var existingDirectories = new HashSet<string>(StringComparer.Ordinal)
        {
            executableDirectory,
            "/opt/homebrew/lib",
            "/opt/homebrew/opt/molten-vk/lib",
        };

        var environment = EmulatorLaunchContract.BuildMacOsEnvironment(
            executable,
            existing,
            currentIcdFiles: null,
            currentSdlVulkanLibrary: null,
            directoryExists: existingDirectories.Contains,
            fileExists: path => string.Equals(path, icd, StringComparison.Ordinal));

        Assert.Equal(
            string.Join(
                Path.PathSeparator,
                existing,
                executableDirectory,
                "/opt/homebrew/lib",
                "/opt/homebrew/opt/molten-vk/lib"),
            environment["DYLD_LIBRARY_PATH"]);
        Assert.Equal(icd, environment["VK_ICD_FILENAMES"]);
    }

    [Fact]
    public void MacEnvironmentDoesNotOverrideConfiguredIcd()
    {
        var environment = EmulatorLaunchContract.BuildMacOsEnvironment(
            "/build/prosperismo_emulator",
            currentLibraryPath: null,
            currentIcdFiles: "/custom/icd.json",
            currentSdlVulkanLibrary: null,
            directoryExists: _ => false,
            fileExists: _ => false);

        Assert.Equal("/custom/icd.json", environment["VK_ICD_FILENAMES"]);
    }

    [Fact]
    public void MacEnvironmentBindsTheBundledMoltenVkForFinderLaunches()
    {
        const string executable = "/release/prosperismo_emulator";
        const string moltenVk = "/release/libMoltenVK.dylib";

        var environment = EmulatorLaunchContract.BuildMacOsEnvironment(
            executable,
            currentLibraryPath: null,
            currentIcdFiles: null,
            currentSdlVulkanLibrary: null,
            directoryExists: path => path == "/release",
            fileExists: path => path == moltenVk);

        Assert.Equal(moltenVk, environment["SDL_VULKAN_LIBRARY"]);
    }

    [Fact]
    public void MacEnvironmentPreservesAnExplicitSdlVulkanLibrary()
    {
        const string configured = "/custom/MoltenVK.dylib";

        var environment = EmulatorLaunchContract.BuildMacOsEnvironment(
            "/release/prosperismo_emulator",
            currentLibraryPath: null,
            currentIcdFiles: null,
            currentSdlVulkanLibrary: configured,
            directoryExists: _ => false,
            fileExists: _ => false);

        Assert.Equal(configured, environment["SDL_VULKAN_LIBRARY"]);
    }

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string expected)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0, $"Missing option {option}.");
        Assert.Equal(expected, arguments[index + 1]);
    }

    private sealed class PatchFixture : IDisposable
    {
        private readonly string _root;

        public PatchFixture(string titleId)
        {
            _root = Path.Combine(Path.GetTempPath(), "prosperismo-contract-" + Guid.NewGuid());
            Directory.CreateDirectory(_root);
            PatchPath = Path.Combine(_root, titleId + ".json");
            File.WriteAllText(PatchPath, "{\"patches\":[]}");
        }

        public string PatchPath { get; }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
