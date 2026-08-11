/**
 * The FirstWave surface model recovered in
 * docs/sony-shell/firstwave-decoded-passes.md: a bicubic patch whose 4x4
 * control lattice is displaced by 3D simplex noise, with `time` as the third
 * noise axis.
 *
 * ROLE: this is the executable *reference* for the model, not shell UI code.
 * React Native draws no mesh; the surface belongs to the native renderer in
 * windows/Prosperismo/FirstWaveSurface.{h,cpp}. This module exists so the
 * recovered maths has a readable, unit-tested definition, and so the native
 * port can be cross-validated against it — the two agree to 1.33e-06, which
 * is the float/double gap. Nothing in the React tree imports it by design;
 * see windows/Prosperismo/FirstWaveSurfaceHostTest.cpp for the paired check.
 *
 * The simplex noise below is the public `webgl-noise` algorithm by Stefan
 * Gustavson and Ashima Arts (MIT licence), reimplemented in TypeScript. Its
 * constants — mod289 / permute*34 / taylorInvSqrt / the 0.6 falloff / 1/7 /
 * F3=1/3 / G3=1/6 — are the published ones, and are exactly the literals the
 * firmware stage carries. No firmware program is translated here; the
 * recovered fact is only that this algorithm drives the lattice.
 *
 * The octave count IS recovered: the firmware performs exactly ONE simplex
 * evaluation per control point, not an fBm stack — its literal multiplicities
 * match a single evaluation exactly (12 permute rounds, 15 mod289, 4 per
 * corner, and a single closing x42). Frequency, amplitude and time scale are
 * still unassigned among six non-canonical literals, so they stay explicit
 * inputs here rather than invented constants.
 */

function mod289(x: number): number {
  return x - Math.floor(x / 289) * 289;
}

function permute(x: number): number {
  return mod289((x * 34 + 1) * x);
}

function taylorInvSqrt(r: number): number {
  return 1.79284291400159 - 0.85373472095314 * r;
}

const F3 = 1 / 3;
const G3 = 1 / 6;

/**
 * Gustavson/Ashima 3D simplex noise. Returns roughly [-1, 1] and is
 * deterministic: the same (x, y, z) always yields the same value, which is
 * what makes the wave reproducible frame to frame.
 */
export function simplexNoise3(x: number, y: number, z: number): number {
  // Skew the input space to determine which simplex cell we are in.
  const s = (x + y + z) * F3;
  const i = Math.floor(x + s);
  const j = Math.floor(y + s);
  const k = Math.floor(z + s);
  const t = (i + j + k) * G3;
  const x0 = x - (i - t);
  const y0 = y - (j - t);
  const z0 = z - (k - t);

  // Corner ordering.
  let i1: number; let j1: number; let k1: number;
  let i2: number; let j2: number; let k2: number;
  if (x0 >= y0) {
    if (y0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
    else if (x0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 0; k2 = 1; }
    else { i1 = 0; j1 = 0; k1 = 1; i2 = 1; j2 = 0; k2 = 1; }
  } else if (y0 < z0) { i1 = 0; j1 = 0; k1 = 1; i2 = 0; j2 = 1; k2 = 1; }
  else if (x0 < z0) { i1 = 0; j1 = 1; k1 = 0; i2 = 0; j2 = 1; k2 = 1; }
  else { i1 = 0; j1 = 1; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }

  const x1 = x0 - i1 + G3;
  const y1 = y0 - j1 + G3;
  const z1 = z0 - k1 + G3;
  const x2 = x0 - i2 + 2 * G3;
  const y2 = y0 - j2 + 2 * G3;
  const z2 = z0 - k2 + 2 * G3;
  const x3 = x0 - 1 + 3 * G3;
  const y3 = y0 - 1 + 3 * G3;
  const z3 = z0 - 1 + 3 * G3;

  const ii = mod289(i);
  const jj = mod289(j);
  const kk = mod289(k);

  // Gradients on a 7x7x6 lattice. NS_X = 2/7 and NS_Y = 0.5/7 - 1 are the
  // `ns` vector of the published implementation; both appear verbatim in the
  // firmware stage (0x3e924925 and 0xbf6db6db), as do 49 and 1/49 below.
  const NS_X = 2 / 7;
  const NS_Y = 0.5 / 7 - 1;
  const gradient = (
    ox: number, oy: number, oz: number,
    dx: number, dy: number, dz: number,
  ): number => {
    // The permutation chain takes a distinct offset per axis; collapsing them
    // to one scalar selects the wrong gradient and widens the output range.
    const p = permute(permute(permute(kk + oz) + jj + oy) + ii + ox);
    const cell = p - 49 * Math.floor(p / 49);
    const xFloor = Math.floor(cell / 7);
    const yFloor = Math.floor(cell - 7 * xFloor);
    const gx = xFloor * NS_X + NS_Y;
    const gy = yFloor * NS_X + NS_Y;
    const h = 1 - Math.abs(gx) - Math.abs(gy);
    // sh = -step(h, 0): fold the gradient back inside the octahedron.
    const sh = h < 0 ? -1 : 0;
    const ax = gx + (Math.floor(gx) * 2 + 1) * sh;
    const ay = gy + (Math.floor(gy) * 2 + 1) * sh;
    const norm = taylorInvSqrt(ax * ax + ay * ay + h * h);
    return (ax * dx + ay * dy + h * dz) * norm;
  };

  // Corner falloff. 0.6 is the 3D radius; the 2D variant uses 0.5.
  let total = 0;
  const corners: readonly (readonly [number, number, number, number, number, number])[] = [
    [0, 0, 0, x0, y0, z0],
    [i1, j1, k1, x1, y1, z1],
    [i2, j2, k2, x2, y2, z2],
    [1, 1, 1, x3, y3, z3],
  ];
  for (const [ox, oy, oz, cx, cy, cz] of corners) {
    const m = Math.max(0.6 - (cx * cx + cy * cy + cz * cz), 0);
    if (m > 0) {
      total += m * m * m * m * gradient(ox, oy, oz, cx, cy, cz);
    }
  }
  return 42 * total;
}

/** Recovered entrance-envelope terms (fw_flow_vl opening sequence). */
export const WAVE_ENVELOPE = {
  /** e = clamp(1 - rate * t, 0, 1); reaches 0 at t = 10s. */
  rate: 0.1,
  /** amp = coefficient * e^3 + steady. */
  coefficient: 0.4,
  steady: 0.16,
  /** Constant drift added to a noise coordinate as 0.2 * time. */
  driftRate: 0.2,
  /** Final world-space scale applied to the control point. */
  worldScale: 2000,
} as const;

/**
 * The recovered amplitude envelope: starts at 0.56 and decays cubically to a
 * 0.16 steady state over ten seconds, so the surface is more agitated as HOME
 * appears and then calms.
 */
export function waveEnvelopeAmplitude(timeSeconds: number): number {
  const e = Math.min(Math.max(1 - WAVE_ENVELOPE.rate * timeSeconds, 0), 1);
  return WAVE_ENVELOPE.coefficient * e * e * e + WAVE_ENVELOPE.steady;
}

export interface WaveFieldOptions {
  /** Spatial frequency of the noise lattice. Not firmware-recovered. */
  frequency: number;
  /** Extra multiplier on top of the recovered envelope. */
  amplitude: number;
  /** How fast `time` advances along the third noise axis. */
  timeScale: number;
  /** Apply the recovered entrance envelope. */
  useEnvelope: boolean;
}

export const DEFAULT_WAVE_FIELD: WaveFieldOptions = {
  frequency: 0.85,
  amplitude: 1,
  timeScale: 0.12,
  useEnvelope: true,
};

/**
 * Displacement of one control point. `time` is both the noise's third axis
 * and a constant drift on the sampling coordinate, which is what keeps the
 * resting surface moving once the entrance envelope has decayed.
 *
 * The firmware squares the scaled noise before displacing, so the result is
 * one-sided: the surface only bulges along the positive direction, never
 * symmetrically. That is why it reads as swells rather than ripples.
 */
export function waveControlPointDisplacement(
  x: number,
  y: number,
  timeSeconds: number,
  options: WaveFieldOptions = DEFAULT_WAVE_FIELD,
): number {
  const {frequency, amplitude, timeScale, useEnvelope} = options;
  const drift = WAVE_ENVELOPE.driftRate * timeSeconds;
  const n = simplexNoise3(
    x * frequency + drift,
    y * frequency,
    timeSeconds * timeScale,
  );
  const envelope = useEnvelope ? waveEnvelopeAmplitude(timeSeconds) : 1;
  return amplitude * envelope * n * n;
}

/** Bernstein basis for a cubic Bezier, ordered b0..b3. */
export function cubicBernstein(t: number): [number, number, number, number] {
  const u = 1 - t;
  return [u * u * u, 3 * u * u * t, 3 * u * t * t, t * t * t];
}

/**
 * Evaluates a 4x4 bicubic patch at (u, v). `control` is row-major with 16
 * entries, matching the firmware's sixteen regularly strided control-point
 * reads.
 */
export function evaluateBicubicPatch(
  control: readonly number[],
  u: number,
  v: number,
): number {
  if (control.length !== 16) {
    throw new Error(`bicubic patch needs 16 control points, received ${control.length}`);
  }
  const bu = cubicBernstein(u);
  const bv = cubicBernstein(v);
  let sum = 0;
  for (let row = 0; row < 4; row += 1) {
    for (let col = 0; col < 4; col += 1) {
      sum += control[row * 4 + col] * bv[row] * bu[col];
    }
  }
  return sum;
}

/**
 * The 16 control heights for one patch at a given time — the lattice the
 * domain stage then evaluates.
 */
export function waveControlLattice(
  originX: number,
  originY: number,
  spacing: number,
  timeSeconds: number,
  options: WaveFieldOptions = DEFAULT_WAVE_FIELD,
): number[] {
  const lattice: number[] = [];
  for (let row = 0; row < 4; row += 1) {
    for (let col = 0; col < 4; col += 1) {
      lattice.push(waveControlPointDisplacement(
        originX + col * spacing,
        originY + row * spacing,
        timeSeconds,
        options,
      ));
    }
  }
  return lattice;
}
