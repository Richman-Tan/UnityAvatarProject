using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Play-mode orchestrator for automated lip-sync fixture runs.
///
/// The editor-side runner (Editor/LipSyncTestRunner.cs) writes a run-request file
/// to Temp/ and enters play mode; this driver picks the request up, plays each
/// fixture through the REAL production entry point (NativeBridgeReceiver.
/// ReceiveBridgeMessage), records the smoothed weights, captures screenshots at
/// every check time, computes metrics, writes everything to
/// TestResults/lipsync/&lt;runId&gt;/ and exits play mode.
///
/// Everything is file-based so the run can be driven and inspected entirely from
/// outside the editor (MCP execute_code / CLI).
/// </summary>
public class LipSyncTestDriver : MonoBehaviour
{
    public static string RequestPath =>
        Path.Combine(Application.dataPath, "../Temp/lipsync_run_request.json");
    public static string ResultsRoot =>
        Path.Combine(Application.dataPath, "../TestResults/lipsync");

    [System.Serializable]
    public class RunRequest
    {
        public string runId;
        public string fixtures; // "all" or comma-separated names
        public bool   exitPlayModeWhenDone = true;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!File.Exists(RequestPath)) return;
        // This iOS project ships runInBackground=false, which freezes editor play
        // mode the moment no editor window has focus — automated runs would stall
        // at frame 1. Runtime-only override; never touches PlayerSettings.
        Application.runInBackground = true;
        var go = new GameObject("LipSyncTestDriver");
        go.AddComponent<LipSyncTestDriver>();
    }

    IEnumerator Start()
    {
        var request = JsonUtility.FromJson<RunRequest>(File.ReadAllText(RequestPath));
        File.Delete(RequestPath); // consume so a stray play never re-runs it

        string runDir = Path.Combine(ResultsRoot, request.runId);
        Directory.CreateDirectory(runDir);

        var receiver = FindAnyObjectByType<NativeBridgeReceiver>();
        var avatar   = FindAnyObjectByType<AvatarController>();
        if (receiver == null || avatar == null)
        {
            WriteSummary(runDir, null, $"missing components: receiver={(receiver != null)} avatar={(avatar != null)}");
            yield return Finish(request);
            yield break;
        }

        FrameFaceCamera(avatar);

        var recorder = gameObject.AddComponent<BlendshapeRecorder>();

        var names = request.fixtures == "all"
            ? LipSyncFixture.ListAll()
            : new List<string>(request.fixtures.Split(','));

        // Let the first frames settle (Awake shape-map build, idle ramp-in).
        yield return new WaitForSeconds(0.5f);

        var results = new List<LipSyncResult>();
        foreach (var name in names)
        {
            LipSyncFixture fixture;
            try { fixture = LipSyncFixture.Load(name.Trim()); }
            catch (System.Exception e)
            {
                Debug.LogError($"[LipSyncTestDriver] failed to load fixture '{name}': {e.Message}");
                continue;
            }

            Debug.Log($"[LipSyncTestDriver] running fixture '{fixture.name}' ({fixture.duration:F2}s)");
            recorder.Begin(avatar);
            receiver.ReceiveBridgeMessage(fixture.rawJson);

            // Walk the timeline, firing screenshots as check times pass.
            float start = Time.time;
            int nextCheck = 0;
            var sortedChecks = new List<LipSyncCheck>(fixture.checks);
            sortedChecks.Sort((a, b) => a.time.CompareTo(b.time));

            float tail = 0.5f; // observe end-of-segment decay
            while (Time.time - start < fixture.duration + tail)
            {
                float t = Time.time - start;
                while (nextCheck < sortedChecks.Count && sortedChecks[nextCheck].time <= t)
                {
                    var c = sortedChecks[nextCheck];
                    string shot = Path.Combine(runDir,
                        $"{fixture.name}_{Sanitize(c.label)}_{(int)(c.time * 1000)}ms.png");
                    ScreenCapture.CaptureScreenshot(shot);
                    nextCheck++;
                }
                yield return null;
            }

            recorder.End();
            recorder.WriteCsv(Path.Combine(runDir, $"{fixture.name}.csv"));

            var result = LipSyncMetrics.Compute(fixture, recorder.Samples);
            File.WriteAllText(Path.Combine(runDir, $"{fixture.name}_metrics.json"),
                              JsonUtility.ToJson(result, true));
            results.Add(result);

            // Clear lip channel between fixtures and let shapes decay fully.
            receiver.ReceiveBridgeMessage("{\"type\":\"stop\"}");
            yield return new WaitForSeconds(0.4f);
        }

        WriteSummary(runDir, results, null);
        Debug.Log($"[LipSyncTestDriver] run '{request.runId}' complete: {runDir}");
        yield return Finish(request);
    }

    /// <summary>Spawns a dedicated close-up camera on the head so check-time
    /// screenshots show the mouth region clearly. A separate camera with a higher
    /// depth is used because the Reallusion preview rig owns the scene's Main
    /// Camera and can reposition it. Play-mode only — never saved into the scene.</summary>
    static void FrameFaceCamera(AvatarController avatar)
    {
        var head = avatar.FindBone("CC_Base_Head", out _);
        if (head == null)
        {
            Debug.LogWarning("[LipSyncTestDriver] CC_Base_Head not found — keeping scene camera.");
            return;
        }

        var go  = new GameObject("LipSyncTestCam");
        var cam = go.AddComponent<Camera>();
        cam.depth = 100f; // render on top of the preview-scene camera
        cam.nearClipPlane = 0.05f;
        cam.fieldOfView = 22f;

        Vector3 face = head.position + Vector3.up * 0.04f;
        go.transform.position = face + avatar.transform.forward * 0.55f;
        go.transform.LookAt(face);
    }

    static void WriteSummary(string runDir, List<LipSyncResult> results, string error)
    {
        var sb = new StringBuilder();
        sb.Append("{\"error\":").Append(error == null ? "null" : "\"" + error + "\"");
        sb.Append(",\"fixtures\":[");
        if (results != null)
            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var r = results[i];
                sb.Append("{\"name\":\"" + r.fixture + "\",\"passed\":" + (r.passed ? "true" : "false") +
                          ",\"checks\":\"" + r.passedChecks + "/" + r.totalChecks +
                          "\",\"jitterRms\":" + r.jitterRms.ToString("F5", System.Globalization.CultureInfo.InvariantCulture) + "}");
            }
        sb.Append("]}");
        File.WriteAllText(Path.Combine(runDir, "summary.json"), sb.ToString());
    }

    IEnumerator Finish(RunRequest request)
    {
        yield return null;
#if UNITY_EDITOR
        if (request.exitPlayModeWhenDone)
            UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "check";
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}
