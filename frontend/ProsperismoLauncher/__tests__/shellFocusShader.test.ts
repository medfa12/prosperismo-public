import {
  focusAreaPassApplies,
  focusBandCoverage,
  focusColorAt,
  focusDiagonalRamp,
  focusLineTableCoordinate,
  focusLineToneCurve,
  focusNoiseOffset,
  focusNoiseUv,
  focusShimmerAcross,
  roundedBoxDistance,
  SHELL_FOCUS_COLORS,
} from '../src/bigPicture/shellFocusShader';

describe('recovered UI3 focus fields', () => {
  it('evaluates a rounded-box signed distance and perpendicular band', () => {
    expect(roundedBoxDistance(0, 0, 84, 84, 168 / 106 * 16)).toBeLessThan(0);
    expect(roundedBoxDistance(84, 0, 84, 84, 20)).toBe(0);
    expect(focusBandCoverage(0, 3)).toBe(1);
    expect(focusBandCoverage(4, 3)).toBe(0);
  });

  it('orbits the firmware noise lookup at .25 radians per second', () => {
    expect(focusNoiseOffset(0)).toEqual([0, 1]);
    expect(focusNoiseUv(0, 0, 0)).toEqual([0.5, 1]);
  });

  it('drives the area shimmer along the anti-diagonal', () => {
    expect(focusDiagonalRamp(-1, 1)).toBe(1);
    expect(focusDiagonalRamp(1, -1)).toBe(0);
    expect(focusShimmerAcross(4, 0)).not.toBe(focusShimmerAcross(4, 1));
  });

  it('uses noise alone for line color and the exact seven-stop palette', () => {
    expect(SHELL_FOCUS_COLORS).toHaveLength(7);
    expect(focusLineTableCoordinate(0.25)).toBe(0.125);
    expect(focusLineToneCurve(0)).toBe(0);
    expect(focusLineToneCurve(0.2)).toBeCloseTo(0.25, 10);
    expect(focusColorAt(0)).toEqual(SHELL_FOCUS_COLORS[0]);
    expect(focusColorAt(1)).toEqual(SHELL_FOCUS_COLORS[6]);
  });

  it('removes the area pass for a target covering forty percent of the screen', () => {
    expect(focusAreaPassApplies({x: 0, y: 0, width: 168, height: 168}, {width: 1920, height: 1080})).toBe(true);
    expect(focusAreaPassApplies({x: 0, y: 0, width: 1920, height: 1080}, {width: 1920, height: 1080})).toBe(false);
  });
});
