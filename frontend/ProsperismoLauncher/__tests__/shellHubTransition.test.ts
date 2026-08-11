import {
  cubicBezierEase,
  HUB_TRANSITION,
  HubAppearAnimation,
  hubSelectedTileRect,
  hubTitlePose,
  ShellHubReadiness,
} from '../src/bigPicture/shellHubTransition';
import {INITIAL_SHELL_STATE, reduceShellState} from '../src/bigPicture/shellState';

describe('recovered hub transition (NPXS40002 m130/m571/m507)', () => {
  it('derives the -166 home lift from SYSTEM_HEIGHT + VERTICAL_HEIGHT_CHANGE', () => {
    expect(HUB_TRANSITION.homeLift).toBe(-(126 + HUB_TRANSITION.verticalHeightChange));
    expect(HUB_TRANSITION.homeLift).toBe(-166);
  });

  it('keeps the m571 pre-scale drivers', () => {
    expect(HUB_TRANSITION.selectedTilePreScale.translateX).toBe(-106);
    expect(HUB_TRANSITION.selectedTilePreScale.translateY).toBeCloseTo(27.761904761904763, 10);
    expect(HUB_TRANSITION.minimizedScale).toBeCloseTo(80 / 168, 10);
  });

  it('hands the selected tile off to the 80x80 badge at (48, 48)', () => {
    expect(hubSelectedTileRect(0)).toEqual({x: 172, y: 126, width: 168, height: 168});
    expect(hubSelectedTileRect(1)).toEqual({x: 48, y: 48, width: 80, height: 80});
    const mid = hubSelectedTileRect(0.5);
    expect(mid.x).toBeCloseTo(110, 10);
    expect(mid.y).toBeCloseTo(87, 10);
    expect(mid.width).toBeCloseTo(124, 10);
  });

  it('parks the title beside the badge in the header pose', () => {
    expect(hubTitlePose(0)).toEqual({x: 356, y: 232});
    expect(hubTitlePose(1)).toEqual({x: 172, y: 57});
  });

  it('keeps every experience unready until focusReady fires once', () => {
    const readiness = new ShellHubReadiness();
    expect(readiness.isReady('cid:local:PPSA01234')).toBe(false);
    expect(readiness.isReady(undefined)).toBe(false);
    readiness.focusReady('cid:local:PPSA01234');
    expect(readiness.isReady('cid:local:PPSA01234')).toBe(true);
    expect(readiness.isReady('cid:local:PPSA09999')).toBe(false);
  });

  it('evaluates the m507 cubic bezier as an increasing ease', () => {
    expect(cubicBezierEase(0.25, 0.1, 0.25, 0.8, 0)).toBe(0);
    expect(cubicBezierEase(0.25, 0.1, 0.25, 0.8, 1)).toBe(1);
    const quarter = cubicBezierEase(0.25, 0.1, 0.25, 0.8, 0.25);
    const half = cubicBezierEase(0.25, 0.1, 0.25, 0.8, 0.5);
    const threeQuarter = cubicBezierEase(0.25, 0.1, 0.25, 0.8, 0.75);
    expect(quarter).toBeGreaterThan(0);
    expect(half).toBeGreaterThan(quarter);
    expect(threeQuarter).toBeGreaterThan(half);
    expect(threeQuarter).toBeLessThan(1);
  });

  it('runs the hub-appears one-shot exactly as recovered', () => {
    const appear = new HubAppearAnimation();
    expect(appear.progress).toBe(0);
    expect(appear.translateY).toBe(850);
    expect(appear.opacity).toBe(1);

    appear.show();
    appear.advance(10);
    expect(appear.opacity).toBe(0);
    expect(appear.progress).toBe(0.95);
    expect(appear.translateY).toBeCloseTo(42.5, 10);

    appear.advance(HubAppearAnimation.preRollMs + HubAppearAnimation.durationMs);
    expect(appear.running).toBe(false);
    expect(appear.progress).toBe(1);
    expect(appear.translateY).toBe(0);
    expect(appear.opacity).toBe(1);

    appear.hide();
    expect(appear.progress).toBe(0);
    expect(appear.translateY).toBe(850);
    expect(appear.opacity).toBe(1);
  });
});

describe('vertical axis gating (m503 focusReady)', () => {
  it('swallows Down while the hub is unready', () => {
    const state = reduceShellState(INITIAL_SHELL_STATE, {type: 'descend-hub', hubReady: false});
    expect(state).toBe(INITIAL_SHELL_STATE);
  });

  it('descends only from the strand and ascends back home', () => {
    const descended = reduceShellState(INITIAL_SHELL_STATE, {type: 'descend-hub', hubReady: true});
    expect(descended.verticalPosition).toBe('hub');
    const doubled = reduceShellState(descended, {type: 'descend-hub', hubReady: true});
    expect(doubled).toBe(descended);
    const ascended = reduceShellState(descended, {type: 'ascend-home'});
    expect(ascended.verticalPosition).toBe('home');
  });

  it('does not descend from the top band even when ready', () => {
    const spaces = reduceShellState(INITIAL_SHELL_STATE, {type: 'focus', region: 'spaces'});
    const state = reduceShellState(spaces, {type: 'descend-hub', hubReady: true});
    expect(state.verticalPosition).toBe('home');
  });

  it('returning home resets the vertical position', () => {
    const descended = reduceShellState(INITIAL_SHELL_STATE, {type: 'descend-hub', hubReady: true});
    const reset = reduceShellState(descended, {type: 'home'});
    expect(reset.verticalPosition).toBe('home');
  });
});
