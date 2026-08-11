import {
  BACKGROUND_BASEMAT_TYPE,
  BACKGROUND_TRANSITION_DEGREE,
  BACKGROUND_TRANSITION_FLAG,
  BACKGROUND_TRANSITION_TYPE,
  backgroundTransitionDurationMs,
  backgroundTransitionFlipsPlate,
  backgroundTransitionOrigin,
  packBackgroundTransitionType,
  unpackBackgroundTransitionType,
} from '../src/bigPicture/shellBackgroundTransition';
import {SHELL_METRICS} from '../src/bigPicture/shellMetrics';

describe('BGLayer background transition contract', () => {
  it('keeps the recovered transition type values', () => {
    expect(BACKGROUND_TRANSITION_TYPE.customImageRipple).toBe(6);
    expect(BACKGROUND_TRANSITION_TYPE.customImageRippleBack).toBe(10);
    expect(BACKGROUND_TRANSITION_TYPE.hide).toBe(1);
    expect(BACKGROUND_TRANSITION_TYPE.systemDefault).toBe(5);
    expect(BACKGROUND_TRANSITION_TYPE.invalid).toBe(-1);
    // 12.40+ addition; verified identical across 12.40, 13.00 and 13.20.
    expect(BACKGROUND_TRANSITION_TYPE.fadeToBlack).toBe(11);
  });

  it('does not flip the plate for FadeToBlack', () => {
    expect(backgroundTransitionFlipsPlate(BACKGROUND_TRANSITION_TYPE.fadeToBlack)).toBe(false);
  });

  it('packs type and degree into one word', () => {
    const packed = packBackgroundTransitionType(
      BACKGROUND_TRANSITION_TYPE.customImageRipple,
      BACKGROUND_TRANSITION_DEGREE.normal,
    );
    expect(packed).toBe(0x20006);
    expect(unpackBackgroundTransitionType(packed)).toEqual({type: 6, degree: 2});
  });

  it('derives the HOME 633.333ms transition from degree Normal', () => {
    expect(backgroundTransitionDurationMs(BACKGROUND_TRANSITION_DEGREE.crossFade)).toBe(300);
    expect(backgroundTransitionDurationMs(BACKGROUND_TRANSITION_DEGREE.normal))
      .toBeCloseTo(SHELL_METRICS.titleBackgroundTransitionMs, 4);
    expect(backgroundTransitionDurationMs(BACKGROUND_TRANSITION_DEGREE.strong)).toBe(800);
  });

  it('flips the plate id only for transitions that present a new image', () => {
    expect(backgroundTransitionFlipsPlate(BACKGROUND_TRANSITION_TYPE.customImageRipple)).toBe(true);
    expect(backgroundTransitionFlipsPlate(BACKGROUND_TRANSITION_TYPE.customImageRippleBack)).toBe(true);
    expect(backgroundTransitionFlipsPlate(BACKGROUND_TRANSITION_TYPE.launchingGame)).toBe(true);
    expect(backgroundTransitionFlipsPlate(BACKGROUND_TRANSITION_TYPE.hide)).toBe(false);
    expect(backgroundTransitionFlipsPlate(BACKGROUND_TRANSITION_TYPE.systemDefault)).toBe(false);
  });

  it('ripples out of the focused tile, not the screen centre', () => {
    // HOME's focused strand card: 168x168 at (172, 126).
    const origin = backgroundTransitionOrigin({x: 172, y: 126, width: 168, height: 168});
    expect(origin.x).toBeCloseTo(256 / 1920, 10);
    expect(origin.y).toBeCloseTo(210 / 1080, 10);
    expect(origin.x).toBeLessThan(0.5);
  });

  it('falls back to the screen centre only when nothing is focused', () => {
    expect(backgroundTransitionOrigin()).toEqual({x: 0.5, y: 0.5});
    expect(backgroundTransitionOrigin({x: Number.NaN, y: 0, width: 1, height: 1}))
      .toEqual({x: 0.5, y: 0.5});
    // 960,540 is the documented fallback point and normalizes to the centre.
    expect(backgroundTransitionOrigin({x: 960, y: 540, width: 0, height: 0}))
      .toEqual({x: 0.5, y: 0.5});
  });

  it('matches the focused HOME card that the shell hands the background owner', () => {
    // BigPictureShell passes the focused strand card's design-space bounds.
    const origin = backgroundTransitionOrigin({
      x: SHELL_METRICS.strand.left,
      y: SHELL_METRICS.strand.top,
      width: SHELL_METRICS.strand.focusedSize,
      height: SHELL_METRICS.strand.focusedSize,
    });
    expect(origin.x).toBeCloseTo(256 / 1920, 10);
    expect(origin.y).toBeCloseTo(210 / 1080, 10);
    // Well left of and above centre, which is the whole point.
    expect(origin.x).toBeLessThan(0.5);
    expect(origin.y).toBeLessThan(0.5);
  });

  it('keeps the aliased basemat and flag values', () => {
    expect(BACKGROUND_BASEMAT_TYPE.ellipseNarrow).toBe(BACKGROUND_BASEMAT_TYPE.ellipseWide);
    expect(BACKGROUND_BASEMAT_TYPE.flat).toBe(1);
    expect(BACKGROUND_TRANSITION_FLAG.basematAnimationInProgress).toBe(0x80);
    expect(BACKGROUND_TRANSITION_FLAG.cancelable).toBe(0x20);
  });
});
