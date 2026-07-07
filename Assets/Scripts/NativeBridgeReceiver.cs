using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Production entry point for the native UaaL bridge (Phase 5) — the counterpart
/// to <see cref="BlendshapeReceiver"/>'s dev-only WebSocket relay path.
///
/// Unlike the WS relay (which streams pre-interpolated weights at 60fps), the
/// native bridge sends the FULL keyframe timeline in one message and lets Unity
/// own the playback clock — driving a 60fps loop across the native module bridge
/// per-call would be far more jittery/expensive than a real WebSocket. The
/// interpolation logic here is a direct port of `weightsAtTime()` from
/// unity-avatar/tools/ws-test-server.js, which was already proven correct in the
/// dev harness.
///
/// Called from native (Swift) via:
///   UnityFramework.sendMessageToGOWithName("HD_Aaron", "ReceiveBridgeMessage", json)
///
/// Message protocol:
///   { "type": "play", "startTimeUnityTime": 12.34, "duration": 3.2,
///     "blendshapes": [{"time": 0.0, "weights": {...}}, ...] }
///   { "type": "stop" }
/// </summary>
[RequireComponent(typeof(AvatarController))]
public class NativeBridgeReceiver : MonoBehaviour
{
    [Header("Debug")]
    public bool logFrames = false;

    private AvatarController _avatar;
    private List<(float time, Dictionary<string, float> weights)> _keyframes;
    private float _duration;
    private float _startTime;
    private bool  _playing;

    void Start()
    {
        // Walk up to the root AvatarController, same rationale as BlendshapeReceiver:
        // this component may sit on a child with an incomplete controller scope.
        _avatar = GetComponentInParent<AvatarController>();
        if (_avatar == null) _avatar = GetComponent<AvatarController>();
    }

    /// <summary>Entry point invoked by the native bridge via UnitySendMessage.</summary>
    public void ReceiveBridgeMessage(string json)
    {
        string type = CC4MessageProtocol.ParseStringField(json, "type");
        switch (type)
        {
            case "play":
            {
                _keyframes = CC4MessageProtocol.ParseKeyframeArray(json, "blendshapes");
                _duration  = CC4MessageProtocol.ParseFloatField(json, "duration");
                float anchor = CC4MessageProtocol.ParseFloatField(json, "startTimeUnityTime", -1f);
                _startTime = anchor >= 0f ? anchor : Time.time;
                _playing   = _keyframes != null && _keyframes.Count > 0 && _duration > 0f;
                if (logFrames)
                    Debug.Log($"[NativeBridgeReceiver] play: {_keyframes?.Count ?? 0} keyframes, duration {_duration:F2}s, anchor {_startTime:F2}");
                break;
            }

            case "stop":
                _playing = false;
                _avatar.ResetAll();
                if (logFrames) Debug.Log("[NativeBridgeReceiver] stop");
                break;

            default:
                if (logFrames) Debug.LogWarning($"[NativeBridgeReceiver] Unknown message type: '{type}'");
                break;
        }
    }

    void Update()
    {
        if (!_playing) return;

        float elapsed = Time.time - _startTime;
        if (elapsed >= _duration)
        {
            _playing = false;
            _avatar.ResetAll();
            return;
        }

        var weights = WeightsAtTime(elapsed);
        if (weights.Count == 0) _avatar.ResetAll();
        else _avatar.SetTargetWeights(weights);
    }

    /// <summary>Direct port of weightsAtTime() from ws-test-server.js — linear
    /// interpolation between the two keyframes bracketing `t`, clamped at the
    /// timeline's ends. No looping (that's a demo-mode-only concept, not needed
    /// for a real playback segment).</summary>
    Dictionary<string, float> WeightsAtTime(float t)
    {
        var result = new Dictionary<string, float>();
        if (_keyframes == null || _keyframes.Count == 0) return result;

        var prev = _keyframes[0];
        var next = _keyframes[_keyframes.Count - 1];
        for (int i = 0; i < _keyframes.Count - 1; i++)
        {
            if (t >= _keyframes[i].time && t < _keyframes[i + 1].time)
            {
                prev = _keyframes[i];
                next = _keyframes[i + 1];
                break;
            }
        }

        float span  = next.time - prev.time;
        float alpha = span > 0f ? (t - prev.time) / span : 1f;

        var keys = new HashSet<string>(prev.weights.Keys);
        keys.UnionWith(next.weights.Keys);
        foreach (var k in keys)
        {
            float a = prev.weights.TryGetValue(k, out var pv) ? pv : 0f;
            float b = next.weights.TryGetValue(k, out var nv) ? nv : 0f;
            float v = Mathf.Lerp(a, b, alpha);
            if (v > 0.0005f) result[k] = v;
        }
        return result;
    }
}
