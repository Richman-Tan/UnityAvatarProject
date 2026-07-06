using UnityEngine;
using UnityEditor;
using System.IO;
using System.Text;

public class DumpBlendshapes
{
    [MenuItem("Tools/Dump HD_Aaron Blendshapes")]
    static void Run()
    {
        var sb = new StringBuilder();

        // Find every SkinnedMeshRenderer in all loaded scenes
        var renderers = Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None);
        sb.AppendLine($"Total SkinnedMeshRenderers in scene: {renderers.Length}");

        foreach (var r in renderers)
        {
            var mesh = r.sharedMesh;
            if (mesh == null)
            {
                sb.AppendLine($"=== {r.name} — sharedMesh is NULL ===");
                continue;
            }
            sb.AppendLine($"=== {r.name} ({mesh.blendShapeCount} shapes) ===");
            for (int i = 0; i < mesh.blendShapeCount; i++)
                sb.AppendLine($"  {mesh.GetBlendShapeName(i)}");
        }

        string outPath = Path.Combine(Application.dataPath, "../blendshapes_dump.txt");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log($"[DumpBlendshapes] Written to {outPath}  ({renderers.Length} renderers found)");
        AssetDatabase.Refresh();
    }
}
