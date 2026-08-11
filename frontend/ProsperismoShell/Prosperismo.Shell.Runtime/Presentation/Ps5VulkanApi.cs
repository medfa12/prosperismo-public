// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace Prosperismo.Libs.Presentation;

/// <summary>Creates the Vulkan dispatch table without relying on a login
/// shell's DYLD_LIBRARY_PATH. Homebrew installs the loader and MoltenVK outside
/// macOS's default dyld search roots, while Silk's stock macOS names do not
/// include those absolute prefixes.</summary>
internal static class Ps5VulkanApi
{
    internal const string BundledLoaderFileName = "libMoltenVK.dylib";
    private const string AppleSiliconLoader = "/opt/homebrew/lib/libvulkan.dylib";
    private const string IntelHomebrewLoader = "/usr/local/lib/libvulkan.dylib";
    private const string AppleSiliconIcd = "/opt/homebrew/etc/vulkan/icd.d/MoltenVK_icd.json";
    private const string IntelHomebrewIcd = "/usr/local/etc/vulkan/icd.d/MoltenVK_icd.json";

    internal static Vk Create()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return Vk.GetApi();
        }

        ConfigureMoltenVkIcd();
        var candidates = LoaderCandidates(AppContext.BaseDirectory);
        return new Vk(Vk.CreateDefaultContext(candidates));
    }

    /// <summary>
    /// Returns loader candidates in shipping order. A packaged app must use
    /// its sibling universal MoltenVK library before consulting developer-only
    /// Homebrew Vulkan loaders.
    /// </summary>
    internal static string[] LoaderCandidates(string baseDirectory) =>
    [
        Path.Combine(baseDirectory, BundledLoaderFileName),
        AppleSiliconLoader,
        IntelHomebrewLoader,
        "libvulkan.dylib",
        "libvulkan.1.dylib",
    ];

    /// <summary>
    /// The Khronos loader needs portability enumeration to expose MoltenVK as
    /// an ICD. Directly loading the packaged MoltenVK implementation does not
    /// advertise that loader extension and already exposes its Metal device.
    /// </summary>
    internal static bool RequiresPortabilityEnumeration(string baseDirectory) =>
        OperatingSystem.IsMacOS() &&
        !File.Exists(Path.Combine(baseDirectory, BundledLoaderFileName));

    private static void ConfigureMoltenVkIcd()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VK_ICD_FILENAMES")))
        {
            return;
        }

        var manifest = File.Exists(AppleSiliconIcd)
            ? AppleSiliconIcd
            : File.Exists(IntelHomebrewIcd)
                ? IntelHomebrewIcd
                : null;
        if (manifest is not null)
        {
            Environment.SetEnvironmentVariable("VK_ICD_FILENAMES", manifest);
        }
    }
}
