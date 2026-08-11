// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Prosperismo.GUI;
using Prosperismo.GUI.Controls;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.GUI.SystemAssets;

namespace ShellShot;

/// <summary>
/// Renders the shell's real controls off-screen and writes png frames.
///
/// The point is being able to look at what shipped. A control can pass every
/// numeric test and still be laid out wrong, and the screen-capture route does
/// not work from a session with no interactive desktop, so this drives Avalonia
/// headless with the Skia renderer and pulls frames straight out of the
/// compositor. Same controls, same styles, no windowing toolkit.
///
///   dotnet run --project tools/shell-shot -- --out shots --scene tilerow
///   dotnet run --project tools/shell-shot -- --out shots --scene entrance --frames 12 --step 150
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Console.WriteLine(Options.Usage);
            return 1;
        }

        Directory.CreateDirectory(options.Output);

        BuildAvaloniaApp().SetupWithoutStarting();

        var window = new Window
        {
            Width = options.Width,
            Height = options.Height,
            SystemDecorations = SystemDecorations.None,
            Background = new SolidColorBrush(Color.FromRgb(2, 4, 8)),
        };

        var scene = Scenes.Build(options.Scene, options);
        window.Content = scene.Root;
        window.Show();

        // One tick to get through the first layout pass before anything is
        // measured or captured.
        Pump(window, TimeSpan.FromMilliseconds(16));


        // are asynchronous. Give the real controls wall-clock time to finish
        // instead of recording a black pre-load frame and calling it the UI.
        if (string.Equals(options.Scene, "native-background", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Scene, "native-background-settings", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Scene, "native-background-bottom", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Scene, "focus-idle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Scene, "settings", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Scene, "settings-detail", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Scene, "notification", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Scene, "profile", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Scene, "search", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Scene, "control-center", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Scene, "control-center-switcher", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Scene, "control-center-notifications", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Scene, "control-center-notification-detail", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Scene, "control-center-notification-options", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Scene, "control-center-notification-delete-confirm", StringComparison.OrdinalIgnoreCase))
        {
            var warmup = Stopwatch.StartNew();
            var warmupDuration = string.Equals(options.Scene, "focus-idle", StringComparison.OrdinalIgnoreCase)
                    ? TimeSpan.FromMilliseconds(2500)
                    : TimeSpan.FromMilliseconds(5000);
            while (warmup.Elapsed < warmupDuration)
            {
                Thread.Sleep(10);
                var advancesNativeClock =
                    !options.Scene.StartsWith("native-background", StringComparison.OrdinalIgnoreCase);
                scene.Advance?.Invoke(advancesNativeClock
                    ? TimeSpan.FromMilliseconds(10)
                    : TimeSpan.Zero);
                Pump(window, TimeSpan.FromMilliseconds(10));
            }
        }

        void AdvanceSceneBy(double milliseconds)
        {
            double remaining = milliseconds;
            while (remaining > 0.0)
            {
                double slice = Math.Min(1000.0 / 60.0, remaining);
                scene.Advance?.Invoke(TimeSpan.FromMilliseconds(slice));
                remaining -= slice;
            }
        }

        AdvanceSceneBy(options.StartAt.TotalMilliseconds);

        for (int i = 0; i < options.Frames; i++)
        {
            double atMs = options.StartAt.TotalMilliseconds + (i * options.StepMs);

            // Advance a frame at a time rather than in one jump. The controls'
            // springs clamp a single advance to 64 ms on purpose, so that a
            // stalled UI thread makes them arrive rather than teleport; feeding
            // them the whole step at once would quietly run them slow.
            if (i > 0)
            {
                AdvanceSceneBy(options.StepMs);
            }
            else
            {
                scene.Advance?.Invoke(TimeSpan.Zero);
            }

            // Some headless backends cache a child image even after its
            // WriteableBitmap is replaced. Invalidate the hosted scene as well
            // so a capture always reflects the timestamp just advanced to.
            scene.Root.InvalidateVisual();
            Pump(window, TimeSpan.FromMilliseconds(16));
            if (scene.IsFramePending is not null)
            {
                var frameWait = Stopwatch.StartNew();
                while (scene.IsFramePending() && frameWait.Elapsed < TimeSpan.FromSeconds(2))
                {
                    Thread.Sleep(5);
                    Pump(window, TimeSpan.FromMilliseconds(5));
                }
                scene.Root.InvalidateVisual();
                Pump(window, TimeSpan.FromMilliseconds(16));
            }

            var frame = window.CaptureRenderedFrame();
            if (frame is null)
            {
                Console.Error.WriteLine("frame {0}: nothing captured", i);
                continue;
            }

            string path = Path.Combine(
                options.Output,
                string.Format(CultureInfo.InvariantCulture, "{0}_{1:0000}ms.png", options.Scene, (int)atMs));
            using (var fs = File.Create(path))
            {
                frame.Save(fs);
            }

            Console.WriteLine("{0}  {1}x{2}", path, frame.PixelSize.Width, frame.PixelSize.Height);
        }

        return 0;
    }

    private static void Pump(Window window, TimeSpan delta)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "avares://Prosperismo.Shell/Assets/Fonts#Fira Sans",
            });
}

internal sealed record Scene(
    Control Root,
    Action<TimeSpan>? Advance,
    Func<bool>? IsFramePending = null);

internal static class Scenes
{
    public static Scene Build(string name, Options options) => name switch
    {
        "entrance" => Entrance(options),
        "marquee" => Marquee(options),
        "home" => Home(options),
        "focus" => Focus(options),
        "focus-idle" => FocusIdle(options),
        "list" => FocusList(options),
        "navband" => FocusNavBand(options),
        "panel" => FunctionPanel(options),
        "profile" => ProfilePanel(options),
        "search" => Search(options),
        "hub" => Hub(options),
        "hub-cta" => Hub(options, ctaOnly: true),
        "backdrop" => Backdrop(options),
        "native-background" => NativeBackground(options),
        "native-background-settings" => NativeBackground(options, settings: true),
        "native-background-bottom" => NativeBackground(options, bottom: true),
        "wave-background" => WaveBackground(options, highContrast: false),
        "high-contrast-background" => WaveBackground(options, highContrast: true),
        "theme-one-background" => WaveBackground(options, highContrast: false, themeColourIndex: 0x01),
        "settings" => Settings(options),
        "settings-detail" => SettingsDetail(options),
        "all-games" => AllGames(options),
        "notification" => Notification(options),
        "control-center" => ControlCenter(options),
        "control-center-switcher" => ControlCenterSwitcher(options),
        "control-center-notifications" => ControlCenterNotifications(options),
        "control-center-notification-detail" => ControlCenterNotificationDetail(options),
        "control-center-notification-options" => ControlCenterNotificationOptions(options),
        "control-center-notification-delete-confirm" => ControlCenterNotificationDeleteConfirm(options),
        "dialog" => Dialog(options),
        "desktop-launcher" => DesktopLauncher(options),
        _ => TileRow(options),
    };

    /// <summary>
    /// Captures the actual compact-launcher control. MainWindow supplies the
    /// same single GameList at runtime; these entries only make its geometry
    /// and selected state visible in a deterministic headless capture.
    /// </summary>
    private static Scene DesktopLauncher(Options options)
    {
        var games = new[]
        {
            new GameEntry("ASTRO's PLAYROOM", "PPSA01325", "01.000.000", "/Games/Astro/eboot.bin", 12L << 30, null, null),
            new GameEntry("Minecraft", "CUSA00265", "02.95.000", "/Games/Minecraft/eboot.bin", 3L << 30, null, null),
            new GameEntry("DEMON'S SOULS", "PPSA01341", "01.005.000", "/Games/DemonsSouls/eboot.bin", 56L << 30, null, null),
        };
        // Let the real control stretch into the root's available content box.
        // Pinning it to the window size while the root also had page margins
        // made the bottom/right edges clip in desktop design-system captures.
        var surface = new DesktopLibrarySurface();
        surface.Games.ItemsSource = games;
        surface.Games.SelectedIndex = 0;
        // MainWindow paints RootLayout with the desktop page token before the
        // 8px content inset is applied. Keep the same relationship here: the
        // outer host owns the page color and the inner grid owns the capture
        // inset. A margin on the only root control leaves the headless host's
        // default black visible as false letterbox strips.
        var host = new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#FFF5F7FA")),
        };
        var root = new Grid
        {
            Margin = new Thickness(32, 24, 32, 20),
        };
        root.Classes.Add("desktopLauncher");
        root.Children.Add(surface);
        host.Children.Add(root);
        return new Scene(host, null);
    }

    private static Scene Notification(Options options)
    {
        var surface = new Panel
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
            Background = new SolidColorBrush(Color.FromRgb(2, 4, 8)),
        };
        var host = new ShellNotificationHost
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
        };
        surface.Children.Add(host);
        host.Post(new ShellNotificationRequest
        {
            NotificationId = "shell-shot",
            UserId = "user",
            Surface = ShellNotificationSurface.Interactive,
            PrimaryText = "Download complete",
            SecondaryText = "The title is ready to play.",
            DetailText = "Open the title now, or leave it in your game library.",
            Actions =
            [
                new ShellNotificationAction("open", "Open"),
                new ShellNotificationAction("dismiss", "Dismiss"),
            ],
        });
        host.CompletePresentationForCapture();
        return new Scene(ScaleDesignSurface(surface), null);
    }

    /// <summary>
    /// Captures the product Control Center over the product real-time shell
    /// background. The focused stock Home slot deliberately remains visible,
    /// making the previous blank-icon regression and focus contrast obvious.
    /// </summary>
    private static Scene ControlCenter(Options options)
    {
        var surface = new Panel
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
        };
        surface.Children.Add(new Prosperismo.GUI.SystemAssets.Shell.ShellBackground
        {
            IsMotionEnabled = false,
        });

        var controlCenter = new ShellControlCenter
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
            Items = ShellControlCenter.ConsoleTourItems,
        };
        controlCenter.SetClockText("07:18");
        surface.Children.Add(controlCenter);
        controlCenter.Open();
        return new Scene(ScaleDesignSurface(surface), null);
    }

    private static Scene ControlCenterNotifications(Options options)
    {
        var surface = new Panel
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
        };
        surface.Children.Add(new Prosperismo.GUI.SystemAssets.Shell.ShellBackground
        {
            IsMotionEnabled = false,
        });

        var controlCenter = new ShellControlCenter
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
            Items = ShellControlCenter.ConsoleTourItems,
        };
        controlCenter.SetClockText("07:18");
        controlCenter.RestoreSelectedItem("notifications");
        controlCenter.SetNotificationState(isDoNotDisturb: false, newNotificationCount: 1);
        surface.Children.Add(controlCenter);
        controlCenter.Open();

        var timestamp = new DateTimeOffset(2026, 8, 11, 7, 18, 0, TimeSpan.Zero);
        var rows = ShellNotificationListComposer.Compose(
        [
            new ShellNotificationHistoryEntry(
                "download",
                new ShellNotificationRequest
                {
                    PrimaryText = "Download complete",
                    SecondaryText = "The title is ready to play.",
                    Surface = ShellNotificationSurface.Interactive,
                },
                timestamp,
                timestamp,
                ShellNotificationHistoryState.New),
            new ShellNotificationHistoryEntry(
                "emulator",
                new ShellNotificationRequest
                {
                    PrimaryText = "The game closed with an error",
                    SecondaryText = "Open the console for diagnostic output.",
                    Surface = ShellNotificationSurface.Interactive,
                },
                timestamp.AddMinutes(-12),
                timestamp.AddMinutes(-12),
                ShellNotificationHistoryState.Seen),
        ], isDoNotDisturb: false);
        var panelPresented = false;
        return new Scene(ScaleDesignSurface(surface), delta =>
        {
            if (panelPresented)
            {
                return;
            }

            _ = controlCenter.ShowPanelAsync(
                "notifications",
                "Notifications",
                rows,
                ShellNotificationListComposer.InitialSelectedIndex(2));
            if (controlCenter.IsPanelOpen)
            {
                controlCenter.CompletePresentationForCapture("notifications");
                panelPresented = true;
            }
        });
    }

    /// <summary>
    /// Deterministic active/recent App Switcher panel. Runtime rows use each
    /// scanned title's icon0; these neutral local images only make the recovered
    /// section and image-gutter layout visible in a repository-owned capture.
    /// </summary>
    private static Scene ControlCenterSwitcher(Options options)
    {
        var surface = new Panel
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
        };
        surface.Children.Add(new Prosperismo.GUI.SystemAssets.Shell.ShellBackground
        {
            IsMotionEnabled = false,
        });

        var controlCenter = new ShellControlCenter
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
            Items = ShellControlCenter.ConsoleTourItems,
        };
        controlCenter.SetClockText("07:18");
        controlCenter.RestoreSelectedItem("apps");
        surface.Children.Add(controlCenter);
        controlCenter.Open();

        Bitmap? icon = null;
        var iconPath = Path.GetFullPath("assets/branding/Square150x150Logo.png");
        if (File.Exists(iconPath))
        {
            icon = new Bitmap(iconPath);
        }
        var rows = new List<ShellFunctionPanelItem>
        {
            new("ASTRO's PLAYROOM")
            {
                LeadingImage = icon,
                SecondaryText = "PPSA01325",
                SectionHeader = "Active",
            },
            new("Minecraft")
            {
                LeadingImage = icon,
                SecondaryText = "PPSA17221",
                SectionHeader = "Last played games",
            },
            new("Recent game")
            {
                LeadingImage = icon,
                SecondaryText = "PPSA00001",
            },
        };

        var panelPresented = false;
        return new Scene(ScaleDesignSurface(surface), delta =>
        {
            if (panelPresented)
            {
                return;
            }

            _ = controlCenter.ShowPanelAsync("apps", "Switcher", rows);
            if (controlCenter.IsPanelOpen)
            {
                controlCenter.CompletePresentationForCapture("apps");
                panelPresented = true;
            }
        });
    }

    private static Scene ControlCenterNotificationDetail(Options options)
    {
        var timestamp = new DateTimeOffset(2026, 8, 11, 7, 18, 0, TimeSpan.Zero);
        var entry = new ShellNotificationHistoryEntry(
            "download",
            new ShellNotificationRequest
            {
                PrimaryText = "Download complete",
                SecondaryText = "The title is ready to play.",
                DetailText = "Your download has finished. You can start the title from your game library.",
                Surface = ShellNotificationSurface.Interactive,
                Actions =
                [
                    new ShellNotificationAction("library", "View game library"),
                    new ShellNotificationAction("dismiss", "Dismiss"),
                ],
            },
            timestamp,
            timestamp,
            ShellNotificationHistoryState.Seen);
        return ControlCenterNotificationScreen(
            "Details",
            ShellNotificationPanelComposer.ComposeDetail(entry));
    }

    private static Scene ControlCenterNotificationDeleteConfirm(Options options) =>
        ControlCenterNotificationScreen(
            "Notifications",
            ShellNotificationPanelComposer.ComposeDeleteAllConfirm(),
            selectedIndex: 1);

    private static Scene ControlCenterNotificationOptions(Options options)
    {
        var timestamp = new DateTimeOffset(2026, 8, 11, 7, 18, 0, TimeSpan.Zero);
        var entry = new ShellNotificationHistoryEntry(
            "download",
            new ShellNotificationRequest { PrimaryText = "Download complete" },
            timestamp,
            timestamp,
            ShellNotificationHistoryState.Seen);
        return ControlCenterNotificationScreen(
            "Options",
            ShellNotificationPanelComposer.ComposeListOptions([entry], entry.Id));
    }

    private static Scene ControlCenterNotificationScreen(
        string header,
        IReadOnlyList<ShellFunctionPanelItem> rows,
        int selectedIndex = 0)
    {
        var surface = new Panel
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
        };
        surface.Children.Add(new Prosperismo.GUI.SystemAssets.Shell.ShellBackground
        {
            IsMotionEnabled = false,
        });
        var controlCenter = new ShellControlCenter
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
            Items = ShellControlCenter.ConsoleTourItems,
        };
        controlCenter.SetClockText("07:18");
        controlCenter.RestoreSelectedItem("notifications");
        surface.Children.Add(controlCenter);
        controlCenter.Open();

        var panelPresented = false;
        return new Scene(ScaleDesignSurface(surface), delta =>
        {
            if (panelPresented)
            {
                return;
            }

            _ = controlCenter.ShowPanelAsync("notifications", header, rows);
            if (controlCenter.IsPanelOpen)
            {
                controlCenter.CompletePresentationForCapture("notifications");
                if (selectedIndex != 0)
                {
                    controlCenter.ReplaceOpenPanelScreen(
                        "notifications",
                        header,
                        rows,
                        selectedIndex);
                }
                panelPresented = true;
            }
        });
    }

    private static Scene Dialog(Options options)
    {
        var surface = new Panel
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
            Background = new SolidColorBrush(Color.FromRgb(2, 4, 8)),
        };
        var dialog = new ShellDialog
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
        };
        dialog.Apply(new ShellDialogRequest
        {
            Presentation = ShellDialogPresentation.Popup,
            Title = "Close application",
            Body = "The application will close. Unsaved progress will be lost.",
            Neutral = new ShellDialogButton(ShellDialogAction.Neutral, "Cancel"),
            Positive = new ShellDialogButton(ShellDialogAction.Positive, "Close application"),
        });
        surface.Children.Add(dialog);
        return new Scene(ScaleDesignSurface(surface), delta =>
        {
            dialog.RefreshFocusRect();
            if (ShellFocusRing.For(dialog) is { } ring)
            {
                ring.ManualClock = true;
                ring.Advance(delta);
            }
        });
    }

    private static Viewbox ScaleDesignSurface(Control surface) => new()
    {
        Stretch = Stretch.Uniform,
        StretchDirection = StretchDirection.Both,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Child = surface,
    };

    /// <summary>
    /// A fixed focus target with only the recovered line and area passes moving.
    /// This separates idle shader motion from layout, selection and entrance
    /// animations, so two captures can prove that a stationary ring is still
    /// </summary>
    private static Scene FocusIdle(Options options)
    {
        var root = new Panel
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
            Background = new SolidColorBrush(Color.FromRgb(2, 4, 8)),
        };
        var ring = new ShellFocusRing
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
            ManualClock = true,
            Radius = 32.0,
            LineScale = 1.0,
        };
        root.Children.Add(ring);
        ring.ShowAt(new Rect(720, 380, 480, 320));

        return new Scene(root, ring.Advance, () => ring.NativeFramePending);
    }

    private static Scene NativeBackground(
        Options options,
        bool bottom = false,
        bool settings = false)
    {
        var background = new Prosperismo.GUI.SystemAssets.Shell.ShellBackground
        {
            IsMotionEnabled = true,
            GlobalState = bottom
                ? Prosperismo.GUI.SystemAssets.Shell.ShellGlobalBackgroundState.Login
                : Prosperismo.GUI.SystemAssets.Shell.ShellGlobalBackgroundState.ColdBootAnimation,
        };
        background.NativeParticles.ManualClock = true;

        var reportedNativeFrame = false;
        var elapsed = TimeSpan.Zero;
        var ambientPublished = false;
        var settingsPublished = false;
        void Advance(TimeSpan delta)
        {
            elapsed += delta;
            if (!bottom && !ambientPublished &&
                elapsed.TotalSeconds >=
                Prosperismo.Libs.Presentation.Ps5NativeColdBootAmbientTimeline
                    .ColdBootDurationSeconds)
            {
                ambientPublished = true;
                background.ContinueAmbientSequence();
            }
            if (settings && !settingsPublished && elapsed >= options.MoveAt)
            {
                settingsPublished = true;
                background.ParticleOverlayVisible = false;
            }
            background.NativeParticles.AdvanceForCapture(delta);
            if (!reportedNativeFrame && background.NativeParticles.IsFrameLoaded)
            {
                reportedNativeFrame = true;
                Console.WriteLine("native particle frame loaded for raw state {0}",
                    Prosperismo.GUI.SystemAssets.Shell.ShellBackgroundComposition
                        .NativeParticleRouteFor(background.GlobalState).RawState);
            }
        }

        return new Scene(background, Advance, () => background.NativeParticles.LiveRenderPending);
    }

    private static Scene WaveBackground(
        Options options,
        bool highContrast,
        int themeColourIndex =
            Prosperismo.GUI.SystemAssets.Shell.Ps5NativeWavePlateEvaluator.SteadyNoParticleThemeIndex)
    {
        var background = new Prosperismo.GUI.SystemAssets.Shell.ShellBackground
        {
            IsMotionEnabled = true,
            HighContrast = highContrast,
            ThemeColourIndex = themeColourIndex,
        };

        return new Scene(background, _ => background.NativeWave.AdvanceFrame());
    }

    private static Scene Settings(Options options)
    {
        var settings = new ShellSettingsCategoryList();
        var background = new Prosperismo.GUI.SystemAssets.Shell.ShellBackground();
        var root = new Panel { Width = Ps5DesignSpace.Width, Height = Ps5DesignSpace.Height };
        root.Children.Add(background);
        root.Children.Add(settings);
        // Deliberately do not advance NativeWave by hand. This scene verifies
        // that the same RequestAnimationFrame route used by the real Settings
        return new Scene(root, _ => settings.Focus());
    }

    private static Scene SettingsDetail(Options options)
    {
        var settings = new ShellSettingsDetailList();
        // Model a real category-route entry. TabbedList sets initialFocusTab
        // and then transfers focus into the mounted content panel.
        settings.SelectedTabIndex = 0;
        var background = new Prosperismo.GUI.SystemAssets.Shell.ShellBackground();
        var root = new Panel { Width = Ps5DesignSpace.Width, Height = Ps5DesignSpace.Height };
        root.Children.Add(background);
        root.Children.Add(settings);
        return new Scene(root, _ => settings.Focus());
    }

    /// <summary>NPXS40071's recovered installed-content grid on the same
    private static Scene AllGames(Options options)
    {
        var games = new ShellAllGames
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
            Title = "Game Library",
            Items = Enumerable.Range(1, Math.Max(1, options.Tiles))
                .Select(index => new ShellLibraryItem($"Installed Game {index}")
                {
                    SubLabel = $"{18 + index * 3}.2 GB",
                    SizeBytes = (18L + index * 3) << 30,
                    InstalledAt = DateTime.Today.AddDays(-index),
                })
                .ToArray(),
            IsRegionFocused = true,
        };

        var root = new Panel { Width = Ps5DesignSpace.Width, Height = Ps5DesignSpace.Height };
        root.Children.Add(new Prosperismo.GUI.SystemAssets.Shell.ShellBackground { IsMotionEnabled = true });
        root.Children.Add(games);

        var elapsed = TimeSpan.Zero;
        bool moved = false;
        return new Scene(root, delta =>
        {
            elapsed += delta;
            games.Focus();
            if (!moved && elapsed >= options.MoveAt)
            {
                moved = true;
                games.MoveFocus(ShellFocusDirection.Right);
            }

            if (ShellFocusRing.For(games) is { } ring)
            {
                ring.ManualClock = true;
                ring.Advance(delta);
            }
        });
    }

    /// <summary>
    /// The home background following the highlight: the plate starts on one
    /// title's own artwork and runs HOME's Normal-degree SlideInLeft program
    /// when the focus moves right to the next tile.
    ///
    /// <para>Pass <c>--art-a</c> and <c>--art-b</c> the two titles' <c>sce_sys</c>
    /// folders. A folder that ships no <c>pic</c> is the point of the exercise
    /// rather than a mistake: the plate falls back to its measured basemat
    /// while the title has no artwork.</para>
    ///
    /// <para>The native image program runs on a <see cref="DispatcherTimer"/>, so this scene
    /// sleeps for the frame's worth of wall-clock time rather than pretending
    /// to advance a manual clock. That makes the captured frames real samples
    /// of the animation instead of a reconstruction of it. The scene also holds
    /// its first capture until the initial DDS has decoded, then holds the move
    /// frame until the second decode has actually started the native program;
    /// otherwise asynchronous image loading records black frames and spends the
    /// transition before there are two textures to compare.</para>
    /// </summary>
    private static Scene Backdrop(Options options)
    {
        var plate = new Ps5BackgroundPlate();

        var band = new ShellNavBand();
        band.SetClockText("21:45");

        var row = BuildRow(options.Tiles);
        var strandHost = new Panel { Height = ShellTileRow.ScaledExperienceSize };
        strandHost.Children.Add(row);

        var page = new Grid { RowDefinitions = new RowDefinitions("126,168,*") };
        Grid.SetRow(band, 0);
        page.Children.Add(band);
        Grid.SetRow(strandHost, 1);
        page.Children.Add(strandHost);

        var root = new Panel();
        root.Children.Add(plate);
        root.Children.Add(page);

        var first = Ps5TitleArtwork.ResolveBackdrop(options.ArtA);
        var second = Ps5TitleArtwork.ResolveBackdrop(options.ArtB);
        Console.WriteLine("backdrop A: {0}", first ?? "<none - uses basemat>");
        Console.WriteLine("backdrop B: {0}", second ?? "<none - uses basemat>");

        plate.TitleArtPath = first;
        plate.ConfigureNativeImageTransition(
            Prosperismo.GUI.SystemAssets.Shell.ShellLayerBackgroundTransitionType.CustomImageSlideInLeft,
            Prosperismo.GUI.SystemAssets.Shell.ShellLayerBackgroundTransitionDegree.Normal);

        var elapsed = TimeSpan.Zero;
        var moved = false;

        return new Scene(
            root,
            delta =>
            {
                elapsed += delta;
                if (!moved && elapsed >= options.MoveAt)
                {
                    moved = true;
                    Console.WriteLine("focus moves at {0:0} ms", elapsed.TotalMilliseconds);
                    plate.TitleArtPath = second;
                }

                // Real time, because the native image program is driven by a
                // real render-priority timer.
                if (delta > TimeSpan.Zero)
                {
                    Thread.Sleep(delta);
                }

                Dispatcher.UIThread.RunJobs();
                row.Advance(delta);
            },
            () => plate.IsImageLoadPending);
    }

    /// <summary>
    /// The hub open over the home: the whole home surface lifted by 166 with
    /// the switcher faded out, and the hub's header at its own inset.
    /// </summary>
    private static Scene Hub(Options options, bool ctaOnly = false)
    {
        var row = BuildRow(options.Tiles);
        var strandHost = new Panel { Height = ShellTileRow.ScaledExperienceSize };
        strandHost.Children.Add(row);

        var band = new ShellNavBand();
        band.SetClockText("21:45");

        var page = new Grid { RowDefinitions = new RowDefinitions("126,168,*") };
        Grid.SetRow(band, 0);
        page.Children.Add(band);
        Grid.SetRow(strandHost, 1);
        page.Children.Add(strandHost);

        var header = new ShellHubHeader
        {
            Title = "A Game With A Very Long Title",
            Tag = "PS4",
        };

        ShellGameHubCta? cta = null;
        ShellGameHubTitleLogo? titleLogo = null;
        Control body;
        if (ctaOnly)
        {
            // A real control capture of the bounded GameCTA host translation.
            // The extra Configure action is a current launcher capability, so
            // the ellipsis is justified here; a title without it is covered by
            // focused unit tests and renders only Play.
            cta = new ShellGameHubCta
            {
                Model = ShellGameHubCtaComposer.Compose(
                    new ShellGameHubHostCapabilities(CanLaunch: true, CanConfigureGame: true)),
                Margin = new Thickness(
                    ShellGameHubLayout.CtaOriginInHubSurface.X,
                    ShellGameHubLayout.CtaOriginInHubSurface.Y,
                    0,
                    0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            titleLogo = new ShellGameHubTitleLogo
            {
                // The capture exercises the confirmed missing-logo path. The
                // main shell supplies a verified IMAGE_TYPE.LOGO bitmap when
                // it exists; it never substitutes the current cover/backdrop.
                DisplayName = "ASTRO's PLAYROOM",
                Margin = new Thickness(
                    ShellGameHubLayout.ConsoleMeasuredLogoOriginInHubSurface.X,
                    ShellGameHubLayout.ConsoleMeasuredLogoOriginInHubSurface.Y,
                    0,
                    0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            body = cta;
        }
        else
        {
            body = new ShellSceneList
            {
                Scenes = new List<ShellScene>
                {
                    new("Continue playing", Enumerable.Range(0, 4)
                        .Select(i => new ShellSceneItem($"Title {i + 1}")).ToList()),
                    new("Recently added", Enumerable.Range(0, 4)
                        .Select(i => new ShellSceneItem($"Title {i + 5}")).ToList()),
                },
                Margin = new Thickness(ShellTileRow.ScaledExpMarginLeft, 180, 0, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            };
        }

        var hub = new Panel
        {
            Margin = new Thickness(0, ShellHubMetrics.MarginTop, 0, 0),
        };
        hub.Children.Add(header);
        if (titleLogo is not null)
        {
            hub.Children.Add(titleLogo);
        }
        hub.Children.Add(body);

        var root = new Panel();
        root.Children.Add(new Prosperismo.GUI.SystemAssets.Shell.ShellBackground { IsMotionEnabled = true });
        root.Children.Add(page);
        root.Children.Add(hub);

        var transition = new ShellHubTransition { ManualClock = true };
        transition.Attach(page, strandHost);
        transition.Open();

        return new Scene(root, delta =>
        {
            transition.Advance(delta);
            row.Advance(delta);
            cta?.Focus();
            cta?.RefreshFocusRect();
        });
    }

    /// <summary>The function-control flyout at its own anchor, over the home.</summary>
    private static Scene FunctionPanel(Options options)
    {
        var home = Home(options);

        var panel = new ShellFunctionPanel
        {
            Header = "Power",
            Items = new List<ShellFunctionPanelItem>
            {
                new("Enter Rest Mode", "⏻"),
                new("Turn Off Console", "⏻"),
                new("Restart Console", "↻"),
                new("Sign Out", "→"),
            },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(
                ShellFunctionPanelMetrics.AnchorX,
                ShellFunctionPanelMetrics.AnchorY,
                0,
                0),
        };

        // The hub's utility strip, drawn at the left inset so its 56 on 48
        // rhythm can be checked against the nav band's above it.
        var utility = new ShellUtilityStrip
        {
            Items = new List<ShellUtilityItem>
            {
                new("Search", "⌕"),
                new("Filter", "≡"),
                new("Sort", "↕"),
                new("Options", "⋯"),
            },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(ShellTileRow.ScaledExpMarginLeft, 420, 0, 0),
        };

        var root = new Panel();
        root.Children.Add(home.Root);
        root.Children.Add(utility);
        root.Children.Add(panel);
        return new Scene(root, home.Advance);
    }

    /// <summary>NPXS40002's offline/local profile popup at HOME's fixed anchor.</summary>
    private static Scene ProfilePanel(Options options)
    {
        var home = Home(options);
        var root = new Panel
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
        };
        root.Children.Add(home.Root);
        root.Children.Add(new Border
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
            Background = new SolidColorBrush(Color.FromArgb(0xA6, 0, 0, 0)),
        });
        var panel = new ShellFunctionPanel
        {
            Header = "Prosperismo",
            Items = ShellProfilePanelComposer.ComposeOffline(),
        };
        panel.SetSelectedIndex(ShellProfilePanelComposer.OfflineInitialSelectedIndex);
        panel.Margin = new Thickness(
            ShellFunctionPanelMetrics.AnchorX,
            ShellFunctionPanelMetrics.AnchorY,
            0,
            0);
        panel.HorizontalAlignment = HorizontalAlignment.Left;
        panel.VerticalAlignment = VerticalAlignment.Top;
        root.Children.Add(panel);
        return new Scene(ScaleDesignSurface(root), delta =>
        {
            home.Advance?.Invoke(delta);
            panel.Focus();
            panel.RefreshFocusRect();
            if (ShellFocusRing.For(panel) is { } ring)
            {
                ring.ManualClock = true;
                ring.Advance(delta);
            }
        });
    }

    private static Scene Search(Options options)
    {
        var root = new Panel
        {
            Width = Ps5DesignSpace.Width,
            Height = Ps5DesignSpace.Height,
        };
        root.Children.Add(new Prosperismo.GUI.SystemAssets.Shell.ShellBackground
        {
            IsMotionEnabled = false,
        });
        var search = new ShellSearchSurface();
        search.SetItems(
        [
            new("ASTRO's PLAYROOM"),
            new("Minecraft"),
            new("Demon's Souls"),
            new("Returnal"),
            new("Ratchet & Clank"),
            new("Gran Turismo 7"),
            new("Horizon Forbidden West"),
            new("Sackboy: A Big Adventure"),
        ]);
        search.Open();
        root.Children.Add(search);
        var selected = false;
        return new Scene(ScaleDesignSurface(root), delta =>
        {
            if (!selected)
            {
                search.MoveVertical(1);
                selected = true;
            }
            search.RefreshFocusRect();
            if (ShellFocusRing.For(search) is { } ring)
            {
                ring.ManualClock = true;
                ring.Advance(delta);
            }
        });
    }

    private static ShellTileRow BuildRow(int count)
    {
        var row = new ShellTileRow
        {
            TileWidth = ShellTileRow.ScaledExperienceSize,
            TileHeight = ShellTileRow.ScaledExperienceSize,
            RestScale = ShellTileRow.ExperienceSize / ShellTileRow.ScaledExperienceSize,
            TileGap = ShellTileRow.DefaultItemMargin,
            FocusedMargin = ShellTileRow.DefaultFocusedMargin,
            TileCornerRadius = ShellTileRow.SwitcherStyles.FocusContainerBorderRadius,
            FocusAnchorX = ShellTileRow.ScaledExpMarginLeft,
            IsRegionFocused = true,
            // A headless run has no wall clock, so the row's own DispatcherTimer
            // would tick with a zero delta and nothing would ever move. The host
            // drives it instead.
            ManualClock = true,
        };

        var items = new List<ShellTile>();
        for (int i = 0; i < count; i++)
        {
            items.Add(i == 0
                ? new ShellTile("A Game With A Very Long Title That Has To Scroll To Be Read", "Bundled")
                : new ShellTile(
                    string.Format(CultureInfo.InvariantCulture, "Title {0}", i + 1),
                    "Publisher"));
        }

        row.Items = items;
        return row;
    }

    private static ShellTileRow BuildRow(IReadOnlyList<ShellTile> items)
    {
        var row = BuildRow(0);
        row.Items = items;
        return row;
    }

    private static Scene TileRow(Options options)
    {
        var row = BuildRow(options.Tiles);
        var host = new Panel { Height = ShellTileRow.ScaledExperienceSize };
        host.Children.Add(row);
        return new Scene(Wrap(host, top: 300), delta =>
        {
            row.Advance(delta);
            row.RefreshFocusRect();
            Dispatcher.UIThread.RunJobs();
            if (ShellFocusRing.For(row) is { } ring)
            {
                ring.ManualClock = true;
                ring.Advance(delta);
            }
        });
    }

    private static Scene Marquee(Options options)
    {
        var label = new ShellMarqueeText
        {
            Text = "A Game With A Very Long Title That Has To Scroll To Be Read",
            IsMarquee = true,
            FontSize = 26,
            Width = 420,
            Height = 40,
            ManualClock = true,
            Foreground = Brushes.White,
        };

        var host = new Panel { Height = 60 };
        host.Children.Add(label);
        return new Scene(Wrap(host, top: 400), label.Advance);
    }

    private static Scene Entrance(Options options)
    {
        var row = BuildRow(options.Tiles);
        var strandHost = new Panel { Height = ShellTileRow.ScaledExperienceSize };
        strandHost.Children.Add(row);

        var band = new ShellNavBand();
        band.SetClockText("21:45");

        var page = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };
        Grid.SetRow(band, 0);
        page.Children.Add(band);
        Grid.SetRow(strandHost, 2);
        page.Children.Add(strandHost);

        var entrance = new ShellEntrance { ManualClock = true };
        entrance.Attach(strandHost, band, null);
        entrance.Begin(options.Tiles);

        return new Scene(page, delta =>
        {
            entrance.Advance(delta);
            row.Advance(delta);
        });
    }

    /// <summary>
    /// The settled home surface as the window composes it: the live background,
    /// then the 126 px nav band, then the 168 px switcher band under it. No
    /// entrance, so this is what the shell actually sits at.
    /// </summary>
    private static Scene Home(Options options)
    {
        var row = BuildRow(options.Tiles);
        var strandHost = new Panel { Height = ShellTileRow.ScaledExperienceSize };
        strandHost.Children.Add(row);

        var band = new ShellNavBand();
        band.SetClockText("21:45");

        var page = new Grid { RowDefinitions = new RowDefinitions("126,168,*") };
        Grid.SetRow(band, 0);
        page.Children.Add(band);
        Grid.SetRow(strandHost, 1);
        page.Children.Add(strandHost);

        var background = new Prosperismo.GUI.SystemAssets.Shell.ShellBackground
        {
            IsMotionEnabled = true,
            // HOME's resting particle state. The layer is gated on the global
            // background state, so leaving it at the default kept the particle
            // pass hidden and the capture showed only the plate.
            GlobalState = Prosperismo.GUI.SystemAssets.Shell.ShellGlobalBackgroundState.ParticleBottom,
        };
        var root = new Panel();
        root.Children.Add(background);
        root.Children.Add(page);

        // The particle layer normally advances on a 30 Hz DispatcherTimer, which
        // never ticks under headless capture. Pump it from the scene clock so a
        // captured frame shows the same layers the live shell draws - otherwise
        // captures silently omit the background and cannot be graded.
        return new Scene(root, elapsed =>
        {
            row.Advance(elapsed);
            background.NativeParticles.AdvanceFrameForCapture();
        });
    }

    /// <summary>
    /// The focus highlight travelling along the nav band's system icons.
    ///
    /// <para>The band is the one part of the home surface where the highlight
    /// itself moves rather than the content moving under it, so it is where the
    /// warp, the directional stretch and the dark first half of a move can
    /// actually be looked at.</para>
    /// </summary>
    private static Scene FocusNavBand(Options options)
    {
        var band = new ShellNavBand();
        band.SetClockText("21:45");
        band.FocusedRegion = ShellNavBand.SystemRegion;
        band.SetSelectedSystemIndex(0);

        var page = new Grid { RowDefinitions = new RowDefinitions("126,*") };
        Grid.SetRow(band, 0);
        page.Children.Add(band);

        var root = new Panel();
        root.Children.Add(new Prosperismo.GUI.SystemAssets.Shell.ShellBackground { IsMotionEnabled = true });
        root.Children.Add(page);

        var elapsed = TimeSpan.Zero;
        bool moved = false;

        return new Scene(root, delta =>
        {
            elapsed += delta;
            if (!moved && elapsed >= options.MoveAt)
            {
                moved = true;
                Console.WriteLine("focus moves at {0:0} ms", elapsed.TotalMilliseconds);
                band.SetSelectedSystemIndex(3);
            }

            // Headless capture advances simulated time much faster than wall
            // time, while ShellNavBand's product timer is wall-clock driven.
            // Drive the same public spring step explicitly so a 4000 ms frame
            // is actually the settled white-disc/dark-glyph state.
            band.AdvanceGlance(delta);
            Dispatcher.UIThread.RunJobs();

            if (ShellFocusRing.For(band) is { } ring)
            {
                ring.ManualClock = true;
                ring.Advance(delta);
            }
        });
    }

    /// <summary>
    /// The focus highlight travelling down a list.
    ///
    /// <para>This is the scene the travel actually shows in. The tile row anchors
    /// the focused tile at a fixed inset and slides the strand underneath it, so
    /// the highlight there never moves however correct its motion is — a list is
    /// the only place the warp, the directional stretch and the dark first half
    /// of a move are visible at all.</para>
    /// </summary>
    private static Scene FocusList(Options options)
    {
        var panel = new ShellFunctionPanel
        {
            Header = "Power",
            Items = new List<ShellFunctionPanelItem>
            {
                new("Enter Rest Mode", "⏻"),
                new("Turn Off Console", "⏻"),
                new("Restart Console", "↻"),
                new("Sign Out", "→"),
            },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(
                ShellFunctionPanelMetrics.AnchorX,
                ShellFunctionPanelMetrics.AnchorY,
                0,
                0),
        };

        var root = new Panel();
        root.Children.Add(new Prosperismo.GUI.SystemAssets.Shell.ShellBackground { IsMotionEnabled = true });
        root.Children.Add(panel);

        var elapsed = TimeSpan.Zero;
        bool moved = false;

        return new Scene(root, delta =>
        {
            elapsed += delta;
            if (!moved && elapsed >= options.MoveAt)
            {
                moved = true;
                Console.WriteLine("focus moves at {0:0} ms", elapsed.TotalMilliseconds);
                panel.SetSelectedIndex(3);
            }

            if (ShellFocusRing.For(panel) is { } ring)
            {
                ring.ManualClock = true;
                ring.Advance(delta);
            }
        });
    }

    /// <summary>
    /// The focus highlight travelling between tiles: the home surface, settled,
    /// with the selection moved once at <c>--move-at</c>.
    ///
    /// <para>The point is being able to look at the move rather than only at its
    /// endpoints. The band is dark for roughly the first half of a travel by
    /// design, so a capture taken only at rest cannot tell a correct
    /// implementation from one that drags a rectangle across the screen — the
    /// mid-move frames are the ones that show which it is.</para>
    /// </summary>
    private static Scene Focus(Options options)
    {
        var row = BuildRow(options.Tiles);
        var strandHost = new Panel { Height = ShellTileRow.ScaledExperienceSize };
        strandHost.Children.Add(row);

        var band = new ShellNavBand();
        band.SetClockText("21:45");

        var page = new Grid { RowDefinitions = new RowDefinitions("126,168,*") };
        Grid.SetRow(band, 0);
        page.Children.Add(band);
        Grid.SetRow(strandHost, 1);
        page.Children.Add(strandHost);

        var root = new Panel();
        // This target isolates focus motion. Parking the independent Plane2
        // clock avoids spending every headless pump rebuilding a full-screen
        // background while preserving the exact focused-card composite.
        root.Children.Add(new Prosperismo.GUI.SystemAssets.Shell.ShellBackground { IsMotionEnabled = false });
        root.Children.Add(page);

        var elapsed = TimeSpan.Zero;
        bool moved = false;

        return new Scene(root, delta =>
        {
            elapsed += delta;
            if (!moved && elapsed >= options.MoveAt)
            {
                moved = true;
                Console.WriteLine("focus moves at {0:0} ms", elapsed.TotalMilliseconds);
                row.SelectedIndex = 2;
            }

            row.Advance(delta);
            row.RefreshFocusRect();
            Dispatcher.UIThread.RunJobs();

            // The highlight lives on the scene's overlay layer, not inside the
            // row, so it has its own clock to advance.
            if (ShellFocusRing.For(row) is { } ring)
            {
                ring.ManualClock = true;
                ring.Advance(delta);
            }
        });
    }

    private static Control Wrap(Control inner, double top)
    {
        var grid = new Grid();
        inner.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        inner.Margin = new Thickness(0, top, 0, 0);
        grid.Children.Add(inner);
        return grid;
    }
}

internal sealed record Options(
    string Output,
    string Scene,
    int Frames,
    double StepMs,
    int Tiles,
    int Width,
    int Height,
    string? ArtA = null,
    string? ArtB = null,
    TimeSpan MoveAt = default,
    TimeSpan StartAt = default)
{
    public const string Usage =
        "usage: shell-shot --out <dir> [--scene tilerow|entrance|marquee|home|focus|focus-idle|list|navband|panel|profile|search|hub|hub-cta|backdrop|wave-background|high-contrast-background|theme-one-background|native-background|native-background-settings|native-background-bottom|settings|settings-detail|all-games|notification|control-center|control-center-switcher|control-center-notifications|control-center-notification-detail|control-center-notification-options|control-center-notification-delete-confirm|dialog|desktop-launcher]\n" +
        "                  [--frames N] [--step MS] [--start MS] [--tiles N]\n" +
        "       backdrop:  --art-a <sce_sys dir> --art-b <sce_sys dir> [--move-at MS]";

    public static Options? Parse(string[] args)
    {
        string? output = null;
        string scene = "tilerow";
        int frames = 1;
        double step = 100;
        int tiles = 8;
        int width = 1920;
        int height = 1080;
        string? artA = null;
        string? artB = null;
        double moveAtMs = 200;
        double startAtMs = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: output = args[++i]; break;
                case "--scene" when i + 1 < args.Length: scene = args[++i]; break;
                case "--frames" when i + 1 < args.Length: frames = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--step" when i + 1 < args.Length: step = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--tiles" when i + 1 < args.Length: tiles = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--width" when i + 1 < args.Length: width = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--height" when i + 1 < args.Length: height = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--art-a" when i + 1 < args.Length: artA = args[++i]; break;
                case "--art-b" when i + 1 < args.Length: artB = args[++i]; break;
                case "--move-at" when i + 1 < args.Length: moveAtMs = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--start" when i + 1 < args.Length: startAtMs = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: break;
            }
        }

        return output is null
            ? null
            : new Options(
                output, scene, frames, step, tiles, width, height,
                artA, artB,
                TimeSpan.FromMilliseconds(moveAtMs),
                TimeSpan.FromMilliseconds(startAtMs));
    }
}
