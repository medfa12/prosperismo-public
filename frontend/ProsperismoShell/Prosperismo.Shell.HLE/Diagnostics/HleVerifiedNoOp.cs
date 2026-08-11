// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace Prosperismo.HLE.Diagnostics;

/// <summary>
/// that claim. The evidence string is mandatory: a mark without it is
/// indistinguishable from an opinion, and the whole point of the census is to
/// stop opinions from being counted as knowledge.
/// </summary>
/// <param name="Nid">The export's NID, without the <c>#lib#mod</c> suffix.</param>
/// <param name="Name">Human-readable name, for the report only.</param>
/// <param name="Evidence">Where the claim comes from and what was observed.</param>
public sealed record HleVerifiedNoOp(string Nid, string Name, string Evidence);

/// <summary>
/// effect census can separate "correctly does nothing" from "does nothing and
/// should not".
///
/// <para>Built-in entries are ones whose bytes were read out of the 4.03
/// <c>PROSPERISMO_HLE_VERIFIED_NOOPS=&lt;path&gt;</c>, a text file of
/// <c>nid | name | evidence</c> lines (<c>#</c> starts a comment). Lines with no
/// evidence are rejected and the rejection is printed in the census report, so a
/// silently ignored line cannot be mistaken for an accepted one.</para>
/// </summary>
public static class HleVerifiedNoOps
{
    private const string FileVariable = "PROSPERISMO_HLE_VERIFIED_NOOPS";

    private static readonly Lock Gate = new();

    private static readonly ConcurrentDictionary<string, HleVerifiedNoOp> Known =
        new(StringComparer.Ordinal);

    private static readonly List<string> Diagnostics = [];

    private static bool _loaded;

    /// <summary>
    /// Every verified no-op currently known, built-ins plus anything loaded from
    /// the environment.
    /// </summary>
    public static IReadOnlyCollection<HleVerifiedNoOp> All
    {
        get
        {
            EnsureLoaded();
            return Known.Values.ToArray();
        }
    }

    /// <summary>
    /// Messages produced while loading — rejected lines, unreadable files. The
    /// census prints these so a malformed annotation file is visible in the same
    /// place its effect would have been.
    /// </summary>
    public static IReadOnlyList<string> LoadDiagnostics
    {
        get
        {
            EnsureLoaded();
            lock (Gate)
            {
                return Diagnostics.ToArray();
            }
        }
    }

    /// <summary>Looks up a NID. Returns false for anything not verified.</summary>
    public static bool TryGet(string? nid, out HleVerifiedNoOp entry)
    {
        EnsureLoaded();

        if (string.IsNullOrWhiteSpace(nid))
        {
            entry = null!;
            return false;
        }

        return Known.TryGetValue(nid.Trim(), out entry!);
    }

    /// <summary>
    /// missing — an unevidenced mark would let a real stub hide in the section of
    /// the report reserved for functions that are supposed to do nothing.
    /// </summary>
    public static void Register(string nid, string name, string evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nid);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);

        EnsureLoaded();
        Known[nid.Trim()] = new HleVerifiedNoOp(
            nid.Trim(),
            string.IsNullOrWhiteSpace(name) ? nid.Trim() : name.Trim(),
            evidence.Trim());
    }

    /// <summary>Drops everything and re-seeds the built-ins on next use. For tests.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Known.Clear();
            Diagnostics.Clear();
            _loaded = false;
        }
    }

    private static void EnsureLoaded()
    {
        if (Volatile.Read(ref _loaded))
        {
            return;
        }

        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            SeedBuiltIns();
            LoadFromEnvironment();
        }
    }

    private static void SeedBuiltIns()
    {
        // EXTRACTED, read out of games/PS5_4.03_reconstructed by parsing the module's
        // program headers to map the symbol's vaddr to a file offset and reading the
        // byte there. Both symbols are one byte long and that byte is 0xC3 (ret):
        // there is no body to port, so an HLE export that does nothing here matches
        // hardware exactly.
        Known["bzQExy189ZI"] = new HleVerifiedNoOp(
            "bzQExy189ZI",
            "_init_env",
            "libSceLibcInternal.sprx dynsym bzQExy189ZI#C#A: st_value 0xD4DB0, st_size 1, " +
            "byte at 0xD4DB0 is 0xC3 (ret). [EXTRACTED 4.03]");

        Known["Z4wwCFiBELQ"] = new HleVerifiedNoOp(
            "Z4wwCFiBELQ",
            "sceNetCtlTerm",
            "libSceNetCtl.sprx dynsym Z4wwCFiBELQ#H#A: st_value 0x1FE0, st_size 1, " +
            "byte at 0x1FE0 is 0xC3 (ret). [EXTRACTED 4.03]");
    }

    private static void LoadFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable(FileVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                Diagnostics.Add($"verified-no-op file not found: {path}");
                return;
            }

            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Add($"verified-no-op file unreadable ({path}): {ex.Message}");
            return;
        }

        var accepted = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line[..comment];
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = line.Split('|');
            if (fields.Length < 3)
            {
                Diagnostics.Add(
                    $"verified-no-op line {i + 1} rejected (want 'nid | name | evidence'): {lines[i].Trim()}");
                continue;
            }

            var nid = fields[0].Trim();
            var name = fields[1].Trim();
            var evidence = string.Join('|', fields[2..]).Trim();

            if (nid.Length == 0 || evidence.Length == 0)
            {
                Diagnostics.Add(
                    $"verified-no-op line {i + 1} rejected (no evidence): {lines[i].Trim()}");
                continue;
            }

            Known[nid] = new HleVerifiedNoOp(nid, name.Length == 0 ? nid : name, evidence);
            accepted++;
        }

        Diagnostics.Add($"verified-no-op file {path}: {accepted} accepted");
    }
}
