import {
  cubicBernstein,
  DEFAULT_WAVE_FIELD,
  evaluateBicubicPatch,
  simplexNoise3,
  waveControlLattice,
  WAVE_ENVELOPE,
  waveControlPointDisplacement,
  waveEnvelopeAmplitude,
} from '../src/bigPicture/shellWaveField';

describe('3D simplex noise (Gustavson/Ashima constants)', () => {
  it('is deterministic', () => {
    expect(simplexNoise3(1.5, -2.25, 0.75)).toBe(simplexNoise3(1.5, -2.25, 0.75));
  });

  it('stays in a bounded range over a wide sample', () => {
    let min = Infinity;
    let max = -Infinity;
    for (let i = 0; i < 4000; i += 1) {
      const v = simplexNoise3(i * 0.137, i * 0.071 - 5, i * 0.031);
      expect(Number.isFinite(v)).toBe(true);
      min = Math.min(min, v);
      max = Math.max(max, v);
    }
    expect(min).toBeGreaterThan(-1.6);
    expect(max).toBeLessThan(1.6);
    // A constant field would be a decoding failure, not noise.
    expect(max - min).toBeGreaterThan(0.5);
  });

  it('varies along the third axis, so time animates the surface', () => {
    const a = simplexNoise3(3.1, 4.2, 0);
    const b = simplexNoise3(3.1, 4.2, 2.5);
    expect(a).not.toBeCloseTo(b, 6);
  });

  it('is continuous: nearby samples stay close', () => {
    const a = simplexNoise3(2.0, 1.0, 0.5);
    const b = simplexNoise3(2.0 + 1e-4, 1.0, 0.5);
    expect(Math.abs(a - b)).toBeLessThan(0.02);
  });
});

describe('cubic Bernstein basis', () => {
  it('forms a partition of unity', () => {
    for (const t of [0, 0.25, 0.5, 0.75, 1]) {
      const sum = cubicBernstein(t).reduce((acc, x) => acc + x, 0);
      expect(sum).toBeCloseTo(1, 12);
    }
  });

  it('interpolates the endpoints', () => {
    expect(cubicBernstein(0)).toEqual([1, 0, 0, 0]);
    expect(cubicBernstein(1)).toEqual([0, 0, 0, 1]);
  });
});

describe('bicubic patch evaluation', () => {
  const flat = new Array(16).fill(2.5);

  it('reproduces a constant lattice exactly', () => {
    expect(evaluateBicubicPatch(flat, 0.3, 0.7)).toBeCloseTo(2.5, 12);
  });

  it('interpolates the corner control points', () => {
    const control = new Array(16).fill(0);
    control[0] = 1; // row 0, col 0
    control[15] = 5; // row 3, col 3
    expect(evaluateBicubicPatch(control, 0, 0)).toBeCloseTo(1, 12);
    expect(evaluateBicubicPatch(control, 1, 1)).toBeCloseTo(5, 12);
  });

  it('stays within the control hull', () => {
    const control = Array.from({length: 16}, (_, i) => (i % 5) - 2);
    const lo = Math.min(...control);
    const hi = Math.max(...control);
    for (let u = 0; u <= 1; u += 0.25) {
      for (let v = 0; v <= 1; v += 0.25) {
        const h = evaluateBicubicPatch(control, u, v);
        expect(h).toBeGreaterThanOrEqual(lo - 1e-9);
        expect(h).toBeLessThanOrEqual(hi + 1e-9);
      }
    }
  });

  it('rejects a lattice that is not 4x4', () => {
    expect(() => evaluateBicubicPatch([1, 2, 3], 0.5, 0.5)).toThrow(/16 control points/);
  });
});

describe('wave control lattice', () => {
  it('produces the sixteen control points the domain stage reads', () => {
    expect(waveControlLattice(0, 0, 1, 0)).toHaveLength(16);
  });

  it('evolves with time', () => {
    const t0 = waveControlLattice(0, 0, 1, 0);
    const t1 = waveControlLattice(0, 0, 1, 6);
    expect(t0).not.toEqual(t1);
  });

  it('scales with amplitude and is zero when amplitude is zero', () => {
    const flat = waveControlLattice(0, 0, 1, 1, {...DEFAULT_WAVE_FIELD, amplitude: 0});
    expect(flat.every(v => v === 0)).toBe(true);
    const single = waveControlPointDisplacement(1, 1, 1, {...DEFAULT_WAVE_FIELD, amplitude: 2});
    const half = waveControlPointDisplacement(1, 1, 1, {...DEFAULT_WAVE_FIELD, amplitude: 1});
    expect(single).toBeCloseTo(half * 2, 12);
  });

  it('is one-sided: displacement is never negative', () => {
    for (let i = 0; i < 500; i += 1) {
      const v = waveControlPointDisplacement(i * 0.31, i * 0.17, i * 0.05);
      expect(v).toBeGreaterThanOrEqual(0);
    }
  });
});

describe('recovered entrance envelope', () => {
  it('starts at 0.56 and settles to 0.16 after ten seconds', () => {
    expect(waveEnvelopeAmplitude(0)).toBeCloseTo(0.56, 12);
    expect(waveEnvelopeAmplitude(10)).toBeCloseTo(0.16, 12);
    expect(waveEnvelopeAmplitude(30)).toBeCloseTo(0.16, 12);
  });

  it('decays cubically and monotonically', () => {
    // e = 1 - 0.1t, so at t=5 the envelope contribution is 0.4 * 0.5^3.
    expect(waveEnvelopeAmplitude(5)).toBeCloseTo(0.4 * 0.125 + 0.16, 12);
    let previous = Infinity;
    for (let t = 0; t <= 12; t += 0.5) {
      const v = waveEnvelopeAmplitude(t);
      expect(v).toBeLessThanOrEqual(previous + 1e-12);
      previous = v;
    }
  });

  it('keeps the surface moving after the envelope settles, via time drift', () => {
    const a = waveControlPointDisplacement(2, 3, 20);
    const b = waveControlPointDisplacement(2, 3, 26);
    expect(waveEnvelopeAmplitude(20)).toBeCloseTo(waveEnvelopeAmplitude(26), 12);
    expect(a).not.toBeCloseTo(b, 6);
  });

  it('pins the recovered constants', () => {
    expect(WAVE_ENVELOPE.rate).toBe(0.1);
    expect(WAVE_ENVELOPE.coefficient).toBe(0.4);
    expect(WAVE_ENVELOPE.steady).toBe(0.16);
    expect(WAVE_ENVELOPE.driftRate).toBe(0.2);
    expect(WAVE_ENVELOPE.worldScale).toBe(2000);
  });
});
