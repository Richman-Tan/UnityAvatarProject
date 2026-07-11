using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Shared hand-rolled JSON helpers for the CC4 avatar message protocol (no
/// Newtonsoft dependency, matching the rest of this project's WS/native bridge
/// code). Used by both <see cref="BlendshapeReceiver"/> (dev WebSocket relay,
/// live 60fps stream) and <see cref="NativeBridgeReceiver"/> (production UaaL
/// native bridge, one message per audio segment) so the two paths can never
/// drift apart on parsing behavior.
/// </summary>
public static class CC4MessageProtocol
{
    /// <summary>
    /// Extracts a top-level string or number field from a flat JSON object.
    /// Returns null if the field is absent. Works for both string values
    /// ("type":"weights") and number values ("version":2).
    /// </summary>
    public static string ParseStringField(string json, string field)
    {
        string key   = "\"" + field + "\"";
        int    start = json.IndexOf(key, System.StringComparison.Ordinal);
        if (start < 0) return null;

        int colon = json.IndexOf(':', start + key.Length);
        if (colon < 0) return null;

        int valStart = colon + 1;
        while (valStart < json.Length && json[valStart] == ' ') valStart++;
        if (valStart >= json.Length) return null;

        if (json[valStart] == '"')
        {
            int valEnd = json.IndexOf('"', valStart + 1);
            if (valEnd < 0) return null;
            return json.Substring(valStart + 1, valEnd - valStart - 1);
        }
        else
        {
            int valEnd = valStart;
            while (valEnd < json.Length && json[valEnd] != ',' && json[valEnd] != '}' && json[valEnd] != ']') valEnd++;
            return json.Substring(valStart, valEnd - valStart).Trim();
        }
    }

    /// <summary>Convenience wrapper over <see cref="ParseStringField"/> for numeric fields.</summary>
    public static float ParseFloatField(string json, string field, float defaultValue = 0f)
    {
        string raw = ParseStringField(json, field);
        if (raw == null) return defaultValue;
        return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : defaultValue;
    }

    /// <summary>
    /// Parses a flat `"fieldName": {"shape": 0.0-1.0, ...}` object into a dict.
    /// Returns null if the field is absent; returns an empty dict for `{}` (a
    /// valid silence frame — callers should treat that as "reset all", not skip).
    /// </summary>
    public static Dictionary<string, float> ParseWeightsDict(string json, string fieldName = "weights")
    {
        string key   = "\"" + fieldName + "\"";
        int    wStart = json.IndexOf(key, System.StringComparison.Ordinal);
        if (wStart < 0) return null;

        int open  = json.IndexOf('{', wStart + key.Length);
        int close = FindMatchingBrace(json, open);
        if (open < 0 || close < 0) return null;

        var    result = new Dictionary<string, float>(8);
        string inner  = json.Substring(open + 1, close - open - 1).Trim();
        if (inner.Length == 0) return result; // empty object = silence

        foreach (var pair in SplitTopLevel(inner, ','))
        {
            int colon = pair.IndexOf(':');
            if (colon < 0) continue;
            string keyName = pair.Substring(0, colon).Trim().Trim('"');
            string valStr  = pair.Substring(colon + 1).Trim();
            if (float.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                result[keyName] = val;
        }
        return result;
    }

    /// <summary>
    /// Parses a `"fieldName": [{"time": 0.0, "weights": {...}}, ...]` array —
    /// the CC4AudioPayload.blendshapes shape — into a time-ordered keyframe list.
    /// Returns null if the field is absent.
    /// </summary>
    public static List<(float time, Dictionary<string, float> weights)> ParseKeyframeArray(string json, string fieldName)
    {
        string key    = "\"" + fieldName + "\"";
        int    kStart = json.IndexOf(key, System.StringComparison.Ordinal);
        if (kStart < 0) return null;

        int open  = json.IndexOf('[', kStart + key.Length);
        int close = FindMatchingBracket(json, open);
        if (open < 0 || close < 0) return null;

        string inner  = json.Substring(open + 1, close - open - 1).Trim();
        var    result = new List<(float, Dictionary<string, float>)>();
        if (inner.Length == 0) return result;

        foreach (var element in SplitTopLevel(inner, ','))
        {
            float time = ParseFloatField(element, "time");
            var   w    = ParseWeightsDict(element, "weights") ?? new Dictionary<string, float>();
            result.Add((time, w));
        }
        return result;
    }

    /// <summary>
    /// Parses a `"fieldName": [{"t":0.1,"d":0.08,"v":"v_pp","w":0.95}, ...]` array —
    /// the raw 14-key viseme event timeline added to the play message for the
    /// Unity-side co-articulation engine. Returns null if the field is absent
    /// (legacy payloads), letting callers fall back to the keyframe path.
    /// </summary>
    public static List<CoarticulationEngine.VisemeEvent> ParseVisemeArray(string json, string fieldName = "visemes")
    {
        string key    = "\"" + fieldName + "\"";
        int    kStart = json.IndexOf(key, System.StringComparison.Ordinal);
        if (kStart < 0) return null;

        int open  = json.IndexOf('[', kStart + key.Length);
        int close = FindMatchingBracket(json, open);
        if (open < 0 || close < 0) return null;

        string inner  = json.Substring(open + 1, close - open - 1).Trim();
        var    result = new List<CoarticulationEngine.VisemeEvent>();
        if (inner.Length == 0) return result;

        foreach (var element in SplitTopLevel(inner, ','))
        {
            result.Add(new CoarticulationEngine.VisemeEvent
            {
                t = ParseFloatField(element, "t"),
                d = ParseFloatField(element, "d"),
                v = ParseStringField(element, "v") ?? "neutral",
                w = ParseFloatField(element, "w", 1f),
            });
        }
        return result;
    }

    // ── Brace/bracket-depth scanning ─────────────────────────────────────────
    // Needed because keyframe elements themselves contain a nested `weights: {}`
    // object — a naive split on the first '}' or top-level ',' would cut a
    // keyframe in half. These treat '{'/'}' and '['/']' as depth-changing on
    // BOTH kinds of brackets, since a keyframe array element can contain both.

    static int FindMatchingBrace(string json, int openIndex) => FindMatching(json, openIndex, '{', '}');
    static int FindMatchingBracket(string json, int openIndex) => FindMatching(json, openIndex, '[', ']');

    static int FindMatching(string json, int openIndex, char open, char close)
    {
        if (openIndex < 0 || openIndex >= json.Length || json[openIndex] != open) return -1;
        int depth = 0;
        for (int i = openIndex; i < json.Length; i++)
        {
            if (json[i] == '{' || json[i] == '[') depth++;
            else if (json[i] == '}' || json[i] == ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>Splits on `separator` only at bracket/brace depth 0, so nested
    /// objects/arrays inside each element are kept intact.</summary>
    static List<string> SplitTopLevel(string s, char separator)
    {
        var result = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') depth--;
            else if (c == separator && depth == 0)
            {
                result.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        result.Add(s.Substring(start));
        return result;
    }
}
