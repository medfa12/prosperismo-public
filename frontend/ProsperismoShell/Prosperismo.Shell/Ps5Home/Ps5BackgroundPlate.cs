// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Prosperismo.GUI.SystemAssets;
using Prosperismo.GUI.SystemAssets.Shell;
using Prosperismo.Libs.Presentation;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// The home background: the <b>focused title's own artwork</b>, whole, at its
/// own aspect ratio, changing through HOME's recovered slide/fade program as
/// the highlight moves.
///
/// <para><b>What the background actually is.</b> The PS5 home background is the
/// artwork of whichever title the highlight is on. It changes with the
/// highlight through HOME's recovered Normal-degree transition selection.
/// <c>bg_hub_default.dds</c> is what its name says — the default for
/// a title that ships no artwork — and against a real library the shell hardly
/// ever opens it. Every title carries its backdrop in
/// <c>&lt;TITLEID&gt;-app/sce_sys/</c>; <see cref="Ps5TitleArtwork"/> resolves
/// which file that is and records the evidence for why it is <c>pic1</c> rather
/// than the <c>pic0</c> everyone guesses.</para>
///
/// <para><b>Fallback order, honestly.</b> Focused title's artwork; failing that
/// a caller-supplied title fallback; failing that the ambient basemat. No
/// procedural plate, no invented gradient, and nothing that dresses an absent
/// asset up as a present one.</para>
///
/// <para><b>The fit.</b> <see cref="Stretch.Uniform"/>, so the entire plate is
/// on screen. The 3840x2160 artwork and the 1920x1080 design canvas are both
/// exactly 16:9, so a uniform fit is also a full-bleed one — there are no bars
/// to explain away. Nothing at all is drawn over the plate: the shell's mat is
/// a <em>per-tile</em> darkening that
/// <see cref="Ps5HomeMetrics.MatOpacityForOffset"/> hands the tile row by
/// distance from the selection, and it is emphatically not a full-frame wash
/// over the background.</para>
///
/// <para><b>Decoding</b> reuses the codebase's one BC7 / DDS DX10 reader through
/// <see cref="ShellBackgroundSource"/>, and Avalonia's own PNG decoder for the
/// two system apps that ship <c>pic1.png</c>. There is exactly one BC7 decoder
/// in this repository and this is not a second one.</para>
/// </summary>
public sealed class Ps5BackgroundPlate : Control
{
    /// <summary>
    /// Title id associated with the current title artwork. It is retained as
    /// presentation state for callers that identify a focused title this way.
    /// </summary>
    public static readonly StyledProperty<string?> TitleIdProperty =
        AvaloniaProperty.Register<Ps5BackgroundPlate, string?>(nameof(TitleId));

    /// <summary>
    /// Absolute path to the focused title's own backdrop, as resolved by
    /// <see cref="Ps5TitleArtwork"/>. This is the normal source of the home
    /// background.
    /// </summary>
    public static readonly StyledProperty<string?> TitleArtPathProperty =
        AvaloniaProperty.Register<Ps5BackgroundPlate, string?>(nameof(TitleArtPath));

    /// <summary>
    /// Optional caller-supplied fallback for <see cref="TitleArtPath"/>. This
    /// corresponds to 4.03 <c>BackgroundTransitionParam.NextFallbackImageUri</c>
    /// and is tried before the system hub-default plate.
    /// </summary>
    public static readonly StyledProperty<string?> FallbackArtPathProperty =
        AvaloniaProperty.Register<Ps5BackgroundPlate, string?>(nameof(FallbackArtPath));

    /// <summary>
    /// True once a real plate is on screen. False means the fallback is
    /// showing, and the host should say why rather than let a flat colour pass
    /// as the console's background.
    /// </summary>
    public static readonly DirectProperty<Ps5BackgroundPlate, bool> IsPlateLoadedProperty =
        AvaloniaProperty.RegisterDirect<Ps5BackgroundPlate, bool>(
            nameof(IsPlateLoaded), static o => o.IsPlateLoaded);

    /// <summary>
    /// True when the plate on screen is the focused title's own artwork rather
    /// than <c>bg_hub_default</c>. Lets a caller tell "the console's real
    /// background" from "the documented fallback" without guessing at pixels.
    /// </summary>
    public static readonly DirectProperty<Ps5BackgroundPlate, bool> IsTitleArtProperty =
        AvaloniaProperty.RegisterDirect<Ps5BackgroundPlate, bool>(
            nameof(IsTitleArt), static o => o.IsTitleArt);

    /// <summary>
    /// The fallback field when no plate can be loaded: the basemat's own
    /// measured colour, <c>(2,4,8)/255</c>, flat and undimmed. It is a measured
    /// </summary>
    public static readonly IBrush FallbackBrush =
        new SolidColorBrush(Ps5HomeMetrics.MatColor);

    /// <summary>
    /// How long the background takes to change when the highlight moves:
    /// <c>TransitionVariety.DefaultAnimationDuration</c>, 0.3 s.
    ///
    /// <para><b>Why this remains.</b> This value is only the fallback for a caller
    /// that does not configure a native image program. HOME and explicit Legacy
    /// custom-image requests configure the recovered per-degree clock and linear
    /// shader progress before changing the image.</para>
    /// </summary>
    public static TimeSpan CrossFadeDuration => Ps5Transitions.Default;

    /// <summary>
    /// The curve the cross-fade runs on: <c>DefaultScreenTransitionCurve</c>,
    /// i.e. <c>EaseSmoothOutBreeze (0.05, 0.4)</c>. See
    /// <see cref="CrossFadeDuration"/> for why this pairing.
    /// </summary>
    public static Ps5AnimationCurve CrossFadeCurve => Ps5AnimationCurve.DefaultScreenTransition;

    private static readonly Ps5PlateCache SharedCache = new();

    private readonly DispatcherTimer _fadeTimer;

    private Bitmap? _plate;
    private Bitmap? _outgoing;
    private TimeSpan _fadeElapsed;
    private TimeSpan _activeFadeDuration = CrossFadeDuration;
    private bool _activeFadeIsNativeLinear;
    private TimeSpan? _configuredNativeFadeDuration;
    private ShellLayerBackgroundTransitionType? _configuredNativeTransitionType;
    private ShellLayerBackgroundTransitionType? _activeNativeTransitionType;
    private ShellLayerBackgroundTransitionDegree _configuredNativeDegree;
    private double _configuredRippleOriginX = 0.5;
    private double _configuredRippleOriginY = 0.5;
    private Bitmap[]? _nativeRippleFrames;
    private string? _nativeRippleFailure;
    private bool _isPlateLoaded;
    private bool _isTitleArt;
    private volatile bool _isImageLoadPending;
    private int _generation;

    public Ps5BackgroundPlate()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;

        // One frame at 60 Hz. The fade is short enough that a dedicated timer is
        // cheaper than standing up a full Avalonia animation, and it keeps the
        // eased opacity in one testable place.
        _fadeTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _fadeTimer.Tick += OnFadeTick;
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

    /// <inheritdoc cref="FallbackArtPathProperty"/>
    public string? FallbackArtPath
    {
        get => GetValue(FallbackArtPathProperty);
        set => SetValue(FallbackArtPathProperty, value);
    }

    /// <inheritdoc cref="IsPlateLoadedProperty"/>
    public bool IsPlateLoaded
    {
        get => _isPlateLoaded;
        private set => SetAndRaise(IsPlateLoadedProperty, ref _isPlateLoaded, value);
    }

    /// <inheritdoc cref="IsTitleArtProperty"/>
    public bool IsTitleArt
    {
        get => _isTitleArt;
        private set => SetAndRaise(IsTitleArtProperty, ref _isTitleArt, value);
    }

    /// <summary>
    /// Selects the recovered native custom-image fade for the next image
    /// change. Ordinary title selection retains its documented fallback until
    /// that caller's native transition arguments are traced.
    /// </summary>
    internal void ConfigureNativeImageFade(ShellLayerBackgroundTransitionDegree degree)
        => ConfigureNativeImageTransition(
            ShellLayerBackgroundTransitionType.CustomImageFade,
            degree);

    /// <summary>
    /// Selects NPXS40087's original <c>ripple_p</c> program. Origin is the
    /// </summary>
    internal void ConfigureNativeImageRipple(
        ShellLayerBackgroundTransitionDegree degree,
        double originX,
        double originY)
    {
        ConfigureNativeImageTransition(
            ShellLayerBackgroundTransitionType.CustomImageRipple,
            degree);
        _configuredRippleOriginX = Math.Clamp(originX, 0.0, 1.0);
        _configuredRippleOriginY = Math.Clamp(originY, 0.0, 1.0);
    }

    /// <summary>
    /// Selects one of the translated custom-image programs for the next image
    /// change. Home uses Normal degree and chooses slide direction from the
    /// previous/new tile indices exactly as its RN bundle does.
    /// </summary>
    internal void ConfigureNativeImageTransition(
        ShellLayerBackgroundTransitionType type,
        ShellLayerBackgroundTransitionDegree degree)
    {
        if (type is not (
            ShellLayerBackgroundTransitionType.CustomImageRipple or
            ShellLayerBackgroundTransitionType.CustomImageSlideInLeft or
            ShellLayerBackgroundTransitionType.CustomImageSlideInRight or
            ShellLayerBackgroundTransitionType.CustomImageFade))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        _configuredNativeFadeDuration = Ps5Transitions.BackgroundTransition(
            (int)type,
            (int)degree) ?? throw new ArgumentOutOfRangeException(nameof(degree));
        _configuredNativeTransitionType = type;
        _configuredNativeDegree = degree;
    }

    /// <summary>Restores the title-selection fallback for later changes.</summary>
    internal void ClearNativeImageFade()
    {
        _configuredNativeFadeDuration = null;
        _configuredNativeTransitionType = null;
        _configuredNativeDegree = ShellLayerBackgroundTransitionDegree.Strong;
    }

    /// <summary>Test seam: duration captured when the current fade began.</summary>
    internal TimeSpan ActiveFadeDuration => _activeFadeDuration;

    /// <summary>Test seam: whether the active fade uses native linear progress.</summary>
    internal bool ActiveFadeIsNativeLinear => _activeFadeIsNativeLinear;

    /// <summary>Test seam: native program captured for the current fade.</summary>
    internal ShellLayerBackgroundTransitionType? ActiveNativeTransitionType =>
        _activeNativeTransitionType;

    /// <summary>Test seam: native duration selected for the next image change.</summary>
    internal TimeSpan? ConfiguredNativeFadeDuration => _configuredNativeFadeDuration;

    /// <summary>Test seam: native program selected for the next image change.</summary>
    internal ShellLayerBackgroundTransitionType? ConfiguredNativeTransitionType =>
        _configuredNativeTransitionType;

    internal double ConfiguredRippleOriginX => _configuredRippleOriginX;
    internal double ConfiguredRippleOriginY => _configuredRippleOriginY;

    /// <summary>
    /// Capture/test seam: true while the most recent image request is still
    /// resolving or decoding. This is intentionally separate from
    /// <see cref="IsPlateLoaded"/>, because the outgoing plate remains loaded
    /// while its replacement is prepared off-thread.
    /// </summary>
    internal bool IsImageLoadPending => _isImageLoadPending;

    /// <summary>Last native ripple preparation failure, or null after success.</summary>
    internal string? NativeRippleFailure => _nativeRippleFailure;

    /// <summary>
    /// The destination rect for a source plate inside a viewport: uniform fit,
    /// centred, never cropped. Pure geometry, so the "the whole plate is
    /// visible" claim is testable without a GPU.
    /// </summary>
    /// <param name="source">Plate size in pixels.</param>
    /// <param name="viewport">Available size.</param>
    public static Rect FitRect(Size source, Size viewport)
    {
        if (source.Width <= 0 || source.Height <= 0 ||
            viewport.Width <= 0 || viewport.Height <= 0)
        {
            return default;
        }

        var scale = Math.Min(viewport.Width / source.Width, viewport.Height / source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;
        return new Rect(
            (viewport.Width - width) / 2.0,
            (viewport.Height - height) / 2.0,
            width,
            height);
    }

    /// <summary>
    /// Opacity of the incoming plate <paramref name="elapsed"/> into a fade of
    /// <paramref name="duration"/>, on <see cref="CrossFadeCurve"/>. Clamped to
    /// <c>[0, 1]</c>, and 1 for a non-positive duration so a caller that
    /// disables the animation gets an instant swap rather than a blank frame.
    /// </summary>
    /// <param name="elapsed">Time since the fade began.</param>
    /// <param name="duration">Total fade duration.</param>
    public static double CrossFadeOpacity(TimeSpan elapsed, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || elapsed >= duration)
        {
            return 1.0;
        }

        if (elapsed <= TimeSpan.Zero)
        {
            return 0.0;
        }

        var t = elapsed.TotalSeconds / duration.TotalSeconds;
        return Math.Clamp(CrossFadeCurve.Evaluate(t), 0.0, 1.0);
    }

    /// <summary>
    /// Opacity computed by 4.03 <c>cross_fade_p</c>: native code writes
    /// <c>min(elapsed / duration, 1)</c> to progress and the shader linearly
    /// interpolates its two texture samples with that value.
    /// </summary>
    public static double NativeCrossFadeOpacity(TimeSpan elapsed, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || elapsed >= duration)
        {
            return 1.0;
        }

        if (elapsed <= TimeSpan.Zero)
        {
            return 0.0;
        }

        return Math.Clamp(elapsed.TotalSeconds / duration.TotalSeconds, 0.0, 1.0);
    }

    /// <summary>
    /// Spatial blend mask recovered directly from the first basic block of
    /// 4.03 <c>slide_in_p</c>. Unlike the old host animation this is a moving,
    /// very broad smoothstep wipe, not a globally eased opacity.
    /// </summary>
    public static double NativeSlideBlend(
        double u,
        double progress,
        Ps5Transitions.NativeSlideParameters parameters)
    {
        progress = Math.Clamp(progress, 0.0, 1.0);
        if (progress <= 0.0)
        {
            return 0.0;
        }

        var centre = (parameters.Direction * 0.5) + 0.5;
        var edge = centre - (parameters.Direction * u) -
            (progress * (1.0 + parameters.Smoothness));
        var t = Math.Clamp(1.0 + (edge / parameters.Smoothness), 0.0, 1.0);
        var smooth = t * t * (3.0 - (2.0 * t));
        return 1.0 - smooth;
    }

    /// <summary>Texture0 UV written by the native slide shader for opaque art.</summary>
    public static double NativeSlideOutgoingU(
        double u,
        double progress,
        double blend,
        Ps5Transitions.NativeSlideParameters parameters) =>
        u + (parameters.Direction * parameters.SlideFactor * blend) +
        (parameters.Direction * progress * parameters.DisplacementFactor);

    /// <summary>Texture1 UV written by the native slide shader for opaque art.</summary>
    public static double NativeSlideIncomingU(
        double u,
        double progress,
        double blend,
        Ps5Transitions.NativeSlideParameters parameters) =>
        u + (parameters.Direction * parameters.SlideFactor * (blend - 1.0)) -
        (parameters.Direction * (1.0 - progress) * parameters.DisplacementFactor);

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BeginLoad(TitleArtPath, FallbackArtPath, TitleId, animate: false);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Interlocked.Increment(ref _generation);
        _fadeTimer.Stop();
        DisposeNativeRippleFrames();
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleArtPathProperty ||
            change.Property == FallbackArtPathProperty ||
            change.Property == TitleIdProperty)
        {
            // A focus move animates; the initial attach does not, so the home
            // screen does not fade up from black on arrival.
            BeginLoad(
                TitleArtPath,
                FallbackArtPath,
                TitleId,
                animate: this.GetVisualRoot() is not null);
        }
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        // The fallback colour sits under the plate as well as standing in for
        // it, so a plate with an alpha channel composites over a known field
        // rather than over whatever the host happened to paint.
        context.FillRectangle(FallbackBrush, bounds);

        if (_nativeRippleFrames is { Length: > 0 } rippleFrames &&
            _activeNativeTransitionType ==
                ShellLayerBackgroundTransitionType.CustomImageRipple)
        {
            var progress = NativeCrossFadeOpacity(_fadeElapsed, _activeFadeDuration);
            var index = Math.Clamp(
                (int)Math.Round(progress * (rippleFrames.Length - 1)),
                0,
                rippleFrames.Length - 1);
            DrawPlate(context, rippleFrames[index], bounds);
            return;
        }

        if (_outgoing is not null && _plate is not null &&
            _activeNativeTransitionType is
                (ShellLayerBackgroundTransitionType.CustomImageSlideInLeft or
                 ShellLayerBackgroundTransitionType.CustomImageSlideInRight))
        {
            DrawNativeSlide(context, _outgoing, _plate, bounds);
            return;
        }

        // A dissolve, not a fade through the base colour: the outgoing plate
        // stays fully opaque underneath while the incoming one ramps up over
        // it, so the background never dips in luminance mid-move.
        if (_outgoing is not null)
        {
            DrawPlate(context, _outgoing, bounds);
        }

        if (_plate is null)
        {
            return;
        }

        var opacity = _outgoing is null
            ? 1.0
            : _activeFadeIsNativeLinear
                ? NativeCrossFadeOpacity(_fadeElapsed, _activeFadeDuration)
                : CrossFadeOpacity(_fadeElapsed, _activeFadeDuration);

        if (opacity >= 1.0)
        {
            DrawPlate(context, _plate, bounds);
            return;
        }

        using (context.PushOpacity(opacity))
        {
            DrawPlate(context, _plate, bounds);
        }
    }

    private static void DrawPlate(DrawingContext context, Bitmap plate, Rect bounds)
    {
        var source = new Size(plate.PixelSize.Width, plate.PixelSize.Height);
        var destination = FitRect(source, bounds.Size);
        if (destination.Width <= 0 || destination.Height <= 0)
        {
            return;
        }

        context.DrawImage(plate, new Rect(source), destination);
    }

    private void DrawNativeSlide(
        DrawingContext context,
        Bitmap outgoing,
        Bitmap incoming,
        Rect bounds)
    {
        var direction = _activeNativeTransitionType ==
            ShellLayerBackgroundTransitionType.CustomImageSlideInLeft
                ? 1.0
                : -1.0;
        var parameters = Ps5Transitions.BackgroundSlide(
            (int)ShellLayerBackgroundTransitionDegree.Normal,
            direction)!.Value;
        var progress = NativeCrossFadeOpacity(_fadeElapsed, _activeFadeDuration);

        const int slices = 384;
        DrawNativeSlideImage(context, outgoing, bounds, progress, parameters, incoming: false, slices);
        DrawNativeSlideImage(context, incoming, bounds, progress, parameters, incoming: true, slices);
    }

    private static void DrawNativeSlideImage(
        DrawingContext context,
        Bitmap image,
        Rect bounds,
        double progress,
        Ps5Transitions.NativeSlideParameters parameters,
        bool incoming,
        int slices)
    {
        var sourceSize = new Size(image.PixelSize.Width, image.PixelSize.Height);
        var destination = FitRect(sourceSize, bounds.Size);
        if (destination.Width <= 0 || destination.Height <= 0)
        {
            return;
        }

        for (var slice = 0; slice < slices; slice++)
        {
            var u0 = (double)slice / slices;
            var u1 = (double)(slice + 1) / slices;
            var uc = (u0 + u1) * 0.5;
            var blend = NativeSlideBlend(uc, progress, parameters);
            if (incoming && blend <= 0.001)
            {
                continue;
            }

            var blend0 = NativeSlideBlend(u0, progress, parameters);
            var blend1 = NativeSlideBlend(u1, progress, parameters);
            var sample0 = incoming
                ? NativeSlideIncomingU(u0, progress, blend0, parameters)
                : NativeSlideOutgoingU(u0, progress, blend0, parameters);
            var sample1 = incoming
                ? NativeSlideIncomingU(u1, progress, blend1, parameters)
                : NativeSlideOutgoingU(u1, progress, blend1, parameters);
            sample0 = Math.Clamp(sample0, 0.0, 1.0 - 1e-7);
            sample1 = Math.Clamp(sample1, sample0 + 1e-7, 1.0);

            var source = new Rect(
                sample0 * sourceSize.Width,
                0.0,
                (sample1 - sample0) * sourceSize.Width,
                sourceSize.Height);
            var target = new Rect(
                destination.X + (u0 * destination.Width),
                destination.Y,
                (u1 - u0) * destination.Width,
                destination.Height);

            if (incoming)
            {
                using (context.PushOpacity(blend * parameters.Opacity))
                {
                    context.DrawImage(image, source, target);
                }
            }
            else
            {
                context.DrawImage(image, source, target);
            }
        }
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        _fadeElapsed += _fadeTimer.Interval;
        if (_fadeElapsed >= _activeFadeDuration)
        {
            _fadeTimer.Stop();
            _fadeElapsed = _activeFadeDuration;
            _outgoing = null;
            DisposeNativeRippleFrames();
        }

        InvalidateVisual();
    }

    // Fire and forget: resolution and decode happen off the UI thread, a newer
    // request abandons an older one, and every failure lands on the documented
    // fallback rather than on an exception.
    private async void BeginLoad(
        string? titleArtPath,
        string? fallbackArtPath,
        string? _titleId,
        bool animate)
    {
        var generation = Interlocked.Increment(ref _generation);
        _isImageLoadPending = true;
        Bitmap? bitmap = null;
        var isTitleArt = false;

        try
        {
            if (!string.IsNullOrWhiteSpace(titleArtPath))
            {
                bitmap = await SharedCache.GetOrAdd(
                    titleArtPath, static p => Task.Run(() => Decode(p)));
                isTitleArt = bitmap is not null;
            }

            if (bitmap is null && !string.IsNullOrWhiteSpace(fallbackArtPath))
            {
                bitmap = await SharedCache.GetOrAdd(
                    fallbackArtPath, static p => Task.Run(() => Decode(p)));
                isTitleArt = bitmap is not null;
            }

        }
        catch (Exception)
        {
            bitmap = null;
            isTitleArt = false;
        }

        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }

        _isImageLoadPending = false;

        // Re-selecting the title already on screen must not restart the fade.
        if (ReferenceEquals(bitmap, _plate))
        {
            IsTitleArt = isTitleArt;
            return;
        }

        Bitmap[]? rippleFrames = null;
        if (animate && _plate is not null && bitmap is not null &&
            _configuredNativeTransitionType ==
                ShellLayerBackgroundTransitionType.CustomImageRipple)
        {
            rippleFrames = await PrepareNativeRippleFramesAsync(_plate, bitmap);
            if (generation != Volatile.Read(ref _generation))
            {
                DisposeFrames(rippleFrames);
                return;
            }
        }

        DisposeNativeRippleFrames();
        _nativeRippleFrames = rippleFrames;
        if (animate && _plate is not null && bitmap is not null)
        {
            _outgoing = _plate;
            _fadeElapsed = TimeSpan.Zero;
            _activeFadeDuration = _configuredNativeFadeDuration ?? CrossFadeDuration;
            _activeFadeIsNativeLinear = _configuredNativeFadeDuration.HasValue;
            _activeNativeTransitionType = _configuredNativeTransitionType;
            _fadeTimer.Start();
        }
        else
        {
            _outgoing = null;
            _fadeTimer.Stop();
        }

        _plate = bitmap;
        IsPlateLoaded = bitmap is not null;
        IsTitleArt = isTitleArt;
        InvalidateVisual();
    }

    private async Task<Bitmap[]?> PrepareNativeRippleFramesAsync(
        Bitmap outgoing,
        Bitmap incoming)
    {
        const int width = 960;
        const int height = 540;
        const int frameCount = 20;
        _nativeRippleFailure = null;

        byte[] source;
        byte[] target;
        try
        {
            // Avalonia bitmap access stays on the UI thread. Vulkan translation
            source = ReadScaledRgba(outgoing, width, height);
            target = ReadScaledRgba(incoming, width, height);
        }
        catch (Exception exception)
        {
            _nativeRippleFailure = $"plate readback: {exception.Message}";
            return null;
        }

        var constants = new ReadOnlyMemory<byte>[frameCount];
        for (var index = 0; index < frameCount; index++)
        {
            var linear = (double)index / (frameCount - 1);
            var parameters = Ps5Transitions.BackgroundRipple(
                (int)_configuredNativeDegree,
                _configuredRippleOriginX,
                _configuredRippleOriginY,
                1.0,
                linear)!.Value;
            constants[index] = PackRippleParameters(parameters);
        }

        var rippleShaderPath = BigPicturePackage.Resolve("3.00/transitions/ripple-p.spv");
        if (!Ps5NativeSpirvAsset.TryLoad(rippleShaderPath, out var rippleSpirv, out var rippleError))
        {
            _nativeRippleFailure = $"packaged ripple shader is unavailable: {rippleError}";
            return null;
        }

        try
        {
            var frames = await Task.Run(() =>
            {
                return Ps5NativeRippleRenderer.RenderOpaqueFrames(
                    new Ps5NativeRippleProgram(rippleSpirv),
                    width,
                    height,
                    source,
                    target,
                    constants);
            });
            var bitmaps = frames
                .Select(frame => (Bitmap)SystemAssets.Textures.DdsImageAvalonia.CreateBitmap(
                    frame.Rgba.Span,
                    frame.Width,
                    frame.Height))
                .ToArray();
            _nativeRippleFailure = null;
            return bitmaps;
        }
        catch (Exception exception)
        {
            _nativeRippleFailure = $"native ripple: {exception.Message}";
            return null;
        }
    }

    private static byte[] PackRippleParameters(Ps5Transitions.NativeRippleParameters value)
    {
        var bytes = new byte[40];
        var values = new[]
        {
            value.OriginX,
            value.OriginY,
            value.Opacity,
            value.Progress,
            value.ProgressPow,
            value.Smoothness,
            value.Ratio,
            value.ScaleFactor,
            value.SwirlFactor,
            value.FishEye,
        };
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(float)),
                unchecked((uint)BitConverter.SingleToInt32Bits((float)values[index])));
        }
        return bytes;
    }

    private static byte[] ReadScaledRgba(Bitmap source, int width, int height)
    {
        var size = source.PixelSize;
        using var copy = new WriteableBitmap(
            size,
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        var rgba = new byte[checked(width * height * 4)];
        using var frame = copy.Lock();
        source.CopyPixels(frame, AlphaFormat.Unpremul);
        unsafe
        {
            var pixels = (byte*)frame.Address;
            for (var y = 0; y < height; y++)
            {
                var sourceY = Math.Min((y * size.Height) / height, size.Height - 1);
                var row = pixels + sourceY * frame.RowBytes;
                for (var x = 0; x < width; x++)
                {
                    var sourceX = Math.Min((x * size.Width) / width, size.Width - 1);
                    var input = row + sourceX * 4;
                    var output = (y * width + x) * 4;
                    rgba[output + 0] = input[2];
                    rgba[output + 1] = input[1];
                    rgba[output + 2] = input[0];
                    rgba[output + 3] = input[3];
                }
            }
        }
        return rgba;
    }

    private void DisposeNativeRippleFrames()
    {
        DisposeFrames(_nativeRippleFrames);
        _nativeRippleFrames = null;
    }

    private static void DisposeFrames(Bitmap[]? frames)
    {
        if (frames is null)
        {
            return;
        }

        foreach (var frame in frames)
        {
            frame.Dispose();
        }
    }

    private static Bitmap? Decode(string path)
    {
        try
        {
            // Two system apps ship pic1.png rather than a DDS; Avalonia's own
            // decoder reads those, downscaled to the same presentation width.
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, ShellBackgroundSource.TargetDecodeWidth);
            }

            // Keep the authored 3840-wide source. The design canvas is logical;
            // on a 4K output it is rendered at two physical pixels per unit.
            var rgba = ShellBackgroundSource.TryLoadRgba(
                path, ShellBackgroundSource.TargetDecodeWidth, out var width, out var height);
            if (rgba is null)
            {
                return null;
            }

            return SystemAssets.Textures.DdsImageAvalonia.CreateBitmap(rgba, width, height);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
