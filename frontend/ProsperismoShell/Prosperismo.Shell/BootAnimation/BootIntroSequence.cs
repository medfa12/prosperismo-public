// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI.BootAnimation;

/// <summary>Where the boot sequence has got to.</summary>
public enum BootIntroState
{
    /// <summary>Started, but nothing has been drawn yet. Nothing to skip and nothing to fade.</summary>
    Waiting,

    /// <summary>A frame is on screen.</summary>
    Playing,

    /// <summary>Ending: the sound is already off and the picture is on its way out.</summary>
    Finishing,

    /// <summary>Gone.</summary>
    Finished,
}

/// <summary>
/// The boot sequence's state machine, kept apart from the control that draws it
/// so the rules can be checked without a window.
///
/// Three of those rules matter. Input only skips once a frame is actually up:
/// before that the overlay is invisible and a keystroke belongs to the shell
/// underneath it. Ending is one-way and happens once, however many skips, end-of
/// -stream notifications and decoder failures arrive together. And an overlay
/// that never drew anything leaves without a fade, so a missing or unplayable
/// movie costs the launch nothing visible at all.
/// </summary>
public sealed class BootIntroSequence
{
    private readonly TimeSpan _hintDelay;

    /// <param name="hintDelay">How long a frame must have been up before the skip hint appears.</param>
    public BootIntroSequence(TimeSpan hintDelay)
    {
        _hintDelay = hintDelay;
    }

    /// <summary>Current state.</summary>
    public BootIntroState State { get; private set; } = BootIntroState.Waiting;

    /// <summary>True once the skip hint has been asked for.</summary>
    public bool IsHintVisible { get; private set; }

    /// <summary>True once the overlay covered the shell. Stays true after the sequence ends.</summary>
    public bool HasContent { get; private set; }

    /// <summary>True while the sequence is still on screen.</summary>
    public bool IsPlaying => State is BootIntroState.Waiting or BootIntroState.Playing;

    /// <summary>
    /// True when ending should fade rather than cut. Nothing was ever visible when
    /// this is false, so there is nothing to fade from.
    /// </summary>
    public bool ShouldFadeOut => HasContent;

    /// <summary>
    /// Records that the overlay is now covering the shell. Returns true only on
    /// the first call, which is when it fades itself in.
    /// </summary>
    public bool NotifyVisible()
    {
        if (State != BootIntroState.Waiting)
        {
            return false;
        }

        State = BootIntroState.Playing;
        HasContent = true;
        return true;
    }

    /// <summary>
    /// Returns true on the single tick where the skip hint should fade in: a frame
    /// is up, the delay has passed, and the sequence is still running.
    /// </summary>
    /// <param name="elapsed">Time since the sequence started.</param>
    public bool TryShowHint(TimeSpan elapsed)
    {
        if (IsHintVisible || State != BootIntroState.Playing || elapsed < _hintDelay)
        {
            return false;
        }

        IsHintVisible = true;
        return true;
    }

    /// <summary>
    /// Ends the sequence in response to input. Returns false, and does nothing,
    /// when nothing is on screen yet or it is already ending.
    /// </summary>
    public bool TrySkip()
    {
        if (State != BootIntroState.Playing)
        {
            return false;
        }

        State = BootIntroState.Finishing;
        IsHintVisible = false;
        return true;
    }

    /// <summary>
    /// Ends the sequence because the movie ran out, the decoder gave up, or it
    /// never produced a frame. Returns false when it is already ending.
    /// </summary>
    public bool TryComplete()
    {
        if (!IsPlaying)
        {
            return false;
        }

        State = BootIntroState.Finishing;
        IsHintVisible = false;
        return true;
    }

    /// <summary>
    /// Marks the overlay as gone. Returns false when that has already happened, so
    /// the caller detaches and raises its completion event exactly once.
    /// </summary>
    public bool TryFinish()
    {
        if (State == BootIntroState.Finished)
        {
            return false;
        }

        State = BootIntroState.Finished;
        IsHintVisible = false;
        return true;
    }
}
