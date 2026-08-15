using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Exports a WebGL build for the DementiaGuideAI web app (web/public/unity/).
/// Sibling of UaalExportBuild (iOS) and UaalExportBuildAndroid — same scene
/// resolution and result-marker pattern, targeting BuildTarget.WebGL.
///
/// Brotli + decompressionFallback: the .unityweb files are decompressed by
/// the loader in JS, so the static host needs no Content-Encoding headers
/// (an uncompressed data file measured 295 MB — over Vercel's ~100 MB
/// per-file limit — so compression is not optional). The web app probes
/// /unity/Build/unity.loader.js to detect the build and reads the synced
/// manifest.json for the suffixed file names (see
/// web/src/avatar/unity/unityBridge.js and web/scripts/sync-unity-webgl.mjs).
///
/// Run from the menu (Tools → UaaL → Export WebGL) or via MCP execute_code:
///   EditorApplication.delayCall += UaalExportBuildWebGL.Run;
///
/// Afterwards copy Builds/WebGL/Build/* into web/public/unity/Build/
/// (see web/public/unity/README.md).
///
/// Known risk: Reallusion's shader tooling caps WebGL at URP 12 — CC4/CC5 HD
/// character materials may need manual downgrades before this build looks
/// right (ShaderPackageUtil.cs enforces the cap).
/// </summary>
public static class UaalExportBuildWebGL
{
    [MenuItem("Tools/UaaL/Export WebGL (Builds/WebGL)")]
    public static void Run()
    {
        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        var exportPath = Path.Combine(projectRoot, "Builds", "WebGL");
        var marker = Path.Combine(exportPath, "export_result.json");
        try { if (File.Exists(marker)) File.Delete(marker); } catch { /* best effort */ }

        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();
        if (scenes.Length == 0)
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" };
        }

        // Brotli with the JS decompression fallback: no Content-Encoding
        // requirements on the static host, and the on-disk files stay under
        // static-hosting per-file limits.
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
        PlayerSettings.WebGL.decompressionFallback = true;

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = exportPath,
            target = BuildTarget.WebGL,
            options = BuildOptions.None,
        };

        Debug.Log($"[UaalExportBuildWebGL] Starting WebGL export → {exportPath} (scenes={string.Join(",", scenes)})");
        string resultJson;
        try
        {
            var report = UnityEditor.BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            resultJson = "{\"result\":\"" + summary.result + "\",\"errors\":" + summary.totalErrors +
                         ",\"warnings\":" + summary.totalWarnings +
                         ",\"endedAt\":\"" + DateTime.Now.ToString("o") + "\"}";
            Debug.Log($"[UaalExportBuildWebGL] {summary.result} errors={summary.totalErrors} warnings={summary.totalWarnings}");
        }
        catch (Exception ex)
        {
            resultJson = "{\"result\":\"Exception\",\"message\":\"" + ex.Message.Replace("\\", "\\\\").Replace("\"", "'") +
                         "\",\"endedAt\":\"" + DateTime.Now.ToString("o") + "\"}";
            Debug.LogError($"[UaalExportBuildWebGL] Exception: {ex}");
        }

        try
        {
            Directory.CreateDirectory(exportPath);
            File.WriteAllText(marker, resultJson);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[UaalExportBuildWebGL] Could not write marker: {ex.Message}");
        }
    }
}
