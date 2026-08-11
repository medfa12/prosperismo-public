// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Prosperismo.GUI.SystemAssets;

namespace Prosperismo.GUI.Controls;

/// <summary>The notification surfaces recovered from NPXS40003.</summary>
public enum ShellNotificationSurface
{
    Informative,
    Interactive,
    InApp,
    Persistent,
}

/// <summary>The nine states exported by InteractiveToastBasic.</summary>
public enum ShellInteractiveToastState
{
    Collapsed = 1,
    CollapsedToExpanded = 2,
    Expanded = 3,
    ExpandedToDetailView = 4,
    DetailView = 5,
    DetailViewToExpanded = 6,
    CollapsedSingle = 7,
    CollapsedToDetailViewSingle = 8,
    DetailViewSingle = 9,
}

public enum ShellNotificationPlacement
{
    Top,
    Center,
    Bottom,
}

/// <summary>One model-provided CTA. NPXS40003 creates one native button for
/// every entry in <c>viewData.actions</c>; there is no fixed button count.</summary>
public sealed class ShellNotificationAction
{
    public ShellNotificationAction(
        string id,
        string text,
        Action? onPress = null,
        bool closeControlCenter = false)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Text = text ?? string.Empty;
        OnPress = onPress;
        CloseControlCenter = closeControlCenter;
    }

    public string Id { get; }
    public string Text { get; }
    public Action? OnPress { get; }
    public bool CloseControlCenter { get; }
}

/// <summary>Shared payload for the shell overlay and notification-list routes.</summary>
public sealed class ShellNotificationRequest
{
    public string? NotificationId { get; init; }
    public string? UserId { get; init; }
    public string? BundleName { get; init; }
    public string? UseCaseId { get; init; }
    public string? PrimaryText { get; init; }
    public string? SecondaryText { get; init; }
    public string? TertiaryText { get; init; }
    public string? DetailText { get; init; }
    public IImage? Icon { get; init; }
    public IImage? SecondIcon { get; init; }
    public ShellNotificationSurface Surface { get; init; } = ShellNotificationSurface.Informative;
    public ShellNotificationPlacement Placement { get; init; } = ShellNotificationPlacement.Top;
    public bool LargeText { get; init; }
    public bool ReplaceAlways { get; init; }
    public bool Persistent { get; init; }
    public TimeSpan? Timeout { get; init; }
    public IReadOnlyList<ShellNotificationAction> Actions { get; init; } = Array.Empty<ShellNotificationAction>();
}

/// <summary>NotificationDb2's four states used by NPXS40003.</summary>
public enum ShellNotificationHistoryState
{
    New,
    Seen,
    Read,
    Deleted,
}

/// <summary>One backend notification-list record.</summary>
public sealed record ShellNotificationHistoryEntry(
    string Id,
    ShellNotificationRequest Request,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ShellNotificationHistoryState State);

/// <summary>Typed row payload round-tripped by the Control Center list.</summary>
public sealed record ShellNotificationHistoryTag(string EntryId);

/// <summary>The always-first NPXS40003 Do Not Disturb switch row.</summary>
public sealed record ShellNotificationDoNotDisturbTag;

/// <summary>Navigation surfaces owned by NPXS40003's notification function control.</summary>
public enum ShellNotificationPanelScreen
{
    List,
    Detail,
    ListOptions,
    DetailOptions,
    DeleteAllConfirm,
}

/// <summary>Commands supplied to the native options/detail navigation layer.</summary>
public enum ShellNotificationPanelCommand
{
    DeleteFocused,
    DeleteAllConfirm,
    OpenSettings,
    DeleteDetail,
    CancelDeleteAll,
    ConfirmDeleteAll,
}

/// <summary>Typed command payload round-tripped by the notification panel.</summary>
public sealed record ShellNotificationPanelCommandTag(
    ShellNotificationPanelCommand Command,
    string? EntryId = null);

/// <summary>Typed CTA payload rendered by NotificationDetailCard.</summary>
public sealed record ShellNotificationDetailActionTag(string EntryId, string ActionId);

/// <summary>Builds NPXS40003's List display surface from backend records.</summary>
public static class ShellNotificationListComposer
{
    // Collapsed header 112 + list-only top/bottom additions 8/10.
    public const double ListRowMinHeight = 130;

    public const int DoNotDisturbIndex = 0;

    public static IReadOnlyList<ShellFunctionPanelItem> Compose(
        IReadOnlyList<ShellNotificationHistoryEntry> history,
        bool isDoNotDisturb,
        bool? animateDoNotDisturbFrom = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        var rows = new List<ShellFunctionPanelItem>(history.Count + 1)
        {
            new("Do Not Disturb", tag: new ShellNotificationDoNotDisturbTag())
            {
                SecondaryText = isDoNotDisturb
                    ? "This will stay on until you log out of your PS5."
                    : "Mute all pop-ups.",
                ToggleValue = isDoNotDisturb,
                ToggleAnimationStartValue = animateDoNotDisturbFrom,
                ShowSeparator = history.Count > 0,
            },
        };

        rows.AddRange(history.Select(static entry => new ShellFunctionPanelItem(
            entry.Request.PrimaryText ?? entry.Request.SecondaryText ?? "Notification",
            tag: new ShellNotificationHistoryTag(entry.Id))
        {
            SecondaryText = entry.Request.SecondaryText,
            TimestampText = entry.CreatedAt.ToLocalTime().ToString("t"),
            IsNew = entry.State == ShellNotificationHistoryState.New,
            LeadingImage = entry.Request.Icon,
            MinHeight = ListRowMinHeight,
        }));
        return rows;
    }

    /// <summary>
    /// NotificationListContent focuses the first notification when one exists;
    /// an empty list leaves focus on the Do Not Disturb switch.
    /// </summary>
    public static int InitialSelectedIndex(int historyCount) => historyCount > 0 ? 1 : 0;
}

/// <summary>
/// Composes the model-owned portions of NPXS40003's detail, options and
/// delete-all screens. The surrounding 652-unit panel remains owned by the
/// </summary>
public static class ShellNotificationPanelComposer
{
    // Host composition minima for generic content. Specialized native detail
    public const double GenericDetailCardMinHeight = 210;
    public const double DeleteAllMessageHostHeight = 220;
    public const double DetailCtaWidthRatio =
        (ShellNotificationView.ToastMaxWidth - (2 * ShellNotificationView.CtaMarginHorizontal)) /
        ShellNotificationView.ToastMaxWidth;

    public static IReadOnlyList<ShellFunctionPanelItem> ComposeDetail(
        ShellNotificationHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var request = entry.Request;
        var rows = new List<ShellFunctionPanelItem>
        {
            new(
                request.PrimaryText ?? request.SecondaryText ?? "Notification",
                tag: new ShellNotificationHistoryTag(entry.Id))
            {
                SecondaryText = request.DetailText ?? request.SecondaryText,
                TimestampText = entry.CreatedAt.ToLocalTime().ToString("t"),
                LeadingImage = request.Icon,
                MinHeight = GenericDetailCardMinHeight,
                SecondaryWrap = true,
            },
        };

        rows.AddRange(request.Actions.Select(action => new ShellFunctionPanelItem(
            action.Text,
            tag: new ShellNotificationDetailActionTag(entry.Id, action.Id))
        {
            ContentCentered = true,
            FocusWidthRatio = DetailCtaWidthRatio,
            ShowSeparator = false,
            ExactHeight = ShellNotificationView.CtaHeight,
        }));
        return rows;
    }

    public static IReadOnlyList<ShellFunctionPanelItem> ComposeListOptions(
        IReadOnlyList<ShellNotificationHistoryEntry> history,
        string? focusedEntryId)
    {
        ArgumentNullException.ThrowIfNull(history);
        var rows = new List<ShellFunctionPanelItem>();
        if (history.Any(entry => string.Equals(entry.Id, focusedEntryId, StringComparison.Ordinal)))
        {
            rows.Add(new ShellFunctionPanelItem(
                "Delete Notification",
                tag: new ShellNotificationPanelCommandTag(
                    ShellNotificationPanelCommand.DeleteFocused,
                    focusedEntryId)));
        }

        if (history.Count > 0)
        {
            rows.Add(new ShellFunctionPanelItem(
                "Delete All Notifications",
                tag: new ShellNotificationPanelCommandTag(
                    ShellNotificationPanelCommand.DeleteAllConfirm))
            {
                ShowSeparator = false,
            });
        }

        rows.Add(new ShellFunctionPanelItem(
            "Notification Settings",
            tag: new ShellNotificationPanelCommandTag(
                ShellNotificationPanelCommand.OpenSettings))
        {
            ShowSeparator = false,
        });
        return rows;
    }

    public static IReadOnlyList<ShellFunctionPanelItem> ComposeDetailOptions(string entryId) =>
    [
        new ShellFunctionPanelItem(
            "Delete Notification",
            tag: new ShellNotificationPanelCommandTag(
                ShellNotificationPanelCommand.DeleteDetail,
                entryId)),
        new ShellFunctionPanelItem(
            "Notification Settings",
            tag: new ShellNotificationPanelCommandTag(
                ShellNotificationPanelCommand.OpenSettings))
        {
            ShowSeparator = false,
        },
    ];

    public static IReadOnlyList<ShellFunctionPanelItem> ComposeDeleteAllConfirm() =>
    [
        new ShellFunctionPanelItem("Delete All Notifications")
        {
            SecondaryText = "Delete all notifications from your list? You won't be able to undo this action.",
            MinHeight = DeleteAllMessageHostHeight,
            IsEnabled = false,
            DimWhenDisabled = false,
            ContentCentered = true,
            ShowSeparator = false,
            IsBold = true,
            SecondaryLineHeight = 38,
            SecondaryWrap = true,
            ContentHorizontalMargin = 48,
        },
        new ShellFunctionPanelItem(
            "Cancel",
            tag: new ShellNotificationPanelCommandTag(
                ShellNotificationPanelCommand.CancelDeleteAll))
        {
            ContentCentered = true,
            FocusWidthRatio = 0.72,
            ShowSeparator = false,
            ExactHeight = ShellNotificationView.CtaHeight,
            BottomMargin = 24,
        },
        new ShellFunctionPanelItem(
            "Delete",
            tag: new ShellNotificationPanelCommandTag(
                ShellNotificationPanelCommand.ConfirmDeleteAll))
        {
            ContentCentered = true,
            FocusWidthRatio = 0.72,
            ShowSeparator = false,
            ExactHeight = ShellNotificationView.CtaHeight,
        },
    ];
}

/// <summary>
/// Durable host backend for NPXS40003's NotificationDb2 list contract. The
/// console queries at most 100 rows and transitions them New -> Seen -> Read
/// or Deleted. In-app and persistent utility pills are not database rows.
/// </summary>
public static class ShellNotificationHistory
{
    public const int QueryLimit = 100;

    private static readonly object Gate = new();
    private static readonly List<ShellNotificationHistoryEntry> Entries = [];
    private static long _nextLocalId;
    private static ShellNotificationHistoryStore? _store;

    public static event EventHandler? Changed;

    /// <summary>
    /// Enables durable list history. Repeated configuration with the same path
    /// is idempotent so a window recreation cannot discard live callbacks.
    /// </summary>
    public static void ConfigurePersistence(string path)
    {
        var store = new ShellNotificationHistoryStore(path);
        lock (Gate)
        {
            if (string.Equals(_store?.FilePath, store.FilePath, StringComparison.Ordinal))
            {
                return;
            }

            _store = store;
            LoadFromStoreLocked();
        }
    }

    public static IReadOnlyList<ShellNotificationHistoryEntry> Snapshot()
    {
        lock (Gate)
        {
            return Entries
                .Where(static entry => entry.State != ShellNotificationHistoryState.Deleted)
                .OrderByDescending(static entry => entry.CreatedAt)
                .Take(QueryLimit)
                .ToArray();
        }
    }

    public static int NewCount
    {
        get
        {
            lock (Gate)
            {
                return Entries.Count(static entry =>
                    entry.State == ShellNotificationHistoryState.New);
            }
        }
    }

    public static ShellNotificationHistoryEntry? Record(ShellNotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Surface is ShellNotificationSurface.InApp or ShellNotificationSurface.Persistent)
        {
            return null;
        }

        ShellNotificationHistoryEntry entry;
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            var index = Entries.FindIndex(existing => SameIdentity(existing.Request, request));
            if (index >= 0)
            {
                var existing = Entries[index];
                entry = existing with
                {
                    Request = request,
                    UpdatedAt = now,
                    State = ShellNotificationHistoryState.New,
                };
                Entries[index] = entry;
            }
            else
            {
                entry = new ShellNotificationHistoryEntry(
                    $"local-{++_nextLocalId}",
                    request,
                    now,
                    now,
                    ShellNotificationHistoryState.New);
                Entries.Add(entry);
            }

            TrimToQueryLimit();
            PersistLocked();
        }

        Changed?.Invoke(null, EventArgs.Empty);
        return entry;
    }

    public static bool MarkSeen(string id) => ChangeState(
        id,
        static state => state == ShellNotificationHistoryState.New
            ? ShellNotificationHistoryState.Seen
            : state);

    public static bool MarkRead(string id) => ChangeState(
        id,
        static state => state is ShellNotificationHistoryState.New or ShellNotificationHistoryState.Seen
            ? ShellNotificationHistoryState.Read
            : state);

    public static bool MarkDeleted(string id) => ChangeState(
        id,
        static _ => ShellNotificationHistoryState.Deleted);

    public static int MarkAllDeleted()
    {
        var changed = 0;
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index < Entries.Count; index++)
            {
                if (Entries[index].State == ShellNotificationHistoryState.Deleted)
                {
                    continue;
                }

                Entries[index] = Entries[index] with
                {
                    State = ShellNotificationHistoryState.Deleted,
                    UpdatedAt = now,
                };
                changed++;
            }

            if (changed > 0)
            {
                PersistLocked();
            }
        }

        if (changed > 0)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
        return changed;
    }

    public static void MarkAllNewSeen()
    {
        var changed = false;
        lock (Gate)
        {
            for (var index = 0; index < Entries.Count; index++)
            {
                if (Entries[index].State != ShellNotificationHistoryState.New)
                {
                    continue;
                }

                Entries[index] = Entries[index] with
                {
                    State = ShellNotificationHistoryState.Seen,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                changed = true;
            }

            if (changed)
            {
                PersistLocked();
            }
        }

        if (changed)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static bool SameIdentity(
        ShellNotificationRequest left,
        ShellNotificationRequest right) =>
        !string.IsNullOrEmpty(left.NotificationId) &&
        !string.IsNullOrEmpty(right.NotificationId) &&
        string.Equals(left.UserId, right.UserId, StringComparison.Ordinal) &&
        string.Equals(left.NotificationId, right.NotificationId, StringComparison.Ordinal);

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Entries.Clear();
            _nextLocalId = 0;
            _store = null;
        }
    }

    internal static void ReloadPersistenceForTests()
    {
        lock (Gate)
        {
            LoadFromStoreLocked();
        }
    }

    private static bool ChangeState(
        string id,
        Func<ShellNotificationHistoryState, ShellNotificationHistoryState> transition)
    {
        var changed = false;
        lock (Gate)
        {
            var index = Entries.FindIndex(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            var next = transition(Entries[index].State);
            if (next != Entries[index].State)
            {
                Entries[index] = Entries[index] with
                {
                    State = next,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                changed = true;
                PersistLocked();
            }
        }

        if (changed)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
        return changed;
    }

    private static void TrimToQueryLimit()
    {
        while (Entries.Count > QueryLimit)
        {
            var oldest = Entries
                .Select((entry, index) => (entry, index))
                .OrderBy(static pair => pair.entry.CreatedAt)
                .First();
            Entries.RemoveAt(oldest.index);
        }
    }

    private static void LoadFromStoreLocked()
    {
        Entries.Clear();
        _nextLocalId = 0;
        if (_store is null)
        {
            return;
        }

        Entries.AddRange(_store.Load(QueryLimit));
        foreach (var entry in Entries)
        {
            const string prefix = "local-";
            if (entry.Id.StartsWith(prefix, StringComparison.Ordinal) &&
                long.TryParse(entry.Id.AsSpan(prefix.Length), out var localId))
            {
                _nextLocalId = Math.Max(_nextLocalId, localId);
            }
        }
    }

    private static void PersistLocked() => _store?.Save(Entries);
}

public sealed class ShellNotificationActionEventArgs : EventArgs
{
    public ShellNotificationActionEventArgs(string actionId) => ActionId = actionId;
    public string ActionId { get; }
}

/// <summary>
/// The process-local counterpart of NotificationClient: callers post a model,
/// while the attached host owns presentation and replacement.
/// </summary>
public static class ShellNotificationBroker
{
    private static readonly object Gate = new();
    private static readonly Queue<ShellNotificationRequest> Pending = new();
    private static EventHandler<ShellNotificationRequest>? _posted;
    private static int _doNotDisturb;

    /// <summary>
    /// Session-scoped counterpart of NPXS40003's native DND mode. The console
    /// explicitly clears this at logout, so it is intentionally not persisted.
    /// </summary>
    public static bool DoNotDisturb
    {
        get => Volatile.Read(ref _doNotDisturb) != 0;
        set => Volatile.Write(ref _doNotDisturb, value ? 1 : 0);
    }

    public static event EventHandler<ShellNotificationRequest>? Posted
    {
        add
        {
            if (value is null)
            {
                return;
            }

            ShellNotificationRequest[] pending;
            lock (Gate)
            {
                _posted += value;
                pending = Pending.ToArray();
                Pending.Clear();
            }

            foreach (var request in pending)
            {
                if (ShouldPresentPopup(request, DoNotDisturb))
                {
                    value.Invoke(null, request);
                }
            }
        }
        remove
        {
            lock (Gate)
            {
                _posted -= value;
            }
        }
    }

    public static void Post(ShellNotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ShellNotificationHistory.Record(request);
        if (!ShouldPresentPopup(request, DoNotDisturb))
        {
            return;
        }

        EventHandler<ShellNotificationRequest>? handler;
        lock (Gate)
        {
            handler = _posted;
            if (handler is null)
            {
                Pending.Enqueue(request);
                return;
            }
        }

        // Recheck after leaving the queue lock so a DND change racing this post
        // cannot surface a popup after the user has enabled suppression.
        if (ShouldPresentPopup(request, DoNotDisturb))
        {
            handler.Invoke(null, request);
        }
    }

    /// <summary>
    /// DND mutes NotificationDb-backed pop-ups, while app-owned utility pills
    /// and persistent system surfaces remain available to the host.
    /// </summary>
    internal static bool ShouldPresentPopup(
        ShellNotificationRequest request,
        bool isDoNotDisturb) =>
        !isDoNotDisturb || request.Surface is ShellNotificationSurface.InApp or
            ShellNotificationSurface.Persistent;
}

/// <summary>
/// NPXS40003 notification content. All dimensions below are values from
/// <c>@rnps-ppr/notification-view-template</c> or
/// <c>@rnps-ppr/ui-shared-utilities-notification</c>. The native NPXS40011
/// popup anchor is not present in the recovered bundles, so placement belongs
/// to <see cref="ShellNotificationHost"/>, not this card.
/// </summary>
public sealed class ShellNotificationView : ContentControl
{
    public const double ToastMaxWidth = 652;
    public const double LargeTextToastMaxWidth = 784;
    public const double ToastMinWidth = 80;
    public const double ToastMinHeight = 74;
    public const double InformativeMaxHeight = 242;
    public const double InteractiveMaxHeight = 690;
    public const double ContentMarginHorizontal = 24;
    public const double IconSize = 64;
    public const double IconMarginVertical = 24;
    public const double IconToTextGap = 20;
    public const double DualIconGap = 8;
    public const double TextMarginVertical = 16;
    public const double TextGap = 4;
    public const double CollapsedHeaderMinHeight = 112;
    public const double CtaHeight = 72;
    public const double CtaMarginHorizontal = 32;
    public const double InAppIconSize = 40;
    public const double InAppPaddingLeft = 20;
    public const double InAppPaddingRight = 24;
    public const double InAppPaddingVertical = 16;
    public const double InAppIconGap = 16;
    public const double PersistentMinHeight = 64;
    public const double PersistentMaxWidth = 784;
    public const double PersistentMaxHeight = 308;
    public static readonly TimeSpan ResizeDuration = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan ContentFadeDuration = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan InAppEnterDuration = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan InAppDefaultTimeout = TimeSpan.FromMilliseconds(3500);
    public static readonly TimeSpan InAppExitDuration = TimeSpan.FromMilliseconds(200);

    private static readonly IBrush SurfaceBrush = new SolidColorBrush(Color.Parse("#F211141A"));
    private static readonly IBrush PersistentBrush = new SolidColorBrush(Color.Parse("#11141A"));
    private static readonly IBrush ButtonBrush = new SolidColorBrush(Color.Parse("#14FFFFFF"));
    private static readonly IBrush TextBrush = Brushes.White;
    private static readonly IBrush SecondaryTextBrush = new SolidColorBrush(Color.Parse("#B3FFFFFF"));

    private readonly Border _plate;
    private readonly Ps5Ui3ChromePlate _chrome;
    private readonly StackPanel _content;
    private readonly StackPanel _detail;
    private readonly StackPanel _actions;
    private readonly List<Control> _actionPlates = new();
    private CancellationTokenSource? _transitionCancellation;
    private ShellNotificationRequest? _request;
    private int _focusedActionIndex = -1;

    public ShellNotificationView()
    {
        Focusable = true;
        IsTabStop = true;

        _content = new StackPanel();
        _detail = new StackPanel { IsVisible = false, Opacity = 0 };
        _actions = new StackPanel { Spacing = 8 };
        _detail.Children.Add(_actions);

        var stack = new StackPanel();
        stack.Children.Add(_content);
        stack.Children.Add(_detail);

        _chrome = new Ps5Ui3ChromePlate
        {
            Asset = Ps5Ui3ChromeAsset.PopupDialogBase,
            FallbackBrush = Brushes.Transparent,
            SliceCornerRadius = 16,
            AssetOpacity = 0,
            Child = stack,
        };

        _plate = new Border
        {
            Background = SurfaceBrush,
            CornerRadius = new CornerRadius(16),
            ClipToBounds = true,
            MinWidth = ToastMinWidth,
            MinHeight = ToastMinHeight,
            Child = _chrome,
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = HeightProperty,
                    Duration = ResizeDuration,
                    Easing = ShellMotion.EaseOutBreeze,
                },
            },
        };

        Content = _plate;
        PointerPressed += OnPointerPressed;
        EffectiveViewportChanged += (_, _) => PushActionFocus();
        AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (InteractiveState == ShellInteractiveToastState.Expanded)
                    {
                        PushActionFocus();
                    }
                },
                DispatcherPriority.Render);
    }

    public event EventHandler<ShellNotificationActionEventArgs>? ActionInvoked;
    public event EventHandler? DismissRequested;

    public ShellInteractiveToastState InteractiveState { get; private set; } =
        ShellInteractiveToastState.Collapsed;

    public ShellNotificationRequest? Request
    {
        get => _request;
        set
        {
            _request = value;
            Rebuild();
        }
    }

    public async Task SetExpandedAsync(bool expanded, int delayMilliseconds = 0)
    {
        if (_request?.Surface != ShellNotificationSurface.Interactive)
        {
            return;
        }

        if ((expanded && InteractiveState == ShellInteractiveToastState.Expanded)
            || (!expanded && InteractiveState == ShellInteractiveToastState.Collapsed))
        {
            return;
        }

        _transitionCancellation?.Cancel();
        _transitionCancellation = new CancellationTokenSource();
        var token = _transitionCancellation.Token;

        InteractiveState = expanded
            ? ShellInteractiveToastState.CollapsedToExpanded
            : ShellInteractiveToastState.DetailViewToExpanded;

        try
        {
            if (delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds, token).ConfigureAwait(true);
            }

            if (expanded)
            {
                _detail.IsVisible = true;
                _detail.Opacity = 0;
                _plate.Height = ComputeExpandedHeight();
                await Task.Delay(ContentFadeDuration, token).ConfigureAwait(true);
                _detail.Opacity = 1;
                await Task.Delay(ContentFadeDuration, token).ConfigureAwait(true);
                InteractiveState = ShellInteractiveToastState.Expanded;
                _focusedActionIndex = _actionPlates.Count == 0 ? -1 : 0;
                PushActionFocus();
            }
            else
            {
                _detail.Opacity = 0;
                await Task.Delay(ContentFadeDuration, token).ConfigureAwait(true);
                _detail.IsVisible = false;
                _plate.Height = CollapsedHeaderMinHeight;
                await Task.Delay(ResizeDuration, token).ConfigureAwait(true);
                InteractiveState = ShellInteractiveToastState.Collapsed;
                _focusedActionIndex = -1;
                ShellFocusRing.For(this)?.Release(this);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Headless visual-QA seam: lands on the same final expanded
    /// state without waiting for a wall-clock animation.</summary>
    internal void SetExpandedForCapture()
    {
        if (_request?.Surface != ShellNotificationSurface.Interactive)
        {
            return;
        }

        _detail.IsVisible = true;
        _detail.Opacity = 1;
        _plate.Height = ComputeExpandedHeight();
        InteractiveState = ShellInteractiveToastState.Expanded;
        _focusedActionIndex = _actionPlates.Count == 0 ? -1 : 0;
        PushActionFocus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _request is null)
        {
            return;
        }

        if (_request.Surface == ShellNotificationSurface.Interactive
            && InteractiveState == ShellInteractiveToastState.Collapsed
            && (e.Key == Key.Enter || e.Key == Key.Space))
        {
            _ = SetExpandedAsync(true);
            e.Handled = true;
            return;
        }

        if (_actionPlates.Count > 0 && InteractiveState == ShellInteractiveToastState.Expanded)
        {
            if (e.Key is Key.Up or Key.Left)
            {
                MoveActionFocus(-1);
                e.Handled = true;
            }
            else if (e.Key is Key.Down or Key.Right)
            {
                MoveActionFocus(1);
                e.Handled = true;
            }
            else if (e.Key is Key.Enter or Key.Space)
            {
                InvokeAction(_focusedActionIndex);
                e.Handled = true;
            }
        }

        if (e.Key is Key.Escape or Key.Back)
        {
            if (_request.Surface == ShellNotificationSurface.Interactive
                && InteractiveState == ShellInteractiveToastState.Expanded)
            {
                _ = SetExpandedAsync(false);
            }
            else
            {
                DismissRequested?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
        }
    }

    private void Rebuild()
    {
        CancelTransition();
        _content.Children.Clear();
        _actions.Children.Clear();
        _detail.Children.Clear();
        _detail.Children.Add(_actions);
        _actionPlates.Clear();
        _focusedActionIndex = -1;
        _detail.IsVisible = false;
        _detail.Opacity = 0;

        if (_request is not { } request)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        InteractiveState = ShellInteractiveToastState.Collapsed;

        switch (request.Surface)
        {
            case ShellNotificationSurface.InApp:
                BuildInApp(request);
                break;
            case ShellNotificationSurface.Persistent:
                BuildPersistent(request);
                break;
            default:
                BuildPopup(request);
                break;
        }
    }

    private void BuildPopup(ShellNotificationRequest request)
    {
        _chrome.AssetOpacity = 0.28;
        _chrome.InvalidateVisual();
        _plate.Background = SurfaceBrush;
        _plate.Width = request.LargeText ? LargeTextToastMaxWidth : ToastMaxWidth;
        _plate.MaxWidth = _plate.Width;
        _plate.MinHeight = request.Surface == ShellNotificationSurface.Interactive
            ? CollapsedHeaderMinHeight
            : ToastMinHeight;
        _plate.MaxHeight = request.Surface == ShellNotificationSurface.Interactive
            ? InteractiveMaxHeight
            : InformativeMaxHeight;
        _plate.Height = request.Surface == ShellNotificationSurface.Interactive
            ? CollapsedHeaderMinHeight
            : double.NaN;
        _plate.Padding = new Thickness(ContentMarginHorizontal, TextMarginVertical);

        _content.Children.Add(BuildHeader(request, IconSize, IconToTextGap));

        if (request.Surface != ShellNotificationSurface.Interactive)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.DetailText))
        {
            _detail.Children.Insert(0, new TextBlock
            {
                Text = request.DetailText,
                FontSize = ShellFontSize.XSmall,
                Foreground = TextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 8, 8, 24),
            });
        }

        for (int index = 0; index < request.Actions.Count; index++)
        {
            var actionIndex = index;
            var action = request.Actions[index];
            var plate = BuildActionPlate(action.Text);
            plate.PointerEntered += (_, _) =>
            {
                _focusedActionIndex = actionIndex;
                PushActionFocus();
            };
            plate.PointerReleased += (_, _) => InvokeAction(actionIndex);
            _actions.Children.Add(plate);
            _actionPlates.Add(plate);
        }

        _actions.Margin = new Thickness(
            CtaMarginHorizontal - ContentMarginHorizontal,
            0,
            CtaMarginHorizontal - ContentMarginHorizontal,
            16);
    }

    private void BuildInApp(ShellNotificationRequest request)
    {
        _chrome.AssetOpacity = 0;
        _chrome.InvalidateVisual();
        _plate.Background = request.PrimaryText is null
            ? new SolidColorBrush(Color.Parse("#0AFFFFFF"))
            : PersistentBrush;
        _plate.Width = double.NaN;
        _plate.Height = double.NaN;
        _plate.MinHeight = InAppIconSize + (2 * InAppPaddingVertical);
        _plate.MaxHeight = double.PositiveInfinity;
        _plate.MaxWidth = PersistentMaxWidth;
        _plate.Padding = new Thickness(
            InAppPaddingLeft,
            InAppPaddingVertical,
            InAppPaddingRight,
            InAppPaddingVertical);
        _content.Children.Add(BuildHeader(request, InAppIconSize, InAppIconGap, ShellFontSize.Small, 2));
    }

    private void BuildPersistent(ShellNotificationRequest request)
    {
        _chrome.AssetOpacity = 0;
        _chrome.InvalidateVisual();
        _plate.Background = PersistentBrush;
        _plate.Width = double.NaN;
        _plate.Height = double.NaN;
        _plate.MinHeight = PersistentMinHeight;
        _plate.MaxHeight = PersistentMaxHeight;
        _plate.MaxWidth = PersistentMaxWidth;
        var hasIcon = request.Icon is not null || request.SecondIcon is not null;
        _plate.Padding = new Thickness(hasIcon ? 20 : 24, 12, 24, 12);
        _content.Children.Add(BuildHeader(request, InAppIconSize, 8, ShellFontSize.XSmall, 2));
    }

    private static Control BuildHeader(
        ShellNotificationRequest request,
        double iconSize,
        double iconGap,
        double? primaryFontSize = null,
        int maxLines = 0)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = iconGap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (request.Icon is not null || request.SecondIcon is not null)
        {
            var icons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = DualIconGap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (request.Icon is { } icon)
            {
                icons.Children.Add(new Image { Source = icon, Width = iconSize, Height = iconSize });
            }
            if (request.SecondIcon is { } secondIcon)
            {
                icons.Children.Add(new Image { Source = secondIcon, Width = iconSize, Height = iconSize });
            }
            row.Children.Add(icons);
        }

        var text = new StackPanel { Spacing = TextGap, VerticalAlignment = VerticalAlignment.Center };
        AddLine(text, request.PrimaryText, primaryFontSize ?? ShellFontSize.Normal, TextBrush, maxLines);
        AddLine(text, request.SecondaryText, ShellFontSize.XSmall, SecondaryTextBrush, maxLines);
        AddLine(text, request.TertiaryText, ShellFontSize.XSmall, SecondaryTextBrush, maxLines);
        row.Children.Add(text);
        return row;
    }

    private static void AddLine(Panel panel, string? value, double fontSize, IBrush brush, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        panel.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = fontSize,
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = maxLines,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
    }

    private static Ps5Ui3ChromePlate BuildActionPlate(string text) => new()
    {
        Height = CtaHeight,
        Asset = Ps5Ui3ChromeAsset.ButtonBase,
        FallbackBrush = ButtonBrush,
        SliceCornerRadius = 16,
        AssetOpacity = 0.055,
        Child = new TextBlock
        {
            Text = text,
            FontSize = ShellFontSize.Normal,
            Foreground = TextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        },
    };

    private double ComputeExpandedHeight()
    {
        var bodyLines = Math.Max(1, (_request?.DetailText?.Length ?? 0) / 42 + 1);
        var bodyHeight = Math.Min(210, bodyLines * 34);
        var ctaHeight = _actionPlates.Count * (CtaHeight + 8);
        return Math.Min(InteractiveMaxHeight, CollapsedHeaderMinHeight + bodyHeight + ctaHeight + 40);
    }

    private void OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (_request?.Surface == ShellNotificationSurface.Interactive
            && InteractiveState == ShellInteractiveToastState.Collapsed)
        {
            Focus();
            _ = SetExpandedAsync(true);
            e.Handled = true;
        }
    }

    private void MoveActionFocus(int delta)
    {
        if (_actionPlates.Count == 0)
        {
            return;
        }

        var start = _focusedActionIndex < 0 ? 0 : _focusedActionIndex;
        var next = Math.Clamp(start + delta, 0, _actionPlates.Count - 1);
        if (next == _focusedActionIndex)
        {
            return;
        }

        _focusedActionIndex = next;
        Prosperismo.GUI.SystemAssets.ShellUiSounds.Play(
            Prosperismo.GUI.SystemAssets.UiSoundEvent.FocusMove);
        PushActionFocus();
    }

    internal void MoveControllerFocus(int delta)
    {
        if (_request?.Surface == ShellNotificationSurface.Interactive &&
            InteractiveState == ShellInteractiveToastState.Expanded)
        {
            MoveActionFocus(delta);
        }
    }

    internal void ActivateFromController()
    {
        if (_request?.Surface != ShellNotificationSurface.Interactive)
        {
            return;
        }

        if (InteractiveState == ShellInteractiveToastState.Collapsed)
        {
            _ = SetExpandedAsync(true);
        }
        else if (InteractiveState == ShellInteractiveToastState.Expanded)
        {
            InvokeAction(_focusedActionIndex);
        }
    }

    internal bool InvokeActionById(string actionId)
    {
        if (_request is null)
        {
            return false;
        }

        var index = _request.Actions
            .Select((action, actionIndex) => (action, actionIndex))
            .FirstOrDefault(pair => string.Equals(
                pair.action.Id,
                actionId,
                StringComparison.Ordinal))
            .actionIndex;
        if (index < 0 || index >= _request.Actions.Count ||
            !string.Equals(_request.Actions[index].Id, actionId, StringComparison.Ordinal))
        {
            return false;
        }

        InvokeAction(index);
        return true;
    }

    internal void BackFromController()
    {
        if (_request?.Surface == ShellNotificationSurface.Interactive &&
            InteractiveState == ShellInteractiveToastState.Expanded)
        {
            _ = SetExpandedAsync(false);
        }
        else
        {
            DismissRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void InvokeAction(int index)
    {
        if (_request is null || index < 0 || index >= _request.Actions.Count)
        {
            return;
        }

        var action = _request.Actions[index];
        Prosperismo.GUI.SystemAssets.ShellUiSounds.Play(
            Prosperismo.GUI.SystemAssets.UiSoundEvent.Enter);
        try
        {
            action.OnPress?.Invoke();
            ActionInvoked?.Invoke(this, new ShellNotificationActionEventArgs(action.Id));
        }
        catch (Exception)
        {
            // A model callback must not leave the shell's overlay wedged or
            // tear down the UI event loop.
        }
        finally
        {
            DismissRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void CancelTransition()
    {
        var cancellation = Interlocked.Exchange(ref _transitionCancellation, null);
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void PushActionFocus()
    {
        if (_focusedActionIndex < 0 || _focusedActionIndex >= _actionPlates.Count)
        {
            ShellFocusRing.For(this)?.Release(this);
            return;
        }

        var plate = _actionPlates[_focusedActionIndex];
        if (ShellFocusRing.For(this) is not { } ring
            || plate.Bounds.Width <= 0
            || plate.TransformToVisual(ring) is not { } transform)
        {
            return;
        }

        ring.Radius = 16;
        ring.Claim(this, new Rect(plate.Bounds.Size).TransformToAABB(transform));
    }
}

/// <summary>
/// Owns transient replacement, queueing, and the verified InAppToast lifetime.
/// Popup-toast screen docking remains a host policy because NPXS40011's native
/// placement code is absent from the available dump. The default 48 px inset
/// measurement.
/// </summary>
public sealed class ShellNotificationHost : Grid
{
    private readonly Queue<ShellNotificationRequest> _queue = new();
    private readonly ShellNotificationView _view;
    private CancellationTokenSource? _lifetimeCancellation;
    private ShellNotificationRequest? _current;

    public ShellNotificationHost()
    {
        ZIndex = 19_000;
        IsHitTestVisible = false;
        ClipToBounds = false;

        _view = new ShellNotificationView
        {
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 48, 48, 0),
        };
        _view.DismissRequested += (_, _) => DismissCurrent();
        Children.Add(_view);

        AttachedToVisualTree += (_, _) => ShellNotificationBroker.Posted += OnPosted;
        DetachedFromVisualTree += (_, _) => ShellNotificationBroker.Posted -= OnPosted;
    }

    public ShellNotificationRequest? Current => _current;

    /// <summary>Raised when replacement, dismissal, or dequeue changes the
    /// process-local notification currently presented.</summary>
    public event EventHandler? CurrentChanged;

    public bool IsInteractiveActive =>
        _current?.Surface == ShellNotificationSurface.Interactive && _view.IsVisible;

    public void MoveControllerFocus(int delta) => _view.MoveControllerFocus(delta);

    public void ActivateFromController() => _view.ActivateFromController();

    public bool InvokeCurrentAction(string actionId) => _view.InvokeActionById(actionId);

    public void BackFromController() => _view.BackFromController();

    public Task ExpandCurrentAsync(int delayMilliseconds = 0) =>
        _view.SetExpandedAsync(true, delayMilliseconds);

    internal void CompletePresentationForCapture()
    {
        _lifetimeCancellation?.Cancel();
        _view.IsVisible = true;
        _view.Opacity = 1;
        _view.SetExpandedForCapture();
    }

    public void Post(ShellNotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Post(request));
            return;
        }

        if (_current is null || CanReplace(_current, request))
        {
            Show(request);
        }
        else
        {
            _queue.Enqueue(request);
        }
    }

    public void DismissCurrent()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(DismissCurrent);
            return;
        }

        _lifetimeCancellation?.Cancel();
        _view.CancelTransition();
        ShellFocusRing.For(_view)?.Release(_view);
        _view.IsVisible = false;
        _view.Opacity = 0;
        _current = null;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        IsHitTestVisible = false;

        if (_queue.Count > 0)
        {
            Show(_queue.Dequeue());
        }
    }

    public static bool CanReplace(ShellNotificationRequest current, ShellNotificationRequest incoming)
    {
        if (!string.Equals(current.UserId, incoming.UserId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(incoming.NotificationId)
            && string.Equals(current.NotificationId, incoming.NotificationId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!incoming.ReplaceAlways)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(incoming.BundleName))
        {
            return string.Equals(current.BundleName, incoming.BundleName, StringComparison.Ordinal);
        }

        return string.Equals(current.UseCaseId, incoming.UseCaseId, StringComparison.Ordinal);
    }

    private void OnPosted(object? sender, ShellNotificationRequest request) => Post(request);

    private async void Show(ShellNotificationRequest request)
    {
        _lifetimeCancellation?.Cancel();
        _lifetimeCancellation = new CancellationTokenSource();
        var token = _lifetimeCancellation.Token;

        _current = request;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        _view.Request = request;
        ApplyPlacement(request.Placement);
        _view.IsVisible = true;
        _view.Opacity = 0;
        IsHitTestVisible = request.Surface == ShellNotificationSurface.Interactive;
        PlayPresentationCue(request.Surface);

        try
        {
            await FadeAsync(_view, 0, 1, request.Surface == ShellNotificationSurface.InApp
                ? ShellNotificationView.InAppEnterDuration
                : ShellNotificationView.ContentFadeDuration, token).ConfigureAwait(true);

            if (request.Surface == ShellNotificationSurface.Interactive)
            {
                _view.Focus();
            }

            var timeout = request.Timeout;
            if (request.Surface == ShellNotificationSurface.InApp && timeout is null && !request.Persistent)
            {
                timeout = ShellNotificationView.InAppDefaultTimeout;
            }

            if (timeout is null || request.Persistent || request.Surface == ShellNotificationSurface.Persistent)
            {
                return;
            }

            await Task.Delay(timeout.Value, token).ConfigureAwait(true);
            await FadeAsync(_view, 1, 0,
                request.Surface == ShellNotificationSurface.InApp
                    ? ShellNotificationView.InAppExitDuration
                    : ShellNotificationView.ContentFadeDuration,
                token).ConfigureAwait(true);
            if (!token.IsCancellationRequested)
            {
                DismissCurrent();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ApplyPlacement(ShellNotificationPlacement placement)
    {
        _view.VerticalAlignment = placement switch
        {
            ShellNotificationPlacement.Center => VerticalAlignment.Center,
            ShellNotificationPlacement.Bottom => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Top,
        };
        _view.HorizontalAlignment = placement == ShellNotificationPlacement.Top
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Center;
        _view.Margin = placement switch
        {
            ShellNotificationPlacement.Top => new Thickness(0, 48, 48, 0),
            ShellNotificationPlacement.Bottom => new Thickness(0, 0, 0, 48),
            _ => default,
        };
    }

    private static void PlayPresentationCue(ShellNotificationSurface surface)
    {
        // These two VAG entry names describe the same two notification
        // surfaces. In-app and persistent notifications have no equivalently
        // named cue in the shell RCO, so they deliberately remain silent.
        switch (surface)
        {
            case ShellNotificationSurface.Informative:
                ShellUiSounds.Play(UiSoundEvent.InformativeToast);
                break;
            case ShellNotificationSurface.Interactive:
                ShellUiSounds.Play(UiSoundEvent.InteractiveToast);
                break;
        }
    }

    private static Task FadeAsync(Control target, double from, double to, TimeSpan duration, CancellationToken token)
    {
        var animation = new Animation
        {
            Duration = duration,
            Easing = new LinearEasing(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, to) } },
            },
        };
        return animation.RunAsync(target, token);
    }
}
