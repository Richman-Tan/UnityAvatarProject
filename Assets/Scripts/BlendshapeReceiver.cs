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
        string type = CC4MessageProtocol.ParseStringField(json, "type");

        switch (type)
        {
            case "weights":
            {
                var weights = CC4MessageProtocol.ParseWeightsDict(json);
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
                string version = CC4MessageProtocol.ParseStringField(json, "version");
                Debug.Log($"[BlendshapeReceiver] Server ready (protocol v{version})");
                break;
            }

            default:
                // Unknown type — ignore silently to stay forward-compatible.
                if (logFrames) Debug.LogWarning($"[BlendshapeReceiver] Unknown message type: '{type}'");
                break;
        }
    }
}
