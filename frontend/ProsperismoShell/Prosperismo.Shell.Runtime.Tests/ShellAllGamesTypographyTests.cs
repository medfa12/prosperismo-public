// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Media;
using Prosperismo.GUI.Controls;
using Prosperismo.GUI.Ps5Home;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class ShellAllGamesTypographyTests
{
    [Fact]
    public void ShellFamilyIsTheBundledOpenResource()
    {
        Assert.Equal("Fira Sans", Ps5FontLibrary.OpenFamilyName);
        Assert.Equal(
            "avares://Prosperismo.Shell/Assets/Fonts#Fira Sans",
            Ps5FontLibrary.OpenFamilyResource);
        var family = Ps5FontLibrary.TryGet(Ps5FontFace.Light);
        Assert.NotNull(family);
        Assert.Equal(Ps5FontLibrary.OpenFamilyName, family!.Name);
    }

    [Theory]
    [InlineData("300", Ps5FontFace.Light)]
    [InlineData("400", Ps5FontFace.Roman)]
    [InlineData("500", Ps5FontFace.Medium)]
    [InlineData("600", Ps5FontFace.SemiBold)]
    [InlineData("700", Ps5FontFace.Bold)]
    [InlineData("regular", Ps5FontFace.Roman)]
    [InlineData("semi-bold", Ps5FontFace.SemiBold)]
    [InlineData("unknown", Ps5FontFace.Light)]
    public void FontWeightTokensSelectConcreteOpenFaces(string token, Ps5FontFace expected)
    {
        Assert.Equal(expected, Ps5FontLibrary.FaceForWeight(token));
    }

    [Fact]
    public void FontFacesMapToNonSyntheticAvaloniaWeights()
    {
        Assert.Equal(FontWeight.Light, Ps5FontLibrary.WeightOf(Ps5FontFace.Light));
        Assert.Equal(FontWeight.Normal, Ps5FontLibrary.WeightOf(Ps5FontFace.Roman));
        Assert.Equal(FontWeight.Medium, Ps5FontLibrary.WeightOf(Ps5FontFace.Medium));
        Assert.Equal(FontWeight.SemiBold, Ps5FontLibrary.WeightOf(Ps5FontFace.SemiBold));
        Assert.Equal(FontWeight.Bold, Ps5FontLibrary.WeightOf(Ps5FontFace.Bold));
        Assert.Equal(FontStyle.Italic, Ps5FontLibrary.StyleOf(Ps5FontFace.BoldItalic));
    }

    [Fact]
    public void LibraryUsesRecoveredUi3TokenScaleInsteadOfLegacyGuesses()
    {
        Assert.Equal(Ps5FontScale.SizeLarge, ShellAllGames.SizeLarge);
        Assert.Equal(Ps5FontScale.SizeNormal, ShellAllGames.SizeNormal);
        Assert.Equal(Ps5FontScale.SizeXSmall, ShellAllGames.SizeXSmall);
        Assert.Equal(Ps5FontScale.Size2XSmall, ShellAllGames.Size2XSmall);
        Assert.Equal(Ps5FontScale.Size3XSmall, ShellAllGames.Size3XSmall);

        Assert.Equal(36, ShellAllGames.SizeLarge);
        Assert.Equal(30, ShellAllGames.SizeNormal);
        Assert.Equal(24, ShellAllGames.SizeXSmall);
        Assert.Equal(21, ShellAllGames.Size2XSmall);
        Assert.Equal(18, ShellAllGames.Size3XSmall);
    }
}
