// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prosperismo.GUI;

internal static class SettingsPersistence
{
    internal static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new EmulatorSettingsJsonConverter() },
    };

    internal static EmulatorSettings NormalizeEmulatorSettings(EmulatorSettings? source)
    {
        var defaults = new EmulatorSettings();
        if (source is null)
        {
            return defaults;
        }

        return new EmulatorSettings
        {
            ScreenResolution = Enum.IsDefined(source.ScreenResolution)
                ? source.ScreenResolution
                : defaults.ScreenResolution,
            VblankFrequency = source.VblankFrequency is >= EmulatorSettingsContract.MinimumVblankFrequency
                and <= EmulatorSettingsContract.MaximumVblankFrequency
                    ? source.VblankFrequency
                    : defaults.VblankFrequency,
            VulkanValidation = source.VulkanValidation,
            ShaderValidation = source.ShaderValidation,
            ShaderOptimization = Enum.IsDefined(source.ShaderOptimization)
                ? source.ShaderOptimization
                : defaults.ShaderOptimization,
            ShaderLogDirection = Enum.IsDefined(source.ShaderLogDirection)
                ? source.ShaderLogDirection
                : defaults.ShaderLogDirection,
            ShaderLogFolder = NormalizeRequiredPath(source.ShaderLogFolder, defaults.ShaderLogFolder),
            CommandBufferDump = source.CommandBufferDump,
            CommandBufferDumpFolder = NormalizeRequiredPath(
                source.CommandBufferDumpFolder,
                defaults.CommandBufferDumpFolder),
            PrintfDirection = Enum.IsDefined(source.PrintfDirection)
                ? source.PrintfDirection
                : defaults.PrintfDirection,
            PrintfOutputFile = NormalizeRequiredPath(source.PrintfOutputFile, defaults.PrintfOutputFile),
            ProfilerDirection = Enum.IsDefined(source.ProfilerDirection)
                ? source.ProfilerDirection
                : defaults.ProfilerDirection,
            RenderDoc = source.RenderDoc,
            NggRectlistDraw = source.NggRectlistDraw,
        };
    }

    internal static void WriteAtomically<T>(string path, T value, JsonSerializerOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("A settings path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, options);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                try
                {
                    File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temporaryPath, fullPath, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string NormalizeRequiredPath(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private sealed class EmulatorSettingsJsonConverter : JsonConverter<EmulatorSettings>
    {
        public override EmulatorSettings Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return new EmulatorSettings();
            }

            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new EmulatorSettings();
            }

            var root = document.RootElement;
            var defaults = new EmulatorSettings();
            var settings = new EmulatorSettings
            {
                ScreenResolution = ReadResolution(root, nameof(EmulatorSettings.ScreenResolution), defaults.ScreenResolution),
                VblankFrequency = ReadInt(root, nameof(EmulatorSettings.VblankFrequency), defaults.VblankFrequency),
                VulkanValidation = ReadBool(root, nameof(EmulatorSettings.VulkanValidation), defaults.VulkanValidation),
                ShaderValidation = ReadBool(root, nameof(EmulatorSettings.ShaderValidation), defaults.ShaderValidation),
                ShaderOptimization = ReadEnum(root, nameof(EmulatorSettings.ShaderOptimization), defaults.ShaderOptimization),
                ShaderLogDirection = ReadEnum(root, nameof(EmulatorSettings.ShaderLogDirection), defaults.ShaderLogDirection),
                ShaderLogFolder = ReadString(root, nameof(EmulatorSettings.ShaderLogFolder), defaults.ShaderLogFolder),
                CommandBufferDump = ReadBool(root, nameof(EmulatorSettings.CommandBufferDump), defaults.CommandBufferDump),
                CommandBufferDumpFolder = ReadString(
                    root,
                    nameof(EmulatorSettings.CommandBufferDumpFolder),
                    defaults.CommandBufferDumpFolder),
                PrintfDirection = ReadEnum(root, nameof(EmulatorSettings.PrintfDirection), defaults.PrintfDirection),
                PrintfOutputFile = ReadString(root, nameof(EmulatorSettings.PrintfOutputFile), defaults.PrintfOutputFile),
                ProfilerDirection = ReadEnum(root, nameof(EmulatorSettings.ProfilerDirection), defaults.ProfilerDirection),
                RenderDoc = ReadBool(root, nameof(EmulatorSettings.RenderDoc), defaults.RenderDoc),
                NggRectlistDraw = ReadBool(root, nameof(EmulatorSettings.NggRectlistDraw), defaults.NggRectlistDraw),
            };

            return NormalizeEmulatorSettings(settings);
        }

        public override void Write(
            Utf8JsonWriter writer,
            EmulatorSettings value,
            JsonSerializerOptions options)
        {
            var settings = NormalizeEmulatorSettings(value);
            writer.WriteStartObject();
            writer.WriteString(nameof(EmulatorSettings.ScreenResolution), ResolutionName(settings.ScreenResolution));
            writer.WriteNumber(nameof(EmulatorSettings.VblankFrequency), settings.VblankFrequency);
            writer.WriteBoolean(nameof(EmulatorSettings.VulkanValidation), settings.VulkanValidation);
            writer.WriteBoolean(nameof(EmulatorSettings.ShaderValidation), settings.ShaderValidation);
            writer.WriteString(nameof(EmulatorSettings.ShaderOptimization), settings.ShaderOptimization.ToString());
            writer.WriteString(nameof(EmulatorSettings.ShaderLogDirection), settings.ShaderLogDirection.ToString());
            writer.WriteString(nameof(EmulatorSettings.ShaderLogFolder), settings.ShaderLogFolder);
            writer.WriteBoolean(nameof(EmulatorSettings.CommandBufferDump), settings.CommandBufferDump);
            writer.WriteString(nameof(EmulatorSettings.CommandBufferDumpFolder), settings.CommandBufferDumpFolder);
            writer.WriteString(nameof(EmulatorSettings.PrintfDirection), settings.PrintfDirection.ToString());
            writer.WriteString(nameof(EmulatorSettings.PrintfOutputFile), settings.PrintfOutputFile);
            writer.WriteString(nameof(EmulatorSettings.ProfilerDirection), settings.ProfilerDirection.ToString());
            writer.WriteBoolean(nameof(EmulatorSettings.RenderDoc), settings.RenderDoc);
            writer.WriteBoolean(nameof(EmulatorSettings.NggRectlistDraw), settings.NggRectlistDraw);
            writer.WriteEndObject();
        }

        private static EmulatorResolution ReadResolution(
            JsonElement root,
            string propertyName,
            EmulatorResolution fallback)
        {
            if (!TryGetProperty(root, propertyName, out var element))
            {
                return fallback;
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                return element.GetString()?.Trim().ToUpperInvariant() switch
                {
                    "1280X720" or "R1280X720" => EmulatorResolution.R1280X720,
                    "1920X1080" or "R1920X1080" => EmulatorResolution.R1920X1080,
                    _ => fallback,
                };
            }

            return ReadEnumValue(element, fallback);
        }

        private static TEnum ReadEnum<TEnum>(JsonElement root, string propertyName, TEnum fallback)
            where TEnum : struct, Enum =>
            TryGetProperty(root, propertyName, out var element)
                ? ReadEnumValue(element, fallback)
                : fallback;

        private static TEnum ReadEnumValue<TEnum>(JsonElement element, TEnum fallback)
            where TEnum : struct, Enum
        {
            if (element.ValueKind == JsonValueKind.String &&
                Enum.TryParse<TEnum>(element.GetString(), ignoreCase: true, out var parsed) &&
                Enum.IsDefined(parsed))
            {
                return parsed;
            }

            if (element.ValueKind == JsonValueKind.Number &&
                element.TryGetInt32(out var number) &&
                Enum.IsDefined(typeof(TEnum), number))
            {
                return (TEnum)Enum.ToObject(typeof(TEnum), number);
            }

            return fallback;
        }

        private static int ReadInt(JsonElement root, string propertyName, int fallback) =>
            TryGetProperty(root, propertyName, out var element) && element.TryGetInt32(out var value)
                ? value
                : fallback;

        private static bool ReadBool(JsonElement root, string propertyName, bool fallback) =>
            TryGetProperty(root, propertyName, out var element) &&
            (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ? element.GetBoolean()
                : fallback;

        private static string ReadString(JsonElement root, string propertyName, string fallback) =>
            TryGetProperty(root, propertyName, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? fallback
                : fallback;

        private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static string ResolutionName(EmulatorResolution resolution) => resolution switch
        {
            EmulatorResolution.R1920X1080 => "1920x1080",
            _ => "1280x720",
        };
    }
}
