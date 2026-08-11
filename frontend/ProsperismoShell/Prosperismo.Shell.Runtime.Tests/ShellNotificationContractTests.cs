// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.Controls;
using Prosperismo.GUI.SystemAssets;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class ShellNotificationContractTests
{
    [Fact]
    public void ReplacesOnlyWithinTheFirmwareIdentityScope()
    {
        var current = new ShellNotificationRequest
        {
            UserId = "user-a",
            NotificationId = "notification-1",
            BundleName = "downloads",
            UseCaseId = "progress",
        };

        Assert.True(ShellNotificationHost.CanReplace(current, new ShellNotificationRequest
        {
            UserId = "user-a",
            NotificationId = "notification-1",
        }));
        Assert.False(ShellNotificationHost.CanReplace(current, new ShellNotificationRequest
        {
            UserId = "user-b",
            NotificationId = "notification-1",
            ReplaceAlways = true,
        }));
        Assert.True(ShellNotificationHost.CanReplace(current, new ShellNotificationRequest
        {
            UserId = "user-a",
            BundleName = "downloads",
            ReplaceAlways = true,
        }));
        Assert.False(ShellNotificationHost.CanReplace(current, new ShellNotificationRequest
        {
            UserId = "user-a",
            BundleName = "uploads",
            ReplaceAlways = true,
        }));
    }

    [Fact]
    public void ExposesRecoveredToastGeometryAndTiming()
    {
        Assert.Equal(652, ShellNotificationView.ToastMaxWidth);
        Assert.Equal(784, ShellNotificationView.LargeTextToastMaxWidth);
        Assert.Equal(112, ShellNotificationView.CollapsedHeaderMinHeight);
        Assert.Equal(72, ShellNotificationView.CtaHeight);
        Assert.Equal(200, ShellNotificationView.ResizeDuration.TotalMilliseconds);
        Assert.Equal(150, ShellNotificationView.ContentFadeDuration.TotalMilliseconds);
        Assert.Equal(3500, ShellNotificationView.InAppDefaultTimeout.TotalMilliseconds);
        Assert.Equal("image_popup_dialog_base", Ps5Ui3Chrome.EntryName(Ps5Ui3ChromeAsset.PopupDialogBase));
        Assert.Equal("image_button_base", Ps5Ui3Chrome.EntryName(Ps5Ui3ChromeAsset.ButtonBase));
    }

    [Fact]
    public void NotificationBackendUsesFirmwareStatesReplacementAndQueryLimit()
    {
        ShellNotificationHistory.ResetForTests();
        try
        {
            var first = ShellNotificationHistory.Record(new ShellNotificationRequest
            {
                NotificationId = "download-1",
                UserId = "local-user",
                Surface = ShellNotificationSurface.Informative,
                PrimaryText = "Downloading",
            });
            Assert.NotNull(first);
            Assert.Equal(ShellNotificationHistoryState.New, first.State);

            var replacement = ShellNotificationHistory.Record(new ShellNotificationRequest
            {
                NotificationId = "download-1",
                UserId = "local-user",
                Surface = ShellNotificationSurface.Informative,
                PrimaryText = "Download complete",
            });
            Assert.Equal(first.Id, replacement?.Id);
            Assert.Single(ShellNotificationHistory.Snapshot());

            Assert.True(ShellNotificationHistory.MarkSeen(first.Id));
            Assert.Equal(
                ShellNotificationHistoryState.Seen,
                ShellNotificationHistory.Snapshot().Single().State);
            Assert.True(ShellNotificationHistory.MarkRead(first.Id));
            Assert.Equal(
                ShellNotificationHistoryState.Read,
                ShellNotificationHistory.Snapshot().Single().State);

            Assert.Null(ShellNotificationHistory.Record(new ShellNotificationRequest
            {
                Surface = ShellNotificationSurface.InApp,
                PrimaryText = "Utility pill",
            }));

            for (var index = 0; index <= ShellNotificationHistory.QueryLimit; index++)
            {
                ShellNotificationHistory.Record(new ShellNotificationRequest
                {
                    NotificationId = $"notification-{index}",
                    UserId = "local-user",
                    PrimaryText = $"Notification {index}",
                });
            }

            Assert.Equal(ShellNotificationHistory.QueryLimit, ShellNotificationHistory.Snapshot().Count);
        }
        finally
        {
            ShellNotificationHistory.ResetForTests();
        }
    }

    [Fact]
    public void NotificationListComposerCarriesListOnlyGeometryAndState()
    {
        var request = new ShellNotificationRequest
        {
            PrimaryText = "Download complete",
            SecondaryText = "The title is ready to play.",
        };
        var timestamp = new DateTimeOffset(2026, 8, 11, 7, 18, 0, TimeSpan.Zero);
        var rows = ShellNotificationListComposer.Compose(
        [
            new ShellNotificationHistoryEntry(
                "notification-1",
                request,
                timestamp,
                timestamp,
                ShellNotificationHistoryState.New),
        ], isDoNotDisturb: false);

        Assert.Equal(2, rows.Count);
        var dnd = rows[0];
        Assert.IsType<ShellNotificationDoNotDisturbTag>(dnd.Tag);
        Assert.False(dnd.ToggleValue);
        Assert.True(dnd.ShowSeparator);
        var row = rows[1];
        Assert.Equal(ShellNotificationListComposer.ListRowMinHeight, row.MinHeight);
        Assert.True(row.IsNew);
        Assert.Equal("The title is ready to play.", row.SecondaryText);
        Assert.Equal("notification-1", Assert.IsType<ShellNotificationHistoryTag>(row.Tag).EntryId);
    }

    [Fact]
    public void NotificationListKeepsDndFirstAndAnimatesOnlyAnExplicitChange()
    {
        var empty = ShellNotificationListComposer.Compose([], isDoNotDisturb: false);
        var dnd = Assert.Single(empty);

        Assert.Equal(ShellNotificationListComposer.DoNotDisturbIndex, 0);
        Assert.Equal(0, ShellNotificationListComposer.InitialSelectedIndex(0));
        Assert.Equal(1, ShellNotificationListComposer.InitialSelectedIndex(1));
        Assert.False(dnd.ShowSeparator);
        Assert.Null(dnd.ToggleAnimationStartValue);

        var changed = Assert.Single(ShellNotificationListComposer.Compose(
            [],
            isDoNotDisturb: true,
            animateDoNotDisturbFrom: false));
        Assert.True(changed.ToggleValue);
        Assert.False(changed.ToggleAnimationStartValue);
        Assert.Equal("This will stay on until you log out of your PS5.", changed.SecondaryText);
    }

    [Theory]
    [InlineData(ShellNotificationSurface.Informative, false)]
    [InlineData(ShellNotificationSurface.Interactive, false)]
    [InlineData(ShellNotificationSurface.InApp, true)]
    [InlineData(ShellNotificationSurface.Persistent, true)]
    public void DoNotDisturbMutesOnlyNotificationDbBackedPopups(
        ShellNotificationSurface surface,
        bool expected)
    {
        Assert.Equal(
            expected,
            ShellNotificationBroker.ShouldPresentPopup(
                new ShellNotificationRequest { Surface = surface },
                isDoNotDisturb: true));
    }

    [Fact]
    public void DoNotDisturbStillRecordsMutedNotifications()
    {
        ShellNotificationHistory.ResetForTests();
        var delivered = new List<ShellNotificationRequest>();
        EventHandler<ShellNotificationRequest> handler = (_, request) => delivered.Add(request);
        ShellNotificationBroker.Posted += handler;
        ShellNotificationBroker.DoNotDisturb = true;
        try
        {
            ShellNotificationBroker.Post(new ShellNotificationRequest
            {
                NotificationId = "muted-notification",
                UserId = "local-user",
                Surface = ShellNotificationSurface.Informative,
                PrimaryText = "Recorded without a popup",
            });

            Assert.Empty(delivered);
            Assert.Equal("muted-notification", ShellNotificationHistory.Snapshot().Single().Request.NotificationId);

            ShellNotificationBroker.Post(new ShellNotificationRequest
            {
                Surface = ShellNotificationSurface.InApp,
                PrimaryText = "Host utility pill",
            });
            Assert.Single(delivered);
            Assert.Single(ShellNotificationHistory.Snapshot());
        }
        finally
        {
            ShellNotificationBroker.DoNotDisturb = false;
            ShellNotificationBroker.Posted -= handler;
            ShellNotificationHistory.ResetForTests();
        }
    }

    [Fact]
    public void FunctionPanelDoesNotReplayDndToggleDuringRefreshOrNavigation()
    {
        AvaloniaBitmapTestHost.EnsureInitialized();
        var panel = new ShellFunctionPanel
        {
            Items = ShellNotificationListComposer.Compose([], isDoNotDisturb: false),
        };
        panel.ApplyTemplate();

        var mounted = Assert.Single(panel.RenderedToggles);
        Assert.False(mounted.IsTransitionRunning);

        panel.SetSelectedIndex(0);
        panel.MoveFocus(1);
        Assert.Same(mounted, Assert.Single(panel.RenderedToggles));
        Assert.False(mounted.IsTransitionRunning);

        panel.Items = ShellNotificationListComposer.Compose([], isDoNotDisturb: false);
        Assert.False(Assert.Single(panel.RenderedToggles).IsTransitionRunning);

        panel.Items = ShellNotificationListComposer.Compose(
            [],
            isDoNotDisturb: true,
            animateDoNotDisturbFrom: false);
        var changed = Assert.Single(panel.RenderedToggles);
        Assert.True(changed.IsTransitionRunning);
        changed.SetState(true, animate: false);
    }

    [Fact]
    public void DetailAndOptionsComposerMatchesFirmwareNavigationGrammar()
    {
        var timestamp = new DateTimeOffset(2026, 8, 11, 7, 18, 0, TimeSpan.Zero);
        var entry = new ShellNotificationHistoryEntry(
            "notification-1",
            new ShellNotificationRequest
            {
                PrimaryText = "Download complete",
                SecondaryText = "The title is ready to play.",
                DetailText = "Your download has finished.",
                Actions = [new ShellNotificationAction("library", "View game library")],
            },
            timestamp,
            timestamp,
            ShellNotificationHistoryState.Seen);

        var detail = ShellNotificationPanelComposer.ComposeDetail(entry);
        Assert.Equal(ShellNotificationPanelComposer.GenericDetailCardMinHeight, detail[0].MinHeight);
        Assert.Equal("Your download has finished.", detail[0].SecondaryText);
        var action = Assert.IsType<ShellNotificationDetailActionTag>(detail[1].Tag);
        Assert.Equal(("notification-1", "library"), (action.EntryId, action.ActionId));
        Assert.Equal(ShellNotificationView.CtaHeight, detail[1].ExactHeight);

        var options = ShellNotificationPanelComposer.ComposeListOptions([entry], entry.Id);
        Assert.Equal(
            ["Delete Notification", "Delete All Notifications", "Notification Settings"],
            options.Select(row => row.Title).ToArray());
        Assert.Equal(
            ShellNotificationPanelCommand.DeleteFocused,
            Assert.IsType<ShellNotificationPanelCommandTag>(options[0].Tag).Command);

        var detailOptions = ShellNotificationPanelComposer.ComposeDetailOptions(entry.Id);
        Assert.Equal(
            ["Delete Notification", "Notification Settings"],
            detailOptions.Select(row => row.Title).ToArray());

        var confirm = ShellNotificationPanelComposer.ComposeDeleteAllConfirm();
        Assert.Equal("Delete All Notifications", confirm[0].Title);
        Assert.Equal("Cancel", confirm[1].Title);
        Assert.Equal("Delete", confirm[2].Title);
        Assert.Equal(484, ShellFunctionPanelMetrics.HeightFor(confirm));
    }

    [Fact]
    public void DeleteAllUsesTheFirmwareDeletedStateAndHidesEveryRow()
    {
        ShellNotificationHistory.ResetForTests();
        try
        {
            ShellNotificationHistory.Record(new ShellNotificationRequest
            {
                NotificationId = "one",
                UserId = "local-user",
                PrimaryText = "One",
            });
            ShellNotificationHistory.Record(new ShellNotificationRequest
            {
                NotificationId = "two",
                UserId = "local-user",
                PrimaryText = "Two",
            });

            Assert.Equal(2, ShellNotificationHistory.MarkAllDeleted());
            Assert.Empty(ShellNotificationHistory.Snapshot());
            Assert.Equal(0, ShellNotificationHistory.MarkAllDeleted());
        }
        finally
        {
            ShellNotificationHistory.ResetForTests();
        }
    }

    [Fact]
    public void NotificationHistorySurvivesRestartWithoutStaleProcessObjects()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"prosperismo-notification-history-{Guid.NewGuid():N}.json");
        ShellNotificationHistory.ResetForTests();
        try
        {
            ShellNotificationHistory.ConfigurePersistence(path);
            var callbackInvocations = 0;
            var first = ShellNotificationHistory.Record(new ShellNotificationRequest
            {
                NotificationId = "download-1",
                UserId = "local-user",
                BundleName = "Prosperismo.Shell",
                Surface = ShellNotificationSurface.Interactive,
                PrimaryText = "Download complete",
                SecondaryText = "The title is ready to play.",
                DetailText = "Your download has finished.",
                Actions =
                [
                    new ShellNotificationAction(
                        "library",
                        "View game library",
                        () => callbackInvocations++),
                ],
            });
            Assert.NotNull(first);
            Assert.True(ShellNotificationHistory.MarkSeen(first.Id));

            ShellNotificationHistory.ReloadPersistenceForTests();

            var restored = Assert.Single(ShellNotificationHistory.Snapshot());
            Assert.Equal(first.Id, restored.Id);
            Assert.Equal(ShellNotificationHistoryState.Seen, restored.State);
            Assert.Equal("Download complete", restored.Request.PrimaryText);
            Assert.Equal("Your download has finished.", restored.Request.DetailText);
            Assert.Empty(restored.Request.Actions);
            Assert.Null(restored.Request.Icon);
            Assert.Equal(0, callbackInvocations);

            var second = ShellNotificationHistory.Record(new ShellNotificationRequest
            {
                NotificationId = "download-2",
                UserId = "local-user",
                PrimaryText = "Second notification",
            });
            Assert.NotNull(second);
            Assert.NotEqual(first.Id, second.Id);
        }
        finally
        {
            ShellNotificationHistory.ResetForTests();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void DialogStackStillClosesOnTheAuthoredCanvas()
    {
        Assert.Equal(1920, ShellDialog.DesignWidth);
        Assert.Equal(1080, ShellDialog.DesignHeight);
        Assert.Equal(1312, ShellDialog.BodyWidth);
        Assert.Equal(1080,
            ShellDialog.TopMargin
            + 694
            + ShellDialog.ButtonRowGap
            + ShellDialog.ButtonRowHeight
            + ShellDialog.BottomMargin);
    }
}
