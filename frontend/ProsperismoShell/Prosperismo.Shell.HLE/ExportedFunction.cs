// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.HLE;

public sealed class ExportedFunction
{
    public ExportedFunction(string libraryName, string nid, string name, Generation target, SysAbiFunction function)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(nid);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(function);

        LibraryName = libraryName;
        Nid = nid;
        Name = name;
        Target = target;

        // Wrapping here rather than at the call sites means every path that
        // invokes this export is instrumented, and an export no probe spec names
        // keeps its original delegate — so an uninstrumented boot pays nothing.
        // The census sits closest to the implementation, so what it measures is
        // the export's own effect and not a probe's.
        Function = Diagnostics.GuestExportProbe.Decorate(
            Diagnostics.HleEffectCensus.Decorate(function, libraryName, name, nid),
            name,
            nid);
    }

    public string LibraryName { get; }

    public string Nid { get; }

    public string Name { get; }

    public Generation Target { get; }

    public SysAbiFunction Function { get; }
}
