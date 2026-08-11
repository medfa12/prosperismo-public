// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI.SystemAssets.Audio;

/// <summary>
/// One endlessly looping music bed inside <see cref="UiSoundPlayer"/>'s mixer.
///
/// It differs from the one-shot voices in three ways that the ambient bed needs:
/// it wraps at a loop point instead of retiring, its gain follows a target
/// rather than being fixed, and it moves toward that target one sample at a
/// time so a duck or a fade is inaudible as a step. Ducking is therefore just
/// <see cref="TargetGain"/> going down; nothing restarts and the bed keeps its
/// place, so coming back out of a duck resumes mid-phrase like the console does.
///
/// The class is deliberately free of any device or platform dependency: the
/// whole of its behaviour is <see cref="Mix"/> writing into a caller-supplied
/// accumulator, which is what lets the loop and duck rules be tested without
/// opening an audio device.
/// </summary>
public sealed class MusicVoice
{
    /// <summary>
    /// Time a gain change takes to complete. Long enough that a duck reads as a
    /// fade rather than a cut, short enough that the bed is out of the way
    /// before a title preview has said anything.
    /// </summary>
    public const double RampSeconds = 0.35;

    private readonly short[] _samples;
    private readonly int _loopStart;
    private readonly int _loopEnd;
    private readonly float _rampStep;

    private int _position;
    private float _gain;
    private float _targetGain;

    /// <summary>Wraps a decoded stereo bed. Loop bounds outside the buffer are clamped.</summary>
    /// <param name="samples">Interleaved stereo PCM16 at <see cref="UiSoundPlayer.MixSampleRate"/>.</param>
    /// <param name="loopStartFrame">Frame to jump back to on wrap.</param>
    /// <param name="loopEndFrame">Frame to wrap at, exclusive.</param>
    /// <param name="initialGain">Starting gain; 0 makes the bed fade in.</param>
    public MusicVoice(
        short[] samples,
        int loopStartFrame,
        int loopEndFrame,
        float initialGain = 0f,
        string name = "")
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length < UiSoundPlayer.MixChannels)
        {
            throw new ArgumentException("A music bed needs at least one frame.", nameof(samples));
        }

        _samples = samples;
        int frames = samples.Length / UiSoundPlayer.MixChannels;

        _loopEnd = loopEndFrame is > 0 && loopEndFrame <= frames ? loopEndFrame : frames;
        _loopStart = loopStartFrame >= 0 && loopStartFrame < _loopEnd ? loopStartFrame : 0;
        _gain = SanitizeGain(initialGain);
        _targetGain = _gain;
        _rampStep = (float)(1.0 / (RampSeconds * UiSoundPlayer.MixSampleRate));
        Name = name;
    }

    /// <summary>Gain the voice is moving toward. Clamped to 0..1.</summary>
    public float TargetGain
    {
        get => _targetGain;
        set => _targetGain = SanitizeGain(value);
    }

    /// <summary>Gain the voice is currently at.</summary>
    public float Gain => _gain;

    /// <summary>Diagnostic source label for this voice.</summary>
    public string Name { get; }

    /// <summary>Playback position in frames.</summary>
    public int PositionFrames => _position;

    /// <summary>First frame of the loop body.</summary>
    public int LoopStartFrame => _loopStart;

    /// <summary>Frame the voice wraps at, exclusive.</summary>
    public int LoopEndFrame => _loopEnd;

    /// <summary>True once the voice has faded out and is not asked back.</summary>
    public bool IsSilent => _gain <= 0f && _targetGain <= 0f;

    /// <summary>Jumps back to the start of the bed, before the loop body.</summary>
    public void Rewind() => _position = 0;

    /// <summary>
    /// Adds this voice into an interleaved stereo accumulator, advancing the
    /// ramp and wrapping at the loop point. Returns true when it contributed
    /// anything, which is what tells the mixer the device is still needed.
    /// </summary>
    /// <param name="accumulator">Interleaved stereo scratch, summed into.</param>
    public bool Mix(int[] accumulator)
    {
        if (accumulator is null || accumulator.Length < UiSoundPlayer.MixChannels)
        {
            return false;
        }

        // A voice that is silent and staying silent still holds its place, but
        // costs nothing and lets the device go idle.
        if (IsSilent)
        {
            return false;
        }

        int frames = accumulator.Length / UiSoundPlayer.MixChannels;
        for (int frame = 0; frame < frames; frame++)
        {
            AdvanceGain();

            if (_position >= _loopEnd)
            {
                _position = _loopStart;
            }

            int source = _position * UiSoundPlayer.MixChannels;
            int destination = frame * UiSoundPlayer.MixChannels;
            for (int channel = 0; channel < UiSoundPlayer.MixChannels; channel++)
            {
                accumulator[destination + channel] += (int)(_samples[source + channel] * _gain);
            }

            _position++;
        }

        return true;
    }

    private void AdvanceGain()
    {
        if (_gain < _targetGain)
        {
            _gain = Math.Min(_gain + _rampStep, _targetGain);
        }
        else if (_gain > _targetGain)
        {
            _gain = Math.Max(_gain - _rampStep, _targetGain);
        }
    }

    private static float SanitizeGain(float value)
    {
        return float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
    }
}
