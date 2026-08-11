// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Media;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// The home screen's geometry, verbatim from the decrypted 3.00 React Native
/// bundles (<c>NPXS40002.js</c>, "HOME", and <c>NPXS40141.base.js</c>, "BASE").
/// Line numbers are cited per group; the full derivation is in
/// <c>docs/ps5-home-shell-spec.md</c> §2 and <c>docs/ps5-shell-recovery-audit.md</c>.
///
/// <para><b>The rule this file exists to enforce.</b> Every number here came
/// out of a file. Where a number did <em>not</em>, it is not here — it is a
/// <c>null</c> on a nullable member with UNRECOVERED in its doc comment, and the
/// renderer is expected to leave that element visibly unfinished. See
/// <see cref="UnselectedIconGap"/>, which is the one piece of the icon row we
/// still cannot draw honestly.</para>
/// </summary>
public static class Ps5HomeMetrics
{
    // ---- 2.1 Experience switcher: the home icon row (HOME:3215-3278, module 25) ----

    /// <summary><c>EXPERIENCE_SIZE</c>: the unselected icon, px.</summary>
    public const double ExperienceSize = 106;

    /// <summary><c>SCALED_EXP_SIZE</c>: the selected icon, px.</summary>
    public const double ScaledExperienceSize = 168;

    /// <summary><c>EXPERIENCE_SCALE</c> = 168/106 = 1.5849056603773586.</summary>
    public const double ExperienceScale = ScaledExperienceSize / ExperienceSize;

    /// <summary><c>SCALED_EXP_MARGIN_LEFT</c>: left inset of the selected icon.</summary>
    public const double ScaledExperienceMarginLeft = 172;

    /// <summary><c>MINIMIZED_EXP_MARGIN_TOP</c>, with the hub open.</summary>
    public const double MinimizedExperienceMarginTop = 48;

    /// <summary><c>MINIMIZED_EXP_MARGIN_LEFT</c>.</summary>
    public const double MinimizedExperienceMarginLeft = 48;

    /// <summary><c>MINIMIZED_EXP_SIZE</c>.</summary>
    public const double MinimizedExperienceSize = 80;

    /// <summary><c>MINIMIZED_EXP_SCALE</c> = 80/168 = 0.47619047619047616.</summary>
    public const double MinimizedExperienceScale = MinimizedExperienceSize / ScaledExperienceSize;

    /// <summary><c>VERTICAL_HEIGHT_CHANGE</c>.</summary>
    public const double VerticalHeightChange = 40;

    /// <summary><c>BORDER_RADIUS</c>: the icon corner, in unselected/source space.</summary>
    public const double BorderRadius = 16;

    /// <summary>
    /// <c>focusContainer.borderRadius = 168/106 * 16</c> = 25.35849056603774.
    /// The corner scales by the same factor as the icon, so the selected tile's
    /// corner is not 16.
    /// </summary>
    public const double FocusBorderRadius = ExperienceScale * BorderRadius;

    /// <summary><c>container</c>: <c>{ flexDirection: "row", width: 1920, height: 168 }</c>.</summary>
    public static Size RowSize { get; } = new(1920, 168);

    /// <summary><c>optionsMenuStyle</c>: 106 x 106.</summary>
    public static Size OptionsMenuSize { get; } = new(106, 106);

    /// <summary><c>strandStyle.marginLeft</c>.</summary>
    public const double StrandStyleMarginLeft = 172;

    /// <summary><c>strandContainer</c>: 1500 x 168.</summary>
    public static Size StrandContainerSize { get; } = new(1500, 168);

    /// <summary><c>downloadbar.width</c>, with <c>downloadbarContainer.marginTop = 2</c>.</summary>
    public const double DownloadBarWidth = 90;

    /// <summary><c>downloadbarContainer.marginTop</c>.</summary>
    public const double DownloadBarMarginTop = 2;

    /// <summary><c>MAX_TILES</c> (HOME:4080); the query slices to the same bound.</summary>
    public const int MaxTiles = 11;

    /// <summary>Tile media is requested at <c>SCALED_EXP_SIZE</c> square (HOME:4082).</summary>
    public static Size TileMediaRequestSize { get; } = new(ScaledExperienceSize, ScaledExperienceSize);

    /// <summary>
    /// UNRECOVERED. The horizontal gap between unselected icons is in neither
    /// module 25 nor any home-ui StyleSheet; the row is a native list and the
    /// spacing lives in <c>ReactNative.PUI.dll</c>'s list defaults, which have
    /// not been read (<c>docs/ps5-shell-recovery-audit.md</c> M7).
    ///
    /// It is null on purpose. A renderer that needs it must draw the row in a
    /// state that is obviously unfinished rather than picking a plausible
    /// number, because a plausible number here is indistinguishable from the
    /// real one and would quietly become "recovered".
    /// </summary>
    public static double? UnselectedIconGap => null;

    // ---- 2.2 Selected-title block (HOME:15399-15444) ----

    /// <summary><c>TITLE_MARGIN_TOP</c>.</summary>
    public const double TitleMarginTop = 10;

    /// <summary><c>TITLE_MARGIN_LEFT</c>.</summary>
    public const double TitleMarginLeft = 16;

    /// <summary><c>TITLE_X = SCALED_EXP_MARGIN_LEFT + SCALED_EXP_SIZE + 16</c> = 356.</summary>
    public const double TitleX = ScaledExperienceMarginLeft + ScaledExperienceSize + 16;

    /// <summary><c>TITLE_Y = EXPERIENCE_SIZE</c> = 106.</summary>
    public const double TitleY = ExperienceSize;

    /// <summary><c>MINIMIZED_TITLE_MARGIN_LEFT</c>.</summary>
    public const double MinimizedTitleMarginLeft = 44;

    /// <summary><c>MINIMIZED_TITLE_MARGIN_TOP</c>.</summary>
    public const double MinimizedTitleMarginTop = 9;

    /// <summary>
    /// Minimized title origin (HOME:40722-40724):
    /// x = 48+80+44 = 172, y = 48+9 = 57.
    /// </summary>
    public static Point MinimizedTitleOrigin { get; } = new(
        MinimizedExperienceMarginLeft + MinimizedExperienceSize + MinimizedTitleMarginLeft,
        MinimizedExperienceMarginTop + MinimizedTitleMarginTop);

    /// <summary><c>itemContainer.height = 168 - 106</c> = 62.</summary>
    public const double TitleItemContainerHeight = ScaledExperienceSize - ExperienceSize;

    /// <summary><c>separatorText</c>: 2 px wide, inset 6 top and bottom, 12 from the left.</summary>
    public static Thickness TitleSeparatorInset { get; } = new(12, 6, 0, 6);

    /// <summary><c>separatorText.width</c>.</summary>
    public const double TitleSeparatorWidth = 2;

    /// <summary><c>tagText.marginLeft</c>.</summary>
    public const double TitleTagMarginLeft = 26;

    public const double TitleMetadataIconMarginLeft = 12;

    /// <summary><c>entitlementIconId</c> / <c>storageIconId</c>: 42 x 42.</summary>
    public static Size TitleMetadataIconSize { get; } = new(42, 42);

    // ---- 2.3 System row (HOME:7287, 10651-10693, 15506-15530) ----

    /// <summary><c>SYSTEM_HEIGHT</c>: the top band owns y 0..126.</summary>
    public const double SystemHeight = 126;

    /// <summary><c>clockWrapper.marginLeft</c>.</summary>
    public const double ClockWrapperMarginLeft = 88;

    /// <summary><c>SYSTEM_ICON_SIZE</c>, the 56 px box each system icon sits in.</summary>
    public const double SystemIconSize = 56;

    /// <summary><c>SYSTEM_ICON_SIZE_NO_GLANCE</c>; also the drawn pictogram size.</summary>
    public const double SystemIconSizeNoGlance = 48;

    /// <summary><c>iconContainer.marginLeft</c>: the gap between system icons.</summary>
    public const double SystemIconMarginLeft = 48;

    /// <summary><c>round.borderRadius</c> = 28, i.e. the 56 box fully rounded.</summary>
    public const double SystemIconCornerRadius = 28;

    /// <summary><c>iconTextContainer</c>: below the icon, 368 wide, 4 px of margin.</summary>
    public static Size SystemIconTextContainerSize { get; } = new(368, double.NaN);

    /// <summary><c>iconTextContainer.marginTop</c>.</summary>
    public const double SystemIconTextMarginTop = 4;

    /// <summary><c>systemIconsCount</c> (HOME:15588): Search, Settings, Profile.</summary>
    public const int SystemIconsCount = 3;

    /// <summary><c>spaceSwitcherWrapper.marginLeft</c>. A layout margin, NOT a safe area.</summary>
    public const double SpaceSwitcherMarginLeft = 84;

    /// <summary><c>systemWrapper.marginRight</c>. A layout margin, NOT a safe area.</summary>
    public const double SystemWrapperMarginRight = 84;

    /// <summary><c>FCFocusLayer</c>: the function-control panel anchor, 126 down and 1188 across.</summary>
    public static Point FunctionControlOrigin { get; } = new(1188, 126);

    /// <summary><c>FCContainer</c>: 652 wide, 216..810 tall, 16 px corner.</summary>
    public static Size FunctionControlSize { get; } = new(652, 216);

    // ---- 2.4 Content strand (HOME:3311-3363, module 28) ----

    /// <summary><c>STRAND_WIDTH</c>.</summary>
    public const double StrandWidth = 1576;

    /// <summary><c>STRAND_HEIGHT</c>.</summary>
    public const double StrandHeight = 864;

    /// <summary><c>CONTAINER_MARGIN</c>. Equal to the selected icon's inset, and not a coincidence.</summary>
    public const double ContainerMargin = 172;

    /// <summary>
    /// The horizontal packing table, keyed <c>[strandWidth][tileWidth]</c>, for
    /// computed: <c>howManyCanFit * (tileWidth + margin)</c> does not always
    /// reproduce them, so they must be looked up rather than derived.
    /// </summary>
    public static IReadOnlyDictionary<int, StrandPacking> HorizontalPacking { get; } =
        new Dictionary<int, StrandPacking>
        {
            [236] = new(6, 32, 268),
            [296] = new(5, 24, 320),
            [360] = new(4, 32, 392),
            [370] = new(4, 32, 402),
            [504] = new(3, 32, 536),
            [772] = new(2, 32, 804),
        };

    /// <summary>Vertical packing: <c>864 / 192</c>.</summary>
    public static StrandPacking VerticalPacking { get; } = new(4, 5, 197);

    // ---- 2.6 Colours (HOME:2859-2863, 41601-41626; BASE:3325-3330) ----

    /// <summary>
    /// The mat colour behind the tile row, <c>#020408</c>. Never black. The same
    /// (2,4,8)/255 the native <c>BGTransition.BasematDefaultColor</c> carries,
    /// which is a satisfying cross-check between the JS and the native side.
    /// </summary>
    public static Color MatColor { get; } = Color.FromRgb(0x02, 0x04, 0x08);

    /// <summary><c>rgba(255,255,255,0.7)</c>: secondary text.</summary>
    public const double SecondaryTextOpacity = 0.7;

    /// <summary><c>rgba(255,255,255,0.25)</c>: the title-block separator.</summary>
    public const double SeparatorOpacity = 0.25;

    /// <summary>
    /// UNSOURCED. The layout spec names 0.3 as the disabled-text opacity, but it
    /// is the one colour value in that document with no line citation, and the
    /// audit (defect S5, gap M8) refuses it until one is produced. Null until
    /// then; do not render disabled text at a guessed opacity.
    /// </summary>
    public static double? DisabledTextOpacity => null;

    /// <summary><c>IconPS.ps.js</c> emphasis colours (BASE:3325-3330).</summary>
    public static Color IconNormal { get; } = Color.FromRgb(0xff, 0xff, 0xff);

    /// <summary><c>positive</c>.</summary>
    public static Color IconPositive { get; } = Color.FromRgb(0x00, 0x78, 0xc8);

    /// <summary><c>negative</c>.</summary>
    public static Color IconNegative { get; } = Color.FromRgb(0xe1, 0x32, 0x32);

    /// <summary><c>inverted</c>.</summary>
    public static Color IconInverted { get; } = Color.FromRgb(0x33, 0x33, 0x33);

    /// <summary>
    /// <c>useMat</c> (HOME:41601-41626). The real darkening over the background
    /// plate: an interpolation on distance-from-selection, so only the last
    /// three of the 11 tiles are dimmed at all.
    ///
    /// <para>This is the whole of the shell's mat. It is emphatically not a
    /// full-frame wash — a previous build laid a screen-wide gradient over the
    /// plate and lost 42 % of its luminance to a number nobody measured.</para>
    /// </summary>
    /// <param name="offsetFromSelection">Tile distance from the selected tile.</param>
    public static double MatOpacityForOffset(int offsetFromSelection) => offsetFromSelection switch
    {
        >= 10 => 0.4,
        9 => 0.2,
        8 => 0.05,
        _ => 0.0,
    };

    /// <summary>The mat brush for a tile at a given distance from the selection.</summary>
    /// <param name="offsetFromSelection">Tile distance from the selected tile.</param>
    public static IBrush MatBrushForOffset(int offsetFromSelection)
    {
        var alpha = (byte)Math.Round(MatOpacityForOffset(offsetFromSelection) * 255.0);
        return new SolidColorBrush(Color.FromArgb(alpha, MatColor.R, MatColor.G, MatColor.B));
    }

    // ---- 2.7 Motion (HOME:4162-4187, 9788-9938, 41396-41410) ----

    /// <summary>
    /// The home to hub travel: <c>SYSTEM_HEIGHT + VERTICAL_HEIGHT_CHANGE</c>
    /// = 166 px upward, on <c>SPRING_OPTIONS_FAST</c>.
    /// </summary>
    public const double HubTravel = SystemHeight + VerticalHeightChange;

    /// <summary><c>SPRING_OPTIONS_SLOW</c>.</summary>
    public static SpringOptions SpringSlow { get; } = new(130, 25, 1, true);

    /// <summary><c>SPRING_OPTIONS_SLOWER</c>.</summary>
    public static SpringOptions SpringSlower { get; } = new(100, 20, 1, true);

    /// <summary><c>SPRING_OPTIONS_FAST</c>. Drives the home/hub move.</summary>
    public static SpringOptions SpringFast { get; } = new(200, 100, 0.2, false);

    /// <summary><c>SPRING_OPTIONS_FASTER</c>.</summary>
    public static SpringOptions SpringFaster { get; } = new(600, 100, 0.2, false);

    /// <summary>
    /// Icon minimize translate (HOME:41396-41410):
    /// x = -(172 + 84 - (48 + 40)) = -168, y = -(126 + 84 - (48 + 40)) = -122.
    /// </summary>
    public static Point MinimizeTranslation { get; } = new(
        -(ScaledExperienceMarginLeft + (ScaledExperienceSize / 2)
          - (MinimizedExperienceMarginLeft + (MinimizedExperienceSize / 2))),
        -(SystemHeight + (ScaledExperienceSize / 2)
          - (MinimizedExperienceMarginTop + (MinimizedExperienceSize / 2))));

    /// <summary>
    /// The tile-content reveal (HOME:36322-36342): opacity 0 to 1 and scale
    /// .95 to 1 in parallel, staggered by exactly one 60 Hz frame.
    /// </summary>
    public static TimeSpan TileRevealDuration { get; } = TimeSpan.FromMilliseconds(300);

    /// <summary><c>stagger: 16.67</c> — one frame at 60 Hz.</summary>
    public static TimeSpan TileRevealStagger { get; } = TimeSpan.FromMilliseconds(16.67);

    /// <summary><c>Easing.bezier(.25, .1, .25, .8)</c> on the tile reveal.</summary>
    public static (double X1, double Y1, double X2, double Y2) TileRevealBezier => (0.25, 0.1, 0.25, 0.8);

    /// <summary>Tile reveal scales up from here.</summary>
    public const double TileRevealFromScale = 0.95;
}

/// <summary>
/// One row of the strand packing table.
/// </summary>
/// <param name="HowManyCanFit">Tiles that fit across the strand.</param>
/// <param name="Margin">Gap between tiles.</param>
/// <param name="TileSizingWithMargin">Tile pitch, i.e. tile plus margin.</param>
public readonly record struct StrandPacking(int HowManyCanFit, double Margin, double TileSizingWithMargin);

/// <summary>
/// A React Native spring config as the home bundle declares it.
/// </summary>
/// <param name="Stiffness">Spring constant.</param>
/// <param name="Damping">Damping coefficient.</param>
/// <param name="Mass">Mass.</param>
/// <param name="OvershootClamping">True when the spring is not allowed past its target.</param>
public readonly record struct SpringOptions(double Stiffness, double Damping, double Mass, bool OvershootClamping);
