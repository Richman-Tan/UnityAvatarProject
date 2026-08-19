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
/// Reached from native (Swift) via AvatarRouter — the bridge sends to
///   UnityFramework.sendMessageToGOWithName("AvatarRouter", "ReceiveBridgeMessage", json)
/// and <see cref="AvatarRouter"/> forwards play/stop to the ACTIVE character's
/// receiver with a direct C# call.
///
/// Message protocol:
///   { "type": "play", "startTimeUnityTime": 12.34, "duration": 3.2,
///     "emotion":     "warm",                                          // sentence sentiment
///     "visemes":     [{"t":0.1,"d":0.08,"v":"v_pp","w":0.95}, ...],   // preferred
///     "blendshapes": [{"time": 0.0, "weights": {...}}, ...] }         // legacy
///   { "type": "stop" }
///   { "type": "setState", "state": "listening" }   // idle|listening|speaking|thinking|empathy|waiting
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
    private IdleAnimator _idle;
    private List<(float time, Dictionary<string, float> weights)> _keyframes;
    private CoarticulationEngine.BakedCurves _baked;
    private readonly Dictionary<string, float> _bakedWeights = new();
    private float _duration;
    private float _startTime;
    private bool  _playing;
    private bool  _snapOnFirstFrame;

    void Start()
    {
        ResolveAvatar();
    }

    // Walk up to the root AvatarController, same rationale as BlendshapeReceiver:
    // this component may sit on a child with an incomplete controller scope.
    // Lazy (not only Start): AvatarRouter direct-calls ReceiveBridgeMessage the
    // moment it activates a character, which can be before Start has run on a
    // root that shipped disabled in the baked scene.
    void ResolveAvatar()
    {
        if (_avatar != null) return;
        _avatar = GetComponentInParent<AvatarController>();
        if (_avatar == null) _avatar = GetComponent<AvatarController>();
        // IdleAnimator is [RequireComponent(typeof(AvatarController))], so it always
        // sits on whichever GameObject the controller resolved to.
        if (_avatar != null) _idle = _avatar.GetComponent<IdleAnimator>();
    }

    /// <summary>Entry point invoked by the native bridge via UnitySendMessage.</summary>
    public void ReceiveBridgeMessage(string json)
    {
        ResolveAvatar();
        if (_avatar == null)
        {
            Debug.LogWarning("[NativeBridgeReceiver] No AvatarController found — dropping message.");
            return;
        }

        string type = CC4MessageProtocol.ParseStringField(json, "type");
        switch (type)
        {
            case "play":
            {
                _duration = CC4MessageProtocol.ParseFloatField(json, "duration");
                float anchor = CC4MessageProtocol.ParseFloatField(json, "startTimeUnityTime", -1f);
                _startTime = anchor >= 0f ? anchor : Time.time;

                // Sentence sentiment has always been in this payload; nothing read
                // it until now, so detectSentiment's output reached nothing and the
                // face stayed flat no matter what was being said.
                if (_idle != null)
                    _idle.SetSpeechEmotion(CC4MessageProtocol.ParseStringField(json, "emotion"));

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
                    // Turn-taking gaze keys off the utterance envelope, so the
                    // animator needs the final duration (baked tail included).
                    if (_idle != null) _idle.OnUtteranceStart(_duration);
                    if (logFrames)
                        Debug.Log($"[NativeBridgeReceiver] play (baked): {visemes.Count} visemes -> {_baked.FrameCount} frames, duration {_duration:F2}s");
                }
                else
                {
                    _keyframes = CC4MessageProtocol.ParseKeyframeArray(json, "blendshapes");
                    _baked     = null;
                    _playing   = _keyframes != null && _keyframes.Count > 0 && _duration > 0f;
                    if (_playing && _idle != null) _idle.OnUtteranceStart(_duration);
                    if (logFrames)
                        Debug.Log($"[NativeBridgeReceiver] play (legacy): {_keyframes?.Count ?? 0} keyframes, duration {_duration:F2}s, anchor {_startTime:F2}");
                }
                break;
            }

            case "stop":
                _playing = false;
                _baked   = null;
                _avatar.ResetAll();
                // Don't leave the last sentence's sentiment held on the face.
                if (_idle != null) { _idle.SetSpeechEmotion(null); _idle.OnUtteranceEnd(); }
                if (logFrames) Debug.Log("[NativeBridgeReceiver] stop");
                break;

            case "setState":
            {
                string raw = CC4MessageProtocol.ParseStringField(json, "state");
                if (_idle == null)
                {
                    Debug.LogWarning("[NativeBridgeReceiver] setState but no IdleAnimator on the character root.");
                }
                else if (TryParseAvatarState(raw, out var parsed))
                {
                    _idle.state = parsed;
                    if (logFrames) Debug.Log($"[NativeBridgeReceiver] setState: {parsed}");
                }
                else
                {
                    Debug.LogWarning($"[NativeBridgeReceiver] setState with unrecognised state '{raw}' — ignoring.");
                }
                break;
            }

            default:
                if (logFrames) Debug.LogWarning($"[NativeBridgeReceiver] Unknown message type: '{type}'");
                break;
        }
    }

    /// <summary>
    /// Maps the bridge's lowercase state strings onto <see cref="IdleAnimator.AvatarState"/>.
    /// Hand-rolled rather than Enum.Parse so an unknown string is a warning rather than an
    /// exception on the UnitySendMessage call path, and so the accepted set stays visible
    /// next to the protocol docs above.
    /// </summary>
    static bool TryParseAvatarState(string raw, out IdleAnimator.AvatarState state)
    {
        switch (raw)
        {
            case "idle":      state = IdleAnimator.AvatarState.Idle;      return true;
            case "listening": state = IdleAnimator.AvatarState.Listening; return true;
            case "speaking":  state = IdleAnimator.AvatarState.Speaking;  return true;
            case "thinking":  state = IdleAnimator.AvatarState.Thinking;  return true;
            case "empathy":   state = IdleAnimator.AvatarState.Empathy;   return true;
            case "waiting":   state = IdleAnimator.AvatarState.Waiting;   return true;
            default:          state = IdleAnimator.AvatarState.Idle;      return false;
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
            if (_idle != null) _idle.OnUtteranceEnd();
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
