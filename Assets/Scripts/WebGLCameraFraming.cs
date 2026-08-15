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

        // A PLAIN GameObject on purpose: HideFlags.HideAndDontSave combined
        // with DontDestroyOnLoad stopped the component ticking past its first
        // frame in the build (it posed once, then the pose was never
        // re-applied). Verified in play mode with the real component.
        var poser = new GameObject("WebArmPoser");
        poser.AddComponent<WebArmPoser>();
        Debug.Log("[WebGLCameraFraming] web framing applied (vFOV " + VerticalFov + ")");
    }
#endif
}

/// <summary>
/// Swings T-pose arms down on each character as it becomes active.
/// Compiled for WebGL and for the EDITOR (so play mode can exercise the real
/// component — the WebGL-only Apply() above is the only thing that ever adds
/// it, so iOS/Android builds neither compile nor run any of this).
/// </summary>
public class WebArmPoser : MonoBehaviour
{
#if UNITY_WEBGL || UNITY_EDITOR
    const float ArmDownDegrees = 72f;
    static readonly string[] Roots = { "HD_Aaron", "HD_Ariana" };

    // The characters carry an enabled Animator with no controller, which
    // rewrites the bind pose every frame — a one-shot bone write is reverted
    // on the next tick (it survives in edit mode only because the Animator
    // doesn't run there). So the target LOCAL rotation is computed once and
    // re-applied in LateUpdate, after everything else has written: local, so
    // the arms still ride the torso when IdleAnimator breathes.
    readonly Dictionary<Transform, Quaternion> _target = new Dictionary<Transform, Quaternion>();
    readonly HashSet<Transform> _seen = new HashSet<Transform>();
    int _tick;

    void LateUpdate()
    {
        // GameObject.Find only sees ACTIVE objects, so a character switch is
        // picked up automatically. Scan EVERY frame until the first character
        // is posed — early frames crawl while the CC shaders compile, so a
        // every-15-frames scan left the T-pose on screen for minutes — then
        // back off, since a switch only needs picking up a few times a second.
        _tick++;
        if (_target.Count == 0 || _tick % 15 == 0)
        {
            for (int i = 0; i < Roots.Length; i++)
            {
                var root = GameObject.Find(Roots[i]);
                if (root == null || _seen.Contains(root.transform)) continue;
                _seen.Add(root.transform);
                var anim = root.GetComponent<Animator>();
                if (anim != null && anim.runtimeAnimatorController == null) anim.enabled = false;
                Capture(root.transform, "CC_Base_L_Upperarm", "CC_Base_L_Forearm");
                Capture(root.transform, "CC_Base_R_Upperarm", "CC_Base_R_Forearm");
                Debug.Log("[WebArmPoser] " + Roots[i] + " arms posed=" + _target.Count);
            }
        }

        foreach (var kv in _target)
        {
            if (kv.Key != null) kv.Key.localRotation = kv.Value;
        }
    }

    void Capture(Transform root, string upperName, string lowerName)
    {
        var upper = FindDeep(root, upperName);
        if (upper == null) return;
        var lower = FindDeep(root, lowerName);
        Vector3 dir = lower != null ? (lower.position - upper.position).normalized : upper.right;
        // T-pose arms point along ±X; swing them down about the world Z axis.
        float sign = dir.x >= 0f ? -1f : 1f;
        var before = upper.localRotation;
        upper.rotation = Quaternion.AngleAxis(sign * ArmDownDegrees, Vector3.forward) * upper.rotation;
        _target[upper] = upper.localRotation;
        upper.localRotation = before; // LateUpdate below applies the target
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
