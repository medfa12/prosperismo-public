// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.SystemAssets;
using Prosperismo.GUI.SystemAssets.Audio;
using Prosperismo.GUI;
using Prosperismo.GUI.BootAnimation;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class ShellAudioDiscoveryTests
{
    [Fact]
    public void PackagedTracksHaveValidPcmWaveHeaders()
    {
        var directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "assets", "big-picture", "3.00", "audio"));
        var names = new[]
        {
            "bgm_home.wav",
            "bgm_onboarding.wav",
            "sfx_coldboot.wav",
            "sfx_initialboot.wav",
            "sfx_transition.wav",
            "sfx_warmboot.wav",
        };

        foreach (var name in names)
        {
            var data = File.ReadAllBytes(Path.Combine(directory, name));
            Assert.True(PcmWaveMusic.LooksLikePcmWave(data));
            var clip = PcmWaveMusic.TryDecode(Path.Combine(directory, name), gain: 1f, forLooping: false);
            Assert.NotNull(clip);
            Assert.True(clip!.FrameCount > 0);
        }
    }

    [Fact]
    public void DefaultDiscoveryUsesCommittedPackageForEveryRecoveredRole()
    {
        var tracks = Enum.GetValues<ShellAudioTrack>();
        Assert.Equal(6, tracks.Length);

        foreach (var track in tracks)
        {
            var path = ShellAudio.GetTrackPath(track);
            Assert.NotNull(path);
            Assert.Contains(
                Path.Combine("assets", "big-picture", "3.00", "audio"),
                path!,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ps5oracle", path!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Downloads", path!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void InitialBootPolicyDoesNotSubstituteColdBootCue()
    {
        var root = Path.Combine(Path.GetTempPath(), $"prosperismo-initialboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "sfx_coldboot.at9"), [0]);
            var resolved = BootIntroPolicy.ResolveAsset(
                configured: null,
                environmentValue: null,
                searchDirectories: [root],
                fileNames: BootIntroPolicy.AudioFileNames);

            Assert.Null(resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InitialBootResolutionUsesThePackagedCueByDefault()
    {
        var path = BootIntroPolicy.ResolveAudioPath(new GuiSettings());

        Assert.NotNull(path);
        Assert.EndsWith("sfx_initialboot.wav", path!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            Path.Combine("assets", "big-picture", "3.00", "audio"),
            path!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecoveredTransitionDecodesToStereoWithoutAnAudioDevice()
    {
        var directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "assets", "big-picture", "3.00", "audio"));
        var clip = PcmWaveMusic.TryDecode(
            Path.Combine(directory, "sfx_transition.wav"),
            gain: 1f,
            forLooping: false);

        Assert.NotNull(clip);
        Assert.True(clip!.FrameCount > 1);
        Assert.Equal(0, clip.Samples.Length % UiSoundPlayer.MixChannels);
        Assert.Contains(clip.Samples, sample => sample != 0);
    }

    [Fact]
    public void HomeBedPreservesTheAuthoredFirmwareLoop()
    {
        var directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "assets", "big-picture", "3.00", "audio"));
        var clip = PcmWaveMusic.TryDecode(
            Path.Combine(directory, "bgm_home.wav"),
            gain: 1f,
            forLooping: true);

        Assert.NotNull(clip);
        Assert.Equal(278_612, clip!.LoopStartFrame);
        Assert.Equal(9_417_257, clip.LoopEndFrame);
        Assert.Equal(clip.FrameCount, clip.LoopEndFrame);
    }

    [Fact]
    public void OnboardingBedPreservesItsIntroAndAuthoredFirmwareLoop()
    {
        var directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "assets", "big-picture", "3.00", "audio"));
        var clip = PcmWaveMusic.TryDecode(
            Path.Combine(directory, "bgm_onboarding.wav"),
            gain: 1f,
            forLooping: true);

        Assert.NotNull(clip);
        Assert.Equal(1_298_113, clip!.LoopStartFrame);
        Assert.Equal(6_579_824, clip.LoopEndFrame);
        Assert.Equal(clip.FrameCount, clip.LoopEndFrame);
    }

    [Fact]
    public void NotificationToastCuesUseTheAuthoritativeRcoEntryNames()
    {
        Assert.Equal(
            "snd_informative_toasts_something_to_read",
            ShellUiSounds.EntryNames[UiSoundEvent.InformativeToast]);
        Assert.Equal(
            "snd_interactive_toasts_something_to_do",
            ShellUiSounds.EntryNames[UiSoundEvent.InteractiveToast]);
    }

    [Fact]
    public void LocalGameSnd0DecodesWhenPresent()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "games", "PPSA01325-app", "sce_sys", "snd0.at9");
        path = Path.GetFullPath(path);

        if (!File.Exists(path))
        {
            return;
        }

        var info = At9Music.TryReadInfo(path);
        var clip = At9Music.TryDecode(path, forLooping: true);

        Assert.NotNull(info);
        Assert.NotNull(clip);
        Assert.True(info!.FrameCount > 0);
        Assert.Contains(clip!.Samples, sample => sample != 0);
    }

    [Fact]
    public void TitleResolverFindsSnd0RegardlessOfRequestedCase()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"prosperismo-snd0-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var actual = Path.Combine(directory, "SND0.AT9");
            File.WriteAllBytes(actual, [0]);

            var resolved = SndPreviewPlayer.ResolveSnd0Path(Path.Combine(directory, "snd0.at9"));
            Assert.NotNull(resolved);
            Assert.Equal(actual, resolved, ignoreCase: true);
            Assert.Equal("snd0.at9", Path.GetFileName(resolved), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MacCoreAudioOpensForTheDecodedTitleClipWhenAvailable()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "games", "PPSA01325-app", "sce_sys", "snd0.at9");
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            return;
        }

        var clip = At9Music.TryDecode(path, gain: 0.01f, forLooping: true);
        Assert.NotNull(clip);

        UiSoundPlayer.StopAll();
        UiSoundPlayer.ClearMusic();
        CoreAudioOutput.ResetStatus();
        UiSoundPlayer.SetMusic(MusicVoiceKind.Title, clip, 0.01f);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (CoreAudioOutput.LastStatus == int.MinValue && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.Equal(0, CoreAudioOutput.LastStatus);
        }
        finally
        {
            UiSoundPlayer.ClearMusic();
            UiSoundPlayer.StopAll();
        }
    }
}
