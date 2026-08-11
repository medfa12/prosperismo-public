// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Prosperismo.GUI.SystemAssets.Shell;

/// <summary>One 0x70-byte authored Plane2 record from NPXS40087.</summary>
internal sealed class Ps5NativeWaveRecord
{
    internal const int FloatCount = 0x70 / sizeof(float);
    private readonly float[] _values;

    internal Ps5NativeWaveRecord(float[] values)
    {
        if (values.Length != FloatCount)
        {
            throw new ArgumentException($"A Plane2 record contains {FloatCount} floats.", nameof(values));
        }

        _values = values;
    }

    internal float this[int index] => _values[index];
}

/// <summary>
/// Reads Plane2's complete authored record table from the narrow packaged
/// binary slice. Explicit full-ELF loading remains available to research tests.
/// </summary>
internal static class Ps5NativeWaveRecordSource
{
    internal const int RecordStride = 0x70;
    internal const int RecordCount = 37;

    private static readonly ConcurrentDictionary<(string Path, int Index), Ps5NativeWaveRecord>
        Cache = new();

    internal static bool TryLoad(int recordIndex, out Ps5NativeWaveRecord? record)
    {
        record = null;
        var path = BigPicturePackage.Resolve("3.00/background/plane2-records.bin");
        if ((uint)recordIndex >= RecordCount || path is null)
        {
            return false;
        }

        try
        {
            record = Cache.GetOrAdd(
                (path, recordIndex),
                static key => ReadPackagedRecord(key.Path, key.Index));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void Invalidate() => Cache.Clear();

    private static Ps5NativeWaveRecord ReadPackagedRecord(string path, int recordIndex)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != RecordStride * RecordCount)
        {
            throw new InvalidDataException("Packaged Plane2 table has the wrong length.");
        }

        var recordBytes = bytes.AsSpan(recordIndex * RecordStride, RecordStride);
        var values = new float[Ps5NativeWaveRecord.FloatCount];
        for (int index = 0; index < values.Length; index++)
        {
            int bits = BinaryPrimitives.ReadInt32LittleEndian(
                recordBytes[(index * sizeof(float))..]);
            values[index] = BitConverter.Int32BitsToSingle(bits);
            if (!float.IsFinite(values[index]))
            {
                throw new InvalidDataException("Plane2 record contains a non-finite value.");
            }
        }

        return new Ps5NativeWaveRecord(values);
    }

}
