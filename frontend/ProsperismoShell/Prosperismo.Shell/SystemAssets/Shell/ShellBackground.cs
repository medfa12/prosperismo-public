// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Prosperismo.GUI.Ps5Home;

namespace Prosperismo.GUI.SystemAssets.Shell;

/// <summary>
///
/// <para>Bottom to top the stack is the flat basemat field at its measured
/// colour, the translated native full-screen wave plate or focused title art,
/// a user-generated native particle frame, and an optional mat. The native
/// produced one; no substitute particle animation is drawn.</para>
///
/// <para><b>What used to be here, and why it is gone.</b> This control hosted a
/// roughly 2,000-line synthesised background - <c>ShellPrismLayer</c>,
/// <c>ShellPrismField</c>, <c>ShellPrismCompositor</c>, <c>ShellPrismPalette</c>,
/// plus <c>ShellParticleLayer</c> and <c>ShellWaveLayer</c> - a particle and wave
/// simulation over a hand-authored palette. None of it was recovered from
/// anything. It has been deleted rather than disabled, along with the capture
/// tool built to grade it, and it must not come back. The values that would make
/// a live background real are the plate uniforms (audit M1), the animated
/// shaders (M2) and the wave mesh (M3). That audit is superseded. The product
/// cold-boot/HOME route targets NPXS40087 12.40's continuously executing
/// BGLayer particle target and <c>rect_uv_vv + light_p</c> room compositor.
/// FirstWave's separately recovered <c>fw_background_p</c> plate remains a
/// research pass while its fixed-function relationship to BGLayer is
/// unresolved. Older CPU plate evaluators and rendered
/// particle caches remain research artifacts only; neither is promoted to a
///
/// <para>Two more inventions went with it. The 26 s drift that scaled the plate
/// to 1.06 and slid it around: motion nobody measured, and a second way to crop
/// <em>separately authored</em> <c>BackgroundBlurImage&lt;n&gt;</c> texture, and
/// box-blurring the sharp plate to stand in for it was a guess at what that
/// texture looks like.</para>
///
/// <para>The plate itself is <see cref="Ps5BackgroundPlate"/>, which fits it
/// whole rather than cropping it. Nothing here throws, nothing blocks the UI
/// thread, and the control ignores pointer input so it can sit behind
/// interactive content.</para>
///
/// <para>See <c>docs/ps5-background-native.md</c> and
/// <c>docs/ps5-shell-recovery-audit.md</c> §5 for the build order this follows.</para>
/// </summary>
public sealed class ShellBackground : Panel
{
    /// <summary>
    /// Title id whose hub background to show; null, "" or "default" select the
    /// hub default. See <seealso cref="SetBackground"/>.
    /// </summary>
    public static readonly StyledProperty<string?> TitleIdProperty =
        AvaloniaProperty.Register<ShellBackground, string?>(nameof(TitleId));

    /// <summary>
    /// Optional title-owned art path. This is forwarded to the plate inside
    /// the native composite so callers do not have to bypass the compositor.
    /// </summary>
    public static readonly StyledProperty<string?> TitleArtPathProperty =
        AvaloniaProperty.Register<ShellBackground, string?>(nameof(TitleArtPath));

    /// <summary>
    /// Enables background motion.
    ///
    /// <para>The console's background motion belongs to the native background
    /// renderer. Disabling this property pauses and hides the in-process native
    /// evaluator. Rendered media is not substituted.</para>
    /// </summary>
    public static readonly StyledProperty<bool> IsMotionEnabledProperty =
        AvaloniaProperty.Register<ShellBackground, bool>(nameof(IsMotionEnabled), defaultValue: true);

    /// <summary>
    /// Enables the older recovered Plane2 / <c>wave_bg_p</c> research plate.
    /// cold-boot/HOME target is NPXS40087 12.40's live BGLayer
    /// <c>particle target -&gt; light_p</c> path, not a 480x270 CPU evaluation
    /// </summary>
    public static readonly StyledProperty<bool> UseNativeWavePlateProperty =
        AvaloniaProperty.Register<ShellBackground, bool>(
            nameof(UseNativeWavePlate), defaultValue: false);

    /// <summary>
    /// Home this is native state 31 and authored record 13.
    /// </summary>
    public static readonly StyledProperty<bool> HighContrastProperty =
        AvaloniaProperty.Register<ShellBackground, bool>(nameof(HighContrast));

    /// <summary>Managed <c>BackgroundLayerState.ThemeColourIndex</c>.</summary>
    public static readonly StyledProperty<int> ThemeColourIndexProperty =
        AvaloniaProperty.Register<ShellBackground, int>(
            nameof(ThemeColourIndex), Ps5NativeWavePlateEvaluator.SteadyNoParticleThemeIndex);

    /// <summary>
    /// through this one enum; here it selects only whether the composite is
    /// drawn at all. Steady home is
    /// <see cref="ShellGlobalBackgroundState.NoParticle"/>.
    /// </summary>
    public static readonly StyledProperty<ShellGlobalBackgroundState> GlobalStateProperty =
        AvaloniaProperty.Register<ShellBackground, ShellGlobalBackgroundState>(
            nameof(GlobalState), defaultValue: ShellGlobalBackgroundState.NoParticle);

    /// <summary>
    /// Basemat shape laid under the UI. <see cref="ShellBasematType.None"/> by
    /// default, and that default is load-bearing: the shell's real mat is
    /// per-tile and distance-driven (<c>useMat</c>, HOME:41601-41626, ported as
    /// <see cref="Ps5HomeMetrics.MatOpacityForOffset"/>), not a full-frame wash.
    /// The <see cref="ShellBasematType.Linear"/> and
    /// <see cref="ShellBasematType.Ellipse"/> ramps approximate a native effect
    /// and dim the plate by an amount nobody measured, so they are opt-in only.
    /// </summary>
    public static readonly StyledProperty<ShellBasematType> BasematTypeProperty =
        AvaloniaProperty.Register<ShellBackground, ShellBasematType>(
            nameof(BasematType), defaultValue: ShellBasematType.None);

    /// <summary>
    /// Rectangle of the focused item, in this control's coordinates.
    ///
    /// <para><b>Seam, deliberately inert.</b> The console's background does react
    /// to focus, but the reaction belongs to the focus-highlight recovery that is
    /// still in flight, and the per-type/per-degree transition timings behind it
    /// are unread native code (audit M4). So the shell keeps reporting focus here
    /// and the background does nothing with it yet. When the curves are measured
    /// they attach at this one property.</para>
    /// </summary>
    public static readonly StyledProperty<Rect> FocusRectProperty =
        AvaloniaProperty.Register<ShellBackground, Rect>(nameof(FocusRect));

    private readonly Border _clearPlate;
    private readonly Ps5NativeWavePlate _nativeWave;
    private readonly Ps5BackgroundPlate _plate;
    private readonly Ps5NativeBackgroundLayer _nativeParticles;
    private readonly Border _basemat;
    private bool _hasLayerImageSelection;
    private Color _basematColor = ShellBackgroundComposition.BasematColor;
    private TimeSpan _basematDuration = TimeSpan.FromMilliseconds(
        ShellBackgroundComposition.BasematDurationMilliseconds);

    public ShellBackground()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;

        // 1. The field under the plate: BGTransition's BasematDefaultColor.
        //    It is what a plate with alpha composites over and is not an
        //    impression of the artwork.
        _clearPlate = new Border
        {
            Background = new SolidColorBrush(ShellBackgroundComposition.BasematColor),
        };

        //    BackgroundLayerState writes HomeScreen (4) at +0x0c and the
        //    native preset map selects record 2. Its noise phase advances once
        //    per rendered frame.
        _nativeWave = new Ps5NativeWavePlate
        {
            MotionEnabled = IsMotionEnabled,
            HighContrast = HighContrast,
            ThemeColourIndex = ThemeColourIndex,
            IsVisible = UseNativeWavePlate,
        };

        // 3. Title art, fitted whole. It cross-fades on a title change over
        //    TransitionVariety.LinearPoint4Sec - measured, and Linear, because
        //    none of the shell's chrome transitions ease.
        _plate = new Ps5BackgroundPlate
        {
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = Ps5Transitions.LinearPoint4Sec,
                    Easing = Ps5Transitions.Linear,
                },
            },
        };
        _plate.PropertyChanged += (_, e) =>
        {
            if (e.Property == Ps5BackgroundPlate.IsPlateLoadedProperty)
            {
                ApplyGlobalState(GlobalState);
            }
            else if (e.Property == Ps5BackgroundPlate.IsTitleArtProperty)
            {
                ApplyGlobalState(GlobalState);
            }
        };

        // persistent particle state, feeds that target to light_p, and returns
        // the live room. A rendered PNG/MP4 sequence is not a valid shell
        // background source.
        _nativeParticles = new Ps5NativeBackgroundLayer
        {
            GlobalState = GlobalState,
            MotionEnabled = IsMotionEnabled,
        };

        // 5. The basemat, under the UI. Off by default (see BasematTypeProperty);
        //    when a host does opt in, it cross-fades over BGTransition's measured
        //    1 s.
        _basemat = new Border
        {
            Background = ShellBackgroundComposition.CreateBasematBrush(
                ShellBasematType.None, ShellBackgroundComposition.BasematColor),
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(
                        ShellBackgroundComposition.BasematDurationMilliseconds),
                },
            },
        };

        Children.Add(_clearPlate);
        Children.Add(_nativeWave);
        Children.Add(_plate);
        Children.Add(_nativeParticles);
        Children.Add(_basemat);

        if (string.Equals(
                Environment.GetEnvironmentVariable(Ps5NativeBackgroundLayer.PreviewEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            // Explicit developer/user preview only. Normal product routing
            // continues to own GlobalState.
            GlobalState = ShellGlobalBackgroundState.ColdBootAnimation;
        }

        ApplyGlobalState(GlobalState);
    }

    /// <inheritdoc cref="TitleIdProperty"/>
    public string? TitleId
    {
        get => GetValue(TitleIdProperty);
        set => SetValue(TitleIdProperty, value);
    }

    /// <inheritdoc cref="TitleArtPathProperty"/>
    public string? TitleArtPath
    {
        get => GetValue(TitleArtPathProperty);
        set => SetValue(TitleArtPathProperty, value);
    }

    /// <inheritdoc cref="IsMotionEnabledProperty"/>
    public bool IsMotionEnabled
    {
        get => GetValue(IsMotionEnabledProperty);
        set => SetValue(IsMotionEnabledProperty, value);
    }

    /// <inheritdoc cref="UseNativeWavePlateProperty"/>
    public bool UseNativeWavePlate
    {
        get => GetValue(UseNativeWavePlateProperty);
        set => SetValue(UseNativeWavePlateProperty, value);
    }

    /// <inheritdoc cref="HighContrastProperty"/>
    public bool HighContrast
    {
        get => GetValue(HighContrastProperty);
        set => SetValue(HighContrastProperty, value);
    }

    /// <inheritdoc cref="ThemeColourIndexProperty"/>
    public int ThemeColourIndex
    {
        get => GetValue(ThemeColourIndexProperty);
        set => SetValue(ThemeColourIndexProperty, value);
    }

    /// <inheritdoc cref="GlobalStateProperty"/>
    public ShellGlobalBackgroundState GlobalState
    {
        get => GetValue(GlobalStateProperty);
        set => SetValue(GlobalStateProperty, value);
    }

    /// <inheritdoc cref="BasematTypeProperty"/>
    public ShellBasematType BasematType
    {
        get => GetValue(BasematTypeProperty);
        set => SetValue(BasematTypeProperty, value);
    }

    /// <inheritdoc cref="FocusRectProperty"/>
    public Rect FocusRect
    {
        get => GetValue(FocusRectProperty);
        set => SetValue(FocusRectProperty, value);
    }

    /// <summary>Test seam: the plate this backdrop draws.</summary>
    internal Ps5BackgroundPlate Plate => _plate;

    internal Ps5NativeWavePlate NativeWave => _nativeWave;

    internal Ps5NativeBackgroundLayer NativeParticles => _nativeParticles;

    /// <summary>
    /// Selects Home's full light composition or Settings' base-only
    /// composition without unmounting the persistent native owner.
    /// </summary>
    internal bool ParticleOverlayVisible
    {
        get => _nativeParticles.ParticleOverlayVisible;
        set => _nativeParticles.ParticleOverlayVisible = value;
    }

    /// <summary>Test seam: the basemat mat this backdrop lays under the UI.</summary>
    internal Border Basemat => _basemat;

    /// <summary>
    /// Restarts the native plate clock when the host changes visibility on an
    /// ancestor page container rather than on this control itself.
    /// </summary>
    internal void RefreshAnimationRoute()
    {
        _nativeWave.RefreshAnimationRoute();
        _nativeParticles.RefreshVisibility();
    }

    /// <summary>
    /// <c>NoParticle</c> state. That command has no native particle setter, so
    /// the retained selector-1 allocation and moving <c>light_p</c> room keep
    /// their monotonic clock without selecting <c>ParticleSpread</c> again.
    /// </summary>
    internal bool ContinueAmbientSequence()
    {
        GlobalState = ShellGlobalBackgroundState.NoParticle;
        return _nativeParticles.LiveSource?.SupportsState(GlobalState) == true;
    }

    /// <summary>
    /// Enters NPXS40087's normal cold-boot state and waits until its live
    /// renderer is ready. The source itself owns the selector-0 to selector-1
    /// hand-off and continues as ambient HOME without resetting its clock.
    /// </summary>
    internal async Task<bool> StartColdBootSequenceAsync()
    {
        GlobalState = ShellGlobalBackgroundState.ColdBootAnimation;
        return await _nativeParticles.EnsureLiveSourceAsync(GlobalState);
    }

    /// <summary>
    /// Shows the hub background for a title id (null or "default" for the hub
    /// default). Equivalent to setting <see cref="TitleId"/>.
    /// </summary>
    /// <param name="titleId">System title id, or null for the hub default.</param>
    public void SetBackground(string? titleId)
    {
        TitleId = titleId;
    }

    /// <summary>
    /// The hook the shell calls when focus moves in the tile strand, in this
    /// control's coordinates. Equivalent to setting <see cref="FocusRect"/>, and
    /// currently inert for the reason given there. Kept live so the call sites
    /// stay wired while the focus-highlight curves are being recovered.
    /// </summary>
    /// <param name="focus">Focused item's rectangle, or an empty rect.</param>
    public void NotifyFocusChanged(Rect focus)
    {
        FocusRect = focus;
    }

    /// <summary>
    /// Applies the image/basemat channel used by Legacy application layers.
    /// This deliberately does not touch <see cref="GlobalState"/>, the native
    /// preset, theme index, wave visibility, or particle state.
    ///
    /// pixel shader for the opaque image path. Unsupported variants still
    /// throw instead of being silently replaced with authored host motion.</para>
    /// </summary>
    public void ApplyLayerBackgroundTransition(ShellLayerBackgroundTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        switch (transition.TransitionType)
        {
            case ShellLayerBackgroundTransitionType.SystemDefault:
                _plate.ClearNativeImageFade();
                _hasLayerImageSelection = false;
                _plate.TitleArtPath = null;
                _plate.FallbackArtPath = null;
                break;

            case ShellLayerBackgroundTransitionType.CustomImageFade:
                _plate.ConfigureNativeImageFade(transition.Degree);
                _hasLayerImageSelection =
                    !string.IsNullOrWhiteSpace(transition.NextImagePath) ||
                    !string.IsNullOrWhiteSpace(transition.NextFallbackImagePath);
                _plate.FallbackArtPath = transition.NextFallbackImagePath;
                _plate.TitleArtPath = transition.NextImagePath;
                break;

            case ShellLayerBackgroundTransitionType.CustomImageRipple:
                var point = transition.TransitionPoint ??
                    (FocusRect.Width > 0 && FocusRect.Height > 0
                        ? FocusRect.Center
                        : new Point(960, 540));
                _plate.ConfigureNativeImageRipple(
                    transition.Degree,
                    Math.Clamp(point.X / 1920.0, 0.0, 1.0),
                    Math.Clamp(point.Y / 1080.0, 0.0, 1.0));
                _hasLayerImageSelection =
                    !string.IsNullOrWhiteSpace(transition.NextImagePath) ||
                    !string.IsNullOrWhiteSpace(transition.NextFallbackImagePath);
                _plate.FallbackArtPath = transition.NextFallbackImagePath;
                _plate.TitleArtPath = transition.NextImagePath;
                break;

            default:
                throw new NotSupportedException(
                    $"Native background transition {transition.TransitionType} " +
                    "has not been translated; refusing to substitute host motion.");
        }

        if (transition.Basemat is { } basemat)
        {
            ApplyBasemat(basemat);
        }

        // Re-evaluate only image visibility. The native Plane2 route remains
        // the same and, when enabled, continues rendering underneath it.
        ApplyGlobalState(GlobalState);
    }

    /// <summary>Applies only BGLayer's basemat channel. SystemModalDialog uses
    /// this path so opening a modal cannot disturb the selected image, wave, or
    /// particle preset.</summary>
    public void SetBasemat(ShellLayerBasematRequest request) => ApplyBasemat(request);

    /// <summary>
    /// Host diagnostic: leaves exactly one named layer visible ("base", "image"
    /// or "basemat"), or restores the whole composite when given null or an
    /// unknown name. Used to photograph the stack one layer at a time; it has no
    /// effect on how the shell itself renders.
    /// </summary>
    /// <param name="layer">Layer to isolate, or null for the full composite.</param>
    public void DiagnosticIsolateLayer(string? layer)
    {
        var all = layer is null;
        _clearPlate.IsVisible = all || layer is "base";
        _nativeWave.IsVisible = (all && UseNativeWavePlate) || layer is "wave";
        _plate.IsVisible = (all && !UseNativeWavePlate) || layer is "image";
        if (all)
        {
            _nativeParticles.RefreshVisibility();
        }
        else
        {
            _nativeParticles.IsVisible = layer is "native" && _nativeParticles.IsFrameLoaded;
        }
        _basemat.IsVisible = all || layer is "basemat";
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == GlobalStateProperty)
        {
            ApplyGlobalState(change.GetNewValue<ShellGlobalBackgroundState>());
        }
        else if (change.Property == BasematTypeProperty)
        {
            _basemat.Background = ShellBackgroundComposition.CreateBasematBrush(
                change.GetNewValue<ShellBasematType>(), _basematColor);
        }
        else if (change.Property == TitleIdProperty)
        {
            _plate.TitleId = change.GetNewValue<string?>();
            ApplyGlobalState(GlobalState);
        }
        else if (change.Property == TitleArtPathProperty)
        {
            _plate.TitleArtPath = change.GetNewValue<string?>();
            ApplyGlobalState(GlobalState);
        }
        else if (change.Property == IsMotionEnabledProperty)
        {
            var enabled = change.GetNewValue<bool>();
            _nativeWave.MotionEnabled = enabled;
            _nativeParticles.MotionEnabled = enabled;
        }
        else if (change.Property == UseNativeWavePlateProperty)
        {
            ApplyGlobalState(GlobalState);
        }
        else if (change.Property == HighContrastProperty)
        {
            _nativeWave.HighContrast = change.GetNewValue<bool>();
        }
        else if (change.Property == ThemeColourIndexProperty)
        {
            _nativeWave.ThemeColourIndex = change.GetNewValue<int>();
        }
    }

    // black states, into a background it simply does not draw.
    //
    // The recovered coldboot particle pattern has a native compute/draw path.
    // Other serialized patterns remain absent until their resource records are
    // executable; substituting coldboot for them would be visibly incorrect.
    private void ApplyGlobalState(ShellGlobalBackgroundState state)
    {
        var mode = ShellBackgroundComposition.LightModeFor(state);
        var draws = ShellBackgroundComposition.DrawsBackground(mode);
        var hasTitleSelection = !string.IsNullOrWhiteSpace(TitleArtPath) ||
            _hasLayerImageSelection ||
            (!string.IsNullOrWhiteSpace(TitleId) &&
             !string.Equals(TitleId, "default", StringComparison.OrdinalIgnoreCase));
        // A requested title path is not proof that a title-owned plate is
        // actually on screen. Failed/late artwork resolution used to suppress
        // the live room indefinitely, leaving only the dark fallback after the
        // outgoing art faded. Keep ownership with the title only while the
        // plate reports a decoded title image; otherwise the native room and
        // its continuously advancing particles remain visible.
        var titleOwnsBackdrop = TitleOwnsBackdrop(
            hasTitleSelection,
            _plate.IsPlateLoaded,
            _plate.IsTitleArt);
        var drawNativeHomePlate = UseNativeWavePlate;
        _nativeWave.IsVisible = draws && drawNativeHomePlate;
        _plate.IsVisible = draws && hasTitleSelection;
        _nativeParticles.GlobalState = state;
        _nativeParticles.IsSuppressed = ShouldSuppressNativeParticles(state, titleOwnsBackdrop);
        _clearPlate.Background = draws
            ? new SolidColorBrush(ShellBackgroundComposition.BasematColor)
            : Brushes.Black;
    }

    /// <summary>
    /// A path request alone must not take ownership away from the live room:
    /// only decoded title-owned artwork may suppress the native background.
    /// </summary>
    internal static bool TitleOwnsBackdrop(
        bool hasTitleSelection,
        bool isPlateLoaded,
        bool isTitleArt) =>
        hasTitleSelection && isPlateLoaded && isTitleArt;

    /// <summary>
    /// Cold boot temporarily owns the full background even when the library
    /// has already selected and decoded a game's artwork. Once HOME takes the
    /// retained ambient state, normal title-art ownership resumes.
    /// </summary>
    internal static bool ShouldSuppressNativeParticles(
        ShellGlobalBackgroundState state,
        bool titleOwnsBackdrop) =>
        titleOwnsBackdrop && state != ShellGlobalBackgroundState.ColdBootAnimation;

    private void ApplyBasemat(ShellLayerBasematRequest request)
    {
        _basematColor = request.Color ?? ShellBackgroundComposition.BasematColor;
        _basematDuration = request.Duration ?? TimeSpan.FromMilliseconds(
            ShellBackgroundComposition.BasematDurationMilliseconds);

        if (_basematDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), "Basemat duration cannot be negative.");
        }

        BasematType = request.Type;
        _basemat.Background = ShellBackgroundComposition.CreateBasematBrush(
            request.Type, _basematColor);
        _basemat.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = _basematDuration,
            },
        };
    }
}
