// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.Json;

namespace Prosperismo.HLE.Diagnostics;

/// <summary>How a value is rendered once its address is resolved.</summary>
public enum GuestDumpKind
{
    Hex,

    /// <summary>
    /// The resolved expression itself, with no memory read. This is what a
    /// register or a scalar argument wants — <c>arg0</c> holds a port handle,
    /// not the address of one.
    /// </summary>
    Value,

    UInt8,
    UInt16,
    UInt32,
    UInt64,
    Int32,
    Int64,
    Single,
    Double,
    Pointer,
    CString,
    StdString,

    /// <summary>A libc++ <c>std::vector</c>: begin/end/capacity at +0/+8/+16.</summary>
    Vector,

    /// <summary>An intrusive linked list walked through a next-pointer offset.</summary>
    List,

    /// <summary>A fixed number of equally-sized elements at an address.</summary>
    Array,

    /// <summary>Named fields at fixed offsets from a base address.</summary>
    Struct,
}

/// <summary>One field inside a struct/vector/list element.</summary>
public sealed class GuestDumpField
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Offset expression relative to the element base, e.g. "+0x10".</summary>
    public string At { get; init; } = "+0";

    public GuestDumpKind As { get; init; } = GuestDumpKind.UInt64;

    /// <summary>Byte length for <see cref="GuestDumpKind.Hex"/> / string caps.</summary>
    public int Length { get; init; } = 32;
}

/// <summary>One dump operation performed when a site fires.</summary>
public sealed class GuestDumpOp
{
    public string Label { get; init; } = string.Empty;

    /// <summary>Address expression; see <see cref="GuestAddressExpression"/>.</summary>
    public string At { get; init; } = string.Empty;

    public GuestDumpKind As { get; init; } = GuestDumpKind.Hex;

    /// <summary>Byte length for hex dumps and the cap for string reads.</summary>
    public int Length { get; init; } = 64;

    /// <summary>Element count for <see cref="GuestDumpKind.Array"/>.</summary>
    public int Count { get; init; } = 1;

    /// <summary>Element size for arrays, vectors and lists.</summary>
    public ulong Stride { get; init; } = 8;

    /// <summary>Upper bound on elements walked, so a corrupt list cannot spin.</summary>
    public int Max { get; init; } = 16;

    /// <summary>Offset of the next pointer within a list node.</summary>
    public ulong Next { get; init; }

    public IReadOnlyList<GuestDumpField> Fields { get; init; } = [];
}

/// <summary>A named point in execution at which a set of dumps runs.</summary>
public sealed class GuestProbeSite
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Stop firing after this many hits; keeps a per-frame site from flooding.</summary>
    public int MaxHits { get; init; } = 4;

    /// <summary>Fire on every Nth hit. 1 fires every time.</summary>
    public int EveryNth { get; init; } = 1;

    /// <summary>
    /// Extra names usable in this site's expressions, mapped to expressions
    /// themselves — e.g. <c>"sound": "r14"</c> names the register the object
    /// happens to live in, so the dumps read in terms of the object.
    /// </summary>
    public IReadOnlyDictionary<string, string> Anchors { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<GuestDumpOp> Dumps { get; init; } = [];
}

/// <summary>
/// A probe specification: what to dump, where. Loaded from JSON at startup so
/// asking a new question about guest state costs a text edit rather than a
/// rebuild-and-redeploy cycle.
/// </summary>
public sealed class GuestProbeSpec
{
    public IReadOnlyList<GuestProbeSite> Sites { get; init; } = [];

    /// <summary>Returns every site with the given name (case-insensitive).</summary>
    public IEnumerable<GuestProbeSite> SitesNamed(string name) =>
        Sites.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses a spec. Returns null and sets <paramref name="error"/> on malformed
    /// input rather than throwing, so a typo in a probe file degrades to "no
    /// probes plus one clear log line" instead of failing the boot.
    /// </summary>
    public static GuestProbeSpec? TryParse(string json, out string? error)
    {
        error = null;

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "root must be an object";
                return null;
            }

            var sites = new List<GuestProbeSite>();
            if (root.TryGetProperty("sites", out var sitesElement) &&
                sitesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var siteElement in sitesElement.EnumerateArray())
                {
                    sites.Add(ParseSite(siteElement));
                }
            }

            if (sites.Count == 0)
            {
                error = "spec declares no sites";
                return null;
            }

            return new GuestProbeSpec { Sites = sites };
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static GuestProbeSite ParseSite(JsonElement element)
    {
        var anchors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("anchors", out var anchorsElement) &&
            anchorsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var anchor in anchorsElement.EnumerateObject())
            {
                if (anchor.Value.ValueKind == JsonValueKind.String)
                {
                    anchors[anchor.Name] = anchor.Value.GetString() ?? string.Empty;
                }
            }
        }

        var dumps = new List<GuestDumpOp>();
        if (element.TryGetProperty("dumps", out var dumpsElement) &&
            dumpsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var dump in dumpsElement.EnumerateArray())
            {
                dumps.Add(ParseDump(dump));
            }
        }

        return new GuestProbeSite
        {
            Name = ReadString(element, "name") ?? string.Empty,
            MaxHits = (int)ReadNumber(element, "maxHits", 4),
            EveryNth = Math.Max(1, (int)ReadNumber(element, "everyNth", 1)),
            Anchors = anchors,
            Dumps = dumps,
        };
    }

    private static GuestDumpOp ParseDump(JsonElement element)
    {
        var fields = new List<GuestDumpField>();
        if (element.TryGetProperty("fields", out var fieldsElement) &&
            fieldsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in fieldsElement.EnumerateArray())
            {
                fields.Add(new GuestDumpField
                {
                    Name = ReadString(field, "name") ?? string.Empty,
                    At = ReadString(field, "at") ?? "+0",
                    As = ParseKind(ReadString(field, "as")),
                    Length = (int)ReadNumber(field, "len", 32),
                });
            }
        }

        return new GuestDumpOp
        {
            Label = ReadString(element, "label") ?? ReadString(element, "at") ?? string.Empty,
            At = ReadString(element, "at") ?? string.Empty,
            As = ParseKind(ReadString(element, "as")),
            Length = (int)ReadNumber(element, "len", 64),
            Count = (int)ReadNumber(element, "count", 1),
            Stride = ReadNumber(element, "stride", 8),
            Max = (int)ReadNumber(element, "max", 16),
            Next = ReadNumber(element, "next", 0),
            Fields = fields,
        };
    }

    private static GuestDumpKind ParseKind(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "u8" or "byte" or "uint8" => GuestDumpKind.UInt8,
        "u16" or "uint16" => GuestDumpKind.UInt16,
        "u32" or "uint32" => GuestDumpKind.UInt32,
        "u64" or "uint64" => GuestDumpKind.UInt64,
        "i32" or "int32" => GuestDumpKind.Int32,
        "i64" or "int64" => GuestDumpKind.Int64,
        "f32" or "float" => GuestDumpKind.Single,
        "f64" or "double" => GuestDumpKind.Double,
        "value" or "val" or "expr" or "reg" => GuestDumpKind.Value,
        "ptr" or "pointer" => GuestDumpKind.Pointer,
        "cstr" or "cstring" => GuestDumpKind.CString,
        "stdstring" or "std::string" or "string" => GuestDumpKind.StdString,
        "vector" or "std::vector" => GuestDumpKind.Vector,
        "list" => GuestDumpKind.List,
        "array" => GuestDumpKind.Array,
        "struct" => GuestDumpKind.Struct,
        _ => GuestDumpKind.Hex,
    };

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads a number that may be written as a JSON number or a hex string, since
    /// every offset in this domain is naturally hex.
    /// </summary>
    private static ulong ReadNumber(JsonElement element, string name, ulong fallback)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetUInt64(out var number) ? number : fallback;

            case JsonValueKind.String:
                var raw = value.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return fallback;
                }

                raw = raw.Trim();
                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return ulong.TryParse(
                        raw.AsSpan(2),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var hex)
                        ? hex
                        : fallback;
                }

                return ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var dec)
                    ? dec
                    : fallback;

            default:
                return fallback;
        }
    }
}
