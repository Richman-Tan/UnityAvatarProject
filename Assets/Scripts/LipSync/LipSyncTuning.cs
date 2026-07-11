using UnityEngine;

/// <summary>
/// All tunables of the co-articulation engine in one asset so iteration on feel
/// never requires a recompile — values can be tweaked live from the Inspector or
/// via MCP execute_code between test runs.
///
/// Times are seconds. "Closure" = bilabial (p/b/m) and labiodental (f/v) visemes,
/// which need faster, fuller articulation than vowels.
/// </summary>
[CreateAssetMenu(fileName = "LipSyncTuning", menuName = "LipSync/Tuning")]
public class LipSyncTuning : ScriptableObject
{
    [Header("Anticipation (envelope starts this long BEFORE the acoustic onset)")]
    [Range(0f, 0.2f)] public float vowelAnticipation   = 0.05f;
    [Range(0f, 0.2f)] public float closureAnticipation = 0.08f;
    [Range(0f, 0.2f)] public float otherAnticipation   = 0.05f;

    [Header("Release (envelope decays this long after the phoneme ends)")]
    [Range(0f, 0.3f)] public float vowelRelease   = 0.12f;
    [Range(0f, 0.3f)] public float closureRelease = 0.06f;
    [Range(0f, 0.3f)] public float otherRelease   = 0.10f;

    [Header("Closure guarantee")]
    [Tooltip("Bilabial/labiodental dominance is forced to at least this peak.")]
    [Range(0.5f, 1f)] public float closureMinPeak = 0.95f;
    [Tooltip("How strongly open shapes are cancelled while a closure is dominant.")]
    [Range(0f, 1f)] public float suppressionStrength = 0.90f;

    [Header("Tongue")]
    [Tooltip("Minimum effective weight for V_Tongue_* shapes. Stress reduction shrinks mouth OPENING, not tongue contact — /th/ still puts the tongue between the teeth in an unstressed 'the'.")]
    [Range(0f, 1f)] public float tongueMinWeight = 0.70f;

    [Header("Dominance blending")]
    [Tooltip("Exponent on dominance when averaging overlapping visemes. >1 sharpens.")]
    [Range(1f, 3f)] public float dominancePower = 1.5f;

    [Header("Bake")]
    [Tooltip("Samples per second of the baked weight curves.")]
    [Range(30, 120)] public int sampleRate = 90;

    [Header("AvatarController smoothing overrides (seconds)")]
    [Tooltip("Tau for viseme/lip shapes when playing a baked timeline. The baked curves already contain attack/release envelopes, so this only bridges frame timing.")]
    [Range(0.005f, 0.1f)] public float lipSmoothingTau = 0.015f;
    [Tooltip("Tau for the jaw bone drive — heavier than lips (jaw has more mass).")]
    [Range(0.005f, 0.15f)] public float jawSmoothingTau = 0.05f;

    static LipSyncTuning _defaults;

    /// <summary>Code defaults, used when no asset is assigned in the scene.</summary>
    public static LipSyncTuning Defaults
    {
        get
        {
            if (_defaults == null)
                _defaults = CreateInstance<LipSyncTuning>();
            return _defaults;
        }
    }

    public float AnticipationFor(VisemeMap.VisemeClass cls) => cls switch
    {
        VisemeMap.VisemeClass.Vowel => vowelAnticipation,
        VisemeMap.VisemeClass.BilabialClosure => closureAnticipation,
        VisemeMap.VisemeClass.LabiodentalClosure => closureAnticipation,
        _ => otherAnticipation,
    };

    public float ReleaseFor(VisemeMap.VisemeClass cls) => cls switch
    {
        VisemeMap.VisemeClass.Vowel => vowelRelease,
        VisemeMap.VisemeClass.BilabialClosure => closureRelease,
        VisemeMap.VisemeClass.LabiodentalClosure => closureRelease,
        _ => otherRelease,
    };
}
