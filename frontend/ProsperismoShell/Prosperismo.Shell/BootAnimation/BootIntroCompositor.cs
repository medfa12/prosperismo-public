// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace Prosperismo.GUI.BootAnimation;

/// <summary>
/// The diffuse half of the boot sequence: a small accumulation buffer the motes
/// splat into and that decays rather than clearing, with the plate, the light, the
/// warm haze and the shaft laid over it. Produces BGRA the layer hands to a
/// bitmap.
///
/// Three decisions carry the image quality, and all three come from grading an
/// earlier renderer against the console's own footage and losing:
///
/// <list type="bullet">
///   <item><description><b>The buffer decays, it does not clear.</b> A mote
///     splatted every frame into a decaying buffer leaves a filament behind it, so
///     the width of the mark on screen comes from the trail rather than from a fat
///     kernel. The earlier renderer used a wide gaussian stamp per mote and
///     measured 22 % of the reference's detail: it had spread the same light over
///     1.7x the area at 0.57x the peak, which is the definition of a fog.</description></item>
///   <item><description><b>Splats are sub-texel and energy normalised.</b> The
///     kernel is divided by its own weight sum, so a mote deposits the same energy
///     wherever it lands between texels. Without that a slow mote flickers as it
///     crosses a texel boundary, which is exactly where a filament wants to be
///     smooth.</description></item>
///   <item><description><b>The diffuse tier is deliberately low resolution.</b>
///     Trails, plate, haze and shaft are all soft and cost nothing to upscale. The
///     budget saved goes into the resolved mote heads, which the layer draws at
///     full resolution on top.</description></item>
/// </list>
///
/// Allocation free per frame: every buffer is sized on resize and reused.
/// </summary>
public sealed class BootIntroCompositor
{
    /// <summary>Widest diffuse buffer.</summary>
    public const int MaxWidth = 960;

    /// <summary>Narrowest buffer worth composing; below this the layer draws nothing.</summary>
    public const int MinWidth = 32;

    /// <summary>Control pixels per diffuse texel.</summary>
    public const int PixelsPerTexel = 2;

    /// <summary>
    /// Half-life of the trail accumulation, in seconds. Longer than the ambient
    /// background's 0.11 because the reference's boot knot is a weave of long
    /// curved ribbons rather than near-still defocused discs: the filament is the
    /// feature here, not the head.
    /// </summary>
    public const double TrailHalfLife = 0.155;

    /// <summary>The tier height the mote gains are quoted against.</summary>
    private const double TuningHeight = 270.0;

    /// <summary>Widest splat kernel, in texels. A mote is a point; the trail gives it width.</summary>
    private const int MaxKernel = 7;

    // The three lobes of the knot's own light, in frame heights.
    private const double CoreRadius = 0.030;
    private const double HaloRadius = 0.085;
    private const double WashRadius = 0.30;

    /// <summary>Entries in the core's radial table, indexed by squared distance.</summary>
    private const int CoreLookup = 1024;

    /// <summary>Squared distance the table covers, in frame heights.</summary>
    private const double CoreLookupSpan = 1.6;

    private const double CoreLookupScale = CoreLookup / CoreLookupSpan;

    private static readonly float[] Core = BuildCoreLookup();

    // The plate's light. Records 21 and 9 both put it at (-150, 80, 400): left of
    // centre and high.
    private const double LightX = 0.42;
    private const double LightY = 0.22;
    private const double LightFalloff = 1.9;

    // The shaft. The reference's warm phase is lit by a broad beam entering just
    // above the top-left corner; its brightest point sits at x 0.14.
    private const double ShaftApexX = 0.14;
    private const double ShaftApexY = -0.05;
    private const double ShaftBearing = 0.055;
    private const double ShaftWaist = 0.10;
    private const double ShaftSpread = 0.46;
    private const double ShaftReach = 1.70;

    /// <summary>How far the corners are pulled down. The reference's frame is plainly not flat.</summary>
    private const double VignetteDepth = 0.30;

    private float[] _accumulation = Array.Empty<float>();
    private float[] _frame = Array.Empty<float>();
    private float[] _glow = Array.Empty<float>();
    private float[] _shaft = Array.Empty<float>();
    private float[] _vignette = Array.Empty<float>();
    private float[] _plateRow = Array.Empty<float>();
    private byte[] _pixels = Array.Empty<byte>();

    /// <summary>Diffuse buffer width in texels; 0 until <see cref="Resize"/> succeeds.</summary>
    public int Width { get; private set; }

    /// <summary>Diffuse buffer height in texels.</summary>
    public int Height { get; private set; }

    /// <summary>BGRA of the last <see cref="Compose"/>, row major and opaque.</summary>
    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>
    /// The same bytes as <see cref="Pixels"/>, as the array they live in, so the
    /// upload can copy straight out of it without materialising a span.
    /// </summary>
    public byte[] PixelBuffer => _pixels;

    /// <summary>Diffuse texel count for a control width.</summary>
    /// <param name="controlWidth">Control width in device-independent pixels.</param>
    /// <returns>Buffer width in texels, or 0 when the control is too small to compose.</returns>
    public static int BufferWidthFor(double controlWidth)
    {
        if (double.IsNaN(controlWidth) || controlWidth <= 0)
        {
            return 0;
        }

        var width = (int)(controlWidth / PixelsPerTexel);
        return width < MinWidth ? 0 : Math.Min(width, MaxWidth);
    }

    /// <summary>
    /// Sizes the buffers for a control size and rebuilds the two shape fields that
    /// only depend on it. Returns false when the control is too small to be worth
    /// composing, which is how the layer degrades on a tiny window.
    /// </summary>
    /// <param name="controlWidth">Control width in device-independent pixels.</param>
    /// <param name="controlHeight">Control height in device-independent pixels.</param>
    /// <returns>True when there is a buffer to compose into.</returns>
    public bool Resize(double controlWidth, double controlHeight)
    {
        var width = BufferWidthFor(controlWidth);
        if (width == 0 || controlHeight <= 0)
        {
            Width = 0;
            Height = 0;
            return false;
        }

        var height = Math.Max(MinWidth / 2, (int)Math.Round(width * (controlHeight / controlWidth)));
        if (width == Width && height == Height)
        {
            return true;
        }

        Width = width;
        Height = height;
        _accumulation = new float[width * height * 3];
        _frame = new float[width * height * 3];
        _glow = new float[width * height];
        _shaft = new float[width * height];
        _vignette = new float[width * height];
        _plateRow = new float[height * 3];
        _pixels = new byte[width * height * 4];
        BuildShapes();
        return true;
    }

    /// <summary>Drops the accumulated trails, so the next frame starts from black.</summary>
    public void Clear()
    {
        Array.Clear(_accumulation);
    }

    /// <summary>
    /// Decays the trails, splats the field's motes into them, lays the plate, the
    /// haze and the shaft over the result and packs it to BGRA.
    /// </summary>
    /// <param name="field">The field to draw.</param>
    /// <param name="frame">The timeline's gains for this instant.</param>
    /// <param name="seconds">Elapsed time since the last compose; drives the trail decay.</param>
    public void Compose(BootIntroField field, in BootIntroFrame frame, double seconds)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (Width == 0 || Height == 0)
        {
            return;
        }

        Decay(seconds);
        Splat(field, frame);
        LayBackdrop(frame);
        LayCore(field, frame);
        Pack();
    }

    // The knot's own light: a tight near-white core, a mid halo and a wide wash,
    // laid after the backdrop so the trail decay never eats it. It is a standing
    // feature of the frame rather than something a mote left behind, and it is
    // what the reference's bloom actually is - without it the sequence is a field
    // of filaments with nothing lighting them.
    //
    // Walked only over the texels the wash reaches, so the two thirds of the frame
    // it never touches cost nothing.
    private void LayCore(BootIntroField field, in BootIntroFrame frame)
    {
        var energy = frame.CoreEnergy;
        if (energy <= 0.001)
        {
            return;
        }

        // The core is whiter than the motes around it. In the reference it is the
        // one thing that is never fully coloured: its brightest pixels read #2176F7
        // through the blue stretch and a warm white through the gold.
        BootIntroPalette.SampleMote(frame, 0.55, 0.5, 0.0, out var r, out var g, out var b);
        var peak = Math.Max(Math.Max(r, g), Math.Max(b, 1e-6));
        var coreR = (float)Lerp(r / peak, 1.0, 0.42);
        var coreG = (float)Lerp(g / peak, 1.0, 0.42);
        var coreB = (float)Lerp(b / peak, 1.0, 0.42);

        var aspect = (double)Width / Math.Max(1, Height);
        var centreX = field.KnotX * Width;
        var centreY = field.KnotY * Height;
        var reach = (int)Math.Ceiling(WashRadius * Height * 2.6);
        var left = Math.Max(0, (int)centreX - (int)(reach * aspect));
        var right = Math.Min(Width - 1, (int)centreX + (int)(reach * aspect));
        var top = Math.Max(0, (int)centreY - reach);
        var bottom = Math.Min(Height - 1, (int)centreY + reach);

        var frameSpan = _frame.AsSpan();
        var scale = 1.0 / Math.Max(1, Height);

        for (var y = top; y <= bottom; y++)
        {
            var dy = (y + 0.5 - centreY) * scale;
            var rowBase = y * Width;

            for (var x = left; x <= right; x++)
            {
                var dx = (x + 0.5 - centreX) * scale;
                var slot = (int)(((dx * dx) + (dy * dy)) * CoreLookupScale);
                if (slot >= CoreLookup)
                {
                    continue;
                }

                // A table, not three exponentials. The wash covers a third of the
                // buffer, and evaluating Math.Exp three times per texel over that
                // area was most of a twenty-millisecond frame on its own.
                var amount = Core[slot] * (float)energy;
                if (amount <= 0.0008f)
                {
                    continue;
                }

                var offset = (rowBase + x) * 3;
                frameSpan[offset] += coreR * amount;
                frameSpan[offset + 1] += coreG * amount;
                frameSpan[offset + 2] += coreB * amount;
            }
        }
    }

    // Trails fade on a half life rather than a fixed step, so a dropped frame
    // shortens no tail. Vectorised: this is the one loop long enough for the width
    // to pay for itself.
    private void Decay(double seconds)
    {
        if (seconds <= 0.0)
        {
            return;
        }

        var factor = (float)Math.Pow(
            0.5, Math.Min(seconds, BootIntroField.MaxStepSeconds) / TrailHalfLife);

        var accumulation = _accumulation.AsSpan();
        var lanes = Vector<float>.Count;
        var wide = new Vector<float>(factor);
        var i = 0;
        for (; i + lanes <= accumulation.Length; i += lanes)
        {
            var slice = accumulation.Slice(i, lanes);
            (new Vector<float>(slice) * wide).CopyTo(slice);
        }

        for (; i < accumulation.Length; i++)
        {
            accumulation[i] *= factor;
        }
    }

    // A mote is a point, deposited sub-texel and energy normalised. The head is
    // small and bright; the tail is whatever the decay has not eaten yet.
    private void Splat(BootIntroField field, in BootIntroFrame frame)
    {
        var motes = field.Motes;
        if (motes.Length == 0 || frame.Particles <= 0.0)
        {
            return;
        }

        var accumulation = _accumulation.AsSpan();
        Span<double> kernel = stackalloc double[MaxKernel * MaxKernel];

        // A mote lays a line of trail whose length in texels grows with the tier,
        // while the energy it deposits per frame does not, so without this a finer
        // buffer is a dimmer one: the same light spread thinner, which is the exact
        // failure this design exists to avoid. Quoted against the tier the gains
        // were tuned at.
        var density = Height / TuningHeight;

        for (var i = 0; i < motes.Length; i++)
        {
            ref readonly var mote = ref motes[i];
            if (mote.Envelope <= 0.01)
            {
                continue;
            }

            var weight = mote.Envelope
                * (0.28 + (0.72 * mote.Depth))
                * frame.Particles
                * 0.80
                * density;
            if (weight <= 0.0015)
            {
                continue;
            }

            var centreX = (mote.X * Width) - 0.5;
            var centreY = (mote.Y * Height) - 0.5;
            if (centreX < -4 || centreY < -4 || centreX > Width + 4 || centreY > Height + 4)
            {
                continue;
            }

            BootIntroPalette.SampleMote(
                frame, mote.Tone, mote.Spectrum, mote.InsideKnot, out var r, out var g, out var b);

            // Radius in texels. Small on purpose: the mark's width on screen comes
            // from the filament behind it, and a wide stamp is how the earlier
            // renderer turned the same light into fog.
            var radius = 0.55 + (mote.Depth * 0.85);
            var left = (int)Math.Floor(centreX - radius);
            var right = (int)Math.Ceiling(centreX + radius);
            var top = (int)Math.Floor(centreY - radius);
            var bottom = (int)Math.Ceiling(centreY + radius);
            var span = right - left + 1;
            if (span > MaxKernel || bottom - top + 1 > MaxKernel)
            {
                continue;
            }

            // Build the kernel and its sum first, so the mote deposits the same
            // energy wherever it falls between texels.
            var inverse = 1.0 / (radius * radius);
            var total = 0.0;
            var slot = 0;
            for (var y = top; y <= bottom; y++)
            {
                var dy = y - centreY;
                for (var x = left; x <= right; x++, slot++)
                {
                    var dx = x - centreX;
                    var squared = ((dx * dx) + (dy * dy)) * inverse;
                    if (squared >= 1.0)
                    {
                        kernel[slot] = 0.0;
                        continue;
                    }

                    // Quartic falloff: most of the energy lands in the middle
                    // texel instead of being spread across the kernel.
                    var falloff = 1.0 - squared;
                    falloff *= falloff;
                    kernel[slot] = falloff;
                    total += falloff;
                }
            }

            if (total <= 1e-9)
            {
                continue;
            }

            var scale = weight / total;
            slot = 0;
            for (var y = top; y <= bottom; y++)
            {
                var inRow = y >= 0 && y < Height;
                var rowBase = y * Width;
                for (var x = left; x <= right; x++, slot++)
                {
                    if (!inRow || x < 0 || x >= Width || kernel[slot] <= 0.0)
                    {
                        continue;
                    }

                    var amount = (float)(kernel[slot] * scale);
                    var offset = (rowBase + x) * 3;
                    accumulation[offset] += (float)r * amount;
                    accumulation[offset + 1] += (float)g * amount;
                    accumulation[offset + 2] += (float)b * amount;
                }
            }
        }
    }

    // The plate, its light, the teal, the warm haze and the shaft, laid into the
    // scratch frame over the trails. Everything whose shape depends only on the
    // buffer size was built once by BuildShapes, so this is one pass of multiply
    // and add per texel.
    private void LayBackdrop(in BootIntroFrame frame)
    {
        BuildPlateRows(frame);

        // The light term. Record 21's own light colour is what the colorchange
        // flashes; record 9's is what the gold plate carries.
        var lightR = (float)((BootIntroPalette.TealLight.R * ((frame.PlateBlue * 0.03) + frame.PlateTeal))
                             + (BootIntroPalette.GoldLight.R * frame.PlateGold * 0.55));
        var lightG = (float)((BootIntroPalette.TealLight.G * ((frame.PlateBlue * 0.03) + frame.PlateTeal))
                             + (BootIntroPalette.GoldLight.G * frame.PlateGold * 0.55));
        var lightB = (float)((BootIntroPalette.TealLight.B * ((frame.PlateBlue * 0.03) + frame.PlateTeal))
                             + (BootIntroPalette.GoldLight.B * frame.PlateGold * 0.55));

        // The room, not the plate. Record 9 has no blue at all and the reference's
        // warm frames plainly do. A second after the warm plate lands the
        // reference's mean saturation falls from 0.35 to 0.13, so the haze slides
        // into an equal-luminance grey rather than dimming.
        var haze = frame.Haze;
        var grey = (BootIntroPalette.WarmHaze.R * 0.2126)
                   + (BootIntroPalette.WarmHaze.G * 0.7152)
                   + (BootIntroPalette.WarmHaze.B * 0.0722);
        var hazeR = (float)(haze * Lerp(BootIntroPalette.WarmHaze.R, grey, frame.Desaturate));
        var hazeG = (float)(haze * Lerp(BootIntroPalette.WarmHaze.G, grey, frame.Desaturate));
        var hazeB = (float)(haze * Lerp(BootIntroPalette.WarmHaze.B, grey, frame.Desaturate));

        var shaftR = (float)(frame.Shaft * Lerp(1.0, BootIntroPalette.ShaftWarm.R, frame.ShaftWarmth));
        var shaftG = (float)(frame.Shaft * Lerp(1.0, BootIntroPalette.ShaftWarm.G, frame.ShaftWarmth));
        var shaftB = (float)(frame.Shaft * Lerp(1.0, BootIntroPalette.ShaftWarm.B, frame.ShaftWarmth));

        var accumulation = _accumulation.AsSpan();
        var target = _frame.AsSpan();
        var glow = _glow.AsSpan();
        var shaftShape = _shaft.AsSpan();
        var rows = _plateRow.AsSpan();

        for (var y = 0; y < Height; y++)
        {
            var plateR = rows[y * 3];
            var plateG = rows[(y * 3) + 1];
            var plateB = rows[(y * 3) + 2];
            var rowBase = y * Width;

            for (var x = 0; x < Width; x++)
            {
                var index = rowBase + x;
                var lobe = glow[index];
                var beam = shaftShape[index];

                // The haze fills the room, but it is lit from one place: a flat
                // fill turns the warm phase into a painted orange card with no
                // structure in it at all, which is what the first pass drew.
                var fill = 0.22f + (0.78f * lobe);
                var offset = index * 3;

                target[offset] = accumulation[offset] + plateR + (lobe * lightR) + (fill * hazeR) + (beam * shaftR);
                target[offset + 1] = accumulation[offset + 1] + plateG + (lobe * lightG) + (fill * hazeG) + (beam * shaftG);
                target[offset + 2] = accumulation[offset + 2] + plateB + (lobe * lightB) + (fill * hazeB) + (beam * shaftB);
            }
        }
    }

    private void BuildPlateRows(in BootIntroFrame frame)
    {
        var rows = _plateRow.AsSpan();
        var lastRow = Math.Max(1, Height - 1);
        var blue = BootIntroPalette.BluePlate;
        var gold = BootIntroPalette.GoldPlate;

        for (var y = 0; y < Height; y++)
        {
            var v = (double)y / lastRow;
            blue.Sample(v, out var br, out var bg, out var bb);
            gold.Sample(v, out var gr, out var gg, out var gb);

            // The teal flash is the blue plate's own gradient driven hard under
            // record 21's light, which the light term above carries.
            var blueGain = frame.PlateBlue + (frame.PlateTeal * 0.35);
            rows[y * 3] = (float)((br * blueGain) + (gr * frame.PlateGold));
            rows[(y * 3) + 1] = (float)((bg * blueGain) + (gg * frame.PlateGold));
            rows[(y * 3) + 2] = (float)((bb * blueGain) + (gb * frame.PlateGold));
        }
    }

    /// <summary>Where the highlight starts losing its colour, in linear units.</summary>
    private const float DesaturateKnee = 0.72f;

    /// <summary>How far a blown highlight is allowed to go toward white.</summary>
    private const float DesaturateCeiling = 0.80f;

    // Tone map and pack. One divide per texel on the brightest channel, so the
    // roll-off itself does not skew the hue, and then a deliberate slide toward
    // white above the knee.
    //
    // That slide is not a stylistic choice. The reference's motes are a saturated
    // yellow ramp - wave preset 9 as authored - and its bright cores measure warm
    // white, #FCDAD7 and #E1B79E, because a mote is an additive light and a bright
    // additive light reads white however coloured the thing emitting it is.
    // Preserving hue all the way up, which is right for a background sheen, turns
    // this sequence into a starburst of yellow sticks.
    //
    // Opaque: this is the whole frame, not a layer over something else.
    private void Pack()
    {
        var frame = _frame.AsSpan();
        var pixels = _pixels.AsSpan();
        var vignette = _vignette.AsSpan();
        var count = Width * Height;

        for (var i = 0; i < count; i++)
        {
            var source = i * 3;
            var shade = vignette[i];
            var r = frame[source] * shade;
            var g = frame[source + 1] * shade;
            var b = frame[source + 2] * shade;

            var peak = r > g ? r : g;
            if (b > peak)
            {
                peak = b;
            }

            var target = i * 4;
            if (peak <= 0.0005f)
            {
                pixels[target] = 0;
                pixels[target + 1] = 0;
                pixels[target + 2] = 0;
                pixels[target + 3] = 255;
                continue;
            }

            var roll = 1.0f / (1.0f + peak);
            var desaturate = peak <= DesaturateKnee
                ? 0.0f
                : Math.Min((peak - DesaturateKnee) * 0.55f, DesaturateCeiling);

            if (desaturate > 0.0f)
            {
                r += (peak - r) * desaturate;
                g += (peak - g) * desaturate;
                b += (peak - b) * desaturate;
            }

            pixels[target] = ToByte(b * roll);
            pixels[target + 1] = ToByte(g * roll);
            pixels[target + 2] = ToByte(r * roll);
            pixels[target + 3] = 255;
        }
    }

    // The two fields that depend only on the buffer size: the plate's light lobe
    // and the shaft's beam profile. Rebuilt on resize and read every frame.
    private void BuildShapes()
    {
        var aspect = (double)Width / Math.Max(1, Height);
        var angle = ShaftBearing * Math.Tau;
        var axisX = Math.Sin(angle);
        var axisY = Math.Cos(angle);
        var apexX = ShaftApexX * aspect;

        for (var y = 0; y < Height; y++)
        {
            var v = (y + 0.5) / Height;
            var lightDy = v - LightY;
            var shaftDy = v - ShaftApexY;
            var rowBase = y * Width;

            for (var x = 0; x < Width; x++)
            {
                var u = (x + 0.5) / Width;
                var lightDx = (u - LightX) * aspect;
                _glow[rowBase + x] = (float)Math.Exp(
                    -((lightDx * lightDx) + (lightDy * lightDy)) * LightFalloff);

                var vx = (u * 2.0) - 1.0;
                var vy = (v * 2.0) - 1.0;
                _vignette[rowBase + x] =
                    (float)(1.0 - (VignetteDepth * Math.Min(1.0, (vx * vx) + (vy * vy))));

                var dx = (u * aspect) - apexX;
                var along = (dx * axisX) + (shaftDy * axisY);
                if (along <= 0.0 || along >= ShaftReach)
                {
                    _shaft[rowBase + x] = 0.0f;
                    continue;
                }

                var across = Math.Abs((dx * axisY) - (shaftDy * axisX));
                var half = ShaftWaist + (ShaftSpread * along);
                var edge = across / half;
                if (edge >= 1.0)
                {
                    _shaft[rowBase + x] = 0.0f;
                    continue;
                }

                // Quartic across the beam and a smooth run-out along it. Both
                // soft: a beam with an edge on it reads as a drawn triangle.
                var profile = 1.0 - (edge * edge);
                profile *= profile;
                var reach = 1.0 - (along / ShaftReach);
                _shaft[rowBase + x] = (float)(profile * reach * reach * reach);
            }
        }
    }

    // The knot's light as a function of squared distance: a tight near-white core,
    // a mid halo and a wide wash. Baked once for the process.
    private static float[] BuildCoreLookup()
    {
        var table = new float[CoreLookup];
        for (var i = 0; i < CoreLookup; i++)
        {
            var squared = (i + 0.5) / CoreLookupScale;
            table[i] = (float)(
                (Math.Exp(-squared / (CoreRadius * CoreRadius)) * 0.95)
                + (Math.Exp(-squared / (HaloRadius * HaloRadius)) * 0.44)
                + (Math.Exp(-squared / (WashRadius * WashRadius)) * 0.055));
        }

        return table;
    }

    private static double Lerp(double from, double to, double amount) => from + ((to - from) * amount);

    private static byte ToByte(float value)
    {
        var scaled = (int)((value * 255.0f) + 0.5f);
        return scaled <= 0 ? (byte)0 : scaled >= 255 ? (byte)255 : (byte)scaled;
    }
}







