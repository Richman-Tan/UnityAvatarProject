using System.Collections.Generic;
using System.Text;
using UnityEngine;
// Requires NativeWebSocket — install once via:
//   Window → Package Manager → + (top-left) → Add package from git URL →
//   https://github.com/endel/NativeWebSocket.git#upm
using NativeWebSocket;

/// <summary>
/// Streams per-frame blendshape weights from a WebSocket server into AvatarController.
///
/// Server → Unity message protocol (v2):
///   { "type": "ready",   "version": 2 }                      — on connect
///   { "type": "weights", "weights": {"V_Open":0.85, ...} }   — at up to 60 fps
///   { "type": "clear" }                                       — segment finished; zero all weights
///
/// Attach to the same GameObject as AvatarController.
/// Set Server Url to ws://localhost:9001 for local testing.
/// </summary>
[RequireComponent(typeof(AvatarController))]
public class BlendshapeReceiver : MonoBehaviour
{
    [Header("WebSocket")]
    public string serverUrl     = "ws://localhost:9001";
    public float  reconnectDelay = 3f;

    [Header("Debug")]
    public bool logFrames = false; // enable to print every received frame

    private AvatarController _avatar;
    private WebSocket        _ws;
    private float            _reconnectTimer;
    private bool             _connecting;

    // WebSocket callbacks arrive on a background thread; enqueue for main-thread dispatch
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _msgQueue = new();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Start()
    {
        // Walk up to the root AvatarController (the one that covers all 17 renderers).
        // GetComponent() alone returns the controller on *this* GameObject, which may
        // be a child mesh with only 1 renderer and no jaw bone.
        _avatar = GetComponentInParent<AvatarController>();
        if (_avatar == null) _avatar = GetComponent<AvatarController>();
        Debug.Log($"[BlendshapeReceiver] Using AvatarController on '{_avatar.gameObject.name}'");

        _reconnectTimer = 0f;
        Connect();
    }

    async void Connect()
    {
        if (_connecting) return;
        _connecting = true;

        Debug.Log($"[BlendshapeReceiver] Connecting to {serverUrl}…");

        _ws = new WebSocket(serverUrl);
        _ws.OnOpen    += () => Debug.Log($"[BlendshapeReceiver] Connected.");
        _ws.OnError   += (e) => Debug.LogWarning($"[BlendshapeReceiver] Error: {e}");
        _ws.OnClose   += (_) => { Debug.Log("[BlendshapeReceiver] Disconnected."); _connecting = false; };
        _ws.OnMessage += (bytes) => _msgQueue.Enqueue(Encoding.UTF8.GetString(bytes));

        await _ws.Connect();
        _connecting = false;
    }

    void Update()
    {
        // NativeWebSocket requires this call on the main thread for non-WebGL builds
#if !UNITY_WEBGL || UNITY_EDITOR
        _ws?.DispatchMessageQueue();
#endif

        // Drain the message queue
        while (_msgQueue.TryDequeue(out var json))
            HandleMessage(json);

        // Auto-reconnect when disconnected
        if (_ws != null && _ws.State == WebSocketState.Closed && !_connecting)
        {
            _reconnectTimer -= Time.deltaTime;
            if (_reconnectTimer <= 0f)
            {
                _reconnectTimer = reconnectDelay;
                Connect();
            }
        }
    }

    async void OnDestroy()
    {
        if (_ws != null && _ws.State == WebSocketState.Open)
            await _ws.Close();
    }

    // ── Message handling ──────────────────────────────────────────────────────

    void HandleMessage(string json)
    {
        string type = ParseStringField(json, "type");

        switch (type)
        {
            case "weights":
            {
                var weights = ParseWeightsDict(json);
                // An empty weights dict is a valid silence frame — don't skip it;
                // ResetAll ensures the mouth closes cleanly between words.
                if (weights == null) return;
                if (weights.Count == 0) { _avatar.ResetAll(); return; }
                if (logFrames) Debug.Log($"[BlendshapeReceiver] weights: {weights.Count} shapes");
                _avatar.SetTargetWeights(weights);
                break;
            }

            case "clear":
                _avatar.ResetAll();
                if (logFrames) Debug.Log("[BlendshapeReceiver] clear — weights zeroed");
                break;

            case "ready":
            {
                string version = ParseStringField(json, "version");
                Debug.Log($"[BlendshapeReceiver] Server ready (protocol v{version})");
                break;
            }

            default:
                // Unknown type — ignore silently to stay forward-compatible.
                if (logFrames) Debug.LogWarning($"[BlendshapeReceiver] Unknown message type: '{type}'");
                break;
        }
    }

    /// <summary>
    /// Extracts a top-level string or number field from a flat JSON object without
    /// a Newtonsoft dependency.  Returns null if the field is absent.
    /// Works for both string values ("type":"weights") and number values ("version":2).
    /// </summary>
    static string ParseStringField(string json, string field)
    {
        string key   = "\"" + field + "\"";
        int    start = json.IndexOf(key, System.StringComparison.Ordinal);
        if (start < 0) return null;

        int colon = json.IndexOf(':', start + key.Length);
        if (colon < 0) return null;

        // Skip whitespace after the colon
        int valStart = colon + 1;
        while (valStart < json.Length && json[valStart] == ' ') valStart++;
        if (valStart >= json.Length) return null;

        if (json[valStart] == '"')
        {
            // Quoted string value
            int valEnd = json.IndexOf('"', valStart + 1);
            if (valEnd < 0) return null;
            return json.Substring(valStart + 1, valEnd - valStart - 1);
        }
        else
        {
            // Unquoted value (number, bool, null) — read until delimiter
            int valEnd = valStart;
            while (valEnd < json.Length && json[valEnd] != ',' && json[valEnd] != '}') valEnd++;
            return json.Substring(valStart, valEnd - valStart).Trim();
        }
    }

    /// <summary>
    /// Parses the "weights" object from a { "type": "weights", "weights": {...} } message.
    /// Returns null if no weights field is found; returns an empty dict for a silence frame.
    /// </summary>
    static Dictionary<string, float> ParseWeightsDict(string json)
    {
        int wStart = json.IndexOf("\"weights\"", System.StringComparison.Ordinal);
        if (wStart < 0) return null;

        int open  = json.IndexOf('{', wStart + 9);
        int close = json.IndexOf('}', open + 1);
        if (open < 0 || close < 0) return null;

        var    result = new Dictionary<string, float>(8);
        string inner  = json.Substring(open + 1, close - open - 1).Trim();
        if (inner.Length == 0) return result; // empty object = silence

        foreach (var pair in inner.Split(','))
        {
            int colon = pair.IndexOf(':');
            if (colon < 0) continue;
            string key    = pair.Substring(0, colon).Trim().Trim('"');
            string valStr = pair.Substring(colon + 1).Trim();
            if (float.TryParse(valStr,
                               System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture,
                               out float val))
                result[key] = val;
        }
        return result;
    }
}
