import {HOME_SPRINGS, ShellSpring} from './shellHomeMotion';

export const SHELL_FONT_SIZE = {
  xxxLarge: 72,
  xxLarge: 54,
  xLarge: 45,
  large: 36,
  normal: 30,
  small: 27,
  xSmall: 24,
  xxSmall: 21,
  xxxSmall: 18,
  xxxxSmall: 15,
} as const;

export const SHELL_FUNCTION_PANEL = {
  anchorX: 1188,
  anchorY: 126,
  width: 652,
  minHeight: 216,
  maxHeight: 810,
  radius: 16,
  headerHeight: 80,
  headerPadding: 24,
  headerOpacity: 0.7,
  iconSize: 48,
  headerIconRadius: 8,
  headerIconMarginRight: 16,
  listItemMinHeight: 98,
  rightIconMarginHorizontal: 16,
  leftIconMarginTop: 21,
  profileRowHeight: 90,
  profileRowMarginBottom: 2,
} as const;

export function shellFunctionPanelHeight(rowCount: number): number {
  return Math.max(
    SHELL_FUNCTION_PANEL.minHeight,
    Math.min(
      SHELL_FUNCTION_PANEL.headerHeight + Math.max(0, rowCount) * SHELL_FUNCTION_PANEL.listItemMinHeight,
      SHELL_FUNCTION_PANEL.maxHeight,
    ),
  );
}

export const SHELL_UTILITY_STRIP = {
  maxWidth: 416,
  marginTop: 8,
  iconSize: 56,
  iconMarginLeft: 48,
  iconPitch: 104,
  labelTop: 56,
  labelWidth: 336,
  labelMarginTop: 16,
  unfocusedOpacity: 0.6,
} as const;

export const SHELL_HUB_NAV = {
  horizontalMarginLeft: 148,
  horizontalMarginRight: 172,
  horizontalWrapperPaddingTop: 40,
  horizontalWrapperMarginLeft: -148,
  horizontalWrapperMarginRight: -172,
  horizontalContentWidth: 1600,
  verticalTrackWidth: 2152,
  verticalMarginTop: 86,
  verticalMarginLeft: 40,
  verticalWrapperMarginLeft: -12,
  sceneContainerMarginTop: -40,
  sceneHeadingGap: 16,
  sceneGap: 48,
  sceneTileGap: 24,
} as const;

export const SHELL_SEARCH = {
  columns: 4,
  tileWidth: 370,
  tileHeight: 370,
  tileGapX: 32,
  tileGapY: 32,
  declaredRowWidth: 1736,
  rowHeight: 434,
  gridWidth: 1576,
  itemsPerStrand: 8,
  contentWidth: 1576,
  pageMarginTop: 30,
  sceneContainerHeight: 892,
  sceneBottomMargin: 30,
  inputHeight: 72,
  inputMarginBottom: 32,
  inputMaxLength: 128,
  inputDebounceMs: 500,
  imeX: 172,
  imeY: 198,
  resultsTravelOsk: 440,
  resultsTravelVoice: 90,
  resultsTravelBluetooth: 0,
  paneTravelError: -220,
  spring: {stiffness: 100, damping: 100, mass: 0.2},
  overflowTilePadding: 32,
  viewAllTileBackground: '#020408',
  captionHeight: 60,
  captionLines: 2,
} as const;

export const SHELL_PROFILE = {
  level: {iconSize: 48, numberMarginLeft: 6, numberOpacity: 0.7},
  smallLevel: {containerWidth: 80, iconSize: 48, textMarginTop: -2},
  largeLevel: {containerMarginTop: 56, iconSize: 108},
  avatar: {
    containerWidth: 256,
    containerHeight: 128,
    containerMarginBottom: 68,
    iconSize: 128,
    textMarginLeft: -11,
    textMarginBottom: 13,
  },
  trophy: {
    width: 370,
    height: 456,
    gradeRowMarginTop: 10,
    earnedMargin: 32,
    earnedLabelOpacity: 0.7,
    errorImageWidth: 306,
    errorImageHeight: 236,
    errorImageMargin: 32,
  },
  rowHeightLarge: 130,
  rowHeightSmall: 98,
  squareWidth: 370,
  squareHeightLarge: 340,
  squareHeightSmall: 314,
  squareHeightLargeVeryLargeFont: 360,
  avatarSize: 144,
  avatarMarginTop: 32,
  avatarMarginRight: 113,
  avatarMarginBottom: 24,
  nameWidth: 322,
  tagMargin: 16,
  footer: {top: 1038, marginLeft: 16, opacity: 0.7, fromBottom: 42},
} as const;

export interface ShellTileSpec {
  name: string;
  width?: number;
  height: number;
  mediaWidth: number;
  mediaHeight: number;
  primaryPadding: number;
  secondaryPadding: number;
  fallbackIcon: number;
  labelLines: number;
  metaHeight?: number;
}

const tile = (
  name: string,
  width: number | undefined,
  height: number,
  mediaWidth: number,
  mediaHeight: number,
  primaryPadding: number,
  secondaryPadding: number,
  fallbackIcon: number,
  labelLines: number,
  metaHeight?: number,
): ShellTileSpec => ({name, width, height, mediaWidth, mediaHeight, primaryPadding, secondaryPadding, fallbackIcon, labelLines, metaHeight});

/** HOME m721 closed content-tile catalogue. This is deliberately separate from the 106/168 HOME strand. */
export const SHELL_TILE_CATALOGUE: readonly ShellTileSpec[] = [
  tile('PLAIN.SQUARE.LARGE', 504, 504, 504, 504, 24, 16, 92, 1),
  tile('PLAIN.SQUARE.MEDIUM', 370, 370, 370, 370, 24, 16, 72, 1),
  tile('PLAIN.SQUARE.SMALL', 296, 296, 296, 296, 16, 8, 64, 1),
  tile('PLAIN.SQUARE.XSMALL', 236, 236, 236, 236, 16, 8, 64, 1),
  tile('PLAIN.WIDE.LARGE', 504, 284, 504, 284, 24, 16, 92, 1),
  tile('PLAIN.WIDE.MEDIUM', 370, 208, 370, 208, 16, 8, 72, 1),
  tile('PLAIN.WIDE.SMALL', 236, 133, 236, 133, 16, 8, 64, 1),
  tile('PLAIN.TALL.MEDIUM', 370, 555, 370, 555, 16, 8, 72, 1),
  tile('PLAIN.FULL.LARGE', 772, 579, 772, 579, 24, 16, 72, 1),
  tile('STACKED.LARGE.FULL', 504, 456, 504, 284, 24, 16, 92, 1, 172),
  tile('STACKED.LARGE.DESCRIPTION', 504, 448, 504, 284, 24, 16, 92, 2, 164),
  tile('STACKED.LARGE.DUAL_LABEL', 504, 442, 504, 284, 24, 16, 92, 2, 158),
  tile('STACKED.LARGE.LABEL', 504, 408, 504, 284, 24, 16, 92, 2, 116),
  tile('STACKED.MEDIUM.FULL', 370, 344, 370, 208, 16, 8, 72, 1, 136),
  tile('STACKED.MEDIUM.DESCRIPTION', 370, 340, 370, 208, 16, 8, 72, 2, 136),
  tile('STACKED.MEDIUM.DUAL_LABEL', 370, 334, 370, 208, 16, 8, 72, 2, 126),
  tile('STACKED.MEDIUM.LABEL', 370, 300, 370, 208, 16, 8, 72, 2, 92),
  tile('STACKED.MEDIUM.SQUARE_DUAL_LABEL', 370, 498, 370, 370, 16, 8, 72, 2, 128),
  tile('STACKED.SMALL.LABEL', 236, 201, 236, 133, 16, 8, 64, 2, 68),
  tile('SLIM.SQUARE', undefined, 192, 144, 144, 24, 8, 64, 2),
  tile('SLIM.WIDE', undefined, 192, 144, 81, 24, 8, 64, 2),
] as const;

export type ShellMarqueeSpeed = 'very-slow' | 'slow' | 'normal' | 'fast';
export type ShellMarqueeStatus = 'stop-at-left' | 'moving' | 'stop-at-right' | 'short';

/** Managed UI3 marquee cycle: dwell, one-way scroll, fade out, snap, fade in. */
export class ShellMarqueeCycle {
  static readonly dwellMs = 2000;
  static readonly referenceFrameMs = 16.6667;
  static readonly fadeOutMs = 300;
  static readonly fadeInMs = 250;

  velocity = 1;
  speed: ShellMarqueeSpeed = 'normal';
  offset = 0;
  opacity = 1;
  status: ShellMarqueeStatus = 'stop-at-left';
  private elapsedMs = 0;
  private dwellRemainingMs = ShellMarqueeCycle.dwellMs;
  private fadeMs = 0;
  private fadingOut = false;
  private fadingIn = false;

  reset(): void {
    this.offset = 0;
    this.opacity = 1;
    this.elapsedMs = 0;
    this.fadeMs = 0;
    this.fadingOut = false;
    this.fadingIn = false;
    this.dwellRemainingMs = ShellMarqueeCycle.dwellMs;
    this.status = 'stop-at-left';
  }

  setShort(): void {
    this.reset();
    this.status = 'short';
  }

  advance(deltaMs: number, scrollDistance: number): boolean {
    if (this.status === 'short' || scrollDistance <= 0) {
      return false;
    }
    if (!(deltaMs > 0) || !Number.isFinite(deltaMs)) {
      return true;
    }
    if (this.fadingOut) {
      this.fadeMs += deltaMs;
      this.opacity = clamp01(1 - this.fadeMs / ShellMarqueeCycle.fadeOutMs);
      if (this.fadeMs >= ShellMarqueeCycle.fadeOutMs) {
        this.offset = 0;
        this.elapsedMs = 0;
        this.fadingOut = false;
        this.fadingIn = true;
        this.fadeMs = 0;
        this.status = 'stop-at-left';
      }
      return true;
    }
    if (this.fadingIn) {
      this.fadeMs += deltaMs;
      this.opacity = clamp01(this.fadeMs / ShellMarqueeCycle.fadeInMs);
      if (this.fadeMs >= ShellMarqueeCycle.fadeInMs) {
        this.fadingIn = false;
        this.opacity = 1;
        this.dwellRemainingMs = ShellMarqueeCycle.dwellMs;
      }
      return true;
    }
    if (this.dwellRemainingMs > 0) {
      this.dwellRemainingMs -= deltaMs;
      this.status = 'stop-at-left';
      return true;
    }
    this.elapsedMs += deltaMs;
    this.status = 'moving';
    this.offset = this.velocity * marqueeSpeedCoefficient(this.speed) * (this.elapsedMs / ShellMarqueeCycle.referenceFrameMs);
    if (this.offset >= scrollDistance) {
      this.offset = scrollDistance;
      this.status = 'stop-at-right';
      this.fadingOut = true;
      this.fadeMs = 0;
    }
    return true;
  }
}

export function marqueeSpeedCoefficient(speed: ShellMarqueeSpeed): number {
  return speed === 'fast' ? 1.5 : speed === 'slow' ? 0.5 : speed === 'very-slow' ? 0.25 : 1;
}

/** HOME m584: the 1920px pan jumps immediately; only the arriving space springs in. */
export class ShellSpaceTransition {
  readonly pitch = 1920;
  readonly opacities: ShellSpring[];
  selectedIndex = 0;

  constructor(spaceCount: number) {
    this.opacities = Array.from({length: Math.max(0, spaceCount)}, (_, index) => {
      const spring = new ShellSpring(0.001, 0.005);
      spring.snapTo(index === 0 ? 1 : 0);
      return spring;
    });
  }

  get translateX(): number { return -this.pitch * this.selectedIndex; }

  select(index: number): void {
    if (this.opacities.length === 0) {
      return;
    }
    this.selectedIndex = Math.max(0, Math.min(index, this.opacities.length - 1));
    this.opacities.forEach((spring, itemIndex) => {
      if (itemIndex === this.selectedIndex) {
        spring.springTo(1, HOME_SPRINGS.slow);
      } else {
        spring.snapTo(0);
      }
    });
  }

  advance(seconds: number): boolean {
    return this.opacities.reduce((moving, spring) => spring.advance(seconds) || moving, false);
  }
}

function clamp01(value: number): number {
  return Math.max(0, Math.min(value, 1));
}
