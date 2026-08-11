// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using Prosperismo.GUI;
using Prosperismo.GUI.SystemAssets.Audio;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class AudioArbitrationTests
{
    [Fact]
    public void AmbientAndTitleUseIndependentMusicSlots()
    {
        if (!UiSoundPlayer.IsSupported)
        {
            return;
        }

        try
        {
            UiSoundPlayer.ClearMusic();
            UiSoundPlayer.SetMusic(MusicVoiceKind.Ambient, SilentClip("ambient"), 0f);
            UiSoundPlayer.SetMusic(MusicVoiceKind.Title, SilentClip("title"), 0f);

            Assert.Equal("ambient", UiSoundPlayer.GetMusicName(MusicVoiceKind.Ambient));
            Assert.Equal("title", UiSoundPlayer.GetMusicName(MusicVoiceKind.Title));
            Assert.Equal(0f, UiSoundPlayer.GetMusicGain(MusicVoiceKind.Ambient));
            Assert.Equal(0f, UiSoundPlayer.GetMusicGain(MusicVoiceKind.Title));

            UiSoundPlayer.ClearMusic(MusicVoiceKind.Title);
            Assert.Equal("ambient", UiSoundPlayer.GetMusicName(MusicVoiceKind.Ambient));
            Assert.Null(UiSoundPlayer.GetMusicName(MusicVoiceKind.Title));
        }
        finally
        {
            UiSoundPlayer.ClearMusic();
        }
    }

    [Fact]
    public void OnboardingHasItsOwnSlotAndCanBeClearedWithoutTouchingAmbient()
    {
        if (!UiSoundPlayer.IsSupported)
        {
            return;
        }

        try
        {
            UiSoundPlayer.ClearMusic();
            UiSoundPlayer.SetMusic(MusicVoiceKind.Ambient, SilentClip("ambient"), 0f);
            UiSoundPlayer.SetMusic(MusicVoiceKind.Onboarding, SilentClip("onboarding"), 0f);

            Assert.Equal("ambient", UiSoundPlayer.GetMusicName(MusicVoiceKind.Ambient));
            Assert.Equal("onboarding", UiSoundPlayer.GetMusicName(MusicVoiceKind.Onboarding));

            UiSoundPlayer.ClearMusic(MusicVoiceKind.Onboarding);
            Assert.Equal("ambient", UiSoundPlayer.GetMusicName(MusicVoiceKind.Ambient));
            Assert.Null(UiSoundPlayer.GetMusicName(MusicVoiceKind.Onboarding));
        }
        finally
        {
            UiSoundPlayer.ClearMusic();
        }
    }

    [Fact]
    public void DesktopPresentationCannotStartSonyAmbient()
    {
        try
        {
            ShellAmbientMusic.Reset();
            ShellAmbientMusic.SetSonyPresentationActive(false);
            ShellAmbientMusic.Start();

            Assert.False(ShellAmbientMusic.IsSonyPresentationActive);
            Assert.False(ShellAmbientMusic.IsLoaded);
            Assert.Null(UiSoundPlayer.GetMusicName(MusicVoiceKind.Ambient));
        }
        finally
        {
            ShellAmbientMusic.Reset();
        }
    }

    [Fact]
    public async Task OlderTitleDecodeCannotReplaceNewerTitle()
    {
        if (!UiSoundPlayer.IsSupported)
        {
            return;
        }

        var oldStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            UiSoundPlayer.ClearMusic();
            ShellAmbientMusic.Reset();

            var player = new SndPreviewPlayer(path =>
            {
                if (path == "old")
                {
                    oldStarted.TrySetResult(true);
                    releaseOld.Task.GetAwaiter().GetResult();
                    return SilentClip("old");
                }

                return SilentClip("new");
            });

            player.Play("old");
            Assert.True(await WaitUntil(() => oldStarted.Task.IsCompleted));

            player.Play("new");
            Assert.True(await WaitUntil(() => UiSoundPlayer.GetMusicName(MusicVoiceKind.Title) == "new"));

            releaseOld.TrySetResult(true);
            await Task.Delay(50);

            Assert.Equal("new", UiSoundPlayer.GetMusicName(MusicVoiceKind.Title));
            Assert.True(ShellAmbientMusic.IsTitleMusicActive);
        }
        finally
        {
            releaseOld.TrySetResult(true);
            UiSoundPlayer.ClearMusic();
            ShellAmbientMusic.Reset();
        }
    }

    [Fact]
    public async Task FailedReplacementClearsPreviousTitleAndLeavesAmbientUnducked()
    {
        if (!UiSoundPlayer.IsSupported)
        {
            return;
        }

        try
        {
            UiSoundPlayer.ClearMusic();
            ShellAmbientMusic.Reset();
            ShellAmbientMusic.Start();
            UiSoundPlayer.SetMusic(MusicVoiceKind.Ambient, SilentClip("ambient"), 0.65f);

            var player = new SndPreviewPlayer(path =>
                path == "working" ? SilentClip("working") : null);

            player.Play("working");
            Assert.True(await WaitUntil(() => UiSoundPlayer.GetMusicName(MusicVoiceKind.Title) == "working"));
            Assert.True(ShellAmbientMusic.IsTitleMusicActive);
            Assert.Equal(0f, UiSoundPlayer.GetMusicGain(MusicVoiceKind.Ambient));

            player.Play("broken");

            // The previous title is removed synchronously, before broken.at9
            // has even finished its debounce/decode path.
            Assert.Null(UiSoundPlayer.GetMusicName(MusicVoiceKind.Title));
            Assert.False(ShellAmbientMusic.IsTitleMusicActive);
            Assert.True(await WaitUntil(() => UiSoundPlayer.GetMusicName(MusicVoiceKind.Title) is null));
            Assert.Equal(0.65f, UiSoundPlayer.GetMusicGain(MusicVoiceKind.Ambient));
        }
        finally
        {
            UiSoundPlayer.ClearMusic();
            ShellAmbientMusic.Reset();
        }
    }

    private static MusicClip SilentClip(string name) =>
        new(new short[8], 0, 4, name);

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(4))
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }
}
