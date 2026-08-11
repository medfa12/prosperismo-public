// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace Prosperismo.Libs.Presentation;

/// <summary>
/// Loads pre-translated host SPIR-V program assets for the Big Picture renderer.
/// </summary>
public static class Ps5NativeSpirvAsset
{
    private const uint Magic = 0x0723_0203;
    private const int HeaderSize = 20;

    public static bool TryLoad(string? path, out ReadOnlyMemory<byte> spirv, out string error)
    {
        spirv = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            error = "SPIR-V asset is unavailable";
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < HeaderSize || bytes.Length % sizeof(uint) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
            {
                error = "asset is not a valid SPIR-V module";
                return false;
            }

            spirv = bytes;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }
}
