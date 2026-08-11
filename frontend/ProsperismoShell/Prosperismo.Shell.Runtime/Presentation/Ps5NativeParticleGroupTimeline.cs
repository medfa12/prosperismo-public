// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.Libs.Presentation;

/// <summary>
/// groups used by the PS5 cold-boot background.
/// </summary>
public sealed class Ps5NativeParticleGroupTimeline
{
    public const float PreviousGroupGraceSeconds = 7.0f;

    private const float TransitionScale = 0.6666666865f;
    private const float TransitionCompletion = 1.5f;
    private readonly float[] _groupStarts = new float[2];
    private readonly float[] _groupEnds = [float.MaxValue, 0.0f];

    public int ActiveGroup { get; private set; }

    public int PatternSelector { get; private set; }

    public int RawState { get; private set; } = 1;

    public bool TransitionActive { get; private set; }

    public float TransitionWeight { get; private set; } = 1.0f;

    public float GetGroupStart(int group) => _groupStarts[ValidateGroup(group)];

    public float GetGroupEnd(int group) => _groupEnds[ValidateGroup(group)];

    public float GetActiveLocalTime(float globalTime) =>
        globalTime - _groupStarts[ActiveGroup];

    /// <summary>
    /// Mirrors the selector-change portion of the native state setter: the
    /// newly active group starts now while the old group receives either the
    /// native seven-second retirement interval or an immediate end.
    /// </summary>
    public void SelectPattern(
        int selector,
        float globalTime,
        bool keepPreviousGroupAlive = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(selector);
        ValidateTime(globalTime);
        if (selector == PatternSelector)
        {
            return;
        }

        var previousGroup = ActiveGroup;
        ActiveGroup ^= 1;
        PatternSelector = selector;
        _groupStarts[ActiveGroup] = globalTime;
        _groupEnds[ActiveGroup] = float.MaxValue;
        _groupEnds[previousGroup] = keepPreviousGroupAlive
            ? globalTime + PreviousGroupGraceSeconds
            : globalTime;
    }

    /// <summary>
    /// Applies the six-entry selector/weight tables used by native function
    /// 0x97560. Numeric state names are retained until call sites establish
    /// authoritative semantic names.
    /// </summary>
    public void ApplyRawState(int rawState, float globalTime)
    {
        ValidateTime(globalTime);
        var index = rawState - 1;
        if ((uint)index >= 6u)
        {
            throw new ArgumentOutOfRangeException(nameof(rawState));
        }

        ReadOnlySpan<int> selectors = [1, 1, 0, 0, 1, 1];
        ReadOnlySpan<float> weights = [1.0f, 1.0f, 0.0f, 0.0f, 1.0f, 1.0f];
        RawState = rawState;
        TransitionActive = rawState is 3 or 4;
        TransitionWeight = weights[index];
        SelectPattern(selectors[index], globalTime);
    }

    /// <summary>
    /// Mirrors native function 0x96ff0. State 3 waits 3.5 seconds and state 4
    /// waits 0.5 seconds; both then ramp at 2/3 per second and complete only
    /// after the phase is strictly greater than 1.5.
    /// </summary>
    public void UpdateTransition(float globalTime)
    {
        ValidateTime(globalTime);
        if (!TransitionActive || RawState is not (3 or 4))
        {
            return;
        }

        var delay = RawState == 3 ? 3.5f : 0.5f;
        var phase = globalTime - (_groupStarts[ActiveGroup] + delay) - 1.0f;
        if (phase > TransitionCompletion)
        {
            RawState = 1;
            TransitionActive = false;
            TransitionWeight = 1.0f;
            SelectPattern(1, globalTime);
            return;
        }

        TransitionWeight = phase > 0.0f ? phase * TransitionScale : 0.0f;
    }

    /// <summary>
    /// The native renderer runs a group while time + step is less than or
    /// equal to its end. The comparison is deliberately inclusive.
    /// </summary>
    public bool ShouldRunGroup(int group, float globalTime, float timeStep)
    {
        var end = _groupEnds[ValidateGroup(group)];
        ValidateTime(globalTime);
        if (!float.IsFinite(timeStep) || timeStep < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(timeStep));
        }

        return globalTime + timeStep <= end;
    }

    public static Ps5NativeParticleGroupTimeline CreateColdBootLargeGroups()
    {
        var timeline = new Ps5NativeParticleGroupTimeline();
        timeline.SelectPattern(1, 6.0f);
        return timeline;
    }

    private static int ValidateGroup(int group)
    {
        if ((uint)group >= 2u)
        {
            throw new ArgumentOutOfRangeException(nameof(group));
        }

        return group;
    }

    private static void ValidateTime(float time)
    {
        if (!float.IsFinite(time) || time < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(time));
        }
    }
}
