// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.GUI.SystemAssets;

namespace Prosperismo.GUI.Controls;

public sealed record ShellSearchItem(string Title, IImage? Image = null, object? Tag = null);

public sealed class ShellSearchQueryEventArgs(string query) : EventArgs
{
    public string Query { get; } = query;
}

public sealed class ShellSearchItemEventArgs(int index, ShellSearchItem? item) : EventArgs
{
    public int Index { get; } = index;
    public ShellSearchItem? Item { get; } = item;
}

/// <summary>
/// frame, 72 px input, four 370 px columns and eight-result strand while the
/// host supplies only locally installed games; no online catalogue is implied.
/// </summary>
public sealed class ShellSearchSurface : Canvas
{
    public const double DesignWidth = 1920;
    public const double DesignHeight = 1080;
    public const double ContainerLeft = ((DesignWidth - ShellSearchMetrics.ContentWidth) / 2.0) - 46.0;
    public const double ResultsTop = ShellSearchMetrics.PageMarginTop +
        ShellSearchMetrics.InputHeight + ShellSearchMetrics.InputMarginBottom;

    private static readonly IBrush TileBrush =
        new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush InputBrush =
        new SolidColorBrush(Color.FromArgb(0xD8, 0x18, 0x1B, 0x21));

    private readonly TextBox _input;
    private readonly Canvas _results;
    private readonly TextBlock _emptyMessage;
    private readonly List<Border> _tiles = [];
    private IReadOnlyList<ShellSearchItem> _items = [];
    private int _selectedIndex = -1;

    public ShellSearchSurface()
    {
        Width = DesignWidth;
        Height = DesignHeight;
        ClipToBounds = true;
        Focusable = true;

        var searchIcon = new Ps5IconPresenter
        {
            IconId = "search",
            Width = 40,
            Height = 40,
            Margin = new Thickness(16),
            Tint = Colors.White,
            OverrideDeclaredFill = true,
        };
        _input = new TextBox
        {
            Watermark = "Search installed games",
            MaxLength = ShellSearchMetrics.InputMaxLength,
            Height = ShellSearchMetrics.InputHeight,
            FontSize = Ps5FontScale.SizeNormal,
            Foreground = Brushes.White,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 0, 24, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _input.TextChanged += (_, _) =>
            QueryChanged?.Invoke(this, new ShellSearchQueryEventArgs(Query));
        _input.KeyDown += OnInputKeyDown;

        var inputGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("72,*") };
        Grid.SetColumn(searchIcon, 0);
        inputGrid.Children.Add(searchIcon);
        Grid.SetColumn(_input, 1);
        inputGrid.Children.Add(_input);
        var inputPlate = new Border
        {
            Width = ShellSearchMetrics.ContentWidth,
            Height = ShellSearchMetrics.InputHeight,
            CornerRadius = new CornerRadius(8),
            Background = InputBrush,
            Child = inputGrid,
        };
        SetLeft(inputPlate, ContainerLeft);
        SetTop(inputPlate, ShellSearchMetrics.PageMarginTop);
        Children.Add(inputPlate);

        _results = new Canvas
        {
            Width = ShellSearchMetrics.ContentWidth,
            Height = ShellSearchMetrics.SceneContainerHeight - ResultsTop,
        };
        SetLeft(_results, ContainerLeft);
        SetTop(_results, ResultsTop);
        Children.Add(_results);

        _emptyMessage = new TextBlock
        {
            Width = ShellSearchMetrics.ContentWidth,
            Height = 200,
            FontSize = Ps5FontScale.SizeNormal,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        SetLeft(_emptyMessage, ContainerLeft);
        SetTop(_emptyMessage, 400 - ShellSearchMetrics.InputMarginBottom);
        Children.Add(_emptyMessage);

        IsVisible = false;
        IsHitTestVisible = false;
    }

    public event EventHandler<ShellSearchQueryEventArgs>? QueryChanged;
    public event EventHandler<ShellSearchItemEventArgs>? ItemActivated;
    public event EventHandler? CloseRequested;

    public string Query => _input.Text?.Trim() ?? string.Empty;
    public int Count => _items.Count;
    public int SelectedIndex => _selectedIndex;
    public ShellSearchItem? SelectedItem =>
        _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;

    public void Open(string? initialQuery = null)
    {
        IsVisible = true;
        IsHitTestVisible = true;
        _selectedIndex = -1;
        _input.Text = initialQuery ?? string.Empty;
        UpdateFocusVisual();
        Dispatcher.UIThread.Post(() => _input.Focus(), DispatcherPriority.Loaded);
    }

    public void Close()
    {
        ShellFocusRing.For(this)?.Release(this);
        IsHitTestVisible = false;
        IsVisible = false;
    }

    public void SetItems(IReadOnlyList<ShellSearchItem>? items)
    {
        _items = (items ?? []).Take(ShellSearchMetrics.ItemsPerStrand).ToArray();
        if (_selectedIndex >= _items.Count)
        {
            _selectedIndex = _items.Count - 1;
        }
        RebuildResults();
    }

    public void MoveHorizontal(int delta)
    {
        if (_selectedIndex < 0 || _items.Count == 0 || delta == 0)
        {
            return;
        }
        SetSelectedIndex(Math.Clamp(_selectedIndex + Math.Sign(delta), 0, _items.Count - 1));
    }

    public void MoveVertical(int delta)
    {
        if (delta == 0)
        {
            return;
        }
        if (_selectedIndex < 0)
        {
            if (delta > 0 && _items.Count > 0)
            {
                SetSelectedIndex(0);
                Focus();
            }
            return;
        }
        if (delta < 0 && _selectedIndex < ShellSearchMetrics.Columns)
        {
            _selectedIndex = -1;
            UpdateFocusVisual();
            _input.Focus();
            return;
        }
        SetSelectedIndex(Math.Clamp(
            _selectedIndex + (Math.Sign(delta) * ShellSearchMetrics.Columns),
            0,
            _items.Count - 1));
    }

    public void ActivateSelected()
    {
        if (SelectedItem is { } item)
        {
            ShellUiSounds.Play(UiSoundEvent.Enter);
            ItemActivated?.Invoke(this, new ShellSearchItemEventArgs(_selectedIndex, item));
        }
    }

    public void RequestClose()
    {
        ShellUiSounds.Play(UiSoundEvent.Cancel);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshFocusRect() => QueueFocusRect();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                MoveHorizontal(-1);
                e.Handled = true;
                break;
            case Key.Right:
                MoveHorizontal(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveVertical(-1);
                e.Handled = true;
                break;
            case Key.Down:
                MoveVertical(1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                ActivateSelected();
                e.Handled = true;
                break;
            case Key.Escape:
            case Key.Back:
                RequestClose();
                e.Handled = true;
                break;
            default:
                base.OnKeyDown(e);
                break;
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            MoveVertical(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            RequestClose();
            e.Handled = true;
        }
    }

    private void SetSelectedIndex(int index)
    {
        if (_items.Count == 0)
        {
            _selectedIndex = -1;
            return;
        }
        var next = Math.Clamp(index, 0, _items.Count - 1);
        if (next != _selectedIndex)
        {
            _selectedIndex = next;
            ShellUiSounds.Play(UiSoundEvent.FocusMove);
        }
        UpdateFocusVisual();
    }

    private void RebuildResults()
    {
        _results.Children.Clear();
        _tiles.Clear();
        for (var index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            var art = new Border
            {
                Width = ShellSearchMetrics.TileWidth,
                Height = ShellSearchMetrics.TileHeight,
                Background = TileBrush,
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Child = item.Image is null
                    ? new TextBlock
                    {
                        Text = Initials(item.Title),
                        FontSize = Ps5FontScale.Size2XLarge,
                        Foreground = Brushes.White,
                        Opacity = 0.7,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                    : new Image { Source = item.Image, Stretch = Stretch.UniformToFill },
            };
            var cell = new Canvas
            {
                Width = ShellSearchMetrics.TileWidth,
                Height = ShellSearchMetrics.RowHeight,
            };
            cell.Children.Add(art);
            var caption = new TextBlock
            {
                Text = item.Title,
                Width = ShellSearchMetrics.TileWidth,
                Height = ShellSearchMetrics.CaptionHeight,
                FontSize = Ps5FontScale.Size2XSmall,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = ShellSearchMetrics.CaptionLines,
                Margin = new Thickness(0, 4, 0, 0),
            };
            SetTop(caption, ShellSearchMetrics.TileHeight);
            cell.Children.Add(caption);
            var captured = index;
            art.PointerEntered += (_, _) =>
            {
                Focus();
                SetSelectedIndex(captured);
            };
            art.PointerReleased += (_, _) =>
            {
                SetSelectedIndex(captured);
                ActivateSelected();
            };
            var (x, _) = ShellSearchMetrics.TileOrigin(index);
            SetLeft(cell, x);
            SetTop(cell, (index / ShellSearchMetrics.Columns) * ShellSearchMetrics.RowHeight);
            _results.Children.Add(cell);
            _tiles.Add(art);
        }

        _emptyMessage.Text = string.IsNullOrWhiteSpace(Query)
            ? "No installed games"
            : $"No results found for “{Query}”";
        _emptyMessage.IsVisible = _items.Count == 0;
        UpdateFocusVisual();
    }

    private void UpdateFocusVisual()
    {
        for (var i = 0; i < _tiles.Count; i++)
        {
            _tiles[i].Opacity = i == _selectedIndex ? 1.0 : 0.92;
        }
        QueueFocusRect();
    }

    private void QueueFocusRect() =>
        Dispatcher.UIThread.Post(PushFocusRect, DispatcherPriority.Render);

    private void PushFocusRect()
    {
        try
        {
            if (ShellFocusRing.For(this) is not { } ring)
            {
                return;
            }
            if (!IsEffectivelyVisible || _selectedIndex < 0 || _selectedIndex >= _tiles.Count)
            {
                ring.Release(this);
                return;
            }
            var tile = _tiles[_selectedIndex];
            if (tile.TransformToVisual(ring) is not { } transform)
            {
                return;
            }
            ring.Radius = 8;
            ring.LineScale = 1;
            ring.Claim(this, new Rect(tile.Bounds.Size).TransformToAABB(transform));
        }
        catch
        {
        }
    }

    private static string Initials(string title)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0]))),
        };
    }
}
