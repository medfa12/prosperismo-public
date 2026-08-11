// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Media;

namespace Prosperismo.GUI.SystemAssets.Shell;

/// <summary>
/// Values written by 4.03 <c>BackgroundTransitionParam.TransitionType</c>.
/// </summary>
public enum ShellLayerBackgroundTransitionType
{
    Invalid = -1,
    LaunchingGame = 0,
    Hide = 1,
    LaunchingGameBackwardCompatible = 2,
    SystemDefault = 5,
    CustomImageRipple = 6,
    CustomImageSlideInLeft = 7,
    CustomImageSlideInRight = 8,
    CustomImageFade = 9,
    CustomImageRippleBack = 10,
}

/// <summary>
/// Values packed into bits 16..23 of the native background-transition type.
/// </summary>
public enum ShellLayerBackgroundTransitionDegree
{
    CrossFade = 0,
    Subtle = 1,
    Normal = 2,
    Strong = 3,
}

/// <summary>
/// Native basemat request. Null colour and duration select the 4.03 defaults:
/// linear RGB (2,4,8)/255 and 1000 ms.
/// </summary>
public readonly record struct ShellLayerBasematRequest(
    ShellBasematType Type,
    Color? Color = null,
    TimeSpan? Duration = null);

/// <summary>
/// title data; this record never copies their payload into the repository.
/// </summary>
public sealed record ShellLayerBackgroundTransition(
    ShellLayerBackgroundTransitionType TransitionType,
    string? NextImagePath = null,
    string? NextBlurImagePath = null,
    string? NextFallbackImagePath = null,
    string? NextOverlayImagePath = null,
    ShellLayerBackgroundTransitionDegree Degree = ShellLayerBackgroundTransitionDegree.Strong,
    ShellLayerBasematRequest? Basemat = null,
    Point? TransitionPoint = null);
