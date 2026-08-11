// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Prosperismo.GUI;

/// <summary>Avalonia translation of Kyty's read-only Trophy*.ucp viewer.</summary>
internal sealed class DesktopTrophyViewerDialog : Window
{
    private const uint UcpMagic = 0xb228c60a;
    private const uint UcpVersion = 1;
    private const int UcpHeaderLength = 0x40;
    private const int UcpTocSkip = 0x20;
    private const int UcpEntryLength = 0x40;
    private const int UcpNameLength = 0x20;

    private readonly List<Bitmap> _ownedImages = [];

    private DesktopTrophyViewerDialog(string gameName, IReadOnlyList<TrophySet> sets)
    {
        Title = $"Trophy Viewer — {gameName}";
        Classes.Add("psDesktopDialog");
        Width = 1000;
        Height = 640;
        MinWidth = 720;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FFF5F7FA"));

        var tabs = new TabControl();
        tabs.ItemsSource = sets.Select(set => new TabItem
        {
            Header = set.Title,
            Content = BuildSet(set),
        }).ToArray();

        var close = new Button
        {
            Content = "Close",
            Classes = { "ghost" },
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10),
        };
        close.Click += (_, _) => Close();

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(tabs);
        Grid.SetRow(close, 1);
        root.Children.Add(close);
        Content = root;
        Closed += (_, _) =>
        {
            foreach (var image in _ownedImages)
            {
                image.Dispose();
            }
        };
    }

    public static bool HasTrophyData(GameEntry game) => FindTrophyFiles(game.Path).Count > 0;

    public static DesktopTrophyViewerDialog? TryCreate(GameEntry game, out string? error)
    {
        error = null;
        var sets = new List<TrophySet>();
        var errors = new List<string>();
        foreach (var path in FindTrophyFiles(game.Path))
        {
            try
            {
                sets.Add(ReadSet(path));
            }
            catch (Exception exception)
            {
                errors.Add(exception.Message);
            }
        }

        if (sets.Count == 0)
        {
            error = errors.Count == 0
                ? "No trophy package found in sce_sys/trophy2."
                : string.Join(Environment.NewLine, errors);
            return null;
        }

        return new DesktopTrophyViewerDialog(game.Name, sets);
    }

    private Control BuildSet(TrophySet set)
    {
        var rows = new StackPanel { Spacing = 1 };
        rows.Children.Add(BuildHeader());
        foreach (var trophy in set.Trophies)
        {
            rows.Children.Add(BuildRow(trophy));
        }
        return new ScrollViewer { Content = rows };
    }

    private static Control BuildHeader() => new Border
    {
        Background = new SolidColorBrush(Color.Parse("#FFEBEEF0")),
        Padding = new Thickness(8, 6),
        Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("90,110,220,*"),
            Children =
            {
                HeaderText("Unlocked", 0),
                HeaderText("Trophy", 1),
                HeaderText("Name", 2),
                HeaderText("Description", 3),
            },
        },
    };

    private static TextBlock HeaderText(string text, int column)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(block, column);
        return block;
    }

    private Control BuildRow(TrophyRow trophy)
    {
        Bitmap? icon = null;
        if (trophy.Icon is { Length: > 0 })
        {
            try
            {
                icon = new Bitmap(new MemoryStream(trophy.Icon));
                _ownedImages.Add(icon);
            }
            catch (Exception)
            {
                icon = null;
            }
        }

        var name = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = trophy.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = GradeText(trophy.Grade),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#FF5B6573")),
                },
            },
        };
        var description = new TextBlock
        {
            Text = trophy.Detail,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var status = new TextBlock
        {
            Text = "locked",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = trophy.Hidden
                ? new SolidColorBrush(Color.Parse("#FF8A8F98"))
                : new SolidColorBrush(Color.Parse("#FF5B6573")),
        };
        var image = new Image
        {
            Source = icon,
            Width = 96,
            Height = 96,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("90,110,220,*"),
            MinHeight = 112,
            Children = { status, image, name, description },
        };
        Grid.SetColumn(image, 1);
        Grid.SetColumn(name, 2);
        Grid.SetColumn(description, 3);
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FFFFFFFF")),
            Padding = new Thickness(8),
            Child = grid,
        };
    }

    private static IReadOnlyList<string> FindTrophyFiles(string ebootPath)
    {
        var baseDirectory = Path.GetDirectoryName(ebootPath);
        var trophyDirectory = baseDirectory is null
            ? null
            : Path.Combine(baseDirectory, "sce_sys", "trophy2");
        if (trophyDirectory is null || !Directory.Exists(trophyDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(trophyDirectory, "*.ucp", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).StartsWith("trophy", StringComparison.OrdinalIgnoreCase))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static TrophySet ReadSet(string path)
    {
        var files = ReadUcp(path);
        if (!files.TryGetValue("tropconf.json", out var configurationBytes))
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} does not contain tropconf.json.");
        }

        using var configuration = JsonDocument.Parse(configurationBytes);
        var defaultLanguage = JsonString(configuration.RootElement, "defaultLanguage");
        var metadataName = string.IsNullOrWhiteSpace(defaultLanguage)
            ? null
            : $"tropmeta_{defaultLanguage}.json";
        byte[]? metadataBytes = null;
        if (metadataName is not null)
        {
            files.TryGetValue(metadataName, out metadataBytes);
        }
        metadataBytes ??= files
            .Where(pair => pair.Key.StartsWith("tropmeta_", StringComparison.OrdinalIgnoreCase) &&
                           pair.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault();
        if (metadataBytes is null)
        {
            files.TryGetValue("tropmeta.json", out metadataBytes);
        }
        if (metadataBytes is null)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} has no readable trophy metadata.");
        }

        using var metadata = JsonDocument.Parse(metadataBytes);
        var texts = ReadTexts(metadata.RootElement);
        var trophies = new List<TrophyRow>();
        if (configuration.RootElement.TryGetProperty("trophies", out var definitions) &&
            definitions.ValueKind == JsonValueKind.Array)
        {
            foreach (var definition in definitions.EnumerateArray())
            {
                var id = JsonValueText(definition, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }
                texts.TryGetValue(id, out var text);
                var hidden = definition.TryGetProperty("hidden", out var hiddenElement) &&
                    hiddenElement.ValueKind == JsonValueKind.True;
                trophies.Add(new TrophyRow(
                    id,
                    string.IsNullOrWhiteSpace(text.Name)
                        ? hidden ? "Hidden Trophy" : $"Trophy {id}"
                        : text.Name,
                    string.IsNullOrWhiteSpace(text.Detail) && hidden ? "This trophy is hidden." : text.Detail,
                    JsonValueText(definition, "grade"),
                    hidden,
                    FindIcon(files, id)));
            }
        }

        if (trophies.Count == 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} does not define any trophies.");
        }
        return new TrophySet(Path.GetFileNameWithoutExtension(path), trophies);
    }

    private static Dictionary<string, TrophyText> ReadTexts(JsonElement root)
    {
        var result = new Dictionary<string, TrophyText>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("trophyMetadata", out var trophies) ||
            trophies.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        foreach (var trophy in trophies.EnumerateArray())
        {
            var id = JsonValueText(trophy, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                result[id] = new TrophyText(
                    JsonValueText(trophy, "name"),
                    JsonValueText(trophy, "detail"));
            }
        }
        return result;
    }

    private static Dictionary<string, byte[]> ReadUcp(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < UcpHeaderLength || ReadBe32(data, 0) != UcpMagic)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a valid trophy package.");
        }
        if (ReadBe32(data, 4) != UcpVersion)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} uses an unsupported trophy package version.");
        }

        var declaredSize = ReadBe64(data, 8);
        var fileCount = ReadBe32(data, 0x10);
        var tocOffset = ReadBe32(data, 0x14);
        var tableEnd = (ulong)tocOffset + UcpTocSkip + ((ulong)fileCount * UcpEntryLength);
        if (declaredSize > (ulong)data.Length || tableEnd > (ulong)data.Length)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is truncated.");
        }

        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0u; index < fileCount; index++)
        {
            var entry = checked((int)((ulong)tocOffset + UcpTocSkip + ((ulong)index * UcpEntryLength)));
            var name = ReadFixedString(data, entry, UcpNameLength).Trim();
            var offset = ReadBe64(data, entry + 0x20);
            var size = ReadBe64(data, entry + 0x28);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            if (offset > (ulong)data.Length || size > (ulong)data.Length - offset ||
                offset > int.MaxValue || size > int.MaxValue)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} has an invalid {name} entry.");
            }
            result[name] = data.AsSpan((int)offset, (int)size).ToArray();
        }
        return result;
    }

    private static byte[]? FindIcon(IReadOnlyDictionary<string, byte[]> files, string id)
    {
        if (files.TryGetValue($"trop{id}.png", out var icon))
        {
            return icon;
        }
        return int.TryParse(id, out var number) && files.TryGetValue($"trop{number:0000}.png", out icon)
            ? icon
            : null;
    }

    private static string JsonString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? JsonElementText(value) : string.Empty;

    private static string JsonValueText(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? JsonElementText(value) : string.Empty;

    private static string JsonElementText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => string.Empty,
    };

    private static uint ReadBe32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, sizeof(uint)));

    private static ulong ReadBe64(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, sizeof(ulong)));

    private static string ReadFixedString(byte[] data, int offset, int maximumLength)
    {
        var length = Array.IndexOf(data, (byte)0, offset, maximumLength);
        if (length < 0)
        {
            length = offset + maximumLength;
        }
        return Encoding.Latin1.GetString(data, offset, length - offset);
    }

    private static string GradeText(string grade) => grade.ToUpperInvariant() switch
    {
        "P" => "Platinum",
        "G" => "Gold",
        "S" => "Silver",
        "B" => "Bronze",
        _ => grade,
    };

    private sealed record TrophySet(string Title, IReadOnlyList<TrophyRow> Trophies);
    private sealed record TrophyRow(string Id, string Name, string Detail, string Grade, bool Hidden, byte[]? Icon);
    private readonly record struct TrophyText(string Name, string Detail);
}
