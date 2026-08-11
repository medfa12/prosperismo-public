// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI;

/// <summary>
/// Serializes launcher state for Prosperismo's native CLI.
/// </summary>
internal static class EmulatorLaunchContract
{
    public const string NativeExecutableStem = "prosperismo_emulator";

    public static IReadOnlyList<string> BuildArguments(
        EmulatorSettings settings,
        string gamePath,
        string? validatedPatchPlanPath = null)
    {
        EmulatorSettingsContract.Validate(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);

        var (width, height) = EmulatorSettingsContract.Dimensions(settings.ScreenResolution);
        var arguments = new List<string>
        {
            "--screen-width",
            width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--screen-height",
            height.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--vblank-frequency",
            settings.VblankFrequency.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--vulkan-validation",
            BoolArg(settings.VulkanValidation),
            "--shader-validation",
            BoolArg(settings.ShaderValidation),
            "--shader-optimization-type",
            ShaderOptimizationArg(settings.ShaderOptimization),
            "--shader-log-direction",
            OutputDirectionArg(settings.ShaderLogDirection),
            "--shader-log-folder",
            settings.ShaderLogFolder,
            "--command-buffer-dump",
            BoolArg(settings.CommandBufferDump),
            "--command-buffer-dump-folder",
            settings.CommandBufferDumpFolder,
            "--printf-direction",
            OutputDirectionArg(settings.PrintfDirection),
            "--printf-output-file",
            settings.PrintfOutputFile,
            "--profiler-direction",
            ProfilerDirectionArg(settings.ProfilerDirection),
            "--spirv-debug-printf",
            "false",
            "--ngg-rectlist-draw",
            BoolArg(settings.NggRectlistDraw),
        };

        if (settings.RenderDoc)
        {
            arguments.Add("--rd");
        }

        arguments.Add("--game");
        arguments.Add(gamePath);

        if (validatedPatchPlanPath is not null)
        {
            arguments.Add("--game-patch");
            arguments.Add(PatchPlanStore.ValidateExistingPlanPath(validatedPatchPlanPath));
        }

        return arguments;
    }

    private static string BoolArg(bool value) => value ? "true" : "false";

    private static string ShaderOptimizationArg(ShaderOptimizationMode value) => value switch
    {
        ShaderOptimizationMode.None => "None",
        ShaderOptimizationMode.Size => "Size",
        ShaderOptimizationMode.Performance => "Performance",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported shader optimization mode."),
    };

    private static string OutputDirectionArg(EmulatorOutputDirection value) => value switch
    {
        EmulatorOutputDirection.Silent => "Silent",
        EmulatorOutputDirection.Console => "Console",
        EmulatorOutputDirection.File => "File",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported emulator output direction."),
    };

    private static string ProfilerDirectionArg(EmulatorProfilerDirection value) => value switch
    {
        EmulatorProfilerDirection.None => "None",
        EmulatorProfilerDirection.Network => "Network",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported emulator profiler direction."),
    };

    /// <summary>
    /// Environment required by the native macOS backend. A terminal launched
    /// from Homebrew commonly inherits these values; an Avalonia app launched
    /// from Finder does not. Keep the fix at the process boundary so launching
    /// a game through the UI behaves like the already-working direct command.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironment(
        string executablePath)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new Dictionary<string, string>();
        }

        return BuildMacOsEnvironment(
            executablePath,
            Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH"),
            Environment.GetEnvironmentVariable("VK_ICD_FILENAMES"),
            Environment.GetEnvironmentVariable("SDL_VULKAN_LIBRARY"),
            Directory.Exists,
            File.Exists);
    }

    internal static IReadOnlyDictionary<string, string> BuildMacOsEnvironment(
        string executablePath,
        string? currentLibraryPath,
        string? currentIcdFiles,
        string? currentSdlVulkanLibrary,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(fileExists);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var libraryDirectories = new List<string>();

        AddPathList(libraryDirectories, currentLibraryPath);
        var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        foreach (var candidate in new[]
        {
            executableDirectory,
            "/opt/homebrew/lib",
            "/opt/homebrew/opt/molten-vk/lib",
            "/usr/local/lib",
            "/usr/local/opt/molten-vk/lib",
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && directoryExists(candidate))
            {
                AddDistinct(libraryDirectories, candidate);
            }
        }

        if (libraryDirectories.Count > 0)
        {
            environment["DYLD_LIBRARY_PATH"] = string.Join(
                Path.PathSeparator,
                libraryDirectories);
        }

        if (!string.IsNullOrWhiteSpace(currentIcdFiles))
        {
            environment["VK_ICD_FILENAMES"] = currentIcdFiles;
        }
        else
        {
            foreach (var candidate in new[]
            {
                executableDirectory is null
                    ? null
                    : Path.Combine(executableDirectory, "MoltenVK_icd.json"),
                executableDirectory is null
                    ? null
                    : Path.Combine(executableDirectory, "vulkan", "icd.d", "MoltenVK_icd.json"),
                "/opt/homebrew/etc/vulkan/icd.d/MoltenVK_icd.json",
                "/usr/local/etc/vulkan/icd.d/MoltenVK_icd.json",
            })
            {
                if (!string.IsNullOrWhiteSpace(candidate) && fileExists(candidate))
                {
                    environment["VK_ICD_FILENAMES"] = candidate;
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(currentSdlVulkanLibrary))
        {
            environment["SDL_VULKAN_LIBRARY"] = currentSdlVulkanLibrary;
        }
        else if (executableDirectory is not null)
        {
            var bundledMoltenVk = Path.Combine(executableDirectory, "libMoltenVK.dylib");
            if (fileExists(bundledMoltenVk))
            {
                // SDL's Vulkan loader does not infer this sibling dylib from
                // DYLD_LIBRARY_PATH when the launcher is opened by Finder.
                // The release package deliberately carries the exact library
                // beside both processes, so bind it at the native boundary.
                environment["SDL_VULKAN_LIBRARY"] = bundledMoltenVk;
            }
        }

        return environment;
    }

    private static void AddPathList(List<string> paths, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var path in value.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddDistinct(paths, path);
        }
    }

    private static void AddDistinct(List<string> paths, string path)
    {
        if (!paths.Contains(path, StringComparer.Ordinal))
        {
            paths.Add(path);
        }
    }
}
