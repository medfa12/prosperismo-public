// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace Prosperismo.GUI.SystemAssets.Rco;

/// <summary>
/// Thrown when a stream does not contain a well-formed RCOF v0x110 resource
/// container. This is the only exception <see cref="RcoContainer"/> raises for
/// malformed input: everything past the header is parsed defensively and edge
/// cases degrade to empty results rather than throwing.
/// </summary>
public sealed class RcoFormatException : Exception
{
    public RcoFormatException(string message) : base(message)
    {
    }
}

/// <summary>
/// Source of an entry's data offset: parsed from the container's node tree
/// (authoritative) or recovered by scanning the data blob for a known magic
/// (heuristic, used only as a fallback when the tree yields nothing).
/// </summary>
public enum RcoOffsetSource
{
    /// <summary>The offset and length came from a <c>src</c> attribute in the node tree.</summary>
    Tree,

    /// <summary>The offset came from a magic-byte scan and was paired to the nearest preceding name.</summary>
    MagicScan,
}

/// <summary>
/// One data-bearing leaf of an RCO container: a texture, sound or other asset
/// referenced by a <c>src</c>/<c>src_4k</c>/<c>src_lv1</c>/<c>src_lv2</c>
/// attribute in the node tree. <see cref="DataOffset"/>/<see cref="DataLength"/>
/// are absolute byte positions in the source file and are authoritative when
/// <see cref="Source"/> is <see cref="RcoOffsetSource.Tree"/>.
/// </summary>
/// <param name="Name">The owning element's <c>id</c> (e.g. "tex_bg_gold"); null when the tree carried none.</param>
/// <param name="TypeLabel">The declared content type from the tree (e.g. "texture/png", "texture/dds", "sound/vag"), or null.</param>
/// <param name="SrcLabel">The source attribute that pointed at the data (e.g. "src", "src_4k").</param>
/// <param name="DataOffset">Absolute offset of the payload in the file.</param>
/// <param name="DataLength">Payload length in bytes.</param>
/// <param name="Source">Whether the offset was parsed from the tree or recovered by a magic scan.</param>
public sealed record RcoEntry(
    string? Name,
    string? TypeLabel,
    string SrcLabel,
    long DataOffset,
    long DataLength,
    RcoOffsetSource Source);

/// <summary>
/// An embedded asset whose payload was identified by a standard-format magic.
/// </summary>
/// <param name="Name">The owning entry's name, or null.</param>
/// <param name="Kind">Detected payload kind: "png", "dds", "gnf", "svg", "vag", "json" or "bin".</param>
/// <param name="Offset">Absolute offset of the payload in the file.</param>
/// <param name="Length">Payload length in bytes.</param>
/// <param name="Source">Whether the offset came from the tree (authoritative) or a magic scan (heuristic).</param>
public sealed record RcoEmbeddedAsset(
    string? Name,
    string Kind,
    long Offset,
    long Length,
    RcoOffsetSource Source);

/// <summary>
/// 0x110), the unencrypted UI-asset packages used by the system shell. See
/// docs/rco-format.md for the byte-level layout this decoder relies on.
///
/// The container is a compiled document tree over a data blob. This reader
/// parses the header, the name and label string tables, and walks the tree to
/// enumerate the data-bearing leaves (textures, sounds, ...) with their name,
/// declared type and payload location. It never seeks blindly: every offset is
/// bounds-checked against the file length, and the whole file is held in memory
/// so <see cref="ReadEntryData"/> is a checked array copy.
/// </summary>
public sealed class RcoContainer
{
    /// <summary>Little-endian magic at offset 0: 'R','C','O','F'.</summary>
    public const uint Magic = 0x464F4352;

    /// <summary>The only container version this reader accepts.</summary>
    public const uint SupportedVersion = 0x110;

    // Fixed header layout (see docs/rco-format.md). The eight section
    // descriptors begin at 0x10; indices 0/2/7 are the name table, the label
    // table and the data blob respectively on every observed file.
    private const int HeaderSize = 0x50;
    private const int SectionTableOffset = 0x10;
    private const int NameSectionIndex = 0;
    private const int LabelSectionIndex = 2;
    private const int DataSectionIndex = 7;

    // Attribute type code that marks a two-word (offset, length) data reference.
    private const uint DataReferenceCode = 8;

    private readonly byte[] _data;
    private readonly (int Offset, int Size) _dataSection;

    private RcoContainer(byte[] data, uint version, (int, int) dataSection, IReadOnlyList<RcoEntry> entries)
    {
        _data = data;
        Version = version;
        _dataSection = dataSection;
        Entries = entries;
    }

    /// <summary>The parsed container version (always <see cref="SupportedVersion"/>).</summary>
    public uint Version { get; }

    /// <summary>The data-bearing leaves discovered in the node tree, in file order.</summary>
    public IReadOnlyList<RcoEntry> Entries { get; }

    /// <summary>
    /// Cheap sniff: true when the stream starts with the RCOF magic. Does not
    /// validate the version (use <see cref="Open"/>/<see cref="Read"/> for that)
    /// and restores the stream position when it can.
    /// </summary>
    public static bool LooksLikeRco(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long start = stream.CanSeek ? stream.Position : -1;
        try
        {
            Span<byte> header = stackalloc byte[4];
            int read = ReadFully(stream, header);
            return read == 4 && BinaryPrimitives_ReadUInt32LE(header) == Magic;
        }
        finally
        {
            if (start >= 0)
            {
                stream.Position = start;
            }
        }
    }

    /// <summary>Opens and fully parses an RCO file. Throws <see cref="RcoFormatException"/> on a bad header/version.</summary>
    public static RcoContainer Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new RcoFormatException($"cannot read '{path}': {exception.Message}");
        }

        return Parse(bytes);
    }

    /// <summary>Reads and fully parses an RCO container from a stream.</summary>
    public static RcoContainer Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Parse(memory.ToArray());
    }

    /// <summary>
    /// Returns a copy of the entry's payload bytes. The range is re-checked
    /// against the file, so a stale or hand-built entry can never read out of
    /// bounds; a range that does not fit yields an empty array.
    /// </summary>
    public byte[] ReadEntryData(RcoEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.DataOffset < 0 || entry.DataLength < 0 ||
            entry.DataOffset > _data.Length ||
            entry.DataLength > _data.Length - entry.DataOffset)
        {
            return Array.Empty<byte>();
        }

        var result = new byte[entry.DataLength];
        Array.Copy(_data, entry.DataOffset, result, 0, entry.DataLength);
        return result;
    }

    /// <summary>
    /// Classifies each entry's payload by its leading magic (PNG/DDS/GNF, plus
    /// SVG/VAG/JSON). When the tree produced no entries at all, falls back to
    /// scanning the data blob for PNG/DDS/GNF magics and pairing each hit to the
    /// nearest preceding name (<see cref="RcoOffsetSource.MagicScan"/>).
    /// </summary>
    public IReadOnlyList<RcoEmbeddedAsset> EnumerateEmbeddedAssets()
    {
        if (Entries.Count > 0)
        {
            var assets = new List<RcoEmbeddedAsset>(Entries.Count);
            foreach (var entry in Entries)
            {
                assets.Add(new RcoEmbeddedAsset(
                    entry.Name,
                    SniffKind(entry.DataOffset),
                    entry.DataOffset,
                    entry.DataLength,
                    entry.Source));
            }

            return assets;
        }

        return MagicScanAssets();
    }

    private static RcoContainer Parse(byte[] data)
    {
        if (data.Length < HeaderSize)
        {
            throw new RcoFormatException($"too small ({data.Length} bytes) to be an RCO container");
        }

        uint magic = ReadUInt32(data, 0);
        if (magic != Magic)
        {
            throw new RcoFormatException($"bad magic 0x{magic:x8} (expected 0x{Magic:x8} 'RCOF')");
        }

        uint version = ReadUInt32(data, 4);
        if (version != SupportedVersion)
        {
            throw new RcoFormatException($"unsupported RCO version 0x{version:x} (only 0x{SupportedVersion:x} is supported)");
        }

        int treeOffset = (int)Math.Min(ReadUInt32(data, 8), (uint)data.Length);
        var name = ReadSection(data, NameSectionIndex);
        var label = ReadSection(data, LabelSectionIndex);
        var dataSection = ReadSection(data, DataSectionIndex);

        // The node tree occupies everything between the fixed header and the
        // first string table. If the tables look implausible we still expose an
        // empty entry list rather than throwing.
        int treeEnd = name.Size > 0 ? name.Offset : data.Length;
        var entries = TryWalkTree(data, treeOffset, treeEnd, name, label, dataSection);

        return new RcoContainer(data, version, dataSection, entries);
    }

    private static (int Offset, int Size) ReadSection(byte[] data, int index)
    {
        int baseOffset = SectionTableOffset + index * 8;
        long offset = ReadUInt32(data, baseOffset);
        long size = ReadUInt32(data, baseOffset + 4);

        // Clamp to the file so a corrupt descriptor cannot produce an
        // out-of-range slice.
        if (offset < 0 || offset > data.Length)
        {
            return (0, 0);
        }

        if (size < 0 || size > data.Length - offset)
        {
            size = data.Length - offset;
        }

        return ((int)offset, (int)size);
    }

    private static IReadOnlyList<RcoEntry> TryWalkTree(
        byte[] data,
        int treeOffset,
        int treeEnd,
        (int Offset, int Size) name,
        (int Offset, int Size) label,
        (int Offset, int Size) dataSection)
    {
        if (treeOffset < HeaderSize || treeOffset >= treeEnd || treeEnd > data.Length)
        {
            return Array.Empty<RcoEntry>();
        }

        // Map every label-record start offset to its text. Only these exact
        // offsets are treated as labels, which rejects words that happen to
        // point into the middle of a label string.
        var labels = ReadStringTable(data, label.Offset, label.Size);

        // Collect id names and src data references in tree order, then bind each
        // src to the nearest following id (the id terminates its element; this
        // reproduces the ground-truth mapping in the reference files).
        var idPositions = new List<int>();
        var idNames = new List<string>();
        var srcPositions = new List<int>();
        var srcEntries = new List<RcoEntry>();

        for (int p = treeOffset; p + 4 <= treeEnd; p += 4)
        {
            uint word = ReadUInt32(data, p);
            if (!labels.TryGetValue((int)word, out var labelText))
            {
                continue;
            }

            if (labelText == "id")
            {
                // Attribute is [id][code][value]; the value is a name pointer.
                if (p + 12 <= treeEnd &&
                    TryReadName(data, name, ReadUInt32(data, p + 8), out var idName))
                {
                    idPositions.Add(p);
                    idNames.Add(idName);
                }
            }
            else if (labelText.StartsWith("src", StringComparison.Ordinal))
            {
                // [src][code=8][offset][length], offsets relative to the data blob.
                if (p + 16 <= treeEnd && ReadUInt32(data, p + 4) == DataReferenceCode)
                {
                    long rel = ReadUInt32(data, p + 8);
                    long len = ReadUInt32(data, p + 12);
                    if (len > 0 && rel < dataSection.Size && rel + len <= dataSection.Size)
                    {
                        srcPositions.Add(p);
                        srcEntries.Add(new RcoEntry(
                            Name: null,
                            TypeLabel: null,
                            SrcLabel: labelText,
                            DataOffset: dataSection.Offset + rel,
                            DataLength: len,
                            Source: RcoOffsetSource.Tree));
                    }
                }
            }
        }

        if (srcEntries.Count == 0)
        {
            return Array.Empty<RcoEntry>();
        }

        return BindNamesAndTypes(data, treeOffset, treeEnd, labels, idPositions, idNames, srcPositions, srcEntries);
    }

    private static IReadOnlyList<RcoEntry> BindNamesAndTypes(
        byte[] data,
        int treeOffset,
        int treeEnd,
        Dictionary<int, string> labels,
        List<int> idPositions,
        List<string> idNames,
        List<int> srcPositions,
        List<RcoEntry> srcEntries)
    {
        var result = new List<RcoEntry>(srcEntries.Count);
        for (int i = 0; i < srcEntries.Count; i++)
        {
            int position = srcPositions[i];
            string? name = NearestFollowingName(idPositions, idNames, position);
            string? type = NearestPrecedingContentType(data, treeOffset, treeEnd, labels, position);
            result.Add(srcEntries[i] with { Name = name, TypeLabel = type });
        }

        return result;
    }

    private static string? NearestFollowingName(List<int> idPositions, List<string> idNames, int position)
    {
        // idPositions is built in ascending order, so a linear lower-bound is
        // fine; the caller iterates entries in the same order.
        int lo = 0;
        int hi = idPositions.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (idPositions[mid] < position)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo < idNames.Count ? idNames[lo] : null;
    }

    private static string? NearestPrecedingContentType(
        byte[] data, int treeOffset, int treeEnd, Dictionary<int, string> labels, int position)
    {
        // The element's declared content type (e.g. "texture/png") is a label
        // word a short distance ahead of its src attribute. Scan backwards a
        // bounded window for the nearest label containing '/'.
        int limit = Math.Max(treeOffset, position - 0x200);
        for (int p = position; p >= limit; p -= 4)
        {
            if (p + 4 > treeEnd)
            {
                continue;
            }

            if (labels.TryGetValue((int)ReadUInt32(data, p), out var text) &&
                text.IndexOf('/') >= 0)
            {
                return text;
            }
        }

        return null;
    }

    private IReadOnlyList<RcoEmbeddedAsset> MagicScanAssets()
    {
        var assets = new List<RcoEmbeddedAsset>();
        int start = _dataSection.Offset;
        int end = _dataSection.Offset + _dataSection.Size;
        for (int p = start; p + 4 <= end; p++)
        {
            string? kind = MagicKind(_data, p);
            if (kind is null)
            {
                continue;
            }

            assets.Add(new RcoEmbeddedAsset(null, kind, p, 0, RcoOffsetSource.MagicScan));
        }

        return assets;
    }

    private string SniffKind(long offset) => MagicKind(_data, (int)offset) ?? "bin";

    private static string? MagicKind(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
        {
            return null;
        }

        if (Matches(data, offset, 0x89, 0x50, 0x4E, 0x47))
        {
            return "png";
        }

        if (Matches(data, offset, 0x44, 0x44, 0x53, 0x20))
        {
            return "dds";
        }

        if (Matches(data, offset, 0x47, 0x4E, 0x46, 0x20))
        {
            return "gnf";
        }

        byte b0 = data[offset];
        if (b0 == (byte)'<')
        {
            return "svg";
        }

        if (Matches(data, offset, 0x56, 0x41, 0x47, 0x70) || Matches(data, offset, 0x56, 0x41, 0x47, 0x31))
        {
            return "vag";
        }

        if (b0 == (byte)'{' || b0 == (byte)'[')
        {
            return "json";
        }

        return null;
    }

    private static bool Matches(byte[] data, int offset, byte b0, byte b1, byte b2, byte b3)
        => data[offset] == b0 && data[offset + 1] == b1 && data[offset + 2] == b2 && data[offset + 3] == b3;

    private static Dictionary<int, string> ReadStringTable(byte[] data, int offset, int size)
    {
        var table = new Dictionary<int, string>();
        int end = offset + size;
        int i = offset;
        while (i < end)
        {
            int stringEnd = Array.IndexOf(data, (byte)0, i, end - i);
            if (stringEnd < 0)
            {
                stringEnd = end;
            }

            if (stringEnd > i)
            {
                table[i - offset] = Encoding.ASCII.GetString(data, i, stringEnd - i);
            }

            i = stringEnd + 1;
        }

        return table;
    }

    private static bool TryReadName(byte[] data, (int Offset, int Size) name, uint rel, out string value)
    {
        value = string.Empty;

        // Name records are [u32 back-reference][NUL-terminated ASCII]; the
        // pointer addresses the record, so the string starts four bytes in.
        long stringStart = name.Offset + (long)rel + 4;
        if (rel < 0 || stringStart >= name.Offset + name.Size || stringStart >= data.Length)
        {
            return false;
        }

        int limit = name.Offset + name.Size;
        int end = Array.IndexOf(data, (byte)0, (int)stringStart, limit - (int)stringStart);
        if (end < 0)
        {
            end = limit;
        }

        if (end <= stringStart)
        {
            return false;
        }

        for (int i = (int)stringStart; i < end; i++)
        {
            if (data[i] < 0x20 || data[i] > 0x7E)
            {
                return false;
            }
        }

        value = Encoding.ASCII.GetString(data, (int)stringStart, end - (int)stringStart);
        return true;
    }

    private static uint ReadUInt32(byte[] data, int offset)
        => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static uint BinaryPrimitives_ReadUInt32LE(ReadOnlySpan<byte> span)
        => (uint)(span[0] | (span[1] << 8) | (span[2] << 16) | (span[3] << 24));

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer.Slice(total));
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
