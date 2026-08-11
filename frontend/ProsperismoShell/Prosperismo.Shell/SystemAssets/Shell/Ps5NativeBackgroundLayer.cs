// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.Libs.Presentation;

namespace Prosperismo.GUI.SystemAssets.Shell;

/// <summary>
/// pipeline. The control has deliberately no still-image, PNG-sequence, video,
/// </summary>
internal sealed class Ps5NativeBackgroundLayer : Image
{
    // Retained for the research cache loaders, which are not selected by this
    // product control. They carry translated buffers rather than rendered media.
    internal const string FrameEnvironmentVariable = "PROSPERISMO_PS5_NATIVE_FRAME";
    internal const string PreviewEnvironmentVariable = "PROSPERISMO_PS5_NATIVE_PREVIEW";
    internal static readonly TimeSpan PresentationInterval = TimeSpan.FromSeconds(1.0 / 60.0);
    internal const BitmapBlendingMode CompositeBlendingMode = BitmapBlendingMode.SourceOver;
    internal const AlphaFormat CompositeAlphaFormat = AlphaFormat.Opaque;
    // Avalonia's macOS dispatcher rounds a one-vblank timer up to the next
    // vblank under load. Poll at half a vblank; the in-flight gate still emits
    // at most one native frame at a time and coalesces excess ticks.
    private static readonly TimeSpan SchedulingInterval = TimeSpan.FromSeconds(1.0 / 120.0);

    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _hideTimer;
    private ShellGlobalBackgroundState _globalState;
    private bool _motionEnabled = true;
    private bool _isSuppressed;
    private bool _particleOverlayVisible = true;
    private float _particleAlpha = 1.0f;
    private bool _isFrameLoaded;
    private bool _attached;
    private bool _liveSourceLoadStarted;
    private int _liveSourceLoadGeneration;
    private TaskCompletionSource<IPs5NativeParticleFrameSource?>? _liveSourceLoadCompletion;
    private IPs5NativeParticleFrameSource? _liveSource;
    private CancellationTokenSource? _liveCancellation;
    private TimeSpan _liveElapsed;
    private long _lastLiveClockTimestamp;
    private bool _liveRenderPending;
    private bool _liveRenderQueued;
    private WriteableBitmap? _liveBitmap;

    public Ps5NativeBackgroundLayer()
    {
        IsHitTestVisible = false;
        Stretch = Stretch.Uniform;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        // The retained source returns light_p's complete room composition,
        // including its particle target. Plus belonged to the removed
        // particle-only transport and double-lit the room over the basemat.
        RenderOptions.SetBitmapBlendingMode(this, CompositeBlendingMode);
        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = Ps5Transitions.LinearPoint4Sec,
                Easing = Ps5Transitions.Linear,
            },
        };
        // The native simulation and presentation target remain 60 Hz. Every
        // output frame performs two Vulkan readbacks, a full-size light texture
        // upload and a final readback. Slow dense frames are coalesced to the
        // latest native time instead of accumulating stale work. Vulkan work
        // itself remains off the UI thread.
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = SchedulingInterval,
        };
        _frameTimer.Tick += (_, _) => QueueLiveFrame(ConsumeLiveClockDelta());
        _hideTimer = new DispatcherTimer { Interval = Ps5Transitions.LinearPoint4Sec };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!IsEligible())
            {
                IsVisible = false;
            }
        };
        Ps5NativeColdBootAmbientLiveFrameSource.StartPrewarming();
        UpdateVisibility();
    }

    internal ShellGlobalBackgroundState GlobalState
    {
        get => _globalState;
        set
        {
            if (_globalState == value)
            {
                return;
            }

            _globalState = value;
            UpdateVisibility();
        }
    }

    internal IPs5NativeParticleFrameSource? LiveSource
    {
        get => _liveSource;
        set
        {
            if (ReferenceEquals(_liveSource, value))
            {
                return;
            }

            CancelLiveRender();
            var old = _liveSource;
            _liveSource = value;
            _liveElapsed = TimeSpan.Zero;
            _lastLiveClockTimestamp = Stopwatch.GetTimestamp();
            _liveRenderPending = false;
            _liveRenderQueued = false;
            DisposeLiveBitmap();
            IsFrameLoaded = false;
            if (old is not null)
            {
                _ = old.DisposeAsync();
            }
            UpdateVisibility();
            QueueLiveFrame(TimeSpan.Zero);
        }
    }

    internal bool MotionEnabled
    {
        get => _motionEnabled;
        set
        {
            if (_motionEnabled == value)
            {
                return;
            }

            _motionEnabled = value;
            UpdateVisibility();
        }
    }

    /// <summary>
    /// Hides the ambient particle pass while title-owned art or another shell
    /// surface owns the backdrop. The last live frame fades out; no rendered
    /// sequence or replacement effect is introduced.
    /// </summary>
    internal bool IsSuppressed
    {
        get => _isSuppressed;
        set
        {
            if (_isSuppressed == value)
            {
                return;
            }

            _isSuppressed = value;
            if (value)
            {
                CancelLiveRender();
            }
            UpdateVisibility();
            QueueLiveFrame(TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Gates only the particle target consumed by <c>light_p</c>. The moving
    /// room remains rendered and the retained source is not replaced when
    /// Settings asks for the base-only composition.
    /// </summary>
    internal bool ParticleOverlayVisible
    {
        get => _particleOverlayVisible;
        set
        {
            if (_particleOverlayVisible == value)
            {
                return;
            }

            _particleOverlayVisible = value;
            QueueLiveFrame(TimeSpan.Zero);
        }
    }

    internal bool IsFrameLoaded
    {
        get => _isFrameLoaded;
        private set
        {
            _isFrameLoaded = value;
            UpdateVisibility();
        }
    }

    internal bool ManualClock { get; set; }

    internal TimeSpan LiveElapsed => _liveElapsed;

    internal bool LiveRenderPending => _liveRenderPending;

    internal void RefreshVisibility() => UpdateVisibility();

    internal void AdvanceForCapture(TimeSpan delta)
    {
        if (ManualClock && IsVisible && delta > TimeSpan.Zero)
        {
            QueueLiveFrame(delta);
        }
    }

    internal void AdvanceFrameForCapture() => QueueLiveFrame(PresentationInterval);

    /// <summary>
    /// finished compiling before the caller starts time-sensitive audio.
    /// </summary>
    internal async Task<bool> EnsureLiveSourceAsync(ShellGlobalBackgroundState state)
    {
        if (_liveSource?.SupportsState(state) == true)
        {
            return true;
        }

        if (!RequiresLiveSource(state) || !_attached)
        {
            return false;
        }

        StartLiveSourceLoad();
        var completion = _liveSourceLoadCompletion;
        if (completion is null)
        {
            return _liveSource?.SupportsState(state) == true;
        }

        var source = await completion.Task.ConfigureAwait(true);
        return source?.SupportsState(state) == true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateVisibility();
        QueueLiveFrame(TimeSpan.Zero);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _liveSourceLoadGeneration++;
        _liveSourceLoadCompletion?.TrySetResult(null);
        CancelLiveRender();
        _frameTimer.Stop();
        _hideTimer.Stop();
        Source = null;
        DisposeLiveBitmap();
        IsFrameLoaded = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateVisibility()
    {
        var route = ShellBackgroundComposition.NativeParticleRouteFor(_globalState);
        // Do not create a fresh source from NoParticle: that command has no
        // native setter and only preserves an allocation that already exists.
        // Cold boot/raw state 3 (or the explicit raw-state-2 diagnostic) must
        // establish the source first.
        if (ShouldCreateLiveSource(_globalState) && _liveSource is null)
        {
            StartLiveSourceLoad();
        }
        var eligible = IsEligible();
        var shouldAdvance = ShouldAdvanceSource(
            _motionEnabled,
            _liveSource is not null,
            _liveSource?.SupportsState(_globalState) == true);
        Opacity = eligible ? Math.Clamp(route.LayerWeight, 0.0f, 1.0f) : 0.0;
        if (shouldAdvance)
        {
            if (!ManualClock && this.GetVisualRoot() is not null)
            {
                if (!_frameTimer.IsEnabled)
                {
                    // Detached time is outside this control's ownership. While
                    // attached, including when title art covers the room, each
                    // tick consumes actual monotonic time instead of assuming
                    // the dispatcher delivered every requested presentation.
                    _lastLiveClockTimestamp = Stopwatch.GetTimestamp();
                }
                _frameTimer.Start();
                if (eligible && !_isFrameLoaded)
                {
                    QueueLiveFrame(TimeSpan.Zero);
                }
            }
        }
        else
        {
            _frameTimer.Stop();
        }

        if (eligible)
        {
            _hideTimer.Stop();
            IsVisible = true;
        }
        else
        {
            if (this.GetVisualRoot() is null)
            {
                IsVisible = false;
            }
            else if (IsVisible)
            {
                _hideTimer.Stop();
                _hideTimer.Start();
            }
        }
    }

    private bool IsEligible() =>
        !_isSuppressed &&
        _motionEnabled &&
        _liveSource?.SupportsState(_globalState) == true;

    private void StartLiveSourceLoad()
    {
        if (!_attached || _liveSourceLoadStarted)
        {
            return;
        }

        _liveSourceLoadStarted = true;
        var completion = new TaskCompletionSource<IPs5NativeParticleFrameSource?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _liveSourceLoadCompletion = completion;
        var generation = ++_liveSourceLoadGeneration;
        _ = LoadLiveSourceAsync(generation, completion);
    }

    private async Task LoadLiveSourceAsync(
        int generation,
        TaskCompletionSource<IPs5NativeParticleFrameSource?> completion)
    {
        var source = await Task.Run<IPs5NativeParticleFrameSource?>(
            static () => Ps5NativeColdBootAmbientLiveFrameSource.TryCreate()).ConfigureAwait(false);
        if (source is Ps5NativeColdBootAmbientLiveFrameSource nativeSource)
        {
            try
            {
                var extent = await Dispatcher.UIThread.InvokeAsync(
                    ResolveCurrentRenderExtent,
                    DispatcherPriority.Loaded);
                await nativeSource.PrimeRenderersAsync(extent.Width, extent.Height)
                    .ConfigureAwait(false);
            }
            catch
            {
                await nativeSource.DisposeAsync().ConfigureAwait(false);
                source = null;
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _liveSourceLoadStarted = false;
            if (!_attached || generation != _liveSourceLoadGeneration ||
                !RequiresLiveSource(_globalState))
            {
                if (source is not null)
                {
                    _ = source.DisposeAsync();
                }

                // A detach/reattach can invalidate an in-flight creation while
                // the new attachment observes _liveSourceLoadStarted. Once the
                // stale result has cleared that flag, immediately retry for the
                // still-attached ambient route instead of remaining blank.
                if (_attached && RequiresLiveSource(_globalState))
                {
                    completion.TrySetResult(null);
                    StartLiveSourceLoad();
                }
                else
                {
                    completion.TrySetResult(null);
                }
                return;
            }

            LiveSource = source;
            completion.TrySetResult(source);
        }, DispatcherPriority.Loaded);
    }

    private void QueueLiveFrame(TimeSpan delta)
    {
        if (_liveSource is not { } source || !_motionEnabled ||
            !source.SupportsState(_globalState))
        {
            return;
        }

        _liveElapsed += delta;
        _particleAlpha = AdvanceParticleOverlayOpacity(
            _particleAlpha,
            _particleOverlayVisible,
            delta);
        // tick. The next dispatch consumes current state; rendered media is
        // never replayed to cover a missed frame.
        if (_liveRenderPending)
        {
            _liveRenderQueued = true;
            return;
        }

        var extent = ResolveCurrentRenderExtent();
        var width = extent.Width;
        var height = extent.Height;

        _liveCancellation ??= new CancellationTokenSource();
        var token = _liveCancellation.Token;
        var request = new Ps5NativeParticleFrameRequest(
            _globalState,
            _liveElapsed,
            width,
            height,
            _particleAlpha);
        _liveRenderPending = true;
        _liveRenderQueued = false;
        if (_isSuppressed && source is Ps5NativeColdBootAmbientLiveFrameSource retained)
        {
            _ = AdvanceLiveSourceAsync(retained, request, token);
        }
        else
        {
            _ = RenderLiveFrameAsync(source, request, token);
        }
    }

    private TimeSpan ConsumeLiveClockDelta()
    {
        var now = Stopwatch.GetTimestamp();
        var previous = _lastLiveClockTimestamp;
        _lastLiveClockTimestamp = now;
        return previous == 0
            ? _frameTimer.Interval
            : Stopwatch.GetElapsedTime(previous, now);
    }

    private async Task RenderLiveFrameAsync(
        IPs5NativeParticleFrameSource source,
        Ps5NativeParticleFrameRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // RenderAsync is ValueTask-based, but the Vulkan backends are
            // deliberately synchronous once their semaphore is available.
            // Calling it directly from QueueLiveFrame can therefore execute
            // the complete compute/draw/readback chain on Avalonia's render
            // dispatcher before the first incomplete await. Force that work
            // onto a worker so a dense cold-boot frame cannot stall input,
            // focus motion, audio dispatch, or the compositor.
            var frame = await Task.Run(
                async () => await source.RenderAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            if (frame is not { IsValid: true } || cancellationToken.IsCancellationRequested ||
                !ReferenceEquals(source, _liveSource) || _isSuppressed)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(
                () => PresentLiveFrame(frame),
                DispatcherPriority.Render,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Keep the last successfully simulated frame. There is no media or
            // host-authored particle fallback on this route.
        }
        finally
        {
            CompleteLiveRender(cancellationToken);
        }
    }

    private async Task AdvanceLiveSourceAsync(
        Ps5NativeColdBootAmbientLiveFrameSource source,
        Ps5NativeParticleFrameRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // The retained simulation is synchronous for the same reason as
            // the visible renderer. Title art may hide the result, but its
            // catch-up dispatches must not move onto Avalonia's UI thread.
            await Task.Run(
                async () => await source.AdvanceSimulationAsync(
                        request.State,
                        request.Elapsed,
                        cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Retain the last valid simulation state. The next background tick
            // retries from the same monotonic source rather than replacing it.
        }
        finally
        {
            CompleteLiveRender(cancellationToken);
        }
    }

    /// <summary>
    /// A timer tick that arrives while Vulkan is busy still advances the native
    /// clock. Start its newest frame as soon as the in-flight presentation is
    /// complete instead of waiting for another timer tick and effectively
    /// halving the output cadence.
    /// </summary>
    private void CompleteLiveRender(CancellationToken cancellationToken)
    {
        void CompleteOnUiThread()
        {
            if (_liveCancellation?.Token != cancellationToken)
            {
                return;
            }

            _liveRenderPending = false;
            if (!_liveRenderQueued || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _liveRenderQueued = false;
            QueueLiveFrame(TimeSpan.Zero);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            CompleteOnUiThread();
        }
        else
        {
            Dispatcher.UIThread.Post(CompleteOnUiThread, DispatcherPriority.Render);
        }
    }

    private void PresentLiveFrame(Ps5NativeParticleFrame frame)
    {
        var pixelSize = new PixelSize(frame.Width, frame.Height);
        var bitmap = _liveBitmap;
        if (bitmap is null || bitmap.PixelSize != pixelSize)
        {
            bitmap = new WriteableBitmap(
                pixelSize,
                new Vector(96, 96),
                PixelFormat.Rgba8888,
                // light_p returns the complete room. Its colour target's alpha
                // is an internal pass value, not shell transparency; treating
                // it as transparency leaks the basemat back into the room.
                CompositeAlphaFormat);
            var old = _liveBitmap;
            _liveBitmap = bitmap;
            Source = bitmap;
            old?.Dispose();
        }
        using (var target = bitmap.Lock())
        {
            unsafe
            {
                fixed (byte* source = frame.Rgba.Span)
                {
                    var sourceRowBytes = frame.Width * 4;
                    if (target.RowBytes == sourceRowBytes)
                    {
                        Buffer.MemoryCopy(
                            source,
                            (byte*)target.Address,
                            (long)target.RowBytes * frame.Height,
                            (long)sourceRowBytes * frame.Height);
                    }
                    else
                    {
                        for (var y = 0; y < frame.Height; y++)
                        {
                            Buffer.MemoryCopy(
                                source + (y * sourceRowBytes),
                                (byte*)target.Address + (y * target.RowBytes),
                                target.RowBytes,
                                sourceRowBytes);
                        }
                    }
                }
            }
        }

        IsFrameLoaded = true;
        InvalidateVisual();
    }

    private void CancelLiveRender()
    {
        _liveRenderQueued = false;
        _liveRenderPending = false;
        var cancellation = Interlocked.Exchange(ref _liveCancellation, null);
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void DisposeLiveBitmap()
    {
        var bitmap = _liveBitmap;
        _liveBitmap = null;
        bitmap?.Dispose();
    }

    internal static float AdvanceParticleOverlayOpacity(
        float opacity,
        bool visible,
        TimeSpan elapsed)
    {
        opacity = float.IsFinite(opacity) ? Math.Clamp(opacity, 0.0f, 1.0f) : 0.0f;
        if (elapsed <= TimeSpan.Zero)
        {
            return opacity;
        }

        var step = (float)(elapsed.TotalSeconds /
            Ps5Transitions.LinearPoint4Sec.TotalSeconds);
        return visible
            ? Math.Min(1.0f, opacity + step)
            : Math.Max(0.0f, opacity - step);
    }

    internal static PixelSize ResolveRenderExtent(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width > 3840 || height > 2160)
        {
            var scale = Math.Min(3840.0 / width, 2160.0 / height);
            width = Math.Max(1, (int)Math.Floor(width * scale));
            height = Math.Max(1, (int)Math.Floor(height * scale));
        }

        if (width <= 1 || height <= 1)
        {
            return new PixelSize(1920, 1080);
        }

        return new PixelSize(width, height);
    }

    private PixelSize ResolveCurrentRenderExtent()
    {
        var width = Math.Max(1, (int)Math.Round(Bounds.Width));
        var height = Math.Max(1, (int)Math.Round(Bounds.Height));
        if (this.GetVisualRoot() is TopLevel root &&
            this.TransformToVisual(root) is { } transform)
        {
            var outputBounds = new Rect(Bounds.Size).TransformToAABB(transform);
            width = Math.Max(1, (int)Math.Ceiling(outputBounds.Width * root.RenderScaling));
            height = Math.Max(1, (int)Math.Ceiling(outputBounds.Height * root.RenderScaling));
        }

        // Render at the transformed physical output extent. Only uniformly
        return ResolveRenderExtent(width, height);
    }

    private static bool RequiresLiveSource(ShellGlobalBackgroundState state) =>
        state == ShellGlobalBackgroundState.NoParticle ||
        ShellBackgroundComposition.NativeParticleRouteFor(state).RawState is 2 or 3;

    internal static bool ShouldCreateLiveSource(ShellGlobalBackgroundState state) =>
        ShellBackgroundComposition.NativeParticleRouteFor(state).RawState is 2 or 3;

    internal static bool ShouldAdvanceSource(
        bool motionEnabled,
        bool hasSource,
        bool supportsState) =>
        motionEnabled && hasSource && supportsState;
}
