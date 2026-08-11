// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Prosperismo.Libs.Presentation;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// NPXS40033's independent Game Hub title-logo channel. It deliberately takes
/// only an <c>IMAGE_TYPE.LOGO</c>-resolved image from the title model: cover,
/// splash, background, and preview media must never be substituted here.
/// </summary>
public sealed class ShellGameHubTitleLogo : TemplatedControl
{
    // NPXS40033 m1351's textTitle style. It applies to the display-name
    // fallback only; an IMAGE_TYPE.LOGO starts at the slot origin instead.
    private const double DisplayNameTopMargin = 20;

    /// <summary>Recovered independent logo size cap.</summary>
    public static Npxs40033GameHubContract Contract => Npxs40087ShellContract.GameHub;

    public static double MaximumWidth => Contract.LogoMaximumWidth;

    public static double MaximumHeight => Contract.LogoMaximumHeight;

    public static int DisplayNameFallbackMaximumLines => Contract.DisplayNameFallbackMaximumLines;

    public static readonly StyledProperty<IImage?> LogoProperty =
        AvaloniaProperty.Register<ShellGameHubTitleLogo, IImage?>(nameof(Logo));

    public static readonly StyledProperty<string?> DisplayNameProperty =
        AvaloniaProperty.Register<ShellGameHubTitleLogo, string?>(nameof(DisplayName));

    private Image? _image;
    private TextBlock? _fallback;

    public ShellGameHubTitleLogo()
    {
        Template = BuildTemplate();
    }

    /// <summary>
    /// Independently resolved title-logo image. A null/error result leaves the
    /// display-name fallback visible rather than omitting this surface.
    /// </summary>
    public IImage? Logo
    {
        get => GetValue(LogoProperty);
        set => SetValue(LogoProperty, value);
    }

    /// <summary>Game display name used only for the confirmed missing-logo fallback.</summary>
    public string? DisplayName
    {
        get => GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LogoProperty || change.Property == DisplayNameProperty)
        {
            Refresh();
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _image = e.NameScope.Find<Image>("PART_Logo");
        _fallback = e.NameScope.Find<TextBlock>("PART_Fallback");
        Refresh();
    }

    private static FuncControlTemplate BuildTemplate() => new((_, scope) =>
    {
        var maximum = ShellGameHubLayout.LogoMaximumSize;
        var panel = new Panel
        {
            MaxWidth = maximum.Width,
            Margin = new Thickness(0, 0, 0, Contract.LogoBottomMargin),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var image = new Image
        {
            Name = "PART_Logo",
            MaxWidth = maximum.Width,
            MaxHeight = maximum.Height,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        image.RegisterInNameScope(scope);

        var fallback = new TextBlock
        {
            Name = "PART_Fallback",
            MaxWidth = maximum.Width,
            MaxLines = Contract.DisplayNameFallbackMaximumLines,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = ShellFontSize.XXLarge,
            Foreground = Brushes.White,
            Margin = new Thickness(0, DisplayNameTopMargin, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        fallback.RegisterInNameScope(scope);

        panel.Children.Add(image);
        panel.Children.Add(fallback);
        return panel;
    });

    private void Refresh()
    {
        if (_image is null || _fallback is null)
        {
            return;
        }

        var hasLogo = Logo is not null;
        _image.Source = Logo;
        _image.IsVisible = hasLogo;
        _fallback.Text = DisplayName ?? string.Empty;
        _fallback.IsVisible = !hasLogo && !string.IsNullOrWhiteSpace(DisplayName);
        IsVisible = hasLogo || _fallback.IsVisible;
    }
}
