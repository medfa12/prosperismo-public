// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.Libs.Presentation;

namespace Prosperismo.Shell.BackgroundPoc;

/// <summary>
/// Proves the PS5 animated background can be produced on macOS from oracle
/// from PS5 GCN to SPIR-V by the recovered Gen5 translator, and executed by
/// Vulkan through MoltenVK. Nothing here reimplements the effect.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var eboot = Arg(args, "--eboot");
        var outDir = Arg(args, "--out") ?? "poc-out";
        if (eboot is null || !File.Exists(eboot))
        {
            Console.Error.WriteLine("usage: BackgroundPoc --eboot <NPXS40087/eboot.bin> [--out <dir>] [--frames N]");
            return 2;
        }

        var frames = int.TryParse(Arg(args, "--frames"), out var f) ? Math.Clamp(f, 1, 240) : 8;

        if (args.Contains("--audit-shader-drift"))
        {
            var comparison = Arg(args, "--compare-eboot");
            if (comparison is null || !File.Exists(comparison))
            {
                Console.Error.WriteLine(
                    "--audit-shader-drift requires --compare-eboot <NPXS40087/eboot.bin>");
                return 2;
            }

            return ShaderDriftAudit.Run(eboot, comparison);
        }

        if (args.Contains("--firstwave"))
        {
            return FirstWaveProbe.Run(eboot);
        }

        if (args.Contains("--render-plate"))
        {
            return FirstWaveProbe.RenderPlate(
                eboot,
                Arg(args, "--out-png") ?? "plate.png",
                float.TryParse(Arg(args, "--time"), out var tv) ? tv : 0f,
                Arg(args, "--constants"),
                uint.TryParse(Arg(args, "--width"), out var plw) ? plw : 3840u,
                uint.TryParse(Arg(args, "--height"), out var plh) ? plh : 2160u);
        }

        if (args.Contains("--wave-surface"))
        {
            return WaveSurfaceProbe.Run(
                eboot,
                Arg(args, "--constants") ?? "fwcb.bin",
                Arg(args, "--seeds") ?? "wave_seeds.bin",
                Arg(args, "--draw-stream"));
        }

        if (args.Contains("--render-light"))
        {
            return LightLayerProbe.Render(
                eboot,
                Arg(args, "--out-png") ?? "light.png",
                Arg(args, "--colorcb") ?? "colorcb.bin",
                null,
                uint.TryParse(Arg(args, "--width"), out var lw) ? lw : 1920u,
                uint.TryParse(Arg(args, "--height"), out var lh) ? lh : 1080u);
        }

        if (args.Contains("--merge-flow"))
        {
            return FirstWaveProbe.MergeFlow(eboot);
        }

        if (args.Contains("--compile-particle"))
        {
            return FirstWaveProbe.Compile(eboot);
        }

        if (args.Contains("--render-particles"))
        {
            return ParticleDrawProbe.Render(
                eboot,
                Arg(args, "--blocks") ?? "frames",
                outDir,
                uint.TryParse(Arg(args, "--width"), out var pw) ? pw : 1920u,
                uint.TryParse(Arg(args, "--height"), out var ph) ? ph : 1080u,
                float.TryParse(Arg(args, "--fps"), out var pf) ? pf : 30f);
        }

        if (args.Contains("--dump-stage"))
        {
            return FirstWaveProbe.Dump(eboot, Arg(args, "--dump-stage") ?? "particle_c");
        }

        if (args.Contains("--scan"))
        {
            return Scan(eboot);
        }

        if (args.Contains("--hunt"))
        {
            return Hunt(eboot,
                Convert.ToInt64((Arg(args, "--offset") ?? "C751A0").Replace("0x", string.Empty), 16),
                Convert.ToInt32((Arg(args, "--length") ?? "1C90").Replace("0x", string.Empty), 16));
        }

        if (args.Contains("--sweep"))
        {
            return Sweep(eboot,
                Convert.ToInt64((Arg(args, "--offset") ?? "C751A0").Replace("0x", string.Empty), 16),
                Convert.ToInt32((Arg(args, "--length") ?? "1C90").Replace("0x", string.Empty), 16));
        }
        const int width = 960;
        const int height = 540;

        // The compiler's baked offset is for the donor's 4.03 eboot. Allow an
        var offsetText = Arg(args, "--offset");
        var lengthText = Arg(args, "--length");
        var offset = offsetText is null
            ? Ps5NativeRippleCompiler.FirmwareElfOffset
            : Convert.ToInt64(offsetText.Replace("0x", string.Empty), 16);
        var length = lengthText is null
            ? Ps5NativeRippleCompiler.FirmwareElfLength
            : Convert.ToInt32(lengthText.Replace("0x", string.Empty), 16);

        Console.WriteLine($"eboot   : {eboot}");
        Console.WriteLine($"slice   : offset 0x{offset:X}, 0x{length:X} bytes");

        if (!TryCompileAt(eboot, offset, length, out var program, out var error))
        {
            Console.Error.WriteLine($"translate: FAILED - {error}");
            return 1;
        }

        Console.WriteLine($"translate: OK - {program.FragmentSpirv.Length:N0} bytes of SPIR-V");

        // A neutral source plate and an empty target; the shader supplies the
        // The ripple is a POST-PROCESS: it samples the source image and
        // displaces it. A flat source therefore yields a flat result no matter
        // how correct the shader is, which is what made earlier runs look
        // featureless. Feed it structure so the displacement is visible.
        var source = new byte[width * height * 4];
        var target = new byte[width * height * 4];
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    var check = ((x / 32) + (y / 32)) % 2 == 0;
                    var ramp = (byte)(30 + 200 * y / height);
                    source[i] = check ? ramp : (byte)20;
                    source[i + 1] = check ? (byte)(ramp / 2) : (byte)40;
                    source[i + 2] = check ? (byte)220 : (byte)70;
                    source[i + 3] = 255;
                }
            }

            Console.WriteLine("source  : built-in checkerboard/ramp test pattern");
        }

        var constants = new List<ReadOnlyMemory<byte>>();
        for (var i = 0; i < frames; i++)
        {
            var c0 = new byte[40];
            // Slot 2 is the animating input, found by sweeping the buffer
            // (--sweep): it is the only slot whose value changes the image.
            var slot = int.TryParse(Arg(args, "--timeslot"), out var s) ? s : 2;
            // Log sweep rather than 1/30s steps: the shader responds across
            // orders of magnitude, so a linear second-scale ramp only shows a
            // sliver of its range.
            var lo = float.TryParse(Arg(args, "--from"), out var a) ? a : 0f;
            var hi = float.TryParse(Arg(args, "--to"), out var b) ? b : 8f;
            var value = frames <= 1 ? lo : lo + (hi - lo) * i / (frames - 1f);
            BitConverter.TryWriteBytes(c0.AsSpan(slot * 4, 4), value);
            constants.Add(c0);
        }

        IReadOnlyList<Ps5NativeParticleFrame> rendered;
        try
        {
            rendered = Ps5NativeRippleRenderer.RenderOpaqueFrames(
                program, width, height, source, target, constants);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"render   : FAILED - {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        Directory.CreateDirectory(outDir);
        var distinct = new HashSet<string>();
        for (var i = 0; i < rendered.Count; i++)
        {
            var frame = rendered[i];
            var path = Path.Combine(outDir, $"ripple_{i:D3}.png");
            WritePng(path, frame.Width, frame.Height, frame.Rgba.Span);
            distinct.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(frame.Rgba.Span)));
        }

        Console.WriteLine($"render   : OK - {rendered.Count} frame(s) at {width}x{height}");
        Console.WriteLine($"distinct : {distinct.Count} unique frame(s)  " +
                          $"({(distinct.Count > 1 ? "ANIMATED" : "static - shader ran but did not vary")})");
        Console.WriteLine($"output   : {Path.GetFullPath(outDir)}");
        return 0;
    }

    /// <summary>
    /// Hunts for any input that makes the shader produce SPATIAL variation.
    /// Animating in time is not enough - a flat field whose level changes is
    /// still flat. This drives every slot across a wide magnitude range and
    /// reports the per-row colour count, which is 1 for a flat field.
    /// </summary>
    private static int Hunt(string eboot, long offset, int length)
    {
        if (!Ps5NativeRippleCompiler.TryCompile(eboot, offset, length, out var program, out var error))
        {
            Console.Error.WriteLine($"translate: FAILED - {error}");
            return 1;
        }

        const int width = 256;
        const int height = 144;
        var source = new byte[width * height * 4];
        var target = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var check = ((x / 16) + (y / 16)) % 2 == 0;
                source[i] = check ? (byte)240 : (byte)15;
                source[i + 1] = check ? (byte)120 : (byte)35;
                source[i + 2] = check ? (byte)255 : (byte)90;
                source[i + 3] = 255;
            }
        }

        var values = new[] { -1000f, -100f, -10f, -1f, 0.001f, 0.1f, 1f, 10f, 100f, 1000f, 10000f };
        var best = 1;
        Console.WriteLine("slot   value      distinct-colours-in-centre-row");
        for (var slot = 0; slot < 10; slot++)
        {
            foreach (var value in values)
            {
                var c0 = new byte[40];
                BitConverter.TryWriteBytes(c0.AsSpan(slot * 4, 4), value);
                try
                {
                    var frames = Ps5NativeRippleRenderer.RenderOpaqueFrames(
                        program, width, height, source, target,
                        new List<ReadOnlyMemory<byte>> { c0 });
                    var span = frames[0].Rgba.Span;
                    var row = (height / 2) * width * 4;
                    var seen = new HashSet<int>();
                    for (var x = 0; x < width; x++)
                    {
                        var o = row + x * 4;
                        seen.Add((span[o] << 16) | (span[o + 1] << 8) | span[o + 2]);
                    }

                    if (seen.Count > 1)
                    {
                        Console.WriteLine($"{slot,4}  {value,10}  {seen.Count}   <== SPATIAL");
                        best = Math.Max(best, seen.Count);
                    }
                }
                catch
                {
                    // Some magnitudes trip the pipeline; keep hunting.
                }
            }
        }

        Console.WriteLine(best > 1
            ? $"found spatial variation (max {best} colours per row)"
            : "no input produced spatial variation - shader stays a flat field");
        return 0;
    }

    /// <summary>
    /// Drives each float slot of the 40-byte constant buffer in turn and
    /// reports which ones change the rendered image. The shader's ABI is not
    /// observation rather than assumed.
    /// </summary>
    private static int Sweep(string eboot, long offset, int length)
    {
        if (!Ps5NativeRippleCompiler.TryCompile(eboot, offset, length, out var program, out var error))
        {
            Console.Error.WriteLine($"translate: FAILED - {error}");
            return 1;
        }

        const int width = 320;
        const int height = 180;
        var source = new byte[width * height * 4];
        var target = new byte[width * height * 4];
        for (var i = 0; i < source.Length; i += 4)
        {
            source[i] = 5; source[i + 1] = 10; source[i + 2] = 22; source[i + 3] = 255;
        }

        Console.WriteLine("slot  distinct  note");
        for (var slot = 0; slot < 10; slot++)
        {
            var constants = new List<ReadOnlyMemory<byte>>();
            foreach (var value in new[] { 0f, 0.25f, 1f, 4f, 16f })
            {
                var c0 = new byte[40];
                BitConverter.TryWriteBytes(c0.AsSpan(slot * 4, 4), value);
                constants.Add(c0);
            }

            try
            {
                var frames = Ps5NativeRippleRenderer.RenderOpaqueFrames(
                    program, width, height, source, target, constants);
                var hashes = frames
                    .Select(f => Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(f.Rgba.Span)))
                    .Distinct()
                    .Count();
                var nonWhite = frames.Any(f => HasContent(f.Rgba.Span));
                Console.WriteLine($"{slot,4}  {hashes,8}  {(hashes > 1 ? "VARIES" : "static")}" +
                                  $"{(nonWhite ? ", has content" : ", blank")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{slot,4}  {"-",8}  {ex.GetType().Name}");
            }
        }

        return 0;
    }

    private static bool HasContent(ReadOnlySpan<byte> rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] != 255 || rgba[i + 1] != 255 || rgba[i + 2] != 255)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks every AMDGPU shader ELF in the eboot and reports which one the
    /// this finds the equivalent in any other.
    /// </summary>
    private static int Scan(string eboot)
    {
        var image = File.ReadAllBytes(eboot);
        var offsets = new List<long>();
        for (var i = 0; i + 20 < image.Length; i++)
        {
            if (image[i] == 0x7F && image[i + 1] == (byte)'E' &&
                image[i + 2] == (byte)'L' && image[i + 3] == (byte)'F' &&
                BitConverter.ToUInt16(image, i + 18) == 224)
            {
                offsets.Add(i);
            }
        }

        Console.WriteLine($"scanning {offsets.Count} AMDGPU shader ELFs...");
        var accepted = 0;
        for (var k = 0; k < offsets.Count; k++)
        {
            var start = offsets[k];
            var end = k + 1 < offsets.Count ? offsets[k + 1] : image.LongLength;
            var length = (int)Math.Min(end - start, 0x8000);
            if (!Ps5NativeRippleCompiler.TryCompile(eboot, start, length, out var p, out var err))
            {
                if (!err.Contains("ABI mismatch", StringComparison.Ordinal) &&
                    !err.Contains("does not contain an ELF", StringComparison.Ordinal))
                {
                    Console.WriteLine($"  0x{start:X}  len 0x{length:X}  {err}");
                }

                continue;
            }

            accepted++;
            Console.WriteLine($"  0x{start:X}  len 0x{length:X}  ACCEPTED - " +
                              $"{p.FragmentSpirv.Length:N0} bytes SPIR-V");
        }

        Console.WriteLine(accepted > 0
            ? $"{accepted} shader(s) match the ripple ABI"
            : "no shader in this eboot matches the ripple ABI");
        return accepted > 0 ? 0 : 1;
    }

    /// <summary>
    /// </summary>
    private static bool TryCompileAt(
        string eboot, long offset, int length,
        out Ps5NativeRippleProgram program, out string error)
    {
        var saved = (Ps5NativeRippleCompiler.FirmwareElfOffset,
                     Ps5NativeRippleCompiler.FirmwareElfLength);
        _ = saved;
        return Ps5NativeRippleCompiler.TryCompile(eboot, offset, length, out program, out error);
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void WritePng(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        var raw = new byte[(width * 4 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            raw[y * (width * 4 + 1)] = 0;
            rgba.Slice(y * width * 4, width * 4).CopyTo(raw.AsSpan(y * (width * 4 + 1) + 1));
        }

        using var file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 6;
        WriteChunk(file, "IHDR", ihdr);
        using var deflated = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(
                   deflated, System.IO.Compression.CompressionLevel.Fastest, true))
        {
            zlib.Write(raw);
        }

        WriteChunk(file, "IDAT", deflated.ToArray());
        WriteChunk(file, "IEND", Array.Empty<byte>());
    }

    private static void BinaryPrimitives(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 24); destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8); destination[3] = (byte)value;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives(length, (uint)data.Length);
        stream.Write(length);
        var payload = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) payload[i] = (byte)type[i];
        data.CopyTo(payload, 4);
        stream.Write(payload);
        var crc = new byte[4];
        BinaryPrimitives(crc, Crc32(payload));
        stream.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }
}
