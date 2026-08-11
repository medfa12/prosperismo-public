// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Prosperismo.GUI.Ps5Home;

namespace Prosperismo.GUI;

public sealed class GameEntry : INotifyPropertyChanged
{
    // Placeholder gradients for games without cover art, picked
    // deterministically from the game name so a game keeps its color.
    private static readonly (Color Start, Color End)[] PlaceholderPalette =
    {
        (Color.Parse("#5B4B8A"), Color.Parse("#2C2A4A")),
        (Color.Parse("#1F6E8C"), Color.Parse("#173B45")),
        (Color.Parse("#7A4069"), Color.Parse("#3B1C32")),
        (Color.Parse("#2D6A4F"), Color.Parse("#1B3A2B")),
        (Color.Parse("#8C5425"), Color.Parse("#4A2B12")),
        (Color.Parse("#4F6D9E"), Color.Parse("#263349")),
        (Color.Parse("#8A4B4B"), Color.Parse("#3F2222")),
        (Color.Parse("#3E7C7B"), Color.Parse("#1E3D3C")),
    };

    private Bitmap? _cover;
    private Bitmap? _titleLogo;
    private IBrush? _placeholderBrush;
    private long _sizeBytes;
    private readonly DesktopLauncherMetadataStore _metadataStore;
    private GameStatus _compatibilityStatus;
    private string _comment = string.Empty;

    public GameEntry(
        string name, string? titleId, string? version, string path, long sizeBytes,
        string? coverPath, string? backgroundPath, Ps5TitleLogoSource? titleLogoSource = null,
        DesktopLauncherMetadataStore? metadataStore = null, string? firmwareVersion = null)
    {
        Name = name;
        TitleId = titleId;
        Version = version;
        Path = path;
        _sizeBytes = sizeBytes;
        CoverPath = coverPath;
        BackgroundPath = backgroundPath;
        TitleLogoSource = titleLogoSource;
        Initials = ComputeInitials(name);

        _metadataStore = metadataStore ?? DesktopLauncherMetadataStore.Default;
        var metadata = _metadataStore.Load(TitleId, Path);
        FirmwareVersion = string.IsNullOrWhiteSpace(firmwareVersion)
            ? metadata.FirmwareVersion ?? TryReadFirmwareVersion(Path)
            : firmwareVersion.Trim();
        _compatibilityStatus = metadata.Status;
        _comment = metadata.Comment;
    }

    /// <summary>
    /// Constructor matching the Qt metadata order: game version followed by
    /// </summary>
    public GameEntry(
        string name, string? titleId, string? version, string? firmwareVersion, string path,
        long sizeBytes, string? coverPath, string? backgroundPath,
        Ps5TitleLogoSource? titleLogoSource = null, DesktopLauncherMetadataStore? metadataStore = null)
        : this(name, titleId, version, path, sizeBytes, coverPath, backgroundPath,
            titleLogoSource, metadataStore, firmwareVersion)
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string? TitleId { get; }

    /// <summary>Content version from sce_sys/param.json, e.g. "01.000.000".</summary>
    public string? Version { get; }

    /// <summary>Required system software version from sce_sys/param.json.</summary>
    public string? FirmwareVersion { get; }

    public string Path { get; }

    /// <summary>
    /// Total size of the game. Initially the eboot's own size from the scan;
    /// replaced with the full install folder size once computed in the
    /// background.
    /// </summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (_sizeBytes == value)
            {
                return;
            }

            _sizeBytes = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeBytes)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
        }
    }

    /// <summary>Path to the cover art image shipped with the game, if found.</summary>
    public string? CoverPath { get; }

    /// <summary>Path to the key art (pic0/pic1) shipped with the game, if found.</summary>
    public string? BackgroundPath { get; }

    /// <summary>
    /// Independently resolved title-logo channel. It must never be inferred
    /// from <see cref="CoverPath"/>, <see cref="BackgroundPath"/>, or title
    /// preview audio.
    /// </summary>
    public Ps5TitleLogoSource? TitleLogoSource { get; }

    /// <summary>
    /// Decoded key art used as the window backdrop while this game is
    /// selected. Loaded on demand and cached; not exposed via binding.
    /// </summary>
    public Bitmap? Background { get; set; }

    public string Initials { get; }

    // Built lazily: brushes are AvaloniaObjects that must be created on the
    // UI thread, while GameEntry itself is constructed on the scan thread.
    public IBrush PlaceholderBrush => _placeholderBrush ??= BuildPlaceholderBrush(Name);

    /// <summary>Decoded cover art; loaded asynchronously after the library scan.</summary>
    public Bitmap? Cover
    {
        get => _cover;
        set
        {
            if (ReferenceEquals(_cover, value))
            {
                return;
            }

            _cover = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCover)));
        }
    }

    public bool HasCover => _cover is not null;

    /// <summary>
    /// Decoded title logo, loaded only from a verified independent source. A
    /// null value deliberately leaves the title strip on its display-name
    /// fallback.
    /// </summary>
    public Bitmap? TitleLogo
    {
        get => _titleLogo;
        set
        {
            if (ReferenceEquals(_titleLogo, value))
            {
                return;
            }

            _titleLogo = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TitleLogo)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTitleLogo)));
        }
    }

    public bool HasTitleLogo => _titleLogo is not null;

    public bool HasTitleId => TitleId is not null;

    /// <summary>Badge text shown in the launch bar, e.g. "v01.000.000".</summary>
    public string? VersionText => Version is null ? null : $"v{Version}";

    public bool HasVersion => Version is not null;

    public bool HasFirmwareVersion => !string.IsNullOrWhiteSpace(FirmwareVersion);

    /// <summary>Local compatibility state, persisted like the Qt launcher.</summary>
    public GameStatus CompatibilityStatus
    {
        get => _compatibilityStatus;
        set
        {
            var normalized = Enum.IsDefined(value) ? value : GameStatus.Unknown;
            if (_compatibilityStatus == normalized)
            {
                return;
            }

            _compatibilityStatus = normalized;
            RaiseCompatibilityChanged();
            PersistMetadata();
        }
    }

    /// <summary>Short alias used by list/sort code that calls this field Status.</summary>
    public GameStatus Status
    {
        get => CompatibilityStatus;
        set => CompatibilityStatus = value;
    }

    public GameStatus GameStatus
    {
        get => CompatibilityStatus;
        set => CompatibilityStatus = value;
    }

    public int CompatibilityStatusIndex
    {
        get => CompatibilityStatus switch
        {
            GameStatus.MainMenu => 1,
            GameStatus.InGame => 2,
            GameStatus.Logo => 3,
            GameStatus.DoesntBoot => 4,
            _ => 0,
        };
        set => CompatibilityStatus = value switch
        {
            1 => GameStatus.MainMenu,
            2 => GameStatus.InGame,
            3 => GameStatus.Logo,
            4 => GameStatus.DoesntBoot,
            _ => GameStatus.Unknown,
        };
    }

    public string CompatibilityStatusText => GameStatusInfo.GetDisplayName(CompatibilityStatus);

    public string StatusText => CompatibilityStatusText;

    public string CompatibilityStatusColor => GameStatusInfo.GetColor(CompatibilityStatus);

    public string StatusColor => CompatibilityStatusColor;

    public GameStatusDisplay CompatibilityStatusDisplay => GameStatusInfo.GetDisplay(CompatibilityStatus);

    public GameStatusDisplay StatusDisplay => CompatibilityStatusDisplay;

    public DesktopLauncherMetadata Metadata => new()
    {
        TitleId = TitleId,
        GamePath = Path,
        FirmwareVersion = FirmwareVersion,
        Status = CompatibilityStatus,
        Comment = Comment,
    };

    /// <summary>Editable local compatibility comment.</summary>
    public string Comment
    {
        get => _comment;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(_comment, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _comment = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Comment)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Metadata)));
            PersistMetadata();
        }
    }

    /// <summary>Writes the current status/comment to the local metadata file.</summary>
    public void SaveMetadata() => PersistMetadata();

    /// <summary>Formatted install size badge shown in the launch bar.</summary>
    public string SizeText => FormatSize(SizeBytes);

    private static string ComputeInitials(string name)
    {
        var initials = name
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(word => char.IsLetterOrDigit(word[0]))
            .Select(word => char.ToUpperInvariant(word[0]))
            .Take(2)
            .ToArray();

        return initials.Length > 0 ? new string(initials) : "?";
    }

    private static IBrush BuildPlaceholderBrush(string name)
    {
        var hash = 0;
        foreach (var ch in name)
        {
            hash = unchecked(hash * 31 + ch);
        }

        var (start, end) = PlaceholderPalette[(int)((uint)hash % PlaceholderPalette.Length)];
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(start, 0),
                new GradientStop(end, 1),
            },
        };
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GiB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MiB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KiB",
            _ => $"{bytes} B",
        };
    }

    private static string? TryReadFirmwareVersion(string ebootPath)
    {
        try
        {
            var gameDirectory = System.IO.Path.GetDirectoryName(ebootPath);
            if (string.IsNullOrWhiteSpace(gameDirectory))
            {
                return null;
            }

            var paramPath = System.IO.Path.Combine(gameDirectory, "sce_sys", "param.json");
            if (!File.Exists(paramPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(paramPath));
            if (!document.RootElement.TryGetProperty("requiredSystemSoftwareVersion", out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var encoded = value.GetString()?.Trim();
            if (encoded is null || encoded.Length != 18 ||
                !(encoded.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) ||
                !encoded[2..8].All(char.IsDigit) ||
                !encoded[8..].All(IsHexDigit))
            {
                return null;
            }

            var digits = encoded[2..8];
            var major = int.Parse(digits[..2], System.Globalization.CultureInfo.InvariantCulture);
            var version = $"{major}.{digits[2..4]}";
            return digits[4..6] == "00" ? version : $"{version}.{digits[4..6]}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private void RaiseCompatibilityChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompatibilityStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GameStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompatibilityStatusIndex)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompatibilityStatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompatibilityStatusColor)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusColor)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompatibilityStatusDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Metadata)));
    }

    private void PersistMetadata()
    {
        try
        {
            _metadataStore.Save(TitleId, Path, new DesktopLauncherMetadata
            {
                FirmwareVersion = FirmwareVersion,
                Status = CompatibilityStatus,
                Comment = Comment,
            });
        }
        catch (Exception)
        {
            // A local metadata failure must not make the game library unusable.
        }
    }
}
