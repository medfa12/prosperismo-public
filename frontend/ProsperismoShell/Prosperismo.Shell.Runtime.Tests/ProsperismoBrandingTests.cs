// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.Ps5Home;
using Prosperismo.GUI.SystemAssets;
using Prosperismo.Logging;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class ProsperismoBrandingTests
{
    [Fact]
    public void BuildBannerIdentifiesProsperismo()
    {
        Assert.StartsWith("Prosperismo ", BuildInfo.Banner, StringComparison.Ordinal);
        Assert.DoesNotContain("SharpEmu", BuildInfo.Banner, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "github.com/medfa12/prosperismo",
            BuildInfo.Banner,
            StringComparison.OrdinalIgnoreCase);
    }
}
