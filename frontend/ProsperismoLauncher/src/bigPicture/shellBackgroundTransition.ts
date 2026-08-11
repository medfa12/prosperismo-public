import type {ShellRect} from './shellHomeMotion';

/**
 * The background transition contract recovered from the managed BGLayer
 * assembly (see docs/sony-shell/bglayer-managed-contract.md §1). The shell
 * side owns the type word, the degree-derived duration and — the part a
 * centre-only implementation gets visibly wrong — the ripple origin.
 *
 * The native pass that actually draws the ripple is not reproduced here.
 */

export const BACKGROUND_TRANSITION_TYPE = {
  invalid: -1,
  launchingGame: 0,
  hide: 1,
  launchingGameBC: 2,
  systemDefault: 5,
  /** `CustomImageRipple` and `CustomImage` are the same value: 6. */
  customImageRipple: 6,
  customImageSlideInLeft: 7,
  customImageSlideInRight: 8,
  customImageFade: 9,
  customImageRippleBack: 10,
  /** 12.40+ only; absent from the 3.20 assembly. */
  fadeToBlack: 11,
} as const;

export type BackgroundTransitionType =
  (typeof BACKGROUND_TRANSITION_TYPE)[keyof typeof BACKGROUND_TRANSITION_TYPE];

export const BACKGROUND_TRANSITION_DEGREE = {
  crossFade: 0,
  subtle: 1,
  normal: 2,
  strong: 3,
} as const;

export type BackgroundTransitionDegree =
  (typeof BACKGROUND_TRANSITION_DEGREE)[keyof typeof BACKGROUND_TRANSITION_DEGREE];

export const BACKGROUND_TRANSITION_FLAG = {
  endTransition: 0x01,
  requestOldImage: 0x02,
  requestNextImage: 0x04,
  requestNextOverlayImage: 0x08,
  canceledTransition: 0x10,
  cancelable: 0x20,
  requestFallbackImage: 0x40,
  basematAnimationInProgress: 0x80,
} as const;

/** BGBasematType. EllipseWide and EllipseNarrow are both 3 in this firmware. */
export const BACKGROUND_BASEMAT_TYPE = {
  none: 0,
  flat: 1,
  linear: 2,
  ellipseWide: 3,
  ellipseNarrow: 3,
} as const;

/* eslint-disable no-bitwise -- the packed type word is the firmware ABI. */

/** `Type = type | ((degree & 0xFF) << 16)`. */
export function packBackgroundTransitionType(
  type: BackgroundTransitionType,
  degree: BackgroundTransitionDegree,
): number {
  return type | ((degree & 0xff) << 16);
}

export function unpackBackgroundTransitionType(packed: number): {
  type: number;
  degree: number;
} {
  return {type: packed & 0xffff, degree: (packed >>> 16) & 0xff};
}

/* eslint-enable no-bitwise */

/**
 * The native owner derives 300ms + degree * 166.66667ms, so HOME's Normal
 * selection transition is 633.333ms.
 */
export function backgroundTransitionDurationMs(degree: BackgroundTransitionDegree): number {
  return 300 + degree * (500 / 3);
}

/**
 * Every transition that presents a *new* image flips the double-buffered
 * plate id; Hide and SystemDefault do not.
 */
export function backgroundTransitionFlipsPlate(type: BackgroundTransitionType): boolean {
  return type === BACKGROUND_TRANSITION_TYPE.launchingGame
    || type === BACKGROUND_TRANSITION_TYPE.launchingGameBC
    || type === BACKGROUND_TRANSITION_TYPE.customImageRipple
    || type === BACKGROUND_TRANSITION_TYPE.customImageSlideInLeft
    || type === BACKGROUND_TRANSITION_TYPE.customImageSlideInRight
    || type === BACKGROUND_TRANSITION_TYPE.customImageFade
    || type === BACKGROUND_TRANSITION_TYPE.customImageRippleBack;
}

export interface BackgroundTransitionOrigin {
  x: number;
  y: number;
}

const DESIGN_WIDTH = 1920;
const DESIGN_HEIGHT = 1080;

/**
 * Normalized ripple origin: `CenterX = screenX / 1920`, `CenterY = screenY /
 * 1080`. The point is the centre of the currently focused widget, so a launch
 * ripples out of the selected tile. Only when nothing is focused does it fall
 * back to the screen centre (960, 540) -> (0.5, 0.5).
 */
export function backgroundTransitionOrigin(focused?: ShellRect): BackgroundTransitionOrigin {
  if (!focused
    || !Number.isFinite(focused.x) || !Number.isFinite(focused.y)
    || !Number.isFinite(focused.width) || !Number.isFinite(focused.height)) {
    return {x: 0.5, y: 0.5};
  }
  return {
    x: (focused.x + focused.width / 2) / DESIGN_WIDTH,
    y: (focused.y + focused.height / 2) / DESIGN_HEIGHT,
  };
}
