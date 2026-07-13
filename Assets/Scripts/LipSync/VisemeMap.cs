using System.Collections.Generic;

/// <summary>
/// Unity-side owner of the 14-key viseme → CC4 blendshape mapping.
///
/// Ported from the RN translator (src/services/avatarBridge/blendshapeTranslator.js
/// VISEME_TO_CC4) so the engine knows viseme IDENTITY (needed for co-articulation
/// classes and closure suppression), plus tongue-shape additions the JS table never
/// had — V_Tongue_* exist on CC_Base_Tongue/CC_Base_Body but were unused before.
///
/// `jaw_drive` is NOT a blendshape: AvatarController rotates CC_Base_JawRoot +
/// CC_Base_Teeth02 with it. Excluded as broken on this FBX export: Jaw_Open
/// (asymmetric), Mouth_LowerLip_Depress_R, Mouth_UpperLip_Raise_R.
/// </summary>
public static class VisemeMap
{
    public enum VisemeClass
    {
        Vowel,
        BilabialClosure,   // p/b/m — lips must fully touch
        LabiodentalClosure,// f/v — lower lip to upper teeth
        TongueConsonant,   // th/dd/nn/kk — tongue visible, mouth ajar
        Sibilant,          // ss/ch — narrow constriction
        Rhotic,            // rr
        Neutral,
    }

    public static readonly Dictionary<string, VisemeClass> Classes = new()
    {
        { "aa", VisemeClass.Vowel }, { "ih", VisemeClass.Vowel },
        { "ou", VisemeClass.Vowel }, { "ee", VisemeClass.Vowel },
        { "oh", VisemeClass.Vowel },
        { "v_pp", VisemeClass.BilabialClosure },
        { "v_ff", VisemeClass.LabiodentalClosure },
        { "v_th", VisemeClass.TongueConsonant },
        { "v_dd", VisemeClass.TongueConsonant },
        { "v_nn", VisemeClass.TongueConsonant },
        { "v_kk", VisemeClass.TongueConsonant },
        { "v_ss", VisemeClass.Sibilant },
        { "v_ch", VisemeClass.Sibilant },
        { "v_rr", VisemeClass.Rhotic },
        { "neutral", VisemeClass.Neutral },
    };

    /// <summary>Shapes that open the mouth — suppressed while a closure viseme is
    /// dominant so /p b m f v/ actually close instead of averaging half-open.</summary>
    public static readonly string[] OpenShapes =
    {
        "V_Open", "V_Lip_Open", "jaw_drive",
        "Mouth_Lips_Pull_DL", "Mouth_Lips_Pull_DR",
        "Mouth_Lips_Pull_UL", "Mouth_Lips_Pull_UR",
    };

    /// <summary>Shape driven to guaranteed contact for each closure class.</summary>
    public static readonly Dictionary<VisemeClass, string> ClosureShape = new()
    {
        { VisemeClass.BilabialClosure,    "V_Explosive"  },
        { VisemeClass.LabiodentalClosure, "V_Dental_Lip" },
    };

    public static readonly Dictionary<string, Dictionary<string, float>> Shapes = new()
    {
        ["aa"] = new()
        {
            { "jaw_drive", 1.00f }, { "V_Lip_Open", 0.90f }, { "V_Open", 0.80f },
            { "Mouth_Lips_Pull_DL", 0.45f }, { "Mouth_Lips_Pull_DR", 0.45f },
            { "Mouth_Lips_Pull_UL", 0.30f }, { "Mouth_Lips_Pull_UR", 0.30f },
        },
        ["ih"] = new()
        {
            { "jaw_drive", 0.45f }, { "V_Lip_Open", 0.42f }, { "V_Wide", 0.90f },
            { "Mouth_Lips_Pull_DL", 0.22f }, { "Mouth_Lips_Pull_DR", 0.22f },
            { "V_Tongue_up", 0.15f },
        },
        ["ou"] = new()
        {
            { "jaw_drive", 0.25f }, { "V_Lip_Open", 0.22f }, { "V_Tight_O", 1.00f },
        },
        ["ee"] = new()
        {
            // Front vowel: lips SPREAD (smile-wide), not tightened — per CC4/AccuLips,
            // front vowels drive V_Wide. Was V_Tight (narrow), which read too pursed.
            { "jaw_drive", 0.20f }, { "V_Lip_Open", 0.15f }, { "V_Wide", 0.90f },
            { "V_Open", 0.20f }, { "V_Tongue_up", 0.15f },
        },
        ["oh"] = new()
        {
            { "jaw_drive", 0.70f }, { "V_Lip_Open", 0.65f }, { "V_Open", 0.58f },
            { "Mouth_Lips_Pull_DL", 0.35f }, { "Mouth_Lips_Pull_DR", 0.35f },
            { "Mouth_Lips_Pull_UL", 0.22f }, { "Mouth_Lips_Pull_UR", 0.22f },
        },
        ["v_pp"] = new()
        {
            { "V_Explosive", 1.00f },
            { "Mouth_Lips_Press_L", 0.40f }, { "Mouth_Lips_Press_R", 0.40f },
        },
        ["v_ff"] = new()
        {
            { "V_Dental_Lip", 1.00f }, { "jaw_drive", 0.08f },
        },
        ["v_th"] = new()
        {
            // Slightly more open than the JS table had so the tongue tip is actually
            // visible between the teeth instead of moving behind closed lips.
            { "jaw_drive", 0.18f }, { "V_Lip_Open", 0.22f }, { "V_Open", 0.18f },
            // 0.60 keeps the tongue visible even at function-word weight scale
            // (0.60 × 0.85 × 0.68 ≈ 0.35) without looking exaggerated on stressed "th".
            { "V_Tongue_Out", 0.60f }, { "V_Tongue_up", 0.30f },
        },
        ["v_dd"] = new()
        {
            { "jaw_drive", 0.22f }, { "V_Lip_Open", 0.20f }, { "V_Open", 0.28f },
            { "Mouth_Lips_Pull_DL", 0.14f }, { "Mouth_Lips_Pull_DR", 0.14f },
            { "V_Tongue_up", 0.80f },
        },
        ["v_kk"] = new()
        {
            { "jaw_drive", 0.32f }, { "V_Lip_Open", 0.28f }, { "V_Open", 0.22f },
            { "Mouth_Lips_Pull_DL", 0.18f }, { "Mouth_Lips_Pull_DR", 0.18f },
            { "V_Tongue_Raise", 0.70f },
        },
        ["v_ch"] = new()
        {
            { "V_Affricate", 0.90f }, { "jaw_drive", 0.15f }, { "V_Lip_Open", 0.12f },
        },
        ["v_ss"] = new()
        {
            { "V_Tight", 0.35f }, { "jaw_drive", 0.08f },
        },
        ["v_nn"] = new()
        {
            { "Mouth_Lips_Press_L", 0.25f }, { "Mouth_Lips_Press_R", 0.25f },
            { "jaw_drive", 0.04f }, { "V_Tongue_up", 0.70f },
        },
        ["v_rr"] = new()
        {
            { "V_Tight_O", 0.48f }, { "jaw_drive", 0.16f }, { "V_Lip_Open", 0.16f },
            { "V_Tongue_Curl_U", 0.50f },
        },
        ["neutral"] = new(),
    };

    public static VisemeClass ClassOf(string viseme) =>
        Classes.TryGetValue(viseme, out var c) ? c : VisemeClass.Neutral;
}
