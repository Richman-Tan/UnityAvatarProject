using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Fast inner-loop check: runs every fixture's viseme timeline through
/// CoarticulationEngine.Bake and evaluates the SAME LipSyncMetrics acceptance
/// criteria directly on the baked curves — no play mode, results in milliseconds.
///
/// This measures the engine's output before AvatarController smoothing, so the
/// play-mode run (LipSyncTestRunner) remains the authoritative end-to-end check;
/// use this to iterate on envelope/tuning math quickly via MCP execute_code:
///   LipSyncBakeCheck.RunAll();          // summary string
///   LipSyncBakeCheck.Run("bilabials");  // full per-check detail
/// </summary>
public static class LipSyncBakeCheck
{
    public static LipSyncTuning ResolveTuning()
    {
        var guids = AssetDatabase.FindAssets("t:LipSyncTuning");
        return guids.Length > 0
            ? AssetDatabase.LoadAssetAtPath<LipSyncTuning>(AssetDatabase.GUIDToAssetPath(guids[0]))
            : LipSyncTuning.Defaults;
    }

    [MenuItem("Tools/LipSync/Bake Check (All Fixtures)")]
    public static void RunAllMenu() => Debug.Log(RunAll());

    public static string RunAll()
    {
        var sb = new StringBuilder();
        foreach (var name in LipSyncFixture.ListAll())
        {
            var r = Evaluate(name);
            sb.AppendLine($"{r.fixture}: {(r.passed ? "PASS" : "FAIL")} {r.passedChecks}/{r.totalChecks} jitter={r.jitterRms:F5}");
        }
        return sb.ToString();
    }

    public static string Run(string fixtureName)
    {
        var r  = Evaluate(fixtureName);
        var sb = new StringBuilder();
        sb.AppendLine($"{r.fixture}: {r.passedChecks}/{r.totalChecks} jitter={r.jitterRms:F5}");
        foreach (var c in r.checks)
            sb.AppendLine($"  {(c.passed ? "PASS" : "FAIL")} {c.type,-12} t={c.time:F2} {c.label,-14} {c.detail}");
        return sb.ToString();
    }

    public static LipSyncResult Evaluate(string fixtureName)
    {
        var fixture = LipSyncFixture.Load(fixtureName);
        var visemes = CC4MessageProtocol.ParseVisemeArray(fixture.rawJson);
        var baked   = CoarticulationEngine.Bake(visemes, fixture.duration, ResolveTuning());
        return LipSyncMetrics.Compute(fixture, ToSamples(baked));
    }

    /// <summary>Re-columns the baked curves into BlendshapeRecorder sample layout so
    /// LipSyncMetrics runs identically on baked and play-mode-recorded data.</summary>
    static List<BlendshapeRecorder.Sample> ToSamples(CoarticulationEngine.BakedCurves baked)
    {
        var colMap = new int[BlendshapeRecorder.TrackedShapes.Length];
        for (int i = 0; i < colMap.Length; i++)
            colMap[i] = System.Array.IndexOf(baked.shapes, BlendshapeRecorder.TrackedShapes[i]);

        var samples = new List<BlendshapeRecorder.Sample>(baked.FrameCount);
        for (int f = 0; f < baked.FrameCount; f++)
        {
            var w = new float[colMap.Length];
            for (int i = 0; i < colMap.Length; i++)
                if (colMap[i] >= 0) w[i] = baked.frames[f, colMap[i]];
            samples.Add(new BlendshapeRecorder.Sample { t = f / baked.sampleRate, w = w });
        }
        return samples;
    }
}
