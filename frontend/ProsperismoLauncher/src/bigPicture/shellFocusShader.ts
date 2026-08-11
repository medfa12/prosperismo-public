import {focusShimmer, type ShellRect} from './shellHomeMotion';

export interface ShellFocusColor {
  r: number;
  g: number;
  b: number;
}

/** UI3 FocusRenderManager.DefaultColorTable, preserved as unit RGB values. */
export const SHELL_FOCUS_COLORS: readonly ShellFocusColor[] = [
  {r: 0.8, g: 1, b: 1},
  {r: 0.78039217, g: 0.8901961, b: 1},
  {r: 0.8980392, g: 0.8980392, b: 1},
  {r: 11 / 15, g: 0.76862746, b: 79 / 85},
  {r: 47 / 51, g: 0.78039217, b: 0.8745098},
  {r: 1, g: 0.8745098, b: 0.7490196},
  {r: 1, g: 0.8, b: 0.8},
] as const;

export const SHELL_FOCUS_SHADER = {
  lineMinOpacity: 0.065,
  areaMinOpacity: 0,
  lineAlphaGamma: 1,
  areaAlphaGamma: 0.8,
  noiseScale: 5,
  noiseMoveFrequency: 0.25,
  areaEdgeFadeLength: 5,
  edgeFadeMinLength: 10,
  areaRenderingThreshold: 0.4,
  areaOpacityDecreaseRateBySize: 30,
  areaOpacityMinimumDecreaseValueBySize: 0.5,
  pressingIntensity: 0.15,
} as const;

export function roundedBoxDistance(
  x: number,
  y: number,
  halfWidth: number,
  halfHeight: number,
  radius: number,
): number {
  const safeRadius = Math.max(0, Math.min(radius, Math.min(halfWidth, halfHeight)));
  const qx = Math.abs(x) - halfWidth + safeRadius;
  const qy = Math.abs(y) - halfHeight + safeRadius;
  return Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) + Math.min(Math.max(qx, qy), 0) - safeRadius;
}

export function focusSmoothStep(edge0: number, edge1: number, value: number): number {
  if (edge0 === edge1) {
    return value < edge0 ? 0 : 1;
  }
  const t = Math.max(0, Math.min((value - edge0) / (edge1 - edge0), 1));
  return t * t * (3 - 2 * t);
}

export function focusBandCoverage(distance: number, bandWidth: number): number {
  const half = Math.max(0.5, bandWidth * 0.5);
  return 1 - focusSmoothStep(half, half + 1, Math.abs(distance));
}

export function focusAreaCoverage(distance: number, fade = 10): number {
  return 1 - focusSmoothStep(0, Math.max(0.5, fade), Math.max(distance, 0));
}

export function focusAreaPassApplies(rect: ShellRect, screen: {width: number; height: number}): boolean {
  if (!(screen.width > 0) || !(screen.height > 0)) {
    return false;
  }
  return rect.width / screen.width * (rect.height / screen.height) < SHELL_FOCUS_SHADER.areaRenderingThreshold;
}

export function focusNoiseOffset(seconds: number): readonly [number, number] {
  const angle = seconds * SHELL_FOCUS_SHADER.noiseMoveFrequency;
  return [Math.sin(angle), Math.cos(angle)];
}

/** (St / 5 + orbit) * .5 + .5; noise scale is a divisor, not a multiplier. */
export function focusNoiseUv(stX: number, stY: number, seconds: number): readonly [number, number] {
  const [changeX, changeY] = focusNoiseOffset(seconds);
  return [
    (stX / SHELL_FOCUS_SHADER.noiseScale + changeX) * 0.5 + 0.5,
    (stY / SHELL_FOCUS_SHADER.noiseScale + changeY) * 0.5 + 0.5,
  ];
}

export function focusDiagonalRamp(stX: number, stY: number): number {
  return Math.max(0, Math.min(0.5 + 0.25 * (stY - stX), 1));
}

export function focusShimmerAcross(seconds: number, diagonal: number): number {
  const [start, end] = focusShimmer(seconds);
  const progress = Math.max(0, Math.min(diagonal, 1));
  return (start + (end - start) * progress) * 0.5;
}

export function focusLineTableCoordinate(noise: number): number {
  return Math.pow(Math.max(0, Math.min(noise, 1)), 1.5);
}

/** Natural-cubic UI3 line-alpha curve through (0,0), (.2,.25), (.9,.9), (1,1). */
export function focusLineToneCurve(value: number): number {
  const x = Math.max(0, Math.min(value, 1));
  if (x <= 0.2) {
    return cubic(x, 0, 0.06742977958332985, 10.114466937499461, -21.008079177080553);
  }
  if (x <= 0.9) {
    return cubic(x - 0.2, 0.25, 1.592247053333448, -2.490380568748873, 2.2032464762493706);
  }
  return cubic(x - 0.9, 0.9, 1.3444865771716008, 2.1364370313748053, -55.81302803090816);
}

export function focusColorAt(value: number): ShellFocusColor {
  const coordinate = Math.max(0, Math.min(value, 1)) * (SHELL_FOCUS_COLORS.length - 1);
  const lower = Math.floor(coordinate);
  const upper = Math.min(lower + 1, SHELL_FOCUS_COLORS.length - 1);
  const progress = coordinate - lower;
  const from = SHELL_FOCUS_COLORS[lower];
  const to = SHELL_FOCUS_COLORS[upper];
  return {
    r: from.r + (to.r - from.r) * progress,
    g: from.g + (to.g - from.g) * progress,
    b: from.b + (to.b - from.b) * progress,
  };
}

export function focusApplyAlphaGamma(value: number, gamma: number): number {
  return Math.pow(Math.max(0, Math.min(value, 1)), gamma);
}

function cubic(t: number, a: number, b: number, c: number, d: number): number {
  return Math.max(0, Math.min(a + t * (b + t * (c + t * d)), 1));
}
