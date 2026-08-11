// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Prosperismo.GUI;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.GUI.SystemAssets;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// A single entry in a <see cref="ShellTileRow"/>. The host supplies these;
/// the row owns all layout, motion, and selection.
///
/// This is the source's experience record narrowed to what a tile draws:
/// <c>experienceName</c>, <c>iconPath</c> and <c>fallbackIconPath</c>
/// (HOME m47). Nothing else on the record reaches the screen.
/// </summary>
public sealed record ShellTile
{
    public ShellTile(
        string title,
        string? subtitle = null,
        IImage? icon = null,
        object? tag = null)
    {
        Title = title ?? string.Empty;
        Subtitle = subtitle;
        Icon = icon;
        Tag = tag;
    }

    /// <summary><c>experienceName</c>: the one string the switcher shows, and
    /// only beside the focused tile.</summary>
    public string Title { get; init; }

    /// <summary>
    /// Caller-side detail. The switcher tile has no sub-label: its stylesheet
    /// (HOME m210) carries an image wrapper and an image, and the only text in
    /// the tile component at all is a <c>primaryTitleId</c> gated behind the
    /// devkit setting <c>isHomeTitleIdDisplayEnabled</c> (HOME m209, m551), so
    /// a retail console never prints one. Nothing in this row renders it.
    /// </summary>
    public string? Subtitle { get; init; }

    /// <summary><c>iconPath</c>. When null the tile falls back the way the
    /// source's Image does, to the app fallback icon.</summary>
    public IImage? Icon { get; init; }

    /// <summary>Caller payload round-tripped through the selection events.</summary>
    public object? Tag { get; init; }

    /// <summary>
    /// <c>metadata.platformType</c>. The title strip prints it as a tag beside
    /// the name, except where it says nothing: unset, or the console's own
    /// platform. See <see cref="ShellTitleMetrics.ShowPlatformTag"/>.
    /// </summary>
    public string? PlatformType { get; init; }

    /// <summary>
    /// <c>metadata.packageType</c>, tagged beside the name except for unset,
    /// FULL and DEMO. See <see cref="ShellTitleMetrics.ShowPackageTag"/>.
    /// </summary>
    public string? PackageType { get; init; }

    /// <summary><c>metadata.entitlementIconId</c> is present.</summary>
    public bool HasEntitlementIcon { get; init; }

    /// <summary><c>metadata.storageIconId</c> is present.</summary>
    public bool HasStorageIcon { get; init; }
}

/// <summary>
/// The interaction states a shell surface distinguishes, HOME m720. It is not a
/// binary selected/not: a tile the row still points at while another region owns
/// focus is GLANCED, and the console drives real differences off that (the
/// action indicator is OPACITY.ACTION_INDICATOR MIN 0.7 when glanced against MAX
/// 1 when focused, EXACT HOME m719:51159-51161 and m737:53635).
/// </summary>
public enum ShellInteractionState
{
    /// <summary>The row points at the tile but does not own focus.</summary>
    Glanced,

    /// <summary>The tile is focused.</summary>
    Focused,

    /// <summary>The tile is being activated.</summary>
    Action,
}

/// <summary>Payload for <see cref="ShellTileRow.SelectionChanged"/> and
/// <see cref="ShellTileRow.ItemActivated"/>.</summary>
public sealed class ShellTileEventArgs : EventArgs
{
    public ShellTileEventArgs(int index, ShellTile? tile)
    {
        Index = index;
        Tile = tile;
    }

    /// <summary>Selected index, or -1 when the row is empty.</summary>
    public int Index { get; }

    /// <summary>The selected tile, or null when the row is empty.</summary>
    public ShellTile? Tile { get; }
}

/// <summary>One row of the shell's <c>HORIZONTAL_SPACING</c> packing table.</summary>
/// <param name="TileWidth">Tile width the row is keyed on.</param>
/// <param name="HowManyCanFit">Tiles that fit across the strand at that width.</param>
/// <param name="Margin">Gutter between two tiles.</param>
public readonly record struct ShellStrandPacking(double TileWidth, int HowManyCanFit, double Margin)
{
    /// <summary><c>tileSizingWithMargin</c>: the pitch two neighbours sit on.</summary>
    public double Pitch => TileWidth + Margin;
}

/// <summary>
/// The content strand's packing table, <c>HORIZONTAL_SPACING[1576]</c>.
///
/// The shell does not compute a gutter from a tile width: it looks the width up.
/// A width that is not in this table falls through to a rounded division and the
/// shell logs a strand error, so a surface that wants to stay on the console's
/// own grid has to choose one of these six and take the gutter that comes with
/// it. That is why this is a lookup and not arithmetic.
/// </summary>
public static class ShellStrandSpacing
{
    // HORIZONTAL_SPACING[1576] is declared once, in Ps5HomeMetrics, keyed on the
    // tile width. This is that same table read back in the row's own shape,
    // ascending by width, rather than a second transcription of it.
    private static readonly ShellStrandPacking[] Table =
        Ps5HomeMetrics.HorizontalPacking
            .OrderBy(entry => entry.Key)
            .Select(entry => new ShellStrandPacking(entry.Key, entry.Value.HowManyCanFit, entry.Value.Margin))
            .ToArray();

    /// <summary>Every blessed width, ascending.</summary>
    public static IReadOnlyList<ShellStrandPacking> All => Table;

    /// <summary>Looks <paramref name="tileWidth"/> up, or returns false when the
    /// width is one the shell would report as a strand error.</summary>
    public static bool TryGet(double tileWidth, out ShellStrandPacking packing)
    {
        foreach (var row in Table)
        {
            if (Math.Abs(row.TileWidth - tileWidth) < 0.0001)
            {
                packing = row;
                return true;
            }
        }

        packing = default;
        return false;
    }
}

/// <summary>
/// The strand's position solver, HOME m531
/// (<c>packages/rnps-js-modules-strand</c>). This is the source's own
/// <c>calculate</c> and the state it needs, kept in the source's shape so the
/// two can be diffed by eye rather than by trust:
///
/// <code>
/// function o(e, t, a, n, {offsets: r}, u, o, i) {
///     var l = t(u).width;
///     if (-1 === i) return u * (l + n) - l * e / 2;
///     var c = r[u] + (u - i) * (l + n);
///     return u &lt; i ? c -= r[u] + a - n : u &gt; i &amp;&amp; (c += r[u] + a - n), c
/// }
/// </code>
/// </summary>
internal static class ShellStrandPositions
{
    /// <summary>
    /// <c>updateState</c>'s offset table, <c>l[S] = h / 2 - I / 2</c> with
    /// <c>h = I * selectedItemScale</c>. Every entry is the same number because
    /// every item box is the same width, and that number (31 at design size) is
    /// what makes the focused tile land on the inset exactly rather than 31 px
    /// past it.
    /// </summary>
    internal static double Offset(double itemWidth, double selectedItemScale) =>
        ((itemWidth * selectedItemScale) / 2.0) - (itemWidth / 2.0);

    /// <summary>
    /// <c>calculate</c>: the settled translateX of one item. The item box is
    /// <paramref name="itemWidth"/> square and is scaled about its own centre,
    /// so this is a translation of the box, not of the drawn edge.
    /// </summary>
    /// <param name="selectedItemScale">e, <c>EXPERIENCE_SCALE</c>.</param>
    /// <param name="itemWidth">t(u).width, from <c>getItemLayout</c>.</param>
    /// <param name="focusedMargin">a, the gap each side of the focused item.</param>
    /// <param name="itemMargin">n, the gap between two resting items.</param>
    /// <param name="offsets">r[u], from <see cref="Offset"/>.</param>
    /// <param name="index">u, the item's index in the data.</param>
    /// <param name="selectedIndex">i, or -1 when nothing is selected.</param>
    internal static double Calculate(
        double selectedItemScale,
        double itemWidth,
        double focusedMargin,
        double itemMargin,
        double offsets,
        int index,
        int selectedIndex)
    {
        double l = itemWidth;
        if (selectedIndex == -1)
        {
            return (index * (l + itemMargin)) - (l * selectedItemScale / 2.0);
        }

        double c = offsets + ((index - selectedIndex) * (l + itemMargin));
        if (index < selectedIndex)
        {
            c -= offsets + focusedMargin - itemMargin;
        }
        else if (index > selectedIndex)
        {
            c += offsets + focusedMargin - itemMargin;
        }

        return c;
    }
}

/// <summary>
/// <c>useMat</c>, HOME m573: the overflow tail fade. The only dimming the
/// console applies to the switcher row is a black mat over the 8th, 9th and
/// 10th tile past the selection, at 0.05, 0.2 and 0.4, which reads as "there is
/// more row past here" rather than as a focus affordance.
///
/// <code>
/// backgroundColor: r[t].interpolate({
///     inputRange:  [0, .05, .2, .4],
///     outputRange: ["rgba(2, 4, 8, 0)", "rgba(2, 4, 8, 0.05)", "rgba(2, 4, 8, 0.2)", "rgba(2, 4, 8, 0.4)"]
/// })
/// ...
/// var e = {8: .05, 9: .2, 10: .4};
/// return n in e ? e[n] : 0
/// </code>
/// </summary>
internal static class ShellExperienceMat
{
    /// <summary>The mat's own colour, before the alpha the distance picks. It is
    /// the background gradient's base (<c>rgba(2, 4, 8, x)</c>, HOME:41609), not
    /// a black, so the mat sinks a tile into the backdrop rather than greying
    /// it.</summary>
    private static readonly Color Basemat = Color.FromRgb(2, 4, 8);

    /// <summary>The animated value's input range.</summary>
    private static readonly double[] InputRange = [0.0, 0.05, 0.2, 0.4];

    /// <summary>The alpha each input maps to, in the same order.</summary>
    private static readonly double[] OutputAlpha = [0.0, 0.05, 0.2, 0.4];

    /// <summary>
    /// <c>{8: .05, 9: .2, 10: .4}</c> keyed on the tile's distance past the
    /// selection. Tiles before the selection, and the first eight after it, take
    /// no mat at all.
    /// </summary>
    internal static double MatValue(int distance) => distance switch
    {
        8 => 0.05,
        9 => 0.2,
        10 => 0.4,
        _ => 0.0,
    };

    /// <summary>Distance the source feeds the table:
    /// <c>index - Math.max(0, selectedIndex)</c>.</summary>
    internal static int Distance(int index, int selectedIndex) =>
        index - Math.Max(0, selectedIndex);

    /// <summary>The interpolation the animated value performs. Kept as the real
    /// piecewise mapping rather than collapsed to identity, because the input
    /// and output ranges only happen to carry the same numbers.</summary>
    internal static double Interpolate(double value)
    {
        if (value <= InputRange[0])
        {
            return OutputAlpha[0];
        }

        for (int i = 1; i < InputRange.Length; i++)
        {
            if (value <= InputRange[i])
            {
                double span = InputRange[i] - InputRange[i - 1];
                double t = span > 0.0 ? (value - InputRange[i - 1]) / span : 0.0;
                return OutputAlpha[i - 1] + (t * (OutputAlpha[i] - OutputAlpha[i - 1]));
            }
        }

        return OutputAlpha[^1];
    }

    /// <summary>The mat brush for one tile, or null when it takes none.</summary>
    internal static IBrush? For(int index, int selectedIndex)
    {
        double alpha = Interpolate(MatValue(Distance(index, selectedIndex)));
        return alpha <= 0.0
            ? null
            : new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255.0), Basemat.R, Basemat.G, Basemat.B));
    }
}

/// <summary>
/// One spring, expressed the way the shell expresses it: stiffness, damping and
/// mass, with no duration anywhere. The strand's focus move is a spring, so a
/// tween cannot stand in for it however carefully the curve is picked.
/// </summary>
internal readonly record struct ShellSpringConfig(
    double Stiffness,
    double Damping,
    double Mass,
    bool OvershootClamping)
{
    /// <summary>
    /// The strand's own focus-move spring. Recovered from the home bundle's
    /// strand module, where it is the default the <c>springOptions</c> atom
    /// falls back to, so it is what actually runs. Do not "round" these: at
    /// mass 0.2 the critical damping is 2*sqrt(400*0.2) = 17.9, so damping 50
    /// puts the strand well into the overdamped region and the move reads as a
    /// firm settle rather than a bounce.
    /// </summary>
    public static readonly ShellSpringConfig StrandFocus = new(400.0, 50.0, 0.2, true);

    /// <summary>SPRING_OPTIONS_SLOWER, the spring the boot reveal uses.</summary>
    public static readonly ShellSpringConfig Slower = From(Ps5HomeMetrics.SpringSlower);

    /// <summary>
    /// SPRING_OPTIONS_SLOW. The space cross-fade and the late half of the boot
    /// reveal (the system band and the title) both run on this one.
    /// </summary>
    public static readonly ShellSpringConfig Slow = From(Ps5HomeMetrics.SpringSlow);

    /// <summary>SPRING_OPTIONS_FAST. Unclamped, unlike the three above.</summary>
    public static readonly ShellSpringConfig Fast = From(Ps5HomeMetrics.SpringFast);

    /// <summary>SPRING_OPTIONS_FASTER. Unclamped.</summary>
    public static readonly ShellSpringConfig Faster = From(Ps5HomeMetrics.SpringFaster);

    /// <summary>
    /// Reads one of the bundle's four named springs out of the spec file. The
    /// four SPRING_OPTIONS presets are declared once, in
    /// <see cref="Ps5HomeMetrics"/>, and this is the integrator's view of them;
    /// <see cref="StrandFocus"/> is not among them because it is the strand
    /// module's own default rather than one of the four.
    /// </summary>
    private static ShellSpringConfig From(SpringOptions options) =>
        new(options.Stiffness, options.Damping, options.Mass, options.OvershootClamping);
}

/// <summary>
/// the shell's parameters. Avalonia has no spring primitive and its transitions
/// are all duration plus easing, so the row owns this and drives it off its own
/// frame tick.
///
/// <para>NPXS40141.base.js <c>SpringAnimation.start</c> transfers the previous
/// animation's last position, velocity and timestamp when focus changes before
/// a spring settles. <see cref="SpringTo"/> mirrors that cancellation contract:
/// the latest target replaces the old one while the current physical state is
/// retained.</para>
/// </summary>
internal sealed class ShellSpring
{
    /// <summary>
    /// The runtime clamps one update to 64 ms. Its retained timestamp leaves
    /// the remainder for later frames, so this implementation keeps the same
    /// backlog instead of silently discarding a long frame.
    /// </summary>
    private const double MaxAdvance = 0.064;

    private readonly double _restDisplacement;
    private readonly double _restVelocity;

    private ShellSpringConfig _config = ShellSpringConfig.StrandFocus;
    private double _value;
    private double _velocity;
    private double _target;
    private double _startValue;
    private double _initialVelocity;
    private double _elapsed;
    private double _pendingTime;
    private bool _settled = true;

    /// <param name="restDisplacement">How close to the target counts as
    /// arrived, in the spring's own units.</param>
    /// <param name="restVelocity">How slow counts as stopped, per second.</param>
    public ShellSpring(double restDisplacement, double restVelocity)
    {
        _restDisplacement = restDisplacement;
        _restVelocity = restVelocity;
    }

    /// <summary>Current position.</summary>
    public double Value => _value;

    /// <summary>Where the spring is heading.</summary>
    public double Target => _target;

    /// <summary>Current velocity, per second.</summary>
    public double Velocity => _velocity;

    /// <summary>True once the spring has arrived and stopped.</summary>
    public bool IsSettled => _settled;

    /// <summary>Places the spring at <paramref name="value"/> with no motion.
    /// Used to seed a tile before its first frame.</summary>
    public void SnapTo(double value)
    {
        _value = value;
        _target = value;
        _startValue = value;
        _velocity = 0.0;
        _initialVelocity = 0.0;
        _elapsed = 0.0;
        _pendingTime = 0.0;
        _settled = true;
    }

    /// <summary>
    /// Retargets the spring. Velocity carries over, which is what makes a focus
    /// move interrupted half way through continue smoothly rather than restart.
    /// </summary>
    public void SpringTo(double target, ShellSpringConfig config)
    {
        _config = config;
        _startValue = _value;
        _initialVelocity = _velocity;
        _elapsed = 0.0;
        _target = target;

        if (Math.Abs(_target - _value) <= _restDisplacement && Math.Abs(_velocity) <= _restVelocity)
        {
            Settle();
            return;
        }

        _settled = false;
    }

    /// <summary>Drops the spring onto its target immediately.</summary>
    public void Settle()
    {
        _value = _target;
        _startValue = _target;
        _velocity = 0.0;
        _initialVelocity = 0.0;
        _elapsed = 0.0;
        _pendingTime = 0.0;
        _settled = true;
    }

    /// <summary>
    /// Advances by <paramref name="seconds"/>. Returns true while the spring
    /// still needs frames.
    /// </summary>
    public bool Advance(double seconds)
    {
        if (_settled)
        {
            return false;
        }

        if (!(seconds > 0.0) || double.IsNaN(seconds))
        {
            return true;
        }

        _pendingTime += seconds;
        double step = Math.Min(_pendingTime, MaxAdvance);
        _pendingTime -= step;
        _elapsed += step;

        double mass = Math.Max(1e-6, _config.Mass);
        double stiffness = Math.Max(1e-6, _config.Stiffness);
        double damping = Math.Max(1e-6, _config.Damping);
        double dampingRatio = damping / (2.0 * Math.Sqrt(stiffness * mass));
        double naturalFrequency = Math.Sqrt(stiffness / mass);
        double displacement = _target - _startValue;
        double negatedInitialVelocity = -_initialVelocity;
        double next;
        double nextVelocity;

        if (dampingRatio < 1.0)
        {
            double dampedFrequency =
                naturalFrequency * Math.Sqrt(1.0 - (dampingRatio * dampingRatio));
            double decay = Math.Exp(-dampingRatio * naturalFrequency * _elapsed);
            double phase = dampedFrequency * _elapsed;
            double velocityTerm =
                (negatedInitialVelocity + (dampingRatio * naturalFrequency * displacement)) /
                dampedFrequency;

            next = _target - decay *
                ((velocityTerm * Math.Sin(phase)) + (displacement * Math.Cos(phase)));
            nextVelocity =
                (dampingRatio * naturalFrequency * decay *
                    ((Math.Sin(phase) * velocityTerm) +
                     (displacement * Math.Cos(phase)))) -
                (decay *
                    ((Math.Cos(phase) *
                        (negatedInitialVelocity +
                         (dampingRatio * naturalFrequency * displacement))) -
                     (dampedFrequency * displacement * Math.Sin(phase))));
        }
        else
        {
            // for every damping ratio >= 1, including StrandFocus (ratio 2.8).
            double decay = Math.Exp(-naturalFrequency * _elapsed);
            next = _target - decay *
                (displacement +
                 ((negatedInitialVelocity + (naturalFrequency * displacement)) * _elapsed));
            nextVelocity = decay *
                ((negatedInitialVelocity * ((_elapsed * naturalFrequency) - 1.0)) +
                 (_elapsed * displacement * naturalFrequency * naturalFrequency));
        }

        bool crossed = _config.OvershootClamping && Crossed(_value, next, _target);
        _value = next;
        _velocity = nextVelocity;

        if (crossed ||
            (Math.Abs(_target - _value) <= _restDisplacement &&
             Math.Abs(_velocity) <= _restVelocity))
        {
            Settle();
            return false;
        }

        return true;
    }

    private static bool Crossed(double from, double to, double target) =>
        (from <= target && to > target) || (from >= target && to < target);
}

/// <summary>
/// The resting geometry of the shell's experience strand, as arithmetic rather
/// than as a picture. Pure and allocation-free so the layout can be checked
/// without a render surface.
///
/// All of the shape here is recovered from the home bundle's strand module and
/// its <c>calculate</c> routine, not from a screenshot or a design trace:
/// resting tiles are 106 px on a side, the focused tile is 168, tiles rest
/// <see cref="ItemMargin"/> apart and the focused tile clears its neighbours by
/// <see cref="FocusedMargin"/> on each side. Everything else falls out of that.
/// </summary>
internal readonly struct ShellStrandGeometry
{
    public ShellStrandGeometry(
        double focusedWidth,
        double restScale,
        double itemMargin,
        double focusedMargin,
        double focusedRadius,
        double anchorX)
    {
        FocusedWidth = focusedWidth;
        RestScale = restScale;
        ItemMargin = itemMargin;
        FocusedMargin = focusedMargin;
        FocusedRadius = focusedRadius;
        AnchorX = anchorX;
    }

    /// <summary>Side of the focused tile (SCALED_EXP_SIZE at design size).</summary>
    public double FocusedWidth { get; }

    /// <summary>Resting side as a fraction of the focused one; 106/168 at
    /// design size, the reciprocal of EXPERIENCE_SCALE.</summary>
    public double RestScale { get; }

    /// <summary>Gap between two resting tiles (itemMargin).</summary>
    public double ItemMargin { get; }

    /// <summary>Gap each side of the focused tile (focusedMargin).</summary>
    public double FocusedMargin { get; }

    /// <summary>Corner radius at the focused size.</summary>
    public double FocusedRadius { get; }

    /// <summary>X the focused tile's left edge pins to
    /// (SCALED_EXP_MARGIN_LEFT at design size).</summary>
    public double AnchorX { get; }

    /// <summary>Side of a resting tile.</summary>
    public double RestWidth => FocusedWidth * RestScale;

    /// <summary>Distance between the left edges of two resting neighbours.</summary>
    public double Pitch => RestWidth + ItemMargin;

    /// <summary>
    /// How far a resting tile's drawn left edge sits inside its full-size box.
    /// This is the bundle's <c>offset = w*s/2 - w/2</c>, 31 at design size, and
    /// it is why the focused tile lands on the inset exactly.
    /// </summary>
    public double CentreOffset => (FocusedWidth - RestWidth) / 2.0;

    /// <summary>
    /// Radius as a fraction of the side. The source computes the focused radius
    /// as <c>168 / 106 * 16</c>, so the radius is a ratio and not a constant;
    /// holding the ratio is what keeps it right at every interpolated size.
    /// </summary>
    public double CornerRadiusRatio => FocusedWidth > 0.0 ? FocusedRadius / FocusedWidth : 0.0;

    /// <summary>Corner radius for a tile drawn at <paramref name="side"/> px.</summary>
    public double RadiusAt(double side) => side * CornerRadiusRatio;

    /// <summary>
    /// <c>selectedItemScale</c> as the strand states it: the factor a resting
    /// item box is multiplied by when it becomes the focused one, 168/106.
    /// </summary>
    public double SelectedItemScale => RestScale > 0.0 ? 1.0 / RestScale : 1.0;

    /// <summary>
    /// Left edge of the drawn tile at <paramref name="index"/>, in the row's own
    /// coordinates, once the strand has settled.
    ///
    /// The bundle expresses this as a translateX per tile plus a scale about the
    /// box centre; edges are what a layout can be checked against, so this is
    /// that same translateX read back out as an edge.
    /// </summary>
    public double TileLeft(int index, int selected)
    {
        double drawnInset = FocusedWidth * (1.0 - ScaleFor(index, selected)) / 2.0;
        return AnchorX + TranslateXFor(index, selected) + drawnInset;
    }

    /// <summary>Settled scale of the tile at <paramref name="index"/>, relative
    /// to the full-size box.</summary>
    public double ScaleFor(int index, int selected) =>
        index == selected ? 1.0 : RestScale;

    /// <summary>
    /// Settled translateX of the tile at <paramref name="index"/>. Tiles are
    /// laid out one on top of another at <see cref="AnchorX"/> and pushed apart
    /// by this, which is the bundle's own arrangement and is what lets one
    /// spring per tile do all the work.
    ///
    /// The number comes straight out of the source's <c>calculate</c>; the only
    /// adjustment is the constant <see cref="CentreOffset"/>, because the source
    /// positions a resting box and scales it up while this row sizes the box at
    /// the focused side and scales it down. Both forms put the same edges in the
    /// same places, and this way the solver stays the source's.
    /// </summary>
    public double TranslateXFor(int index, int selected) =>
        ShellStrandPositions.Calculate(
            SelectedItemScale,
            RestWidth,
            FocusedMargin,
            ItemMargin,
            CentreOffset,
            index,
            selected < 0 ? -1 : selected)
        - CentreOffset;
}

/// <summary>
/// The home's experience switcher: the console's one row of tiles, and the
/// installed titles are its tiles. The focused tile is a genuinely bigger tile,
/// 168 px against the 106 px of its neighbours, and the size change is the whole
/// focus affordance. Unfocused tiles are not dimmed and the focused one is not
/// lifted; the console does neither, and inventing them is what makes a rebuild
/// read as "nearly PS5" instead of PS5.
///
/// Ported from the home bundle rather than described from it: HOME m25 (the
/// constants and the switcher stylesheet), m201 (the strand the Space component
/// builds), m530 (the strand itself and its spring), m531 (the position solver),
/// m540 and m551 (the tile), m210 (the tile's stylesheet), m47 (MAX_TILES and
/// the fallback icon), m573 (the overflow tail mat), m720 (the interaction
/// states) and m214 with m565 (the focused title beside the tile).
///
/// Geometry and motion are both recovered numbers, not approximations. Tiles
/// rest 8 px apart on a 114 px pitch, the focused tile clears its neighbours by
/// 16 px either side and pins its left edge to the content inset, the corner
/// radius is a fixed 0.150943 of the side at every size, and focus moves on a
/// spring with no duration at all.
///
/// The control is code-templated (no external ControlTheme required) so it can
/// be dropped into any window or a standalone preview. All navigation state
/// (<see cref="SelectedIndex"/>, <see cref="MoveFocus"/>, <see cref="SetSelectedIndex"/>)
/// is independent of the render surface, which keeps it unit-testable headless.
/// </summary>
public sealed class ShellTileRow : TemplatedControl
{
    // ---- HOME m25:3215-3224, the experience switcher's own constants ------
    // Lifted from packages/home-ui/src/components/ExperienceSwitcher with the
    // source's own names. Nothing here is a design guess and nothing here is
    // interchangeable with something rounder: the 8 and the 16 are two different
    // margins, and collapsing them into one gap is exactly what made an earlier
    // version of this row read as too airy.
    //
    // These are ALIASES, not a second copy. Ps5HomeMetrics is the spec file:
    // every number there carries its bundle line citation, and this row used to
    // carry its own literals beside them. Two files holding the same measured
    // number is one file holding a number nobody will remember to update, so the
    // literals moved out and what is left here is the switcher's own naming over
    // the one source of truth.

    /// <summary>EXPERIENCE_SIZE: side of a resting tile at design size.</summary>
    public const double ExperienceSize = Ps5HomeMetrics.ExperienceSize;

    /// <summary>SCALED_EXP_SIZE: side of the focused tile at design size.</summary>
    public const double ScaledExperienceSize = Ps5HomeMetrics.ScaledExperienceSize;

    /// <summary>EXPERIENCE_SCALE, <c>168 / 106</c>. The focus affordance, whole.</summary>
    public const double ExperienceScale = Ps5HomeMetrics.ExperienceScale;

    /// <summary>SCALED_EXP_MARGIN_LEFT: x the focused tile's left edge pins to.</summary>
    public const double ScaledExpMarginLeft = Ps5HomeMetrics.ScaledExperienceMarginLeft;

    /// <summary>MINIMIZED_EXP_MARGIN_TOP, for the icon once a title is running.</summary>
    public const double MinimizedExpMarginTop = Ps5HomeMetrics.MinimizedExperienceMarginTop;

    /// <summary>MINIMIZED_EXP_MARGIN_LEFT.</summary>
    public const double MinimizedExpMarginLeft = Ps5HomeMetrics.MinimizedExperienceMarginLeft;

    /// <summary>MINIMIZED_EXP_SIZE.</summary>
    public const double MinimizedExpSize = Ps5HomeMetrics.MinimizedExperienceSize;

    /// <summary>MINIMIZED_EXP_SCALE, <c>80 / 168</c>.</summary>
    public const double MinimizedExpScale = Ps5HomeMetrics.MinimizedExperienceScale;

    /// <summary>VERTICAL_HEIGHT_CHANGE: how far the hub rides up under the row.</summary>
    public const double VerticalHeightChange = Ps5HomeMetrics.VerticalHeightChange;

    /// <summary>BORDER_RADIUS, the radius of a resting 106 px tile.</summary>
    public const double BorderRadius = Ps5HomeMetrics.BorderRadius;

    /// <summary>
    /// The option menu's anchor offset, HOME m558. The console does not hang
    /// the menu off the tile: it hangs it off a shim that covers the tile
    /// exactly (<c>position: absolute; top/left/right/bottom: 0</c>) and then
    /// carries <c>transform: [{translateX: -3}, {translateY: 3}]</c>.
    /// </summary>
    public const double OptionsShimOffsetX = -3.0;

    /// <summary><c>translateY</c> of the same shim.</summary>
    public const double OptionsShimOffsetY = 3.0;

    /// <summary>itemMargin, HOME m201:14577. The gap between two resting tiles.</summary>
    public const double DefaultItemMargin = 8.0;

    /// <summary>focusedMargin, HOME m201:14577. The gap each side of the focused tile.</summary>
    public const double DefaultFocusedMargin = 16.0;

    /// <summary>Radius as a fraction of the side, 16/106. The source writes the
    /// focused radius as <c>168 / 106 * 16</c>, so this ratio is the real
    /// constant and the two pixel values are both derived from it.</summary>
    public const double CornerRadiusRatio = BorderRadius / ExperienceSize;

    /// <summary>MAX_TILES, HOME m47:4080. Eleven, and the source enforces it
    /// twice: the constant, and an <c>items.slice(0, 11)</c> on the way in.
    /// Anything past that lives in the library, not on the home row.</summary>
    public const int MaxTiles = Ps5HomeMetrics.MaxTiles;

    /// <summary>The side the missing-art fallback icon is fetched at,
    /// <c>{ width: SCALED_EXP_SIZE, height: SCALED_EXP_SIZE }</c>, HOME m47.</summary>
    public const double FallbackIconResize = ScaledExperienceSize;

    /// <summary>FALLBACK_ICON large, HOME m721:51217. The mark inside the
    /// fallback plate, not the plate itself.</summary>
    public const double FallbackIconLarge = 92.0;

    /// <summary>
    /// The switcher's own <c>StyleSheet.create</c> block, HOME m25:3225. Same
    /// keys, same values. Rules that are pure flexbox carry no number and are
    /// quoted in the doc comment rather than invented as a constant.
    /// </summary>
    public static class SwitcherStyles
    {
        /// <summary><c>container: { flexDirection:"row", width: 1920, height: 168 }</c>.</summary>
        public const double ContainerWidth = 1920.0;

        /// <summary><c>container.height</c>, the band the switcher owns.</summary>
        public const double ContainerHeight = ScaledExperienceSize;

        /// <summary><c>focusContainer: { borderRadius: 168 / 106 * 16 }</c>, 25.358490566.</summary>
        public const double FocusContainerBorderRadius =
            ScaledExperienceSize / ExperienceSize * BorderRadius;

        /// <summary><c>optionsMenuStyle: { height: 106, width: 106 }</c>: the
        /// options menu anchors to the resting box, not the focused one.</summary>
        public const double OptionsMenuSize = ExperienceSize;

        /// <summary><c>strandStyle: { marginLeft: 172 }</c>.</summary>
        public const double StrandMarginLeft = ScaledExpMarginLeft;

        /// <summary><c>strandContainer: { width: 1500, height: 168 }</c>.</summary>
        public const double StrandContainerWidth = 1500.0;

        /// <summary><c>strandContainer.height</c>.</summary>
        public const double StrandContainerHeight = ScaledExperienceSize;

        /// <summary><c>downloadbarContainer: { marginTop: 2, alignItems:"center" }</c>.</summary>
        public const double DownloadbarContainerMarginTop = 2.0;

        /// <summary><c>downloadbar: { width: 90 }</c>.</summary>
        public const double DownloadbarWidth = 90.0;
    }

    /// <summary>
    /// The tile's own stylesheet, HOME m210. What is not in it matters as much
    /// as what is: no background, no border, no shadow, and no opacity rule for
    /// a resting tile. The tile is an image in a rounded box and nothing else.
    /// The <c>primaryTitleId</c> text it can draw is gated behind the devkit
    /// setting <c>isHomeTitleIdDisplayEnabled</c> (HOME m209), so a retail
    /// console never shows one.
    /// </summary>
    public static class TileStyles
    {
        /// <summary><c>imageWrapper: { borderRadius: BORDER_RADIUS }</c>.</summary>
        public const double ImageWrapperBorderRadius = BorderRadius;

        /// <summary><c>image: { width: EXPERIENCE_SIZE, height: EXPERIENCE_SIZE }</c>.</summary>
        public const double ImageSize = ExperienceSize;
    }

    // ---- HOME m214, the focused title -------------------------------------
    // The title is not a caption under the row. It sits beside the focused tile,
    // absolutely positioned inside the 1920 x 168 container, in a strip exactly
    // as tall as the difference between the two tile sizes.

    /// <summary>TITLE_MARGIN_TOP.</summary>
    public const double TitleMarginTop = Ps5HomeMetrics.TitleMarginTop;

    /// <summary>TITLE_MARGIN_LEFT.</summary>
    public const double TitleMarginLeft = Ps5HomeMetrics.TitleMarginLeft;

    /// <summary>TITLE_X, <c>SCALED_EXP_MARGIN_LEFT + SCALED_EXP_SIZE + 16</c> = 356.</summary>
    public const double TitleX = Ps5HomeMetrics.TitleX;

    /// <summary>TITLE_Y, which is EXPERIENCE_SIZE: the strip is bottom aligned
    /// with the focused tile.</summary>
    public const double TitleY = Ps5HomeMetrics.TitleY;

    /// <summary><c>itemContainer.height</c>, <c>SCALED_EXP_SIZE - EXPERIENCE_SIZE</c> = 62.</summary>
    public const double TitleStripHeight = ScaledExperienceSize - ExperienceSize;

    /// <summary>The title's own max width before any tag or metadata icon
    /// claims part of it (HOME m565, the 1132 base of its width function).</summary>
    public const double TitleMaxWidth = 1132.0;

    // ---- Content strand geometry (a different tier, different numbers) ----
    // The block above is the experience switcher's, and the installed games are
    // its tiles. The numbers below belong to a hub or media strand, a 1576 wide
    // band inside the 172 margins packing one of six blessed tile widths. They
    // are kept because that surface is real and will want them; they are not the
    // games list, and putting the games on them is what made the home read as a
    // desktop launcher.

    /// <summary>CONTAINER_MARGIN, the inset both home tiers pin to.</summary>
    public const double ContainerMargin = Ps5HomeMetrics.ContainerMargin;

    /// <summary>STRAND_WIDTH, the canvas less both margins.</summary>
    public const double StrandWidth = Ps5HomeMetrics.StrandWidth;

    /// <summary>STRAND_HEIGHT, the strand container's own height.</summary>
    public const double StrandHeight = Ps5HomeMetrics.StrandHeight;

    /// <summary>Per-tile delay of the boot reveal's stagger, in ms.</summary>
    private const double BootStaggerMs = 60.0;

    /// <summary>Delay before the focused title joins the boot reveal: the
    /// bundle waits 1050 ms and then a further 333 ms.</summary>
    private const double BootCaptionDelayMs = 1050.0 + 333.0;

    /// <summary>ANIMATION.TIMING.DEFAULT. Opacity is the one thing on this row
    /// that really is a 300 ms tween.</summary>
    private static readonly TimeSpan CaptionFade = TimeSpan.FromMilliseconds(300);

    /// <summary>One frame at 60 Hz, in ticks so it does not drift.</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(166_667);

    // ---- Defaults ---------------------------------------------------------
    private const double DefaultTileWidth = ScaledExperienceSize;
    private const double DefaultTileHeight = ScaledExperienceSize;
    private const double DefaultRestScale = ExperienceSize / ScaledExperienceSize;
    private const double DefaultTileCornerRadius = SwitcherStyles.FocusContainerBorderRadius;

    // ---- HOME m19:2858-2863, the tile surface palette ---------------------
    // Neutral greys and a 5% white. There is no violet, no accent and no border
    // colour anywhere in the switcher's palette, so the tile draws none.

    /// <summary>COLOR.WHITE.</summary>
    private static readonly IBrush ColorWhite = new SolidColorBrush(Color.FromRgb(255, 255, 255));

    /// <summary>COLOR.BLANK, <c>rgba(255, 255, 255, 0.05)</c>. The one surface
    /// the shell puts behind a tile that has no art of its own.</summary>
    private static readonly IBrush ColorBlank = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255));

    /// <summary>OPACITY.ACTION_INDICATOR MIN, HOME m719:51159. What a glanced
    /// surface drops to against a focused one.</summary>
    private const double GlancedOpacity = 0.7;

    /// <summary>OPACITY.ACTION_INDICATOR MAX.</summary>
    private const double FocusedOpacity = 1.0;

    // ---- Styled properties ------------------------------------------------
    public static readonly StyledProperty<IEnumerable<ShellTile>?> ItemsProperty =
        AvaloniaProperty.Register<ShellTileRow, IEnumerable<ShellTile>?>(nameof(Items));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<ShellTileRow, int>(nameof(SelectedIndex), defaultValue: -1);

    // ---- Tile geometry ----------------------------------------------------

    public static readonly StyledProperty<double> TileWidthProperty =
        AvaloniaProperty.Register<ShellTileRow, double>(nameof(TileWidth), DefaultTileWidth);

    public static readonly StyledProperty<double> TileHeightProperty =
        AvaloniaProperty.Register<ShellTileRow, double>(nameof(TileHeight), DefaultTileHeight);

    public static readonly StyledProperty<double> TileGapProperty =
        AvaloniaProperty.Register<ShellTileRow, double>(nameof(TileGap), DefaultItemMargin);

    public static readonly StyledProperty<double> FocusedMarginProperty =
        AvaloniaProperty.Register<ShellTileRow, double>(nameof(FocusedMargin), DefaultFocusedMargin);

    public static readonly StyledProperty<double> TileCornerRadiusProperty =
        AvaloniaProperty.Register<ShellTileRow, double>(nameof(TileCornerRadius), DefaultTileCornerRadius);

    public static readonly StyledProperty<double> RestScaleProperty =
        AvaloniaProperty.Register<ShellTileRow, double>(nameof(RestScale), DefaultRestScale);

    public static readonly StyledProperty<bool> IsRegionFocusedProperty =
        AvaloniaProperty.Register<ShellTileRow, bool>(nameof(IsRegionFocused), defaultValue: true);

    /// <summary>
    /// X the focused tile's left edge is pinned to while the strand slides
    /// under it. The shell uses its content inset (SCALED_EXP_MARGIN_LEFT 172 at
    /// 1920), so a scaled host passes its own scaled inset here.
    /// </summary>
    public static readonly StyledProperty<double> FocusAnchorXProperty =
        AvaloniaProperty.Register<ShellTileRow, double>(nameof(FocusAnchorX), defaultValue: 0);

    /// <summary>When set the row runs no timer of its own and the host drives it
    /// through <see cref="Advance"/>. Used by headless captures and tests.</summary>
    public static readonly StyledProperty<bool> ManualClockProperty =
        AvaloniaProperty.Register<ShellTileRow, bool>(nameof(ManualClock), false);

    // ---- Backing state ----------------------------------------------------
    private readonly List<ShellTile> _items = new();
    private readonly List<TileVisual> _tiles = new();
    private readonly Stopwatch _stopwatch = new();
    private DispatcherTimer? _timer;
    private Canvas? _surface;
    private Border? _titleContainer;
    private DoubleTransition? _captionFade;
    private ShellMarqueeText? _titleText;
    private bool _pendingReveal;
    private bool _focusPushQueued;
    private double _revealClock;
    private double _viewWidth;
    private double _viewHeight;
    private int _overflowCount;

    public ShellTileRow()
    {
        Focusable = true;
        // The shell's travelling focus plane is the only focus visual. Leaving
        // Fluent's default adorner enabled draws the row's entire bounds too.
        FocusAdorner = null;

        // The strand runs off the side of its own band: tiles left of the
        // focused one slide out through the content inset and are cut off by the
        // screen edge, not by the row. Clipping here would delete them a whole
        // tile early.
        ClipToBounds = false;
        Template = BuildTemplate();
        GotFocus += (_, _) => SchedulePushFocusRect();
    }

    /// <summary>Raised whenever the focused tile changes (keyboard, wheel,
    /// pointer or programmatic).</summary>
    public event EventHandler<ShellTileEventArgs>? SelectionChanged;

    /// <summary>Raised on Enter or double-click over the focused tile.</summary>
    public event EventHandler<ShellTileEventArgs>? ItemActivated;

    /// <summary>
    /// Raised when the row is asked for the tiles it does not carry.
    ///
    /// MAX_TILES is eleven and the console has a whole app behind that cap (the
    /// Game Library, NPXS40071); a rebuild that only slices to eleven strands
    /// everything past the eleventh. The row does not own a library surface and
    /// must not grow one, so it publishes the request and the host answers it.
    /// The trigger is the console's own one: pressing down off the switcher,
    /// which is what <c>msgid_sr_more_content_down_btn</c> (EXACT, HOME m540)
    /// announces while a tile is focused.
    ///
    /// The event carries the tile that was focused when the request was made, so
    /// a library surface can open on it. <see cref="OverflowCount"/> says how
    /// many the cap is holding back, which is what a host uses to decide whether
    /// the affordance is worth showing at all.
    /// </summary>
    public event EventHandler<ShellTileEventArgs>? ShowAllRequested;

    /// <summary>The tiles to display. Assigning re-populates the row; at most
    /// <see cref="MaxTiles"/> are kept.</summary>
    public IEnumerable<ShellTile>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>Index of the focused tile, or -1 when empty. Setting it clamps
    /// into range and raises <see cref="SelectionChanged"/> when it moves.</summary>
    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetSelectedIndex(value);
    }

    /// <summary>The focused tile, or null when the row is empty.</summary>
    public ShellTile? SelectedItem =>
        SelectedIndex >= 0 && SelectedIndex < _items.Count ? _items[SelectedIndex] : null;

    /// <summary>Number of tiles currently held, never more than
    /// <see cref="MaxTiles"/>.</summary>
    public int Count => _items.Count;

    /// <summary>How many items the MAX_TILES cap left off the row. Zero when
    /// everything the host handed over fits.</summary>
    public int OverflowCount => _overflowCount;

    /// <summary>
    /// How this row's selection currently reads. FOCUSED while the row owns the
    /// page's focus, GLANCED while it only points at something, per HOME m720.
    /// </summary>
    public ShellInteractionState InteractionState =>
        IsRegionFocused ? ShellInteractionState.Focused : ShellInteractionState.Glanced;

    /// <summary>Width of a tile at full (focused) size.</summary>
    public double TileWidth
    {
        get => GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    /// <summary>Height of a tile at full (focused) size.</summary>
    public double TileHeight
    {
        get => GetValue(TileHeightProperty);
        set => SetValue(TileHeightProperty, value);
    }

    /// <summary>Gap between two resting tiles (itemMargin). With the default
    /// geometry this is 8, which puts resting tiles on a 114 px pitch.</summary>
    public double TileGap
    {
        get => GetValue(TileGapProperty);
        set => SetValue(TileGapProperty, value);
    }

    /// <summary>Gap each side of the focused tile (focusedMargin), 16 at design
    /// size. This is a second, larger margin, not the same one as
    /// <see cref="TileGap"/>.</summary>
    public double FocusedMargin
    {
        get => GetValue(FocusedMarginProperty);
        set => SetValue(FocusedMarginProperty, value);
    }

    /// <summary>Corner radius of a tile at full size. The radius is a fixed
    /// fraction of the side, so this and the resting radius are the same
    /// number seen at two sizes.</summary>
    public double TileCornerRadius
    {
        get => GetValue(TileCornerRadiusProperty);
        set => SetValue(TileCornerRadiusProperty, value);
    }

    /// <summary>Scale of an unfocused tile relative to the focused one, 106/168
    /// by default.</summary>
    public double RestScale
    {
        get => GetValue(RestScaleProperty);
        set => SetValue(RestScaleProperty, value);
    }

    /// <summary>
    /// Whether this row currently owns the page's focus. Only the owning region
    /// claims the scene's single focus ring, which is what lets the one ring
    /// travel between the function row and the strand instead of two rings
    /// cross-fading.
    /// </summary>
    public bool IsRegionFocused
    {
        get => GetValue(IsRegionFocusedProperty);
        set => SetValue(IsRegionFocusedProperty, value);
    }

    /// <summary>X the focused tile's left edge pins to as the strand slides.</summary>
    public double FocusAnchorX
    {
        get => GetValue(FocusAnchorXProperty);
        set => SetValue(FocusAnchorXProperty, value);
    }

    /// <summary>When set, no internal timer runs and the host drives the row
    /// through <see cref="Advance"/>.</summary>
    public bool ManualClock
    {
        get => GetValue(ManualClockProperty);
        set => SetValue(ManualClockProperty, value);
    }

    /// <summary>The strand's settled geometry for the current property values.</summary>
    internal ShellStrandGeometry Geometry => new(
        TileWidth,
        RestScale,
        TileGap,
        FocusedMargin,
        TileCornerRadius,
        FocusAnchorX);

    /// <summary>True while any tile's springs still need frames.</summary>
    internal bool HasPendingMotion
    {
        get
        {
            if (_pendingReveal)
            {
                return true;
            }

            foreach (var tile in _tiles)
            {
                if (!tile.Scale.IsSettled || !tile.Slide.IsSettled)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The rect the focus highlight is drawn on, in this row's own coordinates,
    /// or null when there is nothing focused. This is the shell's
    /// <c>focusImageRectangle</c>: it is derived from the focused tile's settled
    /// geometry rather than read back off the animating visual, so the ring can
    /// be retargeted once instead of chased every frame.
    /// </summary>
    internal Rect? FocusHighlightRect
    {
        get
        {
            if (SelectedIndex < 0 || SelectedIndex >= _items.Count)
            {
                return null;
            }

            double tileWidth = TileWidth;
            double tileHeight = TileHeight;
            if (!(tileWidth > 0) || !(tileHeight > 0))
            {
                return null;
            }

            // The focused tile's left edge is the anchor by construction; no
            // scroll offset and no lift enter into it.
            return new Rect(Geometry.TileLeft(SelectedIndex, SelectedIndex), BandTop, tileWidth, tileHeight);
        }
    }

    /// <summary>
    /// Top of the tile band inside the row. The switcher's container is exactly
    /// as tall as its focused tile (<c>container.height = SCALED_EXP_SIZE</c>,
    /// HOME m25), so at design size this is zero and the focused tile fills the
    /// band; a host that gives the row more room centres the band in it.
    /// </summary>
    private double BandTop
    {
        get
        {
            double tileHeight = TileHeight;
            if (!(_viewHeight > 0))
            {
                return 0;
            }

            return Math.Max(0.0, (_viewHeight - tileHeight) / 2.0);
        }
    }

    // ---- Navigation logic (surface-independent, unit-testable) ------------

    /// <summary>Move focus by <paramref name="delta"/> tiles, clamped at the
    /// ends (no wrap).</summary>
    internal void MoveFocus(int delta)
    {
        if (_items.Count == 0)
        {
            return;
        }

        SetSelectedIndex(Math.Clamp(SelectedIndex + delta, 0, _items.Count - 1));
    }

    /// <summary>Focus the tile at <paramref name="index"/>, clamped into range
    /// (or -1 when empty). Fires <see cref="SelectionChanged"/> on change.</summary>
    internal void SetSelectedIndex(int index)
    {
        int target = _items.Count == 0 ? -1 : Math.Clamp(index, 0, _items.Count - 1);
        if (target == SelectedIndex)
        {
            // Keep the styled property authoritative even on a no-op clamp.
            SetValue(SelectedIndexProperty, target);
            return;
        }

        SetValue(SelectedIndexProperty, target);
        UpdateVisuals();

        ShellUiSounds.Play(UiSoundEvent.FocusMove);
        SelectionChanged?.Invoke(this, new ShellTileEventArgs(target, SelectedItem));
    }

    /// <summary>Raise <see cref="ItemActivated"/> for the focused tile.</summary>
    internal void ActivateSelected()
    {
        if (SelectedItem is { } tile)
        {
            ShellUiSounds.Play(UiSoundEvent.Enter);
            ItemActivated?.Invoke(this, new ShellTileEventArgs(SelectedIndex, tile));
        }
    }

    /// <summary>Advances every spring by <paramref name="delta"/> and repaints
    /// the tiles. The internal timer calls this; a manual host calls it
    /// directly.</summary>
    internal void Advance(TimeSpan delta)
    {
        double seconds = delta.TotalSeconds;
        if (!(seconds > 0.0) || double.IsNaN(seconds))
        {
            return;
        }

        bool busy = false;

        if (_pendingReveal)
        {
            _revealClock += seconds * 1000.0;
            busy = true;
            ReleaseRevealedTiles();
        }

        foreach (var tile in _tiles)
        {
            busy |= tile.Scale.Advance(seconds);
            busy |= tile.Slide.Advance(seconds);
            tile.Apply();
        }

        if (_pendingReveal && _revealClock >= (_tiles.Count * BootStaggerMs))
        {
            _pendingReveal = false;
        }

        if (!busy)
        {
            StopTimer();
        }
    }

    // ---- Items plumbing ---------------------------------------------------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            RebuildItems();
        }
        else if (change.Property == TileWidthProperty ||
                 change.Property == TileHeightProperty ||
                 change.Property == TileCornerRadiusProperty ||
                 change.Property == TileGapProperty ||
                 change.Property == FocusedMarginProperty ||
                 change.Property == FocusAnchorXProperty ||
                 change.Property == RestScaleProperty)
        {
            // Resizing must not replay the reveal stagger, so the existing
            // tiles are resized in place rather than rebuilt.
            ApplyTileGeometry();
            UpdateVisuals();
        }
        else if (change.Property == IsRegionFocusedProperty)
        {
            // GLANCED and FOCUSED are two different states, so the title strip
            // has to be told which one the row is in.
            UpdateCaption();
            SchedulePushFocusRect();
        }
        else if (change.Property == ManualClockProperty && ManualClock)
        {
            StopTimer();
        }
    }

    /// <summary>
    /// Re-publishes the focus rect. The ring lives on the window's overlay, so
    /// the rect it was handed is only valid for one mapping from this row to the
    /// window; a host that scales the whole surface changes that mapping without
    /// ever re-arranging this row, and has to say so.
    /// </summary>
    public void RefreshFocusRect() => SchedulePushFocusRect();

    // ---- Focus ring ------------------------------------------------------

    /// <summary>
    /// Queues a retarget of the scene's focus ring for after the next layout
    /// pass; the highlight rect is only meaningful once the row is arranged.
    /// </summary>
    private void SchedulePushFocusRect()
    {
        if (_focusPushQueued)
        {
            return;
        }

        _focusPushQueued = true;
        try
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    _focusPushQueued = false;
                    PushFocusRect();
                },
                DispatcherPriority.Render);
        }
        catch
        {
            // No dispatcher (a pure logic host): the ring is simply not driven.
            _focusPushQueued = false;
        }
    }

    /// <summary>
    /// Retargets the scene's single focus ring onto the focused tile. The row
    /// publishes a rect and nothing more; it never draws the highlight itself.
    /// </summary>
    private void PushFocusRect()
    {
        try
        {
            if (ShellFocusRing.For(this) is not { } ring)
            {
                return;
            }

            if (!IsRegionFocused || !IsEffectivelyVisible)
            {
                ring.Release(this);
                return;
            }

            if (FocusHighlightRect is not { } local)
            {
                ring.Release(this);
                return;
            }

            if (this.TransformToVisual(ring) is not { } transform)
            {
                return;
            }

            // The ring frames the focused tile, so it takes the focused radius,
            // which is the ratio applied to the focused side.
            // Ensure the shared plane has resolved its RCO texture before this
            // row's first claim; no tile bounds or timing are changed here.
            Ps5FocusNoiseTexture.Preload();
            ring.Radius = TileCornerRadius;
            ring.LineScale = 1.0;
            ring.Claim(this, local.TransformToAABB(transform));
        }
        catch
        {
            // A detached or half-built tree just leaves the ring where it is.
        }
    }

    /// <summary>Drives the ring's 0.3 s press state.</summary>
    private void SetRingPressed(bool pressed)
    {
        try
        {
            if (IsRegionFocused && ShellFocusRing.For(this) is { } ring && ReferenceEquals(ring.Owner, this))
            {
                ring.SetPressed(pressed);
            }
        }
        catch
        {
            // Decoration only.
        }
    }

    /// <summary>Pushes the current tile geometry onto the live tile visuals.</summary>
    private void ApplyTileGeometry()
    {
        double width = TileWidth;
        double height = TileHeight;

        // The box is the focused size and the render transform scales it down,
        // so a single radius on the box holds the ratio at every drawn size.
        var radius = new CornerRadius(TileCornerRadius);
        foreach (var tile in _tiles)
        {
            tile.Root.Width = width;
            tile.Root.Height = height;
            tile.Cover.Width = width;
            tile.Cover.Height = height;
            tile.Cover.CornerRadius = radius;
            tile.Mat.CornerRadius = radius;
        }
    }

    private void RebuildItems()
    {
        _items.Clear();
        _overflowCount = 0;
        if (Items is { } source)
        {
            // MAX_TILES: the strand is capped, and the source enforces it twice.
            int seen = 0;
            foreach (var tile in source)
            {
                if (tile is null)
                {
                    continue;
                }

                seen++;
                if (_items.Count < MaxTiles)
                {
                    _items.Add(tile);
                }
            }

            _overflowCount = Math.Max(0, seen - _items.Count);
        }

        int clamped = _items.Count == 0 ? -1 : Math.Clamp(SelectedIndex, 0, _items.Count - 1);
        SetValue(SelectedIndexProperty, clamped);

        BuildTiles();
        UpdateVisuals();
        SelectionChanged?.Invoke(this, new ShellTileEventArgs(clamped, SelectedItem));
    }

    // ---- Template ---------------------------------------------------------

    private FuncControlTemplate BuildTemplate()
    {
        return new FuncControlTemplate((_, ns) =>
        {
            // The strand no longer scrolls as a block: each tile carries its own
            // translateX spring, exactly as the source does, because the tiles
            // move relative to one another as well as to the viewport.
            var surface = new Canvas
            {
                Name = "PART_Surface",
            };
            surface.RegisterInNameScope(ns);

            // TitleContainer, HOME m565 / m214. One line: experienceName,
            // ellipsized rather than wrapped, in a strip exactly
            // SCALED_EXP_SIZE - EXPERIENCE_SIZE tall that is bottom aligned with
            // the focused tile. FontSizePS.SizeNormal is recovered from the
            // native UI3 UIFont constants; use the shared token rather than a
            // screenshot-tuned approximation.
            // The strip carries the focused title, and the console marks that
            // one `ellipsizeMode: "marquee"` while every other title in the row
            // is `"tail"`. Since we draw one strip rather than one per
            // experience, this label is always the focused one and so is always
            // the marquee.
            var titleText = new ShellMarqueeText
            {
                Name = "PART_Title",
                FontSize = ShellFontSize.Normal,
                FontWeight = FontWeight.Light,
                Foreground = ColorWhite,
                VerticalAlignment = VerticalAlignment.Center,
                IsMarquee = true,
                Width = TitleMaxWidth,
            };
            if (Ps5FontLibrary.TryGet(Ps5FontFace.Light) is { } shellFont)
            {
                titleText.FontFamily = shellFont;
            }
            titleText.RegisterInNameScope(ns);

            _captionFade = new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = CaptionFade,
                Easing = ShellMotion.EaseOutBreeze,
            };

            // itemContainer: absolutely positioned, 62 tall, row, centred.
            var titleContainer = new Border
            {
                Name = "PART_TitleContainer",
                Height = TitleStripHeight,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Opacity = 0,
                Transitions = new Transitions { _captionFade },
                Child = titleText,
            };
            titleContainer.RegisterInNameScope(ns);

            return new Panel
            {
                Children = { surface, titleContainer },
            };
        });
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _surface = e.NameScope.Find<Canvas>("PART_Surface");
        _titleContainer = e.NameScope.Find<Border>("PART_TitleContainer");
        _titleText = e.NameScope.Find<ShellMarqueeText>("PART_Title");
        BuildTiles();
        UpdateVisuals();
    }

    // ---- Visual build -----------------------------------------------------

    private void BuildTiles()
    {
        if (_surface is null)
        {
            return;
        }

        _surface.Children.Clear();
        _tiles.Clear();

        for (int i = 0; i < _items.Count; i++)
        {
            var tile = CreateTile(_items[i], i);
            _tiles.Add(tile);
            _surface.Children.Add(tile.Root);
        }

        _pendingReveal = _items.Count > 0;
        _revealClock = 0.0;
    }

    /// <summary>
    /// One tile, HOME m551. The whole component is:
    ///
    /// <code>
    /// &lt;View style={[styles.imageWrapper, styles.image]}&gt;
    ///   &lt;Image fallbackSource={fallbackIconPath} source={iconPath} style={styles.image}&gt;
    ///     &lt;Animated.View style={[matStyle, styles.image]} /&gt;
    ///   &lt;/Image&gt;
    /// &lt;/View&gt;
    /// </code>
    ///
    /// A rounded box, an image with a fallback source, and the overflow mat over
    /// it. No plate, no border, no shadow, no label. Everything this row used to
    /// draw around the art was ours, not the console's.
    /// </summary>
    private TileVisual CreateTile(ShellTile model, int index)
    {
        // fallbackSource: the source hands the Image a second source rather than
        // drawing a placeholder, so a tile with no art is still a tile with an
        // icon in it, never initials and never a coloured plate.
        Control content = model.Icon is { } icon
            ? new Image { Source = icon, Stretch = Stretch.UniformToFill }
            : BuildFallbackIcon(Math.Min(TileWidth, TileHeight));

        // matStyle sits over the art at the image's own size, so the mat follows
        // the tile's rounding rather than squaring its corners off.
        var mat = new Border
        {
            CornerRadius = new CornerRadius(TileCornerRadius),
            IsHitTestVisible = false,
        };

        // imageWrapper + image: the rounded box the art is clipped into.
        var cover = new Border
        {
            Width = TileWidth,
            Height = TileHeight,
            CornerRadius = new CornerRadius(TileCornerRadius),
            ClipToBounds = true,
            Child = new Panel { Children = { content, mat } },
        };

        // One matrix per tile, mutated in place. Composing the transform from a
        // parsed string would allocate on every frame of every tile, and the
        // strand runs eleven springs at once.
        var motion = new MatrixTransform();

        // The root carries the scale and slide so the art and its mat move
        // together.
        //
        // Horizontally the origin is the centre, because the source scales the
        // box about its centre and compensates with `offsets` in calculate.
        // Vertically it is the TOP, because the source's tiles are top aligned:
        // getItemLayout returns width and height only, so each item is
        // `position: absolute` with no top, and the strand's own -53 container
        // margin against its +53 item margin lands the content at y 0 at every
        // scale. A resting tile therefore occupies the top 106 of the 168 band
        // and the focused one fills it, which is what leaves room for the title
        // strip at top 106 instead of running it through the row.
        var root = new Border
        {
            Width = TileWidth,
            Height = TileHeight,
            Child = cover,
            RenderTransformOrigin = new RelativePoint(0.5, 0.0, RelativeUnit.Relative),
            RenderTransform = motion,
        };

        int captured = index;
        root.PointerEntered += (_, _) => SetSelectedIndex(captured);
        root.PointerPressed += (_, args) =>
        {
            Focus();
            SetSelectedIndex(captured);
            SetRingPressed(true);
            if (args.ClickCount >= 2)
            {
                ActivateSelected();
            }
        };
        root.PointerReleased += (_, _) => SetRingPressed(false);
        root.PointerExited += (_, _) => SetRingPressed(false);

        // Rest thresholds are in the spring's own units: fractions of a tile for
        // the scale, pixels for the slide.
        var visual = new TileVisual(
            root,
            cover,
            mat,
            motion,
            new ShellSpring(0.0004, 0.004),
            new ShellSpring(0.05, 0.5));

        // The boot reveal grows every tile out of nothing, so a fresh tile starts
        // at zero scale and is released on its stagger beat.
        visual.Scale.SnapTo(0.0);
        visual.Apply();
        return visual;
    }

    /// <summary>
    /// The missing-art fallback, <c>cxml://CommonAssets/iconid_texture_app_fallback</c>
    /// resized to 168 (EXACT, HOME m47). That texture lives in the CommonAssets
    /// container, whose entry names our RCO reader cannot index, so this is a
    /// stand-in built only out of tokens the bundles do give up: COLOR.BLANK for
    /// the plate and the dump's own game pictogram at FALLBACK_ICON large. It is
    /// deliberately neutral. Nothing here is cyan, nothing is a gradient, and it
    /// never spells the title out in initials.
    /// </summary>
    private static Control BuildFallbackIcon(double side)
    {
        var panel = new Panel();
        panel.Children.Add(new Border { Background = ColorBlank });

        // The console's own placeholder first. `fallbackSource` on the tile's
        // Image is cxml://CommonAssets/iconid_texture_app_fallback, so this is
        // the art a title with no icon actually gets; our pictograms are only
        // a stand-in for a dump that has not been located.
        var real = ShellIcons.TryGet(ShellIcon.AppFallback);
        if (real is not null)
        {
            // The shipped texture is the whole plate, not a mark on one, so it
            // fills the tile rather than sitting at FALLBACK_ICON size in the
            // middle of it.
            panel.Children.Add(new Image
            {
                Source = real,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            });
            return panel;
        }

        var mark = ShellIcons.TryGet(ShellIcon.Controller) ?? ShellIcons.TryGet(ShellIcon.Library);
        if (mark is not null)
        {
            double markSize = side * (FallbackIconLarge / ScaledExperienceSize);
            panel.Children.Add(new Image
            {
                Source = mark,
                Width = markSize,
                Height = markSize,
                Opacity = GlancedOpacity,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return panel;
    }

    // ---- Visual state update ---------------------------------------------

    private void UpdateVisuals()
    {
        UpdateCaption();

        if (_surface is null || _tiles.Count == 0)
        {
            return;
        }

        var geometry = Geometry;
        int selected = SelectedIndex;
        double top = BandTop;

        for (int i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];

            // Every tile sits on the anchor and is pushed off it by its own
            // spring, which is how the source arranges the strand.
            Canvas.SetLeft(tile.Root, geometry.AnchorX);
            Canvas.SetTop(tile.Root, top);
            tile.Root.ZIndex = i == selected ? 1000 : 500 - Math.Abs(i - selected);

            double scale = geometry.ScaleFor(i, selected);
            double slide = geometry.TranslateXFor(i, selected);

            // useMat: the tail fade is set, not sprung, so it lands with the
            // selection rather than trailing it.
            tile.Mat.Background = ShellExperienceMat.For(i, selected);

            if (_pendingReveal)
            {
                // The slide is put in place immediately so tiles grow where they
                // belong; only the scale waits for this tile's beat.
                tile.RevealAtMs = i * BootStaggerMs;
                tile.Slide.SnapTo(slide);
                if (tile.Released)
                {
                    tile.Scale.SpringTo(scale, ShellSpringConfig.Slower);
                }
                else
                {
                    tile.Scale.SnapTo(0.0);
                }
            }
            else
            {
                tile.Scale.SpringTo(scale, ShellSpringConfig.StrandFocus);
                tile.Slide.SpringTo(slide, ShellSpringConfig.StrandFocus);
            }

            tile.Apply();
        }

        Wake();
        SchedulePushFocusRect();
    }

    /// <summary>
    /// Places and fills the TitleContainer, HOME m565. The strip is absolutely
    /// positioned at (TITLE_X, TITLE_Y) inside the switcher container, which is
    /// 16 px past the focused tile's right edge and bottom aligned with it, and
    /// carries the focused experience's plain display name. The independent
    /// branded title logo belongs to the lower Game Hub surface, not this strip.
    /// The source runs one strip per experience with a per-item opacity; ours runs one
    /// strip and swaps the current content, which is the same picture with eleven fewer
    /// text runs.
    /// </summary>
    private void UpdateCaption()
    {
        if (_titleContainer is null)
        {
            return;
        }

        if (SelectedItem is { } sel)
        {
            double titleWidth = ShellTitleMetrics.NameWidth(
                sel.HasEntitlementIcon,
                sel.HasStorageIcon,
                ShellTitleMetrics.ShowPlatformTag(sel.PlatformType),
                ShellTitleMetrics.ShowPackageTag(sel.PackageType))
                * (TileWidth / ScaledExperienceSize);
            if (_titleText is not null)
            {
                _titleText.Text = sel.Title;

                // The name's width is not a constant: the source deducts what
                // this title's own tags and metadata icons need from 1132. With
                // no metadata the answer is the full 1132, which is the same
                // path the console takes for such a title.
                _titleText.Width = titleWidth;
                _titleText.IsVisible = true;
            }

            if (_captionFade is not null)
            {
                // The title joins the boot reveal late: the bundle waits 1050 ms
                // and then a further 333 ms before fading it up.
                _captionFade.Delay = _pendingReveal
                    ? TimeSpan.FromMilliseconds(BootCaptionDelayMs)
                    : TimeSpan.Zero;
            }

            // TITLE_X is measured from the switcher container's left edge, which
            // is where the focused tile's anchor is measured from too, so the
            // strip follows a host that moves the anchor.
            _titleContainer.Margin = new Thickness(
                FocusAnchorX + TileWidth + (TitleMarginLeft * (TileWidth / ScaledExperienceSize)),
                BandTop + (TitleY * (TileHeight / ScaledExperienceSize)),
                0,
                0);
            _titleContainer.Height = TitleStripHeight * (TileHeight / ScaledExperienceSize);

            // GLANCED against FOCUSED: the row still names what it points at
            // while another region owns focus, at the console's own de-emphasis.
            _titleContainer.Opacity = InteractionState == ShellInteractionState.Focused
                ? FocusedOpacity
                : GlancedOpacity;
        }
        else
        {
            _titleContainer.Opacity = 0;
        }
    }

    /// <summary>Lets go of every tile whose stagger beat has passed.</summary>
    private void ReleaseRevealedTiles()
    {
        var geometry = Geometry;
        int selected = SelectedIndex;

        for (int i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            if (tile.Released || _revealClock < tile.RevealAtMs)
            {
                continue;
            }

            tile.Released = true;
            tile.Scale.SpringTo(geometry.ScaleFor(i, selected), ShellSpringConfig.Slower);
        }
    }

    // ---- Frame tick -------------------------------------------------------

    private void Wake()
    {
        if (ManualClock)
        {
            return;
        }

        if (!HasPendingMotion)
        {
            return;
        }

        try
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = FrameInterval,
                };
                _timer.Tick += OnTick;
            }

            if (!_timer.IsEnabled)
            {
                _stopwatch.Restart();
                _timer.Start();
            }
        }
        catch
        {
            // No dispatcher (a headless logic host): there is no frame to run
            // the springs on, so the strand simply arrives.
            SettleNow();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var delta = _stopwatch.Elapsed;
        _stopwatch.Restart();
        Advance(delta);
    }

    private void StopTimer()
    {
        try
        {
            _timer?.Stop();
            _stopwatch.Reset();
        }
        catch
        {
            // Nothing to unwind.
        }
    }

    /// <summary>Drops every spring onto its target, for hosts with no frames.</summary>
    private void SettleNow()
    {
        _pendingReveal = false;
        foreach (var tile in _tiles)
        {
            tile.Released = true;
            tile.Scale.Settle();
            tile.Slide.Settle();
            tile.Apply();
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (Math.Abs(size.Width - _viewWidth) > 0.5 || Math.Abs(size.Height - _viewHeight) > 0.5)
        {
            _viewWidth = size.Width;
            _viewHeight = size.Height;
            UpdateVisuals();
        }
        else
        {
            SchedulePushFocusRect();
        }

        return size;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopTimer();
        base.OnDetachedFromVisualTree(e);
    }

    // ---- Input ------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                MoveFocus(-1);
                e.Handled = true;
                break;
            case Key.Right:
                MoveFocus(1);
                e.Handled = true;
                break;
            case Key.Home:
                SetSelectedIndex(0);
                e.Handled = true;
                break;
            case Key.End:
                SetSelectedIndex(_items.Count - 1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                ActivateSelected();
                e.Handled = true;
                break;
            case Key.Down:
                // msgid_sr_more_content_down_btn: down off a focused tile is the
                // console's own "there is more past this row" affordance. Only
                // claim the key when there actually is more, so a host that owns
                // a region below the switcher still gets it.
                if (RequestShowAll())
                {
                    e.Handled = true;
                }

                break;
        }

        if (!e.Handled)
        {
            base.OnKeyDown(e);
        }
    }

    /// <summary>
    /// Raises <see cref="ShowAllRequested"/> when the MAX_TILES cap is holding
    /// anything back. Returns whether the request was made, so a caller can fall
    /// through to its own navigation when it was not.
    /// </summary>
    public bool RequestShowAll()
    {
        if (_overflowCount <= 0 || ShowAllRequested is null)
        {
            return false;
        }

        ShellUiSounds.Play(UiSoundEvent.Enter);
        ShowAllRequested.Invoke(this, new ShellTileEventArgs(SelectedIndex, SelectedItem));
        return true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // Either axis scrubs focus; the vertical wheel is the common case.
        double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (delta != 0)
        {
            MoveFocus(delta > 0 ? -1 : 1);
            e.Handled = true;
        }

        base.OnPointerWheelChanged(e);
    }

    /// <summary>
    /// One tile's visuals plus the two springs that move it. The springs are the
    /// tile's own, in parallel, which is the arrangement the source uses.
    /// </summary>
    private sealed class TileVisual
    {
        public TileVisual(
            Border root,
            Border cover,
            Border mat,
            MatrixTransform motion,
            ShellSpring scale,
            ShellSpring slide)
        {
            Root = root;
            Cover = cover;
            Mat = mat;
            Motion = motion;
            Scale = scale;
            Slide = slide;
        }

        public Border Root { get; }

        public Border Cover { get; }

        /// <summary>The overflow tail fade's own view, <c>mat{i}</c>.</summary>
        public Border Mat { get; }

        public MatrixTransform Motion { get; }

        public ShellSpring Scale { get; }

        public ShellSpring Slide { get; }

        /// <summary>Reveal beat, in ms from the start of the boot stagger.</summary>
        public double RevealAtMs { get; set; }

        /// <summary>True once the reveal has let this tile grow.</summary>
        public bool Released { get; set; }

        /// <summary>Writes the springs onto the visual. One matrix, no
        /// allocation, once per tile per frame.</summary>
        public void Apply()
        {
            double k = Scale.Value;
            Motion.Matrix = new Matrix(k, 0, 0, k, Slide.Value, 0);
        }
    }
}
