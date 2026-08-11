import {HOME_GEOMETRY, type ShellRect} from './shellHomeMotion';

/**
 * The Home-owned side of the hub contract, recovered in
 * docs/sony-shell/ps5-hub-and-cards.md §1.3-1.5 (NPXS40002 m130/m571/m507,
 * NPXS40033 m355). Only the shell machinery lives here: the vertical axis,
 * the selected-tile handoff into the 80x80 hub-header badge, the hub-appears
 * timeline, and the one-shot focusReady gate. Hub content itself belongs to
 * the guest app module and is never invented on this side.
 */
export const HUB_TRANSITION = {
  /** -(SYSTEM_HEIGHT 126 + VERTICAL_HEIGHT_CHANGE 40). */
  homeLift: -166,
  verticalHeightChange: 40,
  minimizedSize: 80,
  minimizedMargin: {top: 48, left: 48},
  minimizedScale: 80 / 168,
  /** m571 pre-scale drivers; rendered dx/dy are -168/+44 inside the shell. */
  selectedTilePreScale: {translateX: -106, translateY: 44 * (106 / 168)},
  /** HubContainer standalone content boundary (`marginTop = 128`). */
  moduleBoundaryY: 128,
  /** m214 MINIMIZED_TITLE_MARGIN_LEFT/TOP. */
  titleMinimizedMargin: {left: 44, top: 9},
} as const;

/**
 * Absolute rendered rect of the selected tile at vertical progress v
 * (0 = home, 1 = hub), including the -166v home lift. Every m571 driver uses
 * SPRING_OPTIONS_FAST, so one normalized spring value reproduces the coupled
 * translate+scale trajectory exactly. At v=1 the tile is the 80x80 badge at
 * (48, 48) — the same visual object HubHeader draws as its icon.
 */
export function hubSelectedTileRect(v: number): ShellRect {
  const start = HOME_GEOMETRY.focusedTileSize;
  const size = start + (HUB_TRANSITION.minimizedSize - start) * v;
  return {
    x: HOME_GEOMETRY.focusedTileLeft + (HUB_TRANSITION.minimizedMargin.left - HOME_GEOMETRY.focusedTileLeft) * v,
    y: HOME_GEOMETRY.systemHeight + (HUB_TRANSITION.minimizedMargin.top - HOME_GEOMETRY.systemHeight) * v,
    width: size,
    height: size,
  };
}

/**
 * Absolute TitleContainer pose at vertical progress v. The 62px strip stays
 * centre-aligned on the badge: at v=1 its left edge is 48+80+44 = 172 and its
 * top is the badge centre minus half the strip (= 48+9 = 57, matching
 * MINIMIZED_TITLE_MARGIN_TOP against the header container).
 */
export function hubTitlePose(v: number): {x: number; y: number} {
  const restX = HOME_GEOMETRY.titleX;
  const restY = HOME_GEOMETRY.systemHeight + HOME_GEOMETRY.titleY;
  const hubX = HUB_TRANSITION.minimizedMargin.left
    + HUB_TRANSITION.minimizedSize
    + HUB_TRANSITION.titleMinimizedMargin.left;
  const hubY = HUB_TRANSITION.minimizedMargin.top + HUB_TRANSITION.titleMinimizedMargin.top;
  return {x: restX + (hubX - restX) * v, y: restY + (hubY - restY) * v};
}

/**
 * One-shot focusReady gate (NPXS40002 m503 / NPXS40033 m355). The hub app
 * calls focusReady exactly once when its content first reports focusable;
 * until then Down/X on the tile is swallowed. No executing guest module
 * exists yet, so the default runtime keeps every experience unready — the
 * observable "hub won't open yet" behaviour, exactly as on the console
 * before a hub finishes booting.
 */
export class ShellHubReadiness {
  private readonly ready = new Set<string>();

  focusReady(experienceId: string): void {
    if (experienceId) {
      this.ready.add(experienceId);
    }
  }

  isReady(experienceId: string | undefined): boolean {
    return !!experienceId && this.ready.has(experienceId);
  }
}

/** CSS cubic-bezier(x1, y1, x2, y2) evaluated at x, Newton with bisection fallback. */
export function cubicBezierEase(x1: number, y1: number, x2: number, y2: number, x: number): number {
  if (x <= 0) {
    return 0;
  }
  if (x >= 1) {
    return 1;
  }
  const sampleX = (t: number) => 3 * t * (1 - t) * (1 - t) * x1 + 3 * t * t * (1 - t) * x2 + t * t * t;
  const sampleY = (t: number) => 3 * t * (1 - t) * (1 - t) * y1 + 3 * t * t * (1 - t) * y2 + t * t * t;
  let t = x;
  for (let i = 0; i < 8; i += 1) {
    const error = sampleX(t) - x;
    if (Math.abs(error) < 1e-6) {
      return sampleY(t);
    }
    const derivative = 3 * (1 - t) * (1 - t) * x1 + 6 * t * (1 - t) * (x2 - x1) + 3 * t * t * (1 - x2);
    if (Math.abs(derivative) < 1e-6) {
      break;
    }
    t -= error / derivative;
  }
  let low = 0;
  let high = 1;
  t = x;
  while (high - low > 1e-6) {
    if (sampleX(t) < x) {
      low = t;
    } else {
      high = t;
    }
    t = (low + high) / 2;
  }
  return sampleY(t);
}

/**
 * The one-shot "hub appears" timeline (NPXS40002 m507 useHubAnimation): a
 * 16.67ms pre-roll at opacity 0 / progress 0.95 (translateY 42.5), then
 * opacity 0→1 and progress 0.95→1 in parallel over 300ms with
 * cubic-bezier(0.25, 0.1, 0.25, 0.8). hide() parks the layer at progress 0
 * (translateY 850, offscreen) with opacity restored to 1.
 */
export class HubAppearAnimation {
  static readonly preRollMs = 16.67;
  static readonly durationMs = 300;
  static readonly hiddenTranslateY = 850;

  private elapsedMs = 0;
  private visible = false;

  show(): void {
    this.visible = true;
    this.elapsedMs = 0;
  }

  hide(): void {
    this.visible = false;
    this.elapsedMs = 0;
  }

  advance(deltaMs: number): void {
    if (this.visible && deltaMs > 0 && Number.isFinite(deltaMs)) {
      this.elapsedMs += deltaMs;
    }
  }

  get running(): boolean {
    return this.visible && this.elapsedMs < HubAppearAnimation.preRollMs + HubAppearAnimation.durationMs;
  }

  private eased(): number {
    const t = (this.elapsedMs - HubAppearAnimation.preRollMs) / HubAppearAnimation.durationMs;
    return cubicBezierEase(0.25, 0.1, 0.25, 0.8, t);
  }

  get opacity(): number {
    if (!this.visible) {
      return 1;
    }
    return this.elapsedMs <= HubAppearAnimation.preRollMs ? 0 : this.eased();
  }

  get progress(): number {
    if (!this.visible) {
      return 0;
    }
    if (this.elapsedMs <= HubAppearAnimation.preRollMs) {
      return 0.95;
    }
    return 0.95 + 0.05 * this.eased();
  }

  get translateY(): number {
    return (1 - this.progress) * HubAppearAnimation.hiddenTranslateY;
  }
}
