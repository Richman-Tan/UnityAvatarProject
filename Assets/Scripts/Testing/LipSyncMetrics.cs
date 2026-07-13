using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pure metric functions over a recorded weight-curve set. Encodes the lip-sync
/// acceptance criteria so every iteration is measured the same way:
///
///   bilabial     V_Explosive ≥ 0.90 within ±60ms AND open shapes suppressed
///   labiodental  V_Dental_Lip ≥ 0.80 within ±60ms
///   tongue       mapped tongue shape ≥ 0.30 within ±60ms
///   peak         primary shape of the viseme ≥ 0.35 within ±80ms
///   silence      all lip shapes < 0.10 at the check time
///   end          all lip shapes < 0.05 within 250ms after segment end
///   jitter       RMS of the 60Hz-resampled second difference (lower = smoother)
/// </summary>
public static class LipSyncMetrics
{
    const float ClosureWindow = 0.06f;
    const float PeakWindow    = 0.08f;

    /// <summary>Primary readout shape per viseme for peak/tongue checks.</summary>
    static readonly Dictionary<string, string> PrimaryShape = new()
    {
        { "aa", "V_Open" }, { "oh", "V_Open" }, { "ih", "V_Wide" },
        { "ee", "V_Wide" }, { "ou", "V_Tight_O" },
        { "v_th", "V_Tongue_Out" }, { "v_dd", "V_Tongue_up" },
        { "v_nn", "V_Tongue_up" }, { "v_kk", "V_Tongue_Raise" },
        { "v_rr", "V_Tight_O" },
    };

    // Shapes that indicate an open mouth — must be suppressed during closures
    // and near-zero during silence.
    static readonly string[] OpenShapes = { "V_Open", "V_Lip_Open" };
    static readonly string[] LipShapes =
    {
        "V_Lip_Open", "V_Open", "V_Wide", "V_Tight", "V_Tight_O",
        "V_Explosive", "V_Dental_Lip", "V_Affricate", "jaw_drive",
    };

    public static LipSyncResult Compute(LipSyncFixture fixture, List<BlendshapeRecorder.Sample> samples)
    {
        var result = new LipSyncResult { fixture = fixture.name, sampleCount = samples.Count };
        if (samples.Count < 3) return result;

        foreach (var check in fixture.checks)
        {
            var cr = new CheckResult { time = check.time, type = check.type, label = check.label };
            switch (check.type)
            {
                case "bilabial":
                {
                    cr.value = WindowMax(samples, "V_Explosive", check.time, ClosureWindow);
                    float open = WindowMinOfSum(samples, OpenShapes, check.time, ClosureWindow);
                    float jaw  = WindowMin(samples, "jaw_drive", check.time, ClosureWindow);
                    cr.secondary = open;
                    cr.passed = cr.value >= 0.90f && open <= 0.15f && jaw <= 0.20f;
                    cr.detail = $"V_Explosive={cr.value:F2} openMin={open:F2} jawMin={jaw:F2}";
                    break;
                }
                case "labiodental":
                    cr.value  = WindowMax(samples, "V_Dental_Lip", check.time, ClosureWindow);
                    cr.passed = cr.value >= 0.80f;
                    cr.detail = $"V_Dental_Lip={cr.value:F2}";
                    break;

                case "tongue":
                {
                    string shape = PrimaryShape.TryGetValue(check.viseme, out var s) ? s : "V_Tongue_up";
                    cr.value  = WindowMax(samples, shape, check.time, ClosureWindow);
                    cr.passed = cr.value >= 0.30f;
                    cr.detail = $"{shape}={cr.value:F2}";
                    break;
                }
                case "peak":
                {
                    string shape = PrimaryShape.TryGetValue(check.viseme, out var s) ? s : "V_Open";
                    cr.value  = WindowMax(samples, shape, check.time, PeakWindow);
                    cr.passed = cr.value >= 0.35f;
                    cr.secondary = AnticipationLead(samples, shape, check.time, cr.value);
                    cr.detail = $"{shape}={cr.value:F2} lead={cr.secondary * 1000f:F0}ms";
                    break;
                }
                case "silence":
                    cr.value  = MaxLipAt(samples, check.time);
                    cr.passed = cr.value < 0.10f;
                    cr.detail = $"maxLip={cr.value:F2}";
                    break;

                case "end":
                {
                    cr.value  = DecayTimeAfter(samples, fixture.duration - 0.25f, 0.05f);
                    cr.passed = cr.value >= 0f && cr.value <= 0.25f;
                    cr.detail = cr.value < 0f ? "never decayed" : $"decay={cr.value * 1000f:F0}ms";
                    break;
                }
            }
            result.checks.Add(cr);
            if (cr.passed) result.passedChecks++;
        }

        result.totalChecks = result.checks.Count;
        result.jitterRms   = JitterRms(samples);
        result.passed      = result.passedChecks == result.totalChecks;
        return result;
    }

    // ── Curve helpers ─────────────────────────────────────────────────────────

    static float ValueAt(List<BlendshapeRecorder.Sample> samples, int shapeIdx, float t)
    {
        if (shapeIdx < 0) return 0f;
        for (int i = 0; i < samples.Count - 1; i++)
        {
            if (samples[i + 1].t < t) continue;
            float span  = samples[i + 1].t - samples[i].t;
            float alpha = span > 0f ? (t - samples[i].t) / span : 0f;
            return Mathf.Lerp(samples[i].w[shapeIdx], samples[i + 1].w[shapeIdx], Mathf.Clamp01(alpha));
        }
        return samples.Count > 0 ? samples[^1].w[shapeIdx] : 0f;
    }

    static float WindowMax(List<BlendshapeRecorder.Sample> samples, string shape, float t, float window)
    {
        int idx = BlendshapeRecorder.IndexOf(shape);
        if (idx < 0) return 0f;
        float max = 0f;
        foreach (var s in samples)
            if (s.t >= t - window && s.t <= t + window)
                max = Mathf.Max(max, s.w[idx]);
        return max;
    }

    static float WindowMin(List<BlendshapeRecorder.Sample> samples, string shape, float t, float window)
    {
        int idx = BlendshapeRecorder.IndexOf(shape);
        if (idx < 0) return 0f;
        float min = 1f;
        foreach (var s in samples)
            if (s.t >= t - window && s.t <= t + window)
                min = Mathf.Min(min, s.w[idx]);
        return min;
    }

    /// <summary>Min over the window of the summed open-shape weights — measures how
    /// well the mouth actually closes at some instant inside the closure window.</summary>
    static float WindowMinOfSum(List<BlendshapeRecorder.Sample> samples, string[] shapes, float t, float window)
    {
        var idx = new int[shapes.Length];
        for (int i = 0; i < shapes.Length; i++) idx[i] = BlendshapeRecorder.IndexOf(shapes[i]);
        float min = float.MaxValue;
        foreach (var s in samples)
        {
            if (s.t < t - window || s.t > t + window) continue;
            float sum = 0f;
            foreach (var i in idx) if (i >= 0) sum += s.w[i];
            min = Mathf.Min(min, sum);
        }
        return min == float.MaxValue ? 0f : min;
    }

    static float MaxLipAt(List<BlendshapeRecorder.Sample> samples, float t)
    {
        float max = 0f;
        foreach (var shape in LipShapes)
            max = Mathf.Max(max, ValueAt(samples, BlendshapeRecorder.IndexOf(shape), t));
        return max;
    }

    /// <summary>Seconds after `start` until every lip shape is below `threshold`.
    /// Returns -1 if that never happens in the recording.</summary>
    static float DecayTimeAfter(List<BlendshapeRecorder.Sample> samples, float start, float threshold)
    {
        foreach (var s in samples)
        {
            if (s.t < start) continue;
            bool allBelow = true;
            foreach (var shape in LipShapes)
            {
                int idx = BlendshapeRecorder.IndexOf(shape);
                if (idx >= 0 && s.w[idx] >= threshold) { allBelow = false; break; }
            }
            if (allBelow) return s.t - start;
        }
        return -1f;
    }

    /// <summary>How long before `t` the shape first crossed 20% of its peak (positive
    /// = anticipation). Scans back at most 250ms.</summary>
    static float AnticipationLead(List<BlendshapeRecorder.Sample> samples, string shape, float t, float peak)
    {
        int idx = BlendshapeRecorder.IndexOf(shape);
        if (idx < 0 || peak <= 0f) return 0f;
        float threshold = 0.2f * peak;
        float crossT = t;
        for (int i = samples.Count - 1; i >= 0; i--)
        {
            var s = samples[i];
            if (s.t > t) continue;
            if (s.t < t - 0.25f) break;
            if (s.w[idx] >= threshold) crossT = s.t;
            else break; // walked back past the rise
        }
        return t - crossT;
    }

    /// <summary>RMS of the second difference over all tracked shapes, resampled on a
    /// 60Hz grid so frame-rate variation doesn't skew comparisons between runs.</summary>
    static float JitterRms(List<BlendshapeRecorder.Sample> samples)
    {
        const float dt = 1f / 60f;
        float tEnd = samples[^1].t;
        int n = Mathf.FloorToInt(tEnd / dt);
        if (n < 3) return 0f;

        double sumSq = 0;
        long   count = 0;
        for (int shapeIdx = 0; shapeIdx < BlendshapeRecorder.TrackedShapes.Length; shapeIdx++)
        {
            float prev2 = ValueAt(samples, shapeIdx, 0f);
            float prev1 = ValueAt(samples, shapeIdx, dt);
            for (int i = 2; i < n; i++)
            {
                float cur = ValueAt(samples, shapeIdx, i * dt);
                float dd  = cur - 2f * prev1 + prev2;
                sumSq += dd * dd;
                count++;
                prev2 = prev1;
                prev1 = cur;
            }
        }
        return count == 0 ? 0f : Mathf.Sqrt((float)(sumSq / count));
    }
}

[Serializable]
public class LipSyncResult
{
    public string fixture;
    public bool   passed;
    public int    passedChecks;
    public int    totalChecks;
    public float  jitterRms;
    public int    sampleCount;
    public List<CheckResult> checks = new();
}

[Serializable]
public class CheckResult
{
    public float  time;
    public string type;
    public string label;
    public bool   passed;
    public float  value;     // primary metric value
    public float  secondary; // context-dependent (open-shape min, anticipation lead…)
    public string detail;
}
