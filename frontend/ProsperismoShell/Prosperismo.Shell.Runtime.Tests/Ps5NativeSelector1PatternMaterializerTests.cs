// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using Prosperismo.Libs.Presentation;
using Prosperismo.Libs.Textures;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class Ps5NativeSelector1PatternMaterializerTests
{
    [Fact]
    public void PackagedParticleTexturesDecode()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(), "assets", "big-picture", "3.00", "textures");
        foreach (var name in new[]
                 {
                     "Sce.Vsh.ShellUI.BGLayer.Particle0.png",
                     "Sce.Vsh.ShellUI.BGLayer.Particle1.png",
                 })
        {
            var rgba = PngRgbaImage.Load(
                Path.Combine(directory, name), out var width, out var height);
            Assert.Equal(width * height * 4, rgba.Length);
            Assert.Contains(rgba, value => value != 0);
        }
    }

    [Fact]
    public void ColdBootTimelinePreservesBothNativeInstancesDuringHandoff()
    {
        var before = Ps5NativeColdBootAmbientTimeline.Sample(8.499);
        var handoff = Ps5NativeColdBootAmbientTimeline.Sample(8.5);
        var retained = Ps5NativeColdBootAmbientTimeline.Sample(11.0);
        var released = Ps5NativeColdBootAmbientTimeline.Sample(11.001);

        Assert.Single(before);
        Assert.Equal(0u, before[0].TransitionPatternFlag);
        Assert.Equal(2, handoff.Count);
        Assert.Equal(0x10u, handoff[0].TransitionPatternFlag);
        Assert.Equal(0x11u, handoff[1].TransitionPatternFlag);
        Assert.Equal(0.0, handoff[1].LocalSeconds, precision: 10);
        Assert.Equal(2, retained.Count);
        Assert.Single(released);
        Assert.Equal(Ps5NativeColdBootAmbientTimeline.AmbientSelector, released[0].Selector);
        Assert.False(Ps5NativeColdBootAmbientTimeline.IsBeforePatternAction(6.5));
    }

    [Fact]
    public void ColdBootWallClockMapsTheNativeAuthoringDomainOntoSixSeconds()
    {
        var patternActionElapsed =
            Ps5NativeColdBootAmbientTimeline.ManagedPatternActionSeconds;

        Assert.Equal(
            Ps5NativeColdBootAmbientTimeline.PatternActionSeconds,
            Ps5NativeColdBootAmbientTimeline.NativeSecondsAtElapsed(patternActionElapsed),
            precision: 10);
        Assert.Equal(
            Ps5NativeColdBootAmbientTimeline.PatternActionEndSeconds,
            Ps5NativeColdBootAmbientTimeline.NativeSecondsAtElapsed(
                Ps5NativeColdBootAmbientTimeline.ManagedHomeLightTransitionSeconds),
            precision: 10);
        Assert.Equal(
            Ps5NativeColdBootAmbientTimeline.ParticleTransitionSeconds,
            Ps5NativeColdBootAmbientTimeline.NativeSecondsAtElapsed(6.0),
            precision: 10);
        Assert.Equal(
            Ps5NativeColdBootAmbientTimeline.PreviousInstanceReleaseSeconds,
            Ps5NativeColdBootAmbientTimeline.NativeSecondsAtElapsed(8.5),
            precision: 10);
        Assert.True(Ps5NativeColdBootAmbientTimeline.IsBeforePatternActionAtElapsed(
            patternActionElapsed - 0.001));
        Assert.False(Ps5NativeColdBootAmbientTimeline.IsBeforePatternActionAtElapsed(
            patternActionElapsed));

        var handoff = Ps5NativeColdBootAmbientTimeline.SampleElapsed(6.0);
        Assert.Equal(2, handoff.Count);
        Assert.Equal(0.0, handoff[1].LocalSeconds, precision: 10);
        var released = Assert.Single(Ps5NativeColdBootAmbientTimeline.SampleElapsed(8.501));
        Assert.Equal(Ps5NativeColdBootAmbientTimeline.AmbientSelector, released.Selector);
        Assert.Equal(2.501, released.LocalSeconds, precision: 10);
    }

    [Fact]
    public void ComputeSrtCarriesRecoveredPerFrameAbi()
    {
        const float time = 12.5f;
        const uint transitionPatternFlag = 0x21;
        const float timeStep = 1.0f / 30.0f;
        const float timeRate = 0.25f;
        var srt = Ps5NativeParticleProgramCompiler.CreateSmallParticleComputeSrt(
            time,
            preSimulation: false,
            transitionPatternFlag,
            timeStep,
            timeRate);

        Assert.Equal(Ps5NativeParticleComputeBackend.SrtByteCount, srt.Length);
        Assert.Equal(time, BitConverter.ToSingle(srt, 0x08));
        Assert.Equal(timeStep, BitConverter.ToSingle(srt, 0x0C));
        Assert.Equal(timeRate, BitConverter.ToSingle(srt, 0x10));
        Assert.Equal(0u, BitConverter.ToUInt32(srt, 0x14));
        Assert.Equal(transitionPatternFlag, BitConverter.ToUInt32(srt, 0x18));
    }

    [Fact]
    public void ComputeResourcesRestoreRuntimeAllocationDescriptors()
    {
        var resources = Ps5NativeParticleProgramCompiler.CreateSmallParticleComputeResources(
            new byte[Ps5NativeParticleComputeRequest.ResourceByteCount]);

        Assert.Equal(4u, BitConverter.ToUInt32(resources, 0x04) >> 16);
        Assert.Equal(6000u, BitConverter.ToUInt32(resources, 0x08));
        Assert.Equal(0x44u, BitConverter.ToUInt32(resources, 0x14) >> 16);
        Assert.Equal(6000u, BitConverter.ToUInt32(resources, 0x18));
    }

    [Fact]
    public void LargeDrawHistoryUsesItsOwnInvocationRange()
    {
        const int recordStride = 0x44;
        const int recordIndex = 17;
        var properties = new byte[Ps5NativeParticleComputeRequest.ParticlePropertyByteCount];
        BitConverter.TryWriteBytes(
            properties.AsSpan((recordIndex * recordStride) + 0x38),
            0.75f);
        BitConverter.TryWriteBytes(
            properties.AsSpan((recordIndex * recordStride) + 0x40),
            -1.0f);
        var largeDraw = new byte[0xEC];
        BitConverter.TryWriteBytes(largeDraw.AsSpan(0xAC), 1u);
        BitConverter.TryWriteBytes(largeDraw.AsSpan(0xB0), (uint)recordIndex);
        BitConverter.TryWriteBytes(largeDraw.AsSpan(0xB4), 1u);
        BitConverter.TryWriteBytes(largeDraw.AsSpan(0xB8), 6000u);

        Ps5NativeParticleComputeBackend.ApplyDrawHistoryRange(
            properties,
            largeDraw,
            isLarge: true);

        Assert.Equal(
            0.75f,
            BitConverter.ToSingle(properties, (recordIndex * recordStride) + 0x40));
    }

    [Fact]
    public void Selector1MatchesPythonExporterAtZeroFiveAndTenSeconds()
    {
        var repositoryRoot = FindRepositoryRoot();
        if (!HasPythonParticleOracle(repositoryRoot))
        {
            return;
        }
        var ebootPath = Path.Combine(
            repositoryRoot,
            "ps5oracle",
            "PS5_12.40",
            "filesystems",
            "system_ex",
            "app",
            "NPXS40087",
            "eboot.bin");
        var exporterPath = Path.Combine(repositoryRoot, "tools", "export_particle_frames.py");
        Assert.True(File.Exists(ebootPath), $"missing audited eboot: {ebootPath}");
        Assert.True(File.Exists(exporterPath), $"missing Python exporter: {exporterPath}");

        var materializer = Ps5NativeSelector1PatternMaterializer.FromEboot(ebootPath);
        using var temporaryRoot = new TemporaryDirectory();
        foreach (var elapsedSeconds in new[] { 0.0, 5.0, 10.0 })
        {
            var outputDirectory = Path.Combine(temporaryRoot.Path, $"t-{elapsedSeconds:0}");
            Directory.CreateDirectory(outputDirectory);
            RunPythonExporter(
                repositoryRoot,
                exporterPath,
                ebootPath,
                outputDirectory,
                elapsedSeconds);

            Ps5NativeSelector1PatternVerifier.VerifyAgainstPythonExporterFrame(
                materializer,
                elapsedSeconds,
                Path.Combine(outputDirectory, "00000.bin"));
        }
    }

    [Fact]
    public void ColdBootSelector0MatchesAllPythonExporterResources()
    {
        var repositoryRoot = FindRepositoryRoot();
        if (!HasPythonParticleOracle(repositoryRoot))
        {
            return;
        }
        var ebootPath = Path.Combine(
            repositoryRoot,
            "ps5oracle",
            "PS5_12.40",
            "filesystems",
            "system_ex",
            "app",
            "NPXS40087",
            "eboot.bin");
        var exporterPath = Path.Combine(repositoryRoot, "tools", "export_particle_frames.py");
        Assert.True(File.Exists(ebootPath), $"missing audited eboot: {ebootPath}");
        Assert.True(File.Exists(exporterPath), $"missing Python exporter: {exporterPath}");

        var materializer = Ps5NativeSelector1PatternMaterializer.FromEboot(ebootPath, selector: 0);
        Assert.Equal("coldboot", materializer.EmbeddedName);
        Assert.Equal(0xFF58E0, materializer.BlobFileOffset);
        Assert.Equal(0x1FAA, materializer.BlobByteLength);

        using var temporaryRoot = new TemporaryDirectory();
        foreach (var elapsedSeconds in new[] { 0.0, 6.0, 6.5, 8.5 })
        {
            var outputDirectory = Path.Combine(temporaryRoot.Path, $"coldboot-{elapsedSeconds:R}");
            Directory.CreateDirectory(outputDirectory);
            RunPythonExporter(
                repositoryRoot,
                exporterPath,
                ebootPath,
                outputDirectory,
                elapsedSeconds,
                selector: 0);

            Ps5NativeSelector1PatternVerifier.VerifyAllResourcesAgainstPythonExporterFrame(
                materializer,
                elapsedSeconds,
                Path.Combine(outputDirectory, "00000.bin"));
        }
    }

    private static bool HasPythonParticleOracle(string repositoryRoot)
    {
        var module = Path.Combine(
            repositoryRoot,
            "ps5oracle",
            "sharpemu",
            "scripts",
            "ps5_particle_patterns.py");
        return File.Exists(module);
    }

    private static void RunPythonExporter(
        string repositoryRoot,
        string exporterPath,
        string ebootPath,
        string outputDirectory,
        double elapsedSeconds,
        int selector = 1)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "python3",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(exporterPath);
        startInfo.ArgumentList.Add("--eboot");
        startInfo.ArgumentList.Add(ebootPath);
        startInfo.ArgumentList.Add("--out");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("--selector");
        startInfo.ArgumentList.Add(selector.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--start");
        startInfo.ArgumentList.Add(elapsedSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--fps");
        startInfo.ArgumentList.Add("60");
        startInfo.ArgumentList.Add("--frames");
        startInfo.ArgumentList.Add("1");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start python3 exporter");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"Python exporter failed at t={elapsedSeconds}: {error}\n{output}");
    }

    private static byte[] RunColourPresetExporter(
        string repositoryRoot,
        string scriptPath,
        string ebootPath,
        int preset)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "python3",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--eboot");
        startInfo.ArgumentList.Add(ebootPath);
        startInfo.ArgumentList.Add("--preset");
        startInfo.ArgumentList.Add(preset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--raw");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start light-colour exporter");
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"light-colour exporter failed: {error}");
        return output.ToArray();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "export_particle_frames.py")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("could not locate the Prosperismo repository root");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("prosperismo-selector1-").FullName;

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
