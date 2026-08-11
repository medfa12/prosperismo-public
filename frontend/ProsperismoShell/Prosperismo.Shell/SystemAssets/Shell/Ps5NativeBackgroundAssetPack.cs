// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using Prosperismo.Libs.Presentation;
using Prosperismo.Libs.Textures;

namespace Prosperismo.GUI.SystemAssets.Shell;

/// <summary>
/// Loads the narrow, committed NPXS40087 background payload. The original
/// eboot remains reference evidence and is never an implicit product-runtime
/// dependency.
/// </summary>
internal static class Ps5NativeBackgroundAssetPack
{
    private const string PackagedRelativePath = "assets/big-picture/12.40/background";
    private const int CompatibilityImageLength = 0x1203200;
    private sealed record Slice(
        string RelativePath,
        int FileOffset,
        int ByteLength,
        string Sha256);

    private static readonly Slice[] ImageSlices =
    [
        new("patterns/length-table.bin", 0x0FF58A0, 0x38,
            "ddd1d2209a7fe28bc398650824f443b341a27005563d6abb64e5e8aa409020a4"),
        new("patterns/coldboot.bin", 0x0FF58E0, 0x1FAA,
            "3e35757bbab6287c666e79e1cb732a5e2e0ea9a9b85661921dbff3b40f723425"),
        new("patterns/spread_expanded.bin", 0x0FF7890, 0x1DF5,
            "86013b9b2d22bf66802530bcd8ea43c89ecf4f285fa713f2c4f3edd4242582df"),
        new("descriptors/light-texture.bin", 0x10069F0, 0x20,
            "2bf20949e430bf15acdc30a6982bd6c7a987ba2028c8dd4ce4e85a9de4e811b7"),
        new("descriptors/light-texture.bin", 0x100AAF0, 0x20,
            "2bf20949e430bf15acdc30a6982bd6c7a987ba2028c8dd4ce4e85a9de4e811b7"),
        new("shaders/rect_uv_vv.bin", 0x11EEE00, 0xCC,
            "3dff78d60fe00e4cf542fefecbf0e2ebd39ab81cfc51fe3335c98448ec516bcf"),
        new("shaders/light_p.bin", 0x11F9700, 0x818,
            "a9262053f63747e013aaaf1fd93d088486b77d11a87af9362e33d57ef3f19d3f"),
        new("shaders/particle_c.bin", 0x11FA100, 0x71A4,
            "6c77e3476edc128dd00a91e52cf2b5b40f4c1f4fefc0ce7d80cd4a6bdd8e6384"),
        new("shaders/particle_p.bin", 0x1201500, 0x800,
            "362f26cb03379bc3cfc1e38b31a241c41523a93b98a6cbd5d3823dafa65734e8"),
        new("shaders/particle_vv.bin", 0x1201D00, 0x700,
            "2c27623d512217c614270f8b6396ccf4a84fb53d6d04481cb2229da861c9959e"),
        new("shaders/large_particle_p.bin", 0x1202400, 0x600,
            "c2953663645afee9a5c14a9bf129bf1f6436da72ad0bbb8c6da5e433ce8df7d4"),
        new("shaders/large_particle_vv.bin", 0x1202C00, 0x600,
            "952ddd2d21cc2188d11dfeb793b13b7661f1d71121cff701377d3f24ca58dbf4"),
    ];

    // Asset validation and compatibility-image construction used to happen on
    // every native-source creation. Apart from rereading and hashing the same
    // immediately before the first Vulkan pipelines were created. The package
    // is immutable for a process lifetime, so retain the verified payload and
    // let the background source prewarm it away from the UI thread.
    private static readonly Lazy<Payload?> CachedPayload = new(
        LoadPayload,
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal sealed record Payload(
        byte[] CompatibilityImage,
        byte[] BootColorCb,
        byte[] LoginColorCb,
        byte[] HomeColorCb,
        Ps5NativeParticleTexture Particle0,
        Ps5NativeParticleTexture Particle1,
        byte[] ParticleDescriptor,
        byte[] LightFloorRgba,
        byte[] LightVolumeRgba);

    internal static bool TryLoad(out Payload payload)
    {
        var cached = CachedPayload.Value;
        if (cached is not null)
        {
            payload = cached;
            return true;
        }

        payload = null!;
        return false;
    }

    private static Payload? LoadPayload()
    {
        foreach (var directory in CandidateDirectories())
        {
            try
            {
                var particleDirectory = Path.GetFullPath(Path.Combine(
                    directory, "..", "..", "3.00", "textures"));
                var p0 = Path.Combine(
                    particleDirectory, "Sce.Vsh.ShellUI.BGLayer.Particle0.png");
                var p1 = Path.Combine(
                    particleDirectory, "Sce.Vsh.ShellUI.BGLayer.Particle1.png");
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                var particle0 = ReadPngExact(p0, 116791,
                    "ff2b9a36d64d4b920e08a6375b766a5c993537984e7eea165c7a8f5e9e0fce05",
                    480, 270);
                var particle1 = ReadPngExact(p1, 51900,
                    "8e92a039b649b91ed2641c462be34273faa90c1870680a8146482d3162e7577b",
                    480, 270);
                var lightFloor = ReadPngExact(
                    Path.Combine(directory, "textures", "light-floor.png"),
                    12376,
                    "bc477798cbad58ac8888aca54e7142b04f506219f8a7d1ce2f14253a6d110162",
                    128, 128);
                var lightVolume = ReadPngExact(
                    Path.Combine(directory, "textures", "light-volume.png"),
                    9727,
                    "4e2c9f314961e0eac4a63d963115ff2e7da67d80659bc447483d87be4a07e19c",
                    128, 128);
                var particleDescriptor = ReadExact(
                    Path.Combine(particleDirectory, "..", "descriptors", "bglayer-particle.bin"),
                    32,
                    "6c54a8ce40ac78fb29d34960cc0c877ef66a58bcea2df0e0882c6a7c59a9ffd1");

                var image = new byte[CompatibilityImageLength];
                foreach (var slice in ImageSlices)
                {
                    var bytes = ReadExact(
                        Path.Combine(directory, slice.RelativePath),
                        slice.ByteLength,
                        slice.Sha256);
                    bytes.CopyTo(image, slice.FileOffset);
                }

                var boot = ReadExact(
                    Path.Combine(directory, "colors", "boot-color-cb.bin"),
                    0x7C,
                    Npxs40087ShellContract.BootPalette.AssetSha256);
                var login = ReadExact(
                    Path.Combine(directory, "colors", "login-color-cb.bin"),
                    0x7C,
                    Npxs40087ShellContract.LoginPalette.AssetSha256);
                var home = ReadExact(
                    Path.Combine(directory, "colors", "home-color-cb.bin"),
                    0x7C,
                    Npxs40087ShellContract.HomePalette.AssetSha256);

                return new Payload(
                    image,
                    boot,
                    login,
                    home,
                    new Ps5NativeParticleTexture(480, 270, particle0),
                    new Ps5NativeParticleTexture(480, 270, particle1),
                    particleDescriptor,
                    lightFloor,
                    lightVolume);
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var candidates = new List<string>();
        try
        {
            var current = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
            for (var depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
            {
                var candidate = Path.Combine(current.FullName, PackagedRelativePath);
                if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(candidate);
                }
            }
        }
        catch (Exception)
        {
        }

        return candidates;
    }

    private static byte[] ReadPngExact(
        string path,
        int byteLength,
        string sha256,
        int expectedWidth,
        int expectedHeight)
    {
        var bytes = ReadExact(path, byteLength, sha256);
        var rgba = PngRgbaImage.Decode(bytes, out var width, out var height);
        if (width != expectedWidth || height != expectedHeight)
        {
            throw new InvalidDataException($"PNG dimensions failed validation: {path}");
        }
        return rgba;
    }

    private static byte[] ReadExact(string path, int byteLength, string sha256)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != byteLength ||
            !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"bundled background asset failed validation: {path}");
        }

        return bytes;
    }
}
