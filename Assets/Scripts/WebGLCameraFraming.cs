using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WebGL-only scene setup for the web app's full-screen "avatar room".
/// Two fixes, both WEB ONLY — iOS/Android keep their existing look because
/// the whole body is compile-time gated and the shared scene file is never
/// modified (applied via RuntimeInitializeOnLoadMethod, no component to add):
///
/// 1. CAMERA. The camera ships as a physical camera with HORIZONTAL gate fit
///    tuned for the mobile embeds, so a wide browser canvas fixes the
///    horizontal span and zooms into the face. The web build switches to a
///    plain vertical-FOV camera, steps back and drops slightly for a
///    head-to-hip video-call shot that fills any window shape.
///
/// 2. ARM POSE. The characters have an Animator with NO controller, so they
///    render their bind (T-)pose. The mobile framing crops the arms away;
///    full-screen framing shows them, so the upper-arm bones are swung down
///    to a natural rest pose. Applied per character the first time it is
///    active (AvatarRouter activates Ariana later), setting WORLD rotation
///    once so the arms still follow the torso when IdleAnimator breathes.
///
/// Values validated with Assets/Scripts/Editor/WebPreviewShot.cs.
/// </summary>
public static class WebGLCameraFraming
{
#if UNITY_WEBGL && !UNITY_EDITOR
    const float VerticalFov = 25f;
    const float StepBackMeters = 1.6f;
    const float DropMeters = 0.13f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Apply()
    {
        var go = GameObject.Find("render_focus_");
        var cam = go != null ? go.GetComponent<Camera>() : Camera.main;
        if (cam != null)
        {
            cam.usePhysicalProperties = false; // fieldOfView becomes the vertical FOV
            cam.fieldOfView = VerticalFov;
            cam.transform.position -= cam.transform.forward * StepBackMeters + Vector3.up * DropMeters;
        }

        var poser = new GameObject("WebArmPoser");
        poser.hideFlags = HideFlags.HideAndDontSave;
        poser.AddComponent<WebArmPoser>();
        Object.DontDestroyOnLoad(poser);
        Debug.Log("[WebGLCameraFraming] web framing applied (vFOV " + VerticalFov + ")");
    }
#endif
}

/// <summary>Swings T-pose arms down on each character as it becomes active.</summary>
public class WebArmPoser : MonoBehaviour
{
#if UNITY_WEBGL && !UNITY_EDITOR
    const float ArmDownDegrees = 72f;
    static readonly string[] Roots = { "HD_Aaron", "HD_Ariana" };

    readonly HashSet<Transform> _posed = new HashSet<Transform>();
    int _tick;

    void LateUpdate()
    {
        // GameObject.Find only sees ACTIVE objects, so a character switch is
        // picked up automatically. Scanning a few times a second is plenty.
        if (++_tick % 15 != 0) return;
        for (int i = 0; i < Roots.Length; i++)
        {
            var root = GameObject.Find(Roots[i]);
            if (root == null || _posed.Contains(root.transform)) continue;
            PoseArm(root.transform, "CC_Base_L_Upperarm", "CC_Base_L_Forearm");
            PoseArm(root.transform, "CC_Base_R_Upperarm", "CC_Base_R_Forearm");
            _posed.Add(root.transform);
        }
    }

    void PoseArm(Transform root, string upperName, string lowerName)
    {
        var upper = FindDeep(root, upperName);
        if (upper == null) return;
        var lower = FindDeep(root, lowerName);
        Vector3 dir = lower != null ? (lower.position - upper.position).normalized : upper.right;
        // T-pose arms point along ±X; swing them down about the world Z axis.
        float sign = dir.x >= 0f ? -1f : 1f;
        upper.rotation = Quaternion.AngleAxis(sign * ArmDownDegrees, Vector3.forward) * upper.rotation;
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
#endif
}
