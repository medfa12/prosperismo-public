// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using Prosperismo.GUI.Controls;

namespace Prosperismo.GUI;

public sealed class GuiSettings
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions =
        SettingsPersistence.CreateSerializerOptions();

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Canonical native-emulator defaults shared by the desktop and Big Picture
    /// presentations. These deliberately start with the original Kyty values.
    /// </summary>
    public EmulatorSettings GlobalEmulatorSettings { get; set; } = new();

    public List<string> GameFolders { get; set; } = new();

    /// <summary>Eboot paths hidden from the library via "Remove from library".</summary>
    public List<string> ExcludedGames { get; set; } = new();

    public string LogLevel { get; set; } = "Info";

    public int ImportTraceLimit { get; set; }

    public bool StrictDynlibResolution { get; set; }

    /// <summary>
    /// Mirror emulator output to user/logs/&lt;titleId&gt;-&lt;timestamp&gt;.log, if <see cref="LogFilePath"/> is null.
    /// </summary>
    public bool LogToFile { get; set; }

    /// <summary>If <see cref="LogToFile"/> is true it logs to this file path.</summary>
    public string? LogFilePath { get; set; }

    /// <summary>
    /// If <see cref="OverrideLogFile"/> is false it appends &lt;titleId&gt;-&lt;timestamp&gt; to the filename specified by
    /// <see cref="LogFilePath"/>. Otherwise it uses the exact filename from <see cref="LogFilePath"/>
    /// </summary>
    public bool OverrideLogFile { get; set; }

    /// <summary>Loop the selected game's sce_sys/snd0.at9 preview music.</summary>
    public bool PlayTitleMusic { get; set; } = true;

    /// <summary>Run the recovered native shell background phase and particle clocks.</summary>
    public bool AnimateShellBackground { get; set; } = true;

    /// <summary>Play navigation and selection sounds in the launcher.</summary>
    public bool PlayUiSounds { get; set; } = true;

    /// <summary>
    /// Loop the shell's ambient music bed on the home screen. Separate from
    /// <see cref="PlayUiSounds"/> on purpose: wanting navigation clicks and
    /// wanting background music are different preferences.
    /// </summary>
    public bool PlayShellMusic { get; set; } = true;

    /// <summary>Level for the ambient bed, 0 to 1.</summary>
    public double ShellMusicVolume { get; set; } = 0.65;

    /// <summary>Play the prism boot sequence before the shell appears.</summary>
    public bool PlayBootIntro { get; set; } = true;

    /// <summary>
    /// Latched the first time the boot sequence runs, so a fresh profile sees it
    /// once and every later launch goes straight to the shell. Clearing it (from
    /// the settings toggle) arms the intro again.
    /// </summary>
    public bool HasPlayedBootIntro { get; set; }

    /// <summary>
    /// Where the boot movie lives. No video ships with the emulator, so this is
    /// empty by default and the intro is resolved from the usual locations
    /// instead; when nothing is found the shell simply comes up with no intro.
    /// May name a file or a directory to search.
    /// </summary>
    public string? BootIntroVideoPath { get; set; }

    /// <summary>
    /// to the other vsh_asset audio. May name a file or a directory to search.
    /// </summary>
    public string? BootIntroAudioPath { get; set; }

    public string? EmulatorPath { get; set; }

    /// <summary>UI language, matching a file code under Languages/ (e.g. "en", "tr").</summary>
    public string Language { get; set; } = "en";

    /// <summary>Publish launcher/game status to Discord Rich Presence.</summary>
    public bool DiscordRichPresence { get; set; } = true;

    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>
    /// Last visible NPXS40003 function-control id. The console stores this
    /// value when Control Center unmounts and restores it on the next open.
    /// </summary>
    public string LastFocusedControlCenterId { get; set; } = "home";

    /// <summary>
    /// Local eboot paths in most-recent launch order. NPXS40003's App Switcher
    /// queries the console AppDB by lastPlayedDateOnConsole; this is the
    /// Prosperismo-owned equivalent and is never presented as PSN history.
    /// </summary>
    public List<string> RecentGamePaths { get; set; } = new();

    /// <summary>Names of PROSPERISMO_* switches set to "1" in the emulator's environment at launch.</summary>
    public List<string> EnvironmentToggles { get; set; } = new();

    /// <summary>Internal render resolution scale (1.0 = native, 0.5 = half).</summary>
    public double RenderResolutionScale { get; set; } = 1.0;

    /// <summary>
    /// Discord application ID used for Rich Presence; the default is the
    /// Prosperismo application. Override to rebrand what Discord shows as
    /// "Playing …" (register at discord.com/developers/applications).
    /// </summary>
    public string DiscordClientId { get; set; } = "1525606762248540221";

    // The emulator is portable and keeps its data next to the executable;
    // the GUI follows the same convention.
    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "gui-settings.json");

    /// <summary>
    /// Durable host-side projection of NPXS40003 notification list records.
    /// Kept separate from preferences so deleting history cannot reset emulator
    /// configuration and updating settings cannot invalidate list timestamps.
    /// </summary>
    public static string NotificationHistoryPath =>
        Path.Combine(AppContext.BaseDirectory, "notification-history.json");

    public static GuiSettings Load()
        => LoadFrom(SettingsPath);

    internal static GuiSettings LoadFrom(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return NormalizeFromJson(json);
            }
        }
        catch (Exception)
        {
            // Corrupt or unreadable settings fall back to defaults.
        }

        return new GuiSettings();
    }

    /// <summary>
    /// Deserializes settings and normalizes null references and null or empty list
    /// entries introduced by JSON. Empty scalar strings remain unchanged.
    /// </summary>
    internal static GuiSettings NormalizeFromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<GuiSettings>(json, SerializerOptions) ?? new GuiSettings();

        settings.GameFolders = FilterNullOrEmpty(settings.GameFolders);
        settings.ExcludedGames = FilterNullOrEmpty(settings.ExcludedGames);
        settings.EnvironmentToggles = FilterNullOrEmpty(settings.EnvironmentToggles);
        settings.RecentGamePaths = FilterNullOrEmpty(settings.RecentGamePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ShellAppSwitcherComposer.PersistedHistoryLimit)
            .ToList();
        settings.SchemaVersion = CurrentSchemaVersion;
        settings.GlobalEmulatorSettings =
            SettingsPersistence.NormalizeEmulatorSettings(settings.GlobalEmulatorSettings);
        settings.LogLevel ??= "Info";
        settings.Language ??= "en";
        settings.DiscordClientId ??= "1525606762248540221";
        settings.LastFocusedControlCenterId = string.IsNullOrWhiteSpace(
            settings.LastFocusedControlCenterId)
            ? "home"
            : settings.LastFocusedControlCenterId;
        if (settings.RenderResolutionScale <= 0 || settings.RenderResolutionScale > 2.0)
        {
            settings.RenderResolutionScale = 1.0;
        }

        return settings;
    }

    // JSON can populate non-nullable lists with null references and entries.
    private static List<string> FilterNullOrEmpty(List<string>? source)
    {
        if (source is null)
        {
            return [];
        }

        return source.Where(entry => !string.IsNullOrEmpty(entry)).ToList();
    }

    public void Save()
        => SaveTo(SettingsPath);

    internal void SaveTo(string path)
    {
        try
        {
            SchemaVersion = CurrentSchemaVersion;
            GlobalEmulatorSettings =
                SettingsPersistence.NormalizeEmulatorSettings(GlobalEmulatorSettings);
            SettingsPersistence.WriteAtomically(path, this, SerializerOptions);
        }
        catch (Exception)
        {
            // Settings persistence is best-effort.
        }
    }
}
