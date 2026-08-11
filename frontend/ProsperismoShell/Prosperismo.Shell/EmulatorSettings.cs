// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI;

/// <summary>
/// Window sizes exposed by the retained Kyty launcher contract.
/// This is deliberately separate from the launcher's internal render scale.
/// </summary>
public enum EmulatorResolution
{
    R1280X720,
    R1920X1080,
}

public enum ShaderOptimizationMode
{
    None,
    Size,
    Performance,
}

public enum EmulatorOutputDirection
{
    Silent,
    Console,
    File,
}

public enum EmulatorProfilerDirection
{
    None,
    Network,
}

/// <summary>
/// Canonical Prosperismo native-emulator settings. Defaults intentionally
/// match the original Kyty launcher rather than the lower-level CLI defaults.
/// </summary>
public sealed record EmulatorSettings
{
    public EmulatorResolution ScreenResolution { get; set; } = EmulatorResolution.R1280X720;

    public int VblankFrequency { get; set; } = 60;

    public bool VulkanValidation { get; set; } = true;

    public bool ShaderValidation { get; set; } = true;

    public ShaderOptimizationMode ShaderOptimization { get; set; } = ShaderOptimizationMode.Performance;

    public EmulatorOutputDirection ShaderLogDirection { get; set; } = EmulatorOutputDirection.Silent;

    public string ShaderLogFolder { get; set; } = "_Shaders";

    public bool CommandBufferDump { get; set; }

    public string CommandBufferDumpFolder { get; set; } = "_Buffers";

    public EmulatorOutputDirection PrintfDirection { get; set; } = EmulatorOutputDirection.Silent;

    public string PrintfOutputFile { get; set; } = "_prosperismo.txt";

    public EmulatorProfilerDirection ProfilerDirection { get; set; } = EmulatorProfilerDirection.None;

    public bool RenderDoc { get; set; }

    public bool NggRectlistDraw { get; set; } = true;

    public EmulatorSettings Copy() => this with { };
}

public static class EmulatorSettingsContract
{
    public const int MinimumVblankFrequency = 30;
    public const int MaximumVblankFrequency = 360;

    public static (int Width, int Height) Dimensions(EmulatorResolution resolution) => resolution switch
    {
        EmulatorResolution.R1280X720 => (1280, 720),
        EmulatorResolution.R1920X1080 => (1920, 1080),
        _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Unsupported emulator resolution."),
    };

    public static void Validate(EmulatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ = Dimensions(settings.ScreenResolution);

        if (settings.VblankFrequency is < MinimumVblankFrequency or > MaximumVblankFrequency)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.VblankFrequency),
                settings.VblankFrequency,
                $"Vblank frequency must be between {MinimumVblankFrequency} and {MaximumVblankFrequency}.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(settings.ShaderLogFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.CommandBufferDumpFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.PrintfOutputFile);
    }
}
