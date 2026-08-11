// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
namespace Prosperismo.GUI;

/// <summary>
/// Entry point for the desktop frontend, hosted by the Prosperismo executable
/// when it is started without command-line arguments.
/// </summary>
public static class GuiLauncher
{
    public static int Run()
    {
        try
        {
            // StartWithClassicDesktopLifetime spelled out so a pre-shell window
            // can be put on screen between the shell being constructed and the
            // main loop showing it.
            var lifetime = new ClassicDesktopStyleApplicationLifetime
            {
                Args = Array.Empty<string>(),
                // Keyed on the main window rather than the last one, so a
                // transient window closing does not read as "last window closed"
                // and quit the app.
                ShutdownMode = ShutdownMode.OnMainWindowClose,
            };

            BuildAvaloniaApp().SetupWithLifetime(lifetime);
            return lifetime.Start(Array.Empty<string>());
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            throw;
        }
    }

    /// <summary>
    /// Default UI typeface embedded in this assembly: Fira Sans (SIL OFL
    /// 1.1), shared by the Desktop and Big Picture modes.
    /// </summary>
    private const string DefaultFontFamily = "avares://Prosperismo.Shell/Assets/Fonts#Fira Sans";

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = DefaultFontFamily })
            .LogToTrace();

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "gui-crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Crash logging is best-effort.
        }
    }
}
