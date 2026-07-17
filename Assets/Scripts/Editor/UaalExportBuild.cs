using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Exports the iOS Xcode project to uaal-export/ for the UaaL embed.
/// Append mode (AcceptExternalModificationsToPlayer) is used when the export
/// already exists so only Data/ and generated sources refresh — much faster
/// and keeps local Xcode tweaks.
///
/// Writes uaal-export/export_result.json when done so external tooling
/// (Claude/CI) can poll the filesystem instead of blocking on the editor.
///
/// Run from the menu (Tools → UaaL → Export iOS) or via MCP execute_code:
///   EditorApplication.delayCall += UaalExportBuild.Run;
/// </summary>
public static class UaalExportBuild
{
    [MenuItem("Tools/UaaL/Export iOS (uaal-export)")]
    public static void Run()
    {
        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        var exportPath = Path.Combine(projectRoot, "uaal-export");
        var marker = Path.Combine(exportPath, "export_result.json");
        try { if (File.Exists(marker)) File.Delete(marker); } catch { /* best effort */ }

        // Use the enabled scenes from Build Settings; fall back to the main scene.
        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();
        if (scenes.Length == 0)
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" };
        }

        var append = Directory.Exists(Path.Combine(exportPath, "Unity-iPhone.xcodeproj"));
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = exportPath,
            target = BuildTarget.iOS,
            options = append ? BuildOptions.AcceptExternalModificationsToPlayer : BuildOptions.None,
        };

        Debug.Log($"[UaalExportBuild] Starting iOS export → {exportPath} (append={append}, scenes={string.Join(",", scenes)})");
        string resultJson;
        try
        {
            var report = UnityEditor.BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            resultJson = "{\"result\":\"" + summary.result + "\",\"errors\":" + summary.totalErrors +
                         ",\"warnings\":" + summary.totalWarnings +
                         ",\"endedAt\":\"" + DateTime.Now.ToString("o") + "\"}";
            Debug.Log($"[UaalExportBuild] {summary.result} errors={summary.totalErrors} warnings={summary.totalWarnings}");
        }
        catch (Exception ex)
        {
            resultJson = "{\"result\":\"Exception\",\"message\":" +
                         "\"" + ex.Message.Replace("\"", "'") + "\"}";
            Debug.LogError($"[UaalExportBuild] Exception: {ex}");
        }

        try
        {
            Directory.CreateDirectory(exportPath);
            File.WriteAllText(marker, resultJson);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[UaalExportBuild] Could not write marker: {ex.Message}");
        }
    }
}
