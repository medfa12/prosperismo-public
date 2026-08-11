// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.HLE.Diagnostics;

/// <summary>
/// Loads a probe spec once and fires its sites on demand.
///
/// <para>The point of this type is economics. A hand-written probe costs an edit,
/// a rebuild, a redeploy and a boot to answer one question about guest state.
/// A spec-driven probe costs a text edit, and one boot answers every question
/// the spec asks. Sites are named, so the code that fires them never changes as
/// the questions change.</para>
///
/// <para>Configuration:</para>
/// <list type="bullet">
///   <item><c>PROSPERISMO_PROBE_SPEC=&lt;path&gt;</c> — JSON spec file.</item>
///   <item><c>PROSPERISMO_PROBE_SPEC_JSON=&lt;json&gt;</c> — inline spec, for one-off runs.</item>
/// </list>
/// </summary>
public static class GuestProbeEngine
{
    private const string SpecPathVariable = "PROSPERISMO_PROBE_SPEC";
    private const string SpecInlineVariable = "PROSPERISMO_PROBE_SPEC_JSON";

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, int> HitCounts = new(StringComparer.OrdinalIgnoreCase);

    private static bool _loaded;
    private static GuestProbeSpec? _spec;
    private static Action<string>? _sink;

    /// <summary>
    /// True when a spec is loaded and at least one site exists. Call sites should
    /// test this before building a scope, so a probe-free boot pays nothing.
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            EnsureLoaded();
            return _spec is not null;
        }
    }

    /// <summary>
    /// Where rendered probe lines go. Defaults to <see cref="Console.Out"/> so the
    /// engine has no dependency on the logging assembly; the host replaces it
    /// with the real log sink during startup.
    /// </summary>
    public static void SetSink(Action<string>? sink)
    {
        lock (Gate)
        {
            _sink = sink;
        }
    }

    /// <summary>
    /// Installs a spec directly, bypassing the environment. Used by tests and by
    /// hosts that carry probe specs in their own configuration.
    /// </summary>
    public static void SetSpec(GuestProbeSpec? spec)
    {
        lock (Gate)
        {
            _spec = spec;
            _loaded = true;
            HitCounts.Clear();
        }
    }

    /// <summary>Forgets the loaded spec so the next use re-reads the environment.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _spec = null;
            _loaded = false;
            HitCounts.Clear();
        }
    }

    /// <summary>
    /// True when the loaded spec declares <paramref name="siteName"/> at all,
    /// regardless of its remaining hit budget. Used to decide once, at startup,
    /// whether a call site needs instrumenting.
    /// </summary>
    public static bool HasSite(string siteName)
    {
        EnsureLoaded();
        return _spec is not null &&
               !string.IsNullOrWhiteSpace(siteName) &&
               _spec.SitesNamed(siteName).Any();
    }

    /// <summary>
    /// True when <paramref name="siteName"/> has at least one site that has not
    /// yet exhausted its hit budget. Lets an expensive call site skip building a
    /// scope for a site that would not fire anyway.
    /// </summary>
    public static bool WillFire(string siteName)
    {
        EnsureLoaded();
        if (_spec is null)
        {
            return false;
        }

        lock (Gate)
        {
            foreach (var site in _spec.SitesNamed(siteName))
            {
                var key = SiteKey(site);
                var hits = HitCounts.GetValueOrDefault(key);
                if (site.MaxHits <= 0 || hits < site.MaxHits)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Fires every site named <paramref name="siteName"/> against
    /// <paramref name="scope"/>, emitting one line per dump. Returns the number of
    /// lines emitted. Never throws.
    /// </summary>
    public static int Fire(string siteName, IGuestProbeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        EnsureLoaded();
        if (_spec is null || string.IsNullOrWhiteSpace(siteName))
        {
            return 0;
        }

        var emitted = 0;

        foreach (var site in _spec.SitesNamed(siteName))
        {
            if (!TryClaimHit(site, out var hit))
            {
                continue;
            }

            var siteScope = BuildScope(site, scope);

            foreach (var dump in site.Dumps)
            {
                Emit($"[PROBE][{site.Name}#{hit}] {GuestProbeDumper.Render(dump, siteScope)}");
                emitted++;
            }
        }

        return emitted;
    }

    /// <summary>
    /// Layers the site's anchors over the caller's scope, so dumps can be written
    /// in terms of the object under study ("sound+0x2660") rather than whichever
    /// register happens to hold it at this particular trap.
    /// </summary>
    private static IGuestProbeScope BuildScope(GuestProbeSite site, IGuestProbeScope outer)
    {
        if (site.Anchors.Count == 0)
        {
            return outer;
        }

        var scope = new GuestProbeLayeredScope(outer);
        foreach (var (name, expression) in site.Anchors)
        {
            // Anchors resolve against the caller's scope, not against each other,
            // so their order in the file cannot change their meaning.
            if (GuestAddressExpression.TryEvaluate(expression, outer, out var value, out _))
            {
                scope.Define(name, value);
            }
        }

        return scope;
    }

    private static bool TryClaimHit(GuestProbeSite site, out int hit)
    {
        lock (Gate)
        {
            var key = SiteKey(site);
            var seen = HitCounts.GetValueOrDefault(key);
            HitCounts[key] = seen + 1;

            if (site.MaxHits > 0 && seen >= site.MaxHits)
            {
                hit = seen;
                return false;
            }

            hit = seen;
            return seen % site.EveryNth == 0;
        }
    }

    private static string SiteKey(GuestProbeSite site) =>
        $"{site.Name}:{site.Dumps.Count}:{site.MaxHits}";

    private static void Emit(string line)
    {
        var sink = _sink;
        if (sink is null)
        {
            Console.Out.WriteLine(line);
            return;
        }

        try
        {
            sink(line);
        }
        catch
        {
            // A failing sink must not propagate into the guest's execution path.
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
            _spec = LoadFromEnvironment();
        }
    }

    private static GuestProbeSpec? LoadFromEnvironment()
    {
        var inline = Environment.GetEnvironmentVariable(SpecInlineVariable);
        if (!string.IsNullOrWhiteSpace(inline))
        {
            return Parse(inline, SpecInlineVariable);
        }

        var path = Environment.GetEnvironmentVariable(SpecPathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                Emit($"[PROBE] spec not found: {path}");
                return null;
            }

            return Parse(File.ReadAllText(path), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Emit($"[PROBE] spec unreadable: {ex.Message}");
            return null;
        }
    }

    private static GuestProbeSpec? Parse(string json, string origin)
    {
        var spec = GuestProbeSpec.TryParse(json, out var error);
        if (spec is null)
        {
            // Loud, because a silently ignored probe file looks exactly like a
            // site that never fired — and that mistake costs a boot to discover.
            Emit($"[PROBE] spec rejected ({origin}): {error}");
            return null;
        }

        Emit($"[PROBE] loaded {spec.Sites.Count} site(s) from {origin}: " +
             string.Join(", ", spec.Sites.Select(s => s.Name)));
        return spec;
    }

    /// <summary>A scope that adds names on top of an existing one.</summary>
    private sealed class GuestProbeLayeredScope(IGuestProbeScope inner) : IGuestProbeScope
    {
        private readonly Dictionary<string, ulong> _names = new(StringComparer.OrdinalIgnoreCase);

        public GuestProbeMemory Memory => inner.Memory;

        public void Define(string name, ulong value) => _names[name] = value;

        public bool TryResolveName(string name, out ulong value) =>
            _names.TryGetValue(name, out value) || inner.TryResolveName(name, out value);
    }
}
