using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Dev-only: renders the scene camera to a PNG with the WEB framing/pose
/// applied, so web camera work can be iterated in seconds instead of
/// 20-minute WebGL export cycles. Everything is restored afterwards — the
/// scene file is never dirtied.
///
/// Call via MCP:
///   WebPreviewShot.Shoot(vfov, stepBack, armDownDegrees, width, height, path)
/// </summary>
public static class WebPreviewShot
{
    public static string Shoot(float vfov, float stepBack, float armDown, int width, int height, string path)
    {
        return Shoot(vfov, stepBack, 0f, armDown, width, height, path);
    }

    public static string Shoot(float vfov, float stepBack, float drop, float armDown, int width, int height, string path)
    {
        var camGo = GameObject.Find("render_focus_");
        if (camGo == null) return "render_focus_ not found";
        var cam = camGo.GetComponent<Camera>();
        if (cam == null) return "no Camera";

        // ── save state ────────────────────────────────────────────────────
        bool oldPhysical = cam.usePhysicalProperties;
        float oldFov = cam.fieldOfView;
        Vector3 oldPos = cam.transform.position;
        var restore = new System.Collections.Generic.List<Transform>();
        var restoreRot = new System.Collections.Generic.List<Quaternion>();

        // ── apply web framing ─────────────────────────────────────────────
        cam.usePhysicalProperties = false;
        cam.fieldOfView = vfov;
        cam.transform.position = oldPos - cam.transform.forward * stepBack - Vector3.up * drop;

        // ── pose arms down on every character root in the scene ───────────
        string[] roots = { "HD_Aaron", "HD_Ariana" };
        for (int i = 0; i < roots.Length; i++)
        {
            var root = GameObject.Find(roots[i]);
            if (root == null) continue;
            PoseArm(root.transform, "CC_Base_L_Upperarm", "CC_Base_L_Forearm", armDown, restore, restoreRot);
            PoseArm(root.transform, "CC_Base_R_Upperarm", "CC_Base_R_Forearm", armDown, restore, restoreRot);
        }

        // Edit mode skins from bone matrices cached by the editor's own tick,
        // so a same-call-stack render would show the old (bind) pose.
        var smrs = Object.FindObjectsOfType<SkinnedMeshRenderer>();
        for (int i = 0; i < smrs.Length; i++) smrs[i].forceMatrixRecalculationPerRender = true;

        // ── render ────────────────────────────────────────────────────────
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        rt.antiAliasing = 2;
        var prevTarget = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        cam.targetTexture = prevTarget;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        rt.Release();
        Object.DestroyImmediate(rt);

        // ── restore ───────────────────────────────────────────────────────
        for (int i = 0; i < restore.Count; i++) restore[i].rotation = restoreRot[i];
        cam.usePhysicalProperties = oldPhysical;
        cam.fieldOfView = oldFov;
        cam.transform.position = oldPos;

        return "wrote " + path + " (vfov=" + vfov + " back=" + stepBack + " drop=" + drop + " arm=" + armDown + " posed=" + restore.Count + ")";
    }

    /// <summary>Reports whether the bone we rotate is the one the mesh is skinned to.</summary>
    public static string Diagnose(string rootName, string boneName)
    {
        var root = GameObject.Find(rootName);
        if (root == null) return rootName + " not found";
        var sb = new System.Text.StringBuilder();
        sb.Append("playing=" + Application.isPlaying);
        var anim = root.GetComponent<Animator>();
        sb.Append(" animator=" + (anim == null ? "none"
            : ("enabled=" + anim.enabled + " ctrl=" + (anim.runtimeAnimatorController == null ? "NULL" : "set")
               + " optimized=" + (anim.avatar != null && anim.isOptimizable) + " hasTransformHierarchy=" + anim.hasTransformHierarchy)));

        var bone = FindDeep(root.transform, boneName);
        sb.Append(" || bone=" + (bone == null ? "NOT FOUND" : boneName + " parent=" + bone.parent.name));

        var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        sb.Append(" || smrCount=" + smrs.Length);
        int matched = 0;
        for (int i = 0; i < smrs.Length; i++)
        {
            var bones = smrs[i].bones;
            for (int b = 0; b < bones.Length; b++)
            {
                if (bone != null && bones[b] == bone) { matched++; break; }
            }
        }
        sb.Append(" boundToRotatedBone=" + matched + "/" + smrs.Length);
        return sb.ToString();
    }

    static void PoseArm(Transform root, string upperName, string lowerName, float degrees,
        System.Collections.Generic.List<Transform> restore,
        System.Collections.Generic.List<Quaternion> restoreRot)
    {
        var upper = FindDeep(root, upperName);
        if (upper == null) return;
        var lower = FindDeep(root, lowerName);
        Vector3 dir = lower != null
            ? (lower.position - upper.position).normalized
            : upper.right;
        // T-pose arms point along ±X; rotate about world Z to swing them down.
        float sign = dir.x >= 0f ? -1f : 1f;
        restore.Add(upper);
        restoreRot.Add(upper.rotation);
        upper.rotation = Quaternion.AngleAxis(sign * degrees, Vector3.forward) * upper.rotation;
    }

    static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++)
        {
            var found = FindDeep(t.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }
}
