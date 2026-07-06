using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Linq;

// 2026-07-07: this tool once auto-generated black-block eyebrows. It extracts EVERY
// embedded FBX material (including raw slots like Brows_Color_Transparency /
// Brows_Base_Transparency), and when no real diffuse texture is found for a hair-like
// slot it fakes one with a flat tint (see ApplyTextures). For Brows/Eyelash/etc. a
// proper hand-authored *_Hair_Transparency (or _1st_Pass/_2nd_Pass) material already
// exists using Reallusion's real hair shader — that one must always win. The
// HasExistingHairShaderMaterial guards below stop this tool from creating/assigning/
// remapping the fake tint over a slot that already has the real thing.
public class AaronMaterialSetup
{
    const string FBX_PATH        = "Assets/Avatars/HD_Aaron.Fbx";
    const string TEXTURES_ROOT   = "Assets/Avatars/textures/HD_Aaron";
    const string MATERIALS_OUT   = "Assets/Avatars/Materials/HD_Aaron";
    const string HAIR_SHADER_PREFIX = "Shader Graphs/RL5_Hair";

    [MenuItem("Tools/Setup HD_Aaron Materials")]
    static void Run()
    {
        // 1. Create output folder via System.IO (avoids AssetDatabase quirks)
        string fullMatPath = Path.Combine(Application.dataPath, "../", MATERIALS_OUT);
        fullMatPath = Path.GetFullPath(fullMatPath);
        Directory.CreateDirectory(fullMatPath);
        AssetDatabase.Refresh();

        // 2. Build texture lookup: leaf folder name → texture paths
        //    Leaf folder = material slot name (e.g. "Std_Skin_Head", "Beard_Transparency")
        string[] allPaths = AssetDatabase.GetAllAssetPaths();
        var texPaths = allPaths.Where(p =>
            p.StartsWith(TEXTURES_ROOT) &&
            (p.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) ||
             p.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase) ||
             p.EndsWith(".tga", System.StringComparison.OrdinalIgnoreCase))).ToList();

        var slotTextures = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var p in texPaths)
        {
            string leaf = Path.GetFileName(Path.GetDirectoryName(p).Replace("\\", "/"));
            if (!slotTextures.ContainsKey(leaf)) slotTextures[leaf] = new List<string>();
            slotTextures[leaf].Add(p);
        }

        // 3. Get embedded materials from the FBX
        var embedded = AssetDatabase.LoadAllAssetsAtPath(FBX_PATH).OfType<Material>().ToList();
        Debug.Log($"[AaronSetup] Found {embedded.Count} embedded materials");
        if (embedded.Count == 0)
        {
            Debug.LogError("[AaronSetup] No materials found in FBX. Check FBX import settings.");
            return;
        }

        // 4. Extract each material, add PBR textures, collect paths for remap
        var matPaths = new Dictionary<string, string>(); // matName -> .mat asset path
        foreach (var src in embedded)
        {
            string matName = src.name;
            string matAssetPath = $"{MATERIALS_OUT}/{matName}.mat";

            // Copy the embedded material so we keep its baked colours/properties
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matAssetPath);
            if (mat == null)
            {
                mat = new Material(src);
                mat.name = matName;
                AssetDatabase.CreateAsset(mat, matAssetPath);
                AssetDatabase.ImportAsset(matAssetPath);
            }

            // Layer on external PBR textures
            if (slotTextures.TryGetValue(matName, out var tList))
            {
                Debug.Log($"[AaronSetup] {matName}: {tList.Count} texture(s) found");
                ApplyTextures(mat, tList, matName);
            }
            else
            {
                Debug.LogWarning($"[AaronSetup] {matName}: no matching texture folder");
            }

            EditorUtility.SetDirty(mat);
            matPaths[matName] = matAssetPath;
        }

        AssetDatabase.SaveAssets();

        // 5. Remap FBX importer so future reimports use our materials
        var importer = AssetImporter.GetAtPath(FBX_PATH) as ModelImporter;
        if (importer != null)
        {
            foreach (var src in embedded)
            {
                if (!matPaths.TryGetValue(src.name, out string mp)) continue;
                if (HasExistingHairShaderMaterial(src.name))
                {
                    Debug.LogWarning($"[AaronSetup] Skipping FBX import remap for '{src.name}' — a proper hair-shader material already exists for this slot; remapping would make future reimports fall back to the plain placeholder.");
                    continue;
                }
                var loaded = AssetDatabase.LoadAssetAtPath<Material>(mp);
                if (loaded == null) continue;
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(src), loaded);
            }
            importer.SaveAndReimport();
        }

        // 6. Also directly update the scene instance right now
        var go = GameObject.Find("HD_Aaron");
        if (go != null)
        {
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mats = smr.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    // Never clobber a slot that's already using the real Reallusion hair
                    // shader (e.g. Brows/Eyelash _1st_Pass/_2nd_Pass) — those are correct
                    // as-is and must not be replaced by this tool's generic Lit fallback.
                    if (mats[i].shader != null && mats[i].shader.name.StartsWith(HAIR_SHADER_PREFIX))
                        continue;
                    if (matPaths.TryGetValue(mats[i].name, out string mp))
                    {
                        var replacement = AssetDatabase.LoadAssetAtPath<Material>(mp);
                        if (replacement != null) { mats[i] = replacement; changed = true; }
                    }
                }
                if (changed)
                {
                    Undo.RecordObject(smr, "Assign Aaron Materials");
                    smr.sharedMaterials = mats;
                }
            }
            EditorUtility.SetDirty(go);
        }
        else
        {
            Debug.LogWarning("[AaronSetup] HD_Aaron not found in scene — open the scene first.");
        }

        AssetDatabase.Refresh();
        Debug.Log("[AaronSetup] Done.");
    }

    static void ApplyTextures(Material mat, List<string> paths, string matName)
    {
        bool transparent =
            matName.Contains("Beard",      System.StringComparison.OrdinalIgnoreCase) ||
            matName.Contains("Brow",       System.StringComparison.OrdinalIgnoreCase) ||
            matName.Contains("Eyelash",    System.StringComparison.OrdinalIgnoreCase) ||
            matName.Contains("Tearline",   System.StringComparison.OrdinalIgnoreCase) ||
            matName.Contains("Occlusion",  System.StringComparison.OrdinalIgnoreCase) ||
            matName.Contains("Hair",       System.StringComparison.OrdinalIgnoreCase) ||
            matName.Contains("Transparency", System.StringComparison.OrdinalIgnoreCase);

        if (transparent) EnableTransparency(mat);

        string metallicAlphaPath = null;

        foreach (var path in paths)
        {
            string fn = Path.GetFileNameWithoutExtension(path);
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null) continue;

            if (EndsWith(fn, "_Diffuse") || EndsWith(fn, "_BaseColor") ||
                EndsWith(fn, "_Base_Color") || EndsWith(fn, "_BCBMap") ||
                EndsWith(fn, "_Sclera"))
            {
                SetTex(mat, "_BaseMap", tex);
                SetTex(mat, "_MainTex", tex);
            }
            else if (EndsWith(fn, "_NBMap") || EndsWith(fn, "_Normal") ||
                     EndsWith(fn, "_NormalMap") || EndsWith(fn, "_ScleraN") ||
                     EndsWith(fn, "_IrisN"))
            {
                if (mat.GetTexture("_BumpMap") == null)
                {
                    MarkAsNormal(path);
                    SetTex(mat, "_BumpMap", tex);
                    if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1f);
                    mat.EnableKeyword("_NORMALMAP");
                }
            }
            else if (EndsWith(fn, "_MetallicAlpha"))
            {
                metallicAlphaPath = path;
                SetTex(mat, "_MetallicGlossMap", tex);
                if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic", 0f);
                if (mat.HasProperty("_Smoothness"))  mat.SetFloat("_Smoothness", 1f);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            }
            else if (EndsWith(fn, "_ao") || EndsWith(fn, "_AO"))
            {
                SetTex(mat, "_OcclusionMap", tex);
                if (mat.HasProperty("_OcclusionStrength")) mat.SetFloat("_OcclusionStrength", 1f);
            }
        }

        // CC4 hair/brow "Transparency" materials don't ship a real diffuse map —
        // Reallusion's own hair shader (not present in this project) would combine
        // vertex color + flow/ID maps into procedural color instead. Without it these
        // render solid black (no _BaseMap, default black base color). As a quick
        // stand-in: tint the card a plausible hair/brow color and use the alpha
        // channel of the MetallicAlpha map (the only alpha-cutout mask CC4 exports
        // for these cards) to cut out the strand silhouette.
        if (transparent && mat.GetTexture("_BaseMap") == null && metallicAlphaPath != null)
        {
            if (HasExistingHairShaderMaterial(matName))
            {
                Debug.LogWarning($"[AaronSetup] {matName}: skipping black-tint fallback — a proper hair-shader material already exists for this slot (root '{ExtractRootToken(matName)}'). Leave '{matName}' unused and assign the *_Hair_Transparency / *_1st_Pass / *_2nd_Pass material(s) to the renderer instead.");
            }
            else
            {
                Texture2D alphaTex = AssetDatabase.LoadAssetAtPath<Texture2D>(metallicAlphaPath);
                SetTex(mat, "_BaseMap", alphaTex);
                SetTex(mat, "_MainTex", alphaTex);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", TintFor(matName));
                if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 1f);
                if (mat.HasProperty("_Cutoff"))    mat.SetFloat("_Cutoff", 0.5f);
                mat.EnableKeyword("_ALPHATEST_ON");
            }
        }
    }

    // Strips known descriptor suffixes to get a stable "identity" token, e.g.
    // "Brows_Color_Transparency" / "Brows_Base_Transparency" / "Brows_Hair_Transparency"
    // all reduce to "Brows".
    static readonly string[] SlotSuffixes =
    {
        "_Hair_Transparency_1st_Pass", "_Hair_Transparency_2nd_Pass", "_Hair_Transparency",
        "_Transparency_1st_Pass", "_Transparency_2nd_Pass",
        "_Color_Transparency", "_Base_Transparency", "_Transparency",
    };

    static string ExtractRootToken(string matName)
    {
        foreach (var suffix in SlotSuffixes)
            if (matName.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase))
                return matName.Substring(0, matName.Length - suffix.Length);
        return matName;
    }

    static bool HasExistingHairShaderMaterial(string matName)
    {
        string root = ExtractRootToken(matName);
        var guids = AssetDatabase.FindAssets("t:Material", new[] { MATERIALS_OUT });
        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string fileName = Path.GetFileNameWithoutExtension(path);
            if (!fileName.StartsWith(root, System.StringComparison.OrdinalIgnoreCase)) continue;

            var candidate = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (candidate != null && candidate.shader != null &&
                candidate.shader.name.StartsWith(HAIR_SHADER_PREFIX))
                return true;
        }
        return false;
    }

    static Color TintFor(string matName)
    {
        if (matName.Contains("Brow", System.StringComparison.OrdinalIgnoreCase))
            return new Color(0.12f, 0.08f, 0.05f, 1f);
        if (matName.Contains("Eyelash", System.StringComparison.OrdinalIgnoreCase))
            return new Color(0.05f, 0.05f, 0.05f, 1f);
        if (matName.Contains("Beard", System.StringComparison.OrdinalIgnoreCase))
            return new Color(0.10f, 0.08f, 0.07f, 1f);
        // Hair (incl. the "Clap" scalp-blend cap) — dark grey to match this
        // character's salt-and-pepper hair; tune in the Inspector if it looks off.
        return new Color(0.18f, 0.17f, 0.17f, 1f);
    }

    static void SetTex(Material mat, string prop, Texture2D tex)
    {
        if (mat.HasProperty(prop) && mat.GetTexture(prop) == null)
            mat.SetTexture(prop, tex);
    }

    static void MarkAsNormal(string path)
    {
        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti != null && ti.textureType != TextureImporterType.NormalMap)
        {
            ti.textureType = TextureImporterType.NormalMap;
            ti.SaveAndReimport();
        }
    }

    static void EnableTransparency(Material mat)
    {
        if (mat.HasProperty("_Surface"))  mat.SetFloat("_Surface", 1);
        if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 0);
        if (mat.HasProperty("_Blend"))    mat.SetFloat("_Blend", 0);
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
    }

    static bool EndsWith(string s, string suffix)
        => s.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase);
}
