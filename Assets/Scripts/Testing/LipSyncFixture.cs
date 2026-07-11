using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Fixture metadata parsed from Assets/Tests/Fixtures/<name>.json.
/// The full fixture text is ALSO fed verbatim to NativeBridgeReceiver (it is a
/// superset of the native "play" message), so this class only needs the fields
/// the harness itself uses: duration + checks. JsonUtility skips the rest
/// (visemes/blendshapes) because they aren't declared here.
/// </summary>
[Serializable]
public class LipSyncFixture
{
    public string name;
    public string text;
    public float  duration;
    public List<LipSyncCheck> checks;

    /// <summary>Raw JSON, replayable through NativeBridgeReceiver.ReceiveBridgeMessage.</summary>
    [NonSerialized] public string rawJson;

    public static string FixturesDir => Path.Combine(Application.dataPath, "Tests/Fixtures");

    public static LipSyncFixture Load(string fixtureName)
    {
        string path = Path.Combine(FixturesDir, fixtureName + ".json");
        string json = File.ReadAllText(path);
        var fixture = JsonUtility.FromJson<LipSyncFixture>(json);
        fixture.rawJson = json;
        return fixture;
    }

    public static List<string> ListAll()
    {
        var names = new List<string>();
        foreach (var f in Directory.GetFiles(FixturesDir, "*.json"))
            names.Add(Path.GetFileNameWithoutExtension(f));
        names.Sort();
        return names;
    }
}

/// <summary>One assertion/screenshot point inside a fixture.</summary>
[Serializable]
public class LipSyncCheck
{
    public float  time;
    public string type;    // bilabial | labiodental | tongue | peak | silence | end
    public string viseme;  // for tongue/peak checks
    public string label;
}
