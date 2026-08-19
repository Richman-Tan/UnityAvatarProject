using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor entry points for the automated lip-sync fixture loop.
///
/// Writes a run-request file and enters play mode; LipSyncTestDriver (runtime)
/// consumes the request, runs the fixtures, writes results to
/// TestResults/lipsync/&lt;runId&gt;/ and exits play mode again.
///
/// Static methods are invokable from the menu, or headlessly via MCP
/// execute_code:  LipSyncTestRunner.RunAll();  /  LipSyncTestRunner.Run("bilabials");
/// </summary>
public static class LipSyncTestRunner
{
    [MenuItem("Tools/LipSync/Run All Fixtures")]
    public static void RunAll() => Run("all");

    /// <summary>Measurement-only run: skips the per-check PNGs, which stall the
    /// render pipeline enough to starve the per-frame sampler.</summary>
    [MenuItem("Tools/LipSync/Run All Fixtures (no captures)")]
    public static void RunAllNoCaptures() => Run("all", false);

    public static string Run(string fixtures) => Run(fixtures, true);

    public static string Run(string fixtures, bool captureScreenshots)
    {
        string runId = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var request = new LipSyncTestDriver.RunRequest
        {
            runId = runId,
            fixtures = fixtures,
            exitPlayModeWhenDone = true,
            captureScreenshots = captureScreenshots,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(LipSyncTestDriver.RequestPath));
        File.WriteAllText(LipSyncTestDriver.RequestPath, JsonUtility.ToJson(request));

        if (!EditorApplication.isPlaying)
            EditorApplication.EnterPlaymode();

        Debug.Log($"[LipSyncTestRunner] queued run '{runId}' (fixtures: {fixtures})");
        return runId;
    }

    [MenuItem("Tools/LipSync/Open Latest Results")]
    public static void OpenLatestResults()
    {
        string root = LipSyncTestDriver.ResultsRoot;
        if (!Directory.Exists(root)) { Debug.Log("No results yet."); return; }
        var dirs = Directory.GetDirectories(root);
        if (dirs.Length == 0) { Debug.Log("No results yet."); return; }
        System.Array.Sort(dirs);
        EditorUtility.RevealInFinder(dirs[^1]);
    }
}
