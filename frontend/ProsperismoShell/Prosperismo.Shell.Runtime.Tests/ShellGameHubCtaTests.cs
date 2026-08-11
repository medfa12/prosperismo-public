// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.Controls;
using Prosperismo.Libs.Presentation;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class ShellGameHubCtaTests
{
    [Fact]
    public void IdleLocalTitleComposesPlayAsTheOnlyVisibleCta()
    {
        var model = ShellGameHubCtaComposer.Compose(
            new ShellGameHubHostCapabilities(CanLaunch: true, CanConfigureGame: false));

        Assert.Equal(ShellGameHubActionKind.Play, model.PrimaryAction?.Kind);
        Assert.Equal(1, model.VisibleOrdinaryActionCount);
        Assert.Empty(model.OverflowActions);
        Assert.False(model.HasOverflow);
    }

    [Fact]
    public void ExtraCurrentHostActionMovesToOverflowInsteadOfCreatingSecondPrimaryCta()
    {
        var model = ShellGameHubCtaComposer.Compose(
            new ShellGameHubHostCapabilities(CanLaunch: true, CanConfigureGame: true));

        Assert.Equal(ShellGameHubCtaComposer.MaxVisibleOrdinaryActions, model.VisibleOrdinaryActionCount);
        Assert.Equal(ShellGameHubActionKind.Play, model.PrimaryAction?.Kind);
        var overflow = Assert.Single(model.OverflowActions);
        Assert.Equal(ShellGameHubActionKind.ConfigureGame, overflow.Kind);
        Assert.True(model.HasOverflow);
    }

    [Fact]
    public void OverflowIsNotAPermanentPlayCompanion()
    {
        var playOnly = ShellGameHubCtaComposer.Compose(
            new ShellGameHubHostCapabilities(CanLaunch: true, CanConfigureGame: false));
        var noActions = ShellGameHubCtaComposer.Compose(
            new ShellGameHubHostCapabilities(CanLaunch: false, CanConfigureGame: false));

        Assert.False(playOnly.HasOverflow);
        Assert.Null(noActions.PrimaryAction);
        Assert.False(noActions.HasOverflow);
    }

    [Fact]
    public void CtaGeometryIsConsumedFromTheNpxs40033Contract()
    {
        var contract = Npxs40087ShellContract.GameHub;

        Assert.Equal(contract.MaximumVisibleOrdinaryActions, ShellGameHubCtaComposer.MaxVisibleOrdinaryActions);
        Assert.Equal(contract.CtaContainerWidth, ShellGameHubCta.ContainerWidth);
        Assert.Equal(contract.CtaContainerHeight, ShellGameHubCta.ContainerHeight);
        Assert.Equal(contract.ButtonHeight, ShellGameHubCta.ButtonHeight);
        Assert.Equal(contract.ButtonGap, ShellGameHubCta.ButtonGap);
        Assert.Equal(contract.CondensedButtonWidth, ShellGameHubCta.OverflowButtonWidth);
        Assert.Equal(contract.PrimaryButtonWidthWithOverflow, ShellGameHubCta.PrimaryButtonWidth(hasOverflow: true));
        Assert.Equal(contract.CtaContainerWidth, ShellGameHubCta.PrimaryButtonWidth(hasOverflow: false));
        Assert.Equal(contract.ButtonHeight / 2.0, ShellGameHubCta.ButtonCornerRadius);

        var cta = new ShellGameHubCta();
        Assert.Equal(contract.CtaContainerWidth, cta.Width);
        Assert.Equal(contract.CtaContainerHeight, cta.Height);
    }

    [Fact]
    public void GameHubPlacementStaysInTheFixed1920CanvasBeforeTheViewboxTransformsIt()
    {
        var contract = Npxs40087ShellContract.GameHub;

        Assert.False(ShellGameHubLayout.ShowsHeaderInEmbeddedHome);
        Assert.Equal(contract.DesignWidth, ShellGameHubLayout.DesignWidth);
        Assert.Equal(contract.CtaContainerLeft, ShellGameHubLayout.CtaCanvasOrigin.X);
        Assert.Equal(contract.CtaContainerTop, ShellGameHubLayout.CtaCanvasOrigin.Y);
        Assert.Equal(contract.CtaContainerLeft, ShellGameHubLayout.CtaOriginInHubSurface.X);
        Assert.Equal(contract.CtaContainerTop - ShellHubMetrics.MarginTop,
            ShellGameHubLayout.CtaOriginInHubSurface.Y);
        Assert.Equal(172, ShellGameHubLayout.ConsoleMeasuredLogoCanvasOrigin.X);
        Assert.Equal(530, ShellGameHubLayout.ConsoleMeasuredLogoCanvasOrigin.Y);
        Assert.Equal(402, ShellGameHubLayout.ConsoleMeasuredLogoOriginInHubSurface.Y);
    }

    [Fact]
    public void IndependentLogoUsesTheContractCapAndMissingLogoFallbackRule()
    {
        var contract = Npxs40087ShellContract.GameHub;

        Assert.Equal(contract.LogoMaximumWidth, ShellGameHubTitleLogo.MaximumWidth);
        Assert.Equal(contract.LogoMaximumHeight, ShellGameHubTitleLogo.MaximumHeight);
        Assert.Equal(contract.DisplayNameFallbackMaximumLines,
            ShellGameHubTitleLogo.DisplayNameFallbackMaximumLines);
        Assert.True(contract.LogoPreservesAspectRatio);
    }

    [Fact]
    public void CtaActivationAndOverflowUseSeparateTypedEvents()
    {
        var cta = new ShellGameHubCta
        {
            Model = ShellGameHubCtaComposer.Compose(
                new ShellGameHubHostCapabilities(CanLaunch: true, CanConfigureGame: true)),
        };
        ShellGameHubActionKind? primary = null;
        IReadOnlyList<ShellGameHubAction>? overflow = null;
        cta.PrimaryActionRequested += (_, e) => primary = e.Action.Kind;
        cta.OverflowRequested += (_, e) => overflow = e.Actions;

        cta.SetSelectedIndex(0);
        cta.ActivateSelected();
        cta.SetSelectedIndex(1);
        cta.ActivateSelected();

        Assert.Equal(ShellGameHubActionKind.Play, primary);
        Assert.NotNull(overflow);
        Assert.Equal(ShellGameHubActionKind.ConfigureGame, Assert.Single(overflow!).Kind);
    }

    [Fact]
    public void RestingHubControlsStayVisibleBeforeTheyReceiveActiveFocus()
    {
        var resting = ShellGameHubPresentationState.Resolve(
            isSonyPresentation: true,
            hasSelectedTitle: true,
            visibleActionCount: 2,
            isActive: false);
        var active = ShellGameHubPresentationState.Resolve(
            isSonyPresentation: true,
            hasSelectedTitle: true,
            visibleActionCount: 2,
            isActive: true);

        Assert.True(resting.IsVisible);
        Assert.False(resting.IsInteractive);
        Assert.True(active.IsVisible);
        Assert.True(active.IsInteractive);
    }

    [Theory]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 0)]
    public void HubControlsStayAbsentWithoutSonyTitleActions(
        bool isSonyPresentation,
        bool hasSelectedTitle,
        int visibleActionCount)
    {
        var state = ShellGameHubPresentationState.Resolve(
            isSonyPresentation,
            hasSelectedTitle,
            visibleActionCount,
            isActive: true);

        Assert.False(state.IsVisible);
        Assert.False(state.IsInteractive);
    }

    [Fact]
    public void DownEntersTheHubAndUpReturnsToTheRememberedHomeTitle()
    {
        const string home = "tile-item-focus-layer";
        const string hub = "prosperismo-game-hub-cta";
        var graph = new ShellFocusGraph();
        graph.Add(new ShellFocusRegion(home)
        {
            ItemCount = 4,
            LastFocusedItem = 2,
            CanMoveDown = true,
            DownCandidate = hub,
        });
        graph.Add(new ShellFocusRegion(hub)
        {
            ItemCount = 1,
            CanMoveUp = true,
            UpCandidate = home,
        });
        graph.SetActive(home);

        Assert.True(graph.TryMove(ShellFocusDirection.Down, out var entered, out var ctaIndex));
        Assert.Equal(hub, entered);
        Assert.Equal(0, ctaIndex);
        Assert.True(graph.TryMove(ShellFocusDirection.Up, out var exited, out var titleIndex));
        Assert.Equal(home, exited);
        Assert.Equal(2, titleIndex);
    }

    [Fact]
    public void TransitionUsesTheRecoveredHomeLiftForHubFocus()
    {
        var transition = new ShellHubTransition { ManualClock = true };

        transition.Open();
        transition.SettleNow();

        Assert.True(transition.IsOpen);
        Assert.Equal(-166, transition.HomeTranslateY);
        Assert.Equal(0, transition.SwitcherOpacity);

        transition.Close();
        transition.SettleNow();
        Assert.False(transition.IsOpen);
        Assert.Equal(0, transition.HomeTranslateY);
    }
}
