// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.Libs.Presentation;

namespace Prosperismo.GUI.SystemAssets.Shell;

/// <summary>
/// The PS5 background particle field, evaluated from the recovered simulation
///
/// <c>Particle0.gnf</c>/<c>Particle1.gnf</c> light textures and return null
/// without them, so on a machine with no such dump the particle layer can
/// never light up. This source has no asset dependency: the small particles
/// fetch), so they can be reproduced exactly from arithmetic.</para>
///
/// <para>Recovered contract, from <c>particle_c</c> in the 12.40 shell eboot —
/// <list type="bullet">
/// <item>motion is <b>three-octave curl noise</b> over Ashima/Gustavson
/// <b>4D</b> simplex noise, lacunarity exactly 2, octave count hardcoded to 3;</item>
/// <item>the 4D corner falloff radius is <b>0.5</b>, not the 0.6 of the
/// published 3D variant, and the output scale is <b>49.0</b>;</item>
/// <item>the fBm gain is per-particle: <c>0.5 + 0.5 * (slot / capacity)</c>;</item>
/// <item>integration is a single explicit Euler step, <c>pos += vel * dt</c>;</item>
/// <item>there is <b>no gravity, no drag, no bounds wrap and no bounce</b> —
/// recycling is purely by lifetime;</item>
/// <item>the RNG is Park-Miller (16807, modulus 2^31-1).</item>
/// </list>
///
/// <para>What is <b>not</b> recovered, and is therefore a presentation choice
/// camera projection, particle colour and size distribution, and the light
/// shaft. Those were graded against the reference capture
/// individually below.</para>
/// </summary>
internal sealed class Ps5ProceduralParticleFrameSource : IPs5NativeParticleFrameSource
{
    // ---- recovered (particle_c) ----
    private const int Octaves = 3;              // hardcoded s_cmp_lt_i32 sN, 3
    private const float Lacunarity = 2.0f;      // v_ldexp_f32 1.0, i
    private const float NoiseFalloff4D = 0.5f;  // NOT the published 0.6
    private const float NoiseOutputScale = 49.0f;

    // Potential-field decorrelation offsets, measured from the three sites.
    private static readonly (float X, float Y, float Z)[] PotentialOffsets =
    {
        (0f, 0f, 0f),
        (123.4f, 129845.6f, -1239.1f),
        (-9519f, 9051f, -123f),
    };

    // ---- presentation (graded, not recovered) ----
    private const int Capacity = 900;
    private const float FieldWidth = 16.0f;
    private const float FieldHeight = 9.0f;
    private const float FieldDepth = 6.0f;
    private const float NoiseFrequency = 0.085f;
    private const float CurlStrength = 0.55f;
    private const float VelocityApproach = 2.4f;
    private const float TimeStep = 1f / 30f;

    private readonly Particle[] _particles = new Particle[Capacity];
    private bool _seeded;
    private double _simulatedSeconds;

    private struct Particle
    {
        public float X, Y, Z;
        public float Vx, Vy, Vz;
        public float Life, MaxLife;
        public float Radius;       // world units
        public float Gain;         // per-particle fBm gain, recovered form
        public float Warmth;       // 0 = white, 1 = deep gold
        public uint Seed;
        public bool Large;
    }

    public ValueTask<Ps5NativeParticleFrame?> RenderAsync(
        Ps5NativeParticleFrameRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Width <= 0 || request.Height <= 0)
        {
            return ValueTask.FromResult<Ps5NativeParticleFrame?>(null);
        }

        EnsureSeeded();
        AdvanceTo(request.Elapsed.TotalSeconds, cancellationToken);
        var rgba = Rasterize(request.Width, request.Height);
        return ValueTask.FromResult<Ps5NativeParticleFrame?>(
            new Ps5NativeParticleFrame(request.Width, request.Height, rgba));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---------------- simulation ----------------

    private void EnsureSeeded()
    {
        if (_seeded)
        {
            return;
        }

        uint seed = 1u;
        for (var i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            p.Seed = seed = NextRandom(seed);
            ResetParticle(ref p, i, spawnAnywhere: true);
        }

        _seeded = true;
    }

    private static uint NextRandom(uint seed)
    {
        var next = (ulong)seed * 16807UL;
        next = (next & 0x7FFFFFFFUL) + (next >> 31);
        if (next >= 0x7FFFFFFFUL)
        {
            next -= 0x7FFFFFFFUL;
        }

        return (uint)(next == 0 ? 1 : next);
    }

    private static float Unit(ref uint seed)
    {
        seed = NextRandom(seed);
        return (seed & 0xFFFFFF) / (float)0x1000000;
    }

    private void ResetParticle(ref Particle p, int index, bool spawnAnywhere)
    {
        var seed = p.Seed;
        p.X = (Unit(ref seed) - 0.5f) * FieldWidth;
        // Denser toward the lower half, matching the reference capture.
        var v = Unit(ref seed);
        p.Y = (MathF.Sqrt(v) - 0.62f) * FieldHeight;
        p.Z = (Unit(ref seed) - 0.5f) * FieldDepth;
        p.Vx = p.Vy = p.Vz = 0f;
        p.MaxLife = 6f + Unit(ref seed) * 12f;
        p.Life = spawnAnywhere ? Unit(ref seed) * p.MaxLife : 0f;
        // Recovered form: gain = 0.5 + 0.5 * (slot / capacity).
        p.Gain = 0.5f + 0.5f * (index / (float)Capacity);
        var lottery = Unit(ref seed);
        p.Large = lottery > 0.86f;
        p.Radius = p.Large
            ? 0.055f + Unit(ref seed) * 0.115f
            : 0.006f + Unit(ref seed) * 0.020f;
        p.Warmth = 0.25f + Unit(ref seed) * 0.75f;
        p.Seed = seed;
    }

    private void AdvanceTo(double targetSeconds, CancellationToken cancellationToken)
    {
        // Deterministic fixed-step catch-up, capped so a long stall cannot
        // spin the simulation for an unbounded time.
        var steps = 0;
        while (_simulatedSeconds + TimeStep <= targetSeconds && steps < 600)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Step((float)_simulatedSeconds);
            _simulatedSeconds += TimeStep;
            steps++;
        }

        if (steps == 0 && _simulatedSeconds == 0d && targetSeconds > 0d)
        {
            Step(0f);
            _simulatedSeconds = TimeStep;
        }
    }

    private void Step(float time)
    {
        for (var i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];

            p.Life += TimeStep;
            if (p.Life >= p.MaxLife)
            {
                ResetParticle(ref p, i, spawnAnywhere: false);
                continue;
            }

            Curl(p.X, p.Y, p.Z, time, p.Gain, out var cx, out var cy, out var cz);

            var tx = cx * CurlStrength;
            var ty = cy * CurlStrength;
            var tz = cz * CurlStrength;

            // Clamped approach to the target velocity, then one explicit Euler
            var dx = tx - p.Vx;
            var dy = ty - p.Vy;
            var dz = tz - p.Vz;
            var lenSq = dx * dx + dy * dy + dz * dz;
            var maxDelta = TimeStep * VelocityApproach;
            if (lenSq > maxDelta * maxDelta && lenSq > 0f)
            {
                var scale = maxDelta / MathF.Sqrt(lenSq);
                p.Vx += dx * scale;
                p.Vy += dy * scale;
                p.Vz += dz * scale;
            }
            else
            {
                p.Vx = tx;
                p.Vy = ty;
                p.Vz = tz;
            }

            p.X += p.Vx * TimeStep;
            p.Y += p.Vy * TimeStep;
            p.Z += p.Vz * TimeStep;
        }
    }

    /// <summary>
    /// Curl of a three-component vector potential, each component a 3-octave
    /// of three evaluations compute, with the six accumulators subtracted in
    /// three pairs.
    /// </summary>
    private static void Curl(
        float x, float y, float z, float time, float gain,
        out float cx, out float cy, out float cz)
    {
        const float H = 0.35f;
        var p0y = Potential(0, x, y + H, z, time, gain);
        var p0z = Potential(0, x, y - H, z, time, gain);
        var p1y = Potential(1, x, y, z + H, time, gain);
        var p1z = Potential(1, x, y, z - H, time, gain);
        var p2y = Potential(2, x + H, y, z, time, gain);
        var p2z = Potential(2, x - H, y, z, time, gain);

        cx = (p0y - p0z) - (p1y - p1z);
        cy = (p1y - p1z) - (p2y - p2z);
        cz = (p2y - p2z) - (p0y - p0z);
    }

    private static float Potential(int site, float x, float y, float z, float time, float gain)
    {
        var (ox, oy, oz) = PotentialOffsets[site];
        var sum = 0f;
        var amplitude = 1f;
        var frequency = NoiseFrequency;
        for (var octave = 0; octave < Octaves; octave++)
        {
            sum += amplitude * Simplex4D(
                (x + ox) * frequency,
                (y + oy) * frequency,
                (z + oz) * frequency,
                time * 0.08f);
            amplitude *= gain;
            frequency *= Lacunarity;
        }

        return sum;
    }

    // ---------------- Ashima/Gustavson 4D simplex noise ----------------
    // constants, with one deviation recorded above: the corner falloff radius
    // is 0.5 rather than 0.6.

    private const float F4 = 0.309016994374947451f;
    private const float G4 = 0.138196601125011f;

    private static float Mod289(float x) => x - MathF.Floor(x / 289f) * 289f;

    private static float Permute(float x) => Mod289((x * 34f + 1f) * x);

    private static float TaylorInvSqrt(float r) => 1.79284291400159f - 0.85373472095314f * r;

    private static void Grad4(float j, out float gx, out float gy, out float gz, out float gw)
    {
        var x = MathF.Floor(j / 7f) / 7f * 2f - 1f;
        var y = MathF.Floor(j % 7f) / 7f * 2f - 1f;
        var w = 1.5f - (MathF.Abs(x) + MathF.Abs(y));
        var sx = x >= 0f ? 1f : -1f;
        var sy = y >= 0f ? 1f : -1f;
        if (w < 0f)
        {
            x -= sx * (1f - MathF.Abs(y));
            y -= sy * (1f - MathF.Abs(x));
        }

        gx = x;
        gy = y;
        gz = w < 0f ? -1f : 1f;
        gw = w;
        var norm = TaylorInvSqrt(gx * gx + gy * gy + gz * gz + gw * gw);
        gx *= norm; gy *= norm; gz *= norm; gw *= norm;
    }

    private static float Simplex4D(float x, float y, float z, float w)
    {
        var s = (x + y + z + w) * F4;
        var i = MathF.Floor(x + s);
        var j = MathF.Floor(y + s);
        var k = MathF.Floor(z + s);
        var l = MathF.Floor(w + s);
        var t = (i + j + k + l) * G4;
        var x0 = x - (i - t);
        var y0 = y - (j - t);
        var z0 = z - (k - t);
        var w0 = w - (l - t);

        // Rank the four coordinates to order the simplex corners.
        int rx = 0, ry = 0, rz = 0, rw = 0;
        if (x0 > y0) rx++; else ry++;
        if (x0 > z0) rx++; else rz++;
        if (x0 > w0) rx++; else rw++;
        if (y0 > z0) ry++; else rz++;
        if (y0 > w0) ry++; else rw++;
        if (z0 > w0) rz++; else rw++;

        float I1x = rx >= 3 ? 1f : 0f, I1y = ry >= 3 ? 1f : 0f, I1z = rz >= 3 ? 1f : 0f, I1w = rw >= 3 ? 1f : 0f;
        float I2x = rx >= 2 ? 1f : 0f, I2y = ry >= 2 ? 1f : 0f, I2z = rz >= 2 ? 1f : 0f, I2w = rw >= 2 ? 1f : 0f;
        float I3x = rx >= 1 ? 1f : 0f, I3y = ry >= 1 ? 1f : 0f, I3z = rz >= 1 ? 1f : 0f, I3w = rw >= 1 ? 1f : 0f;

        var x1 = x0 - I1x + G4; var y1 = y0 - I1y + G4; var z1 = z0 - I1z + G4; var w1 = w0 - I1w + G4;
        var x2 = x0 - I2x + 2f * G4; var y2 = y0 - I2y + 2f * G4; var z2 = z0 - I2z + 2f * G4; var w2 = w0 - I2w + 2f * G4;
        var x3 = x0 - I3x + 3f * G4; var y3 = y0 - I3y + 3f * G4; var z3 = z0 - I3z + 3f * G4; var w3 = w0 - I3w + 3f * G4;
        var x4 = x0 - 1f + 4f * G4; var y4 = y0 - 1f + 4f * G4; var z4 = z0 - 1f + 4f * G4; var w4 = w0 - 1f + 4f * G4;

        var ii = Mod289(i); var jj = Mod289(j); var kk = Mod289(k); var ll = Mod289(l);

        float Corner(float ox, float oy, float oz, float ow, float dx, float dy, float dz, float dw)
        {
            var m = NoiseFalloff4D - (dx * dx + dy * dy + dz * dz + dw * dw);
            if (m <= 0f)
            {
                return 0f;
            }

            var p = Permute(Permute(Permute(Permute(ll + ow) + kk + oz) + jj + oy) + ii + ox);
            var jIndex = p - 49f * MathF.Floor(p / 49f);
            Grad4(jIndex, out var gx, out var gy, out var gz, out var gw);
            m *= m;
            return m * m * (gx * dx + gy * dy + gz * dz + gw * dw);
        }

        var n = Corner(0, 0, 0, 0, x0, y0, z0, w0)
              + Corner(I1x, I1y, I1z, I1w, x1, y1, z1, w1)
              + Corner(I2x, I2y, I2z, I2w, x2, y2, z2, w2)
              + Corner(I3x, I3y, I3z, I3w, x3, y3, z3, w3)
              + Corner(1, 1, 1, 1, x4, y4, z4, w4);

        return NoiseOutputScale * n;
    }

    // ---------------- rasterisation ----------------

    private byte[] Rasterize(int width, int height)
    {
        var rgba = new byte[(long)width * height * 4];
        var accum = new float[width * height * 3];

        var halfW = width * 0.5f;
        var halfH = height * 0.5f;
        var scale = width / FieldWidth;

        foreach (ref var p in _particles.AsSpan())
        {
            // Lifetime fade: in over the first 12%, out over the last 25%.
            var age = p.MaxLife <= 0f ? 0f : p.Life / p.MaxLife;
            var fade = MathF.Min(age / 0.12f, MathF.Min(1f, (1f - age) / 0.25f));
            if (fade <= 0f)
            {
                continue;
            }

            // Weak perspective: depth drives both size and dimming, which is
            // what produces the reference's mix of sharp points and soft discs.
            var depth = 1f / (1f + (p.Z + FieldDepth * 0.5f) * 0.14f);
            var sx = halfW + p.X * scale * depth;
            var sy = halfH - p.Y * scale * depth;
            var radius = MathF.Max(0.6f, p.Radius * scale * depth);
            var intensity = fade * depth * (p.Large ? 0.5f : 0.95f);

            // Warm gold through to near-white, matching the graded reference.
            var r = 1.00f;
            var g = 0.86f - 0.18f * p.Warmth;
            var b = 0.62f - 0.42f * p.Warmth;

            Splat(accum, width, height, sx, sy, radius, intensity, r, g, b, p.Large);
        }

        for (var y = 0; y < height; y++)
        {
            var v = y / (float)height;
            for (var x = 0; x < width; x++)
            {
                var idx = (y * width + x) * 3;
                var shaft = LightShaft(x / (float)width, v);
                var rr = accum[idx] + shaft * 0.75f;
                var gg = accum[idx + 1] + shaft * 0.72f;
                var bb = accum[idx + 2] + shaft * 0.66f;

                var o = (y * width + x) * 4;
                rgba[o] = ToByte(rr);
                rgba[o + 1] = ToByte(gg);
                rgba[o + 2] = ToByte(bb);
                // Additive layer: alpha carries luminance so the compositor can
                // blend it over the plate without a second pass.
                rgba[o + 3] = ToByte(MathF.Max(rr, MathF.Max(gg, bb)));
            }
        }

        return rgba;
    }

    /// <summary>
    /// The volumetric shaft entering from the upper left. Graded from the
    /// geometry has not been recovered.
    /// </summary>
    private static float LightShaft(float u, float v)
    {
        const float OriginU = 0.14f;
        const float Spread = 0.30f;
        var axis = OriginU + v * 0.16f;
        var d = MathF.Abs(u - axis) / (Spread * (0.35f + v));
        var across = MathF.Exp(-d * d * 2.4f);
        var along = MathF.Exp(-v * 1.55f);
        return across * along * 0.30f;
    }

    private static void Splat(
        float[] accum, int width, int height,
        float cx, float cy, float radius, float intensity,
        float r, float g, float b, bool large)
    {
        var x0 = Math.Max(0, (int)MathF.Floor(cx - radius));
        var x1 = Math.Min(width - 1, (int)MathF.Ceiling(cx + radius));
        var y0 = Math.Max(0, (int)MathF.Floor(cy - radius));
        var y1 = Math.Min(height - 1, (int)MathF.Ceiling(cy + radius));
        if (x1 < x0 || y1 < y0)
        {
            return;
        }

        var inv = 1f / radius;
        for (var y = y0; y <= y1; y++)
        {
            var dy = (y - cy) * inv;
            for (var x = x0; x <= x1; x++)
            {
                var dx = (x - cx) * inv;
                var d2 = dx * dx + dy * dy;
                if (d2 >= 1f)
                {
                    continue;
                }

                // Small particles: tight power falloff, as particle_p computes.
                // Large particles: near-flat top with a soft edge - an out-of-
                // focus disc rather than a point.
                float a;
                if (large)
                {
                    var edge = 1f - d2;
                    a = MathF.Min(1f, edge * 3.2f);
                    a *= a;
                }
                else
                {
                    var f = 1f - MathF.Sqrt(d2);
                    a = f * f * f;
                }

                var w = a * intensity;
                var idx = (y * width + x) * 3;
                accum[idx] += r * w;
                accum[idx + 1] += g * w;
                accum[idx + 2] += b * w;
            }
        }
    }

    private static byte ToByte(float linear)
    {
        var v = linear <= 0f ? 0f : linear >= 1f ? 1f : linear;
        return (byte)(v * 255f + 0.5f);
    }
}
