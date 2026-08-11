import {createHomeFocusGraph, HOME_FOCUS_REGIONS} from '../src/bigPicture/shellFocusGraph';
import {
  focusAreaOpacityScale,
  focusLineBody,
  focusListItemRect,
  focusInOutCurve,
  focusMomentumFor,
  focusMovingCurve,
  focusShimmer,
  focusWarpCurve,
  HomeGlanceState,
  HOME_GEOMETRY,
  HomeStartupChoreography,
  homeTileLeft,
  homeTileMatOpacity,
  homeTileRadius,
  ShellFocusTimeline,
  ShellSpring,
  systemIconFocusBackgroundOpacity,
  systemIconFocusedChannel,
  systemIconFocusProgress,
  HOME_SPRINGS,
} from '../src/bigPicture/shellHomeMotion';

describe('recovered HOME geometry and motion', () => {
  it('keeps the exact 106/168 strand geometry and two different gaps', () => {
    expect(homeTileLeft(2, 2)).toBe(172);
    expect(homeTileLeft(1, 2)).toBe(50);
    expect(homeTileLeft(3, 2)).toBe(356);
    expect(homeTileRadius(168)).toBeCloseTo(168 / 106 * 16, 12);
    expect(HOME_GEOMETRY.focusTrap).toEqual({left: 204, top: 157, size: 106, radius: 16});
  });

  it('applies only the recovered overflow tail mat', () => {
    expect(homeTileMatOpacity(7, 0)).toBe(0);
    expect(homeTileMatOpacity(8, 0)).toBe(0.05);
    expect(homeTileMatOpacity(9, 0)).toBe(0.2);
    expect(homeTileMatOpacity(10, 0)).toBe(0.4);
  });

  it('runs the named focus graph with one owner and last-focused restoration', () => {
    const graph = createHomeFocusGraph(5);
    graph.remember(HOME_FOCUS_REGIONS.strand, 3);
    expect(graph.tryMove('up')).toEqual({region: HOME_FOCUS_REGIONS.spaces, index: 0});
    expect(graph.tryMove('left')).toBeUndefined();
    expect(graph.tryMove('right')).toEqual({region: HOME_FOCUS_REGIONS.system, index: 0});
    expect(graph.tryMove('down')).toEqual({region: HOME_FOCUS_REGIONS.strand, index: 3});
  });

  it('preserves the firmware focus curves without normalizing their endpoints', () => {
    expect(focusInOutCurve(1)).toBeCloseTo(1023 / 1024, 12);
    expect(focusMovingCurve(1)).toBe(0.96875);
    expect(focusWarpCurve(0, 0.5)).toBe(0.5);
    expect(focusMomentumFor(100)).toBe(0.5);
    expect(focusMomentumFor(1000)).toBe(0.9);
  });

  it('hides the travelling line until its geometry is nearly settled', () => {
    const focus = new ShellFocusTimeline();
    focus.showAt({x: 172, y: 126, width: 168, height: 168}, 168 / 106 * 16);
    focus.advance(0.5);
    expect(focus.snapshot().state).toBe('shown');
    focus.retarget({x: 1364, y: 35, width: 56, height: 56}, 28);
    expect(focus.snapshot().lineOpacity).toBe(0);
    expect(focus.snapshot().rect.x).toBeGreaterThan(172);
    focus.advance(0.3);
    expect(focus.snapshot().lineOpacity).toBe(1);
  });

  it('uses the recovered area size gate and five-second shimmer', () => {
    expect(focusAreaOpacityScale({x: 0, y: 0, width: 168, height: 168}, {width: 1920, height: 1080})).toBeGreaterThan(0.5);
    expect(focusAreaOpacityScale({x: 0, y: 0, width: 1920, height: 1080}, {width: 1920, height: 1080})).toBe(0);
    expect(focusShimmer(0)[0]).toBe(-1);
    expect(focusShimmer(4)[0]).toBe(1);
  });

  it('carries strand velocity through retargets and settles without overshoot', () => {
    const spring = new ShellSpring();
    spring.snapTo(0);
    spring.springTo(168, HOME_SPRINGS.strand);
    spring.advance(0.016);
    expect(spring.value).toBeGreaterThan(0);
    spring.springTo(114, HOME_SPRINGS.strand);
    for (let index = 0; index < 200; index += 1) {
      spring.advance(0.016);
    }
    expect(spring.value).toBe(114);
  });

  it('keeps modal glance icon size while removing its label', () => {
    const glance = new HomeGlanceState();
    glance.setGlanced(true);
    glance.advance(10);
    expect(glance.iconScale).toBeCloseTo(1, 5);
    expect(glance.labelOpacity).toBeCloseTo(1, 5);
    glance.setModalVisible(true);
    glance.advance(10);
    expect(glance.iconScale).toBeCloseTo(1, 5);
    expect(glance.labelOpacity).toBeCloseTo(0, 5);
  });

  it('drives system-icon fill and inversion from the same recovered glance progress', () => {
    expect(systemIconFocusProgress(48 / 56)).toBe(0);
    expect(systemIconFocusBackgroundOpacity(48 / 56)).toBe(0);
    expect(systemIconFocusedChannel(48 / 56)).toBe(255);
    expect(systemIconFocusProgress(52 / 56)).toBeCloseTo(0.5, 12);
    expect(systemIconFocusBackgroundOpacity(52 / 56)).toBeCloseTo(0.27, 12);
    expect(systemIconFocusedChannel(52 / 56)).toBe(148);
    expect(systemIconFocusBackgroundOpacity(1)).toBe(1);
    expect(systemIconFocusedChannel(1)).toBe(41);
  });

  it('inflates only the focus margin and applies the exact ListItem crop', () => {
    expect(focusLineBody({x: 100, y: 80, width: 56, height: 56}, 28, 1)).toEqual({
      rect: {x: 94, y: 74, width: 68, height: 68},
      radius: 34,
    });
    expect(focusLineBody({x: 100, y: 80, width: 56, height: 56}, 28, 1.2)).toEqual({
      rect: {x: 92.8, y: 72.8, width: 70.4, height: 70.4},
      radius: 35.2,
    });
    expect(focusListItemRect({x: 0, y: 0, width: 1312, height: 102})).toEqual({
      x: 0, y: 3, width: 1312, height: 94,
    });
  });

  it('releases startup layers on the exact recovered schedule', () => {
    const startup = new HomeStartupChoreography();
    startup.begin(3);
    expect(startup.switcherTranslateX).toBe(1920);
    startup.advance(1049);
    expect(startup.systemAlpha).toBe(0);
    startup.advance(1);
    startup.advance(64);
    expect(startup.systemAlpha).toBeGreaterThan(0);
    startup.settle();
    expect(startup.switcherTranslateX).toBe(0);
    expect(startup.hubAlpha).toBe(1);
  });
});
