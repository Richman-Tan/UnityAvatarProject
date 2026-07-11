using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Production entry point for the native UaaL bridge (Phase 5) — the counterpart
/// to <see cref="BlendshapeReceiver"/>'s dev-only WebSocket relay path.
///
/// Unlike the WS relay (which streams pre-interpolated weights at 60fps), the
/// native bridge sends the FULL timeline in one message and lets Unity own the
/// playback clock — driving a 60fps loop across the native module bridge
/// per-call would be far more jittery/expensive than a real WebSocket.
///
/// Called from native (Swift) via:
///   UnityFramework.sendMessageToGOWithName("HD_Aaron", "ReceiveBridgeMessage", json)
///
/// Message protocol:
///   { "type": "play", "startTimeUnityTime": 12.34, "duration": 3.2,
///     "visemes":     [{"t":0.1,"d":0.08,"v":"v_pp","w":0.95}, ...],   // preferred
///     "blendshapes": [{"time": 0.0, "weights": {...}}, ...] }         // legacy
///   { "type": "stop" }
///
/// When a `visemes` array is present, the raw 14-key timeline is baked ONCE
/// through <see cref="CoarticulationEngine"/> (dominance-envelope co-articulation,
/// tongue shapes, guaranteed bilabial closure) and the baked curves drive the
/// avatar. The legacy `blendshapes` keyframe-lerp path is kept for older
/// payloads and as a fallback.
/// </summary>
public class NativeBridgeReceiver : MonoBehaviour
{
    [Header("Co-articulation")]
    [Tooltip("Tuning asset for the co-articulation engine. Falls back to code defaults when unset.")]
    public LipSyncTuning tuning;

    [Header("Debug")]
    public bool logFrames = false;

    private AvatarController _avatar;
    private List<(float time, Dictionary<string, float> weights)> _keyframes;
    private CoarticulationEngine.BakedCurves _baked;
    private readonly Dictionary<string, float> _bakedWeights = new();
    private float _duration;
    private float _startTime;
    private bool  _playing;
    private bool  _snapOnFirstFrame;

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
                _duration = CC4MessageProtocol.ParseFloatField(json, "duration");
                float anchor = CC4MessageProtocol.ParseFloatField(json, "startTimeUnityTime", -1f);
                _startTime = anchor >= 0f ? anchor : Time.time;

                var visemes = CC4MessageProtocol.ParseVisemeArray(json);
                if (visemes != null && visemes.Count > 0)
                {
                    _baked     = CoarticulationEngine.Bake(visemes, _duration, tuning);
                    _keyframes = null;
                    // The baked tail includes the final release envelope; keep playing
                    // through it so the mouth closes smoothly instead of being cut off.
                    _duration  = Mathf.Max(_duration, _baked.duration);
                    _playing   = _duration > 0f;
                    _snapOnFirstFrame = true;
                    _avatar.SetLipSmoothing(tuning != null ? tuning : LipSyncTuning.Defaults);
                    if (logFrames)
                        Debug.Log($"[NativeBridgeReceiver] play (baked): {visemes.Count} visemes -> {_baked.FrameCount} frames, duration {_duration:F2}s");
                }
                else
                {
                    _keyframes = CC4MessageProtocol.ParseKeyframeArray(json, "blendshapes");
                    _baked     = null;
                    _playing   = _keyframes != null && _keyframes.Count > 0 && _duration > 0f;
                    if (logFrames)
                        Debug.Log($"[NativeBridgeReceiver] play (legacy): {_keyframes?.Count ?? 0} keyframes, duration {_duration:F2}s, anchor {_startTime:F2}");
                }
                break;
            }

            case "stop":
                _playing = false;
                _baked   = null;
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

        if (_baked != null)
        {
            _baked.WeightsAtTime(elapsed, _bakedWeights);
            if (_bakedWeights.Count == 0) _avatar.ResetAll();
            else _avatar.SetTargetWeights(_bakedWeights);
            if (_snapOnFirstFrame)
            {
                _snapOnFirstFrame = false;
                _avatar.SnapLipTargets();
            }
            return;
        }

        var weights = WeightsAtTime(elapsed);
        if (weights.Count == 0) _avatar.ResetAll();
        else _avatar.SetTargetWeights(weights);
    }

    /// <summary>Legacy path: linear interpolation between the two keyframes
    /// bracketing `t`, clamped at the timeline's ends (a direct port of
    /// weightsAtTime() from ws-test-server.js).</summary>
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
