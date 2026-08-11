export interface ShellRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export const HOME_GEOMETRY = {
  designWidth: 1920,
  designHeight: 1080,
  systemHeight: 126,
  contentInset: 84,
  tileSize: 106,
  focusedTileSize: 168,
  focusedTileLeft: 172,
  itemMargin: 8,
  focusedMargin: 16,
  tileRadius: 16,
  maxTiles: 11,
  titleX: 356,
  titleY: 106,
  titleStripHeight: 62,
  focusTrap: {left: 204, top: 157, size: 106, radius: 16},
  systemIconSize: 56,
  systemIconRestSize: 48,
  systemIconMargin: 48,
  clockMargin: 88,
} as const;

export const HOME_SPRINGS = {
  strand: {stiffness: 400, damping: 50, mass: 0.2, overshootClamping: true},
  slow: {stiffness: 130, damping: 25, mass: 1, overshootClamping: true},
  slower: {stiffness: 100, damping: 20, mass: 1, overshootClamping: true},
  fast: {stiffness: 200, damping: 100, mass: 0.2, overshootClamping: false},
  faster: {stiffness: 600, damping: 100, mass: 0.2, overshootClamping: false},
} as const;

export type HomeSpringConfig = (typeof HOME_SPRINGS)[keyof typeof HOME_SPRINGS];

export function homeTileScale(index: number, selectedIndex: number): number {
  return index === selectedIndex ? 1 : HOME_GEOMETRY.tileSize / HOME_GEOMETRY.focusedTileSize;
}

/** HOME m531 position solver, expressed as the drawn tile's left edge. */
export function homeTileLeft(index: number, selectedIndex: number): number {
  const {focusedTileLeft, focusedTileSize, tileSize, itemMargin, focusedMargin} = HOME_GEOMETRY;
  if (index === selectedIndex) {
    return focusedTileLeft;
  }
  if (index < selectedIndex) {
    return focusedTileLeft - focusedMargin - tileSize - (selectedIndex - index - 1) * (tileSize + itemMargin);
  }
  return focusedTileLeft + focusedTileSize + focusedMargin + (index - selectedIndex - 1) * (tileSize + itemMargin);
}

export function homeTileRadius(side: number): number {
  return side * HOME_GEOMETRY.tileRadius / HOME_GEOMETRY.tileSize;
}

/** HOME m573: only the eighth, ninth and tenth titles after selection take a mat. */
export function homeTileMatOpacity(index: number, selectedIndex: number): number {
  const distance = index - Math.max(0, selectedIndex);
  if (distance === 8) {
    return 0.05;
  }
  if (distance === 9) {
    return 0.2;
  }
  return distance === 10 ? 0.4 : 0;
}

export function homeTitleNameWidth(options: {
  entitlementIcon?: boolean;
  storageIcon?: boolean;
  platformTag?: boolean;
  packageTag?: boolean;
}): number {
  return 1132
    - (options.entitlementIcon ? 54 : 0)
    - (options.storageIcon ? 54 : 0)
    - (options.platformTag ? 76 : 0)
    - (options.packageTag ? 260 : 0);
}

export function showHomePlatformTag(platformType: string | undefined): boolean {
  return !!platformType && platformType !== 'PPR';
}

export function showHomePackageTag(packageType: string | undefined): boolean {
  return !!packageType && packageType !== 'FULL' && packageType !== 'DEMO';
}

export class ShellSpring {
  private valueValue = 0;
  private velocityValue = 0;
  private targetValue = 0;
  private config: HomeSpringConfig = HOME_SPRINGS.strand;
  private settled = true;

  constructor(private readonly restDisplacement = 0.0005, private readonly restVelocity = 0.005) {}

  get value(): number { return this.valueValue; }
  get velocity(): number { return this.velocityValue; }
  get target(): number { return this.targetValue; }
  get isSettled(): boolean { return this.settled; }

  snapTo(value: number): void {
    this.valueValue = value;
    this.targetValue = value;
    this.velocityValue = 0;
    this.settled = true;
  }

  springTo(target: number, config: HomeSpringConfig): void {
    this.config = config;
    this.targetValue = target;
    if (Math.abs(target - this.valueValue) <= this.restDisplacement && Math.abs(this.velocityValue) <= this.restVelocity) {
      this.settle();
      return;
    }
    this.settled = false;
  }

  settle(): void {
    this.valueValue = this.targetValue;
    this.velocityValue = 0;
    this.settled = true;
  }

  advance(seconds: number): boolean {
    if (this.settled) {
      return false;
    }
    if (!(seconds > 0) || !Number.isFinite(seconds)) {
      return true;
    }
    let remaining = Math.min(seconds, 0.064);
    while (remaining > 0) {
      const dt = Math.min(remaining, 0.001);
      remaining -= dt;
      const acceleration = (
        -this.config.stiffness * (this.valueValue - this.targetValue)
        - this.config.damping * this.velocityValue
      ) / Math.max(1e-6, this.config.mass);
      this.velocityValue += acceleration * dt;
      const next = this.valueValue + this.velocityValue * dt;
      if (this.config.overshootClamping && crossed(this.valueValue, next, this.targetValue)) {
        this.settle();
        return false;
      }
      this.valueValue = next;
    }
    if (Math.abs(this.targetValue - this.valueValue) <= this.restDisplacement && Math.abs(this.velocityValue) <= this.restVelocity) {
      this.settle();
      return false;
    }
    return true;
  }
}

function crossed(from: number, to: number, target: number): boolean {
  return (from <= target && to > target) || (from >= target && to < target);
}

export class HomeStartupChoreography {
  static readonly tileStaggerMs = 60;
  static readonly systemDelayMs = 1050;
  static readonly titleDelayMs = 1383;
  static readonly hubDelayMs = 1450;

  private readonly switcher = new ShellSpring();
  private readonly system = new ShellSpring();
  private readonly systemOpacity = new ShellSpring();
  private readonly titleOpacity = new ShellSpring();
  private readonly hub = new ShellSpring();
  private tiles: ShellSpring[] = [];
  private elapsedValue = 0;
  running = false;

  begin(tileCount: number): void {
    this.elapsedValue = 0;
    this.tiles = Array.from({length: Math.max(0, Math.min(tileCount, HOME_GEOMETRY.maxTiles))}, () => {
      const spring = new ShellSpring();
      spring.snapTo(1);
      return spring;
    });
    for (const spring of [this.switcher, this.system, this.systemOpacity, this.titleOpacity, this.hub]) {
      spring.snapTo(1);
    }
    this.switcher.springTo(0, HOME_SPRINGS.slower);
    this.running = true;
  }

  advance(deltaMs: number): boolean {
    if (!this.running || !(deltaMs > 0) || !Number.isFinite(deltaMs)) {
      return this.running;
    }
    this.elapsedValue += deltaMs;
    this.tiles.forEach((spring, index) => {
      if (this.elapsedValue >= index * HomeStartupChoreography.tileStaggerMs && spring.target !== 0) {
        spring.springTo(0, HOME_SPRINGS.slower);
      }
    });
    if (this.elapsedValue >= HomeStartupChoreography.systemDelayMs) {
      this.system.springTo(0, HOME_SPRINGS.slow);
      this.systemOpacity.springTo(0, HOME_SPRINGS.slow);
    }
    if (this.elapsedValue >= HomeStartupChoreography.titleDelayMs) {
      this.titleOpacity.springTo(0, HOME_SPRINGS.slow);
    }
    if (this.elapsedValue >= HomeStartupChoreography.hubDelayMs) {
      this.hub.springTo(0, HOME_SPRINGS.slow);
    }
    const seconds = deltaMs / 1000;
    let busy = false;
    for (const spring of [this.switcher, this.system, this.systemOpacity, this.titleOpacity, this.hub, ...this.tiles]) {
      busy = spring.advance(seconds) || busy;
    }
    this.running = busy || this.elapsedValue < HomeStartupChoreography.hubDelayMs;
    return this.running;
  }

  settle(): void {
    for (const spring of [this.switcher, this.system, this.systemOpacity, this.titleOpacity, this.hub, ...this.tiles]) {
      spring.snapTo(0);
    }
    this.running = false;
  }

  get elapsedMs(): number { return this.elapsedValue; }
  get switcherTranslateX(): number { return this.switcher.value * HOME_GEOMETRY.designWidth; }
  get switcherTranslateY(): number { return this.system.value * 31; }
  get systemTranslateY(): number { return this.system.value * -20; }
  get systemAlpha(): number { return 1 - this.systemOpacity.value; }
  get titleAlpha(): number { return 1 - this.titleOpacity.value; }
  get hubTranslateY(): number { return this.hub.value * 20; }
  get hubAlpha(): number { return 1 - this.hub.value; }
  tileProgress(index: number): number { return this.tiles[index]?.value ?? 0; }
}

export type ShellFocusState = 'hidden' | 'showing' | 'shown' | 'hiding';

export interface ShellFocusSnapshot {
  state: ShellFocusState;
  rect: ShellRect;
  radius: number;
  areaOpacity: number;
  lineOpacity: number;
  bandWidth: number;
  inOutScale: number;
  warpStretch: number;
  travelAngle: number;
  moving: number;
  pressing: number;
  shimmer: readonly [number, number];
}

/** Exact UI3 one-owner focus timeline translated from ShellFocusRingTimeline. */
export class ShellFocusTimeline {
  static readonly frameSeconds = 0.01666667;
  static readonly inSeconds = 0.3;
  static readonly inDelaySeconds = 0.2;
  static readonly outSeconds = 0.3;
  static readonly moveSeconds = 0.3;
  static readonly pressSeconds = 0.3;
  static readonly warpSeconds = 0.25;

  private stateValue: ShellFocusState = 'hidden';
  private from: ShellRect = {x: 0, y: 0, width: 1, height: 1};
  private to: ShellRect = this.from;
  private fromRadius = 0;
  private toRadius = 0;
  private showElapsed = 0;
  private warpElapsed = ShellFocusTimeline.warpSeconds;
  private moveElapsed = ShellFocusTimeline.moveSeconds;
  private fadeElapsed = 0;
  private pressElapsed = Number.MAX_VALUE;
  private momentum = 0;
  private distance = 0;
  private angle = 0;
  private keyRepeating = false;
  private clock = 0;

  showAt(rect: ShellRect, radius: number): void {
    if (!finiteRect(rect)) {
      return;
    }
    this.from = {...rect};
    this.to = {...rect};
    this.fromRadius = radius;
    this.toRadius = radius;
    this.warpElapsed = ShellFocusTimeline.warpSeconds;
    this.moveElapsed = ShellFocusTimeline.moveSeconds;
    this.momentum = 0;
    this.distance = 0;
    this.angle = 0;
    if (this.stateValue === 'hidden' || this.stateValue === 'hiding') {
      this.stateValue = 'showing';
      this.showElapsed = 0;
      this.fadeElapsed = 0;
    }
  }

  retarget(target: ShellRect, radius: number): void {
    if (!finiteRect(target)) {
      return;
    }
    if (this.stateValue === 'hidden') {
      this.showAt(target, radius);
      return;
    }
    if (rectsClose(this.to, target) && Math.abs(this.toRadius - radius) < 0.5) {
      return;
    }
    const current = this.currentRect();
    const currentRadius = this.currentRadius();
    this.from = current;
    this.fromRadius = currentRadius;
    this.to = {...target};
    this.toRadius = radius;
    const dx = centreX(target) - centreX(current);
    const dy = centreY(target) - centreY(current);
    const pixels = Math.hypot(dx, dy);
    this.momentum = focusMomentumFor(pixels);
    this.distance = Math.min(1, pixels / 1920);
    this.angle = Math.atan2(dy, dx) + Math.PI;
    this.warpElapsed = 0;
    this.moveElapsed = 0;
    if (this.stateValue === 'hiding') {
      this.stateValue = 'showing';
      this.showElapsed = ShellFocusTimeline.inDelaySeconds;
    }
  }

  hide(): void {
    if (this.stateValue === 'hidden' || this.stateValue === 'hiding') {
      return;
    }
    this.stateValue = 'hiding';
    this.showElapsed = 0;
    this.fadeElapsed = 0;
  }

  reset(): void {
    this.stateValue = 'hidden';
    this.showElapsed = 0;
    this.fadeElapsed = 0;
    this.warpElapsed = ShellFocusTimeline.warpSeconds;
    this.moveElapsed = ShellFocusTimeline.moveSeconds;
    this.pressElapsed = Number.MAX_VALUE;
  }

  press(): void { this.pressElapsed = 0; }
  setKeyRepeating(repeating: boolean): void { this.keyRepeating = repeating; }

  advance(seconds: number): void {
    if (!(seconds > 0) || !Number.isFinite(seconds)) {
      return;
    }
    this.clock += seconds;
    this.warpElapsed = Math.min(ShellFocusTimeline.warpSeconds, this.warpElapsed + seconds);
    this.moveElapsed = Math.min(ShellFocusTimeline.moveSeconds, this.moveElapsed + seconds);
    this.pressElapsed = Math.min(ShellFocusTimeline.pressSeconds * 2, this.pressElapsed + seconds);
    if (this.stateValue === 'showing') {
      this.showElapsed += seconds;
      if (this.showElapsed >= ShellFocusTimeline.inDelaySeconds + ShellFocusTimeline.inSeconds) {
        this.stateValue = 'shown';
      }
    } else if (this.stateValue === 'hiding') {
      this.showElapsed += seconds;
      this.fadeElapsed += seconds;
      if (this.showElapsed >= ShellFocusTimeline.outSeconds) {
        this.stateValue = 'hidden';
      }
    }
  }

  snapshot(): ShellFocusSnapshot {
    const showing = this.showing();
    const moving = this.moving();
    const pressing = this.pressing();
    const baseOpacity = this.baseOpacity();
    const side = Math.max(this.to.width, this.to.height, 1);
    const startScale = Math.min(1 + 80 / side, 1.2);
    const targetRatio = Math.max(this.to.height, 1) / Math.max(this.to.width, 1);
    const strain = targetRatio < 0.25 ? 0 : 0.75;
    return {
      state: this.stateValue,
      rect: this.currentRect(),
      radius: this.currentRadius(),
      areaOpacity: Math.max(0, baseOpacity * showing),
      lineOpacity: Math.max(0, baseOpacity * showing * (1 - 4 * moving)),
      bandWidth: (3 + lerp(3, 0, showing)) * (pressing > 0 ? 2 : 1) + (pressing > 0 ? 3 : 0),
      inOutScale: lerp(startScale, 1, showing),
      warpStretch: Math.min(0.2, strain * moving * this.distance),
      travelAngle: this.angle,
      moving,
      pressing,
      shimmer: focusShimmer(this.clock),
    };
  }

  private showing(): number {
    if (this.stateValue === 'shown') {
      return 1;
    }
    if (this.stateValue === 'showing') {
      return focusInOutCurve(clamp01((this.showElapsed - ShellFocusTimeline.inDelaySeconds) / ShellFocusTimeline.inSeconds));
    }
    if (this.stateValue === 'hiding') {
      return 1 - focusInOutCurve(clamp01(this.showElapsed / ShellFocusTimeline.outSeconds));
    }
    return 0;
  }

  private moving(): number {
    return this.moveElapsed >= ShellFocusTimeline.moveSeconds
      ? 0
      : 1 - focusMovingCurve(clamp01(this.moveElapsed / ShellFocusTimeline.moveSeconds));
  }

  private pressing(): number {
    if (this.pressElapsed >= ShellFocusTimeline.pressSeconds * 2) {
      return 0;
    }
    if (this.pressElapsed < ShellFocusTimeline.pressSeconds) {
      return focusInOutCurve(clamp01(this.pressElapsed / ShellFocusTimeline.pressSeconds));
    }
    return 1 - focusInOutCurve(clamp01((this.pressElapsed - ShellFocusTimeline.pressSeconds) / ShellFocusTimeline.pressSeconds));
  }

  private baseOpacity(): number {
    if (this.stateValue === 'showing') {
      return 1;
    }
    if (this.stateValue === 'shown') {
      return 1;
    }
    if (this.stateValue === 'hiding') {
      const rate = this.keyRepeating ? 2 : 1;
      return 1 - clamp01(this.fadeElapsed * rate / 0.2);
    }
    return 0;
  }

  private currentRect(): ShellRect {
    if (this.warpElapsed >= ShellFocusTimeline.warpSeconds) {
      return {...this.to};
    }
    const progress = clamp01((this.warpElapsed + ShellFocusTimeline.frameSeconds) / ShellFocusTimeline.warpSeconds);
    const k = focusWarpCurve(progress, this.momentum);
    const width = Math.max(0, lerp(this.from.width, this.to.width, k));
    const height = Math.max(0, lerp(this.from.height, this.to.height, k));
    const cx = lerp(centreX(this.from), centreX(this.to), k);
    const cy = lerp(centreY(this.from), centreY(this.to), k);
    return {x: cx - width / 2, y: cy - height / 2, width, height};
  }

  private currentRadius(): number {
    if (this.warpElapsed >= ShellFocusTimeline.warpSeconds) {
      return this.toRadius;
    }
    const progress = clamp01((this.warpElapsed + ShellFocusTimeline.frameSeconds) / ShellFocusTimeline.warpSeconds);
    return lerp(this.fromRadius, Math.max(this.toRadius, 0), focusMovingCurve(progress));
  }
}

export function focusInOutCurve(value: number): number {
  return value > 0 ? 1 - Math.pow(1 - value * 0.5, 10) : 0;
}

export function focusMovingCurve(value: number): number {
  return value > 0 ? 1 - Math.pow(1 - value * 0.5, 5) : 0;
}

export function focusWarpCurve(value: number, momentum: number): number {
  return 1 - (1 - momentum) * Math.pow(1 - value * 0.5, 10);
}

export function focusMomentumFor(distance: number): number {
  const t = (distance - 100) / 900;
  return Math.max(0, Math.min(0.5 + (0.9 - 0.5) * t, 0.9));
}

/** UI3 five-second two-channel shimmer: parked three seconds, sweeping for two. */
export function focusShimmer(seconds: number): readonly [number, number] {
  const channel = (time: number) => {
    const phase = ((time % 5) + 5) % 5;
    return Math.cos(Math.max(phase - 4, -1) * Math.PI);
  };
  return [channel(seconds), channel(seconds + 0.5)];
}

export function focusAreaOpacityScale(rect: ShellRect, screen: {width: number; height: number}): number {
  if (!(screen.width > 0) || !(screen.height > 0)) {
    return 0;
  }
  const coverage = rect.width / screen.width * (rect.height / screen.height);
  if (coverage >= 0.4) {
    return 0;
  }
  return Math.max(0.5, 1 - coverage * 30);
}

export class HomeGlanceState {
  private glanced = false;
  private modalVisible = false;
  private readonly icon = new AnalyticSpring(200, 100, 0.2, 0);
  private readonly label = new AnalyticSpring(200, 100, 0.2, 0);

  setGlanced(value: boolean): void {
    this.glanced = value;
    this.retarget();
  }

  setModalVisible(value: boolean): void {
    this.modalVisible = value;
    this.retarget();
  }

  advance(seconds: number): void {
    this.icon.advance(seconds);
    this.label.advance(seconds);
  }

  get iconScale(): number { return 48 / 56 + (1 - 48 / 56) * this.icon.value; }
  get labelOpacity(): number { return this.label.value; }
  get indicatorOpacity(): number { return this.glanced ? 1 : 0.7; }

  private retarget(): void {
    this.icon.setTarget(this.glanced || this.modalVisible ? 1 : 0);
    this.label.setTarget(this.glanced && !this.modalVisible ? 1 : 0);
  }
}

/**
 * ButtonBase.visibleOnFocus progress, derived from the recovered 48 -> 56
 * system-icon glance scale. Keeping this as a pure function ensures the white
 * plate and the glyph tint are driven by the same spring value.
 */
export function systemIconFocusProgress(iconScale: number): number {
  const rest = HOME_GEOMETRY.systemIconRestSize / HOME_GEOMETRY.systemIconSize;
  return clamp01((iconScale - rest) / (1 - rest));
}

/** HOME ShellNavBand: lerp(.08*t, 1*t, t). */
export function systemIconFocusBackgroundOpacity(iconScale: number): number {
  const progress = systemIconFocusProgress(iconScale);
  return 0.08 * progress + 0.92 * progress * progress;
}

/** HOME ButtonBase stock inversion: white (255) to #292929 (41). */
export function systemIconFocusedChannel(iconScale: number): number {
  const progress = systemIconFocusProgress(iconScale);
  return Math.round(255 + (41 - 255) * progress);
}

/**
 * FocusRenderManager line body. InOutScale affects only the recovered
 * thickness+offset margin; it never scales the focused widget's own rect.
 */
export function focusLineBody(
  rect: ShellRect,
  radius: number,
  inOutScale: number,
  lineScale = 1,
): {rect: ShellRect; radius: number} {
  const safeLineScale = Math.max(0.25, Math.min(lineScale, 2));
  const inflate = (3 + 3) * Math.max(0, inOutScale) * safeLineScale;
  return {
    rect: {
      x: rect.x - inflate,
      y: rect.y - inflate,
      width: rect.width + inflate * 2,
      height: rect.height + inflate * 2,
    },
    radius: Math.max(0, radius) + inflate,
  };
}

/** FocusStyle.ListItem: 3 px off the top and 5 px off the bottom. */
export function focusListItemRect(rect: ShellRect): ShellRect {
  return {
    x: rect.x,
    y: rect.y + 3,
    width: rect.width,
    height: Math.max(0, rect.height - 8),
  };
}

class AnalyticSpring {
  private from: number;
  private target: number;
  private elapsed: number;
  private readonly slow: number;
  private readonly fast: number;
  private readonly settleSeconds: number;
  private readonly settleValue: number;

  constructor(stiffness: number, damping: number, mass: number, initial: number) {
    const omega = Math.sqrt(stiffness / mass);
    const zeta = damping / (2 * Math.sqrt(stiffness * mass));
    const spread = Math.max(1e-6, Math.sqrt(Math.max(0, zeta * zeta - 1)));
    this.slow = omega * (zeta - spread);
    this.fast = omega * (zeta + spread);
    this.settleSeconds = Math.log(this.fast / ((this.fast - this.slow) * 0.001)) / this.slow;
    this.settleValue = this.displacement(this.settleSeconds);
    this.from = initial;
    this.target = initial;
    this.elapsed = this.settleSeconds;
  }

  get value(): number {
    if (this.elapsed >= this.settleSeconds) {
      return this.target;
    }
    const progress = this.displacement(this.elapsed) / this.settleValue;
    return this.from + (this.target - this.from) * progress;
  }

  setTarget(value: number): void {
    if (Math.abs(value - this.target) < 1e-9) {
      return;
    }
    this.from = this.value;
    this.target = value;
    this.elapsed = 0;
  }

  advance(seconds: number): void {
    if (seconds > 0 && Number.isFinite(seconds)) {
      this.elapsed = Math.min(this.settleSeconds, this.elapsed + seconds);
    }
  }

  private displacement(seconds: number): number {
    const a = this.slow;
    const b = this.fast;
    return 1 - (b * Math.exp(-a * seconds) - a * Math.exp(-b * seconds)) / (b - a);
  }
}

function clamp01(value: number): number {
  return Number.isNaN(value) ? 0 : Math.max(0, Math.min(value, 1));
}

function lerp(from: number, to: number, progress: number): number {
  return from + (to - from) * progress;
}

function centreX(rect: ShellRect): number { return rect.x + rect.width / 2; }
function centreY(rect: ShellRect): number { return rect.y + rect.height / 2; }
function finiteRect(rect: ShellRect): boolean {
  return Number.isFinite(rect.x) && Number.isFinite(rect.y)
    && Number.isFinite(rect.width) && Number.isFinite(rect.height)
    && rect.width > 0 && rect.height > 0;
}
function rectsClose(a: ShellRect, b: ShellRect): boolean {
  return Math.abs(a.x - b.x) < 0.5 && Math.abs(a.y - b.y) < 0.5
    && Math.abs(a.width - b.width) < 0.5 && Math.abs(a.height - b.height) < 0.5;
}
