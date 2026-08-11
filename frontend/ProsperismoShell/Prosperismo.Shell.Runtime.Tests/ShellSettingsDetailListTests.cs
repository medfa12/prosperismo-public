// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI;
using Prosperismo.GUI.Controls;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class ShellSettingsDetailListTests
{
    [Fact]
    public void NativeCatalogContainsEveryEmulatorSettingExactlyOnce()
    {
        var ids = ShellSettingsDetailList.Tabs
            .SelectMany(tab => tab.Rows)
            .Select(row => row.ItemId)
            .Where(id => id.StartsWith("emulator.", StringComparison.Ordinal))
            .OrderBy(id => id)
            .ToArray();

        var expected = new[]
        {
            ShellEmulatorSettingIds.ScreenResolution,
            ShellEmulatorSettingIds.VblankFrequency,
            ShellEmulatorSettingIds.VulkanValidation,
            ShellEmulatorSettingIds.ShaderValidation,
            ShellEmulatorSettingIds.ShaderOptimization,
            ShellEmulatorSettingIds.ShaderLogDirection,
            ShellEmulatorSettingIds.ShaderLogFolder,
            ShellEmulatorSettingIds.CommandBufferDump,
            ShellEmulatorSettingIds.CommandBufferDumpFolder,
            ShellEmulatorSettingIds.PrintfDirection,
            ShellEmulatorSettingIds.PrintfOutputFile,
            ShellEmulatorSettingIds.ProfilerDirection,
            ShellEmulatorSettingIds.RenderDoc,
            ShellEmulatorSettingIds.NggRectlistDraw,
        }.OrderBy(id => id).ToArray();

        Assert.Equal(expected, ids);
        Assert.DoesNotContain(ShellSettingsDetailList.Tabs, tab => tab.TabId is
            "id_prosperismo_logging" or "id_prosperismo_environment");
    }

    [Fact]
    public void CyclesPreserveNativeContractValues()
    {
        Assert.Equal(EmulatorResolution.R1920X1080,
            ShellSettingsDetailList.CycleScreenResolution(EmulatorResolution.R1280X720));
        Assert.Equal(120, ShellSettingsDetailList.CycleVblankFrequency(60));
        Assert.Equal(ShaderOptimizationMode.None,
            ShellSettingsDetailList.CycleShaderOptimization(ShaderOptimizationMode.Performance));
        Assert.Equal(EmulatorOutputDirection.File,
            ShellSettingsDetailList.CycleOutputDirection(EmulatorOutputDirection.Console));
        Assert.Equal(EmulatorProfilerDirection.Network,
            ShellSettingsDetailList.CycleProfilerDirection(EmulatorProfilerDirection.None));
    }

    [Fact]
    public void NativeInteractionsRaiseOneEventAndPublishStableChangedId()
    {
        var list = new ShellSettingsDetailList();
        var events = 0;
        list.EmulatorSettingChanged += (_, _) => events++;

        Assert.True(list.ActivateSetting(ShellEmulatorSettingIds.VulkanValidation));

        Assert.False(list.VulkanValidation);
        Assert.Equal(ShellEmulatorSettingIds.VulkanValidation, list.LastChangedEmulatorSettingId);
        Assert.Equal(1, events);
    }

    [Fact]
    public void FocusNavigationDoesNotReplayToggleAnimation()
    {
        var list = new ShellSettingsDetailList();

        Assert.Equal(0, list.VisibleToggleTransitionCount);

        list.MoveHorizontal(1);
        list.MoveVertical(1);

        Assert.Equal(0, list.VisibleToggleTransitionCount);

        Assert.True(list.ActivateSetting("id_discord_presence"));
        Assert.Equal(1, list.VisibleToggleTransitionCount);
    }

    [Fact]
    public void EnumerableSettingOpensWithoutMutatingBackendValue()
    {
        var list = new ShellSettingsDetailList { VblankFrequency = 60 };
        var events = 0;
        list.EmulatorSettingChanged += (_, _) => events++;

        Assert.True(list.OpenChoiceForSetting(ShellEmulatorSettingIds.VblankFrequency));

        Assert.True(list.IsChoicePopupOpen);
        Assert.Equal(ShellEmulatorSettingIds.VblankFrequency, list.ActiveChoiceItemId);
        Assert.Equal(1, list.SelectedChoiceIndex);
        Assert.Equal(new[] { "30 Hz", "60 Hz", "120 Hz", "144 Hz", "240 Hz", "360 Hz" },
            list.ActiveChoiceLabels);
        Assert.Equal(60, list.VblankFrequency);
        Assert.Equal(0, events);
    }

    [Fact]
    public void PopupSelectionCommitsExactlyOnceAndBackOnlyClosesPopup()
    {
        var list = new ShellSettingsDetailList { VblankFrequency = 60 };
        var changes = 0;
        var backs = 0;
        list.EmulatorSettingChanged += (_, _) => changes++;
        list.BackRequested += (_, _) => backs++;

        Assert.True(list.OpenChoiceForSetting(ShellEmulatorSettingIds.VblankFrequency));
        list.MoveVertical(1);
        list.ActivateSelected();

        Assert.False(list.IsChoicePopupOpen);
        Assert.Equal(120, list.VblankFrequency);
        Assert.Equal(ShellEmulatorSettingIds.VblankFrequency, list.LastChangedEmulatorSettingId);
        Assert.Equal(1, changes);

        Assert.True(list.OpenChoiceForSetting(ShellEmulatorSettingIds.VblankFrequency));
        list.RequestBack();

        Assert.False(list.IsChoicePopupOpen);
        Assert.Equal(0, backs);
        Assert.Equal(120, list.VblankFrequency);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void PopupNavigationClampsAtNativeListEdges()
    {
        var list = new ShellSettingsDetailList { ScreenResolution = EmulatorResolution.R1280X720 };

        Assert.True(list.OpenChoiceForSetting(ShellEmulatorSettingIds.ScreenResolution));
        list.MoveVertical(-1);
        Assert.Equal(0, list.SelectedChoiceIndex);
        list.MoveVertical(1);
        list.MoveVertical(1);
        Assert.Equal(1, list.SelectedChoiceIndex);
    }

    [Fact]
    public void DependentOutputRowsExposeDisabledStateUntilTheirOwnerIsEnabled()
    {
        var list = new ShellSettingsDetailList();

        Assert.False(list.IsSettingEnabled(ShellEmulatorSettingIds.ShaderLogFolder));
        Assert.Equal("Disabled (output is not File)",
            list.GetDisplayValue(ShellEmulatorSettingIds.ShaderLogFolder));
        Assert.False(list.IsSettingEnabled(ShellEmulatorSettingIds.CommandBufferDumpFolder));
        Assert.Equal("Disabled", list.GetDisplayValue(ShellEmulatorSettingIds.CommandBufferDumpFolder));

        list.ShaderLogDirection = EmulatorOutputDirection.File;
        list.CommandBufferDump = true;
        list.PrintfDirection = EmulatorOutputDirection.File;

        Assert.True(list.IsSettingEnabled(ShellEmulatorSettingIds.ShaderLogFolder));
        Assert.True(list.IsSettingEnabled(ShellEmulatorSettingIds.CommandBufferDumpFolder));
        Assert.True(list.IsSettingEnabled(ShellEmulatorSettingIds.PrintfOutputFile));
    }

    [Fact]
    public void EnabledPathRowsRequestHostPickerByStableId()
    {
        var list = new ShellSettingsDetailList
        {
            ShaderLogDirection = EmulatorOutputDirection.File,
        };
        string? requested = null;
        list.EmulatorTextSettingRequested += id => requested = id;

        Assert.True(list.ActivateSetting(ShellEmulatorSettingIds.ShaderLogFolder));

        Assert.Equal(ShellEmulatorSettingIds.ShaderLogFolder, requested);
        Assert.Equal(ShellEmulatorSettingIds.ShaderLogFolder, list.LastChangedEmulatorSettingId);
    }

    [Fact]
    public void SettingsSnapshotRoundTripsAllNativeFields()
    {
        var source = new EmulatorSettings
        {
            ScreenResolution = EmulatorResolution.R1920X1080,
            VblankFrequency = 144,
            VulkanValidation = false,
            ShaderValidation = false,
            ShaderOptimization = ShaderOptimizationMode.Size,
            ShaderLogDirection = EmulatorOutputDirection.File,
            ShaderLogFolder = "shader-output",
            CommandBufferDump = true,
            CommandBufferDumpFolder = "buffers",
            PrintfDirection = EmulatorOutputDirection.File,
            PrintfOutputFile = "guest.txt",
            ProfilerDirection = EmulatorProfilerDirection.Network,
            RenderDoc = true,
            NggRectlistDraw = false,
        };
        var list = new ShellSettingsDetailList();

        list.SetEmulatorSettings(source);

        Assert.Equal(source, list.GetEmulatorSettings());
    }
}
