// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.Controls;
using Prosperismo.GUI.SystemAssets;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class Ps5Ui3ChromeTests
{
    [Theory]
    [InlineData(Ps5Ui3ChromeAsset.ButtonBase, "image_button_base")]
    [InlineData(Ps5Ui3ChromeAsset.EmphasisButtonBase, "image_emphasisbutton_base")]
    [InlineData(Ps5Ui3ChromeAsset.MenuBase, "image_menu_base")]
    [InlineData(Ps5Ui3ChromeAsset.PopupDialogBase, "image_popup_dialog_base")]
    public void ChromeAssetsNameTheAuthoritativeUi3Entries(
        Ps5Ui3ChromeAsset asset,
        string expectedEntryName)
    {
        Assert.Equal(expectedEntryName, Ps5Ui3Chrome.EntryName(asset));
    }

    [Fact]
    public void ContextMenuUsesMenuBaseWithoutChangingItsComposedRows()
    {
        Assert.Equal(Ps5Ui3ChromeAsset.MenuBase, ShellContextMenu.ChromeAsset);
        Assert.Equal(16, ShellContextMenu.ChromeRadius);
        Assert.InRange(ShellContextMenu.ChromeOpacity, 0, 1);
    }

    [Fact]
    public void PackagedBuildLoadsEveryChromeTexture()
    {
        AvaloniaBitmapTestHost.EnsureInitialized();
        foreach (var asset in Enum.GetValues<Ps5Ui3ChromeAsset>())
        {
            Assert.NotNull(Ps5Ui3Chrome.TryGet(asset));
        }
    }
}
