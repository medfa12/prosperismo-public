// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.HLE.Diagnostics;

/// <summary>
/// Fires probe sites around HLE export calls.
///
/// <para>Every export is a natural observation point: the guest has just handed
/// us a fully-formed argument list and is about to act on what we return. A site
/// named <c>export:sceAudioOut2PortGetState</c> (or by NID) dumps whatever the
/// spec asks for at that moment, with the SysV argument registers in scope as
/// <c>arg0</c>..<c>arg5</c> and by register name.</para>
///
/// <para>Sites ending in <c>:ret</c> fire after the call instead, with
/// <c>ret</c> bound to the return value — which is how you see what the title was
/// told, not just what it asked.</para>
/// </summary>
public static class GuestExportProbe
{
    /// <summary>Site name for the call boundary of an export.</summary>
    public static string EntrySite(string exportName) => "export:" + exportName;

    /// <summary>Site name for the return boundary of an export.</summary>
    public static string ReturnSite(string exportName) => "export:" + exportName + ":ret";

    /// <summary>
    /// Wraps <paramref name="function"/> so probes fire around it, or returns it
    /// unchanged when nothing targets this export. Returning the original
    /// delegate matters: an unprobed export must pay nothing at all, and the
    /// overwhelming majority are unprobed on any given boot.
    /// </summary>
    public static SysAbiFunction Decorate(SysAbiFunction function, string exportName, string nid)
    {
        ArgumentNullException.ThrowIfNull(function);

        if (!GuestProbeEngine.IsEnabled)
        {
            return function;
        }

        // A site may name the export or its NID; NIDs are what an import table
        // gives you before a name has been resolved.
        var entry = FirstDefinedSite(EntrySite(exportName), EntrySite(nid));
        var exit = FirstDefinedSite(ReturnSite(exportName), ReturnSite(nid));

        if (entry is null && exit is null)
        {
            return function;
        }

        return context =>
        {
            if (entry is not null)
            {
                GuestProbeEngine.Fire(entry, BuildScope(context, returnValue: null));
            }

            var result = function(context);

            if (exit is not null)
            {
                GuestProbeEngine.Fire(exit, BuildScope(context, result));
            }

            return result;
        };
    }

    /// <summary>
    /// Binds the SysV integer argument registers so a spec can read through them
    /// — <c>[arg0+0x10]</c> is the natural way to dump a struct the guest passed
    /// by pointer.
    /// </summary>
    public static IGuestProbeScope BuildScope(CpuContext context, int? returnValue)
    {
        ArgumentNullException.ThrowIfNull(context);

        var scope = new GuestProbeScope(new GuestProbeMemory(context.Memory));

        scope.Define("rdi", context[CpuRegister.Rdi]).Define("arg0", context[CpuRegister.Rdi]);
        scope.Define("rsi", context[CpuRegister.Rsi]).Define("arg1", context[CpuRegister.Rsi]);
        scope.Define("rdx", context[CpuRegister.Rdx]).Define("arg2", context[CpuRegister.Rdx]);
        scope.Define("rcx", context[CpuRegister.Rcx]).Define("arg3", context[CpuRegister.Rcx]);
        scope.Define("r8", context[CpuRegister.R8]).Define("arg4", context[CpuRegister.R8]);
        scope.Define("r9", context[CpuRegister.R9]).Define("arg5", context[CpuRegister.R9]);
        scope.Define("rsp", context[CpuRegister.Rsp]);
        scope.Define("rax", context[CpuRegister.Rax]);

        if (returnValue.HasValue)
        {
            scope.Define("ret", unchecked((ulong)(long)returnValue.Value));
        }

        return scope;
    }

    private static string? FirstDefinedSite(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && GuestProbeEngine.HasSite(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
