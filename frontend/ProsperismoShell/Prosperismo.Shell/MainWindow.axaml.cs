// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Animation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Prosperismo.HLE.Host;
using Prosperismo.HLE.Host.Windows;
using Prosperismo.GUI.Controls;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.GUI.SystemAssets;
using Prosperismo.GUI.SystemAssets.Audio;
using Prosperismo.GUI.SystemAssets.Shell;
using Prosperismo.Libs.Presentation;
using Prosperismo.Libs.VideoOut;
using Prosperismo.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Net.Http.Headers;

namespace Prosperismo.GUI;

public partial class MainWindow : Window
{
    private const int MaxConsoleLines = 4000;
    private const int MaxConsoleLinesPerFlush = 500;
    private const double LaunchBlurRadius = 12;
    private const double BlurTransitionSeconds = 0.24;
    private const double ShellAspectRatio = 16.0 / 9.0;
    private const int ShellTargetPixelWidth = 1920;
    private const int ShellTargetPixelHeight = 1080;

    // ---- Home layout: the shell's own 1920x1080 constants ----------------
    // The home has one row of tiles and the installed titles are its tiles. That
    // row is the experience switcher, so every number it needs is already on
    // ShellTileRow and none of them are set here: 106 at rest, 168 focused, 8
    // between two resting tiles, 16 either side of the focused one, and the
    // focused left edge on SCALED_EXP_MARGIN_LEFT.
    //
    // What used to be here was the content strand's table instead: 370 wide on a
    // hub or media strand. No bundle puts the installed-games list on that tier,
    // and configuring the switcher into it deleted the size jump that is the
    // console's entire focus statement.

    // SCALED_EXP_MARGIN_LEFT: x the focused tile's left edge pins to.
    private const double HomeContentMargin = ShellTileRow.ScaledExpMarginLeft;

    // container: { width: 1920, height: 168 } (HOME m25), which is also
    // experienceSwitcherWrapper's height (HOME m216). The band sits directly
    // under the 126 nav, so y 126 to 294.
    private const double HomeDesignWidth = ShellTileRow.SwitcherStyles.ContainerWidth;
    private const double SwitcherBandHeight = ShellTileRow.SwitcherStyles.ContainerHeight;

    // Focus-graph region names, mirroring the shell's own focus layers. The
    // Home owns the switcher, top navigation, and a host-translated minimal
    // CTA rail for the focused title. The nav band's "home-system" is above
    // the switcher; the CTA is reached by the recovered Down route.
    private const string StrandRegion = "tile-item-focus-layer";
    private const string GameHubCtaRegion = "prosperismo-game-hub-cta";

    private static readonly IBrush DefaultLineBrush = new SolidColorBrush(Color.Parse("#C7CFDE"));
    private static readonly IBrush DimLineBrush = new SolidColorBrush(Color.Parse("#6B7488"));
    private static readonly IBrush InfoLineBrush = new SolidColorBrush(Color.Parse("#6FA8FF"));
    private static readonly IBrush WarningLineBrush = new SolidColorBrush(Color.Parse("#E8B341"));
    private static readonly IBrush ErrorLineBrush = new SolidColorBrush(Color.Parse("#F2777C"));
    private static readonly IBrush SuccessLineBrush = new SolidColorBrush(Color.Parse("#63D489"));
    private static readonly StringComparer FilePathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison FilePathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly List<GameEntry> _allGames = new();
    private readonly ObservableCollection<GameEntry> _visibleGames = new();
    private readonly AvaloniaList<LogLine> _consoleLines = new();
    private readonly List<LogLine> _allConsoleLines = new();
    private readonly ConcurrentQueue<(string Line, bool IsError)> _pendingLines = new();
    private readonly DispatcherTimer _consoleFlushTimer;
    private readonly DispatcherTimer _libraryBlurTimer;
    private readonly ShellModalManager _systemModalManager;

    // Home focus: a named-region graph with clamped edges and last-focused-item
    // restore, the model the console shell uses instead of geometric nav.
    private readonly ShellFocusGraph _homeFocus = new();
    private readonly ShellHubTransition _hubTransition = new();
    private bool _sonyGameHubOpen;

    /// <summary>
    /// Host seam for the Game Hub's extra CTA actions. It is intentionally
    /// distinct from the Home tile's OPTIONS menu: Group 6 owns that native
    /// option-menu model and must not be coupled to this list.
    /// </summary>
    public event EventHandler<ShellGameHubOverflowRequestedEventArgs>? GameHubOverflowRequested;

    private bool _controlCenterNotificationsOpen;
    private bool _profilePopoverOpen;
    private CancellationTokenSource? _profilePopoverCancellation;
    private ShellNotificationPanelScreen _controlCenterNotificationScreen =
        ShellNotificationPanelScreen.List;
    private bool _controlCenterNotificationsDoNotDisturb;
    private string? _controlCenterNotificationDetailId;
    private string? _controlCenterNotificationFocusedId;
    private int _controlCenterNotificationFocusedIndex;
    private bool _controlCenterAppSwitcherOptionsOpen;
    private readonly ShellContextMenu _controlCenterAppSwitcherMenu = new()
    {
        MinWidth = 384,
        MaxWidth = 512,
    };

    /// <summary>
    /// The console's staged home entrance (HOME m843). It moves the switcher
    /// host, the nav band and the hub; the row keeps driving its own tile
    /// stagger and caption fade, which are the beats it already owned.
    /// </summary>
    private readonly ShellEntrance _entrance = new();
    private bool _entrancePlayed;
    private readonly DispatcherTimer _strandRefreshTimer;
    private bool _syncingStrand;
    private bool _syncingDesktopLibrary;
    private bool _syncingSearchBoxes;
    private int _desktopSortColumn;
    private bool _desktopSortAscending = true;
    private bool _sonyStartupStarted;
    private bool _sonyAmbientStarted;
    private bool _sonyColdBootActive;
    private bool _sonyShellReady;
    private bool _shellUiStateStarted;
    private readonly TaskCompletionSource<bool> _sonyColdBootCompleted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _lastHomePlateIndex = -1;

    // The launcher toolbar is folded away on the home; this is the one state
    // that unfolds its search half.
    private bool _searchStripOpen;
    private bool _sonySearchOpen;
    private bool _sonyAllGamesOpen;
    private double _homeScale = 1.0;
    private bool _homeMetricsApplied;

    /// <summary>
    /// The launcher's own chrome, called up over the home screen. Off by
    /// default: on a console the home screen IS the whole screen, so the title
    /// bar, the launch bar and the status strip are not drawn there. F10 brings
    /// them back for as long as they are wanted.
    /// </summary>
    private bool _launcherChromeOnHome;

    // The bounded shell resource set is publish-copied beside the apphost.
    private readonly ShellPresentationMode _presentationMode;
    private readonly Bitmap? _defaultGameIcon;

    private BlurEffect? _libraryBlur;
    private double _libraryBlurStartRadius;
    private double _libraryBlurTargetRadius;
    private long _libraryBlurStartedAt;
    private bool _clearLibraryBlurWhenComplete;

    private GuiSettings _settings = new();
    private readonly PerGameEmulatorSettingsStore _perGameEmulatorSettings = new();
    private bool _syncingEmulatorSettingsControls;
    private EmulatorProcess? _emulator;
    private GameSurfaceHost? _gameSurfaceHost;
    private ConsoleWindow? _consoleWindow;
    private GuiConsoleMirror? _consoleMirror;
    private StreamWriter? _fileLog;
    private readonly SndPreviewPlayer _sndPreview = new();
    private GameEntry? _titleMediaOwner;
    private string? _emulatorExePath;
    private PendingLaunch? _pendingLaunch;
    private bool _gameFullscreen;
    private bool _isRunning;
    private bool _isStopping;
    private bool _awaitingFirstFrame;
    private int _autoScrollTicks;
    private int _activePageIndex;
    private CancellationTokenSource? _pageTransitionCancellation;
    private PixelRect? _sonyDisplayBounds;
    private PixelRect? _sonyDisplayWorkingArea;
    private double _sonyDisplayScaling;
    private bool _applyingSonyDisplayPolicy;
    private Updater.UpdateInfo? _availableUpdate;
    private string _updateStatusKey = "Updater.Status.Ready";
    private object?[] _updateStatusArgs = [BuildInfo.CommitSha ?? "dev"];

    // Discord Rich Presence state.
    private readonly long _launcherStartUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private DiscordRichPresence? _discord;
    private string? _runningGameName;
    private string? _runningGameTitleId;
    // Title ids are optional in scanned content, so session identity must not
    // depend on them. This binds a tile's Close application row to exactly the
    // eboot path that was launched.
    private string? _runningGamePath;
    private long _runningSinceUnixSeconds;
    private int _detailLoadGeneration;
    private int _libraryScanGeneration;
    private int _backdropGeneration;

    // Prosperismo's native SDL/Vulkan backend owns its game window. The old
    // managed embedded-host protocol is not a valid native launch contract.
    private bool UsesEmbeddedPresentation => false;

    // Bundled key art shown whenever no game-specific backdrop applies; the
    // plain window color remains the fallback when the asset fails to load.
    private Bitmap? _defaultBackdrop;

    // Whether the native loading/closing popup should be showing; it is a
    // desktop-topmost popup, so it closes while the launcher is in the
    // background or minimized and reopens from this flag on activation.
    private bool _sessionLoadingActive;

    // Controller navigation state.
    private readonly DispatcherTimer _gamepadTimer;
    private HostGamepadButtons _previousPadButtons;
    private long _navLeftNextAt;
    private long _navRightNextAt;
    private long _navUpNextAt;
    private long _navDownNextAt;

    //Github http client for latest commit
    private static readonly HttpClient GithubHttpClient = CreateGithubHttpClient();
    private string? _latestCommitSha;

    private sealed record PendingLaunch(
        string EbootPath,
        string DisplayName,
        string? TitleId,
        EmulatorSettings EmulatorSettings,
        string? PatchPlanPath);

    private static Bitmap? TryLoadPackagedDefaultGameIcon()
    {
        try
        {
            var path = BigPicturePackage.Resolve("3.00/textures/tex_default_game.png");
            if (path is null)
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        ApplyGameHubRecoveredLayout();
        _systemModalManager = new ShellModalManager(
            SystemOverlayHost,
            HomePlate);

        _presentationMode = ShellPresentation.Current;
        _sonyColdBootActive = _presentationMode == ShellPresentationMode.Sony;
        _sonyShellReady = _presentationMode != ShellPresentationMode.Sony;
        _settings = GuiSettings.Load();
        // NPXS40003 describes DND as lasting until logout. A new launcher
        // process is a new local shell session, so never restore it from disk.
        ShellNotificationBroker.DoNotDisturb = false;
        ShellNotificationHistory.ConfigurePersistence(GuiSettings.NotificationHistoryPath);
        // The desktop launcher may share the title-preview mixer, but it never
        // SndPreviewPlayer.Stop(), which is intentionally reusable by both
        // presentations.
        ShellAmbientMusic.SetSonyPresentationActive(_presentationMode == ShellPresentationMode.Sony);
        if (ShellPresentation.UsesDesktopLauncherVisuals(_presentationMode))
        {
            // Desktop is a direct Avalonia translation of Kyty's compact Qt
            // launcher. It uses the host title bar and keeps all operations on
            // separate borderless console presentation.
            RootLayout.Classes.Add("desktopLauncher");
            ExtendClientAreaToDecorationsHint = false;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.Default;
            TitleBar.IsVisible = false;
            StatusBar.IsVisible = false;
            // The observed PlayStation web desktop branch is authored at a
            // 1280px viewport with a 42px mesh margin. Keep the compact
            // launcher resizable, but open at that branch and do not collapse
            // the seven-column configuration list below its usable width.
            Width = 1280;
            Height = 760;
            MinWidth = 960;
            MinHeight = 600;
        }
        _launcherChromeOnHome = _presentationMode == ShellPresentationMode.Desktop;
        _defaultGameIcon = TryLoadPackagedDefaultGameIcon();

        if (_presentationMode == ShellPresentationMode.Sony)
        {
            // HOME's SceneList supplies an optional, separately authored
            // blurUri to BGLayer's Ripple/CrossFade transition. It never asks
            // the compositor to blur the sharp image in lieu of that asset.
            // presents the title-owned artwork untouched until the native
            // transition shader and optional blur slot are hosted directly.
            BackdropImage.Effect = null;
            BackdropScrim.IsVisible = false;
        }

        // now, so the bundled key art is no longer painted over it. Game art
        // still fades in through BackdropImage when a title is selected. The
        // hub background and the boot chime start once the window is up so
        // nothing runs against the shell before it is attached.
        _defaultBackdrop = null;

        // Subscribed here rather than in the Loaded handler so a second Loaded
        // (reparenting) cannot double-subscribe. The event cannot fire before
        // the Preload below starts the work.
        ShellIcons.Loaded += OnShellIconsLoaded;
        Closed += (_, _) => ShellIcons.Loaded -= OnShellIconsLoaded;

        Loaded += async (_, _) =>
        {
            if (UsesSonyAmbientFor(_presentationMode))
            {
                HomePlate.TitleId = null;
            }

            void CompleteSonyColdBoot()
            {
                // Cold boot is an exclusive startup state. Publish HOME's
                // retained ambient instance and release the initialization
                // gate, but do not reveal or start the UI here: OnOpenedAsync
                // first initializes the library and shell state behind the
                // still-hidden surface, then reveals HOME atomically.
                if (_presentationMode != ShellPresentationMode.Sony || _sonyAmbientStarted)
                {
                    return;
                }

                _sonyAmbientStarted = true;
                _sonyColdBootActive = false;
                HomePlate.ContinueAmbientSequence();
                _sonyColdBootCompleted.TrySetResult(true);
            }

            if (_presentationMode == ShellPresentationMode.Sony)
            {
                if (!_sonyStartupStarted)
                {
                    _sonyStartupStarted = true;
                    _sonyColdBootActive = true;
                    BackdropImage.Opacity = 0;
                    HomePlate.TitleId = null;
                    HomePlate.TitleArtPath = null;
                    // so audio and the native monotonic clock share the same zero.
                    // The recovered managed cold-boot state lasts six seconds.
                    // pattern's 0..8.5 authoring domain. The later native
                    // instance release remains an internal particle lifetime;
                    // it is not the UI or audio boundary.
                    var coldBootReady = await HomePlate.StartColdBootSequenceAsync();
                    ShellAudio.PlayBootChime();
                    if (coldBootReady)
                    {
                        DispatcherTimer.RunOnce(
                            CompleteSonyColdBoot,
                            TimeSpan.FromSeconds(
                                Ps5NativeColdBootAmbientTimeline.ColdBootDurationSeconds));
                    }
                    else
                    {
                        CompleteSonyColdBoot();
                    }
                }
            }
            else
            {
                StartShellUiState();
            }
        };

        GameList.ItemsSource = _visibleGames;
        DesktopLibrary.Games.ItemsSource = _visibleGames;
        ConsoleList.ItemsSource = _consoleLines;
        _consoleMirror = GuiConsoleMirror.Install((line, isError) =>
            _pendingLines.Enqueue((line, isError)));
        Closed += (_, _) => _emulator?.Stop();
        Closed += (_, _) => _defaultGameIcon?.Dispose();

        _consoleFlushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _consoleFlushTimer.Tick += (_, _) =>
        {
            FlushPendingConsoleLines();
            MaybeAutoScroll();
        };
        _consoleFlushTimer.Start();

        _libraryBlurTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _libraryBlurTimer.Tick += (_, _) => AdvanceLibraryBlur();

        // Cover art arrives one game at a time after a scan; rebuilding the
        // strand per cover would replay its reveal stagger, so the refresh is
        // coalesced into a single pass once the covers stop landing.
        _strandRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(140),
        };
        _strandRefreshTimer.Tick += (_, _) =>
        {
            _strandRefreshTimer.Stop();
            SyncStrandTiles();
        };

        SetUpHomeLayout();
        SetUpControlCenter();

        // Native popups float above every window on the desktop; they must
        // follow the launcher into the background or a minimized state.
        Activated += (_, _) =>
        {
            UpdateSessionBarVisibility();
            SessionLoadingPopup.IsOpen = _sessionLoadingActive;
        };
        Deactivated += (_, _) =>
        {
            SessionBarPopup.IsOpen = false;
            SessionLoadingPopup.IsOpen = false;
        };

        TitleBar.PointerPressed += OnTitleBarPointerPressed;
        GameList.SelectionChanged += (_, _) =>
        {
            // The strand is the visible library; mirror the model onto it and
            // let it play the shell's focus cue for the move.
            if (!_syncingStrand)
            {
                _syncingStrand = true;
                try
                {
                    GameStrand.SetSelectedIndex(GameList.SelectedIndex);
                }
                finally
                {
                    _syncingStrand = false;
                }
            }

            if (!_syncingDesktopLibrary &&
                !ReferenceEquals(DesktopLibrary.Games.SelectedItem, GameList.SelectedItem))
            {
                _syncingDesktopLibrary = true;
                try
                {
                    DesktopLibrary.Games.SelectedItem = GameList.SelectedItem;
                }
                finally
                {
                    _syncingDesktopLibrary = false;
                }
            }

            UpdateSelectedGame();
        };
        DesktopLibrary.Games.SelectionChanged += (_, _) =>
        {
            if (_syncingDesktopLibrary ||
                ReferenceEquals(GameList.SelectedItem, DesktopLibrary.Games.SelectedItem))
            {
                return;
            }

            _syncingDesktopLibrary = true;
            try
            {
                GameList.SelectedItem = DesktopLibrary.Games.SelectedItem;
            }
            finally
            {
                _syncingDesktopLibrary = false;
            }
        };
        SearchBox.TextChanged += (_, _) => SynchronizeSearchText(SearchBox, DesktopLibrary.SearchBox);
        DesktopLibrary.SearchBox.TextChanged += (_, _) => SynchronizeSearchText(DesktopLibrary.SearchBox, SearchBox);
        SearchBox.LostFocus += (_, _) => CloseSearchStripIfIdle();
        // HOME it is only raised by the shell's search affordance.
        DesktopLibrary.SearchBox.LostFocus += (_, _) => CloseSearchStripIfIdle();
        SearchBox.KeyDown += OnLibrarySearchKeyDown;
        DesktopLibrary.SearchBox.KeyDown += OnLibrarySearchKeyDown;
        ConsoleSearchBox.TextChanged += (_, _) => RefreshVisibleConsoleLines();
        AddFolderButton.Click += async (_, _) => await AddFolderAsync();
        DesktopLibrary.AddFolderRequested += async (_, _) => await AddFolderAsync();
        EmptyAddFolderButton.Click += async (_, _) => await AddFolderAsync();
        RescanButton.Click += async (_, _) => await RescanLibraryAsync(offerActions: true);
        DesktopLibrary.RescanRequested += async (_, _) => await RescanLibraryAsync(offerActions: true);
        OpenFileButton.Click += async (_, _) => await OpenFileAsync();
        DesktopLibrary.OpenFileRequested += async (_, _) => await OpenFileAsync();
        DesktopLibrary.BigPictureRequested += (_, _) => RestartInBigPicture();
        DesktopLibrary.GameSettingsRequested += (_, _) => OpenSelectedGameSettings();
        DesktopLibrary.GlobalSettingsRequested += (_, _) => _ = OpenDesktopGlobalSettingsAsync();
        DesktopLibrary.ClearCustomSettingsRequested += (_, e) =>
            _ = ClearCustomGameSettingsAsync(e.Game);
        DesktopLibrary.LaunchRequested += (_, e) =>
        {
            if (!_isRunning && !_isStopping)
            {
                Launch(e.Game.Path, e.Game.Name, e.Game.TitleId);
            }
        };
        DesktopLibrary.OpenGameFolderRequested += (_, e) => OpenGameFolder(e.Game);
        DesktopLibrary.ViewTrophiesRequested += (_, e) => _ = OpenDesktopTrophiesAsync(e.Game);
        DesktopLibrary.PatchesRequested += (_, e) => _ = OpenDesktopPatchesAsync(e.Game);
        DesktopLibrary.RemoveSaveDataRequested += (_, e) => _ = RemoveDesktopSaveDataAsync(e.Game);
        DesktopLibrary.StatusEditRequested += (_, e) =>
            e.Game.CompatibilityStatus = GameStatusInfo.Parse(e.Value);
        DesktopLibrary.CommentEditRequested += (_, e) => e.Game.Comment = e.Value;
        DesktopLibrary.SortRequested += (_, e) => ApplyDesktopSort(e.Column);
        LaunchButton.Click += (_, _) => LaunchSelected();
        ClearLogButton.Click += (_, _) => { _consoleLines.Clear(); _allConsoleLines.Clear(); };
        StopButton.Click += async (_, _) => await RequestStopEmulatorAsync();
        SessionStopButton.Click += async (_, _) => await RequestStopEmulatorAsync();
        SessionConsoleButton.Click += (_, _) => ShowConsoleWindow();
        CopyLogButton.Click += async (_, _) => await CopyConsoleAsync();
        DetachConsoleButton.Click += (_, _) => ShowConsoleWindow();
        LibraryTabButton.Click += (_, _) => SetActivePage(0);
        OptionsTabButton.Click += (_, _) => SetActivePage(1);

        // The nav band raised SystemActivated and nobody listened, so the gear
        // did nothing. It matters more now than it used to: the home screen owns
        // the window and hides the launcher chrome, so the Options tab button is
        // off-screen and this is the only route to settings.
        TopNavBand.SystemActivated += (_, e) =>
        {
            switch (e.Destination)
            {
                case ShellSystemDestination.Settings:
                    SetActivePage(1);
                    break;

                case ShellSystemDestination.Search:
                    OpenSonySearch();
                    break;

                case ShellSystemDestination.Profile:
                    OpenProfilePopover();
                    break;
            }
        };
        ProfileFunctionPanel.ItemActivated += (_, e) =>
            _ = OnProfilePanelItemActivatedAsync(e.Item, fromControlCenter: false);
        SonySearch.QueryChanged += (_, _) => RefreshSonySearchItems();
        SonySearch.CloseRequested += (_, _) => CloseSonySearch();
        SonySearch.ItemActivated += (_, e) =>
        {
            if (e.Item?.Tag is not GameEntry game)
            {
                return;
            }

            CloseSonySearch();
            SearchBox.Text = string.Empty;
            RefreshVisibleGames();
            GameList.SelectedItem = game;
            Dispatcher.UIThread.Post(OpenSonyGameHub, DispatcherPriority.Loaded);
        };
        ConsoleToggle.IsCheckedChanged += (_, _) => ConsolePanel.IsVisible = ConsoleToggle.IsChecked == true && _consoleWindow is null;

        // The settings page edits _settings live, so a launch started while
        // it is open already uses the new values.
        LogLevelBox.SelectionChanged += (_, _) => _settings.LogLevel = SelectedLogLevel();
        TraceImportsBox.ValueChanged += (_, _) => _settings.ImportTraceLimit = (int)(TraceImportsBox.Value ?? 0);
        RenderResolutionBox.SelectionChanged += (_, _) =>
        {
            if (RenderResolutionBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
                double.TryParse(
                    tag,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var scale))
            {
                _settings.RenderResolutionScale = scale;
                SonySettingsDetail.RenderResolutionScale = scale;
            }
        };
        WireDesktopEmulatorSettingsControls();

        SonySettingsCategories.CategoryActivated += (_, e) =>
        {
            SonySettingsCategories.IsVisible = false;
            SonySettingsDetail.IsVisible = true;
            var tabIndex = ShellSettingsDetailList.Tabs
                .Select((tab, index) => (tab, index))
                .First(pair => pair.tab.TabId == e.Category.ItemId)
                .index;
            SonySettingsDetail.SelectedTabIndex = tabIndex;
            Dispatcher.UIThread.Post(() => SonySettingsDetail.Focus(), DispatcherPriority.Loaded);
        };
        SonySettingsCategories.BackRequested += (_, _) => SetActivePage(0);
        SonySettingsDetail.BackRequested += (_, _) => CloseSonySettingsDetail();
        SonySettingsDetail.RenderResolutionScaleChanged += (_, _) =>
        {
            RenderResolutionBox.SelectedIndex = SonySettingsDetail.RenderResolutionScale switch
            {
                >= .875 => 0,
                >= .625 => 1,
                >= .375 => 2,
                _ => 3,
            };
        };
        SonySettingsDetail.EmulatorSettingChanged += (_, _) =>
        {
            if (SonySettingsDetail.LastChangedEmulatorSettingId is not null)
            {
                _settings.GlobalEmulatorSettings = SonySettingsDetail.GetEmulatorSettings();
                ApplyEmulatorSettingsToDesktopControls(_settings.GlobalEmulatorSettings);
                return;
            }

            TitleMusicToggle.IsChecked = SonySettingsDetail.PlayTitleMusic;
            ShellMotionToggle.IsChecked = SonySettingsDetail.AnimateShellBackground;
            UiSoundsToggle.IsChecked = SonySettingsDetail.PlayUiSounds;
            ShellMusicToggle.IsChecked = SonySettingsDetail.PlayShellMusic;
            BootIntroToggle.IsChecked = SonySettingsDetail.PlayBootIntro;
            DiscordToggle.IsChecked = SonySettingsDetail.DiscordPresence;
            AutoUpdateToggle.IsChecked = SonySettingsDetail.CheckUpdates;
            StrictToggle.IsChecked = SonySettingsDetail.StrictDynlib;
            LogToFileToggle.IsChecked = SonySettingsDetail.LogToFile;
            OverrideLogFileToggle.IsChecked = SonySettingsDetail.OverrideLogFile;
            LogLevelBox.SelectedIndex = SonySettingsDetail.LogLevel.ToLowerInvariant() switch
            {
                "trace" => 0, "debug" => 1, "info" => 2,
                "warning" => 3, "error" => 4, "critical" => 5, _ => 2,
            };
            TraceImportsBox.Value = SonySettingsDetail.ImportTraceLimit;
            EnvBthidToggle.IsChecked = SonySettingsDetail.IsEnvironmentEnabled("env_bthid");
            EnvLoopGuardToggle.IsChecked = SonySettingsDetail.IsEnvironmentEnabled("env_loop_guard");
            EnvWritableApp0Toggle.IsChecked = SonySettingsDetail.IsEnvironmentEnabled("env_writable_app0");
            EnvVkValidationToggle.IsChecked = SonySettingsDetail.IsEnvironmentEnabled("env_vk_validation");
            EnvDumpSpirvToggle.IsChecked = SonySettingsDetail.IsEnvironmentEnabled("env_dump_spirv");
            EnvLogDirectMemoryToggle.IsChecked = SonySettingsDetail.IsEnvironmentEnabled("env_log_direct_memory");
            EnvLogIoToggle.IsChecked = SonySettingsDetail.IsEnvironmentEnabled("env_log_io");
            EnvLogNpToggle.IsChecked = SonySettingsDetail.IsEnvironmentEnabled("env_log_np");
        };
        SonySettingsDetail.LanguageCycleRequested += (_, _) =>
        {
            if (LanguageBox.ItemsSource is not IEnumerable<Localization.LanguageInfo> languages)
            {
                return;
            }
            var available = languages.ToArray();
            if (available.Length == 0)
            {
                return;
            }
            LanguageBox.SelectedIndex = (LanguageBox.SelectedIndex + 1 + available.Length) % available.Length;
            SonySettingsDetail.LanguageName =
                (LanguageBox.SelectedItem as Localization.LanguageInfo)?.NativeName ?? "Default";
        };
        SonySettingsDetail.LogFilePathRequested += async (_, _) => await SelectLogFilePathAsync();
        SonySettingsDetail.EmulatorTextSettingRequested += settingId =>
            _ = SelectNativeEmulatorSettingPathAsync(settingId);
        SonySettingsDetail.ActionRequested += action =>
        {
            switch (action)
            {
                case "id_check_updates":
                    _ = OnUpdateButtonAsync();
                    break;
                case "id_rescan_library":
                    _ = RescanLibraryAsync(offerActions: true);
                    break;
                case "id_github":
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://github.com/medfa12/prosperismo",
                        UseShellExecute = true,
                    });
                    break;
                case "id_discord":
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://discord.com/invite/6GejPEDqpc",
                        UseShellExecute = true,
                    });
                    break;
            }
        };
        StrictToggle.IsCheckedChanged += (_, _) => _settings.StrictDynlibResolution = StrictToggle.IsChecked == true;
        LogToFileToggle.IsCheckedChanged += (_, _) => _settings.LogToFile = LogToFileToggle.IsChecked == true;
        OverrideLogFileToggle.IsCheckedChanged += (_, _) =>
            _settings.OverrideLogFile = OverrideLogFileToggle.IsChecked == true;
        TitleMusicToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.PlayTitleMusic = TitleMusicToggle.IsChecked == true;
            OnTitleMusicSettingChanged();
        };
        ShellMotionToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.AnimateShellBackground = ShellMotionToggle.IsChecked == true;
            if (_presentationMode == ShellPresentationMode.Sony)
            {
                HomePlate.IsMotionEnabled = _settings.AnimateShellBackground;
                if (_settings.AnimateShellBackground)
                {
                    // The one persistent owner remains mounted under both routes,
                    // so resuming it never creates a second Settings clock.
                    HomePlate.RefreshAnimationRoute();
                }
            }
        };
        UiSoundsToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.PlayUiSounds = UiSoundsToggle.IsChecked == true;
            ApplyUiSoundSetting();
        };
        ShellMusicToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.PlayShellMusic = ShellMusicToggle.IsChecked == true;
            ShellAmbientMusic.IsEnabled = _presentationMode == ShellPresentationMode.Sony &&
                _settings.PlayShellMusic;
        };
        BootIntroToggle.IsCheckedChanged += (_, _) =>
            // Guarded inside SetArmed: ApplySettingsToControls raises this same
            // event, so an unguarded write would read a spent latch back as
            // "turn it off" and disable the intro for good.
            BootAnimation.BootIntroPolicy.SetArmed(_settings, BootIntroToggle.IsChecked == true);
        DiscordToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.DiscordRichPresence = DiscordToggle.IsChecked == true;
            UpdateDiscordPresence();
        };
        AutoUpdateToggle.IsCheckedChanged += (_, _) =>
            _settings.CheckForUpdatesOnStartup = AutoUpdateToggle.IsChecked == true;
        UpdateButton.Click += async (_, _) => await OnUpdateButtonAsync();
        SelectLogFilePathButton.Click += async (_, _) => await SelectLogFilePathAsync();
        EnvBthidToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("PROSPERISMO_BTHID_UNAVAILABLE", EnvBthidToggle.IsChecked == true);
        EnvLoopGuardToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("PROSPERISMO_DISABLE_IMPORT_LOOP_GUARD", EnvLoopGuardToggle.IsChecked == true);
        EnvWritableApp0Toggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("PROSPERISMO_WRITABLE_APP0", EnvWritableApp0Toggle.IsChecked == true);
        EnvVkValidationToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("PROSPERISMO_VK_VALIDATION", EnvVkValidationToggle.IsChecked == true);
        EnvDumpSpirvToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("PROSPERISMO_DUMP_SPIRV", EnvDumpSpirvToggle.IsChecked == true);
        EnvLogDirectMemoryToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("PROSPERISMO_LOG_DIRECT_MEMORY", EnvLogDirectMemoryToggle.IsChecked == true);
        EnvLogIoToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("PROSPERISMO_LOG_IO", EnvLogIoToggle.IsChecked == true);
        EnvLogNpToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("PROSPERISMO_LOG_NP", EnvLogNpToggle.IsChecked == true);
        LanguageBox.SelectionChanged += (_, _) => OnLanguageChanged();

        StrandHost.AddHandler(ContextRequestedEvent, OnGameContextRequested, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // The option menu gets the shell's own open/close cues, a move cue as
        // the highlighted row changes, and a confirm cue on activation. The
        // shell has distinct up/down list-move cues (PSFX_MOVE_FOCUS_UP/DOWN_
        // IN_THE_LIST); the extracted cue set only maps the generic focus-move
        // blip, so both directions play that one.
        WireOptionMenuAudio(GameContextMenu);
        WireOptionMenuAudio(GameHubOverflowMenu);

        Opened += async (_, _) => await OnOpenedAsync();
        Closing += (_, _) => OnWindowClosing();
        PositionChanged += OnWindowPositionChangedForSonyDisplay;
        ScalingChanged += OnSonyDisplayEnvironmentChanged;
        Screens.Changed += OnSonyDisplayEnvironmentChanged;
        Closed += (_, _) => Screens.Changed -= OnSonyDisplayEnvironmentChanged;
        StartLayoutDiagnosticsIfRequested();

        WindowsDualSenseReader.EnsureStarted();
        WindowsXInputReader.EnsureStarted();
        _gamepadTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _gamepadTimer.Tick += (_, _) => PollGamepad();
        _gamepadTimer.Start();


        GithubButton.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/medfa12/prosperismo",
                UseShellExecute = true
            });
        };

        DiscordButton.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://discord.com/invite/6GejPEDqpc",
                UseShellExecute = true
            });
        };

        LatestCommitHashText.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_latestCommitSha))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName =
                    $"https://github.com/medfa12/prosperismo/commit/{_latestCommitSha}",
                UseShellExecute = true
            });
        };
    }

    /// <summary>Posts a managed system dialog below the shell's SystemOverlay
    /// host, with BGLayer basemat dimming and PS/capture input locking.</summary>
    public Task<string> ShowSystemDialogAsync(ShellDialogRequest request) =>
        _systemModalManager.ShowAsync(request);

    private void SetUpControlCenter()
    {
        WireOptionMenuAudio(_controlCenterAppSwitcherMenu);
        _controlCenterAppSwitcherMenu.Closed += (_, _) =>
            _controlCenterAppSwitcherOptionsOpen = false;
        ControlCenterHost.Items = ShellControlCenter.ConsoleTourItems;
        RefreshControlCenterNotificationState();
        ControlCenterHost.RestoreSelectedItem(_settings.LastFocusedControlCenterId);
        ControlCenterHost.ControlActivated += OnControlCenterControlActivated;
        ControlCenterHost.PanelItemActivated += (_, args) =>
            _ = OnControlCenterPanelItemActivatedAsync(args.Item);
        ControlCenterHost.PanelSelectionChanged += (_, args) =>
        {
            if (_controlCenterNotificationsOpen &&
                _controlCenterNotificationScreen == ShellNotificationPanelScreen.List)
            {
                if (args.Item?.Tag is ShellNotificationHistoryTag tag)
                {
                    _controlCenterNotificationFocusedId = tag.EntryId;
                    _controlCenterNotificationFocusedIndex = args.Index;
                    if (ShellNotificationHistory.MarkSeen(tag.EntryId))
                    {
                        RefreshOpenControlCenterNotifications();
                    }
                }
                else if (args.Item?.Tag is ShellNotificationDoNotDisturbTag)
                {
                    _controlCenterNotificationFocusedId = null;
                    _controlCenterNotificationFocusedIndex =
                        ShellNotificationListComposer.DoNotDisturbIndex;
                }
            }
        };
        ControlCenterHost.PanelOptionsRequested += (_, _) =>
            ShowControlCenterPanelOptions();
        ControlCenterHost.PanelDeleteRequested += (_, _) =>
            DeleteFocusedControlCenterNotification();
        ControlCenterHost.PanelBackRequested += (_, args) =>
            args.Handled = HandleControlCenterNotificationBack() ||
                HandleControlCenterAppSwitcherBack();
        ControlCenterHost.PanelClosed += (_, _) =>
        {
            _controlCenterAppSwitcherOptionsOpen = false;
            if (_controlCenterAppSwitcherMenu.IsOpen)
            {
                _controlCenterAppSwitcherMenu.Close();
            }
            if (_controlCenterNotificationsOpen)
            {
                _controlCenterNotificationsOpen = false;
                if (_controlCenterNotificationScreen == ShellNotificationPanelScreen.Detail &&
                    _controlCenterNotificationDetailId is { } detailId)
                {
                    ShellNotificationHistory.MarkRead(detailId);
                }
                // NotificationListContent finalizes every still-New row to Seen
                // when the list unmounts. Detail unmount owns the Read transition.
                ShellNotificationHistory.MarkAllNewSeen();
                ResetControlCenterNotificationNavigation();
            }
        };
        NotificationHost.CurrentChanged += (_, _) =>
        {
            if (ControlCenterHost.IsOpen &&
                ControlCenterHost.PanelOwnerId == "notifications")
            {
                RefreshOpenControlCenterNotifications();
            }
        };
        ShellNotificationHistory.Changed += (_, _) =>
            Dispatcher.UIThread.Post(
                () =>
                {
                    RefreshControlCenterNotificationState();
                    RefreshOpenControlCenterNotifications();
                },
                DispatcherPriority.Render);
        ControlCenterHost.Closed += (_, _) =>
        {
            _settings.LastFocusedControlCenterId =
                ControlCenterHost.SelectedItem?.Id ?? "home";
            _settings.Save();
        };
    }

    private void RefreshControlCenterNotificationState()
    {
        var newCount = ShellNotificationHistory.Snapshot().Count(entry =>
            entry.State == ShellNotificationHistoryState.New);
        ControlCenterHost.SetNotificationState(
            _controlCenterNotificationsDoNotDisturb,
            newCount);
    }

    private void OnControlCenterControlActivated(object? sender, ShellControlCenterEventArgs args)
    {
        switch (args.Item?.Id)
        {
            case "home":
                _ = ControlCenterHost.CloseAsync();
                break;
            case "notifications":
                _ = ShowControlCenterNotificationsAsync();
                break;
            case "apps":
                _ = ShowControlCenterAppSwitcherAsync();
                break;
            case "profile":
                _ = ControlCenterHost.ShowPanelAsync(
                    "profile",
                    LocalProfileDisplayName(),
                    ShellProfilePanelComposer.ComposeOffline(),
                    ShellProfilePanelComposer.OfflineInitialSelectedIndex);
                break;
            case "power":
                _ = ShowControlCenterPowerAsync();
                break;
            case { } unavailable:
                // These controls are visible in the direct console tour but
                // Prosperismo has no truthful PSN/Spotify/service provider.
                // Keep the stock slot and fail visibly instead of inventing a
                // service payload or silently eating Cross.
                var title = args.Item?.Title ?? unavailable;
                _ = ControlCenterHost.ShowPanelAsync(
                    unavailable,
                    title,
                    [new ShellFunctionPanelItem("Unavailable on this host") { IsEnabled = false }]);
                ShellUiSounds.Play(UiSoundEvent.Error);
                break;
        }
    }

    private Task ShowControlCenterNotificationsAsync()
    {
        _controlCenterNotificationsOpen = true;
        ResetControlCenterNotificationNavigation();
        var history = ShellNotificationHistory.Snapshot();
        var rows = ShellNotificationListComposer.Compose(
            history,
            _controlCenterNotificationsDoNotDisturb);
        _controlCenterNotificationFocusedId = history.FirstOrDefault()?.Id;
        _controlCenterNotificationFocusedIndex =
            ShellNotificationListComposer.InitialSelectedIndex(history.Count);

        return ControlCenterHost.ShowPanelAsync(
            "notifications",
            "Notifications",
            rows,
            _controlCenterNotificationFocusedIndex);
    }

    private Task ShowControlCenterAppSwitcherAsync()
    {
        _controlCenterAppSwitcherOptionsOpen = false;
        return ControlCenterHost.ShowPanelAsync(
            "apps",
            "Switcher",
            ShellAppSwitcherComposer.Compose(
                _allGames,
                _runningGamePath,
                _settings.RecentGamePaths,
                FilePathComparison));
    }

    private void ShowControlCenterPanelOptions()
    {
        if (ControlCenterHost.PanelOwnerId == "apps" &&
            !_controlCenterAppSwitcherOptionsOpen &&
            ControlCenterHost.SelectedPanelItem?.Tag is ShellAppSwitcherEntryTag app)
        {
            ShowControlCenterAppSwitcherOptions(app);
            return;
        }

        ShowControlCenterNotificationOptions();
    }

    private void ShowControlCenterAppSwitcherOptions(ShellAppSwitcherEntryTag entry)
    {
        var game = _allGames.FirstOrDefault(candidate =>
            string.Equals(candidate.Path, entry.Path, FilePathComparison));
        if (game is null || ControlCenterHost.PanelOwnerId != "apps")
        {
            ShellUiSounds.Play(UiSoundEvent.Error);
            return;
        }

        var isActive = entry.Section == ShellAppSwitcherSection.Active &&
            ShellTitleOptionsComposer.IsCurrentRunningTitle(
                game.Path,
                _runningGamePath,
                FilePathComparison);
        var rows = ShellAppSwitcherComposer.ComposeOptions(
            game,
            entry.Section,
            canReturnToRunningGame: isActive && !_isStopping,
            canCloseRunningGame: isActive && !_isStopping,
            canLaunch: !_isRunning && !_isStopping,
            // Prosperismo cannot suspend a live native session to expose its
            // Hub safely. Recent idle titles do have the normal Hub route.
            canOpenHub: entry.Section == ShellAppSwitcherSection.RecentGame &&
                !_isRunning && !_isStopping);
        if (rows.Count == 0)
        {
            ShellUiSounds.Play(UiSoundEvent.Error);
            return;
        }

        _controlCenterAppSwitcherOptionsOpen = true;
        _controlCenterAppSwitcherMenu.SetEntries(rows.Select(row =>
            new ShellMenuEntry(row.Title, () =>
            {
                if (row.Tag is ShellAppSwitcherActionTag action)
                {
                    _ = HandleControlCenterAppSwitcherActionAsync(action);
                }
            })
            {
                ShowIconGutter = false,
            }));
        var anchor = ControlCenterHost.SelectedPanelAnchor ?? ControlCenterHost;
        _controlCenterAppSwitcherMenu.PlacementTarget = anchor;
        _controlCenterAppSwitcherMenu.HorizontalOffset = 8;
        _controlCenterAppSwitcherMenu.VerticalOffset = 0;
        _controlCenterAppSwitcherMenu.Open(anchor);
    }

    private bool HandleControlCenterAppSwitcherBack()
    {
        if (ControlCenterHost.PanelOwnerId != "apps" ||
            !_controlCenterAppSwitcherMenu.IsOpen)
        {
            return false;
        }

        _controlCenterAppSwitcherMenu.Close();
        return true;
    }

    private static string LocalProfileDisplayName()
    {
        var name = Environment.UserName?.Trim();
        return string.IsNullOrWhiteSpace(name) ? "Prosperismo" : name;
    }

    private void OpenProfilePopover()
    {
        if (_presentationMode != ShellPresentationMode.Sony || !_sonyShellReady ||
            _isRunning || _isStopping || ControlCenterHost.IsOpen)
        {
            return;
        }

        _profilePopoverCancellation?.Cancel();
        _profilePopoverCancellation?.Dispose();
        _profilePopoverCancellation = new CancellationTokenSource();

        _profilePopoverOpen = true;
        ProfileFunctionPanel.Header = LocalProfileDisplayName();
        ProfileFunctionPanel.Items = ShellProfilePanelComposer.ComposeOffline();
        ProfileFunctionPanel.SetSelectedIndex(ShellProfilePanelComposer.OfflineInitialSelectedIndex);
        ProfilePopoverSurface.IsVisible = true;
        ProfilePopoverSurface.IsHitTestVisible = true;
        ProfilePopoverSurface.Transitions =
        [
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = ShellControlCenter.PanelShowDuration,
                Easing = ShellMotion.EaseOutBlast,
            },
        ];
        if (ProfilePopoverSurface.RenderTransform is TranslateTransform translate)
        {
            translate.Transitions =
            [
                new DoubleTransition
                {
                    Property = TranslateTransform.YProperty,
                    Duration = ShellControlCenter.PanelShowDuration,
                    Easing = ShellMotion.EaseOutBlast,
                },
            ];
            translate.Y = 0;
        }
        ProfilePopoverSurface.Opacity = 1;
        ProfileFunctionPanel.Focus();
        ShellUiSounds.Play(UiSoundEvent.OpenOptionMenu);
    }

    private async Task CloseProfilePopoverAsync(bool playSound = true)
    {
        if (!_profilePopoverOpen)
        {
            return;
        }

        _profilePopoverOpen = false;
        _profilePopoverCancellation?.Cancel();
        _profilePopoverCancellation?.Dispose();
        _profilePopoverCancellation = new CancellationTokenSource();
        var token = _profilePopoverCancellation.Token;
        ProfilePopoverSurface.IsHitTestVisible = false;
        ProfilePopoverSurface.Transitions =
        [
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = ShellControlCenter.CloseDuration,
            },
        ];
        ProfilePopoverSurface.Opacity = 0;
        if (ProfilePopoverSurface.RenderTransform is TranslateTransform translate)
        {
            translate.Transitions =
            [
                new DoubleTransition
                {
                    Property = TranslateTransform.YProperty,
                    Duration = ShellControlCenter.CloseDuration,
                },
            ];
            translate.Y = 20;
        }
        if (playSound)
        {
            ShellUiSounds.Play(UiSoundEvent.CloseOptionMenu);
        }

        try
        {
            await Task.Delay(ShellControlCenter.CloseDuration, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested && !_profilePopoverOpen)
            {
                ProfilePopoverSurface.IsVisible = false;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnProfilePopoverScrimPressed(object? sender, PointerPressedEventArgs args)
    {
        args.Handled = true;
        _ = CloseProfilePopoverAsync();
    }

    private async Task OnProfilePanelItemActivatedAsync(
        ShellFunctionPanelItem? item,
        bool fromControlCenter)
    {
        if (item?.Tag is not ShellProfilePanelActionTag actionTag)
        {
            return;
        }

        ShellUiSounds.Play(UiSoundEvent.Enter);
        if (fromControlCenter)
        {
            await ControlCenterHost.CloseAsync().ConfigureAwait(true);
        }
        else
        {
            await CloseProfilePopoverAsync(playSound: false).ConfigureAwait(true);
        }

        switch (actionTag.Action)
        {
            case ShellProfilePanelAction.ViewProfile:
                await ShowSystemDialogAsync(new ShellDialogRequest
                {
                    Presentation = ShellDialogPresentation.Popup,
                    Title = LocalProfileDisplayName(),
                    Body = "Local Prosperismo profile. PlayStation Network services are offline.",
                    Positive = new ShellDialogButton(ShellDialogAction.Positive, "Close"),
                }).ConfigureAwait(true);
                break;

            case ShellProfilePanelAction.ViewTrophies:
                if (GameList.SelectedItem is GameEntry game)
                {
                    await OpenDesktopTrophiesAsync(game).ConfigureAwait(true);
                }
                else
                {
                    await ShowSystemDialogAsync(new ShellDialogRequest
                    {
                        Presentation = ShellDialogPresentation.Popup,
                        Title = "Trophies",
                        Body = "Select an installed game to view its local trophy data.",
                        Positive = new ShellDialogButton(ShellDialogAction.Positive, "Close"),
                    }).ConfigureAwait(true);
                }
                break;

            case ShellProfilePanelAction.SwitchUser:
                await ShowSystemDialogAsync(new ShellDialogRequest
                {
                    Presentation = ShellDialogPresentation.Popup,
                    Title = "Switch User",
                    Body = "No additional local Prosperismo profile is configured.",
                    Positive = new ShellDialogButton(ShellDialogAction.Positive, "Close"),
                }).ConfigureAwait(true);
                break;

            case ShellProfilePanelAction.LogOut:
                var result = await ShowSystemDialogAsync(new ShellDialogRequest
                {
                    Presentation = ShellDialogPresentation.Popup,
                    Title = "Log Out?",
                    Body = "End this Big Picture session and return to the desktop launcher.",
                    Neutral = new ShellDialogButton(ShellDialogAction.Neutral, "Cancel"),
                    Positive = new ShellDialogButton(ShellDialogAction.Positive, "Log Out"),
                }).ConfigureAwait(true);
                if (result == ShellDialogAction.Positive)
                {
                    RestartInDesktopLauncher();
                }
                break;
        }
    }

    private void RefreshOpenControlCenterNotifications()
    {
        if (!_controlCenterNotificationsOpen ||
            _controlCenterNotificationScreen != ShellNotificationPanelScreen.List ||
            ControlCenterHost.PanelOwnerId != "notifications")
        {
            return;
        }

        ControlCenterHost.UpdateOpenPanelItems(
            "notifications",
            ShellNotificationListComposer.Compose(
                ShellNotificationHistory.Snapshot(),
                _controlCenterNotificationsDoNotDisturb));
    }

    private void ResetControlCenterNotificationNavigation()
    {
        _controlCenterNotificationScreen = ShellNotificationPanelScreen.List;
        _controlCenterNotificationDetailId = null;
        _controlCenterNotificationFocusedId = null;
        _controlCenterNotificationFocusedIndex = 0;
    }

    private ShellNotificationHistoryEntry? FindControlCenterNotification(string? entryId) =>
        string.IsNullOrWhiteSpace(entryId)
            ? null
            : ShellNotificationHistory.Snapshot().FirstOrDefault(entry =>
                string.Equals(entry.Id, entryId, StringComparison.Ordinal));

    private void ShowControlCenterNotificationDetail(string entryId)
    {
        if (FindControlCenterNotification(entryId) is not { } entry)
        {
            RefreshOpenControlCenterNotifications();
            return;
        }

        ShellNotificationHistory.MarkSeen(entry.Id);
        _controlCenterNotificationScreen = ShellNotificationPanelScreen.Detail;
        _controlCenterNotificationDetailId = entry.Id;
        ControlCenterHost.ReplaceOpenPanelScreen(
            "notifications",
            "Details",
            ShellNotificationPanelComposer.ComposeDetail(entry));
    }

    private void ShowControlCenterNotificationList(bool markDetailRead = true)
    {
        if (markDetailRead && _controlCenterNotificationDetailId is { } detailId)
        {
            ShellNotificationHistory.MarkRead(detailId);
        }

        _controlCenterNotificationScreen = ShellNotificationPanelScreen.List;
        _controlCenterNotificationDetailId = null;
        var history = ShellNotificationHistory.Snapshot();
        var historyIndex = -1;
        for (var index = 0; index < history.Count; index++)
        {
            if (string.Equals(
                history[index].Id,
                _controlCenterNotificationFocusedId,
                StringComparison.Ordinal))
            {
                historyIndex = index;
                break;
            }
        }

        var selectedIndex = historyIndex >= 0
            ? historyIndex + 1
            : Math.Clamp(_controlCenterNotificationFocusedIndex, 0, history.Count);
        var selectedHistoryIndex = selectedIndex - 1;
        _controlCenterNotificationFocusedIndex = selectedIndex;
        _controlCenterNotificationFocusedId = selectedHistoryIndex >= 0 &&
            selectedHistoryIndex < history.Count
            ? history[selectedHistoryIndex].Id
            : null;
        if (_controlCenterNotificationFocusedId is { } focusedId &&
            ShellNotificationHistory.MarkSeen(focusedId))
        {
            history = ShellNotificationHistory.Snapshot();
        }
        ControlCenterHost.ReplaceOpenPanelScreen(
            "notifications",
            "Notifications",
            ShellNotificationListComposer.Compose(
                history,
                _controlCenterNotificationsDoNotDisturb),
            selectedIndex);
    }

    private void ShowControlCenterNotificationOptions()
    {
        if (!_controlCenterNotificationsOpen ||
            ControlCenterHost.PanelOwnerId != "notifications")
        {
            return;
        }

        switch (_controlCenterNotificationScreen)
        {
            case ShellNotificationPanelScreen.List:
                if (ControlCenterHost.SelectedPanelItem?.Tag is ShellNotificationHistoryTag selected)
                {
                    _controlCenterNotificationFocusedId = selected.EntryId;
                    _controlCenterNotificationFocusedIndex = ControlCenterHost.SelectedPanelIndex;
                }
                _controlCenterNotificationScreen = ShellNotificationPanelScreen.ListOptions;
                ControlCenterHost.ReplaceOpenPanelScreen(
                    "notifications",
                    "Options",
                    ShellNotificationPanelComposer.ComposeListOptions(
                        ShellNotificationHistory.Snapshot(),
                        _controlCenterNotificationFocusedId));
                ShellUiSounds.Play(UiSoundEvent.OpenOptionMenu);
                break;
            case ShellNotificationPanelScreen.Detail when _controlCenterNotificationDetailId is { } detailId:
                _controlCenterNotificationScreen = ShellNotificationPanelScreen.DetailOptions;
                ControlCenterHost.ReplaceOpenPanelScreen(
                    "notifications",
                    "Options",
                    ShellNotificationPanelComposer.ComposeDetailOptions(detailId));
                ShellUiSounds.Play(UiSoundEvent.OpenOptionMenu);
                break;
        }
    }

    private void DeleteFocusedControlCenterNotification()
    {
        if (!_controlCenterNotificationsOpen)
        {
            return;
        }

        if (_controlCenterNotificationScreen == ShellNotificationPanelScreen.List &&
            ControlCenterHost.SelectedPanelItem?.Tag is ShellNotificationHistoryTag selected)
        {
            ShellNotificationHistory.MarkDeleted(selected.EntryId);
            ShowControlCenterNotificationList(markDetailRead: false);
            ShellUiSounds.Play(UiSoundEvent.Enter);
        }
        else if (_controlCenterNotificationScreen == ShellNotificationPanelScreen.Detail &&
                 _controlCenterNotificationDetailId is { } detailId)
        {
            ShellNotificationHistory.MarkDeleted(detailId);
            ShowControlCenterNotificationList(markDetailRead: false);
            ShellUiSounds.Play(UiSoundEvent.Enter);
        }
    }

    private bool HandleControlCenterNotificationBack()
    {
        if (!_controlCenterNotificationsOpen ||
            ControlCenterHost.PanelOwnerId != "notifications")
        {
            return false;
        }

        switch (_controlCenterNotificationScreen)
        {
            case ShellNotificationPanelScreen.Detail:
                ShellUiSounds.Play(UiSoundEvent.Cancel);
                ShowControlCenterNotificationList();
                return true;
            case ShellNotificationPanelScreen.ListOptions:
                ShellUiSounds.Play(UiSoundEvent.CloseOptionMenu);
                ShowControlCenterNotificationList(markDetailRead: false);
                return true;
            case ShellNotificationPanelScreen.DetailOptions:
                ShellUiSounds.Play(UiSoundEvent.CloseOptionMenu);
                if (_controlCenterNotificationDetailId is { } detailId)
                {
                    ShowControlCenterNotificationDetail(detailId);
                }
                return true;
            case ShellNotificationPanelScreen.DeleteAllConfirm:
                ShellUiSounds.Play(UiSoundEvent.Cancel);
                ShowControlCenterNotificationList(markDetailRead: false);
                return true;
            default:
                return false;
        }
    }

    private Task ShowControlCenterPowerAsync()
    {
        var rows = new List<ShellFunctionPanelItem>();
        if (_isRunning && !_isStopping)
        {
            rows.Add(new ShellFunctionPanelItem(
                "Close application",
                tag: "close-current-application"));
        }

        rows.Add(new ShellFunctionPanelItem("Exit Big Picture", tag: "exit-big-picture")
        {
            IsEnabled = !_isRunning && !_isStopping,
        });
        return ControlCenterHost.ShowPanelAsync("power", "Power", rows);
    }

    private async Task OnControlCenterPanelItemActivatedAsync(ShellFunctionPanelItem? item)
    {
        if (item?.Tag is ShellNotificationDoNotDisturbTag)
        {
            var previous = _controlCenterNotificationsDoNotDisturb;
            _controlCenterNotificationsDoNotDisturb = !previous;
            ShellNotificationBroker.DoNotDisturb = _controlCenterNotificationsDoNotDisturb;
            RefreshControlCenterNotificationState();
            if (_controlCenterNotificationsDoNotDisturb &&
                NotificationHost.Current?.Surface is ShellNotificationSurface.Informative or
                    ShellNotificationSurface.Interactive)
            {
                NotificationHost.DismissCurrent();
            }

            ControlCenterHost.UpdateOpenPanelItems(
                "notifications",
                ShellNotificationListComposer.Compose(
                    ShellNotificationHistory.Snapshot(),
                    _controlCenterNotificationsDoNotDisturb,
                    animateDoNotDisturbFrom: previous));
            ShellUiSounds.Play(UiSoundEvent.Enter);
            return;
        }

        if (item?.Tag is ShellProfilePanelActionTag)
        {
            await OnProfilePanelItemActivatedAsync(item, fromControlCenter: true).ConfigureAwait(true);
            return;
        }

        if (item?.Tag is ShellAppSwitcherEntryTag switcherEntry)
        {
            ShowControlCenterAppSwitcherOptions(switcherEntry);
            return;
        }

        if (item?.Tag is ShellAppSwitcherActionTag switcherAction)
        {
            await HandleControlCenterAppSwitcherActionAsync(switcherAction).ConfigureAwait(true);
            return;
        }

        switch (item?.Tag as string)
        {
            case "view-current-notification":
                await ControlCenterHost.CloseAsync();
                NotificationHost.ActivateFromController();
                break;
            case "close-current-application":
                await ControlCenterHost.CloseAsync();
                // RequestStopEmulatorAsync captures and rechecks the current
                // running path after the confirmation dialog (Group 6).
                await RequestStopEmulatorAsync();
                break;
            case "exit-big-picture":
                await ControlCenterHost.CloseAsync();
                var action = await ShowSystemDialogAsync(new ShellDialogRequest
                {
                    Presentation = ShellDialogPresentation.Popup,
                    Title = "Exit Big Picture?",
                    Body = "Return to the desktop launcher.",
                    Neutral = new ShellDialogButton(ShellDialogAction.Neutral, "Cancel"),
                    Positive = new ShellDialogButton(ShellDialogAction.Positive, "Exit"),
                });
                if (action == ShellDialogAction.Positive)
                {
                    RestartInDesktopLauncher();
                }
                break;
        }

        if (item?.Tag is ShellNotificationHistoryTag notificationTag)
        {
            ShowControlCenterNotificationDetail(notificationTag.EntryId);
            return;
        }

        if (item?.Tag is ShellNotificationDetailActionTag actionTag &&
            FindControlCenterNotification(actionTag.EntryId) is { } actionEntry)
        {
            ShellNotificationHistory.MarkRead(actionEntry.Id);
            var action = actionEntry.Request.Actions.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, actionTag.ActionId, StringComparison.Ordinal));
            if (action?.CloseControlCenter == true)
            {
                await ControlCenterHost.CloseAsync();
            }
            var liveInvoked = NotificationHost.Current is { Surface: ShellNotificationSurface.Interactive } current &&
                ShellNotificationHistory.SameIdentity(current, actionEntry.Request) &&
                NotificationHost.InvokeCurrentAction(actionTag.ActionId);
            if (!liveInvoked)
            {
                try
                {
                    action?.OnPress?.Invoke();
                }
                catch (Exception)
                {
                    ShellUiSounds.Play(UiSoundEvent.Error);
                }
            }
            if (ControlCenterHost.IsOpen)
            {
                ShowControlCenterNotificationList(markDetailRead: false);
            }
            return;
        }

        if (item?.Tag is ShellNotificationPanelCommandTag commandTag)
        {
            await HandleControlCenterNotificationCommandAsync(commandTag);
        }
    }

    private async Task HandleControlCenterAppSwitcherActionAsync(
        ShellAppSwitcherActionTag action)
    {
        var game = _allGames.FirstOrDefault(candidate =>
            string.Equals(candidate.Path, action.Path, FilePathComparison));
        if (game is null)
        {
            ShellUiSounds.Play(UiSoundEvent.Error);
            return;
        }

        switch (action.Action)
        {
            case ShellAppSwitcherAction.BackToGame:
                if (ShellTitleOptionsComposer.IsCurrentRunningTitle(
                    game.Path,
                    _runningGamePath,
                    FilePathComparison))
                {
                    await ControlCenterHost.CloseAsync().ConfigureAwait(true);
                }
                break;
            case ShellAppSwitcherAction.CloseGame:
                await ControlCenterHost.CloseAsync().ConfigureAwait(true);
                await RequestStopEmulatorAsync(game).ConfigureAwait(true);
                break;
            case ShellAppSwitcherAction.PlayGame:
                if (!_isRunning && !_isStopping)
                {
                    await ControlCenterHost.CloseAsync().ConfigureAwait(true);
                    GameList.SelectedItem = game;
                    Launch(game.Path, game.Name, game.TitleId);
                }
                break;
            case ShellAppSwitcherAction.GoToGameHub:
                if (!_isRunning && !_isStopping)
                {
                    await ControlCenterHost.CloseAsync().ConfigureAwait(true);
                    SetActivePage(0);
                    GameList.SelectedItem = game;
                    OpenSonyGameHub();
                }
                break;
        }
    }

    private async Task HandleControlCenterNotificationCommandAsync(
        ShellNotificationPanelCommandTag command)
    {
        switch (command.Command)
        {
            case ShellNotificationPanelCommand.DeleteFocused:
            case ShellNotificationPanelCommand.DeleteDetail:
                if (command.EntryId is { } entryId)
                {
                    ShellNotificationHistory.MarkDeleted(entryId);
                }
                ShowControlCenterNotificationList(markDetailRead: false);
                ShellUiSounds.Play(UiSoundEvent.CloseOptionMenu);
                break;
            case ShellNotificationPanelCommand.DeleteAllConfirm:
                _controlCenterNotificationScreen = ShellNotificationPanelScreen.DeleteAllConfirm;
                ControlCenterHost.ReplaceOpenPanelScreen(
                    "notifications",
                    "Notifications",
                    ShellNotificationPanelComposer.ComposeDeleteAllConfirm(),
                    selectedIndex: 1);
                ShellUiSounds.Play(UiSoundEvent.CloseOptionMenu);
                break;
            case ShellNotificationPanelCommand.CancelDeleteAll:
                ShowControlCenterNotificationList(markDetailRead: false);
                ShellUiSounds.Play(UiSoundEvent.Cancel);
                break;
            case ShellNotificationPanelCommand.ConfirmDeleteAll:
                ShellNotificationHistory.MarkAllDeleted();
                ShowControlCenterNotificationList(markDetailRead: false);
                ShellUiSounds.Play(UiSoundEvent.Enter);
                break;
            case ShellNotificationPanelCommand.OpenSettings:
                ShellUiSounds.Play(UiSoundEvent.CloseOptionMenu);
                await ControlCenterHost.CloseAsync();
                SetActivePage(1);
                break;
        }
    }

    /// <summary>
    /// Appends the window's arranged geometry to the file named by
    /// PROSPERISMO_GUI_LAYOUT_DUMP, once a second, for as long as it is up. Off
    /// unless the variable is set.
    ///
    /// This exists because a screenshot is not evidence about layout. A capture
    /// tool that is not per-monitor DPI aware reads back window rects divided by
    /// the display scale, allocates a bitmap that size, and lets the window paint
    /// itself into it at full size, which crops the right and bottom edges and
    /// looks exactly like a shell laid out too large. The numbers below come from
    /// inside the process and settle that question directly: ClientSize is in
    /// device-independent units, and ClientSize times RenderScaling is what the
    /// window really occupies in pixels.
    /// </summary>
    private void StartLayoutDiagnosticsIfRequested()
    {
        var path = Environment.GetEnvironmentVariable("PROSPERISMO_GUI_LAYOUT_DUMP");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"--- {DateTime.Now:HH:mm:ss} state={WindowState} ---");
                sb.AppendLine($"RenderScaling={RenderScaling} ClientSize={ClientSize} " +
                    $"pixels={ClientSize.Width * RenderScaling}x{ClientSize.Height * RenderScaling}");

                foreach (var (name, visual) in new (string, Visual?)[]
                {
                    ("RootLayout", RootLayout),
                    ("MainContent", MainContent),
                    ("ShellSurfaceHost", ShellSurfaceHost),
                    ("LibraryPage", LibraryPage),
                    ("TopNavBand", TopNavBand),
                    ("LaunchBar", LaunchBar),
                    ("StatusBar", StatusBar),
                })
                {
                    sb.AppendLine(visual is null
                        ? $"{name}=<null>"
                        : $"{name} bounds={visual.Bounds} " +
                          $"originInWindow={visual.TranslatePoint(new Point(0, 0), this)}");
                }

                sb.AppendLine($"homeScale={_homeScale}");
                sb.AppendLine($"presentation={_presentationMode} " +
                    $"shellPackage={BigPicturePackage.PackageRelativePath} " +
                    $"defaultGameIcon={(_defaultGameIcon is null ? "missing" : "packaged")}");

                // The focus ring is the one element whose position cannot be
                // read off a screenshot: it is a glow with an 80 px warp budget,
                // so its painted edge is not its rect. These three lines say
                // which control owns it, where it was told to go, and where it
                // is, all in the overlay's own coordinates.
                var ring = ShellFocusRing.For(GameStrand);
                sb.AppendLine(ring is null
                    ? "ring=<none>"
                    : $"ring owner={ring.Owner?.GetType().Name ?? "<null>"} " +
                      $"visible={ring.Timeline.IsVisible} " +
                      $"target={ring.Timeline.TargetRect} current={ring.Timeline.CurrentRect} " +
                      $"originInWindow={ring.TranslatePoint(new Point(0, 0), this)}");
                sb.AppendLine($"strand sel={GameStrand.SelectedIndex} count={GameStrand.Count} " +
                    $"anchorX={GameStrand.FocusAnchorX} tileW={GameStrand.TileWidth} " +
                    $"localFocusRect={GameStrand.FocusHighlightRect} " +
                    $"originInWindow={GameStrand.TranslatePoint(new Point(0, 0), this)}");
                sb.AppendLine($"navband focusedRegion={TopNavBand.FocusedRegion ?? "<null>"} " +
                    $"localFocusRect={TopNavBand.FocusHighlightRect}");

                // Codepoints, not the string: a screenshot cannot tell a real
                // middle dot from the two characters a double-encoded source
                // file turns it into, and this line can.
                sb.AppendLine("versionCodepoints=" + string.Join(
                    ",", (VersionText.Text ?? string.Empty).Select(c => ((int)c).ToString("X4"))));
                File.AppendAllText(path, sb.ToString());
            }
            catch (Exception)
            {
                // a diagnostic must never take the window down with it.
            }
        };
        Closed += (_, _) => timer.Stop();
        timer.Start();
    }

    /// <summary>
    /// Switches between the Library and Options pages. Desktop presentation
    /// game/media spaces through <see cref="NavigateShoulder"/>.
    /// </summary>
    private void SetActivePage(int index)
    {
        if (_presentationMode == ShellPresentationMode.Sony && !_sonyShellReady)
        {
            return;
        }

        if (_presentationMode == ShellPresentationMode.Desktop)
        {
            if (index == 1)
            {
                _ = OpenDesktopGlobalSettingsAsync();
            }
            return;
        }

        if (_profilePopoverOpen)
        {
            _ = CloseProfilePopoverAsync();
        }
        if (_sonySearchOpen)
        {
            CloseSonySearch();
        }

        if (index == _activePageIndex)
        {
            return;
        }

        if (_sonyGameHubOpen)
        {
            CloseSonyGameHub();
        }

        if (_activePageIndex == 1)
        {
            _settings.Save(); // leaving the Options page
        }

        _activePageIndex = index;
        SetActiveClass(LibraryTabButton, index == 0);
        SetActiveClass(OptionsTabButton, index == 1);

        // A scene change is the one motion the shell's own spec names outright:
        // DefaultScreenTransitionCurve, which is EaseSmoothOutBreeze (0.05, 0.4),
        // over TransitionVariety.DefaultAnimationDuration. Both are measured, so
        // this is the whole of the choice made here.
        EnsureScreenTransition(ShellSurfaceHost);
        EnsureScreenTransition(SonySettingsSurfaceHost);
        EnsureScreenTransition(OptionsPage);
        UpdatePageSurfaces();
        UpdateFocusedTitleMedia();

        // Leaving the home closes the search strip with it; coming back must
        // not find it half unfolded.
        _searchStripOpen = false;
        UpdateContentToolbarVisibility();

        if (index == 0)
        {
            Dispatcher.UIThread.Post(RestoreLibraryFocusForPresentation, DispatcherPriority.Loaded);
        }
        else if (SonySettingsSurfaceHost.IsVisible)
        {
            Dispatcher.UIThread.Post(
                () => ((Control)(SonySettingsDetail.IsVisible
                    ? SonySettingsDetail
                    : SonySettingsCategories)).Focus(),
                DispatcherPriority.Loaded);
        }
    }

    private void CloseSonySettingsDetail()
    {
        SonySettingsDetail.IsVisible = false;
        SonySettingsCategories.IsVisible = true;
        Dispatcher.UIThread.Post(() => SonySettingsCategories.Focus(), DispatcherPriority.Loaded);
    }

    private void RestoreLibraryFocusForPresentation()
    {
        if (RestoresDesktopLibraryFocusFor(_presentationMode, _activePageIndex))
        {
            // An empty desktop library hides the list in favour of its add
            // folder prompt, so focus the always-visible search field instead.
            (DesktopLibrary.Games.IsVisible
                ? (Control)DesktopLibrary.Games
                : DesktopLibrary.SearchBox).Focus();
            return;
        }

        RestoreHomeFocus();
    }

    /// <summary>
    /// Keeps NPXS40008's console surface separate from the legacy desktop
    /// allowed to expose the desktop editor; desktop mode retains every
    /// existing emulator control.
    /// </summary>
    internal static bool PersistentBackgroundIsVisibleFor(
        ShellPresentationMode presentationMode,
        int pageIndex) =>
        presentationMode == ShellPresentationMode.Sony &&
        (pageIndex == 0 || pageIndex == 1);

    internal static bool SonyHomeSurfaceIsVisibleFor(
        ShellPresentationMode presentationMode,
        int pageIndex) =>
        presentationMode == ShellPresentationMode.Sony && pageIndex == 0;

    internal static bool DesktopLibrarySurfaceIsVisibleFor(
        ShellPresentationMode presentationMode,
        int pageIndex) =>
        presentationMode == ShellPresentationMode.Desktop && pageIndex == 0;

    internal static bool UsesSonyAmbientFor(ShellPresentationMode presentationMode) =>
        presentationMode == ShellPresentationMode.Sony;

    /// <summary>
    /// cold boot has handed the persistent background to initialized HOME.
    /// </summary>
    internal static bool ShellUiIsReadyFor(
        ShellPresentationMode presentationMode,
        bool sonyShellReady) =>
        presentationMode != ShellPresentationMode.Sony || sonyShellReady;

    internal static bool CanToggleControlCenter(
        ShellPresentationMode presentationMode,
        bool isPsButtonLocked) =>
        presentationMode == ShellPresentationMode.Sony && !isPsButtonLocked;

    internal static bool RestoresDesktopLibraryFocusFor(
        ShellPresentationMode presentationMode,
        int pageIndex) =>
        presentationMode == ShellPresentationMode.Desktop && pageIndex == 0;

    private void UpdatePageSurfaces()
    {
        var sonyUiReady = ShellUiIsReadyFor(_presentationMode, _sonyShellReady);
        var sonyHomeRoute = SonyHomeSurfaceIsVisibleFor(_presentationMode, _activePageIndex);
        var sonyHome = sonyUiReady && sonyHomeRoute;
        var desktopLibrary = DesktopLibrarySurfaceIsVisibleFor(_presentationMode, _activePageIndex);
        var sonySettings =
            sonyUiReady &&
            _activePageIndex == 1 &&
            _presentationMode == ShellPresentationMode.Sony;
        var shellBackground = PersistentBackgroundIsVisibleFor(
            _presentationMode,
            _activePageIndex);

        _pageTransitionCancellation?.Cancel();
        _pageTransitionCancellation?.Dispose();
        _pageTransitionCancellation = new CancellationTokenSource();
        var transitionToken = _pageTransitionCancellation.Token;

        // Keep the outgoing surface attached for the measured screen fade.
        // Hiding its parent immediately used to cut the ambient particles even
        // though an opacity transition had been installed on a child.
        SetPageSurfaceState(ShellSurfaceHost, sonyHome, transitionToken);
        SetPageSurfaceState(ShellBackgroundSurfaceHost, shellBackground, transitionToken);
        LibraryPage.IsVisible = sonyHome;
        LibraryPage.Opacity = sonyHome ? 1 : 0;
        SetPageSurfaceState(DesktopLibrary, desktopLibrary, transitionToken);
        SetPageSurfaceState(SonySettingsSurfaceHost, sonySettings, transitionToken);
        SetPageSurfaceState(
            OptionsPage,
            _activePageIndex == 1 && _presentationMode == ShellPresentationMode.Desktop,
            transitionToken);
        ShellOverlaySurface.IsVisible = sonyUiReady;
        ShellOverlaySurface.IsHitTestVisible = sonyUiReady;

        // route beneath it. Desktop has no hidden BGLayer owner to wake.
        if (UsesSonyAmbientFor(_presentationMode))
        {
            // Settings publishes the base-only composition. Keep the one
            // light_p owner mounted and fade just its particle target so the
            // room/grain clock neither cuts nor restarts on return to HOME.
            // During exclusive cold boot the HOME UI is intentionally hidden,
            // but the native cold-boot particle target must remain visible.
            HomePlate.ParticleOverlayVisible = sonyHomeRoute;
            HomePlate.RefreshAnimationRoute();
        }
    }

    private static void SetPageSurfaceState(
        Control surface,
        bool shown,
        CancellationToken cancellationToken)
    {
        surface.IsHitTestVisible = shown;
        if (shown)
        {
            surface.IsVisible = true;
            surface.Opacity = 1;
            return;
        }

        if (!surface.IsVisible)
        {
            surface.Opacity = 0;
            return;
        }

        surface.Opacity = 0;
        _ = HidePageSurfaceAfterTransitionAsync(surface, cancellationToken);
    }

    private static async Task HidePageSurfaceAfterTransitionAsync(
        Control surface,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Ps5Transitions.Default, cancellationToken).ConfigureAwait(true);
            if (!cancellationToken.IsCancellationRequested && surface.Opacity <= 0.001)
            {
                surface.IsVisible = false;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Gives a page the shell's screen transition, once. The curve is
    /// <see cref="Ps5AnimationCurve.DefaultScreenTransition"/> and the duration
    /// is <c>Ps5Transitions.Default</c>; neither is tunable here, and a page
    /// that already has the transition is left alone so switching back and forth
    /// does not stack them.
    /// </summary>
    /// <param name="page">Page to give the transition to.</param>
    private static void EnsureScreenTransition(Visual page)
    {
        if (page is not Animatable animatable || animatable.Transitions is { Count: > 0 })
        {
            return;
        }

        animatable.Transitions =
        [
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = Ps5Transitions.Default,
                Easing = Ps5AnimationCurve.DefaultScreenTransition,
            },
        ];
    }

    private static void SetActiveClass(Button button, bool active)
    {
        if (active)
        {
            if (!button.Classes.Contains("active"))
            {
                button.Classes.Add("active");
            }
        }
        else
        {
            button.Classes.Remove("active");
        }
    }

    // ---- Home layout: function row + content strand ----------------------

    /// <summary>
    /// Wires the home's one region. The switcher band holds the installed
    /// titles and nothing else; the utility commands that used to sit in it are
    /// duplicates of the nav band's own destinations, and the console keeps
    /// search and settings there rather than in the tile row.
    /// <c>GameList</c> stays behind the row as the selection model every other
    /// code path already reads.
    /// </summary>
    private void SetUpHomeLayout()
    {
        _homeFocus.Add(new ShellFocusRegion(StrandRegion)
        {
            CanMoveUp = true,
            UpCandidate = ShellNavBand.SpaceSwitcherRegion,
            CanMoveDown = true,
            DownCandidate = GameHubCtaRegion,
        });
        _homeFocus.Add(new ShellFocusRegion(GameHubCtaRegion)
        {
            CanMoveUp = true,
            UpCandidate = StrandRegion,
        });
        _homeFocus.Add(new ShellFocusRegion(ShellNavBand.SpaceSwitcherRegion)
        {
            ItemCount = ShellNavBand.ConsoleSpaceIds.Count,
            CanMoveDown = true,
            DownCandidate = StrandRegion,
        });
        _homeFocus.Add(new ShellFocusRegion(ShellNavBand.SystemRegion)
        {
            ItemCount = ShellNavBand.SystemIconsCount,
            CanMoveDown = true,
            DownCandidate = StrandRegion,
        });

        GameStrand.SelectionChanged += OnStrandSelectionChanged;
        // The selected title already presents its resting Hub controls.
        // Activating the tile transfers focus into that existing CTA rail;
        // only activating Play launches the title.
        GameStrand.ItemActivated += (_, _) => OpenSonyGameHub();
        GameStrand.ShowAllRequested += OnShowAllGamesRequested;
        GameHubCta.PrimaryActionRequested += OnGameHubPrimaryActionRequested;
        GameHubCta.OverflowRequested += OnGameHubOverflowRequested;
        GameHubCta.ExitRequested += (_, _) => CloseSonyGameHub();
        SonyAllGames.SelectionChanged += OnSonyAllGamesSelectionChanged;
        SonyAllGames.ItemActivated += OnSonyAllGamesItemActivated;
        SonyAllGames.Closed += (_, _) => CloseSonyAllGames();
        SonyAllGames.EmptyActionInvoked += async (_, _) =>
        {
            await AddFolderAsync();
            if (_sonyAllGamesOpen)
            {
                RefreshSonyAllGamesItems();
            }
        };
        GameStrand.GotFocus += (_, _) =>
        {
            _homeFocus.SetActive(StrandRegion);
            TopNavBand.FocusedRegion = null;
            UpdateHomeRegionVisuals();
        };
        TopNavBand.GotFocus += (_, _) =>
        {
            TopNavBand.FocusedRegion ??= ShellNavBand.SpaceSwitcherRegion;
            _homeFocus.SetActive(TopNavBand.FocusedRegion);
            UpdateHomeRegionVisuals();
        };
        TopNavBand.PropertyChanged += (_, change) =>
        {
            if (change.Property != ShellNavBand.FocusedRegionProperty ||
                !TopNavBand.IsFocused ||
                TopNavBand.FocusedRegion is not { } region)
            {
                return;
            }

            // Pointer movement can switch between the two regions while the
            // band already owns keyboard focus, so GotFocus alone is not enough
            // to keep the named graph authoritative.
            _homeFocus.SetActive(region);
            _homeFocus.Remember(
                region,
                region == ShellNavBand.SystemRegion
                    ? TopNavBand.SelectedSystemIndex
                    : TopNavBand.SpaceCursor);
            UpdateHomeRegionVisuals();
        };
        TopNavBand.EdgeReached += (_, direction) =>
        {
            MoveHomeFocus(direction);
        };

        // The switcher is where the library lives, so it starts as the focused
        // region even before any game has been scanned.
        _homeFocus.SetActive(StrandRegion);
        UpdateHomeRegionVisuals();
        _hubTransition.Attach(HomeSurface, StrandHost);

        if (_sonyShellReady)
        {
            ShowMissingDumpNotice();
        }

        // The initial page is already index zero, so SetActivePage(0) is a
        // no-op. Route it explicitly: Desktop receives its compact library,
        UpdatePageSurfaces();
        UpdateContentToolbarVisibility();

        LibraryPage.SizeChanged += (_, _) => ApplyHomeScale();

        // The Viewbox rescales the whole plate without re-arranging anything
        // inside it, so the row has to be told when the plate moves under the
        // overlay the focus ring draws on.
        ShellSurfaceHost.SizeChanged += (_, _) => RefreshHomeFocusRects();

        // Same reason, different mover: the entrance slides the switcher a whole
        // authored screen (SwitcherTravelX 1920) on a RenderTransform, and a
        // RenderTransform never triggers a layout pass. The ring was handed its
        // rect once, mid-flight, and kept it: measured on the running build it
        // sat at overlay x 647 while the settled tile was at x 232, and the
        // 17.5 px it was low is SwitcherTravelY (31 authored px) exactly. The
        // ring was not drifting and was not mis-indexed - it was frozen on a
        // pose the row had already left.
        _entrance.Moved += (_, _) => RefreshHomeFocusRects();
        _entrance.Finished += (_, _) => RefreshHomeFocusRects();

        ApplyHomeScale();
    }

    /// <summary>
    /// Retains the old lifecycle hook while keeping the bundled-font notice
    /// surface hidden. Typography is deterministic and does not depend on a
    /// </summary>
    private void ShowMissingDumpNotice()
    {
        DumpNotice.IsVisible = false;
    }

    /// <summary>
    /// The switcher caps at MAX_TILES and the console has a whole app behind
    /// opens that surface; the desktop presentation retains its existing
    /// search-strip behaviour.
    /// </summary>
    private void OnShowAllGamesRequested(object? sender, ShellTileEventArgs e)
    {
        if (_presentationMode == ShellPresentationMode.Sony)
        {
            OpenSonyAllGames();
        }
        else
        {
            OpenSearchStrip();
        }
    }

    private void OpenSonyAllGames()
    {
        if (_sonyAllGamesOpen)
        {
            return;
        }

        // The console library is an unfiltered installed-content surface. A
        // desktop search left open must not silently remove games from it.
        _searchStripOpen = false;
        SearchBox.Text = string.Empty;
        UpdateContentToolbarVisibility();

        var selectedPath = (GameStrand.SelectedItem?.Tag as GameEntry)?.Path
            ?? (GameList.SelectedItem as GameEntry)?.Path;
        RefreshSonyAllGamesItems(selectedPath);

        _sonyAllGamesOpen = true;
        HomeSurface.IsVisible = false;
        DumpNotice.IsVisible = false;
        SonyAllGames.IsVisible = true;
        SonyAllGames.IsRegionFocused = true;
        GameStrand.IsRegionFocused = false;
        TopNavBand.FocusedRegion = null;

        Dispatcher.UIThread.Post(() => SonyAllGames.Focus(), DispatcherPriority.Loaded);
    }

    private void CloseSonyAllGames()
    {
        if (!_sonyAllGamesOpen)
        {
            return;
        }

        SyncSonyAllGamesSelectionToHome();
        _sonyAllGamesOpen = false;
        SonyAllGames.IsRegionFocused = false;
        SonyAllGames.IsVisible = false;
        HomeSurface.IsVisible = true;
        ShowMissingDumpNotice();
        RestoreHomeFocus();
    }

    private void RefreshSonyAllGamesItems(string? preferredPath = null)
    {
        preferredPath ??= (SonyAllGames.SelectedItem?.Tag as GameEntry)?.Path
            ?? (GameList.SelectedItem as GameEntry)?.Path;

        SonyAllGames.Items = _allGames.Select(game => new ShellLibraryItem(game.Name, game.Cover ?? _defaultGameIcon, game)
        {
            SubLabel = game.SizeText,
            SizeBytes = game.SizeBytes,
            InstalledAt = GetInstalledAt(game.Path),
        }).ToArray();

        int selectedIndex = SonyAllGames.Count > 0 ? 0 : -1;
        if (preferredPath is not null)
        {
            for (int index = 0; index < SonyAllGames.SortedItems.Count; index++)
            {
                if (SonyAllGames.SortedItems[index].Tag is GameEntry game &&
                    game.Path.Equals(preferredPath, FilePathComparison))
                {
                    selectedIndex = index;
                    break;
                }
            }
        }

        if (selectedIndex >= 0)
        {
            SonyAllGames.SetSelectedIndex(selectedIndex);
        }
    }

    private static DateTime GetInstalledAt(string executablePath)
    {
        try
        {
            return File.GetCreationTime(executablePath);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private void OnSonyAllGamesSelectionChanged(object? sender, ShellLibraryItemEventArgs e)
    {
        if (_sonyAllGamesOpen && e.Item?.Tag is GameEntry game)
        {
            GameList.SelectedItem = game;
        }
    }

    private void OnSonyAllGamesItemActivated(object? sender, ShellLibraryItemEventArgs e)
    {
        if (e.Item?.Tag is not GameEntry game)
        {
            return;
        }

        GameList.SelectedItem = game;
        CloseSonyAllGames();
        LaunchSelected();
    }

    private void SyncSonyAllGamesSelectionToHome()
    {
        if (SonyAllGames.SelectedItem?.Tag is not GameEntry selected)
        {
            return;
        }

        if (_visibleGames.FirstOrDefault(game =>
                game.Path.Equals(selected.Path, FilePathComparison)) is { } visible)
        {
            GameList.SelectedItem = visible;
        }
    }

    /// <summary>
    /// The icon load finished on a background thread; rebuild the surfaces that
    /// baked a glyph in.
    /// </summary>
    private void OnShellIconsLoaded(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(ApplyShellIcons);
    }

    /// <summary>
    /// Swaps the hand-drawn marks for the system shell's own art wherever the
    /// the shoulder-button chips stay lettered L1/R1 either way — the
    /// PlayStation names for those buttons.
    /// </summary>
    private void ApplyShellIcons()
    {
        // The keyguide art draws its own button cap, so it replaces the chip
        // rather than sitting inside it.
        ShowShellIcon(ShellIcon.L1, LeftShoulderHintIcon, LeftShoulderHintChip);
        ShowShellIcon(ShellIcon.R1, RightShoulderHintIcon, RightShoulderHintChip);

    }

    /// <summary>
    /// Points <paramref name="target"/> at an icon's art and hides
    /// <paramref name="fallback"/>, or leaves both as they were when the icon is
    /// unavailable.
    /// </summary>
    private static void ShowShellIcon(ShellIcon icon, Image target, Control fallback)
    {
        if (ShellIcons.TryGet(icon) is not { } art)
        {
            return;
        }

        target.Source = art;
        target.IsVisible = true;
        fallback.IsVisible = false;
    }

    /// <summary>
    /// Shows the launcher's own toolbar only where the shell chrome is not
    /// already saying the same thing. On the home the strip stays folded and
    /// only its search half is ever unfolded, and only while the field is in
    /// use. The Options page has no shell chrome, so it keeps the page switcher
    /// and the library tools; every command stays one click away.
    /// </summary>
    private void UpdateContentToolbarVisibility()
    {
        var home = _activePageIndex == 0;
        var desktop = _presentationMode == ShellPresentationMode.Desktop;
        var sonySettings =
            _activePageIndex == 1 &&
            _presentationMode == ShellPresentationMode.Sony;
        var chromeAllowed = !_isRunning && !_gameFullscreen;

        // Called up over the home, the strip has to carry the page switcher:
        // it is the only way a keyboard-only user reaches the Options page once
        // the shell owns the window, and hiding the tabs behind a pad's R1 would
        // make F10 a toggle that shows nothing useful.
        var summoned = home && _launcherChromeOnHome;

        PageSwitcher.IsVisible = !desktop && !sonySettings && (!home || summoned);
        LibraryToolbar.IsVisible = !desktop && !sonySettings &&
            (!home || summoned || _searchStripOpen);
        ContentToolbar.IsVisible = chromeAllowed &&
            !desktop && !sonySettings && (!home || summoned || _searchStripOpen);
        ApplyLauncherChrome();
    }

    /// <summary>
    /// True while the home screen owns the whole window: the launcher's title
    /// bar, launch bar, console panel and status strip are all folded away and
    /// the 1920x1080 canvas is given the entire client area.
    /// </summary>
    private bool HomeOwnsWindow =>
        (_activePageIndex == 0 ||
         (_activePageIndex == 1 && _presentationMode == ShellPresentationMode.Sony)) &&
        !_isRunning &&
        !_isStopping &&
        !_gameFullscreen &&
        (_activePageIndex == 1 || !_launcherChromeOnHome);

    /// <summary>
    /// Hands the window to the home screen, or gives it back to the launcher.
    ///
    /// <para>On a console the home screen is the whole screen. Every piece of
    /// launcher furniture around the canvas - the PROSPERISMO title bar, the
    /// Console / Launch / Stop bar, the status strip, the "your library is
    /// empty" block's surrounding card - reads as a desktop application framing
    /// a picture of a PS5, and no amount of fidelity inside the canvas survives
    /// it.</para>
    ///
    /// <para><b>The decision.</b> The emulator still needs Launch, Console and
    /// Stop, so the chrome is not deleted, it is moved off the home:</para>
    /// <list type="bullet">
    /// <item>the <b>Options page</b> (R1 on a pad, or F10 then the tab) keeps
    /// every control it always had, chrome and all;</item>
    /// <item><b>F10</b> toggles the launcher chrome back over the home for as
    /// long as it is wanted, which is the deliberate escape hatch for a
    /// keyboard-only user with no pad to press R1 with;</item>
    /// <item>launching a title, or a session running, brings the chrome back on
    /// its own, because a running session is not the home screen.</item>
    /// </list>
    ///
    /// <para>F11's game fullscreen owns the same properties, so this stands
    /// aside entirely while that is in force.</para>
    /// </summary>
    private void ApplyLauncherChrome()
    {
        if (_gameFullscreen || (WindowState == WindowState.FullScreen && _isRunning))
        {
            return;
        }

        if (_presentationMode == ShellPresentationMode.Desktop)
        {
            ExtendClientAreaToDecorationsHint = false;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.Default;
            TitleBar.IsVisible = false;
            StatusBar.IsVisible = false;
            LaunchBar.IsVisible = false;
            ConsolePanel.IsVisible = false;
            Grid.SetRow(BackdropHost, 1);
            Grid.SetRowSpan(BackdropHost, 1);
            Grid.SetRow(MainContent, 1);
            Grid.SetRowSpan(MainContent, 1);
            MainContent.Margin = new Thickness(8);
            return;
        }

        var full = HomeOwnsWindow || WindowState == WindowState.FullScreen;

        // The window's own caption buttons are drawn by the system over the
        // client area, so hiding our title bar alone still leaves a minimise,
        // restore and close cluster sitting in the shell's top-right corner,
        // which is where the console draws its clock. They go with the rest of
        // the chrome. Alt+F4 still closes the window, and F10 brings the caption
        // back, so this does not strand anyone.
        ExtendClientAreaChromeHints = full
            ? ExtendClientAreaChromeHints.NoChrome
            : ExtendClientAreaChromeHints.PreferSystemChrome;

        TitleBar.IsVisible = !full;
        StatusBar.IsVisible = !full;
        LaunchBar.IsVisible = !full;
        ConsolePanel.IsVisible = !full && ConsoleToggle.IsChecked == true && _consoleWindow is null;
        // The canvas gets the whole client area, and the letterbox field goes
        // with it: the plate is drawn inside the canvas, so what is left over is
        // BGTransition's basemat and nothing else. Row 1 alone would leave the
        // title bar's and status strip's rows painted in the launcher's own
        // background colour above and below the shell.
        Grid.SetRow(BackdropHost, full ? 0 : 1);
        Grid.SetRowSpan(BackdropHost, full ? 3 : 1);
        Grid.SetRow(MainContent, full ? 0 : 1);
        Grid.SetRowSpan(MainContent, full ? 3 : 1);
        MainContent.Margin = full || _isRunning
            ? new Thickness(0)
            : new Thickness(32, 24, 32, 20);
    }

    /// <summary>
    /// Uses a borderless full monitor only when its physical shape is 16:9 and
    /// it can provide at least a 1920x1080 target. Other monitors get the
    /// largest centred 16:9 window that fits their working area, avoiding the
    /// black side strips created by maximizing the fixed shell canvas.
    /// </summary>
    private void ApplySonyDisplayPolicy(bool allowFullscreen)
    {
        if (_presentationMode != ShellPresentationMode.Sony || _applyingSonyDisplayPolicy)
        {
            return;
        }

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        _sonyDisplayBounds = screen.Bounds;
        _sonyDisplayWorkingArea = screen.WorkingArea;
        _sonyDisplayScaling = screen.Scaling;
        _applyingSonyDisplayPolicy = true;
        try
        {
            var bounds = screen.Bounds;
            var canFill = CanSonyDisplayFullscreen(bounds, allowFullscreen);
            if (canFill)
            {
                WindowState = WindowState.FullScreen;
                ApplyLauncherChrome();
                return;
            }

            WindowState = WindowState.Normal;
            var work = screen.WorkingArea;
            var windowPixels = ResolveSonyWindowPixelSize(work);
            var pixelWidth = windowPixels.Width;
            var pixelHeight = windowPixels.Height;

            var scaling = Math.Max(0.1, screen.Scaling);
            Width = pixelWidth / scaling;
            Height = pixelHeight / scaling;
            Position = new PixelPoint(
                work.X + ((work.Width - pixelWidth) / 2),
                work.Y + ((work.Height - pixelHeight) / 2));
            ApplyLauncherChrome();
        }
        finally
        {
            _applyingSonyDisplayPolicy = false;
        }
    }

    internal static bool CanSonyDisplayFullscreen(PixelRect bounds, bool allowFullscreen)
    {
        var aspect = bounds.Height > 0 ? bounds.Width / (double)bounds.Height : 0.0;
        return allowFullscreen &&
            bounds.Width >= ShellTargetPixelWidth &&
            bounds.Height >= ShellTargetPixelHeight &&
            Math.Abs(aspect - ShellAspectRatio) <= 0.02;
    }

    internal static PixelSize ResolveSonyWindowPixelSize(PixelRect workingArea)
    {
        var pixelWidth = Math.Min(ShellTargetPixelWidth, Math.Max(1, workingArea.Width));
        var pixelHeight = (int)Math.Floor(pixelWidth / ShellAspectRatio);
        if (pixelHeight > workingArea.Height)
        {
            pixelHeight = Math.Max(1, workingArea.Height);
            pixelWidth = (int)Math.Floor(pixelHeight * ShellAspectRatio);
        }

        return new PixelSize(Math.Max(1, pixelWidth), Math.Max(1, pixelHeight));
    }

    private void OnWindowPositionChangedForSonyDisplay(object? sender, PixelPointEventArgs e) =>
        RefreshSonyDisplayPolicyIfEnvironmentChanged();

    private void OnSonyDisplayEnvironmentChanged(object? sender, EventArgs e) =>
        RefreshSonyDisplayPolicyIfEnvironmentChanged();

    private void RefreshSonyDisplayPolicyIfEnvironmentChanged()
    {
        if (_presentationMode != ShellPresentationMode.Sony || _applyingSonyDisplayPolicy)
        {
            return;
        }

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null ||
            (_sonyDisplayBounds == screen.Bounds &&
             _sonyDisplayWorkingArea == screen.WorkingArea &&
             Math.Abs(_sonyDisplayScaling - screen.Scaling) < 0.001))
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => ApplySonyDisplayPolicy(allowFullscreen: true),
            DispatcherPriority.Loaded);
    }

    /// <summary>Toggles the launcher chrome over the home screen (F10).</summary>
    private void ToggleLauncherChrome()
    {
        // The desktop editor is a separate presentation mode, never an overlay
        if (_activePageIndex == 1 && _presentationMode == ShellPresentationMode.Sony)
        {
            return;
        }

        _launcherChromeOnHome = !_launcherChromeOnHome;
        UpdatePageSurfaces();
        UpdateContentToolbarVisibility();
    }

    /// <summary>
    /// Hands the desktop launcher off to a new instance selected as Big
    /// Picture. The route is process-startup state, not a mutable in-window
    /// skin; only close this instance after the child was successfully started.
    /// </summary>
    private void RestartInBigPicture()
    {
        if (_isRunning || _isStopping)
        {
            StatusBarRight.Text = "Stop the current session before opening Big Picture.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            StatusBarRight.Text = "Big Picture could not find the current executable.";
            return;
        }

        try
        {
            Process.Start(ShellPresentation.CreateBigPictureRestartStartInfo(Environment.ProcessPath));
            Close();
        }
        catch (Exception exception)
        {
            AppendConsoleLine($"[GUI][WARN] Could not open Big Picture: {exception.Message}", WarningLineBrush);
            StatusBarRight.Text = "Big Picture could not be started.";
        }
    }

    /// <summary>Hands Big Picture back to a fresh conventional launcher.</summary>
    private void RestartInDesktopLauncher()
    {
        if (_isRunning || _isStopping || string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return;
        }

        try
        {
            Process.Start(ShellPresentation.CreateDesktopRestartStartInfo(Environment.ProcessPath));
            Close();
        }
        catch (Exception exception)
        {
            AppendConsoleLine(
                $"[GUI][WARN] Could not return to the desktop launcher: {exception.Message}",
                WarningLineBrush);
        }
    }

    /// <summary>Unfolds the search field over the home and puts the caret in
    /// it. The band's search tile is the only way in.</summary>
    private void OpenSearchStrip()
    {
        if (_presentationMode == ShellPresentationMode.Desktop)
        {
            DesktopLibrary.SearchBox.Focus();
            return;
        }

        _searchStripOpen = true;
        UpdateContentToolbarVisibility();

        // The box has to be laid out before it can take the caret.
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Loaded);
    }

    private void OpenSonySearch()
    {
        if (_presentationMode != ShellPresentationMode.Sony || !_sonyShellReady ||
            _isRunning || _isStopping || _sonySearchOpen)
        {
            return;
        }

        if (_sonyGameHubOpen)
        {
            CloseSonyGameHub();
        }
        if (_sonyAllGamesOpen)
        {
            CloseSonyAllGames();
        }
        if (_profilePopoverOpen)
        {
            _ = CloseProfilePopoverAsync(playSound: false);
        }

        _sonySearchOpen = true;
        _homeFocus.SetActive(ShellNavBand.SystemRegion);
        HomeSurface.IsVisible = false;
        DumpNotice.IsVisible = false;
        GameStrand.IsRegionFocused = false;
        TopNavBand.FocusedRegion = null;
        UpdateFocusedTitleMedia(force: true);
        RefreshSonySearchItems();
        SonySearch.Open();
        ShellUiSounds.Play(UiSoundEvent.ChangePanel);
    }

    private void CloseSonySearch()
    {
        if (!_sonySearchOpen)
        {
            return;
        }

        _sonySearchOpen = false;
        SonySearch.Close();
        HomeSurface.IsVisible = true;
        RestoreHomeFocus();
    }

    private void RefreshSonySearchItems()
    {
        if (!_sonySearchOpen)
        {
            return;
        }

        var query = SonySearch.Query;
        var matches = _allGames
            .Where(game =>
                query.Length == 0 ||
                game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (game.TitleId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(ShellSearchMetrics.ItemsPerStrand)
            .Select(game => new ShellSearchItem(game.Name, game.Cover, game))
            .ToArray();
        SonySearch.SetItems(matches);
    }

    /// <summary>Folds the search field away again, unless it still holds a
    /// query the library is filtered by.</summary>
    private void CloseSearchStripIfIdle()
    {
        if (_presentationMode == ShellPresentationMode.Desktop ||
            !_searchStripOpen || !string.IsNullOrEmpty(SearchBox.Text))
        {
            return;
        }

        _searchStripOpen = false;
        UpdateContentToolbarVisibility();
    }

    private string CurrentLibrarySearchText =>
        (_presentationMode == ShellPresentationMode.Desktop
            ? DesktopLibrary.SearchBox.Text
            : SearchBox.Text)?.Trim() ?? string.Empty;

    /// <summary>
    /// The two presentation-owned text boxes are views of one filtered
    /// <see cref="GameList"/>. Keep their text synchronized without creating
    /// another collection or scan path.
    /// </summary>
    private void SynchronizeSearchText(TextBox source, TextBox target)
    {
        if (_syncingSearchBoxes)
        {
            return;
        }

        _syncingSearchBoxes = true;
        try
        {
            if (!string.Equals(source.Text, target.Text, StringComparison.Ordinal))
            {
                target.Text = source.Text;
            }
            RefreshVisibleGames();
        }
        finally
        {
            _syncingSearchBoxes = false;
        }
    }

    private void OnLibrarySearchKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || sender is not TextBox searchBox)
        {
            return;
        }

        searchBox.Text = string.Empty;
        args.Handled = true;
        _searchStripOpen = false;
        UpdateContentToolbarVisibility();
        if (_presentationMode == ShellPresentationMode.Sony)
        {
            RestoreHomeFocus();
        }
    }

    /// <summary>
    /// Pins the switcher band onto the shell's own 1920x1080 constants.
    ///
    /// It reads the surface rather than the display: the canvas is fixed and a
    /// Viewbox letterboxes it, so inside it the factor is 1 and every recovered
    /// number lands at its authored size. The factor survives only for a host
    /// that gives the canvas less room than it asks for.
    /// </summary>
    private void ApplyHomeScale()
    {
        var width = LibraryPage.Bounds.Width;
        var height = LibraryPage.Bounds.Height;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        var scale = Math.Clamp(width / HomeDesignWidth, 0.30, 1.0);

        // The early-out has to know whether the metrics have ever been written,
        // not merely whether the scale changed since the last write. _homeScale
        // starts at 1.0 and the canvas is pinned to exactly 1920 wide, so the
        // very first call computed scale == 1.0, matched the seed, and returned
        // before applying anything. StrandHost.Height was no guard either: the
        // XAML already gives it 168. The row therefore kept FocusAnchorX at its
        // property default of 0 forever, which is the whole of the missing
        // 172 px content inset - the focused tile sat on the canvas edge.
        if (_homeMetricsApplied && Math.Abs(scale - _homeScale) < 0.004)
        {
            return;
        }

        _homeMetricsApplied = true;
        _homeScale = scale;

        // The band is the full canvas width: strandStyle's 172 is carried by the
        // row's own anchor, not by an outer margin, because TITLE_X is measured
        // from the container's left edge and an outer inset would move it too.
        LibraryHome.Margin = new Thickness(0);
        StrandHost.Height = SwitcherBandHeight * scale;

        GameStrand.TileWidth = ShellTileRow.ScaledExperienceSize * scale;
        GameStrand.TileHeight = ShellTileRow.ScaledExperienceSize * scale;
        GameStrand.RestScale = ShellTileRow.ExperienceSize / ShellTileRow.ScaledExperienceSize;
        GameStrand.TileGap = ShellTileRow.DefaultItemMargin * scale;

        // Two different margins: 8 between two resting tiles, 16 either side of
        // the focused one. Collapsing them into one gap is what makes the row
        // read as too airy.
        GameStrand.FocusedMargin = ShellTileRow.DefaultFocusedMargin * scale;
        GameStrand.TileCornerRadius = ShellTileRow.SwitcherStyles.FocusContainerBorderRadius * scale;

        // The console slides the row so the focused tile's left edge sits on
        // SCALED_EXP_MARGIN_LEFT rather than centring it.
        GameStrand.FocusAnchorX = HomeContentMargin * scale;

        // The entrance's travels are authored against the 1920 canvas, so it
        // has to be rescaled with everything else or the row slides in from the
        // wrong place on a smaller window.
        _entrance.Scale = scale;
    }

    /// <summary>
    /// Re-publishes the row's focus rect. The travelling ring lives on the
    /// window's overlay, so its rect is only valid for one mapping from the row
    /// to the window. Scaling the fixed surface changes that mapping without
    /// ever re-arranging the row inside it, so the ring would otherwise stay
    /// behind on the tile it framed at the previous size.
    /// </summary>
    private void RefreshHomeFocusRects()
    {
        GameStrand.RefreshFocusRect();
    }

    /// <summary>Rebuilds the strand from the currently visible games, keeping
    /// its focus on whatever <c>GameList</c> has selected.</summary>
    private void SyncStrandTiles()
    {
        _strandRefreshTimer.Stop();

        var tiles = new List<ShellTile>(_visibleGames.Count);
        foreach (var game in _visibleGames)
        {
            // experienceName and iconPath, and that is the whole tile. The title
            // id goes nowhere: the only place the switcher can print one is
            // behind a devkit setting (HOME m209), so a console never shows it.
            tiles.Add(new ShellTile(
                game.Name,
                icon: game.Cover ?? _defaultGameIcon,
                tag: game));
        }

        var index = GameList.SelectedIndex;
        if (index < 0 && tiles.Count > 0)
        {
            index = 0;
        }

        _syncingStrand = true;
        try
        {
            GameStrand.Items = tiles;
            GameStrand.SetSelectedIndex(index);
        }
        finally
        {
            _syncingStrand = false;
        }

        GameStrand.IsVisible = tiles.Count > 0;

        // The strand caps itself at MAX_TILES, so the focus graph has to count
        // what the row kept rather than what the library holds.
        _homeFocus.SetItemCount(StrandRegion, GameStrand.Count);

        // The entrance plays once, on the first row the shell ever shows. The
        // row rebuilds itself whenever cover art lands, and replaying a 1.45 s
        // staged reveal on every one of those would be a different thing
        // entirely from what the console does.
        if (!_entrancePlayed && tiles.Count > 0)
        {
            _entrancePlayed = true;
            _entrance.Attach(StrandHost, TopNavBand, null);
            _entrance.Scale = _homeScale;
            _entrance.Begin(GameStrand.Count);
        }

        if (tiles.Count > 0 && GameList.SelectedIndex != GameStrand.SelectedIndex)
        {
            GameList.SelectedIndex = GameStrand.SelectedIndex;
        }

        // Cover art and install sizes arrive after the scan. Keep an open
        // Game Library live without forcing it closed or losing its item.
        if (_sonyAllGamesOpen)
        {
            RefreshSonyAllGamesItems();
        }
    }

    /// <summary>Coalesces the strand rebuilds that cover art arriving after a
    /// scan would otherwise cause, so its reveal stagger plays once.</summary>
    private void QueueStrandRefresh()
    {
        _strandRefreshTimer.Stop();
        _strandRefreshTimer.Start();
    }

    private void OnStrandSelectionChanged(object? sender, ShellTileEventArgs e)
    {
        _homeFocus.Remember(StrandRegion, e.Index);
        if (_syncingStrand)
        {
            return;
        }

        // Writing through to the list model is what refreshes the launch bar,
        // the backdrop, the preview music and the run buttons.
        _syncingStrand = true;
        try
        {
            GameList.SelectedIndex = e.Index;
        }
        finally
        {
            _syncingStrand = false;
        }
    }

    /// <summary>
    /// Vertical movement between the two home regions. Edges that name no
    /// candidate — or whose candidate is empty — are clamped, so focus never
    /// wraps off the top of the function row or the bottom of the strand.
    /// </summary>
    private void MoveHomeFocus(ShellFocusDirection direction)
    {
        if (_activePageIndex != 0 || !LibraryPage.IsVisible)
        {
            return;
        }

        // The host CTA rail has only the recovered return route. Its own
        // horizontal movement is handled by ShellGameHubCta; vertical Down is
        // clamped until a real Game Hub content adapter owns more regions.
        if (_sonyGameHubOpen)
        {
            if (direction == ShellFocusDirection.Up)
            {
                CloseSonyGameHub();
            }

            return;
        }

        if (_homeFocus.TryMove(direction, out var region, out var index))
        {
            ApplyHomeFocus(region, index);
        }
    }

    private void ApplyHomeFocus(string region, int index)
    {
        _homeFocus.SetActive(region);

        if (region == GameHubCtaRegion)
        {
            OpenSonyGameHub();
            return;
        }

        if (region == StrandRegion)
        {
            // The nav must stop publishing its rect before the strand claims the
            // one scene-level focus plane. Leaving FocusedRegion populated was
            // what made a top icon and a game card appear focused together.
            TopNavBand.FocusedRegion = null;

            // The row plays the focus cue itself when the index actually
            // changes; a restored last-focused item needs it played here.
            var rowWillSound = GameStrand.SelectedIndex != index;
            GameStrand.SetSelectedIndex(index);
            UpdateHomeRegionVisuals();
            GameStrand.Focus();
            if (!rowWillSound)
            {
                ShellUiSounds.Play(UiSoundEvent.FocusMove);
            }

            return;
        }

        // Up from the experience switcher enters the real PS top band. The old
        // implementation unconditionally focused GameStrand here, immediately
        // firing its GotFocus handler and undoing the graph move.
        GameStrand.IsRegionFocused = false;
        TopNavBand.FocusedRegion = region;
        if (region == ShellNavBand.SystemRegion)
        {
            TopNavBand.SetSelectedSystemIndex(index);
        }
        else
        {
            TopNavBand.SetSpaceCursor(index);
        }

        UpdateHomeRegionVisuals();
        TopNavBand.Focus();
        ShellUiSounds.Play(UiSoundEvent.FocusMove);
    }

    private void RestoreHomeFocus()
    {
        if (_activePageIndex != 0)
        {
            return;
        }

        if (_homeFocus.ActiveRegion is { } region && region != StrandRegion)
        {
            var index = _homeFocus.Find(region)?.LastFocusedItem ?? 0;
            ApplyHomeFocus(region, index);
        }
        else if (GameStrand.IsVisible)
        {
            ApplyHomeFocus(StrandRegion, GameStrand.SelectedIndex);
        }
    }

    /// <summary>
    /// Tells the switcher whether it owns the page's focus. Exactly one region
    /// may claim the scene's single travelling ring, and the row's own GLANCED
    /// versus FOCUSED state hangs off the same flag. Nothing dims the row as a
    /// whole: the only dimming the console applies to the switcher is the
    /// overflow tail mat, which the row draws itself.
    /// </summary>
    private void UpdateHomeRegionVisuals()
    {
        var strandOwnsFocus = _homeFocus.ActiveRegion == StrandRegion;
        GameStrand.IsRegionFocused = strandOwnsFocus;

        // FocusedRegion is also the nav band's permission to claim the shared
        // ShellFocusRing. Keep it null while the row owns the scene.
        if (strandOwnsFocus)
        {
            TopNavBand.FocusedRegion = null;
        }

        UpdateFocusedTitleMedia();
    }

    /// <summary>
    /// Opens the host's minimal focused-title Hub. The actual guest app-module
    /// is still unrecovered, so this surface is limited to actions the current
    /// launcher can establish from its selected <see cref="GameEntry"/> and
    /// session state. It must never synthesize entitlement or network CTAs.
    /// </summary>
    private void OpenSonyGameHub()
    {
        if (_sonyGameHubOpen || _presentationMode != ShellPresentationMode.Sony ||
            GameList.SelectedItem is not GameEntry)
        {
            return;
        }

        RefreshGameHubCta();
        if (GameHubCta.VisibleActionCount == 0)
        {
            // HOME swallows a descent while no hub content can accept focus.
            // A title without an active local action is the equivalent host
            // state; returning focus rather than drawing an inert fake Hub is
            // the honest behaviour.
            _homeFocus.SetActive(StrandRegion);
            UpdateHomeRegionVisuals();
            return;
        }

        _sonyGameHubOpen = true;
        _homeFocus.SetActive(GameHubCtaRegion);
        ApplyGameHubPresentationState();
        HomeSurface.IsHitTestVisible = false;
        GameStrand.IsRegionFocused = false;
        TopNavBand.FocusedRegion = null;
        ShellFocusRing.For(GameStrand)?.Release(GameStrand);
        _hubTransition.Open();

        Dispatcher.UIThread.Post(
            () =>
            {
                GameHubCta.SetSelectedIndex(0);
                GameHubCta.Focus();
                GameHubCta.RefreshFocusRect();
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>Returns the Home focus plane and closes the Hub transition.</summary>
    private void CloseSonyGameHub()
    {
        if (!_sonyGameHubOpen)
        {
            return;
        }

        _sonyGameHubOpen = false;
        ApplyGameHubPresentationState();
        HomeSurface.IsHitTestVisible = true;
        ShellFocusRing.For(GameHubCta)?.Release(GameHubCta);
        _hubTransition.Close();
        _homeFocus.SetActive(StrandRegion);
        UpdateHomeRegionVisuals();

        Dispatcher.UIThread.Post(
            () =>
            {
                GameStrand.Focus();
                GameStrand.RefreshFocusRect();
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Applies positions expressed in the Game Hub's fixed 1920x1080 design
    /// surface. The <c>ShellSurfaceHost</c> Viewbox uniformly scales this entire
    /// surface, including on a letterboxed window, so these values are never
    /// multiplied by a host-size factor here.
    /// </summary>
    private void ApplyGameHubRecoveredLayout()
    {
        GameHubHeader.IsVisible = ShellGameHubLayout.ShowsHeaderInEmbeddedHome;

        var cta = ShellGameHubLayout.CtaOriginInHubSurface;
        GameHubCta.Margin = new Thickness(cta.X, cta.Y, 0, 0);

        var logo = ShellGameHubLayout.ConsoleMeasuredLogoOriginInHubSurface;
        GameHubTitleLogo.Margin = new Thickness(logo.X, logo.Y, 0, 0);
    }

    /// <summary>
    /// Re-composes the Hub CTA from the selected entry and actual launcher
    /// capabilities. Configure is a host-provided extra action, explicitly
    /// labelled ASSUMED in the fidelity docs; Play is the active
    /// <see cref="LaunchSelected"/> capability for an idle local title.
    /// </summary>
    private void RefreshGameHubCta()
    {
        if (GameList.SelectedItem is not GameEntry game)
        {
            GameHubHeader.Title = string.Empty;
            GameHubHeader.Tag = null;
            GameHubHeader.Icon = null;
            GameHubTitleLogo.Logo = null;
            GameHubTitleLogo.DisplayName = null;
            GameHubCta.Model = null;
            _homeFocus.SetItemCount(GameHubCtaRegion, 0);
            ApplyGameHubPresentationState();
            return;
        }

        if (ShellGameHubLayout.ShowsHeaderInEmbeddedHome)
        {
            GameHubHeader.Title = game.Name;
            GameHubHeader.Tag = null;
            GameHubHeader.Icon = game.Cover ?? _defaultGameIcon;
        }
        else
        {
            GameHubHeader.Title = string.Empty;
            GameHubHeader.Tag = null;
            GameHubHeader.Icon = null;
        }
        // Game Hub receives only the independently recovered title-logo
        // two-line display-name fallback; it is never replaced with splash,
        // backdrop, cover, or preview media.
        GameHubTitleLogo.Logo = game.TitleLogo;
        GameHubTitleLogo.DisplayName = game.Name;
        GameHubCta.Model = ShellGameHubCtaComposer.Compose(
            new ShellGameHubHostCapabilities(
                CanLaunch: !_isRunning && !_isStopping && !string.IsNullOrWhiteSpace(game.Path),
                CanConfigureGame: !string.IsNullOrWhiteSpace(game.TitleId)));
        _homeFocus.SetItemCount(GameHubCtaRegion, GameHubCta.VisibleActionCount);
        ApplyGameHubPresentationState();
    }

    /// <summary>
    /// Keeps availability and focus ownership independent. A selected title's
    /// CTA is visible at rest; entering the Hub only enables hit testing and
    /// gives the already-rendered rail active focus.
    /// </summary>
    private void ApplyGameHubPresentationState()
    {
        var state = ShellGameHubPresentationState.Resolve(
            _presentationMode == ShellPresentationMode.Sony,
            GameList.SelectedItem is GameEntry,
            GameHubCta.VisibleActionCount,
            _sonyGameHubOpen);
        GameHubSurface.IsVisible = state.IsVisible;
        GameHubSurface.IsHitTestVisible = state.IsInteractive;
    }

    private void OnGameHubPrimaryActionRequested(
        object? sender,
        ShellGameHubActionRequestedEventArgs e)
    {
        switch (e.Action.Kind)
        {
            case ShellGameHubActionKind.Play:
                LaunchSelected();
                break;
            case ShellGameHubActionKind.ConfigureGame:
                OpenSelectedGameSettings();
                break;
        }
    }

    /// <summary>
    /// Consumes the Hub CTA's typed overflow request in a Hub-owned popup. The
    /// outward event remains a host seam, but HOME's title OPTIONS payload is
    /// neither used nor mutated here.
    /// </summary>
    private void OnGameHubOverflowRequested(
        object? sender,
        ShellGameHubOverflowRequestedEventArgs e)
    {
        GameHubOverflowRequested?.Invoke(this, e);
        if (_presentationMode != ShellPresentationMode.Sony || !_sonyGameHubOpen ||
            GameList.SelectedItem is not GameEntry game)
        {
            return;
        }

        var model = ShellTitleOptionsComposer.ComposeHubOverflow(e.Actions);
        if (model.Items.Count == 0)
        {
            return;
        }

        GameHubOverflowMenu.SetEntries(BindTitleOptions(game, model));
        var anchor = GameHubCta.OverflowAnchor ?? GameHubCta;
        GameHubOverflowMenu.PlacementTarget = anchor;
        GameHubOverflowMenu.HorizontalOffset = 0;
        GameHubOverflowMenu.VerticalOffset = 0;
        GameHubOverflowMenu.Open(anchor);
    }

    // ---- Github http client config ----
    // This is for getting lash commit id
    private static HttpClient CreateGithubHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Prosperismo/1.0");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github.sha"));

        client.DefaultRequestHeaders.Add(
            "X-GitHub-Api-Version",
            "2026-03-10");

        return client;
    }
    private async Task LoadLatestCommitAsync()
    {
        const string apiUrl =
            "https://api.github.com/repos/medfa12/prosperismo/commits/main";

        _latestCommitSha = null;
        LatestCommitHashText.Content = "Loading…";
        LatestCommitHashText.IsEnabled = false;

        try
        {
            using var response = await GithubHttpClient.GetAsync(apiUrl);
            var responseBody =
                (await response.Content.ReadAsStringAsync()).Trim();

            if (!response.IsSuccessStatusCode)
            {
                LatestCommitHashText.Content =
                    $"HTTP {(int)response.StatusCode}";

                ToolTip.SetTip(
                    LatestCommitHashText,
                    string.IsNullOrWhiteSpace(responseBody)
                        ? response.ReasonPhrase
                        : responseBody);

                return;
            }

            if (responseBody.Length < 7)
            {
                LatestCommitHashText.Content = "Invalid response";
                ToolTip.SetTip(LatestCommitHashText, responseBody);
                return;
            }

            // Keep the complete SHA for the URL.
            _latestCommitSha = responseBody;

            // Display only the short SHA.
            LatestCommitHashText.Content =
                responseBody[..Math.Min(7, responseBody.Length)];

            LatestCommitHashText.IsEnabled = true;

            ToolTip.SetTip(
                LatestCommitHashText,
                $"Open commit {_latestCommitSha}");
        }
        catch (TaskCanceledException ex)
        {
            LatestCommitHashText.Content = "Timeout";
            ToolTip.SetTip(LatestCommitHashText, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            LatestCommitHashText.Content = "Connection error";
            ToolTip.SetTip(LatestCommitHashText, ex.Message);
        }
        catch (Exception ex)
        {
            LatestCommitHashText.Content = "Error";
            ToolTip.SetTip(LatestCommitHashText, ex.Message);
        }
    }

    // ---- Controller navigation ----

    private void PollGamepad()
    {
        // DualSense wins when both are connected; XInput covers Xbox pads.
        if (!WindowsDualSenseReader.TryGetState(out var pad) && !WindowsXInputReader.TryGetState(out pad))
        {
            _previousPadButtons = HostGamepadButtons.None;
            return;
        }

        if (_presentationMode == ShellPresentationMode.Sony && !_sonyShellReady)
        {
            // Consume the current state so a button held through cold boot is
            // not interpreted as a fresh press when HOME is revealed.
            _previousPadButtons = pad.Buttons;
            return;
        }

        if (!IsActive)
        {
            // Ignore input while the launcher is in the background, e.g. the
            // game window is focused and using the same controller.
            _previousPadButtons = pad.Buttons;
            return;
        }

        if (_systemModalManager.ActiveDialog is { } dialog)
        {
            var modalNow = Environment.TickCount64;
            var modalLeft = (pad.Buttons & HostGamepadButtons.Left) != 0 || pad.LeftX < 64;
            var modalRight = (pad.Buttons & HostGamepadButtons.Right) != 0 || pad.LeftX > 192;
            var modalPressed = pad.Buttons & ~_previousPadButtons;
            if (ShouldNavigate(modalLeft, ref _navLeftNextAt, modalNow))
            {
                dialog.MoveFocus(-1);
            }
            if (ShouldNavigate(modalRight, ref _navRightNextAt, modalNow))
            {
                dialog.MoveFocus(1);
            }
            if ((modalPressed & HostGamepadButtons.Cross) != 0)
            {
                dialog.ActivateFocused();
            }
            if ((modalPressed & HostGamepadButtons.Circle) != 0)
            {
                dialog.Dismiss();
            }

            _previousPadButtons = pad.Buttons;
            return;
        }

        var systemPressed = pad.Buttons & ~_previousPadButtons;
        if (CanToggleControlCenter(_presentationMode, _systemModalManager.IsPsButtonLocked) &&
            (systemPressed & HostGamepadButtons.PsButton) != 0)
        {
            if (_profilePopoverOpen)
            {
                _ = CloseProfilePopoverAsync(playSound: false);
                ControlCenterHost.Open();
                _previousPadButtons = pad.Buttons;
                return;
            }

            if (ControlCenterHost.IsOpen)
            {
                _ = ControlCenterHost.CloseAsync();
            }
            else
            {
                ControlCenterHost.Open();
            }

            _previousPadButtons = pad.Buttons;
            return;
        }

        if (_profilePopoverOpen)
        {
            var profileNow = Environment.TickCount64;
            var profileUp =
                (pad.Buttons & HostGamepadButtons.Up) != 0 || pad.LeftY < 64;
            var profileDown =
                (pad.Buttons & HostGamepadButtons.Down) != 0 || pad.LeftY > 192;
            if (ShouldNavigate(profileUp, ref _navUpNextAt, profileNow))
            {
                ProfileFunctionPanel.MoveFocus(-1);
            }
            if (ShouldNavigate(profileDown, ref _navDownNextAt, profileNow))
            {
                ProfileFunctionPanel.MoveFocus(1);
            }
            if ((systemPressed & HostGamepadButtons.Cross) != 0)
            {
                ProfileFunctionPanel.ActivateSelected();
            }
            if ((systemPressed & HostGamepadButtons.Circle) != 0)
            {
                _ = CloseProfilePopoverAsync();
            }

            _previousPadButtons = pad.Buttons;
            return;
        }

        // Once open, NPXS40003 owns controller navigation. Interactive toast
        // CTAs resume ownership when the Control Center closes; ordinary title
        // input remains below both system surfaces.
        if (ControlCenterHost.IsOpen)
        {
            var controlCenterNow = Environment.TickCount64;
            var controlCenterLeft =
                (pad.Buttons & HostGamepadButtons.Left) != 0 || pad.LeftX < 64;
            var controlCenterRight =
                (pad.Buttons & HostGamepadButtons.Right) != 0 || pad.LeftX > 192;
            var controlCenterUp =
                (pad.Buttons & HostGamepadButtons.Up) != 0 || pad.LeftY < 64;
            var controlCenterDown =
                (pad.Buttons & HostGamepadButtons.Down) != 0 || pad.LeftY > 192;

            if (_controlCenterAppSwitcherMenu.IsOpen)
            {
                if (ShouldNavigate(controlCenterUp, ref _navUpNextAt, controlCenterNow) &&
                    _controlCenterAppSwitcherMenu.MoveControllerFocus(-1))
                {
                    ShellUiSounds.Play(UiSoundEvent.FocusMove);
                }
                if (ShouldNavigate(controlCenterDown, ref _navDownNextAt, controlCenterNow) &&
                    _controlCenterAppSwitcherMenu.MoveControllerFocus(1))
                {
                    ShellUiSounds.Play(UiSoundEvent.FocusMove);
                }
                if ((systemPressed & HostGamepadButtons.Cross) != 0 &&
                    _controlCenterAppSwitcherMenu.ActivateFromController())
                {
                    ShellUiSounds.Play(UiSoundEvent.Enter);
                }
                if ((systemPressed & (HostGamepadButtons.Circle | HostGamepadButtons.Options)) != 0)
                {
                    _controlCenterAppSwitcherMenu.Close();
                }

                _previousPadButtons = pad.Buttons;
                return;
            }

            if (ShouldNavigate(controlCenterLeft, ref _navLeftNextAt, controlCenterNow))
            {
                ControlCenterHost.MoveHorizontal(
                    -1,
                    allowEdgeWrap: (systemPressed & HostGamepadButtons.Left) != 0);
            }
            if (ShouldNavigate(controlCenterRight, ref _navRightNextAt, controlCenterNow))
            {
                ControlCenterHost.MoveHorizontal(
                    1,
                    allowEdgeWrap: (systemPressed & HostGamepadButtons.Right) != 0);
            }
            if (ShouldNavigate(controlCenterUp, ref _navUpNextAt, controlCenterNow))
            {
                ControlCenterHost.MovePanelFocus(-1);
            }
            if (ShouldNavigate(controlCenterDown, ref _navDownNextAt, controlCenterNow))
            {
                ControlCenterHost.MovePanelFocus(1);
            }
            if ((systemPressed & HostGamepadButtons.Cross) != 0)
            {
                ControlCenterHost.ActivateSelected();
            }
            if ((systemPressed & HostGamepadButtons.Circle) != 0)
            {
                _ = ControlCenterHost.BackAsync();
            }
            if ((systemPressed & HostGamepadButtons.Options) != 0)
            {
                ControlCenterHost.RequestPanelOptions();
            }
            if ((systemPressed & HostGamepadButtons.Square) != 0)
            {
                ControlCenterHost.RequestPanelDelete();
            }

            _previousPadButtons = pad.Buttons;
            return;
        }

        if (NotificationHost.IsInteractiveActive)
        {
            var notificationNow = Environment.TickCount64;
            var notificationLeft =
                (pad.Buttons & HostGamepadButtons.Left) != 0 || pad.LeftX < 64;
            var notificationRight =
                (pad.Buttons & HostGamepadButtons.Right) != 0 || pad.LeftX > 192;
            var notificationUp =
                (pad.Buttons & HostGamepadButtons.Up) != 0 || pad.LeftY < 64;
            var notificationDown =
                (pad.Buttons & HostGamepadButtons.Down) != 0 || pad.LeftY > 192;
            var notificationPressed = pad.Buttons & ~_previousPadButtons;
            if (ShouldNavigate(notificationLeft || notificationUp, ref _navLeftNextAt, notificationNow))
            {
                NotificationHost.MoveControllerFocus(-1);
            }
            if (ShouldNavigate(notificationRight || notificationDown, ref _navRightNextAt, notificationNow))
            {
                NotificationHost.MoveControllerFocus(1);
            }
            if ((notificationPressed & HostGamepadButtons.Cross) != 0)
            {
                NotificationHost.ActivateFromController();
            }
            if ((notificationPressed & HostGamepadButtons.Circle) != 0)
            {
                NotificationHost.BackFromController();
            }

            _previousPadButtons = pad.Buttons;
            return;
        }

        if (_isRunning || _isStopping)
        {
            // The game renders inside the launcher window, so the launcher
            // stays active while playing. The controller belongs to the game
            // then: no navigation, and Circle/B must never stop the session.
            _previousPadButtons = pad.Buttons;
            return;
        }

        if (_activePageIndex == 0 && _presentationMode == ShellPresentationMode.Sony &&
            _sonySearchOpen)
        {
            var searchNow = Environment.TickCount64;
            var searchLeft =
                (pad.Buttons & HostGamepadButtons.Left) != 0 || pad.LeftX < 64;
            var searchRight =
                (pad.Buttons & HostGamepadButtons.Right) != 0 || pad.LeftX > 192;
            var searchUp =
                (pad.Buttons & HostGamepadButtons.Up) != 0 || pad.LeftY < 64;
            var searchDown =
                (pad.Buttons & HostGamepadButtons.Down) != 0 || pad.LeftY > 192;
            var searchPressed = pad.Buttons & ~_previousPadButtons;
            if (ShouldNavigate(searchLeft, ref _navLeftNextAt, searchNow))
            {
                SonySearch.MoveHorizontal(-1);
            }
            if (ShouldNavigate(searchRight, ref _navRightNextAt, searchNow))
            {
                SonySearch.MoveHorizontal(1);
            }
            if (ShouldNavigate(searchUp, ref _navUpNextAt, searchNow))
            {
                SonySearch.MoveVertical(-1);
            }
            if (ShouldNavigate(searchDown, ref _navDownNextAt, searchNow))
            {
                SonySearch.MoveVertical(1);
            }
            if ((searchPressed & HostGamepadButtons.Cross) != 0)
            {
                SonySearch.ActivateSelected();
            }
            if ((searchPressed & HostGamepadButtons.Circle) != 0)
            {
                SonySearch.RequestClose();
            }

            _previousPadButtons = pad.Buttons;
            return;
        }

        var shoulderPressed = pad.Buttons & ~_previousPadButtons;
        if (!_sonyAllGamesOpen && !_sonySearchOpen &&
            (shoulderPressed & HostGamepadButtons.L1) != 0)
        {
            NavigateShoulder(-1);
        }

        if (!_sonyAllGamesOpen && !_sonySearchOpen &&
            (shoulderPressed & HostGamepadButtons.R1) != 0)
        {
            NavigateShoulder(1);
        }

        var now = Environment.TickCount64;
        var left = (pad.Buttons & HostGamepadButtons.Left) != 0 || pad.LeftX < 64;
        var right = (pad.Buttons & HostGamepadButtons.Right) != 0 || pad.LeftX > 192;
        var up = (pad.Buttons & HostGamepadButtons.Up) != 0 || pad.LeftY < 64;
        var down = (pad.Buttons & HostGamepadButtons.Down) != 0 || pad.LeftY > 192;
        var pressed = pad.Buttons & ~_previousPadButtons;

        if (_activePageIndex == 0 && _presentationMode == ShellPresentationMode.Sony &&
            _sonyAllGamesOpen)
        {
            if (ShouldNavigate(left, ref _navLeftNextAt, now))
            {
                SonyAllGames.MoveFocus(ShellFocusDirection.Left);
            }
            if (ShouldNavigate(right, ref _navRightNextAt, now))
            {
                SonyAllGames.MoveFocus(ShellFocusDirection.Right);
            }
            if (ShouldNavigate(up, ref _navUpNextAt, now))
            {
                SonyAllGames.MoveFocus(ShellFocusDirection.Up);
            }
            if (ShouldNavigate(down, ref _navDownNextAt, now))
            {
                SonyAllGames.MoveFocus(ShellFocusDirection.Down);
            }
            if ((pressed & HostGamepadButtons.Cross) != 0)
            {
                SonyAllGames.ActivateSelected();
            }
            if ((pressed & HostGamepadButtons.Circle) != 0)
            {
                CloseSonyAllGames();
            }

            _previousPadButtons = pad.Buttons;
            return;
        }

        if (_activePageIndex == 0 && _presentationMode == ShellPresentationMode.Sony &&
            _sonyGameHubOpen)
        {
            if (ShouldNavigate(left, ref _navLeftNextAt, now))
            {
                GameHubCta.MoveFocus(-1);
            }
            if (ShouldNavigate(right, ref _navRightNextAt, now))
            {
                GameHubCta.MoveFocus(1);
            }
            if (ShouldNavigate(up, ref _navUpNextAt, now))
            {
                CloseSonyGameHub();
            }
            if ((pressed & HostGamepadButtons.Cross) != 0)
            {
                GameHubCta.ActivateSelected();
            }
            if ((pressed & HostGamepadButtons.Circle) != 0)
            {
                CloseSonyGameHub();
            }

            _previousPadButtons = pad.Buttons;
            return;
        }

        if (_activePageIndex == 1 && _presentationMode == ShellPresentationMode.Sony)
        {
            var detail = SonySettingsDetail.IsVisible;
            if (ShouldNavigate(left, ref _navLeftNextAt, now) && detail)
            {
                SonySettingsDetail.MoveHorizontal(-1);
            }
            if (ShouldNavigate(right, ref _navRightNextAt, now) && detail)
            {
                SonySettingsDetail.MoveHorizontal(1);
            }
            if (ShouldNavigate(up, ref _navUpNextAt, now))
            {
                if (detail) SonySettingsDetail.MoveVertical(-1);
                else SonySettingsCategories.MoveSelection(-1);
            }
            if (ShouldNavigate(down, ref _navDownNextAt, now))
            {
                if (detail) SonySettingsDetail.MoveVertical(1);
                else SonySettingsCategories.MoveSelection(1);
            }
            if ((pressed & HostGamepadButtons.Cross) != 0)
            {
                if (detail) SonySettingsDetail.ActivateSelected();
                else SonySettingsCategories.ActivateSelected();
            }
            if ((pressed & HostGamepadButtons.Circle) != 0)
            {
                if (detail) SonySettingsDetail.RequestBack();
                else SonySettingsCategories.RequestBack();
            }

            _previousPadButtons = pad.Buttons;
            return;
        }

        // Desktop Options remains a conventional desktop surface; do not feed
        // it console focus actions or change its existing keyboard/mouse path.
        if (_activePageIndex != 0)
        {
            _previousPadButtons = pad.Buttons;
            return;
        }

        if (ShouldNavigate(left, ref _navLeftNextAt, now))
        {
            if (_homeFocus.ActiveRegion == StrandRegion)
            {
                MoveSelection(-1);
            }
            else
            {
                TopNavBand.MoveFocus(-1);
            }
        }

        if (ShouldNavigate(right, ref _navRightNextAt, now))
        {
            if (_homeFocus.ActiveRegion == StrandRegion)
            {
                MoveSelection(1);
            }
            else
            {
                TopNavBand.MoveFocus(1);
            }
        }

        // Vertical is a region change now, not a grid row: up leaves the
        // strand for the function row, down comes back, and both edges clamp.
        if (ShouldNavigate(up, ref _navUpNextAt, now))
        {
            MoveHomeFocus(ShellFocusDirection.Up);
        }

        if (ShouldNavigate(down, ref _navDownNextAt, now))
        {
            MoveHomeFocus(ShellFocusDirection.Down);
        }

        if ((pressed & HostGamepadButtons.Cross) != 0)
        {
            if (_homeFocus.ActiveRegion == StrandRegion)
            {
                LaunchSelected();
            }
            else
            {
                TopNavBand.ActivateSelected();
            }
        }

        _previousPadButtons = pad.Buttons;
    }

    /// <summary>
    /// L1/R1 to move through its exact game/media spaces and plays
    /// <c>psfx_change_space</c>; the desktop launcher's Library/Options shortcut
    /// remains unchanged.
    /// </summary>
    private void NavigateShoulder(int delta)
    {
        if (_presentationMode == ShellPresentationMode.Sony && _activePageIndex == 0)
        {
            int next = ShellNavBand.AdjacentSpaceIndex(TopNavBand.SelectedSpaceIndex, delta);
            if (next == TopNavBand.SelectedSpaceIndex)
            {
                return;
            }

            TopNavBand.SelectedSpaceIndex = next;
            ShellUiSounds.Play(UiSoundEvent.ChangeSpace);
            return;
        }

        SetActivePage(delta < 0 ? 0 : 1);
    }

    /// <summary>
    /// Edge-triggered with hold-to-repeat: fires on press, then repeats
    /// after 400ms at 130ms intervals while held.
    /// </summary>
    private static bool ShouldNavigate(bool held, ref long nextAt, long now)
    {
        if (!held)
        {
            nextAt = 0;
            return false;
        }

        if (nextAt == 0)
        {
            nextAt = now + 400;
            return true;
        }

        if (now >= nextAt)
        {
            nextAt = now + 130;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Horizontal movement inside whichever home region owns focus, clamped at
    /// both ends. In the strand it walks the selection, which is still the
    /// <c>GameList</c> index every other code path reads.
    /// </summary>
    private void MoveSelection(int delta)
    {
        if (_visibleGames.Count == 0)
        {
            return;
        }

        var index = GameList.SelectedIndex < 0
            ? 0
            : Math.Clamp(GameList.SelectedIndex + delta, 0, _visibleGames.Count - 1);
        GameList.SelectedIndex = index;
    }

    private async Task OnOpenedAsync()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var display = version is not null ? $"v{version.ToString(3)}" : "v0.0.1";
        display += BuildInfo.CommitSha is null
            ? " · dev"
            : BuildInfo.IsOfficialRelease
                ? $" · {BuildInfo.CommitSha}"
                : $" · UNOFFICIAL {BuildInfo.CommitSha}";
        VersionText.Text = display;
        Title = _presentationMode == ShellPresentationMode.Desktop
            ? "Prosperismo Launcher"
            : $"Prosperismo {display}";
        ToolTip.SetTip(VersionText, BuildInfo.Banner);
        DesktopLibrary.SettingsFileText = $"Settings file: {GuiSettings.SettingsPath}";
        DesktopLibrary.VersionText = $"Version: {display}";
        // macOS ignores a native fullscreen request made during the window's
        // opening callback before AppKit has committed the first NSWindow.
        // Apply on the next settled UI turn so a suitable 16:9 display really
        // fills instead of remaining at the XAML 1280x720 startup size.
        DispatcherTimer.RunOnce(
            () => ApplySonyDisplayPolicy(allowFullscreen: true),
            TimeSpan.FromMilliseconds(100));

        if (_presentationMode == ShellPresentationMode.Sony)
        {
            await _sonyColdBootCompleted.Task;
        }

        Localization.Instance.Load(_settings.Language);
        PopulateLanguageBox();
        ApplyLocalization();
        ApplySettingsToControls();
        LocateEmulator();
        UpdateDiscordPresence();
        _ = LoadLatestCommitAsync();

        if (_settings.CheckForUpdatesOnStartup)
        {
            _ = CheckForUpdatesAsync();
        }
        await RescanLibraryAsync();

        if (_presentationMode == ShellPresentationMode.Sony)
        {
            _sonyShellReady = true;
            UpdatePageSurfaces();
            UpdateContentToolbarVisibility();
            ShowMissingDumpNotice();
            StartShellUiState();
            UpdateFocusedTitleMedia(force: true);
            ShellAmbientMusic.IsEnabled = _settings.PlayShellMusic;
            ShellAmbientMusic.Volume = _settings.ShellMusicVolume;
            ShellAmbientMusic.Start();
        }

        // After the first asynchronous scan, give focus to the active route:
        // HOME restores its recovered graph, while Desktop targets its visible
        Dispatcher.UIThread.Post(RestoreLibraryFocusForPresentation, DispatcherPriority.Loaded);
    }

    private void PopulateLanguageBox()
    {
        var languages = Localization.Instance.DiscoverLanguages();
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedItem = languages.FirstOrDefault(language =>
            string.Equals(language.Code, _settings.Language, StringComparison.OrdinalIgnoreCase))
            ?? languages.FirstOrDefault();
    }

    private void OnLanguageChanged()
    {
        if (LanguageBox.SelectedItem is not Localization.LanguageInfo language)
        {
            return;
        }

        _settings.Language = language.Code;
        Localization.Instance.Load(language.Code);
        ApplyLocalization();
    }

    /// <summary>
    /// Re-applies every UI string from the current language, so switching
    /// languages in Options takes effect immediately without reopening the
    /// window.
    /// </summary>
    private void ApplyLocalization()
    {
        var loc = Localization.Instance;

        LibraryTabButton.Content = loc.Get("Page.Library");
        OptionsTabButton.Content = loc.Get("Page.Options");

        SearchBox.Watermark = loc.Get("Library.SearchWatermark");
        AddFolderButton.Content = loc.Get("Library.AddFolder");
        RescanButton.Content = loc.Get("Library.Rescan");
        OpenFileButton.Content = loc.Get("Library.OpenFile");
        DesktopLibrary.SearchBox.Watermark = loc.Get("Library.SearchWatermark");
        DesktopLibrary.AddFolderButton.Content = loc.Get("Library.AddFolder");
        DesktopLibrary.RescanButton.Content = loc.Get("Library.Rescan");
        DesktopLibrary.OpenFileButton.Content = loc.Get("Library.OpenFile");

        EmptyAddFolderButton.Content = loc.Get("Library.Empty.AddFolder");
        LoadingStateText.Text = loc.Get("Library.Loading");

        GeneralTabItem.Header = loc.Get("Options.General");
        EnvTabItem.Header = loc.Get("Options.Env.Tab");
        EnvSectionTitle.Text = loc.Get("Options.Section.Environment");
        EnvDesc.Text = loc.Get("Options.Env.Desc");
        EnvBthidRow.Description = loc.Get("Options.Env.Bthid.Desc");
        EnvLoopGuardRow.Description = loc.Get("Options.Env.LoopGuard.Desc");
        EnvWritableApp0Row.Description = loc.Get("Options.Env.WritableApp0.Desc");
        EnvVkValidationRow.Description = loc.Get("Options.Env.VkValidation.Desc");
        EnvDumpSpirvRow.Description = loc.Get("Options.Env.DumpSpirv.Desc");
        EnvLogDirectMemoryRow.Description = loc.Get("Options.Env.LogDirectMemory.Desc");
        EnvLogIoRow.Description = loc.Get("Options.Env.LogIo.Desc");
        EnvLogNpRow.Description = loc.Get("Options.Env.LogNp.Desc");
        EmulationSectionTitle.Text = loc.Get("Options.Section.Emulation");
        LoggingSectionTitle.Text = loc.Get("Options.Section.Logging");
        LauncherSectionTitle.Text = loc.Get("Options.Section.Launcher");

        CpuEngineRow.Label = loc.Get("Options.CpuEngine.Label");
        CpuEngineRow.Description = loc.Get("Options.CpuEngine.Desc");
        CpuEngineNativeItem.Content = loc.Get("Options.CpuEngine.Native");

        StrictRow.Label = loc.Get("Options.Strict.Label");
        StrictRow.Description = loc.Get("Options.Strict.Desc");

        LogLevelRow.Label = loc.Get("Options.LogLevel.Label");
        LogLevelRow.Description = loc.Get("Options.LogLevel.Desc");
        LogLevelTraceItem.Content = loc.Get("Options.LogLevel.Trace");
        LogLevelDebugItem.Content = loc.Get("Options.LogLevel.Debug");
        LogLevelInfoItem.Content = loc.Get("Options.LogLevel.Info");
        LogLevelWarningItem.Content = loc.Get("Options.LogLevel.Warning");
        LogLevelErrorItem.Content = loc.Get("Options.LogLevel.Error");
        LogLevelCriticalItem.Content = loc.Get("Options.LogLevel.Critical");

        TraceImportsRow.Label = loc.Get("Options.TraceImports.Label");
        TraceImportsRow.Description = loc.Get("Options.TraceImports.Desc");

        LogToFileRow.Label = loc.Get("Options.LogToFile.Label");
        LogToFileRow.Description = loc.Get("Options.LogToFile.Desc");

        LogFilePathRow.Label = loc.Get("Options.LogFilePath.Label");
        SelectLogFilePathButton.Content = loc.Get("Options.LogFilePath.Select");
        UpdateLogFilePathText();

        OverrideLogFileRow.Label = loc.Get("Options.OverrideLogFile.Label");
        OverrideLogFileRow.Description = loc.Get("Options.OverrideLogFile.Desc");

        LanguageRow.Label = loc.Get("Options.Language.Label");
        LanguageRow.Description = loc.Get("Options.Language.Desc");

        TitleMusicRow.Label = loc.Get("Options.TitleMusic.Label");
        TitleMusicRow.Description = loc.Get("Options.TitleMusic.Desc");

        ShellMotionRow.Label = loc.Get("Options.ShellMotion.Label");
        ShellMotionRow.Description = loc.Get("Options.ShellMotion.Desc");

        UiSoundsRow.Label = loc.Get("Options.UiSounds.Label");
        UiSoundsRow.Description = loc.Get("Options.UiSounds.Desc");

        DiscordRow.Label = loc.Get("Options.Discord.Label");
        DiscordRow.Description = loc.Get("Options.Discord.Desc");
        AutoUpdateRow.Label = loc.Get("Updater.Auto.Label");
        AutoUpdateRow.Description = loc.Get("Updater.Auto.Desc");

        foreach (var toggle in new[] { StrictToggle, LogToFileToggle, OverrideLogFileToggle, TitleMusicToggle, ShellMotionToggle, UiSoundsToggle, DiscordToggle, AutoUpdateToggle })
        {
            toggle.OnContent = loc.Get("Common.On");
            toggle.OffContent = loc.Get("Common.Off");
        }

        ConsoleSectionTitle.Text = loc.Get("Console.Title");
        ConsoleSearchBox.Watermark = loc.Get("Console.SearchWatermark");
        AutoScrollCheck.Content = loc.Get("Console.AutoScroll");
        DetachConsoleButton.Content = loc.Get("Console.Split");
        CopyLogButton.Content = loc.Get("Console.Copy");
        ClearLogButton.Content = loc.Get("Console.Clear");

        ConsoleToggle.Content = loc.Get("Launch.Console");
        LaunchButton.Content = loc.Get("Launch.Launch");
        StopButton.Content = loc.Get("Launch.Stop");

        AboutSectionTitle.Text = loc.Get("Options.About");
        GithubLabel.Text = loc.Get("About.Github.Label");
        GithubDesc.Text = loc.Get("About.Github.Desc");
        DiscordServerLabel.Text = loc.Get("About.Discord.Label");
        DiscordServerDesc.Text = loc.Get("About.Discord.Desc");
        GithubButton.Content = loc.Get("About.GithubButton");
        DiscordButton.Content = loc.Get("About.DiscordButton");
        UpdateLabel.Text = loc.Get("Updater.Label");
        LatestCommitLabel.Text = loc.Get("About.Github.LatestCommitLabel");
        LatestCommitDescription.Text = loc.Get("About.Github.LatestCommitDescription");
        RefreshUpdateText();

        UpdateEmptyStateTexts();
        UpdateSelectedGameTexts();
    }

    // ---- Discord Rich Presence ----

    /// <summary>
    /// Publishes the launcher state to Discord: browsing while idle, the
    /// running game (with elapsed time) during emulation. No-ops when
    /// disabled or when no Discord application ID is configured.
    /// </summary>
    private void UpdateDiscordPresence()
    {
        if (!_settings.DiscordRichPresence || _settings.DiscordClientId.Length == 0)
        {
            _discord?.Dispose();
            _discord = null;
            return;
        }

        _discord ??= new DiscordRichPresence(_settings.DiscordClientId);
        if (_isRunning && _runningGameName is { } gameName)
        {
            _discord.SetPresence(
                Localization.Instance.Format("Discord.Playing", gameName),
                _runningGameTitleId,
                _runningSinceUnixSeconds);
        }
        else
        {
            // Discord does not render activities without timestamps, so the
            // browsing state carries the launcher's start time.
            var count = _allGames.Count == 1
                ? Localization.Instance.Get("Page.GameCount.One")
                : Localization.Instance.Format("Page.GameCount.Other", _allGames.Count);
            _discord.SetPresence(
                Localization.Instance.Get("Discord.Browsing"),
                count,
                _launcherStartUnixSeconds);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (_presentationMode == ShellPresentationMode.Sony && !_sonyShellReady)
        {
            args.Handled = true;
            return;
        }

        // Host-only keyboard seam for the DualSense PS button. Controller
        // routing uses the confirmed HID bit and both paths obey modal lock.
        if (args.Key == Key.F9 &&
            CanToggleControlCenter(_presentationMode, _systemModalManager.IsPsButtonLocked))
        {
            args.Handled = true;
            if (_profilePopoverOpen)
            {
                _ = CloseProfilePopoverAsync(playSound: false);
                ControlCenterHost.Open();
                return;
            }

            if (ControlCenterHost.IsOpen)
            {
                _ = ControlCenterHost.CloseAsync();
            }
            else
            {
                ControlCenterHost.Open();
            }
            return;
        }

        if (_profilePopoverOpen)
        {
            args.Handled = true;
            switch (args.Key)
            {
                case Key.Up:
                    ProfileFunctionPanel.MoveFocus(-1);
                    break;
                case Key.Down:
                    ProfileFunctionPanel.MoveFocus(1);
                    break;
                case Key.Enter:
                case Key.Space:
                    ProfileFunctionPanel.ActivateSelected();
                    break;
                case Key.Escape:
                case Key.Back:
                    _ = CloseProfilePopoverAsync();
                    break;
                default:
                    args.Handled = false;
                    break;
            }
            return;
        }

        if (ControlCenterHost.IsOpen)
        {
            args.Handled = true;
            switch (args.Key)
            {
                case Key.Left:
                    ControlCenterHost.MoveHorizontal(-1, allowEdgeWrap: false);
                    break;
                case Key.Right:
                    ControlCenterHost.MoveHorizontal(1, allowEdgeWrap: false);
                    break;
                case Key.Up:
                    ControlCenterHost.MovePanelFocus(-1);
                    break;
                case Key.Down:
                    ControlCenterHost.MovePanelFocus(1);
                    break;
                case Key.Enter:
                case Key.Space:
                    ControlCenterHost.ActivateSelected();
                    break;
                case Key.Escape:
                case Key.Back:
                    _ = ControlCenterHost.BackAsync();
                    break;
                default:
                    args.Handled = false;
                    break;
            }
            return;
        }

        args.Handled = true;
        switch (args.Key)
        {
            case Key.F11:
                OnWindowFullScreen(this, new RoutedEventArgs());
                break;

            // The home screen owns the whole window; F10 calls the launcher's
            // own chrome back over it. See ApplyLauncherChrome.
            case Key.F10:
                ToggleLauncherChrome();
                break;

            // The rows own Left/Right; Up/Down cross between the function row
            // and the strand through the focus graph.
            case Key.Up when CanNavigateHome():
                MoveHomeFocus(ShellFocusDirection.Up);
                break;
            case Key.Down when CanNavigateHome():
                MoveHomeFocus(ShellFocusDirection.Down);
                break;
            default:
                args.Handled = false;
                break;
        }
    }

    /// <summary>Whether directional keys belong to the home layout right now,
    /// rather than to a running session or a text/entry control.</summary>
    private bool CanNavigateHome()
    {
        if (_activePageIndex != 0 || _sonyAllGamesOpen || _sonySearchOpen || _profilePopoverOpen ||
            _isRunning || _isStopping || !LibraryPage.IsVisible)
        {
            return false;
        }

        return FocusManager?.GetFocusedElement() is not (TextBox or ComboBox or NumericUpDown);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs args)
    {
        if (_presentationMode == ShellPresentationMode.Sony && !_sonyShellReady)
        {
            args.Handled = true;
            return;
        }

        // While a session is on screen, Enter and Space are game input
        // (Cross button). Keyboard focus stays on the launcher window, so a
        // previously clicked, still-focused button (console toggle, session
        // bar) would also activate and reshape the game view. Swallow the
        // keys before button activation; the emulator process reads raw key
        // state and is unaffected. Fullscreen hides those buttons, which is
        // why this only manifested in windowed sessions.
        if (_isRunning && GameView.IsVisible &&
            !ControlCenterHost.IsOpen &&
            _systemModalManager.ActiveDialog is null &&
            !NotificationHost.IsInteractiveActive &&
            args.Key is Key.Enter or Key.Space)
        {
            args.Handled = true;
        }
    }

    private void OnWindowFullScreen(object sender, RoutedEventArgs args)
    {
        if (WindowState == WindowState.FullScreen)
        {
            // never letterboxes a maximized 1920x1080 shell surface.
            if (_presentationMode == ShellPresentationMode.Sony)
            {
                ApplySonyDisplayPolicy(allowFullscreen: false);
            }
            else
            {
                WindowState = WindowState.Maximized;
            }
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome;
            TitleBar.IsVisible = true;
            StatusBar.IsVisible = true;
            if (_gameFullscreen)
            {
                _gameFullscreen = false;
                Grid.SetRow(MainContent, 1);
                Grid.SetRowSpan(MainContent, 1);
                MainContent.Margin = _isRunning
                    ? new Thickness(0)
                    : new Thickness(32, 24, 32, 20);
                UpdateContentToolbarVisibility();
                ConsolePanel.IsVisible = ConsoleToggle.IsChecked == true && _consoleWindow is null;
                LaunchBar.IsVisible = true;
                QueueGameSurfaceResize();
                UpdateSessionBarVisibility();
            }

            // Restoring the chrome above is only right when the launcher owns
            // the window. Pressing F11 twice on the home screen used to leave
            // the title bar and the status strip painted over a full bleed
            // canvas, with the caption buttons sitting on top of the console's
            // clock, because the block above only re-derived the layout for a
            // game's fullscreen. This re-derives it for every exit.
            UpdateContentToolbarVisibility();
        }
        else
        {
            WindowState = WindowState.FullScreen;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
            TitleBar.IsVisible = false;
            StatusBar.IsVisible = false;
            if (_isRunning && !_isStopping && !_awaitingFirstFrame && GameView.IsVisible)
            {
                // The native child receives its new physical Bounds as soon
                // as this grid spans the monitor. The presenter recreates its
                // swapchain from that size, rather than stretching 720p.
                _gameFullscreen = true;
                // Re-arming restarts the idle countdown, so the cursor also
                // hides a moment after F11 even without further mouse motion.
                _gameSurfaceHost?.SetCursorAutoHide(true);
                Grid.SetRow(MainContent, 0);
                Grid.SetRowSpan(MainContent, 3);
                MainContent.Margin = new Thickness(0);
                UpdateContentToolbarVisibility();
                ConsolePanel.IsVisible = false;
                LaunchBar.IsVisible = false;
                QueueGameSurfaceResize();
                UpdateSessionBarVisibility();
            }
        }
    }

    private void QueueGameSurfaceResize()
    {
        Dispatcher.UIThread.Post(
            () => _gameSurfaceHost?.RefreshSurfaceSize(),
            DispatcherPriority.Render);
    }

    private void OnWindowClosing()
    {
        _settings.Save();
        _consoleFlushTimer.Stop();
        _libraryBlurTimer.Stop();
        _gamepadTimer.Stop();
        _sndPreview.Stop();
        ShellAmbientMusic.Stop();
        _discord?.Dispose();
        _consoleWindow?.Close();
        _emulator?.Dispose();
        _consoleMirror?.Dispose();
        DropFileLog();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    // ---- Settings ----

    private void WireDesktopEmulatorSettingsControls()
    {
        EmulatorResolutionBox.SelectionChanged += (_, _) => UpdateGlobalEmulatorSettingsFromDesktop();
        EmulatorVblankFrequencyBox.ValueChanged += (_, _) => UpdateGlobalEmulatorSettingsFromDesktop();
        EmulatorVulkanValidationToggle.IsCheckedChanged += (_, _) => UpdateGlobalEmulatorSettingsFromDesktop();
        EmulatorShaderValidationToggle.IsCheckedChanged += (_, _) => UpdateGlobalEmulatorSettingsFromDesktop();
        EmulatorShaderOptimizationBox.SelectionChanged += (_, _) => UpdateGlobalEmulatorSettingsFromDesktop();
        EmulatorNggRectlistDrawToggle.IsCheckedChanged += (_, _) => UpdateGlobalEmulatorSettingsFromDesktop();
        EmulatorCommandBufferDumpToggle.IsCheckedChanged += (_, _) => UpdateGlobalEmulatorSettingsFromDesktop();
        EmulatorCommandBufferDumpFolderBox.TextChanged += (_, _) => UpdateGlobalEmulatorSettingsFromDesktop();
        EmulatorRenderDocToggle.IsCheckedChanged += (_, _) => UpdateGlobalEmulatorSettingsFromDesktop();
        EmulatorShaderLogDirectionBox.SelectionChanged += (_, _) => UpdateGlobalEmulatorSettingsFromDesktop();
        EmulatorShaderLogFolderBox.TextChanged += (_, _) => UpdateGlobalEmulatorSettingsFromDesktop();
        EmulatorPrintfDirectionBox.SelectionChanged += (_, _) => UpdateGlobalEmulatorSettingsFromDesktop();
        EmulatorPrintfOutputFileBox.TextChanged += (_, _) => UpdateGlobalEmulatorSettingsFromDesktop();
        EmulatorProfilerDirectionBox.SelectionChanged += (_, _) => UpdateGlobalEmulatorSettingsFromDesktop();
    }

    private void UpdateGlobalEmulatorSettingsFromDesktop()
    {
        if (_syncingEmulatorSettingsControls)
        {
            return;
        }

        var current = _settings.GlobalEmulatorSettings;
        var settings = new EmulatorSettings
        {
            ScreenResolution = SelectedEnum(EmulatorResolutionBox, current.ScreenResolution),
            VblankFrequency = Math.Clamp(
                (int)(EmulatorVblankFrequencyBox.Value ?? current.VblankFrequency),
                EmulatorSettingsContract.MinimumVblankFrequency,
                EmulatorSettingsContract.MaximumVblankFrequency),
            VulkanValidation = EmulatorVulkanValidationToggle.IsChecked == true,
            ShaderValidation = EmulatorShaderValidationToggle.IsChecked == true,
            ShaderOptimization = SelectedEnum(EmulatorShaderOptimizationBox, current.ShaderOptimization),
            ShaderLogDirection = SelectedEnum(EmulatorShaderLogDirectionBox, current.ShaderLogDirection),
            ShaderLogFolder = RequiredText(EmulatorShaderLogFolderBox.Text, current.ShaderLogFolder),
            CommandBufferDump = EmulatorCommandBufferDumpToggle.IsChecked == true,
            CommandBufferDumpFolder = RequiredText(
                EmulatorCommandBufferDumpFolderBox.Text,
                current.CommandBufferDumpFolder),
            PrintfDirection = SelectedEnum(EmulatorPrintfDirectionBox, current.PrintfDirection),
            PrintfOutputFile = RequiredText(EmulatorPrintfOutputFileBox.Text, current.PrintfOutputFile),
            ProfilerDirection = SelectedEnum(EmulatorProfilerDirectionBox, current.ProfilerDirection),
            RenderDoc = EmulatorRenderDocToggle.IsChecked == true,
            NggRectlistDraw = EmulatorNggRectlistDrawToggle.IsChecked == true,
        };

        _settings.GlobalEmulatorSettings = settings;
        SonySettingsDetail.SetEmulatorSettings(settings);
        UpdateDesktopEmulatorSettingDependencies(settings);
    }

    private void ApplyEmulatorSettingsToDesktopControls(EmulatorSettings settings)
    {
        _syncingEmulatorSettingsControls = true;
        try
        {
            SelectComboTag(EmulatorResolutionBox, settings.ScreenResolution.ToString());
            EmulatorVblankFrequencyBox.Value = settings.VblankFrequency;
            EmulatorVulkanValidationToggle.IsChecked = settings.VulkanValidation;
            EmulatorShaderValidationToggle.IsChecked = settings.ShaderValidation;
            SelectComboTag(EmulatorShaderOptimizationBox, settings.ShaderOptimization.ToString());
            EmulatorNggRectlistDrawToggle.IsChecked = settings.NggRectlistDraw;
            EmulatorCommandBufferDumpToggle.IsChecked = settings.CommandBufferDump;
            EmulatorCommandBufferDumpFolderBox.Text = settings.CommandBufferDumpFolder;
            EmulatorRenderDocToggle.IsChecked = settings.RenderDoc;
            SelectComboTag(EmulatorShaderLogDirectionBox, settings.ShaderLogDirection.ToString());
            EmulatorShaderLogFolderBox.Text = settings.ShaderLogFolder;
            SelectComboTag(EmulatorPrintfDirectionBox, settings.PrintfDirection.ToString());
            EmulatorPrintfOutputFileBox.Text = settings.PrintfOutputFile;
            SelectComboTag(EmulatorProfilerDirectionBox, settings.ProfilerDirection.ToString());
        }
        finally
        {
            _syncingEmulatorSettingsControls = false;
        }

        UpdateDesktopEmulatorSettingDependencies(settings);
    }

    private void UpdateDesktopEmulatorSettingDependencies(EmulatorSettings settings)
    {
        EmulatorShaderLogFolderBox.IsEnabled = settings.ShaderLogDirection == EmulatorOutputDirection.File;
        EmulatorCommandBufferDumpFolderBox.IsEnabled = settings.CommandBufferDump;
        EmulatorPrintfOutputFileBox.IsEnabled = settings.PrintfDirection == EmulatorOutputDirection.File;
    }

    private static void SelectComboTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
    }

    private static TEnum SelectedEnum<TEnum>(ComboBox comboBox, TEnum fallback)
        where TEnum : struct, Enum =>
        comboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
        Enum.TryParse<TEnum>(tag, ignoreCase: true, out var value) &&
        Enum.IsDefined(value)
            ? value
            : fallback;

    private static string RequiredText(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private void ApplySettingsToControls()
    {
        ApplyEmulatorSettingsToDesktopControls(_settings.GlobalEmulatorSettings);
        SonySettingsDetail.SetEmulatorSettings(_settings.GlobalEmulatorSettings);
        LogLevelBox.SelectedIndex = _settings.LogLevel.ToLowerInvariant() switch
        {
            "trace" => 0,
            "debug" => 1,
            "info" => 2,
            "warning" or "warn" => 3,
            "error" => 4,
            "critical" or "fatal" => 5,
            _ => 2,
        };
        TraceImportsBox.Value = Math.Clamp(_settings.ImportTraceLimit, 0, 4096);
        RenderResolutionBox.SelectedIndex = _settings.RenderResolutionScale switch
        {
            >= 0.875 => 0,
            >= 0.625 => 1,
            >= 0.375 => 2,
            _ => 3,
        };
        SonySettingsDetail.RenderResolutionScale = _settings.RenderResolutionScale;
        SonySettingsDetail.PlayTitleMusic = _settings.PlayTitleMusic;
        SonySettingsDetail.AnimateShellBackground = _settings.AnimateShellBackground;
        if (UsesSonyAmbientFor(_presentationMode))
        {
            HomePlate.IsMotionEnabled = _settings.AnimateShellBackground;
        }
        SonySettingsDetail.PlayUiSounds = _settings.PlayUiSounds;
        SonySettingsDetail.PlayShellMusic = _settings.PlayShellMusic;
        SonySettingsDetail.PlayBootIntro = BootAnimation.BootIntroPolicy.IsArmed(_settings);
        SonySettingsDetail.DiscordPresence = _settings.DiscordRichPresence;
        SonySettingsDetail.CheckUpdates = _settings.CheckForUpdatesOnStartup;
        SonySettingsDetail.StrictDynlib = _settings.StrictDynlibResolution;
        SonySettingsDetail.LogToFile = _settings.LogToFile;
        SonySettingsDetail.OverrideLogFile = _settings.OverrideLogFile;
        SonySettingsDetail.LogLevel = _settings.LogLevel;
        SonySettingsDetail.ImportTraceLimit = _settings.ImportTraceLimit;
        SonySettingsDetail.LogFilePath = _settings.LogFilePath ?? "Default";
        SonySettingsDetail.LanguageName =
            (LanguageBox.SelectedItem as Localization.LanguageInfo)?.NativeName ?? _settings.Language;
        SonySettingsDetail.SetEnvironmentEnabled("env_bthid", _settings.EnvironmentToggles.Contains("PROSPERISMO_BTHID_UNAVAILABLE"));
        SonySettingsDetail.SetEnvironmentEnabled("env_loop_guard", _settings.EnvironmentToggles.Contains("PROSPERISMO_DISABLE_IMPORT_LOOP_GUARD"));
        SonySettingsDetail.SetEnvironmentEnabled("env_writable_app0", _settings.EnvironmentToggles.Contains("PROSPERISMO_WRITABLE_APP0"));
        SonySettingsDetail.SetEnvironmentEnabled("env_vk_validation", _settings.EnvironmentToggles.Contains("PROSPERISMO_VK_VALIDATION"));
        SonySettingsDetail.SetEnvironmentEnabled("env_dump_spirv", _settings.EnvironmentToggles.Contains("PROSPERISMO_DUMP_SPIRV"));
        SonySettingsDetail.SetEnvironmentEnabled("env_log_direct_memory", _settings.EnvironmentToggles.Contains("PROSPERISMO_LOG_DIRECT_MEMORY"));
        SonySettingsDetail.SetEnvironmentEnabled("env_log_io", _settings.EnvironmentToggles.Contains("PROSPERISMO_LOG_IO"));
        SonySettingsDetail.SetEnvironmentEnabled("env_log_np", _settings.EnvironmentToggles.Contains("PROSPERISMO_LOG_NP"));
        StrictToggle.IsChecked = _settings.StrictDynlibResolution;
        LogToFileToggle.IsChecked = _settings.LogToFile;
        OverrideLogFileToggle.IsChecked = _settings.OverrideLogFile;
        TitleMusicToggle.IsChecked = _settings.PlayTitleMusic;
        ShellMotionToggle.IsChecked = _settings.AnimateShellBackground;
        UiSoundsToggle.IsChecked = _settings.PlayUiSounds;
        ShellMusicToggle.IsChecked = _settings.PlayShellMusic;
        BootIntroToggle.IsChecked = BootAnimation.BootIntroPolicy.IsArmed(_settings);
        // The toggles above only raise IsCheckedChanged when the value
        // differs from the XAML default, so push the loaded values through
        // explicitly.
        ApplyUiSoundSetting();
        DiscordToggle.IsChecked = _settings.DiscordRichPresence;
        AutoUpdateToggle.IsChecked = _settings.CheckForUpdatesOnStartup;
        EnvBthidToggle.IsChecked = _settings.EnvironmentToggles.Contains("PROSPERISMO_BTHID_UNAVAILABLE");
        EnvLoopGuardToggle.IsChecked = _settings.EnvironmentToggles.Contains("PROSPERISMO_DISABLE_IMPORT_LOOP_GUARD");
        EnvWritableApp0Toggle.IsChecked = _settings.EnvironmentToggles.Contains("PROSPERISMO_WRITABLE_APP0");
        EnvVkValidationToggle.IsChecked = _settings.EnvironmentToggles.Contains("PROSPERISMO_VK_VALIDATION");
        EnvDumpSpirvToggle.IsChecked = _settings.EnvironmentToggles.Contains("PROSPERISMO_DUMP_SPIRV");
        EnvLogDirectMemoryToggle.IsChecked = _settings.EnvironmentToggles.Contains("PROSPERISMO_LOG_DIRECT_MEMORY");
        EnvLogIoToggle.IsChecked = _settings.EnvironmentToggles.Contains("PROSPERISMO_LOG_IO");
        EnvLogNpToggle.IsChecked = _settings.EnvironmentToggles.Contains("PROSPERISMO_LOG_NP");
        UpdateLogFilePathText();
    }

    private async Task OnUpdateButtonAsync()
    {
        if (_availableUpdate is null)
        {
            await CheckForUpdatesAsync();
            return;
        }

        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(value =>
                SetUpdateStatus("Updater.Status.Downloading", value));
            await Updater.DownloadAndRestartAsync(_availableUpdate, progress);
            SetUpdateStatus("Updater.Status.Installing");
            Close();
        }
        catch (InvalidDataException)
        {
            SetUpdateStatus("Updater.Status.ChecksumFailed");
            UpdateButton.IsEnabled = true;
        }
        catch
        {
            SetUpdateStatus("Updater.Status.Failed");
            UpdateButton.IsEnabled = true;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        _availableUpdate = null;
        UpdateButton.IsEnabled = false;
        SetUpdateStatus("Updater.Status.Checking");
        try
        {
            _availableUpdate = await Updater.CheckAsync(BuildInfo.CommitSha);
            SetUpdateStatus(
                _availableUpdate is null ? "Updater.Status.Current" : "Updater.Status.Available",
                _availableUpdate?.Sha ?? BuildInfo.CommitSha ?? "dev");
        }
        catch (OperationCanceledException)
        {
            SetUpdateStatus("Updater.Status.Timeout");
        }
        catch (PlatformNotSupportedException)
        {
            SetUpdateStatus("Updater.Status.Unsupported");
        }
        catch
        {
            SetUpdateStatus("Updater.Status.Failed");
        }
        finally
        {
            UpdateButton.IsEnabled = true;
            RefreshUpdateText();
        }
    }

    private void SetUpdateStatus(string key, params object?[] args)
    {
        _updateStatusKey = key;
        _updateStatusArgs = args;
        RefreshUpdateText();
    }

    private void RefreshUpdateText()
    {
        UpdateStatusText.Text = Localization.Instance.Format(_updateStatusKey, _updateStatusArgs);
        UpdateButton.Content = Localization.Instance.Get(
            _availableUpdate is null ? "Updater.Check" : "Updater.DownloadRestart");
    }

    private void SetEnvironmentToggle(string name, bool enabled)
    {
        if (enabled)
        {
            if (!_settings.EnvironmentToggles.Contains(name))
            {
                _settings.EnvironmentToggles.Add(name);
            }
        }
        else
        {
            _settings.EnvironmentToggles.Remove(name);
        }
    }

    private string SelectedLogLevel()
    {
        return LogLevelBox.SelectedIndex switch
        {
            0 => "Trace",
            1 => "Debug",
            2 => "Info",
            3 => "Warning",
            4 => "Error",
            5 => "Critical",
            _ => "Info",
        };
    }

    private void UpdateLogFilePathText()
    {
        LogFilePathRow.Description = string.IsNullOrWhiteSpace(_settings.LogFilePath)
            ? Localization.Instance.Get("Options.LogFilePath.Default")
            : _settings.LogFilePath;
    }

    private async Task SelectLogFilePathAsync()
    {
        var loc = Localization.Instance;
        SaveFilePickerResult result = await StorageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
        {
            Title = loc.Get("Dialog.SaveLogFile"),
            SuggestedFileName = "ProsperismoLog",
            DefaultExtension = "log",
            FileTypeChoices =
                [
                    new FilePickerFileType(loc.Get("Dialog.PlainTextFiles")) { Patterns = ["*.txt"] },
                    new FilePickerFileType(loc.Get("Dialog.LogFiles")) { Patterns = ["*.log"] }
                ]
        });

        if (result.File is not null)
        {
            _settings.LogFilePath = result.File.Path.LocalPath;
            UpdateLogFilePathText();
            SonySettingsDetail.LogFilePath = _settings.LogFilePath;
        }
    }

    private async Task SelectNativeEmulatorSettingPathAsync(string settingId)
    {
        string? selectedPath;
        if (settingId == ShellEmulatorSettingIds.PrintfOutputFile)
        {
            var result = await StorageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
            {
                Title = "Select guest printf output file",
                SuggestedFileName = Path.GetFileName(SonySettingsDetail.PrintfOutputFile),
                DefaultExtension = "txt",
                FileTypeChoices =
                [
                    new FilePickerFileType("Text files") { Patterns = ["*.txt"] },
                    new FilePickerFileType("All files") { Patterns = ["*"] },
                ],
            });
            selectedPath = result.File?.TryGetLocalPath();
        }
        else
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = settingId == ShellEmulatorSettingIds.ShaderLogFolder
                    ? "Select shader log folder"
                    : "Select command-buffer folder",
                AllowMultiple = false,
            });
            selectedPath = folders.FirstOrDefault()?.TryGetLocalPath();
        }

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        switch (settingId)
        {
            case ShellEmulatorSettingIds.ShaderLogFolder:
                SonySettingsDetail.ShaderLogFolder = selectedPath;
                break;
            case ShellEmulatorSettingIds.CommandBufferDumpFolder:
                SonySettingsDetail.CommandBufferDumpFolder = selectedPath;
                break;
            case ShellEmulatorSettingIds.PrintfOutputFile:
                SonySettingsDetail.PrintfOutputFile = selectedPath;
                break;
            default:
                return;
        }

        _settings.GlobalEmulatorSettings = SonySettingsDetail.GetEmulatorSettings();
        ApplyEmulatorSettingsToDesktopControls(_settings.GlobalEmulatorSettings);
    }

    // ---- Emulator discovery ----

    private void LocateEmulator()
    {
        _emulatorExePath = EmulatorInstallationLocator.Locate(
            AppContext.BaseDirectory,
            _settings.EmulatorPath);
        EmulatorPathText.Text = _emulatorExePath is not null
            ? Localization.Instance.Format("Status.EmulatorPath", _emulatorExePath)
            : Localization.Instance.Get("Status.EmulatorNotFound");
        DesktopLibrary.EmulatorText = _emulatorExePath is not null
            ? $"Emulator: {_emulatorExePath}"
            : "Emulator: not found";
    }

    // ---- Game library ----

    private async Task AddFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localization.Instance.Get("Dialog.ChooseGameFolder"),
            AllowMultiple = false,
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var changed = false;
        if (!_settings.GameFolders.Contains(path, FilePathComparer))
        {
            _settings.GameFolders.Add(path);
            changed = true;
        }

        // Adding (or re-adding) a folder is an explicit signal to restore any
        // games beneath it that were removed from the library earlier.
        var prefix = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        changed |= _settings.ExcludedGames.RemoveAll(excluded =>
            excluded.StartsWith(prefix, FilePathComparison)) > 0;

        if (changed)
        {
            _settings.Save();
        }

        await RescanLibraryAsync(offerActions: true);
    }

    private async Task RescanLibraryAsync(bool offerActions = false)
    {
        var generation = ++_libraryScanGeneration;
        var folders = _settings.GameFolders.ToArray();
        var excluded = new HashSet<string>(_settings.ExcludedGames, FilePathComparer);
        StatusBarRight.Text = Localization.Instance.Get("Status.ScanningLibrary");
        EmptyState.IsVisible = false;
        LoadingState.IsVisible = true;

        var games = await Task.Run(() => ScanFolders(folders, excluded));

        // Folder changes and explicit rescans can overlap. Only the newest
        // snapshot is allowed to replace the visible library; otherwise a slow
        // stale scan can resurrect removed games or discard a newly added root.
        if (generation != _libraryScanGeneration)
        {
            return;
        }

        _allGames.Clear();
        _allGames.AddRange(games);
        RefreshVisibleGames();
        LoadingState.IsVisible = false;
        LoadGameDetailsInBackground(games);
        UpdateDiscordPresence();
        StatusBarRight.Text = folders.Length == 0
            ? Localization.Instance.Get("Status.AddFolderPrompt")
            : Localization.Instance.Format("Status.LibraryScanned", games.Count, folders.Length);
        if (folders.Length > 0)
        {
            ShellNotificationBroker.Post(new ShellNotificationRequest
            {
                NotificationId = "library-scan",
                UserId = "local-user",
                BundleName = "Prosperismo.Shell",
                UseCaseId = "library",
                Surface = offerActions
                    ? ShellNotificationSurface.Interactive
                    : ShellNotificationSurface.InApp,
                ReplaceAlways = true,
                PrimaryText = "Game Library updated",
                SecondaryText = $"Found {games.Count} game{(games.Count == 1 ? string.Empty : "s")} in {folders.Length} folder{(folders.Length == 1 ? string.Empty : "s")}.",
                DetailText = offerActions
                    ? "Open the installed-content view, or continue on the home screen."
                    : null,
                Actions = offerActions
                    ?
                    [
                        new ShellNotificationAction(
                            "library",
                            "View game library",
                            OpenSonyAllGames,
                            closeControlCenter: true),
                        new ShellNotificationAction("dismiss", "Dismiss"),
                    ]
                    : Array.Empty<ShellNotificationAction>(),
            });
        }
    }

    /// <summary>
    /// Enriches games off the UI thread — decodes cover art and totals each
    /// game's install folder size — posting results back as they become
    /// ready. A newer scan invalidates older loads.
    /// </summary>
    private void LoadGameDetailsInBackground(IReadOnlyList<GameEntry> games)
    {
        var generation = ++_detailLoadGeneration;
        _ = Task.Run(() =>
        {
            // Covers first: they are cheap and the most visible, so the grid
            // fills with art before the (potentially slow) size pass runs.
            foreach (var game in games)
            {
                if (generation != _detailLoadGeneration)
                {
                    return;
                }

                if (game.CoverPath is null)
                {
                    continue;
                }

                try
                {
                    using var stream = File.OpenRead(game.CoverPath);
                    var bitmap = Bitmap.DecodeToWidth(stream, 312);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (generation == _detailLoadGeneration)
                        {
                            game.Cover = bitmap;
                            QueueStrandRefresh();
                            RefreshSonySearchItems();
                        }
                    });
                }
                catch (Exception)
                {
                    // A missing or undecodable image keeps the placeholder.
                }
            }

            // A logo is a fourth independent title-media channel: it neither
            // waits for the backdrop nor for snd0. Sources are hash-pinned
            // package artwork (PNG-in-SELF or a hash-pinned icon wordmark) and
            // are never copied out of the user's local title.
            foreach (var game in games)
            {
                if (generation != _detailLoadGeneration || game.TitleLogoSource is null)
                {
                    continue;
                }

                try
                {
                    var bitmap = game.TitleLogoSource.TryLoadBitmap();
                    if (bitmap is null)
                    {
                        continue;
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (generation == _detailLoadGeneration)
                        {
                            game.TitleLogo = bitmap;
                            QueueStrandRefresh();
                            // The focused Hub is a sibling of the strand, so
                            // its independent logo channel needs its own
                            // refresh when the asynchronous asset arrives.
                            if (ReferenceEquals(GameList.SelectedItem, game))
                            {
                                RefreshGameHubCta();
                            }
                        }
                    });
                }
                catch (Exception)
                {
                    // A changed or unreadable package retains the plain-text
                    // display-name fallback rather than showing guessed art.
                }
            }

            foreach (var game in games)
            {
                if (generation != _detailLoadGeneration)
                {
                    return;
                }

                var size = ComputeInstallSize(game.Path);
                if (size > 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (generation == _detailLoadGeneration)
                        {
                            game.SizeBytes = size;
                        }
                    });
                }
            }
        });
    }

    /// <summary>
    /// Totals the size of the game's install folder (the directory holding
    /// the eboot), which is far more accurate than the eboot alone.
    /// </summary>
    private static long ComputeInstallSize(string ebootPath)
    {
        var directory = Path.GetDirectoryName(ebootPath);
        if (directory is null)
        {
            return 0;
        }

        long total = 0;
        try
        {
            var enumeration = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
            };
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", enumeration))
            {
                total += file.Length;
            }
        }
        catch (Exception)
        {
            // Fall back to whatever was accumulated so far.
        }

        return total;
    }

    private static List<GameEntry> ScanFolders(IReadOnlyList<string> folders, IReadOnlySet<string> excludedPaths)
    {
        var games = new List<GameEntry>();
        var seen = new HashSet<string>(FilePathComparer);
        var enumeration = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MaxRecursionDepth = 8,
            // trees are not. File-name matching is case-insensitive on the
            // console and must remain so on macOS/Linux hosts.
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "eboot.bin", enumeration))
                {
                    var fullPath = Path.GetFullPath(file);
                    if (!seen.Add(fullPath) || excludedPaths.Contains(fullPath))
                    {
                        continue;
                    }

                    long size = 0;
                    try
                    {
                        size = new FileInfo(fullPath).Length;
                    }
                    catch (IOException)
                    {
                    }

                    var (title, titleId, version, firmwareVersion) = TryReadParamJson(fullPath);
                    games.Add(new GameEntry(
                        title ?? GameNameFor(fullPath), titleId, version, firmwareVersion, fullPath, size,
                        FindCoverFor(fullPath), FindBackgroundFor(fullPath),
                        Ps5TitleArtwork.ResolveEmbeddedTitleLogoForExecutable(fullPath, titleId)));
                }
            }
            catch (Exception)
            {
                // Skip folders that fail to enumerate.
            }
        }

        games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return games;
    }

    /// <summary>
    /// Reads the game title, title id and content version from
    /// sce_sys/param.json next to the executable, when present.
    /// </summary>
    private static (string? Title, string? TitleId, string? Version, string? FirmwareVersion)
        TryReadParamJson(string ebootPath)
    {
        try
        {
            var sceSys = Ps5TitleArtwork.ResolveSystemDirectoryForExecutable(ebootPath);
            if (sceSys is null)
            {
                return (null, null, null, null);
            }

            var paramPath = FindFileIgnoringCase(sceSys, "param.json");
            if (paramPath is null)
            {
                return (null, null, null, null);
            }

            // ReadAllText handles a UTF-8 BOM, which JsonDocument rejects in
            // raw bytes.
            using var document = JsonDocument.Parse(File.ReadAllText(paramPath));
            var root = document.RootElement;

            string? titleId = null;
            if (root.TryGetProperty("titleId", out var idElement) && idElement.ValueKind == JsonValueKind.String)
            {
                titleId = idElement.GetString();
            }

            // Match Kyty's launcher: appVersion is authoritative and
            // contentVersion is the fallback.
            string? version = null;
            if (root.TryGetProperty("appVersion", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.String)
            {
                version = versionElement.GetString();
            }
            else if (root.TryGetProperty("contentVersion", out var contentVersionElement) &&
                     contentVersionElement.ValueKind == JsonValueKind.String)
            {
                version = contentVersionElement.GetString();
            }

            string? firmwareVersion = null;
            if (root.TryGetProperty("requiredSystemSoftwareVersion", out var firmwareElement) &&
                firmwareElement.ValueKind == JsonValueKind.String)
            {
                firmwareVersion = DecodeRequiredSystemSoftwareVersion(firmwareElement.GetString());
            }

            string? title = null;
            if (root.TryGetProperty("localizedParameters", out var localized) &&
                localized.ValueKind == JsonValueKind.Object)
            {
                if (localized.TryGetProperty("defaultLanguage", out var language) &&
                    language.ValueKind == JsonValueKind.String &&
                    localized.TryGetProperty(language.GetString()!, out var defaultBlock) &&
                    defaultBlock.ValueKind == JsonValueKind.Object &&
                    defaultBlock.TryGetProperty("titleName", out var titleName) &&
                    titleName.ValueKind == JsonValueKind.String)
                {
                    title = titleName.GetString();
                }
                else
                {
                    foreach (var property in localized.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Object &&
                            property.Value.TryGetProperty("titleName", out var anyTitleName) &&
                            anyTitleName.ValueKind == JsonValueKind.String)
                        {
                            title = anyTitleName.GetString();
                            break;
                        }
                    }
                }
            }

            return (
                string.IsNullOrWhiteSpace(title) ? null : title,
                string.IsNullOrWhiteSpace(titleId) ? null : titleId,
                string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
                firmwareVersion);
        }
        catch (Exception)
        {
            return (null, null, null, null);
        }
    }

    private static string? DecodeRequiredSystemSoftwareVersion(string? encoded)
    {
        var value = encoded?.Trim();
        if (value is null || value.Length != 18 ||
            !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !value.AsSpan(2, 6).ToString().All(char.IsAsciiDigit) ||
            !value.AsSpan(8).ToString().All(Uri.IsHexDigit))
        {
            return null;
        }

        var digits = value.AsSpan(2, 6);
        var major = int.Parse(digits[..2]);
        var minor = digits.Slice(2, 2).ToString();
        var patch = digits.Slice(4, 2).ToString();
        return patch == "00" ? $"{major}.{minor}" : $"{major}.{minor}.{patch}";
    }

    /// <summary>
    /// Finds the cover art shipped with the game: sce_sys/icon0.png next to
    /// the executable (falling back to pic0.png).
    /// </summary>
    private static string? FindCoverFor(string ebootPath)
    {
        var sceSys = Ps5TitleArtwork.ResolveSystemDirectoryForExecutable(ebootPath);
        if (sceSys is null)
        {
            return null;
        }

        return FindFileIgnoringCase(sceSys, "icon0.png", "pic0.png");
    }

    /// <summary>
    /// Finds the key art shipped with the game (sce_sys/pic0.png, falling
    /// back to pic1.png), used as the window backdrop when selected.
    /// </summary>
    private static string? FindBackgroundFor(string ebootPath)
    {
        return Ps5TitleArtwork.ResolveBackdropForExecutable(ebootPath);
    }

    private static string? FindFileIgnoringCase(string? directory, params string[] candidates)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            var present = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(name))
                {
                    present.TryAdd(name, path);
                }
            }
            foreach (var candidate in candidates)
            {
                if (present.TryGetValue(candidate, out var path))
                {
                    return path;
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static string GameNameFor(string ebootPath)
    {
        var directory = Path.GetDirectoryName(ebootPath);
        var name = directory is not null ? Path.GetFileName(directory) : null;
        return string.IsNullOrEmpty(name) ? Path.GetFileName(ebootPath) : name;
    }

    // ---- Game context menu ----

    /// <summary>
    /// Opens the option menu for the focused strand tile. Hovering a tile
    /// focuses it, so the focused tile is the one under the pointer; with an
    /// empty strand there is nothing to act on and the menu is suppressed.
    /// The menu is anchored to the focused tile's visual, never the pointer,
    /// the way the shell passes the focused tile as the menu's target.
    /// </summary>
    private void OnGameContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!GameStrand.IsVisible || GameStrand.SelectedItem?.Tag is not GameEntry game)
        {
            e.Handled = true;
            return;
        }

        GameList.SelectedItem = game;
        var model = ShellTitleOptionsComposer.ComposeHome(CreateTitleOptionFacts(game));
        GameContextMenu.SetEntries(BindTitleOptions(game, model));
        GameContextMenu.PlacementTarget = FindFocusedTileVisual() ?? StrandHost;

        // The console does not anchor the option menu to the tile itself. It
        // anchors it to a shim (HOME m558) that covers the tile exactly and is
        // then nudged `translateX: -3, translateY: 3`, so the menu sits three
        // pixels left and three down of where the tile alone would put it.
        // Scaled with the surface, because the shim is authored against 1920.
        GameContextMenu.HorizontalOffset = ShellTileRow.OptionsShimOffsetX * _homeScale;
        GameContextMenu.VerticalOffset = ShellTileRow.OptionsShimOffsetY * _homeScale;
    }

    private ShellTitleOptionHostFacts CreateTitleOptionFacts(GameEntry game) => new(
        TitleId: game.TitleId,
        Path: game.Path,
        CanOpenFolder: !string.IsNullOrWhiteSpace(game.Path) && File.Exists(game.Path),
        CanConfigureGame: !string.IsNullOrWhiteSpace(game.TitleId),
        CanRemoveFromLibrary: _allGames.Any(candidate =>
            string.Equals(candidate.Path, game.Path, FilePathComparison)),
        CanCopyPath: Clipboard is not null && !string.IsNullOrWhiteSpace(game.Path),
        CanCopyTitleId: Clipboard is not null && !string.IsNullOrWhiteSpace(game.TitleId),
        IsSelectedTitleRunning: ShellTitleOptionsComposer.IsCurrentRunningTitle(
            game.Path, _runningGamePath, FilePathComparison));

    /// <summary>Binds a freshly composed model to one captured title. Each
    /// callback uses that captured title rather than the mutable selection.</summary>
    private IEnumerable<ShellMenuEntry> BindTitleOptions(
        GameEntry game,
        ShellTitleOptionsModel model)
    {
        foreach (var item in model.Items)
        {
            yield return new ShellMenuEntry(item.Label, () => InvokeTitleOption(game, item.Action))
            {
                MenuId = item.MenuId,
                Icon = item.Icon,
                Section = item.Section,
                IsEnabled = item.IsEnabled,
            };
        }
    }

    private void InvokeTitleOption(GameEntry game, ShellTitleOptionActionKind action)
    {
        switch (action)
        {
            case ShellTitleOptionActionKind.OpenFolder:
                OpenGameFolder(game);
                break;
            case ShellTitleOptionActionKind.ConfigureGame:
                OpenGameSettings(game);
                break;
            case ShellTitleOptionActionKind.RemoveFromLibrary:
                _ = RequestRemoveGameFromLibraryAsync(game);
                break;
            case ShellTitleOptionActionKind.CopyPath:
                _ = CopyToClipboardAsync(game.Path, "Clipboard.Path");
                break;
            case ShellTitleOptionActionKind.CopyTitleId:
                _ = CopyToClipboardAsync(game.TitleId, "Clipboard.TitleId");
                break;
            case ShellTitleOptionActionKind.CloseApplication:
                _ = RequestStopEmulatorAsync(game);
                break;
        }
    }

    private static void WireOptionMenuAudio(ShellContextMenu menu)
    {
        menu.Opening += (_, _) => ShellUiSounds.Play(UiSoundEvent.OpenOptionMenu);
        menu.Closing += (_, _) => ShellUiSounds.Play(UiSoundEvent.CloseOptionMenu);
        menu.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down)
            {
                ShellUiSounds.Play(UiSoundEvent.FocusMove);
            }
        });
        menu.AddHandler(PointerEnteredEvent, (_, _) => ShellUiSounds.Play(UiSoundEvent.FocusMove));
        menu.AddHandler(MenuItem.ClickEvent, (_, _) => ShellUiSounds.Play(UiSoundEvent.Enter));
    }

    /// <summary>
    /// The visual of the focused strand tile, used as the option menu's
    /// anchor. The strand's tiles live on its template surface in item order,
    /// so the focused one is the child at the selected index.
    /// </summary>
    private Control? FindFocusedTileVisual()
    {
        int index = GameStrand.SelectedIndex;
        if (index < 0)
        {
            return null;
        }

        var surface = GameStrand.GetVisualDescendants()
            .OfType<Canvas>()
            .FirstOrDefault(c => c.Name == "PART_Surface");
        return surface is not null && index < surface.Children.Count
            ? surface.Children[index] as Control
            : null;
    }

    private async Task OpenDesktopGlobalSettingsAsync()
    {
        if (_presentationMode != ShellPresentationMode.Desktop || _isRunning || _isStopping)
        {
            return;
        }

        var dialog = new GlobalSettingsDialog(
            _settings.GlobalEmulatorSettings,
            _settings.GameFolders,
            async _ =>
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Add game folders",
                    AllowMultiple = true,
                });
                return folders
                    .Select(folder => folder.TryGetLocalPath())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Cast<string>()
                    .ToArray();
            });
        dialog.Applied += (_, e) =>
        {
            _settings.GlobalEmulatorSettings = e.Settings.Copy();
            _settings.GameFolders = e.GameFolders.ToList();
            _settings.Save();
            ApplyEmulatorSettingsToDesktopControls(_settings.GlobalEmulatorSettings);
            SonySettingsDetail.SetEmulatorSettings(_settings.GlobalEmulatorSettings);
            _ = RescanLibraryAsync(offerActions: false);
        };
        await dialog.ShowDialog(this);
    }

    private async Task ClearCustomGameSettingsAsync(GameEntry game)
    {
        if (_isRunning || _isStopping)
        {
            return;
        }

        var profile = _perGameEmulatorSettings.Load(
            game.Path,
            game.TitleId,
            MatchingTitleInstallPaths(game.TitleId));
        if (profile is null)
        {
            return;
        }

        if (!await DesktopMessageDialog.ConfirmAsync(
                this,
                "Clear custom settings",
                $"Clear the custom emulator settings for \"{game.Name}\"?"))
        {
            return;
        }

        try
        {
            _perGameEmulatorSettings.Delete(game.Path);
            UpdateRunButtons();
        }
        catch (Exception exception)
        {
            await DesktopMessageDialog.ShowAsync(
                this,
                "Clear custom settings",
                $"Could not clear custom settings: {exception.Message}");
        }
    }

    private async Task OpenDesktopTrophiesAsync(GameEntry game)
    {
        if (DesktopTrophyViewerDialog.TryCreate(game, out var error) is { } dialog)
        {
            await dialog.ShowDialog(this);
            return;
        }

        await DesktopMessageDialog.ShowAsync(
            this,
            "Trophy Viewer",
            error ?? "No readable trophy data found.");
    }

    private async Task OpenDesktopPatchesAsync(GameEntry game)
    {
        string? patchError = null;
        if (_emulatorExePath is not null && !string.IsNullOrWhiteSpace(game.TitleId) &&
            DesktopPatchDialog.TryCreate(game.Name, game.TitleId, _emulatorExePath, out patchError) is { } dialog)
        {
            await dialog.ShowDialog(this);
            return;
        }

        await DesktopMessageDialog.ShowAsync(
            this,
            "Patches (Experimental)",
            string.IsNullOrWhiteSpace(game.TitleId)
                ? "This game has no supported title ID."
                : _emulatorExePath is null
                    ? "The native emulator could not be located."
                    : patchError ?? $"No local patch file exists for {game.TitleId}.");
    }

    private async Task RemoveDesktopSaveDataAsync(GameEntry game)
    {
        var directories = DesktopSaveDataDirectories(game).ToArray();
        if (directories.Length == 0)
        {
            await DesktopMessageDialog.ShowAsync(
                this,
                "Remove save data",
                "No save data folder was found for this game.");
            return;
        }

        var message = $"Remove save data for \"{game.Name}\"?\n\nThis will delete:\n" +
            string.Join(Environment.NewLine, directories) +
            "\n\nThis cannot be undone.";
        if (!await DesktopMessageDialog.ConfirmAsync(this, "Remove save data", message))
        {
            return;
        }

        var failures = new List<string>();
        foreach (var directory in directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                failures.Add(directory);
            }
        }

        if (failures.Count > 0)
        {
            await DesktopMessageDialog.ShowAsync(
                this,
                "Remove save data",
                "Could not remove:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }
    }

    private IEnumerable<string> DesktopSaveDataDirectories(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.TitleId))
        {
            yield break;
        }

        var roots = new HashSet<string>(FilePathComparer)
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
        };
        if (_emulatorExePath is not null && Path.GetDirectoryName(_emulatorExePath) is { } emulatorDirectory)
        {
            roots.Add(emulatorDirectory);
        }
        foreach (var root in roots.ToArray())
        {
            if (Directory.GetParent(root)?.FullName is { } parent)
            {
                roots.Add(parent);
            }
        }

        var seen = new HashSet<string>(FilePathComparer);
        foreach (var root in roots)
        {
            var candidate = Path.GetFullPath(Path.Combine(root, "_SaveData", game.TitleId));
            if (Directory.Exists(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private string[] MatchingTitleInstallPaths(string? titleId) =>
        string.IsNullOrWhiteSpace(titleId)
            ? []
            : _allGames
                .Where(candidate => string.Equals(
                    candidate.TitleId,
                    titleId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.Path)
                .ToArray();

    private void ApplyDesktopSort(int column)
    {
        if (column == _desktopSortColumn)
        {
            _desktopSortAscending = !_desktopSortAscending;
        }
        else
        {
            _desktopSortColumn = Math.Clamp(column, 0, 6);
            _desktopSortAscending = true;
        }
        RefreshVisibleGames();
    }

    private void OpenSelectedGameSettings()
    {
        if (GameList.SelectedItem is GameEntry game)
        {
            OpenGameSettings(game);
        }
    }

    private async void OpenGameSettings(GameEntry game)
    {
        var matchingTitleInstallPaths = string.IsNullOrWhiteSpace(game.TitleId)
            ? Array.Empty<string>()
            : _allGames
                .Where(candidate => string.Equals(
                    candidate.TitleId,
                    game.TitleId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.Path)
                .ToArray();
        await new PerGameSettingsDialog(
            game.Path,
            game.TitleId,
            game.Name,
            _settings.GlobalEmulatorSettings,
            _perGameEmulatorSettings,
            matchingTitleInstallPaths,
            useDesktopDesign: _presentationMode == ShellPresentationMode.Desktop).ShowDialog(this);
        UpdateRunButtons();
    }

    private void OpenGameFolder(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.Path) || !File.Exists(game.Path))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{game.Path}\"",
                    UseShellExecute = false,
                });
            }
            else if (Path.GetDirectoryName(game.Path) is { } directory)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsMacOS() ? "open" : "xdg-open",
                    Arguments = $"\"{directory}\"",
                    UseShellExecute = false,
                });
            }
        }
        catch (Exception ex)
        {
            StatusBarRight.Text = Localization.Instance.Format("Status.CouldNotOpenFolder", ex.Message);
        }
    }

    /// <summary>Copies <paramref name="text"/> and reports it via <paramref name="whatKey"/>, e.g. "Clipboard.Path".</summary>
    private async Task CopyToClipboardAsync(string? text, string whatKey)
    {
        if (string.IsNullOrEmpty(text) || Clipboard is null)
        {
            return;
        }

        await Clipboard.SetTextAsync(text);
        StatusBarRight.Text = Localization.Instance.Format("Status.CopiedToClipboard", Localization.Instance.Get(whatKey));
    }

    private async Task RequestRemoveGameFromLibraryAsync(GameEntry game)
    {
        var action = await ShowSystemDialogAsync(new ShellDialogRequest
        {
            Presentation = ShellDialogPresentation.Popup,
            Title = "Remove from Game Library?",
            Body = $"{game.Name} will be removed from this library. Its files will stay on disk.",
            Neutral = new ShellDialogButton(ShellDialogAction.Neutral, "Cancel"),
            Positive = new ShellDialogButton(ShellDialogAction.Positive, "Remove"),
        });
        if (action == ShellDialogAction.Positive)
        {
            RemoveGameFromLibrary(game);
        }
    }

    private void RemoveGameFromLibrary(GameEntry game)
    {
        if (!_settings.ExcludedGames.Contains(game.Path, FilePathComparer))
        {
            _settings.ExcludedGames.Add(game.Path);
            _settings.Save();
        }

        _libraryScanGeneration++;
        LoadingState.IsVisible = false;
        _allGames.RemoveAll(g => string.Equals(g.Path, game.Path, FilePathComparison));
        GameList.SelectedItem = null;
        RefreshVisibleGames();
        StatusBarRight.Text = Localization.Instance.Format("Status.RemovedFromLibrary", game.Name);
    }

    private void RefreshVisibleGames()
    {
        var query = CurrentLibrarySearchText;
        var selectedPath = (GameList.SelectedItem as GameEntry)?.Path;

        _visibleGames.Clear();
        var matchingGames = _allGames.Where(game =>
            query.Length == 0 ||
            game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            game.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (game.TitleId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        if (_presentationMode == ShellPresentationMode.Desktop)
        {
            matchingGames = SortDesktopGames(matchingGames);
        }
        foreach (var game in matchingGames)
        {
            _visibleGames.Add(game);
        }

        if (selectedPath is not null &&
            _visibleGames.FirstOrDefault(g => g.Path.Equals(selectedPath, FilePathComparison))
                is { } reselected)
        {
            GameList.SelectedItem = reselected;
        }

        SyncStrandTiles();

        EmptyState.IsVisible = _presentationMode == ShellPresentationMode.Sony &&
            _visibleGames.Count == 0;
        DesktopLibrary.IsLibraryEmpty = _visibleGames.Count == 0;
        UpdateEmptyStateTexts();

        UpdateSelectedGame();
        RefreshSonySearchItems();
    }

    private IEnumerable<GameEntry> SortDesktopGames(IEnumerable<GameEntry> games)
    {
        static string Text(string? value) => value ?? string.Empty;
        Func<GameEntry, string> key = _desktopSortColumn switch
        {
            1 => game => Text(game.TitleId),
            2 => game => Text(game.Version),
            3 => game => Text(game.FirmwareVersion),
            4 => game => game.Path,
            5 => game => game.CompatibilityStatusText,
            6 => game => game.Comment,
            _ => game => game.Name,
        };
        return _desktopSortAscending
            ? games.OrderBy(key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(game => game.Path, StringComparer.OrdinalIgnoreCase)
            : games.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(game => game.Path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Refreshes the empty-state title/hint from the current language and
    /// search text; a no-op while the empty state is not showing.
    /// </summary>
    private void UpdateEmptyStateTexts()
    {
        if (_visibleGames.Count != 0)
        {
            return;
        }

        var query = CurrentLibrarySearchText;
        var hasFilter = query.Length > 0;
        EmptyStateTitle.Text = hasFilter
            ? Localization.Instance.Get("Library.Empty.SearchTitle")
            : Localization.Instance.Get("Library.Empty.Title");
        EmptyStateHint.Text = hasFilter
            ? Localization.Instance.Format("Library.Empty.SearchHint", query)
            : Localization.Instance.Get("Library.Empty.Hint");
        EmptyAddFolderButton.IsVisible = !hasFilter;
    }

    private void UpdateSelectedGame()
    {
        if (GameList.SelectedItem is GameEntry game)
        {
            UpdateSelectedGameTexts();
            SelectedCoverPanel.DataContext = game;
            SelectedBadgesRow.DataContext = game;
            SelectedBadgesRow.IsVisible = true;
        }
        else
        {
            UpdateSelectedGameTexts();
            SelectedCoverPanel.DataContext = null;
            SelectedBadgesRow.DataContext = null;
            SelectedBadgesRow.IsVisible = false;
        }

        UpdateFocusedTitleMedia();
        UpdateRunButtons();
    }

    /// <summary>
    /// Gives title artwork and preview music to the selected game only while
    /// the game strand owns HOME's focus. Navigation, settings, and an empty
    /// library all return ownership to the authored ambient background.
    /// Desktop presentation keeps its existing selected-title behavior.
    /// </summary>
    private void UpdateFocusedTitleMedia(bool force = false)
    {
        var titleOwnsMedia = _presentationMode != ShellPresentationMode.Sony ||
            (_activePageIndex == 0 && _homeFocus.ActiveRegion == StrandRegion);
        var selectedGame = titleOwnsMedia ? GameList.SelectedItem as GameEntry : null;
        var game = selectedGame is not null && HasTitleOwnedMedia(selectedGame)
            ? selectedGame
            : null;

        if (!force && ReferenceEquals(game, _titleMediaOwner))
        {
            return;
        }

        _titleMediaOwner = game;
        if (UsesSonyAmbientFor(_presentationMode))
        {
            UpdateHomePlateFor(game);
        }
        _ = UpdateBackdropAsync(game);
        if (game is null)
        {
            _sndPreview.Stop();
        }
        else
        {
            PlaySelectedGamePreview(game);
        }
    }

    private static bool HasTitleOwnedMedia(GameEntry game)
    {
        var artwork = Ps5TitleArtwork.ResolveBackdropForExecutable(game.Path);
        var audio = ResolveTitleSoundPath(game.Path);
        return !string.IsNullOrWhiteSpace(artwork) ||
            audio is not null ||
            game.TitleLogoSource is not null;
    }

    /// <summary>
    /// Text-only refresh of the launch bar's title/path, split out of
    /// <see cref="UpdateSelectedGame"/> so a language change can re-apply it
    /// without restarting the backdrop fade or preview music.
    /// </summary>
    private void UpdateSelectedGameTexts()
    {
        if (GameList.SelectedItem is GameEntry game)
        {
            SelectedGameTitle.Text = game.Name;
            SelectedGamePath.Text = game.Path;
        }
        else
        {
            SelectedGameTitle.Text = Localization.Instance.Get("Launch.NoGameSelected");
            SelectedGamePath.Text = Localization.Instance.Get("Launch.NoGameHint");
        }
    }

    /// <summary>
    /// Points the home background at the focused title's own artwork.
    ///
    /// <para>This is what makes the home screen look like the console's: the
    /// PS5 background is the focused title's backdrop and it changes as the
    /// highlight travels the row. <see cref="Ps5BackgroundPlate"/> cross-fades
    /// between them and falls back to <c>bg_hub_default.dds</c> for a title
    /// that ships no artwork, which is the only thing that file is for.</para>
    /// </summary>
    /// <param name="game">The focused title, or null when nothing is focused.</param>
    private void UpdateHomePlateFor(GameEntry? game)
    {
        var index = game is null ? -1 : GameList.SelectedIndex;
        var direction = Ps5Transitions.HomeDirectionFor(_lastHomePlateIndex, index);
        var transition = Ps5Transitions.HomeBackgroundTransitionFor(direction);

        // RN HOME module 196 maps right -> SlideInLeft, left ->
        // SlideInRight and default -> Fade. Module 511 attaches degree Normal
        // to every home background request before SceneControl.SetBackground.
        if (string.Equals(
                Environment.GetEnvironmentVariable("PROSPERISMO_PS5_FORCE_RIPPLE"),
                "1",
                StringComparison.Ordinal))
        {
            // Ordinary HOME remains on the exact RN slide/fade selection above.
            HomePlate.Plate.ConfigureNativeImageRipple(
                ShellLayerBackgroundTransitionDegree.Normal,
                0.5,
                0.5);
        }
        else
        {
            HomePlate.Plate.ConfigureNativeImageTransition(
                transition,
                ShellLayerBackgroundTransitionDegree.Normal);
        }
        HomePlate.TitleArtPath = game is null
            || _sonyColdBootActive
            ? null
            : Ps5TitleArtwork.ResolveBackdropForExecutable(game.Path);
        _lastHomePlateIndex = index;
    }

    /// <summary>
    /// Loops the selected game's sce_sys/snd0.at9 preview music, console
    /// home screen style. Silent while a game is running or when disabled
    /// in the options.
    /// </summary>
    private void PlaySelectedGamePreview(GameEntry game)
    {
        if (_isRunning || !_settings.PlayTitleMusic)
        {
            return;
        }

        var sndPath = ResolveTitleSoundPath(game.Path);
        if (sndPath is not null)
        {
            _sndPreview.Play(sndPath);
        }
        else
        {
            _sndPreview.Stop();
        }
    }

    private static string? ResolveTitleSoundPath(string executablePath) =>
        FindFileIgnoringCase(
            Ps5TitleArtwork.ResolveSystemDirectoryForExecutable(executablePath),
            "snd0.at9");

    private void OnTitleMusicSettingChanged()
    {
        if (!_settings.PlayTitleMusic)
        {
            _sndPreview.Stop();
        }
        else
        {
            UpdateFocusedTitleMedia(force: true);
        }
    }

    /// <summary>
    /// Pushes the persisted menu-sound preference into the shell UI-sound
    /// service (SystemAssets.ShellUiSounds). The service is resolved by name
    /// so the launcher builds identically whether or not that component is
    /// present in the tree yet; once it lands, its static IsEnabled flag is
    /// driven directly from here with no further wiring.
    /// </summary>
    private void ApplyUiSoundSetting()
    {
        ShellUiSounds.IsEnabled = _settings.PlayUiSounds;
    }

    /// <summary>Drives the nav band clock. The markup ships a resting 00:00 so
    /// the band measures right before the first tick lands.</summary>
    private void StartSystemClock()
    {
        // Aligned to the minute rather than ticking every second: the band only
        // shows hours and minutes, so a per-second timer would wake the UI
        // thread sixty times for one visible change.
        void Show()
        {
            var text = DateTime.Now.ToString("HH:mm");
            TopNavBand.SetClockText(text);
            ControlCenterHost.SetClockText(text);
        }

        Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var shown = DateTime.Now.Minute;
        timer.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            if (now.Minute == shown)
            {
                return;
            }

            shown = now.Minute;
            Show();
        };
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    private void StartShellUiState()
    {
        if (_shellUiStateStarted)
        {
            return;
        }

        _shellUiStateStarted = true;
        // None of these UI-owned clocks, decoders, or focus resources starts
        ShellUiSounds.Preload();
        StartSystemClock();
        ShellIcons.Preload();
        ApplyShellIcons();
    }

    /// <summary>Pauses the preview music while the window is minimized.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            // The XAML WindowState="Maximized" assignment raises this change
            // during InitializeComponent, before named controls are wired up.
            if (WindowState == WindowState.Minimized)
            {
                _sndPreview.Pause();
                ShellAmbientMusic.SetSuspended(true);
                if (SessionLoadingPopup is { } popup)
                {
                    popup.IsOpen = false;
                }
            }
            else
            {
                _sndPreview.Resume();
                ShellAmbientMusic.SetSuspended(_isRunning);
                if (SessionLoadingPopup is { } popup)
                {
                    popup.IsOpen = _sessionLoadingActive;
                }
            }
        }
    }

    /// <summary>
    /// Fades the window backdrop to the selected game's key art. The image
    /// decodes off the UI thread and is cached on the entry; a newer
    /// selection cancels the fade-in of an older one.
    /// </summary>
    private async Task UpdateBackdropAsync(GameEntry? game)
    {
        var generation = ++_backdropGeneration;
        BackdropImage.Opacity = 0;

        // Match the hub wallpaper to the selection where the shell ships art
        // for it; unknown title ids fall back to the default hub background.
        HomePlate.TitleId = _sonyColdBootActive ? null : game?.TitleId;

        // The bundled key art is the primary backdrop whenever the selection
        // has no art of its own; the window color stays as the last fallback.
        void ShowDefaultBackdrop()
        {
            if (!_sonyColdBootActive && generation == _backdropGeneration && _defaultBackdrop is not null)
            {
                BackdropImage.Source = _defaultBackdrop;
                BackdropImage.Opacity = 1.0;
            }
        }

        if (game?.BackgroundPath is null)
        {
            ShowDefaultBackdrop();
            return;
        }

        if (game.Background is null)
        {
            try
            {
                var path = game.BackgroundPath;
                game.Background = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(path);
                    return Bitmap.DecodeToWidth(stream, 1600);
                });
            }
            catch (Exception)
            {
                ShowDefaultBackdrop(); // undecodable key art
                return;
            }
        }

        if (!_sonyColdBootActive && generation == _backdropGeneration)
        {
            BackdropImage.Source = game.Background;
            BackdropImage.Opacity = 1.0;
        }
    }

    // ---- Launching ----

    private async Task OpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localization.Instance.Get("Dialog.OpenExecutable"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Localization.Instance.Get("Dialog.PsExecutables"))
                    { Patterns = new[] { "eboot.bin", "*.bin", "*.self", "*.elf" } },
                FilePickerFileTypes.All,
            },
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            Launch(path, Path.GetFileName(path));
        }
    }

    private void LaunchSelected()
    {
        if (GameList.SelectedItem is GameEntry game)
        {
            Launch(game.Path, game.Name, game.TitleId);
        }
    }

    private string? ResolvePatchPlanForLaunch(string? titleId)
    {
        if (_emulatorExePath is null || string.IsNullOrWhiteSpace(titleId))
        {
            return null;
        }

        try
        {
            return PatchPlanStore.ResolveExistingPlan(_emulatorExePath, titleId);
        }
        catch (ArgumentException exception)
        {
            AppendConsoleLine($"[GUI][WARN] Patch plan skipped: {exception.Message}", WarningLineBrush);
            return null;
        }
    }

    private void Launch(string ebootPath, string displayName, string? titleId = null)
    {
        if (_isRunning)
        {
            return;
        }

        ShellUiSounds.Play(UiSoundEvent.Enter);

        var resolvedTitleId = string.IsNullOrWhiteSpace(titleId)
            ? _allGames.FirstOrDefault(game => game.Path.Equals(ebootPath, FilePathComparison))?.TitleId
            : titleId;
        var effective = EffectiveLaunchSettings.Resolve(_settings, PerGameSettings.Load(resolvedTitleId));
        var matchingTitleInstallPaths = string.IsNullOrWhiteSpace(resolvedTitleId)
            ? Array.Empty<string>()
            : _allGames
                .Where(game => string.Equals(game.TitleId, resolvedTitleId, StringComparison.OrdinalIgnoreCase))
                .Select(game => game.Path)
                .ToArray();
        var nativeProfile = _perGameEmulatorSettings.Load(
            ebootPath,
            resolvedTitleId,
            matchingTitleInstallPaths);
        var emulatorSettings = (nativeProfile?.Settings ?? _settings.GlobalEmulatorSettings).Copy();
        var patchPlanPath = ResolvePatchPlanForLaunch(resolvedTitleId);

        _sndPreview.Stop();
        _consoleLines.Clear();
        _allConsoleLines.Clear();

        DropFileLog();
        if (effective.LogToFile)
        {
            OpenFileLog(resolvedTitleId);
        }

        _isRunning = true;
        ShellAmbientMusic.SetSuspended(true);
        _runningGameName = displayName;
        _runningGamePath = Path.GetFullPath(ebootPath);
        SessionGameTitle.Text = displayName;
        _runningGameTitleId = resolvedTitleId;
        _runningSinceUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        StatusDot.Fill = SuccessLineBrush;
        StatusText.Text = Localization.Instance.Format("Launch.Running", displayName);
        StatusBarRight.Text = Localization.Instance.Format("Status.Running", displayName);
        UpdateRunButtons();
        UpdateDiscordPresence();

        ShowGameView();
        _pendingLaunch = new PendingLaunch(
            Path.GetFullPath(ebootPath),
            displayName,
            _runningGameTitleId,
            emulatorSettings,
            patchPlanPath);

        if (UsesEmbeddedPresentation && _gameSurfaceHost?.Surface is { } surface)
        {
            StartPendingSession(surface);
        }
        else if (!UsesEmbeddedPresentation)
        {
            // The current native backend owns a standalone SDL/Vulkan window.
            // Start it immediately; there is no embedded surface or first-frame
            // marker for the Avalonia shell to wait for.
            StartPendingSession(null);
        }
    }

    /// <summary>
    /// Stops the running game and updates status/presence immediately. The
    /// process-exit path still runs when the corpse is collected, but a game
    /// wedged in a GPU driver call can keep its process alive for a long
    /// time after termination — the launcher should not look (or tell
    /// Discord it is) "playing" during that window.
    /// </summary>
    private void StopEmulator()
    {
        if (!_isRunning || _isStopping)
        {
            return;
        }

        if (_emulator is null)
        {
            // The native host can be created a moment after Launch. Do not
            // let that delayed callback start a session the user already
            // cancelled.
            _pendingLaunch = null;
            OnEmulatorExited(0);
            return;
        }

        _isStopping = true;
        StopButton.IsEnabled = false;
        SessionStopButton.IsEnabled = false;
        SessionHintText.Text = Localization.Instance.Get("Launch.Stopping");
        SessionF11Badge.IsVisible = false;
        ShowSessionLoading("Closing game", "Waiting for the emulation session to exit...");
        _emulator.Stop();
        _runningGameName = null;
        _runningGameTitleId = null;
        _runningGamePath = null;
        StatusText.Text = Localization.Instance.Get("Launch.Stopping");
        StatusBarRight.Text = Localization.Instance.Get("Status.Stopping");
        UpdateDiscordPresence();
        UpdateSessionBarVisibility();
        ReturnToLibraryWhileStopping();
    }

    private async Task RequestStopEmulatorAsync(GameEntry? expectedGame = null)
    {
        if (!_isRunning || _isStopping)
        {
            return;
        }

        var expectedPath = expectedGame?.Path;
        if (expectedPath is not null && !ShellTitleOptionsComposer.IsCurrentRunningTitle(
                expectedPath, _runningGamePath, FilePathComparison))
        {
            return;
        }

        // Capture the session identity before opening the dialog. The user may
        // change HOME selection while it is open; only this same session may be
        // stopped when the affirmative callback returns.
        var runningPath = _runningGamePath;
        var runningName = _runningGameName;
        var action = await ShowSystemDialogAsync(ShellDialog.CloseApplication(runningName));
        if (action == ShellDialogAction.Positive && ShellTitleOptionsComposer.IsCurrentRunningTitle(
                runningPath, _runningGamePath, FilePathComparison))
        {
            StopEmulator();
        }
    }

    /// <summary>
    /// Builds "user/logs/&lt;titleId&gt;-&lt;timestamp&gt;.log" next to the emulator
    /// executable, following the same portable-data convention as savedata.
    /// </summary>
    private string? BuildLogFilePath(string? titleId)
    {
        try
        {
            var exeDirectory = Path.GetDirectoryName(_emulatorExePath) ?? AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(exeDirectory))
            {
                return null;
            }

            var logsDirectory = Path.Combine(exeDirectory, "user", "logs");
            Directory.CreateDirectory(logsDirectory);

            var id = string.IsNullOrWhiteSpace(titleId) ? "UNKNOWN" : titleId;
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                id = id.Replace(invalid, '_');
            }

            return Path.Combine(logsDirectory, $"{id}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        }
        catch (Exception)
        {
            return null; // unwritable location: launch continues without a log file
        }
    }

    private void OnEmulatorExited(int exitCode)
    {
        FlushPendingConsoleLines();
        _isRunning = false;
        ShellAmbientMusic.SetSuspended(WindowState == WindowState.Minimized);
        _isStopping = false;
        _emulator?.Dispose();
        _emulator = null;
        _pendingLaunch = null;
        DisposeGameSurfaceHost();
        HideGameView();

        var meaningKey = exitCode switch
        {
            0 => "Exit.Ok",
            1 => "Exit.InvalidArguments",
            2 => "Exit.EbootNotFound",
            3 => "Exit.RuntimeException",
            4 => "Exit.EmulationError",
            -1073741819 => "Exit.EmulationError",
            _ => "Exit.Unknown",
        };
        var stoppedByUser = exitCode == EmulatorProcess.HostStopExitCode;
        var meaning = Localization.Instance.Get(meaningKey);
        var brush = exitCode == 0 || stoppedByUser ? SuccessLineBrush : ErrorLineBrush;
        AppendConsoleLine(
            stoppedByUser
                ? "Game closed by the user."
                : Localization.Instance.Format("Launch.ProcessExited", exitCode, meaning),
            brush);
        CloseFileLogSoon();

        StatusDot.Fill = exitCode == 0 || stoppedByUser ? (IBrush)SuccessLineBrush : ErrorLineBrush;
        StatusText.Text = stoppedByUser
            ? "Game closed by the user."
            : Localization.Instance.Format("Launch.Exited", exitCode, meaning);
        StatusBarRight.Text = Localization.Instance.Get("Status.Idle");
        _runningGameName = null;
        _runningGameTitleId = null;
        _runningGamePath = null;
        UpdateRunButtons();
        UpdateDiscordPresence();
        UpdateFocusedTitleMedia(force: true);

        if (!stoppedByUser && exitCode != 0)
        {
            ShellNotificationBroker.Post(new ShellNotificationRequest
            {
                NotificationId = "emulation-exit",
                UserId = "local-user",
                BundleName = "Prosperismo.Shell",
                UseCaseId = "emulation",
                Surface = ShellNotificationSurface.Interactive,
                ReplaceAlways = true,
                PrimaryText = "The game closed with an error",
                SecondaryText = $"Exit code {exitCode}: {meaning}",
                DetailText = "Open the console to review the emulator log and diagnostic output.",
                Actions =
                [
                    new ShellNotificationAction(
                        "console",
                        "Open console",
                        ShowConsoleWindow,
                        closeControlCenter: true),
                    new ShellNotificationAction("dismiss", "Dismiss"),
                ],
            });
        }
    }

    private void StartPendingSession(VulkanHostSurface? surface)
    {
        if (_pendingLaunch is not { } launch || _emulator is not null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_emulatorExePath))
        {
            AppendConsoleLine(Localization.Instance.Get("Launch.ExeNotFound"), ErrorLineBrush);
            OnEmulatorExited(3);
            return;
        }

        var process = new EmulatorProcess();
        process.OutputReceived += OnEmulatorOutput;
        process.Exited += code => Dispatcher.UIThread.Post(() => OnEmulatorExited(code));

        try
        {
            var arguments = BuildEmulatorArguments(launch, surface);
            var environment = EmulatorLaunchContract.BuildEnvironment(_emulatorExePath);
            _emulator = process;
            _pendingLaunch = null;
            process.Start(
                _emulatorExePath,
                arguments,
                Path.GetDirectoryName(_emulatorExePath),
                environment);
            _settings.RecentGamePaths = ShellAppSwitcherComposer.RecordLaunch(
                _settings.RecentGamePaths,
                launch.EbootPath,
                FilePathComparison);
            _settings.Save();
            AppendConsoleLine(
                Localization.Instance.Format("Launch.Command", launch.EbootPath),
                DimLineBrush);
        }
        catch (Exception exception)
        {
            _emulator = null;
            process.Dispose();
            AppendConsoleLine(
                Localization.Instance.Format("Launch.StartFailed", exception.Message),
                ErrorLineBrush);
            OnEmulatorExited(3);
        }
    }

    private List<string> BuildEmulatorArguments(PendingLaunch launch, VulkanHostSurface? surface)
    {
        var arguments = EmulatorLaunchContract.BuildArguments(
            launch.EmulatorSettings,
            launch.EbootPath,
            launch.PatchPlanPath).ToList();

        if (UsesEmbeddedPresentation &&
            surface is not null &&
            surface.TryGetChildProcessDescriptor(out var descriptor))
        {
            arguments.Add($"--host-surface={descriptor}");
        }
        else if (UsesEmbeddedPresentation)
        {
            AppendConsoleLine(
                "[GUI][WARN] Embedded child surfaces are unavailable on this platform; opening a game window instead.",
                WarningLineBrush);
        }

        return arguments;
    }

    private void OnEmulatorOutput(string line, bool isError)
    {
        _pendingLines.Enqueue((line, isError));
        if (!UsesEmbeddedPresentation)
        {
            return;
        }

        if (!line.Contains("[VIDEOOUT][INFO] Hosted splash ready.", StringComparison.Ordinal) &&
            !line.Contains("[VIDEOOUT][INFO] Hosted first frame presented.", StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_isRunning && !_isStopping)
            {
                _awaitingFirstFrame = false;
                ClearLibraryBlur();
                MainContent.Margin = new Thickness(0);
                RestoreGameViewToFull();
                GameView.Background = Brushes.Black;
                GameView.IsHitTestVisible = true;
                LibraryPage.IsVisible = false;
                OptionsPage.IsVisible = false;
                UpdateContentToolbarVisibility();
                ConsolePanel.IsVisible = false;
                LaunchBar.IsVisible = false;
                HideSessionLoading();
                UpdateSessionBarVisibility();

                // Defer so the layout pass from the margin change above settles first.
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_isRunning || _isStopping)
                    {
                        return;
                    }

                    _gameSurfaceHost?.RefreshSurfaceSize();
                    _gameSurfaceHost?.SetPresentationVisible(true);
                    _gameSurfaceHost?.SetCursorAutoHide(true);
                });
            }
        });
    }

    private GameSurfaceHost EnsureGameSurfaceHost()
    {
        if (_gameSurfaceHost is not null)
        {
            return _gameSurfaceHost;
        }

        var host = new GameSurfaceHost();
        // Configure this before attaching it to Avalonia so its first native
        // HWND is hidden while the child process starts.
        host.SetPresentationVisible(false);
        host.SurfaceAvailable += (_, surface) =>
        {
            if (ReferenceEquals(_gameSurfaceHost, host))
            {
                StartPendingSession(surface);
            }
        };
        host.SurfaceDestroyed += (_, surface) => OnGameSurfaceDestroyed(host, surface);
        _gameSurfaceHost = host;
        GameSurfaceContainer.Children.Add(host);
        return host;
    }

    private void DisposeGameSurfaceHost()
    {
        var host = _gameSurfaceHost;
        if (host is null)
        {
            return;
        }

        _gameSurfaceHost = null;
        host.SetPresentationVisible(false);
        GameSurfaceContainer.Children.Remove(host);
    }

    private void OnGameSurfaceDestroyed(GameSurfaceHost host, VulkanHostSurface surface)
    {
        if (ReferenceEquals(_gameSurfaceHost, host) && _isRunning)
        {
            StopEmulator();
        }
    }

    /// <summary>
    /// The native host attachment is a real child window: it sits above every
    /// Avalonia control it covers and swallows their mouse input regardless of
    /// hit-test settings. While the library must stay interactive (loading,
    /// closing), the surface is parked offscreen AT FULL SIZE via a negative
    /// margin. It must not be shrunk instead: the emulator child polls the
    /// HWND client size and its presenter defers swapchain creation while the
    /// surface is 1px, which would deadlock the loading handshake.
    /// </summary>
    private void ParkGameViewOffscreen()
    {
        GameView.Margin = new Thickness(-20000, 0, 20000, 0);
    }

    private void RestoreGameViewToFull()
    {
        GameView.Margin = new Thickness(0);
    }

    private void ShowGameView()
    {
        _isStopping = false;
        _awaitingFirstFrame = UsesEmbeddedPresentation;
        if (!UsesEmbeddedPresentation)
        {
            // Standalone native windows do not produce the hosted-frame markers
            // consumed by OnEmulatorOutput. Keep the launcher interactive while
            // the child owns its own presentation window.
            GameView.IsVisible = false;
            GameView.IsHitTestVisible = true;
            HideSessionLoading();
            UpdateSessionBarVisibility();
            return;
        }

        var host = EnsureGameSurfaceHost();
        ParkGameViewOffscreen();
        GameView.IsVisible = true;
        GameView.Background = Brushes.Transparent;
        GameView.IsHitTestVisible = false;
        host.SetPresentationVisible(false);
        AnimateLibraryBlur(LaunchBlurRadius);
        SessionHintText.Text = "Fullscreen";
        SessionF11Badge.IsVisible = true;
        UpdateSessionBarVisibility();
        ShowSessionLoading("Loading game", "Preparing the emulation session...");
    }

    private void HideGameView()
    {
        if (_gameFullscreen && WindowState == WindowState.FullScreen)
        {
            OnWindowFullScreen(this, new RoutedEventArgs());
        }

        _gameSurfaceHost?.SetCursorAutoHide(false);
        _gameSurfaceHost?.SetPresentationVisible(false);
        _awaitingFirstFrame = false;
        GameView.IsVisible = false;
        GameView.IsHitTestVisible = true;
        SessionBarPopup.IsOpen = false;
        HideSessionLoading();
        AnimateLibraryBlur(0, clearWhenComplete: true);
        MainContent.Margin = _presentationMode == ShellPresentationMode.Desktop
            ? new Thickness(8)
            : new Thickness(32, 24, 32, 20);
        ConsolePanel.IsVisible = _presentationMode != ShellPresentationMode.Desktop &&
            ConsoleToggle.IsChecked == true && _consoleWindow is null;
        LaunchBar.IsVisible = _presentationMode != ShellPresentationMode.Desktop;
        LibraryPage.IsVisible = _activePageIndex == 0;
        OptionsPage.IsVisible = _activePageIndex == 1;
        UpdateContentToolbarVisibility();
        // Game art when the source still holds it, otherwise the bundled
        // default; a bare color only when neither is available.
        BackdropImage.Opacity = BackdropImage.Source is not null ? 1 : 0;
    }

    private void AnimateLibraryBlur(double targetRadius, bool clearWhenComplete = false)
    {
        _libraryBlur ??= new BlurEffect();
        PagesHost.Effect = _libraryBlur;

        _libraryBlurStartRadius = _libraryBlur.Radius;
        _libraryBlurTargetRadius = Math.Max(0, targetRadius);
        _libraryBlurStartedAt = Stopwatch.GetTimestamp();
        _clearLibraryBlurWhenComplete = clearWhenComplete && _libraryBlurTargetRadius == 0;

        if (Math.Abs(_libraryBlurStartRadius - _libraryBlurTargetRadius) < 0.01)
        {
            CompleteLibraryBlur();
            return;
        }

        _libraryBlurTimer.Start();
    }

    private void AdvanceLibraryBlur()
    {
        if (_libraryBlur is null)
        {
            _libraryBlurTimer.Stop();
            return;
        }

        var elapsed = (Stopwatch.GetTimestamp() - _libraryBlurStartedAt) /
                      (double)Stopwatch.Frequency;
        var progress = Math.Clamp(elapsed / BlurTransitionSeconds, 0, 1);
        // Cubic ease-out gives the loading transition a quick response while
        // keeping the final change of sharpness unobtrusive.
        var easedProgress = 1 - Math.Pow(1 - progress, 3);
        _libraryBlur.Radius = _libraryBlurStartRadius +
                              ((_libraryBlurTargetRadius - _libraryBlurStartRadius) * easedProgress);

        if (progress >= 1)
        {
            CompleteLibraryBlur();
        }
    }

    private void CompleteLibraryBlur()
    {
        _libraryBlurTimer.Stop();
        if (_libraryBlur is not null)
        {
            _libraryBlur.Radius = _libraryBlurTargetRadius;
        }

        if (_clearLibraryBlurWhenComplete)
        {
            PagesHost.Effect = null;
            _libraryBlur = null;
            _clearLibraryBlurWhenComplete = false;
        }
    }

    private void ClearLibraryBlur()
    {
        _libraryBlurTimer.Stop();
        _libraryBlur = null;
        _clearLibraryBlurWhenComplete = false;
        PagesHost.Effect = null;
    }

    private void ShowSessionLoading(string title, string detail)
    {
        SessionLoadingTitle.Text = title;
        SessionLoadingDetail.Text = detail;
        _sessionLoadingActive = true;
        SessionLoadingPopup.IsOpen = IsActive && WindowState != WindowState.Minimized;
    }

    private void HideSessionLoading()
    {
        _sessionLoadingActive = false;
        SessionLoadingPopup.IsOpen = false;
    }

    private void ReturnToLibraryWhileStopping()
    {
        if (_gameFullscreen && WindowState == WindowState.FullScreen)
        {
            OnWindowFullScreen(this, new RoutedEventArgs());
        }

        // Keep the native child alive until the session exits, but hide it
        // immediately. Destroying it while Vulkan still owns the surface can
        // crash the GUI; parking it in the 1x1 corner lets the library
        // recover — and stay clickable — while the native closing popup
        // reports teardown progress.
        _gameSurfaceHost?.SetPresentationVisible(false);
        _awaitingFirstFrame = false;
        ParkGameViewOffscreen();
        GameView.Background = Brushes.Transparent;
        GameView.IsHitTestVisible = false;
        SessionBarPopup.IsOpen = false;
        AnimateLibraryBlur(LaunchBlurRadius);
        MainContent.Margin = new Thickness(32, 24, 32, 20);
        ConsolePanel.IsVisible = ConsoleToggle.IsChecked == true && _consoleWindow is null;
        LaunchBar.IsVisible = true;
        LibraryPage.IsVisible = _activePageIndex == 0;
        OptionsPage.IsVisible = _activePageIndex == 1;
        UpdateContentToolbarVisibility();
        BackdropImage.Opacity = BackdropImage.Source is not null ? 1 : 0;
        UpdateRunButtons();
        Console.Error.WriteLine("[GUI][INFO] Library restored while embedded session is closing.");
    }

    private void OpenFileLog(string? titleId)
    {
        var filePath = ResolveLogFilePath(titleId);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _fileLog = new StreamWriter(filePath, append: false) { AutoFlush = true };
            AppendConsoleLine(Localization.Instance.Format("Launch.LogFile", filePath), DimLineBrush);
        }
        catch (Exception exception)
        {
            AppendConsoleLine($"[GUI][WARN] Could not open log file: {exception.Message}", WarningLineBrush);
            DropFileLog();
        }
    }

    private string? ResolveLogFilePath(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(_settings.LogFilePath))
        {
            return BuildLogFilePath(titleId);
        }

        if (_settings.OverrideLogFile)
        {
            return _settings.LogFilePath;
        }

        var path = _settings.LogFilePath;
        var id = string.IsNullOrWhiteSpace(titleId) ? "UNKNOWN" : titleId;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            id = id.Replace(invalid.ToString(), string.Empty, StringComparison.Ordinal);
        }

        var directory = Path.GetDirectoryName(path);
        var filename = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var timestampedName = $"{filename}-{id}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}";
        return string.IsNullOrEmpty(directory) ? timestampedName : Path.Combine(directory, timestampedName);
    }

    private void UpdateRunButtons()
    {
        var selectedGame = GameList.SelectedItem as GameEntry;
        LaunchButton.IsEnabled = !_isRunning && selectedGame is not null;
        StopButton.IsEnabled = _isRunning && !_isStopping;
        SessionStopButton.IsEnabled = _isRunning && !_isStopping;
        OpenFileButton.IsEnabled = !_isRunning;
        DesktopLibrary.OpenFileButton.IsEnabled = !_isRunning;
        DesktopLibrary.IsBigPictureEnabled = !_isRunning && !_isStopping;
        DesktopLibrary.GlobalSettingsButton.IsEnabled = !_isRunning && !_isStopping;
        DesktopLibrary.IsGameSettingsEnabled = !_isRunning && selectedGame is not null;
        DesktopLibrary.IsClearCustomSettingsEnabled = !_isRunning && !_isStopping &&
            selectedGame is not null && HasCustomGameSettings(selectedGame);
        RefreshGameHubCta();
    }

    private bool HasCustomGameSettings(GameEntry game)
    {
        try
        {
            return _perGameEmulatorSettings.Load(
                game.Path,
                game.TitleId,
                MatchingTitleInstallPaths(game.TitleId)) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void UpdateSessionBarVisibility()
    {
        SessionBarPopup.IsOpen = _isRunning && !_isStopping && !_awaitingFirstFrame && GameView.IsVisible &&
            !_gameFullscreen && WindowState != WindowState.FullScreen;
    }

    // ---- Console ----

    private void FlushPendingConsoleLines()
    {
        if (_pendingLines.IsEmpty)
        {
            return;
        }

        var incoming = new List<LogLine>();
        while (incoming.Count < MaxConsoleLinesPerFlush &&
               _pendingLines.TryDequeue(out var pending))
        {
            WriteFileLog(pending.Line);
            incoming.Add(new LogLine(pending.Line, BrushForLine(pending.Line)));
        }

        FlushFileLog();

        _allConsoleLines.AddRange(incoming);

        string query = ConsoleSearchBox.Text ?? string.Empty;

        IEnumerable<LogLine> linesToAdd = incoming;
        if (!string.IsNullOrWhiteSpace(query))
        {
            linesToAdd = incoming.Where(line =>
                line.Text != null &&
                line.Text.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        _consoleLines.AddRange(linesToAdd);

        var overflow = _consoleLines.Count - MaxConsoleLines;
        while (_allConsoleLines.Count > MaxConsoleLines)
        {
            var droppedLine = _allConsoleLines[0];
            _allConsoleLines.RemoveAt(0);
            if (_consoleLines.Count > 0 && _consoleLines[0] == droppedLine)
            {
                _consoleLines.RemoveAt(0);
            }
        }

        _autoScrollTicks = 3;
    }

    private void AppendConsoleLine(string text, IBrush brush)
    {
        WriteFileLog(text);
        FlushFileLog();

        var line = new LogLine(text, brush);
        _allConsoleLines.Add(line);

        string query = ConsoleSearchBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query) || (text != null && text.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            _consoleLines.Add(line);
        }

        while (_allConsoleLines.Count > MaxConsoleLines)
        {
            var droppedLine = _allConsoleLines[0];
            _allConsoleLines.RemoveAt(0);
            if (_consoleLines.Count > 0 && _consoleLines[0] == droppedLine)
            {
                _consoleLines.RemoveAt(0);
            }
        }

        _autoScrollTicks = 3;
        MaybeAutoScroll();
    }

    private void RefreshVisibleConsoleLines()
    {
        string query = ConsoleSearchBox.Text ?? string.Empty;

        _consoleLines.Clear();

        if (string.IsNullOrWhiteSpace(query))
        {
            _consoleLines.AddRange(_allConsoleLines);
        }
        else
        {
            var filtered = _allConsoleLines.Where(line =>
                line.Text != null &&
                line.Text.Contains(query, StringComparison.OrdinalIgnoreCase));

            _consoleLines.AddRange(filtered);
        }
    }

    // ---- Console-to-file mirroring ----

    private void WriteFileLog(string text)
    {
        if (_fileLog is not { } writer)
        {
            return;
        }

        try
        {
            writer.Write('[');
            writer.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
            writer.Write("] ");
            writer.WriteLine(text);
        }
        catch (Exception)
        {
            DropFileLog(); // unwritable (disk full, etc.): stop mirroring
        }
    }

    private void FlushFileLog()
    {
        try
        {
            _fileLog?.Flush();
        }
        catch (Exception)
        {
            DropFileLog();
        }
    }

    private void DropFileLog()
    {
        var writer = _fileLog;
        _fileLog = null;
        try
        {
            writer?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The pipe reader threads can deliver a final burst after the exit
    /// event, so the file stays open for one more flush cycle.
    /// </summary>
    private void CloseFileLogSoon()
    {
        if (_fileLog is not { } writer)
        {
            return;
        }

        DispatcherTimer.RunOnce(() =>
        {
            if (ReferenceEquals(_fileLog, writer))
            {
                FlushPendingConsoleLines();
                DropFileLog();
            }
        }, TimeSpan.FromMilliseconds(400));
    }

    private void MaybeAutoScroll()
    {
        // ScrollToEnd is applied over a few flush-timer ticks because the
        // virtualizing panel re-estimates its extent after large batches, and
        // a single scroll can land short of the true end. A synchronous
        // ScrollIntoView during rapid adds is avoided entirely — it can crash
        // the panel with "Invalid Arrange rectangle".
        if (_autoScrollTicks <= 0 || AutoScrollCheck.IsChecked != true)
        {
            return;
        }

        _autoScrollTicks--;
        (ConsoleList.Scroll as ScrollViewer)?.ScrollToEnd();
    }

    private static IBrush BrushForLine(string line)
    {
        if (line.Contains("[ERROR]", StringComparison.Ordinal) ||
            line.Contains("[CRITICAL]", StringComparison.Ordinal))
        {
            return ErrorLineBrush;
        }

        if (line.Contains("[WARNING]", StringComparison.Ordinal))
        {
            return WarningLineBrush;
        }

        if (line.Contains("[INFO]", StringComparison.Ordinal))
        {
            return InfoLineBrush;
        }

        if (line.Contains("[DEBUG]", StringComparison.Ordinal) ||
            line.Contains("[TRACE]", StringComparison.Ordinal))
        {
            return DimLineBrush;
        }

        return DefaultLineBrush;
    }

    private async Task CopyConsoleAsync()
    {
        if (_consoleLines.Count == 0 || Clipboard is null)
        {
            return;
        }

        var text = string.Join(Environment.NewLine, _consoleLines.Select(line => line.Text));
        await Clipboard.SetTextAsync(text);
    }

    private void ShowConsoleWindow()
    {
        if (_consoleWindow is { } window)
        {
            window.Activate();
            return;
        }

        ConsoleSearchBox.Text = string.Empty;
        ConsoleToggle.IsChecked = false;
        ConsolePanel.IsVisible = false;
        _consoleWindow = new ConsoleWindow(
            _consoleLines,
            () => { _consoleLines.Clear(); _allConsoleLines.Clear(); },
            AutoScrollCheck.IsChecked == true);
        _consoleWindow.Closed += (_, _) =>
        {
            _consoleWindow = null;
            ConsoleToggle.IsChecked = true;
            ConsolePanel.IsVisible = true;
        };
        _consoleWindow.Show(this);
    }
}
