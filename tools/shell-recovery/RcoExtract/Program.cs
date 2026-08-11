// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text;
using Prosperismo.GUI.SystemAssets.Audio;
using Prosperismo.GUI.SystemAssets.Rco;

if (args.Length == 2 && args[0] == "summary")
{
    var summary = RcoContainer.Open(args[1]);
    var kinds = summary.EnumerateEmbeddedAssets()
        .GroupBy(asset => asset.Kind)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => $"{group.Key}={group.Count()}");
    Console.WriteLine(
        $"version=0x{summary.Version:X} entries={summary.Entries.Count} " +
        string.Join(" ", kinds));
    return 0;
}

if (args.Length < 3 || args[0] is not ("list" or "get" or "get-wav"))
{
    Console.Error.WriteLine(
        "usage:\n" +
        "  RcoExtract summary <container.rco>\n" +
        "  RcoExtract list <container.rco> <entry-id>\n" +
        "  RcoExtract get     <container.rco> <entry-id> <output> [src-label]\n" +
        "  RcoExtract get-wav <container.rco> <entry-id> <output.wav> [src-label]");
    return 2;
}

var command = args[0];
var path = args[1];
var name = args[2];
var container = RcoContainer.Open(path);
var entries = container.Entries
    .Where(entry => string.Equals(entry.Name, name, StringComparison.Ordinal))
    .ToArray();

if (entries.Length == 0)
{
    Console.Error.WriteLine($"entry not found: {name}");
    return 3;
}

if (command == "list")
{
    foreach (var entry in entries)
    {
        var payload = container.ReadEntryData(entry);
        Console.WriteLine(
            $"{entry.Name} {entry.SrcLabel} {entry.TypeLabel ?? "-"} " +
            $"offset=0x{entry.DataOffset:X} size={entry.DataLength} " +
            $"sha256={Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()}");
    }
    return 0;
}

if (args.Length < 4)
{
    Console.Error.WriteLine("get requires an output path");
    return 2;
}

var requestedLabel = args.Length >= 5 ? args[4] : null;
RcoEntry? selected;
if (requestedLabel is not null)
{
    selected = entries.FirstOrDefault(entry => string.Equals(
        entry.SrcLabel, requestedLabel, StringComparison.Ordinal));
}
else
{
    selected = entries.FirstOrDefault(entry => entry.SrcLabel == "src_4k")
               ?? entries.FirstOrDefault(entry => entry.SrcLabel == "src")
               ?? entries.MaxBy(entry => entry.DataLength);
}

if (selected is null)
{
    Console.Error.WriteLine(
        $"entry {name} has no payload labelled {requestedLabel ?? "src/src_4k"}");
    return 4;
}

var data = container.ReadEntryData(selected);
var output = Path.GetFullPath(args[3]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
if (command == "get-wav")
{
    var clip = VagDecoder.TryDecode(data);
    if (clip is null)
    {
        Console.Error.WriteLine($"entry {name} is not a decodable VAG stream");
        return 5;
    }

    data = EncodeStereoPcmWave(clip);
}

File.WriteAllBytes(output, data);
Console.WriteLine(
    $"{name} {selected.SrcLabel} -> {output} ({data.Length} bytes, " +
    $"sha256 {Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()})");
return 0;

static byte[] EncodeStereoPcmWave(VagClip source)
{
    const int targetRate = 48_000;
    const int targetChannels = 2;
    var sourceFrames = source.Samples.Length / source.Channels;
    var targetFrames = Math.Max(1, (int)Math.Round(
        sourceFrames * (double)targetRate / source.SampleRate));
    var pcm = new short[targetFrames * targetChannels];
    var step = (double)sourceFrames / targetFrames;
    for (var frame = 0; frame < targetFrames; frame++)
    {
        var position = frame * step;
        var index = Math.Min((int)position, sourceFrames - 1);
        var next = Math.Min(index + 1, sourceFrames - 1);
        var fraction = position - index;
        for (var channel = 0; channel < targetChannels; channel++)
        {
            var sourceChannel = source.Channels == 1 ? 0 : Math.Min(channel, source.Channels - 1);
            var a = source.Samples[(index * source.Channels) + sourceChannel];
            var b = source.Samples[(next * source.Channels) + sourceChannel];
            pcm[(frame * targetChannels) + channel] = (short)Math.Clamp(
                Math.Round(a + ((b - a) * fraction)), short.MinValue, short.MaxValue);
        }
    }

    var dataBytes = checked(pcm.Length * sizeof(short));
    var wav = new byte[44 + dataBytes];
    var span = wav.AsSpan();
    Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
    BitConverter.TryWriteBytes(span[4..], 36 + dataBytes);
    Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(span[8..]);
    BitConverter.TryWriteBytes(span[16..], 16);
    BitConverter.TryWriteBytes(span[20..], (ushort)1);
    BitConverter.TryWriteBytes(span[22..], (ushort)targetChannels);
    BitConverter.TryWriteBytes(span[24..], targetRate);
    BitConverter.TryWriteBytes(span[28..], targetRate * targetChannels * sizeof(short));
    BitConverter.TryWriteBytes(span[32..], (ushort)(targetChannels * sizeof(short)));
    BitConverter.TryWriteBytes(span[34..], (ushort)16);
    Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
    BitConverter.TryWriteBytes(span[40..], dataBytes);
    for (var index = 0; index < pcm.Length; index++)
    {
        BitConverter.TryWriteBytes(span[(44 + (index * sizeof(short)))..], pcm[index]);
    }
    return wav;
}
