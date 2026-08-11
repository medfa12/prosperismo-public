// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// One scene in a hub: a heading over a row of content tiles.
///
/// A scene's <b>content</b> is not in the home bundle. <c>Scene.tsx</c>
/// (HOME m169) is a data provider that renders whatever the hub application
/// hands it - <c>{data, error, loading, sceneIndex, templateRef, ...}</c> - and
/// each hub ships its own renderer in its own RNPS bundle. What the home bundle
/// owns, and what this is, is the container: how scenes stack, how they take
/// focus, and how they fade.
///
/// So the tiles here are drawn at the console's own content-tile sizes
/// (<see cref="ShellTileCatalogue"/>) from whatever the host supplies. For us
/// that is the local library, which is real content rather than a placeholder.
/// </summary>
public sealed record ShellSceneItem
{
    public ShellSceneItem(string title, IImage? art = null, object? tag = null)
    {
        Title = title ?? string.Empty;
        Art = art;
        Tag = tag;
    }

    /// <summary>Label under the tile.</summary>
    public string Title { get; init; }

    /// <summary>Cover art, or null for the fallback icon.</summary>
    public IImage? Art { get; init; }

    /// <summary>Caller payload.</summary>
    public object? Tag { get; init; }
}

/// <summary>One scene: a heading and its row of items.</summary>
public sealed record ShellScene
{
    public ShellScene(string heading, IReadOnlyList<ShellSceneItem> items, ShellTileSpec? tile = null)
    {
        Heading = heading ?? string.Empty;
        Items = items ?? Array.Empty<ShellSceneItem>();
        Tile = tile ?? ShellTileCatalogue.SquareSmall;
    }

    /// <summary>The scene's title.</summary>
    public string Heading { get; init; }

    /// <summary>What the scene shows.</summary>
    public IReadOnlyList<ShellSceneItem> Items { get; init; }

    /// <summary>Which catalogue shape its tiles are drawn at.</summary>
    public ShellTileSpec Tile { get; init; }
}

/// <summary>
/// The hub's scene list, HOME m391 with the container offset from m399.
///
/// Scenes stack vertically, the list pulls itself up by
/// <see cref="ShellSceneListMetrics.ContainerMarginTop"/> so the first sits
/// flush against the row above, and each scene past the first carries a body
/// margin.
///
/// Each scene is its own focus layer with <c>canMoveLeft: false,
/// canMoveRight: false</c>: horizontal movement belongs to the tiles inside a
/// scene, vertical movement moves between scenes. That split is why a hub feels
/// like a stack of independent rows rather than one grid.
/// </summary>
public sealed class ShellSceneList : TemplatedControl
{
    private static readonly IBrush HeadingBrush =
        new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

    private static readonly IBrush TilePlateBrush =
        new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));

    private static readonly IBrush TileFocusBrush =
        new SolidColorBrush(Color.FromRgb(0x2E, 0xA8, 0xE0));

    private static readonly IBrush LabelBrush = Brushes.White;

    /// <summary>Gap between a scene's heading and its tiles.</summary>
    public const double HeadingGap = ShellTilePadding.Medium;

    /// <summary>Gap between one scene and the next, the source's body margin.</summary>
    public const double SceneGap = 48.0;

    /// <summary>Gap between tiles within a scene.</summary>
    public const double TileGap = ShellTilePadding.Large;

    public static readonly StyledProperty<IReadOnlyList<ShellScene>?> ScenesProperty =
        AvaloniaProperty.Register<ShellSceneList, IReadOnlyList<ShellScene>?>(nameof(Scenes));

    public static readonly StyledProperty<int> SelectedSceneProperty =
        AvaloniaProperty.Register<ShellSceneList, int>(nameof(SelectedScene), 0);

    public static readonly StyledProperty<int> SelectedItemIndexProperty =
        AvaloniaProperty.Register<ShellSceneList, int>(nameof(SelectedItemIndex), 0);

    /// <summary>Authored pixels to host pixels.</summary>
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<ShellSceneList, double>(nameof(Scale), 1.0);

    private StackPanel? _host;
    private readonly List<List<Border>> _tiles = new();

    public ShellSceneList()
    {
        Focusable = true;
        Template = BuildTemplate();
    }

    /// <summary>Raised when the focused scene or tile changes.</summary>
    public event EventHandler? SelectionChanged;

    public IReadOnlyList<ShellScene>? Scenes
    {
        get => GetValue(ScenesProperty);
        set => SetValue(ScenesProperty, value);
    }

    public int SelectedScene
    {
        get => GetValue(SelectedSceneProperty);
        set => SetValue(SelectedSceneProperty, value);
    }

    public int SelectedItemIndex
    {
        get => GetValue(SelectedItemIndexProperty);
        set => SetValue(SelectedItemIndexProperty, value);
    }

    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    /// <summary>Scenes currently held.</summary>
    public int SceneCount => Scenes?.Count ?? 0;

    /// <summary>Items in the focused scene.</summary>
    public int ItemCountInSelectedScene =>
        Scenes is { } s && SelectedScene >= 0 && SelectedScene < s.Count
            ? s[SelectedScene].Items.Count
            : 0;

    /// <summary>
    /// Moves between scenes. Vertical only: horizontal belongs to the tiles.
    /// </summary>
    public void MoveScene(int delta)
    {
        if (SceneCount == 0)
        {
            return;
        }

        int next = Math.Clamp(SelectedScene + delta, 0, SceneCount - 1);
        if (next == SelectedScene)
        {
            return;
        }

        SetCurrentValue(SelectedSceneProperty, next);

        // Entering a scene starts at its first tile rather than keeping a
        // column, which is what makes each scene feel independent.
        SetCurrentValue(SelectedItemIndexProperty, 0);
    }

    /// <summary>Moves within the focused scene.</summary>
    public void MoveItem(int delta)
    {
        int count = ItemCountInSelectedScene;
        if (count == 0)
        {
            return;
        }

        SetCurrentValue(
            SelectedItemIndexProperty,
            Math.Clamp(SelectedItemIndex + delta, 0, count - 1));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                MoveScene(-1);
                e.Handled = true;
                return;
            case Key.Down:
                MoveScene(1);
                e.Handled = true;
                return;
            case Key.Left:
                MoveItem(-1);
                e.Handled = true;
                return;
            case Key.Right:
                MoveItem(1);
                e.Handled = true;
                return;
            default:
                base.OnKeyDown(e);
                return;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ScenesProperty || change.Property == ScaleProperty)
        {
            Rebuild();
        }
        else if (change.Property == SelectedSceneProperty
                 || change.Property == SelectedItemIndexProperty)
        {
            UpdateVisuals();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _host = e.NameScope.Find<StackPanel>("PART_Scenes");
        Rebuild();
    }

    private static FuncControlTemplate BuildTemplate() => new((_, ns) =>
    {
        var scenes = new StackPanel
        {
            Name = "PART_Scenes",
            Orientation = Orientation.Vertical,
        };
        scenes.RegisterInNameScope(ns);

        var scroll = new ScrollViewer
        {
            Content = scenes,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        return scroll;
    });

    private void Rebuild()
    {
        if (_host is null)
        {
            return;
        }

        double scale = Scale > 0 ? Scale : 1.0;

        // `container.marginTop: -40` pulls the list up so the first scene sits
        // flush against the row above. It is deliberately NOT written to this
        // control's Margin: that is the host's, and a control that overwrites
        // its parent's placement silently ignores it. The number is on
        // ShellSceneListMetrics for a host to fold into its own offset.

        _host.Children.Clear();
        _tiles.Clear();

        var scenes = Scenes;
        if (scenes is null)
        {
            return;
        }

        for (int s = 0; s < scenes.Count; s++)
        {
            var scene = scenes[s];

            var heading = new TextBlock
            {
                Text = scene.Heading,
                Foreground = HeadingBrush,
                FontSize = 28 * scale,
                Margin = new Thickness(0, 0, 0, HeadingGap * scale),
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var sceneTiles = new List<Border>();

            for (int i = 0; i < scene.Items.Count; i++)
            {
                var item = scene.Items[i];

                var art = item.Art is null
                    ? (Control)new Border { Background = TilePlateBrush }
                    : new Image { Source = item.Art, Stretch = Stretch.UniformToFill };

                var label = new TextBlock
                {
                    Text = item.Title,
                    Foreground = LabelBrush,
                    FontSize = 20 * scale,
                    MaxWidth = scene.Tile.Width * scale,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, ShellTilePadding.Small * scale, 0, 0),
                };

                var media = new Border
                {
                    Width = scene.Tile.MediaWidth * scale,
                    Height = scene.Tile.MediaHeight * scale,
                    CornerRadius = new CornerRadius(ShellTileRow.BorderRadius * scale),
                    ClipToBounds = true,
                    Child = art,
                };

                var stack = new StackPanel { Orientation = Orientation.Vertical };
                stack.Children.Add(media);
                stack.Children.Add(label);

                var tile = new Border
                {
                    BorderThickness = new Thickness(3 * scale),
                    BorderBrush = Brushes.Transparent,
                    CornerRadius = new CornerRadius((ShellTileRow.BorderRadius + 3) * scale),
                    Padding = new Thickness(0),
                    Margin = new Thickness(i == 0 ? 0 : TileGap * scale, 0, 0, 0),
                    Child = stack,
                };

                int capturedScene = s;
                int capturedItem = i;
                tile.PointerEntered += (_, _) =>
                {
                    SetCurrentValue(SelectedSceneProperty, capturedScene);
                    SetCurrentValue(SelectedItemIndexProperty, capturedItem);
                };

                sceneTiles.Add(tile);
                row.Children.Add(tile);
            }

            _tiles.Add(sceneTiles);

            var block = new StackPanel
            {
                Orientation = Orientation.Vertical,
                // Every scene past the first carries the body margin.
                Margin = new Thickness(0, s == 0 ? 0 : SceneGap * scale, 0, 0),
            };
            block.Children.Add(heading);
            block.Children.Add(row);
            _host.Children.Add(block);
        }

        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        for (int s = 0; s < _tiles.Count; s++)
        {
            for (int i = 0; i < _tiles[s].Count; i++)
            {
                bool focused = s == SelectedScene && i == SelectedItemIndex;
                _tiles[s][i].BorderBrush = focused ? TileFocusBrush : Brushes.Transparent;
            }
        }
    }
}
