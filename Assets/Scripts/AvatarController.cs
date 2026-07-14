using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives CC4 character blendshapes from viseme/expression data.
/// Attach to the root GameObject of the CC4 character prefab.
///
/// Lip shapes are blendshape-driven (Mouth_Drop_Lower, Mouth_Up_Upper_L/R etc.).
/// Lower teeth follow via a small jaw bone rotation (jawTeethScale keeps it subtle).
/// All shared blendshape names (CC_Base_Body + CC_Base_Teeth + CC_Base_Tongue)
/// are driven together.
/// </summary>
public class AvatarController : MonoBehaviour
{
    [Header("Smoothing")]
    [Tooltip("Lerp time constant in seconds. 0.03 matches the RN app viseme smoother.")]
    public float smoothingTau = 0.03f;

    // Per-group overrides pushed by NativeBridgeReceiver from LipSyncTuning when a
    // baked timeline plays: the baked curves already contain attack/release
    // envelopes, so lip shapes want a much lighter tau than the generic one, while
    // the jaw wants a heavier one (more mass). Negative = fall back to smoothingTau.
    private float _lipTau = -1f;
    private float _jawTau = -1f;
    private readonly HashSet<string> _lipShapeNames = new();

    [Header("Jaw Bone (teeth only)")]
    [Tooltip("Main jaw bone — drives lower face skin.")]
    public string jawBoneName = "CC_Base_JawRoot";
    [Tooltip("Lower teeth bone inside the CC_Base_Teeth skeleton.")]
    public string lowerTeethBoneName = "CC_Base_Teeth02";
    [Tooltip("Max rotation in degrees. Keep at 0 for HD CC4 — jaw movement comes from blendshapes (V_Open etc.) only. " +
             "Set > 0 only if the character has no jaw-open blendshape and you want bone-driven movement.")]
    [Range(0f, 30f)]
    public float maxJawAngle = 0f;
    [Tooltip("Flip this if the jaw opens upward instead of downward.")]
    public bool  jawFlipDirection = false;

    [Header("Merged Open Mouth morph (CC5 'Mouth Open as Morph')")]
    [Tooltip("Blendshape that opens jaw+teeth+tongue as one baked morph. When this shape " +
             "exists on the mesh, jaw_drive drives IT instead of the jaw bone (set maxJawAngle=0).")]
    public string mergedOpenMouthShape = "Merged_Open_Mouth";
    [Tooltip("jaw_drive (0-1) maps to at most this morph weight. Real speech rarely fully " +
             "opens the jaw; ~0.6-0.7 reads natural, 1.0 looks like a yawn.")]
    [Range(0f, 1f)] public float mergedOpenMouthScale = 0.7f;
    private bool _hasMergedOpen;

    // Each facial sub-mesh carries its OWN copy of the merged open-mouth morph, but the
    // meshes extracted from the body (stubble/beard, brows, eyelashes) prefix the name:
    // StubbleMerged_Open_Mouth, Brows_HairMerged_Open_Mouth, Eyelash_LowMerged_Open_Mouth…
    // Driving only the exact "Merged_Open_Mouth" opened the body while the beard stayed
    // put and visibly detached. Collect every "*Merged_Open_Mouth" variant and drive them
    // together — each name is already in _shapeMap, so they ride the normal smoothing path.
    private readonly List<string> _mergedOpenNames = new();

    private Transform  _jawBone;
    private Quaternion _jawRest;
    private Transform  _lowerTeethBone;
    private Quaternion _lowerTeethRest;
    private float      _currentJawWeight;

    private struct ShapeTarget
    {
        public SkinnedMeshRenderer renderer;
        public int index;
    }

    // One blendshape name can map to multiple renderers (body + teeth + tongue share names)
    private readonly Dictionary<string, List<ShapeTarget>> _shapeMap = new();
    private readonly Dictionary<string, float>             _current  = new();

    // Two independent target channels so lipsync and idle animation (blink/brow/smile)
    // never clobber each other. Neither channel currently drives overlapping shape
    // names, so merging is a plain union — see BuildEffectiveTargets().
    private readonly Dictionary<string, float> _lipTarget  = new(); // viseme + jaw_drive, set by BlendshapeReceiver
    private readonly Dictionary<string, float> _idleTarget = new(); // blink/brow/smile, set by IdleAnimator
    private readonly Dictionary<string, float> _effectiveTarget = new();

    /// <summary>True while a lipsync segment has active (non-empty) target weights.
    /// Lets IdleAnimator derive a "speaking" signal without extra plumbing.</summary>
    public bool IsLipSyncActive => _lipTarget.Count > 0;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        BuildShapeMap();
        FindJawBone();

        // CC5 "Mouth Open as Morph" bakes jaw+teeth+tongue opening into one blendshape.
        // When present we drive it from jaw_drive instead of rotating the (skew-prone,
        // corrective-less) jaw bone. Smoothed on the lip channel like other lip shapes.
        // Gather the body's "Merged_Open_Mouth" plus every prefixed variant on the
        // extracted meshes (beard, brows, eyelashes) so they all open together. Each is
        // already keyed in _shapeMap by BuildShapeMap, so the normal Update/LateUpdate
        // smoothing loop writes them to their own renderer — no separate write path.
        foreach (var name in _shapeMap.Keys)
            if (name.EndsWith(mergedOpenMouthShape)) _mergedOpenNames.Add(name);
        _hasMergedOpen = _mergedOpenNames.Count > 0;
        foreach (var name in _mergedOpenNames) _lipShapeNames.Add(name);
    }

    void BuildShapeMap()
    {
        var renderers = GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
        int unique = 0;

        foreach (var r in renderers)
        {
            var mesh = r.sharedMesh;
            if (mesh == null) continue;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                if (!_shapeMap.TryGetValue(name, out var list))
                {
                    list = new List<ShapeTarget>(2);
                    _shapeMap[name] = list;
                    _current[name]  = 0f;
                    unique++;
                }
                list.Add(new ShapeTarget { renderer = r, index = i });
            }
        }

        Debug.Log($"[AvatarController] Mapped {unique} unique blendshapes across {renderers.Length} renderers.");
    }

    void FindJawBone()
    {
        _jawBone        = FindBone(jawBoneName, out _jawRest);
        _lowerTeethBone = FindBone(lowerTeethBoneName, out _lowerTeethRest);
        if (_jawBone == null)
            Debug.LogWarning($"[AvatarController] '{jawBoneName}' not found.");
        if (_lowerTeethBone == null)
            Debug.LogWarning($"[AvatarController] '{lowerTeethBoneName}' not found — check bone name in Inspector.");
    }

    /// <summary>
    /// Finds a bone anywhere under this character by exact name and captures its rest
    /// local rotation. Shared by jaw/teeth lookup above and by IdleAnimator for the
    /// head/eye/spine bones it drives (breathing, gaze, head tilts).
    /// </summary>
    public Transform FindBone(string name, out Quaternion restLocalRotation)
    {
        foreach (var t in GetComponentsInChildren<Transform>(includeInactive: true))
        {
            if (t.name == name)
            {
                restLocalRotation = t.localRotation;
                return t;
            }
        }
        restLocalRotation = Quaternion.identity;
        return null;
    }

    void Update()
    {
        // Merge the two target channels. No shape names currently overlap between
        // lipsync (visemes + jaw_drive) and idle (blink/brow/smile), so this is a
        // plain union — idle values win only if a future shape name were to collide.
        _effectiveTarget.Clear();
        foreach (var kvp in _lipTarget)  _effectiveTarget[kvp.Key] = kvp.Value;
        foreach (var kvp in _idleTarget) _effectiveTarget[kvp.Key] = kvp.Value;

        // Route jaw_drive onto the merged open-mouth morph (CC5 export). jaw_drive itself
        // is not a blendshape, so without this the mouth would only part its lips.
        if (_hasMergedOpen && _effectiveTarget.TryGetValue("jaw_drive", out var jawOpen))
        {
            float mergedW = Mathf.Clamp01(jawOpen * mergedOpenMouthScale);
            for (int i = 0; i < _mergedOpenNames.Count; i++)
                _effectiveTarget[_mergedOpenNames[i]] = mergedW;
        }

        // Advance the smoothed _current values toward _effectiveTarget each frame.
        // We do NOT call SetBlendShapeWeight here — the Animator runs its own
        // internal update AFTER script Update() and would silently overwrite any
        // weights written here.  The actual write happens in LateUpdate() below,
        // after the Animator has finished.
        float alphaGeneric = AlphaFor(smoothingTau);
        float alphaLip     = _lipTau > 0f ? AlphaFor(_lipTau) : alphaGeneric;

        foreach (var kvp in _effectiveTarget)
        {
            if (!_shapeMap.ContainsKey(kvp.Key)) continue;
            float alpha = _lipShapeNames.Contains(kvp.Key) ? alphaLip : alphaGeneric;
            _current[kvp.Key] = Mathf.Lerp(_current[kvp.Key], kvp.Value * 100f, alpha);
        }

        foreach (var name in _shapeMap.Keys)
        {
            if (_effectiveTarget.ContainsKey(name)) continue;
            float cur = _current[name];
            if (cur < 0.05f) { _current[name] = 0f; continue; }
            _current[name] = Mathf.Lerp(cur, 0f, _lipShapeNames.Contains(name) ? alphaLip : alphaGeneric);
        }
    }

    // Both blendshapes AND jaw bone are written here so they always win over the
    // Animator.  Unity's Animator writes its output (transforms + blendshapes) in
    // its own internal LateUpdate pass that runs before MonoBehaviour LateUpdate,
    // so anything written here is the final value before the frame renders.
    void LateUpdate()
    {
        // ── Blendshapes ──────────────────────────────────────────────────────────
        foreach (var name in _shapeMap.Keys)
        {
            float val = _current[name];
            foreach (var t in _shapeMap[name])
                t.renderer.SetBlendShapeWeight(t.index, val);
        }

        // Facial-hair (beard), brows and eyelashes follow the chin because each of
        // their own "*Merged_Open_Mouth" variants is driven above through _shapeMap —
        // no separate overlay pass needed; that was a weaker Jaw_Open approximation.

        // ── Jaw bone (optional — off by default for HD CC4) ──────────────────────
        // When maxJawAngle is 0 the jaw is driven entirely by blendshapes (V_Open
        // etc.) so we skip the bone write entirely.
        if (maxJawAngle <= 0f || _jawBone == null) return;

        float alpha = AlphaFor(_jawTau > 0f ? _jawTau : smoothingTau);

        float jawTarget = _lipTarget.GetValueOrDefault("jaw_drive", 0f);
        _currentJawWeight = Mathf.Lerp(_currentJawWeight, jawTarget, alpha);

        // CC4 jaw bone has Z=270° rest rotation, so local X points DOWN (world -Y).
        // Rotating around local X would spin the jaw like a propeller — wrong axis.
        // Local Y = world RIGHT (+X), which is the correct jaw hinge axis.
        // Negative local-Y rotation swings the chin down (mouth opens).
        // Tick jawFlipDirection if the jaw closes instead of opens.
        float sign     = jawFlipDirection ? 1f : -1f;
        float jawAngle = sign * maxJawAngle * _currentJawWeight;
        var   jawRot   = Quaternion.Euler(0f, jawAngle, 0f);

        _jawBone.localRotation = _jawRest * jawRot;

        // Lower teeth live in a separate CC_Base_Teeth skeleton and must be driven
        // independently — they don't inherit the jaw root's transform.
        if (_lowerTeethBone != null)
            _lowerTeethBone.localRotation = _lowerTeethRest * jawRot;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    float AlphaFor(float tau) => 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(tau, 0.001f));

    /// <summary>Lipsync channel — visemes + jaw_drive. Called by BlendshapeReceiver.</summary>
    public void SetTargetWeights(Dictionary<string, float> weights)
    {
        _lipTarget.Clear();
        foreach (var kvp in weights)
        {
            _lipTarget[kvp.Key] = Mathf.Clamp01(kvp.Value);
            _lipShapeNames.Add(kvp.Key); // shapes seen on the lip channel keep lip tau while decaying
        }
    }

    /// <summary>Pushes per-group smoothing taus from the co-articulation tuning asset.
    /// Called by NativeBridgeReceiver when a baked timeline starts.</summary>
    public void SetLipSmoothing(LipSyncTuning tuning)
    {
        _lipTau = tuning.lipSmoothingTau;
        _jawTau = tuning.jawSmoothingTau;
    }

    /// <summary>Snaps the smoothed lip-channel values straight to their current
    /// targets. Called once at baked-playback start so an utterance-initial closure
    /// (e.g. the M of "Maybe") doesn't lose its peak to smoothing ramp-up from 0 —
    /// the envelope's own attack already shaped the curve.</summary>
    public void SnapLipTargets()
    {
        foreach (var kvp in _lipTarget)
        {
            if (_shapeMap.ContainsKey(kvp.Key))
                _current[kvp.Key] = kvp.Value * 100f;
            else if (kvp.Key == "jaw_drive")
                _currentJawWeight = kvp.Value;
        }
        // Keep every merged open-mouth variant (body + beard + brows + eyelashes) in
        // lock-step with the jaw_drive snap so the beard opens with the body from frame 1.
        if (_hasMergedOpen && _lipTarget.TryGetValue("jaw_drive", out var jd))
        {
            float mergedW = Mathf.Clamp01(jd * mergedOpenMouthScale) * 100f;
            for (int i = 0; i < _mergedOpenNames.Count; i++)
                _current[_mergedOpenNames[i]] = mergedW;
        }
    }

    /// <summary>Clears the lipsync channel only — idle animation (blink/brow/smile)
    /// keeps running. Called by BlendshapeReceiver on a "clear" WS message.</summary>
    public void ResetAll()
    {
        _lipTarget.Clear();
    }

    /// <summary>Current smoothed weight (0–1) for a blendshape name, or the smoothed
    /// jaw drive when "jaw_drive" is requested. Diagnostic accessor for the lip-sync
    /// test harness (BlendshapeRecorder).</summary>
    public float GetCurrentWeight(string name)
    {
        if (name == "jaw_drive") return _currentJawWeight;
        return _current.TryGetValue(name, out var v) ? v / 100f : 0f;
    }

    /// <summary>Idle animation channel — blink/brow/smile. Called every frame by
    /// IdleAnimator with its complete current shape set (full-replace semantics).</summary>
    public void SetIdleWeights(Dictionary<string, float> weights)
    {
        _idleTarget.Clear();
        foreach (var kvp in weights)
            _idleTarget[kvp.Key] = Mathf.Clamp01(kvp.Value);
    }

    // ── Inspector helpers ─────────────────────────────────────────────────────

    [Header("Inspector Test")]
    public string testShapeName  = "Jaw_Open";
    [Range(0f, 1f)]
    public float  testShapeValue = 1f;

    [ContextMenu("Apply Test Shape")]
    void ApplyTestShape() =>
        SetTargetWeights(new Dictionary<string, float> { { testShapeName, testShapeValue } });

    [ContextMenu("Reset All Shapes")]
    void ResetAllShapes() => ResetAll();

    [ContextMenu("Test AA Viseme")]
    void TestAAViseme()
    {
        SetTargetWeights(new Dictionary<string, float>
        {
            { "jaw_drive",            1.00f },
            { "V_Lip_Open",           0.90f },
            { "V_Open",               0.80f },
            { "Mouth_Lips_Pull_DL",   0.45f },
            { "Mouth_Lips_Pull_DR",   0.45f },
            { "Mouth_Lips_Pull_UL",   0.30f },
            { "Mouth_Lips_Pull_UR",   0.30f },
        });
    }

    [ContextMenu("Test OH Viseme")]
    void TestOHViseme()
    {
        SetTargetWeights(new Dictionary<string, float>
        {
            { "jaw_drive",            0.70f },
            { "V_Lip_Open",           0.65f },
            { "V_Open",               0.58f },
            { "Mouth_Lips_Pull_DL",   0.35f },
            { "Mouth_Lips_Pull_DR",   0.35f },
            { "Mouth_Lips_Pull_UL",   0.22f },
            { "Mouth_Lips_Pull_UR",   0.22f },
        });
    }

    [ContextMenu("Test EE Viseme")]
    void TestEEViseme()
    {
        SetTargetWeights(new Dictionary<string, float>
        {
            { "jaw_drive",  0.18f },
            { "V_Lip_Open", 0.18f },
            { "V_Tight",    0.90f },
        });
    }

    // Prints every blendshape on every renderer — helps identify what CC_Base_Teeth has
    [ContextMenu("Dump Blendshapes Per Renderer")]
    void DumpBlendshapesPerRenderer()
    {
        var renderers = GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
        foreach (var r in renderers)
        {
            var mesh = r.sharedMesh;
            if (mesh == null || mesh.blendShapeCount == 0) continue;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{r.name}] {mesh.blendShapeCount} shapes:");
            for (int i = 0; i < mesh.blendShapeCount; i++)
                sb.AppendLine($"  {mesh.GetBlendShapeName(i)}");
            Debug.Log(sb.ToString());
        }
    }
}
