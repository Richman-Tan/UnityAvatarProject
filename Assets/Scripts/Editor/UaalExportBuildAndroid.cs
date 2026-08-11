using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Exports the Android Gradle project to android-export/ for the UaaL embed —
/// the Android sibling of UaalExportBuild (iOS). Always a full (non-append)
/// export: Unity's Gradle export has no supported append mode.
///
/// exportAsGoogleAndroidProject produces a self-contained unityLibrary Gradle
/// module (unity-classes.jar, arm64 jniLibs, streaming assets, noCompress
/// wiring) that the RN app consumes via settings.gradle projectDir — see
/// plugins/withUnityFramework.js in the app repo.
///
/// Writes android-export/export_result.json when done so external tooling
/// (Claude/CI) can poll the filesystem instead of blocking on the editor.
///
/// Run from the menu (Tools → UaaL → Export Android) or via MCP execute_code:
///   EditorApplication.delayCall += UaalExportBuildAndroid.Run;
/// </summary>
public static class UaalExportBuildAndroid
{
    const string ExportDirName = "android-export";
    const string MinimalScenePath = "Assets/Scenes/AndroidEmbedTest.unity";

    [MenuItem("Tools/UaaL/Export Android (android-export)")]
    public static void Run() => Export(minimalScene: false);

    [MenuItem("Tools/UaaL/Export Android - Minimal Test Scene")]
    public static void RunMinimal() => Export(minimalScene: true);

    static void Export(bool minimalScene)
    {
        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        var exportPath = Path.Combine(projectRoot, ExportDirName);
        var marker = Path.Combine(exportPath, "export_result.json");
        try { if (File.Exists(marker)) File.Delete(marker); } catch { /* best effort */ }

        ApplyAndroidPlayerSettings();

        string[] scenes;
        if (minimalScene)
        {
            EnsureMinimalTestScene();
            scenes = new[] { MinimalScenePath };
        }
        else
        {
            scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                scenes = new[] { "Assets/Scenes/SampleScene.unity" };
            }
        }

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = exportPath,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        };

        Debug.Log($"[UaalExportBuildAndroid] Starting Android export → {exportPath} (minimal={minimalScene}, scenes={string.Join(",", scenes)})");
        string resultJson;
        try
        {
            var report = UnityEditor.BuildPipeline.BuildPlayer(options);
            PatchUnityLibraryGradleForCommittedArtifacts(exportPath);
            var summary = report.summary;
            resultJson = "{\"result\":\"" + summary.result + "\",\"errors\":" + summary.totalErrors +
                         ",\"warnings\":" + summary.totalWarnings +
                         ",\"minimalScene\":" + (minimalScene ? "true" : "false") +
                         ",\"endedAt\":\"" + DateTime.Now.ToString("o") + "\"}";
            Debug.Log($"[UaalExportBuildAndroid] {summary.result} errors={summary.totalErrors} warnings={summary.totalWarnings}");
        }
        catch (Exception ex)
        {
            resultJson = "{\"result\":\"Exception\",\"message\":" +
                         "\"" + ex.Message.Replace("\"", "'") + "\"}";
            Debug.LogError($"[UaalExportBuildAndroid] Exception: {ex}");
        }

        try
        {
            Directory.CreateDirectory(exportPath);
            File.WriteAllText(marker, resultJson);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[UaalExportBuildAndroid] Could not write marker: {ex.Message}");
        }
    }

    /// <summary>
    /// Enforces the Android build configuration the RN embed depends on, so a
    /// manual Build Settings drift can never produce an incompatible export:
    /// Gradle-project export, ASTC textures, IL2CPP/ARM64 (matches the arm64-v8a
    /// ABI filter in the app's gradle.properties), classic Activity entry point
    /// (GameActivity is much harder to host inside a foreign RN Activity), and
    /// Unity audio disabled (expo-av owns all playback; FMOD would otherwise
    /// contend for Android audio focus).
    /// </summary>
    static void ApplyAndroidPlayerSettings()
    {
        EditorUserBuildSettings.exportAsGoogleAndroidProject = true;
        EditorUserBuildSettings.buildAppBundle = false;
        EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;

        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.anonymous.DementiaGuideAi");
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;

        var audioManager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset").FirstOrDefault();
        if (audioManager != null)
        {
            var so = new SerializedObject(audioManager);
            var disableAudio = so.FindProperty("m_DisableAudio");
            if (disableAudio != null && !disableAudio.boolValue)
            {
                disableAudio.boolValue = true;
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
                Debug.Log("[UaalExportBuildAndroid] Disabled Unity audio (expo-av owns playback).");
            }
        }
    }

    /// <summary>
    /// Inserts a committed-artifact guard into the exported unityLibrary
    /// build.gradle's buildIl2Cpp task: the multi-GB Il2CppOutputProject
    /// source/toolchain tree is gitignored (build intermediate), so on a fresh
    /// clone the task must use the committed prebuilt libil2cpp.so instead of
    /// trying (and failing) to run the absent IL2CPP compiler. Re-applied on
    /// every export because Unity regenerates build.gradle from its template.
    /// </summary>
    static void PatchUnityLibraryGradleForCommittedArtifacts(string exportPath)
    {
        var gradlePath = Path.Combine(exportPath, "unityLibrary", "build.gradle");
        if (!File.Exists(gradlePath)) return;

        var content = File.ReadAllText(gradlePath);
        if (content.Contains("Committed-artifact mode")) return;

        const string anchor = "    doLast {\n        // skipIl2CppBuild used for testing purposes";
        const string guard = "    doLast {\n" +
            "        // Committed-artifact mode (added post-export by UaalExportBuildAndroid.cs):\n" +
            "        // the multi-GB Il2CppOutputProject source/toolchain tree is a build\n" +
            "        // intermediate and is NOT committed to git — libil2cpp.so is prebuilt\n" +
            "        // and committed in jniLibs instead. On a fresh clone, verify and skip.\n" +
            "        if (!file(\"${workingDir}/src/main/Il2CppOutputProject\").exists()) {\n" +
            "            archs.each { arch, abi ->\n" +
            "                def prebuilt = file(getIl2CppOutputPath(workingDir, abi))\n" +
            "                if (!prebuilt.exists() || prebuilt.length() < 1000000) {\n" +
            "                    throw new GradleException(\"Il2CppOutputProject absent and no prebuilt ${prebuilt} — re-export from Unity (Tools → UaaL → Export Android).\")\n" +
            "                }\n" +
            "            }\n" +
            "            println(\"buildIl2Cpp: using committed prebuilt libil2cpp.so (Il2CppOutputProject not present).\")\n" +
            "            return\n" +
            "        }\n" +
            "        // skipIl2CppBuild used for testing purposes";

        if (!content.Contains(anchor))
        {
            Debug.LogWarning("[UaalExportBuildAndroid] buildIl2Cpp doLast anchor not found in unityLibrary/build.gradle — Unity template changed? Committed-artifact guard NOT applied; fresh clones will fail to build until this is fixed.");
            return;
        }
        File.WriteAllText(gradlePath, content.Replace(anchor, guard));
        Debug.Log("[UaalExportBuildAndroid] Applied committed-artifact guard to unityLibrary/build.gradle.");
    }

    /// <summary>
    /// Creates the minimal embed-test scene if missing: camera + light + a
    /// spinning cube named "AvatarRouter" carrying EmbedTestProbe, so
    /// UnitySendMessage from the RN bridge lands without the character rig.
    /// Validates the whole embed pipeline independently of the Reallusion
    /// shader/material risk.
    /// </summary>
    static void EnsureMinimalTestScene()
    {
        if (File.Exists(Path.Combine(Directory.GetParent(Application.dataPath).FullName, MinimalScenePath)))
        {
            return;
        }

        var previousScenePath = EditorSceneManager.GetActiveScene().path;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "AvatarRouter";
        cube.transform.position = new Vector3(0f, 1f, 0f);
        cube.AddComponent<EmbedTestProbe>();

        var camera = UnityEngine.Object.FindFirstObjectByType<Camera>();
        if (camera != null)
        {
            camera.transform.position = new Vector3(0f, 1f, -4f);
            camera.transform.LookAt(cube.transform);
            camera.backgroundColor = new Color(0.16f, 0.24f, 0.28f); // app teal, obvious vs RN white
            camera.clearFlags = CameraClearFlags.SolidColor;
        }

        EditorSceneManager.SaveScene(scene, MinimalScenePath);
        Debug.Log($"[UaalExportBuildAndroid] Created {MinimalScenePath}");

        if (!string.IsNullOrEmpty(previousScenePath))
        {
            EditorSceneManager.OpenScene(previousScenePath);
        }
    }
}
