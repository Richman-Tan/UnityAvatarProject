using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Idle/personality animation for the CC4 avatar — blink, breathing, eyebrow and
/// smile "moments", gaze/saccades, and state-specific head tilts. Ported from the
/// Three.js/WebView avatar's ANIM system (src/components/AvatarVRM.js), using the
/// constants already tuned for this CC4 blendshape set (see that file's CC4 override
/// block). Runs independently of lipsync — AvatarController merges this component's
/// blendshape output with the viseme timeline so neither clobbers the other.
///
/// Attach to the same GameObject as AvatarController (the HD_Aaron root).
/// </summary>
[RequireComponent(typeof(AvatarController))]
public class IdleAnimator : MonoBehaviour
{
    public enum AvatarState { Idle, Listening, Speaking, Thinking, Empathy, Waiting }

    [Header("State")]
    [Tooltip("Drives which per-state animation blend is targeted. 'Speaking' is also " +
             "auto-triggered whenever AvatarController has an active lipsync segment.")]
    public AvatarState state = AvatarState.Idle;

    [Header("Gaze (verify direction in Play mode, flip if mirrored)")]
    [Tooltip("Flips which side Eye_Look_Right/Left (and head yaw) fire on.")]
    public bool gazeHorizontalFlip = false;

    // ── Blink constants (seconds / probabilities) ───────────────────────────────
    const float BLINK_CLOSE_DUR   = 0.075f;
    const float BLINK_HOLD_DUR    = 0.030f;
    const float BLINK_OPEN_DUR    = 0.180f;
    const float BLINK_MIN         = 3.0f;
    const float BLINK_MAX         = 7.5f;
    const float BLINK_DOUBLE_PROB = 0.18f;
    const float BLINK_DOUBLE_GAP  = 0.12f;

    // ── Smile constants (CC4-tuned, from AvatarVRM.js patchExprMap CC4 override) ─
    const float IDLE_SMILE               = 0.42f;
    const float IDLE_SMILE_PEAK          = 0.85f;
    const float ACTIVE_SMILE_MIN         = 0.14f;
    const float IDLE_SMILE_MOMENT_SPEED  = 2.8f;
    const float IDLE_SMILE_HOLD_MIN      = 1.5f;
    const float IDLE_SMILE_HOLD_MAX      = 3.0f;
    const float IDLE_SMILE_INT_MIN       = 3.0f;
    const float IDLE_SMILE_INT_MAX       = 8.0f;

    // ── Eyebrow constants (CC4-tuned) ────────────────────────────────────────────
    const float LISTEN_BROW_INNER  = 0.20f;
    const float EMPATHY_BROW_INNER = 0.28f;
    const float THINK_BROW_DOWN    = 0.20f;
    const float IDLE_BROW_PEAK     = 0.45f;
    const float IDLE_BROW_SPEED    = 2.2f;
    const float IDLE_BROW_HOLD_MIN = 0.7f;
    const float IDLE_BROW_HOLD_MAX = 1.8f;
    const float IDLE_BROW_INT_MIN  = 4.0f;
    const float IDLE_BROW_INT_MAX  = 11.0f;

    // ── Breathing constants ──────────────────────────────────────────────────────
    const float BREATH_RATE_IDLE  = 1.5f;  // rad/s
    const float BREATH_RATE_SPEAK = 2.2f;  // rad/s
    const float BREATH_SPINE_AMP  = 0.015f; // rad, applied to CC_Base_Waist
    const float BREATH_CHEST_AMP  = 0.025f; // rad, applied to CC_Base_Spine02
    const float BREATH_VAR_AMP    = 0.003f; // rad/s, max random rate offset
    const float BREATH_VAR_INT_MIN = 3.0f;
    const float BREATH_VAR_INT_MAX = 8.0f;

    // ── Gaze / saccade constants ─────────────────────────────────────────────────
    const float SACCADE_V_AMP    = 0.040f; // rad, vertical (pitch) component
    const float SACCADE_H_AMP    = 0.028f; // rad, horizontal (yaw) component
    const float SACCADE_MIN_IDLE = 1.0f, SACCADE_MAX_IDLE = 3.0f;
    const float SACCADE_MIN_LISTEN = 0.6f, SACCADE_MAX_LISTEN = 1.8f;
    const float SACCADE_APPROACH_RATE = 18f; // matches reference's dt*18 lerp

    const float GAZE_HOLD_MIN    = 1.4f, GAZE_HOLD_RANGE = 2.0f;
    const float GAZE_AWAY_MIN    = 0.55f, GAZE_AWAY_RANGE = 1.10f;
    const float GAZE_AWAY_H      = 0.18f, GAZE_AWAY_V = 0.10f;
    const float GAZE_RETURN_SPEED = 1.1f, GAZE_SHIFT_SPEED = 2.5f;
    const float GAZE_WANDER_PROB = 0.30f, GAZE_WANDER_H = 0.30f, GAZE_WANDER_V = 0.20f;

    // Fraction of gaze/saccade routed to the eyes (blendshapes) vs. the head bone.
    const float EYE_H_SCALE   = 0.60f;
    const float EYE_V_SCALE   = 0.65f;
    const float EYE_SACCADE   = 0.75f;
    // Angle (rad) that maps to full Eye_Look_* blendshape weight (1.0). The eye
    // bones don't drive this mesh (see task-6 finding), so gaze must be expressed
    // as blendshapes rather than bone rotation.
    const float EYE_LOOK_MAX_ANGLE = 0.5f;

    // ── State head-tilt constants ────────────────────────────────────────────────
    const float THINK_GAZE_H = 0.09f, THINK_GAZE_V = -0.06f, THINK_TILT_Z = 0.13f;
    const float EMPATHY_TILT_Z = -0.08f, EMPATHY_TILT_X = 0.015f;
    const float LISTEN_TILT_X = 0.010f;
    const float WAIT_TILT_Z = 0.040f;
    const float NOD_AMP = 0.032f, NOD_SPEED = 2.2f;
    const float NOD_HOLD_MIN = 0.30f, NOD_HOLD_MAX = 0.70f;
    const float NOD_INT_MIN = 3.5f, NOD_INT_MAX = 7.0f;
    const float IDLE_TILT_AMP = 0.055f, IDLE_TILT_SPEED = 1.0f;
    const float IDLE_TILT_HOLD_MIN = 1.2f, IDLE_TILT_HOLD_MAX = 2.8f;
    const float IDLE_TILT_INT_MIN = 10.0f, IDLE_TILT_INT_MAX = 24.0f;
    const float HEAD_ROLL_AMP = 0.010f, HEAD_ROLL_FREQ = 0.33f;

    // ── State blend time constants ───────────────────────────────────────────────
    // The reference used fixed per-frame lerp factors (e.g. 0.04) tuned for ~60fps.
    // We instead use dt-scaled exponential smoothing with an equivalent time
    // constant, which is correct at any frame rate.
    const float TAU_ACTIVE_SPEAK_LISTEN = 0.40f;
    const float TAU_THINK_WAIT          = 0.45f;
    const float TAU_EMPATHY             = 0.50f;

    private AvatarController _avatar;

    // Six independent 0-1 blends toward the current state.
    private float activeBlend, speakBlend, thinkBlend, empathyBlend, waitBlend, listenBlend;

    // Idle-only personality moments (smile/brow/tilt) only fire while mostly idle,
    // so they don't visibly interrupt an active state transition.
    private bool IsIdleEnough => activeBlend < 0.25f;

    // ── Blink FSM ────────────────────────────────────────────────────────────────
    private enum BlinkPhase { Idle, Closing, Hold, Opening, Between, Closing2, Hold2, Opening2 }
    private BlinkPhase _blinkPhase = BlinkPhase.Idle;
    private float _blinkTimer;
    private float _blinkNext = 4f;
    private bool  _blinkIsDouble;
    private float _blinkValue;

    // Constructed in Awake(), not as a field initializer — IdleMoment's constructor
    // calls Random.Range, which Unity forbids in MonoBehaviour field initializers.
    private IdleMoment _idleSmileMoment;
    private IdleMoment _idleBrowMoment;
    private bool       _idleBrowIsOuter;

    private float _smileOutput;
    private float _browInnerOutput, _browDownOutput, _browOuterOutput;

    // ── Breathing state ──────────────────────────────────────────────────────────
    private Transform  _waistBone, _chestBone;
    private Quaternion _waistRest, _chestRest;
    private float _breathPhase;
    private float _breathVarCurrent, _breathVarTarget;
    private float _breathVarTimer, _breathVarNextInterval;
    private float _breathValue; // sin(breathPhase), written in Update, consumed in LateUpdate

    // ── Gaze / saccade / head-tilt state ─────────────────────────────────────────
    private Transform  _headBone;
    private Quaternion _headRest;
    private float _elapsed; // free-running clock for the ambient head-roll sine

    // Micro-saccades (fast, small, frequent random glances)
    private float _saccadeTargetV, _saccadeTargetH;
    private float _saccadeCurrentV, _saccadeCurrentH;
    private float _nextSaccadeTime;

    // Conversational gaze phase FSM (center / away)
    private enum GazePhase { Center, Away }
    private GazePhase _gazePhase = GazePhase.Center;
    private float _gazePhaseTimer, _gazePhaseDuration;
    private float _gazeTargetH, _gazeTargetV;
    private float _gazeCurrentH, _gazeCurrentV;

    // Listening nod
    private float _nodCurrent, _nodTarget;
    private float _nodTimer, _nodNextInterval, _nodHoldTimer, _nodHoldDuration;
    private bool  _nodHolding;

    private IdleMoment _idleTiltMoment;
    private float _idleTiltSign;

    // Final composed head rotation (radians), consumed in LateUpdate
    private float _headPitch, _headYaw, _headRoll;
    // Final composed eye-look blendshape weights
    private float _eyeLookUp, _eyeLookDown, _eyeLookLeft, _eyeLookRight;

    private readonly Dictionary<string, float> _idleWeights = new();

    // ── Lifecycle ────────────────────────────────────────────────────────────────

    void Awake()
    {
        _avatar = GetComponent<AvatarController>();
        _idleSmileMoment = new IdleMoment(IDLE_SMILE_PEAK, IDLE_SMILE_HOLD_MIN, IDLE_SMILE_HOLD_MAX, IDLE_SMILE_INT_MIN, IDLE_SMILE_INT_MAX, IDLE_SMILE_MOMENT_SPEED);
        _idleBrowMoment  = new IdleMoment(IDLE_BROW_PEAK, IDLE_BROW_HOLD_MIN, IDLE_BROW_HOLD_MAX, IDLE_BROW_INT_MIN, IDLE_BROW_INT_MAX, IDLE_BROW_SPEED,
            onTrigger: () => _idleBrowIsOuter = Random.value < 0.5f);
        RerollBlinkNext();

        _waistBone = _avatar.FindBone("CC_Base_Waist", out _waistRest);
        _chestBone = _avatar.FindBone("CC_Base_Spine02", out _chestRest);
        if (_waistBone == null) Debug.LogWarning("[IdleAnimator] 'CC_Base_Waist' not found — breathing disabled for spine.");
        if (_chestBone == null) Debug.LogWarning("[IdleAnimator] 'CC_Base_Spine02' not found — breathing disabled for chest.");
        _breathVarNextInterval = Random.Range(BREATH_VAR_INT_MIN, BREATH_VAR_INT_MAX);

        _headBone = _avatar.FindBone("CC_Base_Head", out _headRest);
        if (_headBone == null) Debug.LogWarning("[IdleAnimator] 'CC_Base_Head' not found — gaze/tilt head motion disabled.");

        _idleTiltMoment = new IdleMoment(IDLE_TILT_AMP, IDLE_TILT_HOLD_MIN, IDLE_TILT_HOLD_MAX, IDLE_TILT_INT_MIN, IDLE_TILT_INT_MAX, IDLE_TILT_SPEED,
            onTrigger: () => _idleTiltSign = Random.value < 0.5f ? 1f : -1f);

        _nextSaccadeTime = Random.Range(SACCADE_MIN_IDLE, SACCADE_MAX_IDLE);
        _gazePhaseDuration = Random.Range(GAZE_HOLD_MIN, GAZE_HOLD_MIN + GAZE_HOLD_RANGE);
        _nodNextInterval = Random.Range(NOD_INT_MIN, NOD_INT_MAX);
    }

    void Update()
    {
        float dt = Time.deltaTime;
        _elapsed += dt;

        UpdateStateBlends(dt);
        UpdateBlink(dt);
        UpdateSmile(dt);
        UpdateEyebrows(dt);
        UpdateBreathing(dt);
        UpdateGaze(dt);

        _idleWeights.Clear();
        _idleWeights["Eye_Blink_L"]          = _blinkValue;
        _idleWeights["Eye_Blink_R"]          = _blinkValue;
        _idleWeights["Mouth_Corner_Pull_L"]  = _smileOutput;
        _idleWeights["Mouth_Corner_Pull_R"]  = _smileOutput;
        _idleWeights["Brow_Raise_In_L"]      = _browInnerOutput;
        _idleWeights["Brow_Raise_In_R"]      = _browInnerOutput;
        _idleWeights["Brow_Down_L"]          = _browDownOutput;
        _idleWeights["Brow_Down_R"]          = _browDownOutput;
        _idleWeights["Brow_Raise_Outer_L"]   = _browOuterOutput;
        _idleWeights["Brow_Raise_Outer_R"]   = _browOuterOutput;
        _idleWeights["Eye_Look_Up_L"]        = _eyeLookUp;
        _idleWeights["Eye_Look_Up_R"]        = _eyeLookUp;
        _idleWeights["Eye_Look_Down_L"]      = _eyeLookDown;
        _idleWeights["Eye_Look_Down_R"]      = _eyeLookDown;
        _idleWeights["Eye_Look_Left_L"]      = _eyeLookLeft;
        _idleWeights["Eye_Look_Left_R"]      = _eyeLookLeft;
        _idleWeights["Eye_Look_Right_L"]     = _eyeLookRight;
        _idleWeights["Eye_Look_Right_R"]     = _eyeLookRight;
        _avatar.SetIdleWeights(_idleWeights);
    }

    // ── State blends ─────────────────────────────────────────────────────────────

    void UpdateStateBlends(float dt)
    {
        bool listening = state == AvatarState.Listening;
        bool thinking  = state == AvatarState.Thinking;
        bool empathy   = state == AvatarState.Empathy;
        bool waiting   = state == AvatarState.Waiting;
        // A lipsync segment playing counts as "speaking" even if the caller never
        // explicitly set AvatarState.Speaking — keeps ws-test-server playback in sync.
        bool speaking  = state == AvatarState.Speaking || _avatar.IsLipSyncActive;
        bool active    = speaking || listening || thinking || empathy || waiting;

        activeBlend  = ExpLerp(activeBlend,  active    ? 1f : 0f, dt, TAU_ACTIVE_SPEAK_LISTEN);
        speakBlend   = ExpLerp(speakBlend,   speaking  ? 1f : 0f, dt, TAU_ACTIVE_SPEAK_LISTEN);
        listenBlend  = ExpLerp(listenBlend,  listening ? 1f : 0f, dt, TAU_ACTIVE_SPEAK_LISTEN);
        thinkBlend   = ExpLerp(thinkBlend,   thinking  ? 1f : 0f, dt, TAU_THINK_WAIT);
        waitBlend    = ExpLerp(waitBlend,    waiting   ? 1f : 0f, dt, TAU_THINK_WAIT);
        empathyBlend = ExpLerp(empathyBlend, empathy   ? 1f : 0f, dt, TAU_EMPATHY);
    }

    // ── Blink ────────────────────────────────────────────────────────────────────

    void UpdateBlink(float dt)
    {
        _blinkTimer += dt;
        switch (_blinkPhase)
        {
            case BlinkPhase.Idle:
                _blinkValue = 0f;
                if (_blinkTimer >= _blinkNext)
                {
                    _blinkIsDouble = Random.value < BLINK_DOUBLE_PROB;
                    Advance(BlinkPhase.Closing);
                }
                break;
            case BlinkPhase.Closing:
                _blinkValue = Smoothstep(_blinkTimer / BLINK_CLOSE_DUR);
                if (_blinkTimer >= BLINK_CLOSE_DUR) Advance(BlinkPhase.Hold);
                break;
            case BlinkPhase.Hold:
                _blinkValue = 1f;
                if (_blinkTimer >= BLINK_HOLD_DUR) Advance(BlinkPhase.Opening);
                break;
            case BlinkPhase.Opening:
                _blinkValue = 1f - Smoothstep(_blinkTimer / BLINK_OPEN_DUR);
                if (_blinkTimer >= BLINK_OPEN_DUR)
                {
                    if (_blinkIsDouble) Advance(BlinkPhase.Between);
                    else { Advance(BlinkPhase.Idle); RerollBlinkNext(); }
                }
                break;
            case BlinkPhase.Between:
                _blinkValue = 0f;
                if (_blinkTimer >= BLINK_DOUBLE_GAP) Advance(BlinkPhase.Closing2);
                break;
            case BlinkPhase.Closing2:
                _blinkValue = Smoothstep(_blinkTimer / BLINK_CLOSE_DUR);
                if (_blinkTimer >= BLINK_CLOSE_DUR) Advance(BlinkPhase.Hold2);
                break;
            case BlinkPhase.Hold2:
                _blinkValue = 1f;
                if (_blinkTimer >= BLINK_HOLD_DUR) Advance(BlinkPhase.Opening2);
                break;
            case BlinkPhase.Opening2:
                _blinkValue = 1f - Smoothstep(_blinkTimer / BLINK_OPEN_DUR);
                if (_blinkTimer >= BLINK_OPEN_DUR) { Advance(BlinkPhase.Idle); RerollBlinkNext(); }
                break;
        }
    }

    void Advance(BlinkPhase next)
    {
        _blinkPhase = next;
        _blinkTimer = 0f;
    }

    void RerollBlinkNext()
    {
        _blinkNext = Random.Range(BLINK_MIN, BLINK_MAX);
        // People blink noticeably less while speaking.
        if (speakBlend > 0.3f) _blinkNext *= 1.8f;
    }

    // ── Smile ────────────────────────────────────────────────────────────────────

    void UpdateSmile(float dt)
    {
        float idleSmileOutput = _idleSmileMoment.Tick(dt, enabled: IsIdleEnough);
        float smileTarget = Mathf.Lerp(ACTIVE_SMILE_MIN, IDLE_SMILE, 1f - activeBlend * 0.75f);
        _smileOutput = Mathf.Min(1f, Mathf.Max(smileTarget, idleSmileOutput));
    }

    // ── Eyebrows ─────────────────────────────────────────────────────────────────

    void UpdateEyebrows(float dt)
    {
        float browInner = listenBlend * LISTEN_BROW_INNER + empathyBlend * EMPATHY_BROW_INNER;
        float browDown  = thinkBlend * THINK_BROW_DOWN;
        float browOuter = 0f;

        // Idle brow moments fade out while speaking rather than disappearing outright.
        float idleBrowOutput = _idleBrowMoment.Tick(dt, enabled: IsIdleEnough) * Mathf.Max(0f, 1f - speakBlend * 0.6f);
        if (_idleBrowIsOuter) browOuter += idleBrowOutput;
        else                  browInner += idleBrowOutput;

        _browInnerOutput = Mathf.Min(1f, browInner);
        _browDownOutput  = Mathf.Min(1f, browDown);
        _browOuterOutput = Mathf.Min(1f, browOuter);
    }

    // ── Breathing ────────────────────────────────────────────────────────────────

    void UpdateBreathing(float dt)
    {
        // Slowly reroll a small random offset to the breathing rate every few seconds,
        // rather than summing a second sine wave — avoids phase discontinuities.
        _breathVarTimer += dt;
        if (_breathVarTimer >= _breathVarNextInterval)
        {
            _breathVarTarget = (Random.value - 0.5f) * BREATH_VAR_AMP;
            _breathVarTimer = 0f;
            _breathVarNextInterval = Random.Range(BREATH_VAR_INT_MIN, BREATH_VAR_INT_MAX);
        }
        _breathVarCurrent = Mathf.Lerp(_breathVarCurrent, _breathVarTarget, dt * 0.15f);

        float breathRate = Mathf.Lerp(BREATH_RATE_IDLE, BREATH_RATE_SPEAK, speakBlend) + _breathVarCurrent;
        _breathPhase += dt * breathRate;
        _breathValue = Mathf.Sin(_breathPhase);
    }

    // ── Gaze / saccades / head tilts ─────────────────────────────────────────────
    //
    // Eyes: this rig's eye bones don't drive any visible mesh deformation (see
    // task-6 finding — confirmed via BakeMesh vertex comparison), so gaze is
    // expressed as Eye_Look_Up/Down/Left/Right blendshapes instead of bone
    // rotation. Head: bone-driven, using the confirmed axis mapping — local X =
    // pitch (+ = down), Y = yaw, Z = roll.
    void UpdateGaze(float dt)
    {
        UpdateSaccades(dt);
        UpdateGazePhase(dt);
        UpdateNod(dt);

        float idleTiltOutput = _idleTiltMoment.Tick(dt, enabled: IsIdleEnough) * _idleTiltSign;

        // Vertical (pitch): conversational gaze + thinking bias + saccade residual
        // routed to the head, plus empathy/listening tilt and the listening nod.
        float lookV = Mathf.Lerp(_gazeCurrentV, THINK_GAZE_V, thinkBlend);
        _headPitch = lookV * (1f - EYE_V_SCALE)
                   + _saccadeCurrentV * (1f - EYE_SACCADE)
                   + empathyBlend * EMPATHY_TILT_X
                   + listenBlend * LISTEN_TILT_X
                   + _nodCurrent * listenBlend;

        // Horizontal (yaw): same routing split for the head's share of gaze.
        float lookH = Mathf.Lerp(_gazeCurrentH, THINK_GAZE_H, thinkBlend);
        float yawSign = gazeHorizontalFlip ? -1f : 1f;
        _headYaw = yawSign * (lookH * 0.62f * (1f - EYE_H_SCALE) + _saccadeCurrentH * (1f - EYE_SACCADE));

        // Roll: ambient sine + per-state tilt biases + idle tilt moments.
        _headRoll = Mathf.Sin(_elapsed * HEAD_ROLL_FREQ) * HEAD_ROLL_AMP
                  + thinkBlend * THINK_TILT_Z
                  + empathyBlend * EMPATHY_TILT_Z
                  + waitBlend * WAIT_TILT_Z
                  + idleTiltOutput;

        // Eyes get the majority share of gaze/saccade, expressed as blendshapes.
        float eyeV = lookV * EYE_V_SCALE + _saccadeCurrentV * EYE_SACCADE;
        float eyeH = yawSign * (lookH * EYE_H_SCALE + _saccadeCurrentH * EYE_SACCADE);

        _eyeLookDown  = Mathf.Clamp01(eyeV / EYE_LOOK_MAX_ANGLE);
        _eyeLookUp    = Mathf.Clamp01(-eyeV / EYE_LOOK_MAX_ANGLE);
        _eyeLookRight = Mathf.Clamp01(eyeH / EYE_LOOK_MAX_ANGLE);
        _eyeLookLeft  = Mathf.Clamp01(-eyeH / EYE_LOOK_MAX_ANGLE);
    }

    void UpdateSaccades(float dt)
    {
        if (_elapsed > _nextSaccadeTime)
        {
            _saccadeTargetV = (Random.value - 0.5f) * SACCADE_V_AMP;
            _saccadeTargetH = (Random.value - 0.5f) * SACCADE_H_AMP;
            float min = listenBlend > 0.5f ? SACCADE_MIN_LISTEN : SACCADE_MIN_IDLE;
            float max = listenBlend > 0.5f ? SACCADE_MAX_LISTEN : SACCADE_MAX_IDLE;
            _nextSaccadeTime = _elapsed + min + Random.value * (max - min);
        }
        float alpha = 1f - Mathf.Exp(-dt * SACCADE_APPROACH_RATE);
        _saccadeCurrentV = Mathf.Lerp(_saccadeCurrentV, _saccadeTargetV, alpha);
        _saccadeCurrentH = Mathf.Lerp(_saccadeCurrentH, _saccadeTargetH, alpha);
    }

    void UpdateGazePhase(float dt)
    {
        _gazePhaseTimer += dt;
        if (_gazePhaseTimer >= _gazePhaseDuration)
        {
            _gazePhaseTimer = 0f;
            if (_gazePhase == GazePhase.Center)
            {
                _gazePhase = GazePhase.Away;
                bool wander = Random.value < GAZE_WANDER_PROB;
                float h = wander ? GAZE_WANDER_H : GAZE_AWAY_H;
                float v = wander ? GAZE_WANDER_V : GAZE_AWAY_V;
                _gazeTargetH = (Random.value < 0.5f ? -1f : 1f) * h;
                _gazeTargetV = (Random.value < 0.5f ? -1f : 1f) * v;
                float awayRange = wander ? GAZE_AWAY_RANGE * 1.6f : GAZE_AWAY_RANGE;
                float awayScale = speakBlend > 0.5f ? 0.65f : listenBlend > 0.5f ? 0.5f : 1.0f;
                _gazePhaseDuration = (GAZE_AWAY_MIN + Random.value * awayRange) * awayScale;
            }
            else
            {
                _gazePhase = GazePhase.Center;
                _gazeTargetH = 0f;
                _gazeTargetV = 0f;
                float holdScale = listenBlend > 0.5f ? 1.4f : speakBlend > 0.5f ? 1.0f : 0.7f;
                _gazePhaseDuration = (GAZE_HOLD_MIN + Random.value * GAZE_HOLD_RANGE) * holdScale;
            }
        }
        float speed = _gazePhase == GazePhase.Center ? GAZE_RETURN_SPEED : GAZE_SHIFT_SPEED;
        float alpha = 1f - Mathf.Exp(-dt * speed);
        _gazeCurrentH = Mathf.Lerp(_gazeCurrentH, _gazeTargetH, alpha);
        _gazeCurrentV = Mathf.Lerp(_gazeCurrentV, _gazeTargetV, alpha);
    }

    void UpdateNod(float dt)
    {
        if (listenBlend <= 0.3f) { _nodCurrent = Mathf.Lerp(_nodCurrent, 0f, 1f - Mathf.Exp(-dt * NOD_SPEED)); return; }

        _nodTimer += dt;
        if (!_nodHolding && _nodTimer >= _nodNextInterval)
        {
            _nodTarget = NOD_AMP;
            _nodHolding = true;
            _nodHoldDuration = Random.Range(NOD_HOLD_MIN, NOD_HOLD_MAX);
            _nodHoldTimer = 0f;
        }
        if (_nodHolding)
        {
            _nodHoldTimer += dt;
            if (_nodHoldTimer >= _nodHoldDuration)
            {
                _nodTarget = 0f;
                _nodHolding = false;
                _nodTimer = 0f;
                _nodNextInterval = Random.Range(NOD_INT_MIN, NOD_INT_MAX);
            }
        }
        _nodCurrent = Mathf.Lerp(_nodCurrent, _nodTarget, 1f - Mathf.Exp(-dt * NOD_SPEED));
    }

    // Bone writes happen in LateUpdate, same as AvatarController's jaw bone — the
    // Animator's own internal update runs before MonoBehaviour LateUpdate, so this
    // is the final write before the frame renders.
    void LateUpdate()
    {
        if (_waistBone != null)
            _waistBone.localRotation = _waistRest * Quaternion.Euler(_breathValue * BREATH_SPINE_AMP * Mathf.Rad2Deg, 0f, 0f);
        if (_chestBone != null)
            _chestBone.localRotation = _chestRest * Quaternion.Euler(_breathValue * BREATH_CHEST_AMP * Mathf.Rad2Deg, 0f, 0f);
        if (_headBone != null)
            _headBone.localRotation = _headRest * Quaternion.Euler(_headPitch * Mathf.Rad2Deg, _headYaw * Mathf.Rad2Deg, _headRoll * Mathf.Rad2Deg);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    static float ExpLerp(float current, float target, float dt, float tau)
    {
        float alpha = 1f - Mathf.Exp(-dt / Mathf.Max(tau, 0.0001f));
        return Mathf.Lerp(current, target, alpha);
    }

    static float Smoothstep(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Generic "personality moment" mini-FSM: after a random interval, ramps up to a
    /// randomized fraction of `peak`, holds, ramps back to zero, then waits another
    /// random interval. Reused for idle smile/eyebrow/head-tilt moments so each one
    /// doesn't need its own hand-rolled timer state.
    /// </summary>
    private class IdleMoment
    {
        readonly float _peak, _holdMin, _holdMax, _intMin, _intMax, _speed;
        readonly System.Action _onTrigger;
        float _target, _current;
        float _timer, _nextInterval;
        bool  _holding;
        float _holdTimer, _holdDuration;

        public IdleMoment(float peak, float holdMin, float holdMax, float intMin, float intMax, float speed, System.Action onTrigger = null)
        {
            _peak = peak; _holdMin = holdMin; _holdMax = holdMax;
            _intMin = intMin; _intMax = intMax; _speed = speed;
            _onTrigger = onTrigger;
            _nextInterval = Random.Range(intMin, intMax);
        }

        /// <param name="enabled">Pass false while the avatar is in an active (non-idle)
        /// state — freezes the trigger timer and fades the output to zero, matching the
        /// reference's `isIdleEnough = activeBlend &lt; 0.25` gate. These are idle-only
        /// personality quirks and shouldn't fire mid-state-change.</param>
        public float Tick(float dt, bool enabled = true)
        {
            if (enabled)
            {
                _timer += dt;
                if (!_holding && _timer >= _nextInterval)
                {
                    _target = _peak * (0.5f + Random.value * 0.5f);
                    _holding = true;
                    _holdDuration = Random.Range(_holdMin, _holdMax);
                    _holdTimer = 0f;
                    _onTrigger?.Invoke();
                }
                if (_holding)
                {
                    _holdTimer += dt;
                    if (_holdTimer >= _holdDuration)
                    {
                        _target = 0f;
                        _holding = false;
                        _timer = 0f;
                        _nextInterval = Random.Range(_intMin, _intMax);
                    }
                }
            }
            else
            {
                _target = 0f;
            }
            float alpha = 1f - Mathf.Exp(-dt * _speed);
            _current = Mathf.Lerp(_current, _target, alpha);
            return _current;
        }
    }

    // ── Inspector helpers ────────────────────────────────────────────────────────

    [ContextMenu("Set State: Idle")]      void SetIdle()      => state = AvatarState.Idle;
    [ContextMenu("Set State: Listening")] void SetListening() => state = AvatarState.Listening;
    [ContextMenu("Set State: Speaking")]  void SetSpeaking()  => state = AvatarState.Speaking;
    [ContextMenu("Set State: Thinking")]  void SetThinking()  => state = AvatarState.Thinking;
    [ContextMenu("Set State: Empathy")]   void SetEmpathy()   => state = AvatarState.Empathy;
    [ContextMenu("Set State: Waiting")]   void SetWaiting()   => state = AvatarState.Waiting;
}
