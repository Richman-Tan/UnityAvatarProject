using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

/// <summary>
/// WebGL-only texture size caps for the web avatar build (web/public/unity/).
/// Writes a "WebGL" platform override block (max 1024, compressed) into every
/// texture importer under Assets/ — iOS/Android platform settings are
/// untouched by construction, though the meta churn does invalidate their
/// cached import artifacts (the next switch back re-encodes).
///
/// Full unattended chain for producing the web build:
///   EditorApplication.delayCall += WebGLTextureOverrides.SwitchApplyAndExport;
/// Requests the WebGL target switch; after the switch lands (the flag file
/// survives the domain reload) OnActiveBuildTargetChanged applies the
/// overrides and schedules UaalExportBuildWebGL.Run. Poll
/// Builds/WebGL/export_result.json for the outcome. Overrides are applied
/// AFTER the switch on purpose: on the WebGL side the touched textures
/// re-encode with fast desktop formats instead of forcing an immediate
/// hours-long ASTC re-encode for the outgoing Android target.
/// </summary>
public static class WebGLTextureOverrides
{
    public const int MaxSize = 1024;

    static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
    static string FlagPath => Path.Combine(ProjectRoot, "Builds", "webgl-export-requested.flag");
    static string ResultPath => Path.Combine(ProjectRoot, "Builds", "webgl_texture_overrides_result.json");

    [MenuItem("Tools/UaaL/Apply WebGL Texture Overrides")]
    public static void Apply()
    {
        int scanned = 0, changed = 0;
        var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" });
        try
        {
            AssetDatabase.StartAssetEditing();
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;
                scanned++;
                var s = importer.GetPlatformTextureSettings("WebGL");
                if (s.overridden && s.maxTextureSize <= MaxSize) continue;
                s.overridden = true;
                s.maxTextureSize = MaxSize;
                s.format = TextureImporterFormat.Automatic;
                s.textureCompression = TextureImporterCompression.Compressed;
                importer.SetPlatformTextureSettings(s);
                AssetDatabase.WriteImportSettingsIfDirty(path);
                changed++;
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }
        Directory.CreateDirectory(Path.Combine(ProjectRoot, "Builds"));
        File.WriteAllText(ResultPath,
            "{\"scanned\":" + scanned + ",\"changed\":" + changed +
            ",\"endedAt\":\"" + DateTime.Now.ToString("o") + "\"}");
        Debug.Log($"[WebGLTextureOverrides] scanned={scanned} changed={changed}");
    }

    [MenuItem("Tools/UaaL/Export WebGL (full chain: switch + overrides + build)")]
    public static void SwitchApplyAndExport()
    {
        Directory.CreateDirectory(Path.Combine(ProjectRoot, "Builds"));
        File.WriteAllText(FlagPath, DateTime.Now.ToString("o"));
        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL)
        {
            ContinueOnWebGL();
            return;
        }
        Debug.Log("[WebGLTextureOverrides] Switching active build target to WebGL…");
        EditorUserBuildSettings.SwitchActiveBuildTargetAsync(BuildTargetGroup.WebGL, BuildTarget.WebGL);
    }

    internal static void ContinueOnWebGL()
    {
        if (!File.Exists(FlagPath)) return;
        try { File.Delete(FlagPath); } catch { /* best effort */ }
        Apply();
        EditorApplication.delayCall += UaalExportBuildWebGL.Run;
    }
}

/// <summary>
/// Survives the domain reload that accompanies the target switch; the flag
/// file gates it to explicitly requested exports (a manual platform switch
/// alone must not trigger a build).
/// </summary>
class WebGLExportOnTargetSwitch : IActiveBuildTargetChanged
{
    public int callbackOrder => 0;

    public void OnActiveBuildTargetChanged(BuildTarget previous, BuildTarget current)
    {
        if (current != BuildTarget.WebGL) return;
        EditorApplication.delayCall += WebGLTextureOverrides.ContinueOnWebGL;
    }
}
