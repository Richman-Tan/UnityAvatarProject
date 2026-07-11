using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Samples the smoothed lip/tongue/jaw weights from AvatarController every frame
/// while armed. The buffer feeds LipSyncMetrics and can be dumped to CSV for
/// offline curve comparison between iterations.
/// </summary>
public class BlendshapeRecorder : MonoBehaviour
{
    /// <summary>Shapes sampled every frame. jaw_drive is the bone-drive channel.</summary>
    public static readonly string[] TrackedShapes =
    {
        "V_Lip_Open", "V_Open", "V_Wide", "V_Tight", "V_Tight_O",
        "V_Explosive", "V_Dental_Lip", "V_Affricate",
        "Mouth_Lips_Pull_DL", "Mouth_Lips_Pull_DR",
        "Mouth_Lips_Pull_UL", "Mouth_Lips_Pull_UR",
        "Mouth_Lips_Press_L", "Mouth_Lips_Press_R",
        "V_Tongue_up", "V_Tongue_Out", "V_Tongue_Raise",
        "V_Tongue_Curl_U", "V_Tongue_Curl_D",
        "jaw_drive",
    };

    public struct Sample
    {
        public float   t;
        public float[] w; // parallel to TrackedShapes
    }

    public readonly List<Sample> Samples = new();

    private AvatarController _avatar;
    private float _startTime;
    private bool  _recording;

    public void Begin(AvatarController avatar)
    {
        _avatar    = avatar;
        _startTime = Time.time;
        _recording = true;
        Samples.Clear();
    }

    public void End() => _recording = false;

    void LateUpdate()
    {
        // LateUpdate so we sample the same values AvatarController wrote this frame
        // (script execution order: AvatarController.LateUpdate runs first because it
        // was added to the scene earlier; the sub-frame difference is irrelevant at
        // the smoothing time constants involved).
        if (!_recording || _avatar == null) return;

        var w = new float[TrackedShapes.Length];
        for (int i = 0; i < TrackedShapes.Length; i++)
            w[i] = _avatar.GetCurrentWeight(TrackedShapes[i]);
        Samples.Add(new Sample { t = Time.time - _startTime, w = w });
    }

    public void WriteCsv(string path)
    {
        var sb = new StringBuilder();
        sb.Append("t,").AppendLine(string.Join(",", TrackedShapes));
        foreach (var s in Samples)
        {
            sb.Append(s.t.ToString("F4", CultureInfo.InvariantCulture));
            foreach (var v in s.w)
                sb.Append(',').Append(v.ToString("F4", CultureInfo.InvariantCulture));
            sb.AppendLine();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, sb.ToString());
    }

    public static int IndexOf(string shape) =>
        System.Array.IndexOf(TrackedShapes, shape);
}
