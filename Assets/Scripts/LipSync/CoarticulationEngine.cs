using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Bakes a 14-key viseme event timeline into per-shape weight curves using
/// Cohen–Massaro-style overlapping dominance envelopes, replacing the old
/// keyframe-lerp playback that snapped between mouth poses.
///
/// Model, per event:
///   dominance D(t) rises with a smoothstep from (onset = t − anticipation) to a
///   peak shortly after the acoustic onset, sustains through the phoneme, and
///   smoothsteps back down over the release window. Neighbouring phonemes overlap,
///   so at any instant several visemes are partially dominant — their shape
///   targets are blended with a dominance-weighted average (power-sharpened),
///   which IS the co-articulation.
///
/// Special-cased closures (the most visible lip-sync defect when wrong):
///   bilabial /p b m/ and labiodental /f v/ dominance is forced to a high minimum
///   peak, their contact shape (V_Explosive / V_Dental_Lip) is combined with MAX
///   instead of averaged, and all mouth-opening shapes are actively suppressed
///   while the closure is dominant — guaranteeing the lips actually touch.
///
/// Pure C#: no UnityEngine state, deterministic, editor-testable without play
/// mode (Mathf only for math). Bake once per segment, then playback is an O(1)
/// frame lookup.
/// </summary>
public static class CoarticulationEngine
{
    public struct VisemeEvent
    {
        public float  t;  // acoustic onset (s)
        public float  d;  // duration (s)
        public string v;  // 14-key viseme id
        public float  w;  // peak weight 0–1
    }

    public class BakedCurves
    {
        public float    sampleRate;
        public float    duration;              // covers timeline + longest release tail
        public string[] shapes;                // column order
        public float[,] frames;                // [frameIndex, shapeIndex]

        public int FrameCount => frames.GetLength(0);

        /// <summary>Linear-interpolated weights at time t (s), written into `result`
        /// (cleared first). Values below 0.0005 are omitted, matching the old
        /// keyframe path so AvatarController decay behaviour is unchanged.</summary>
        public void WeightsAtTime(float t, Dictionary<string, float> result)
        {
            result.Clear();
            if (FrameCount == 0) return;

            float f     = Mathf.Clamp(t * sampleRate, 0f, FrameCount - 1.001f);
            int   i     = (int)f;
            float alpha = f - i;
            int   iNext = Mathf.Min(i + 1, FrameCount - 1);

            for (int s = 0; s < shapes.Length; s++)
            {
                float v = Mathf.Lerp(frames[i, s], frames[iNext, s], alpha);
                if (v > 0.0005f) result[shapes[s]] = v;
            }
        }
    }

    struct ActiveEvent
    {
        public VisemeEvent               e;
        public VisemeMap.VisemeClass     cls;
        public Dictionary<string, float> shapes;
        public float onset, peakT, sustainEnd, off, peak;
        public bool  isClosure;
    }

    public static BakedCurves Bake(List<VisemeEvent> events, float timelineDuration, LipSyncTuning tuning)
    {
        tuning ??= LipSyncTuning.Defaults;

        // Prepare envelope windows.
        var active = new List<ActiveEvent>(events.Count);
        float end  = timelineDuration;
        foreach (var e in events)
        {
            var cls = VisemeMap.ClassOf(e.v);
            if (cls == VisemeMap.VisemeClass.Neutral || e.w <= 0.001f) continue;
            if (!VisemeMap.Shapes.TryGetValue(e.v, out var shapes) || shapes.Count == 0) continue;

            bool isClosure = cls == VisemeMap.VisemeClass.BilabialClosure ||
                             cls == VisemeMap.VisemeClass.LabiodentalClosure;

            var a = new ActiveEvent
            {
                e = e, cls = cls, shapes = shapes, isClosure = isClosure,
                onset      = e.t - tuning.AnticipationFor(cls),
                peakT      = e.t + Mathf.Min(e.d * 0.3f, 0.04f),
                sustainEnd = e.t + e.d,
                off        = e.t + e.d + tuning.ReleaseFor(cls),
                peak       = isClosure ? Mathf.Max(e.w, tuning.closureMinPeak) : e.w,
            };
            active.Add(a);
            end = Mathf.Max(end, a.off);
        }

        // Collect the union of shapes ever driven.
        var shapeIndex = new Dictionary<string, int>();
        foreach (var a in active)
            foreach (var name in a.shapes.Keys)
                if (!shapeIndex.ContainsKey(name))
                    shapeIndex[name] = shapeIndex.Count;

        var shapeNames = new string[shapeIndex.Count];
        foreach (var kvp in shapeIndex) shapeNames[kvp.Value] = kvp.Key;

        int fs = Mathf.Max(30, tuning.sampleRate);
        int n  = Mathf.CeilToInt(end * fs) + 1;
        var curves = new BakedCurves
        {
            sampleRate = fs,
            duration   = end,
            shapes     = shapeNames,
            frames     = new float[n, shapeNames.Length],
        };
        if (active.Count == 0 || shapeNames.Length == 0) return curves;

        float p = Mathf.Max(1f, tuning.dominancePower);
        var wSum = new float[shapeNames.Length];
        var dSum = new float[shapeNames.Length];

        for (int frame = 0; frame < n; frame++)
        {
            float t = frame / (float)fs;
            System.Array.Clear(wSum, 0, wSum.Length);
            System.Array.Clear(dSum, 0, dSum.Length);
            float closureDom = 0f;
            int   closureShapeIdx = -1;
            float closureShapeTarget = 0f;

            foreach (var a in active)
            {
                float D = Dominance(a, t);
                if (D <= 0.001f) continue;
                float dp = Mathf.Pow(D, p);

                foreach (var kvp in a.shapes)
                {
                    int idx = shapeIndex[kvp.Key];
                    // Tongue contact is near-binary in real speech: stress reduction
                    // shrinks mouth opening, not whether the tongue touches — floor
                    // the weight for V_Tongue_* so unstressed "the" still shows it.
                    float peak = kvp.Key.StartsWith("V_Tongue")
                        ? Mathf.Max(a.peak, tuning.tongueMinWeight)
                        : a.peak;
                    wSum[idx] += dp * (peak * kvp.Value);
                    dSum[idx] += dp;
                }

                if (a.isClosure && D > closureDom)
                {
                    closureDom = D;
                    string contact = VisemeMap.ClosureShape[a.cls];
                    closureShapeIdx = shapeIndex.TryGetValue(contact, out var ci) ? ci : -1;
                    closureShapeTarget = a.peak * a.shapes.GetValueOrDefault(contact, 1f);
                }
            }

            for (int s = 0; s < shapeNames.Length; s++)
            {
                // Dominance-weighted average; denominator floored at 1 so an isolated
                // half-dominant viseme produces a half-strength shape instead of
                // snapping to full target (classic Cohen–Massaro normalization issue).
                float v = wSum[s] / Mathf.Max(dSum[s], 1f);

                if (closureDom > 0f && System.Array.IndexOf(VisemeMap.OpenShapes, shapeNames[s]) >= 0)
                    v *= 1f - tuning.suppressionStrength * closureDom;

                curves.frames[frame, s] = Mathf.Clamp01(v);
            }

            // Guaranteed contact: the closure shape follows MAX(avg, dominance·target),
            // immune to averaging against neighbouring vowels.
            if (closureShapeIdx >= 0)
                curves.frames[frame, closureShapeIdx] =
                    Mathf.Max(curves.frames[frame, closureShapeIdx],
                              Mathf.Clamp01(closureDom * closureShapeTarget));
        }

        return curves;
    }

    static float Dominance(in ActiveEvent a, float t)
    {
        if (t <= a.onset || t >= a.off) return 0f;
        if (t < a.peakT)
            return Smoothstep((t - a.onset) / Mathf.Max(a.peakT - a.onset, 1e-4f));
        if (t <= a.sustainEnd)
            return 1f;
        return 1f - Smoothstep((t - a.sustainEnd) / Mathf.Max(a.off - a.sustainEnd, 1e-4f));
    }

    static float Smoothstep(float x)
    {
        x = Mathf.Clamp01(x);
        return x * x * (3f - 2f * x);
    }
}
