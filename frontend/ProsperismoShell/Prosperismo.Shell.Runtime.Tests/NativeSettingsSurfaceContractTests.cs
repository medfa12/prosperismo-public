// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using Prosperismo.GUI;
using Prosperismo.GUI.Controls;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class NativeSettingsSurfaceContractTests
{
    private static readonly string[] DesktopControlNames =
    [
        "EmulatorResolutionBox",
        "EmulatorVblankFrequencyBox",
        "EmulatorVulkanValidationToggle",
        "EmulatorShaderValidationToggle",
        "EmulatorShaderOptimizationBox",
        "EmulatorShaderLogDirectionBox",
        "EmulatorShaderLogFolderBox",
        "EmulatorCommandBufferDumpToggle",
        "EmulatorCommandBufferDumpFolderBox",
        "EmulatorPrintfDirectionBox",
        "EmulatorPrintfOutputFileBox",
        "EmulatorProfilerDirectionBox",
        "EmulatorRenderDocToggle",
        "EmulatorNggRectlistDrawToggle",
    ];

    [Fact]
    public void DesktopCompiledSurfaceContainsEveryNativeSettingControl()
    {
        var fields = typeof(MainWindow).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        foreach (var name in DesktopControlNames)
        {
            Assert.Contains(fields, field => field.Name == name);
        }
    }

    [Fact]
    public void BothPresentationsUseTheCanonicalSettingsType()
    {
        Assert.Equal(
            typeof(EmulatorSettings),
            typeof(GuiSettings).GetProperty(nameof(GuiSettings.GlobalEmulatorSettings))?.PropertyType);
        Assert.Equal(
            typeof(EmulatorSettings),
            typeof(ShellSettingsDetailList).GetMethod(nameof(ShellSettingsDetailList.GetEmulatorSettings))?.ReturnType);
        Assert.NotNull(typeof(DesktopLibrarySurface).GetEvent(nameof(DesktopLibrarySurface.GameSettingsRequested)));
    }

    [Fact]
    public void MainWindowHasNoLegacyBackendContractState()
    {
        Assert.Null(typeof(MainWindow).GetField(
            "_emulatorCliContract",
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Equal("prosperismo_emulator", EmulatorLaunchContract.NativeExecutableStem);
    }
}
