// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.Controls;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.GUI.SystemAssets;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class PackagedSystemIconTests
{
    [Fact]
    public void StandaloneNavIconsAreBundled()
    {
        foreach (var destination in Enum.GetValues<ShellSystemDestination>())
        {
            var id = ShellNavBand.IconIdFor(destination);
            if (id is not null)
            {
                Assert.Contains("iconid_" + id, Ps5BundledIconLibrary.IconIds);
            }
        }
    }

    [Fact]
    public void StandaloneSettingsCategoryIconsAreBundled()
    {
        foreach (var category in ShellSettingsCategoryList.Categories)
        {
            Assert.NotNull(category.IconId);
            Assert.Contains("iconid_" + category.IconId, Ps5BundledIconLibrary.IconIds);
        }
    }

    [Fact]
    public void StandaloneDesktopFunctionIconsAreBundled()
    {
        foreach (var id in ShellIcons.VectorOnlyEntryNames.Values)
        {
            Assert.Contains(id, Ps5BundledIconLibrary.IconIds);
        }
    }
}
