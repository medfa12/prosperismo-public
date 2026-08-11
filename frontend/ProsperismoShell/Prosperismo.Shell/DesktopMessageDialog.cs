// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Prosperismo.GUI;

/// <summary>Small native desktop message box used by the translated Qt launcher.</summary>
internal sealed class DesktopMessageDialog : Window
{
    private DesktopMessageDialog(string title, string message, bool confirmation)
    {
        Title = title;
        Classes.Add("psDesktopDialog");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        if (confirmation)
        {
            var no = new Button { Content = "No", MinWidth = 84, Classes = { "ghost" } };
            no.Click += (_, _) => Close(false);
            buttons.Children.Add(no);
        }
        var yes = new Button
        {
            Content = confirmation ? "Yes" : "OK",
            MinWidth = 84,
            Classes = { confirmation ? "accent" : "ghost" },
        };
        yes.Click += (_, _) => Close(true);
        buttons.Children.Add(yes);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 680,
                },
                buttons,
            },
        };
    }

    public static async Task ShowAsync(Window owner, string title, string message)
    {
        _ = await new DesktopMessageDialog(title, message, confirmation: false)
            .ShowDialog<bool>(owner);
    }

    public static Task<bool> ConfirmAsync(Window owner, string title, string message) =>
        new DesktopMessageDialog(title, message, confirmation: true).ShowDialog<bool>(owner);

    private void Close(bool result)
    {
        base.Close(result);
    }
}
