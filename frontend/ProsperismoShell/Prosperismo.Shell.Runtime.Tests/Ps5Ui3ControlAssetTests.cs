// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.Controls;
using Prosperismo.GUI.SystemAssets;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class Ps5Ui3ControlAssetTests
{
    [Fact]
    public void DefinitionsDescribeAllRecoveredControlFamilies()
    {
        Assert.Equal(8, Ps5Ui3ControlAssets.Definitions.Count);
        Assert.Equal("image_switch_base", Ps5Ui3ControlAssets.Definitions[Ps5Ui3ControlAsset.SwitchBase].EntryName);
        Assert.Equal("image_progressbar_light", Ps5Ui3ControlAssets.Definitions[Ps5Ui3ControlAsset.ProgressBarLight].EntryName);
        Assert.Equal("image_busy_indicator_square", Ps5Ui3ControlAssets.Definitions[Ps5Ui3ControlAsset.BusyIndicatorSquare].EntryName);
        Assert.Equal("image_busy_indicator_horizontal", Ps5Ui3ControlAssets.Definitions[Ps5Ui3ControlAsset.BusyIndicatorHorizontal].EntryName);
    }

    [Fact]
    public void ReusableControlsKeepTheirIndependentStateContracts()
    {
        var toggle = new Ps5ToggleSwitch { IsOn = true };
        var progress = new Ps5ProgressBar { Value = 1.25, IsIndeterminate = true };
        var spinner = new Ps5BusyIndicator { Kind = Ps5BusyIndicatorKind.Horizontal, IsActive = false };

        Assert.True(toggle.IsOn);
        Assert.True(toggle.IsToggleEnabled);
        Assert.Equal(1.25, progress.Value);
        Assert.True(progress.IsIndeterminate);
        Assert.Equal(Ps5BusyIndicatorKind.Horizontal, spinner.Kind);
        Assert.False(spinner.IsActive);
    }

    [Fact]
    public void ToggleOnlyAnimatesAnExplicitStateChange()
    {
        var toggle = new Ps5ToggleSwitch();

        toggle.SetState(true, animate: false);

        Assert.True(toggle.IsOn);
        Assert.Equal(1, toggle.VisualProgress);
        Assert.False(toggle.IsTransitionRunning);

        toggle.SetState(false, animate: true);

        Assert.False(toggle.IsOn);
        Assert.Equal(1, toggle.VisualProgress);
        Assert.True(toggle.IsTransitionRunning);

        toggle.SetState(false, animate: false);

        Assert.Equal(0, toggle.VisualProgress);
        Assert.False(toggle.IsTransitionRunning);
    }

    [Fact]
    public void PackagedBuildLoadsEveryControlTexture()
    {
        AvaloniaBitmapTestHost.EnsureInitialized();
        foreach (var asset in Enum.GetValues<Ps5Ui3ControlAsset>())
        {
            Assert.NotNull(Ps5Ui3ControlAssets.TryGet(asset));
        }
    }
}
