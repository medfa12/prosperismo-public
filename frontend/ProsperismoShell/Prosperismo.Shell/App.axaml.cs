// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Prosperismo.GUI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Keeps the shared resource explicit for hosts that initialize the shell
    /// through the application lifetime. The family itself is already bundled
    /// </summary>
    internal void ApplyShellTypeface()
    {
        Resources["ShellFontFamily"] = new Avalonia.Media.FontFamily(
            Ps5Home.Ps5FontLibrary.OpenFamilyResource);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ApplyShellTypeface();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
