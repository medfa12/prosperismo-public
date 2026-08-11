/**
 * Values recovered from the HOME firmware bundle and native PUI controls.
 * This module intentionally contains only settled measurements; see
 * docs/sony-shell/ps5-rn-layout.md for the evidence locators.
 */
export const SHELL_METRICS = {
  canvas: {width: 1920, height: 1080},
  systemBandHeight: 126,
  systemInset: 84,
  systemIconSize: 56,
  systemIconPitch: 104,
  clockMarginLeft: 88,
  strand: {
    left: 172,
    top: 126,
    height: 168,
    itemSize: 106,
    focusedSize: 168,
    itemMargin: 8,
    focusedMargin: 16,
    maxItems: 11,
    radius: 16,
    titleTop: 106,
  },
  contentWidth: 1576,
  gridItemMargin: 20,
  // FocusRenderManager defaults: 3px line + 3px exterior offset. The 8px
  // control-centre constant is a different control family, not the card line.
  focusLineWidth: 3,
  focusLineOffset: 3,
  focusInset: 3,
  // HOME selection requests degree Normal. The native transition owner derives
  // 300ms + degree * 166.66667ms, therefore Normal (degree 2) is 633.333ms.
  titleBackgroundTransitionMs: 633.3333435058594,
  panelRadius: 16,
  colors: {
    darkGrey: '#353535',
    grey: '#292929',
    blank: 'rgba(255,255,255,0.05)',
    white: '#FFFFFF',
    iconInverted: '#292929',
    obscure: 'rgba(13,13,13,0.6)',
    secondaryText: 'rgba(255,255,255,0.7)',
    weakDivider: 'rgba(255,255,255,0.1)',
    modalScrim: 'rgba(0,0,0,0.8)',
    settingsBasemat: '#020408',
  },
  strandSpring: {
    stiffness: 400,
    damping: 50,
    mass: 0.2,
    overshootClamping: true,
  },
} as const;

export const SHELL_FOCUSED_TILE_SCALE =
  SHELL_METRICS.strand.focusedSize / SHELL_METRICS.strand.itemSize;

export const SHELL_FOCUSED_TILE_RADIUS =
  SHELL_METRICS.strand.radius * SHELL_FOCUSED_TILE_SCALE;

export type HomeFocusTarget = {
  kind: 'card' | 'system';
  x: number;
  y: number;
  width: number;
  height: number;
  radius: number;
};

/** Exact recovered EaseOutBlast(r=10, i=0.5) parametric curve. */
export function shellEaseOutBlast(value: number): number {
  return 1 - Math.pow(1 - Math.min(value * 0.5, 1), 10);
}

/**
 * Fixed-canvas focus geometry. The system group's 88px clock separation is
 * represented by its 48px flex gap plus the clock's 40px left margin.
 */
export function shellHomeFocusTarget(region: 'strand' | 'system', systemIndex = 0): HomeFocusTarget {
  if (region === 'strand') {
    const exterior = SHELL_METRICS.focusLineWidth + SHELL_METRICS.focusLineOffset;
    return {
      kind: 'card',
      x: SHELL_METRICS.strand.left - exterior,
      y: SHELL_METRICS.strand.top - exterior,
      width: SHELL_METRICS.strand.focusedSize + exterior * 2,
      height: SHELL_METRICS.strand.focusedSize + exterior * 2,
      radius: SHELL_FOCUSED_TILE_RADIUS + SHELL_METRICS.focusLineOffset,
    };
  }
  const actionCount = 3;
  const actionGap = SHELL_METRICS.systemIconPitch - SHELL_METRICS.systemIconSize;
  const clockWidth = 120;
  const clockExtraMargin = SHELL_METRICS.clockMarginLeft - actionGap;
  const groupWidth = actionCount * SHELL_METRICS.systemIconSize
    + actionCount * actionGap
    + clockExtraMargin
    + clockWidth;
  const firstIconX = SHELL_METRICS.canvas.width - SHELL_METRICS.systemInset - groupWidth;
  return {
    kind: 'system',
    x: firstIconX + Math.max(0, Math.min(systemIndex, actionCount - 1)) * SHELL_METRICS.systemIconPitch,
    y: (SHELL_METRICS.systemBandHeight - SHELL_METRICS.systemIconSize) / 2,
    width: SHELL_METRICS.systemIconSize,
    height: SHELL_METRICS.systemIconSize,
    radius: SHELL_METRICS.systemIconSize / 2,
  };
}

/**
 * The base (unscaled) art position for a strand item. The selected card's
 * base position includes the 31px transform-origin offset, so its on-screen
 * left edge is exactly 172 after the 106→168 scale is applied.
 */
export function shellTileBaseX(index: number, selectedIndex: number): number {
  const {left, itemSize, focusedSize, itemMargin, focusedMargin} = SHELL_METRICS.strand;
  const scaleOffset = (focusedSize - itemSize) / 2;
  if (index === selectedIndex) {
    return left + scaleOffset;
  }
  if (index < selectedIndex) {
    return left - focusedMargin - itemSize - (selectedIndex - index - 1) * (itemSize + itemMargin);
  }
  return left + focusedSize + focusedMargin + (index - selectedIndex - 1) * (itemSize + itemMargin);
}

/** Kept as a testable relative form of the firmware strand calculation. */
export function shellTileTranslateX(index: number, selectedIndex: number): number {
  return shellTileBaseX(index, selectedIndex) - SHELL_METRICS.strand.left;
}
