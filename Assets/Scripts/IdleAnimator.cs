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
    // Blink INTERVALS are log-normal, not uniform — that is the shape the
    // physiology actually has, and a uniform draw reads as metronomic.
    // Rates from Bentivoglio et al. 1997 (n=150): 17/min at rest, 26/min in
    // conversation (+99.7%, p<1e-9). The old code did the opposite — it
    // multiplied the interval by 1.8 while speaking, on the comment "people
    // blink noticeably less while speaking" — which yielded ~6.3 blinks/min,
    // about a quarter of the real conversational rate.
    // These are the WAIT between blinks, not the blink period, and they are not
    // the only thing that fires a blink: each blink event itself occupies ~0.36 s
    // on average (0.285 s single, 0.69 s double at BLINK_DOUBLE_PROB), a double
    // contributes two lid closures, and gaze-shift coupling truncates some waits.
    // Setting the wait to 60/17 and 60/26 therefore OVERSHOOTS — measured in play
    // mode at 33 closures/min against a 26 target. The waits below are corrected
    // by that measured factor so the observed closure rate lands on the
    // literature figures; BlinkRateSelfCheck re-derives it if the model changes.
    const float BLINK_RATE_CORRECTION = 33f / 26f;              // measured overshoot
    const float BLINK_MEAN_REST   = (60f / 17f) * BLINK_RATE_CORRECTION;  // 4.48 s wait
    const float BLINK_MEAN_SPEAK  = (60f / 26f) * BLINK_RATE_CORRECTION;  // 2.93 s wait
    const float BLINK_SIGMA       = 0.5f;        // log-normal shape
    const float BLINK_MIN_GAP     = 0.6f;        // refractory floor
    const float BLINK_DOUBLE_PROB = 0.18f;
    const float BLINK_DOUBLE_GAP  = 0.12f;
    // Blinks cluster on gaze shifts — the lid closes as the eye jumps. Tuned
    // DOWN from 0.42 after a play-mode measurement: coupled blinks land on top of
    // the interval timer, and at 0.42 the combined rate came out at 32.9/min
    // against the 26/min conversational target.
    const float BLINK_ON_GAZE_SHIFT_PROB = 0.22f;
    // Every blink used to be bit-identical. Real ones vary, but NOT by capping
    // every blink short — that would just reintroduce the "never seals" artefact
    // this fix exists to remove. Most spontaneous blinks close completely; partial
    // blinks are a separate, less common event. So: full closure by default, with
    // an occasional genuine partial.
    const float BLINK_PARTIAL_PROB = 0.15f;
    const float BLINK_PARTIAL_MIN  = 0.72f, BLINK_PARTIAL_MAX = 0.92f;
    // True inter-eye offset is 5-20 ms — under one frame at 60fps — so it is
    // expressed as a short lag on the right lid rather than pretending to
    // sub-frame precision.
    const float BLINK_R_LAG_TAU   = 0.012f;
    // A blink is not just the lid: the brow dips slightly and the lower lid rises.
    const float BLINK_BROW_DIP    = 0.16f;
    const float BLINK_LOWER_LID   = 0.28f;

    // ── Smile constants (CC4-tuned, from AvatarVRM.js patchExprMap CC4 override) ─
    // IDLE_SMILE was 0.42 — a permanently held 42% mouth-corner pull, i.e. a
    // frozen grin the face never came out of. A resting face is close to neutral
    // with only a faint upturn; actual smiles come from the idle "moments" below
    // and (once wired) from conversation sentiment.
    const float IDLE_SMILE               = 0.12f;
    const float IDLE_SMILE_PEAK          = 0.85f;
    const float ACTIVE_SMILE_MIN         = 0.06f;

    // ── Duchenne smile (AU6 + AU12) ──────────────────────────────────────────
    // A smile driven by the mouth alone (AU12, Mouth_Corner_Pull) with an inert
    // eye region is the canonical insincere/uncanny expression. A genuine smile
    // also raises the cheeks and narrows the eyes (AU6, orbicularis oculi), and
    // AU6 *leads* AU12 by roughly 50-100 ms.
    //
    // The lead is produced by lagging the mouth channel rather than delaying with
    // a buffer: the eye channel tracks the smile signal directly (~30 ms through
    // AvatarController's smoother) while the mouth adds SMILE_MOUTH_LAG_TAU on top
    // (~90 ms total), leaving AU6 ahead by ~60 ms.
    const float DUCHENNE_CHEEK_RAISE  = 0.75f;  // AU6 amount, as a fraction of the smile
    const float DUCHENNE_SQUINT_INNER = 0.35f;  // crow's-feet narrowing
    const float SMILE_MOUTH_LAG_TAU   = 0.06f;  // s
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

    // ── Sentence-level emotion ───────────────────────────────────────────────
    // packages/core/sentiment/detectSentiment.ts classifies every sentence as
    // positive | warm | concern | question | neutral, and the web app has always
    // shipped that label inside the play payload. Unity never read it, so the
    // classifier's output reached nothing and the face was flat regardless of what
    // was being said. Mapping and scale ported from the Three.js renderer, which
    // did consume it (renderer.js:1555-1587).
    const float EMOTION_SCALE      = 1.8f;   // CC4 override value
    const float EMOTION_RISE_TAU   = 0.40f;  // fades in fast once audio starts
    const float EMOTION_FALL_TAU   = 1.67f;  // and decays slowly, so a 50 ms
                                             // inter-sentence gap doesn't reset it
    const float EMO_POSITIVE_SMILE = 0.20f, EMO_POSITIVE_RELAX = 0.12f;
    const float EMO_WARM_BROW_IN   = 0.10f, EMO_WARM_RELAX     = 0.16f;
    const float EMO_CONCERN_BROW_IN = 0.13f, EMO_CONCERN_BROW_DOWN = 0.07f;
    const float EMO_QUESTION_BROW_OUT = 0.13f, EMO_QUESTION_BROW_IN = 0.05f;

    // ── Gaze-coupled brow and squint ─────────────────────────────────────────
    // Brows track vertical gaze and the eyes narrow on hard lateral looks. Both
    // were tuned in the Three.js path and never ported; without them the brow is
    // rigid while the eyes move underneath it, which reads as a mask.
    const float GAZE_BROW_UP   = 0.65f;   // brow raise at full upward gaze
    const float GAZE_BROW_DOWN = 0.28f;   // brow lowering at full downward gaze
    const float GAZE_SQUINT    = 0.40f;   // narrowing at full lateral gaze

    // ── Turn-taking gaze (Kendon; timings from Andrist et al.) ───────────────
    // Speakers look AWAY as they begin an utterance (planning load) and back at
    // the listener as they finish (yielding the turn). Getting this wrong is why
    // an avatar can hit the right overall gaze ratio and still feel off: the
    // aversions land in the wrong places relative to what it is saying.
    // Andrist measures the turn-taking aversion firing on 73.1% of utterances and
    // running 2.30 s. The 1.03 s pre-utterance lead in that paper is not
    // reproducible here — the bridge delivers `play` at audio onset, with no
    // lookahead — so the aversion starts at onset instead. Noted, not faked.
    const float TURN_AVERT_PROB  = 0.731f;
    const float TURN_AVERT_DUR   = 2.30f;
    const float TURN_YIELD_LEAD  = 0.80f;  // re-establish gaze this long before the end
    const float TURN_YIELD_HOLD_AFTER = 0.60f;  // and keep holding past the end

    // ── Breathing constants ──────────────────────────────────────────────────────
    const float BREATH_RATE_IDLE  = 1.5f;  // rad/s
    const float BREATH_RATE_SPEAK = 2.2f;  // rad/s
    const float BREATH_SPINE_AMP  = 0.015f; // rad, applied to CC_Base_Waist
    const float BREATH_CHEST_AMP  = 0.025f; // rad, applied to CC_Base_Spine02
    const float BREATH_VAR_AMP    = 0.003f; // rad/s, max random rate offset
    const float BREATH_VAR_INT_MIN = 3.0f;
    const float BREATH_VAR_INT_MAX = 8.0f;
    // Ported from the Three.js ANIM block, which had these tuned and never brought
    // across: the breath should travel up the neck and out into the arms, not stop
    // at the chest. A torso that breathes while the neck and shoulders sit
    // perfectly still is one of the things that reads as a mannequin.
    const float BREATH_NECK_AMP  = 0.008f;  // rad

    // ── Postural life ────────────────────────────────────────────────────────
    // The body has NO animator controller and no clips — below the neck it was a
    // static bind pose with only 0.86 deg of waist and 1.43 deg of chest breathing.
    // Nobody holds that still. Dual-frequency sway so it never repeats visibly,
    // plus an occasional weight shift.
    const float SWAY_IDLE      = 0.012f, SWAY_ACTIVE = 0.022f, SWAY_SPEAK = 0.030f;
    const float SWAY_RATE_A    = 0.33f,  SWAY_RATE_B = 0.21f;
    const float SHIFT_AMP      = 0.020f;  // rad of lateral weight shift at the waist
    const float SHIFT_TAU      = 1.60f;   // slow — a weight shift is not a twitch
    const float SHIFT_INT_MIN  = 12f, SHIFT_INT_MAX = 26f;
    const float CLAVICLE_BREATH = 0.010f; // shoulders rise slightly on the inhale

    // ── Speech-coupled head motion ───────────────────────────────────────────
    // Nothing coupled head movement to speech: the head did exactly the same thing
    // talking as silent. Real speakers accent stressed syllables with small head
    // moves. Rather than invent a prosody model, this rides the articulation
    // envelope already being computed for the lips — wide-open vowels coincide
    // with the stressed syllables that carry head accents.
    const float SPEECH_ACCENT_GAIN = 0.030f;  // rad of head pitch at full articulation
    const float SPEECH_ACCENT_TAU  = 0.13f;   // lags the mouth slightly, as the head does
    const float SPEECH_SWAY_GAIN   = 0.016f;  // small yaw/roll drift while talking

    // ── Gaze / saccade constants ─────────────────────────────────────────────────
    // Two saccade populations. Micro-saccades stop a fixating eye from looking
    // frozen; feature saccades are the eye-to-eye-to-mouth scan of the viewer's
    // face. Horizontally dominant, as human saccades are.
    // Amplitudes and intervals for someone holding a conversational partner in
    // view: the eye fixates for roughly 1-2 s between shifts, and the shifts
    // themselves stay small (a couple of degrees) because the thing being looked
    // at is not moving. Cross-checked against the one published production spec
    // with concrete numbers for this case (Convai's conversational-gaze settings:
    // 1.8 s +/- 0.65 mean interval, 2.5 deg max) — used only as a sanity check on
    // the tuning; nothing here depends on that SDK.
    //
    // The first pass used 0.3-1.6 s intervals and up to 9 deg, reasoning from the
    // eye-to-eye-to-mouth face-scanning literature. That is defensible for someone
    // studying a face, but on a front-on avatar filling the frame it reads as
    // twitchy — the eyes are the only thing moving, so every jump is conspicuous in
    // a way it is not on a real face at conversational distance.
    const float SACCADE_MICRO_MIN_DEG   = 0.15f, SACCADE_MICRO_MAX_DEG   = 0.7f;
    const float SACCADE_FEATURE_MIN_DEG = 1.6f,  SACCADE_FEATURE_MAX_DEG = 3.4f;
    const float SACCADE_FEATURE_PROB    = 0.22f;
    const float SACCADE_VERTICAL_RATIO  = 0.55f; // vertical excursions are smaller
    const float SACCADE_MIN_IDLE = 1.30f, SACCADE_MAX_IDLE = 2.60f;   // mean 1.95 s
    const float SACCADE_MIN_LISTEN = 1.05f, SACCADE_MAX_LISTEN = 2.15f; // mean 1.60 s

    const float GAZE_HOLD_MIN    = 1.4f, GAZE_HOLD_RANGE = 2.0f;
    const float GAZE_AWAY_MIN    = 0.55f, GAZE_AWAY_RANGE = 1.10f;
    const float GAZE_AWAY_H      = 0.18f, GAZE_AWAY_V = 0.10f;
    const float GAZE_WANDER_PROB = 0.30f, GAZE_WANDER_H = 0.30f, GAZE_WANDER_V = 0.20f;

    // Gaze shifts are BALLISTIC, not eased. Human saccades follow the "main
    // sequence": duration scales roughly linearly with amplitude, and the eye
    // arrives — it does not asymptote.
    //     duration_s = SACCADE_BASE + SACCADE_PER_DEG * amplitude_deg
    // (~43 ms for 10 deg, ~89 ms for 31 deg). The previous exponential lerp
    // (GAZE_RETURN_SPEED 1.1 -> tau 910 ms, GAZE_SHIFT_SPEED 2.5 -> tau 400 ms)
    // was slower than the hold phases themselves, so the gaze never actually
    // reached centre: simulating it gave 2.5% real mutual gaze while speaking
    // against a nominal 40%, and 0.2% while thinking. The avatar was, in effect,
    // never looking at the viewer at all.
    const float SACCADE_BASE_DUR    = 0.021f;  // s, intercept of the main sequence
    const float SACCADE_DUR_PER_DEG = 0.0022f; // s per degree of amplitude
    const float SACCADE_MIN_DUR     = 0.030f;  // s, floor for tiny corrections

    // ── Head follows the eyes, it does not travel with them ──────────────────
    // Making gaze ballistic is right for EYES and wrong for the head: the head
    // takes a share of the same signal, so it inherited the snap and the whole
    // face read as teleporting between poses.
    //
    // Eye-head coordination during a gaze shift is well established: the eye
    // saccades first and arrives in 30-80 ms, the head follows over roughly
    // 200-400 ms, and the eye then counter-rotates to stay on target while the
    // head is still moving. A ~170 ms time constant puts the head's arrival in
    // that window, i.e. about 3x slower than the eye — a ratio that matches the
    // one published production spec carrying concrete numbers here (Convai, head
    // smoothing 6.0 vs eye 18.0). Reference only; no dependency on it.
    //
    // The head also does NOT chase micro-saccades: nobody's head twitches because
    // their eye moved half a degree.
    const float HEAD_FOLLOW_TAU = 0.167f;
    const float HEAD_SACCADE_SHARE = 0f;    // was (1 - EYE_SACCADE) = 0.25

    // ── The head does not follow SMALL gaze shifts at all ────────────────────
    // Lagging the head fixed the snap but left it restless: it still took a flat
    // proportional share of every aversion, so it repositioned by 2-4 deg roughly
    // every 2 s, in a direction randomised each time. Real heads don't do that.
    //
    // Human gaze shifts below the "eye-only range" are executed by the eyes
    // alone — the head contributes nothing. Above that threshold, head
    // contribution grows with amplitude. Two independent sources agree on where
    // the knee sits:
    //   * Stahl 1999 / Ruhland et al. STAR: threshold 15-20 deg, with graphics
    //     practice using a lower 10-15 deg (a completely still head reads as
    //     dead on screen, so animation deliberately starts the ramp early).
    //   * UMA DynamicExpressionPlayer, an independent implementation:
    //     HeadAssistStartAngle 15, HeadAssistFullAngle 35.
    //
    // Our aversions are 11.8 deg normally and 20.7 deg on a wander — i.e. the
    // common case sits INSIDE the eye-only range, where the head should be
    // still, and only the large minority earns a head turn.
    //
    // Smoothstep rather than a linear ramp so there is no knee at the threshold:
    // a tuning change to the aversion amplitudes can't flip head motion on and
    // off discontinuously.
    const float HEAD_ASSIST_START_DEG = 10f;  // below this the head is still
    const float HEAD_ASSIST_FULL_DEG  = 32f;  // by this it takes its full share

    // The head's share of a gaze shift, once the shift is big enough to earn one.
    const float HEAD_YAW_GAIN = 0.62f;                            // head under-rotates in yaw
    const float HEAD_V_SHARE  = 1f - EYE_V_SCALE;                 // 0.35
    const float HEAD_H_SHARE  = HEAD_YAW_GAIN * (1f - EYE_H_SCALE); // 0.248

    /// <summary>Fraction of a gaze shift the head contributes, as a function of
    /// the shift's amplitude. Zero inside the eye-only range, ramping to full for
    /// shifts large enough that a real neck would help.</summary>
    static float HeadAssist(float amplitudeDeg)
    {
        float t = Mathf.Clamp01((amplitudeDeg - HEAD_ASSIST_START_DEG)
                              / (HEAD_ASSIST_FULL_DEG - HEAD_ASSIST_START_DEG));
        return t * t * (3f - 2f * t);
    }

    // ── Per-state gaze-aversion ratios ───────────────────────────────────────
    // How much of the time the avatar looks AWAY from the viewer, per state.
    // Targets come from the conversational-gaze literature:
    //   thinking  ~78% aversion   (Doherty-Sneddon — averting gaze offloads
    //                              cognitive effort; the single most human thing
    //                              a face does while it is working something out)
    //   speaking  ~60% aversion   (Kendon — speakers hold mutual gaze ~40%)
    //   listening ~30% aversion   (Kendon — listeners hold it ~70%)
    //   idle      ~42% aversion   (unchanged: nobody is being addressed)
    // The scales below multiply the base hold/away durations, whose means are
    // 2.40 s and 1.20 s respectively. ExpectedAversionRatio() recomputes the
    // resulting ratio from whatever the base constants currently are, so retuning
    // GAZE_HOLD_*/GAZE_AWAY_* can be re-checked against these targets rather than
    // silently drifting away from them.
    //
    // Before this existed, `thinking` fell through to the idle branch AND was
    // held at a constant up-right bias, so the avatar stared at the viewer for
    // the whole RAG window (measured 1.5-4.5 s, 7.6 s cold) — well past the
    // ~3.3 s preferred / 5 s tolerable limit for unbroken mutual gaze.
    const float GAZE_HOLD_SCALE_THINK  = 0.33f, GAZE_AWAY_SCALE_THINK  = 2.34f;
    const float GAZE_HOLD_SCALE_SPEAK  = 0.60f, GAZE_AWAY_SCALE_SPEAK  = 1.80f;
    const float GAZE_HOLD_SCALE_LISTEN = 1.17f, GAZE_AWAY_SCALE_LISTEN = 1.00f;
    const float GAZE_HOLD_SCALE_IDLE   = 0.70f, GAZE_AWAY_SCALE_IDLE   = 1.00f;

    // While thinking, aversions skew up-and-away rather than landing anywhere on
    // the sphere — the recognisable "working it out" look. Applied as a bias on
    // the away target's vertical component (negative = up, see _eyeLookUp).
    const float THINK_AWAY_UP_BIAS = 0.09f;   // rad, ~5.2 deg
    const float THINK_AWAY_SCALE   = 1.35f;   // thinking aversions travel further

    // Fraction of gaze/saccade routed to the eyes (blendshapes) vs. the head bone.
    const float EYE_H_SCALE   = 0.60f;
    const float EYE_V_SCALE   = 0.65f;
    const float EYE_SACCADE   = 0.75f;

    // ── Look-at and vergence ─────────────────────────────────────────────────
    // "Centre" used to mean offset-zero in HEAD-LOCAL space, i.e. "look wherever
    // the head happens to point". Measured against the shipped camera that put
    // the avatar's gaze 9.3 deg above the lens on mobile and 4.3 deg on web —
    // both well past the ~1-2 deg at which people detect averted gaze. Centre now
    // means the actual camera, recomputed per frame, which also gives the eyes a
    // vestibulo-ocular reflex for free: they counter-rotate as the head moves
    // instead of the gaze sliding off the viewer every time it breathes.
    const float LOOKAT_MAX_PITCH = 0.45f;  // rad, clamp so extreme framings don't roll the eyes back
    const float LOOKAT_MAX_YAW   = 0.55f;

    // Both eyes were written the same weight, so they stayed parallel. At the
    // mobile camera distance of 0.385 m each eye should converge ~4.5 deg; at the
    // web distance of 1.985 m, ~0.9 deg. Parallel gaze on a near subject is the
    // thousand-yard stare.
    const float DEFAULT_IPD      = 0.062f;  // m, overwritten from the rig at Awake
    const float VERGENCE_MAX     = 0.14f;   // rad (~8 deg per eye), safety clamp

    // ── Lid follow and pupil ─────────────────────────────────────────────────
    const float LID_FOLLOW_UP   = 0.55f;   // Eye_UpperLid_Up_* at full upward gaze
    const float LID_FOLLOW_DOWN = 0.34f;   // partial lid closure at full downward gaze
    const float LID_FOLLOW_TAU  = 0.05f;   // lids lag the eye slightly, as they do in life
    const float PUPIL_AROUSAL_GAIN = 0.42f;
    const float PUPIL_DRIFT_AMP    = 0.07f;
    const float PUPIL_DRIFT_RATE   = 0.55f; // rad/s
    const float PUPIL_TAU          = 0.55f; // pupils are slow
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
    private float _blinkAmp = 1f;      // per-blink depth jitter
    private float _blinkValueR;        // right lid, lagged a touch behind the left

    // Constructed in Awake(), not as a field initializer — IdleMoment's constructor
    // calls Random.Range, which Unity forbids in MonoBehaviour field initializers.
    private IdleMoment _idleSmileMoment;
    private IdleMoment _idleBrowMoment;
    private bool       _idleBrowIsOuter;

    private float _smileOutput;
    private float _smileEyeOutput, _smileMouthOutput;
    private float _browInnerOutput, _browDownOutput, _browOuterOutput;

    /// <summary>Sentence sentiment for the utterance currently being spoken.</summary>
    public enum SpeechEmotion { Neutral, Positive, Warm, Concern, Question }
    private SpeechEmotion _speechEmotion = SpeechEmotion.Neutral;
    private float _emotionBlend;      // 0-1, follows whether audio is actually playing
    private float _emotionSmileLift, _emotionRelaxLift;
    private float _emotionBrowIn, _emotionBrowDown, _emotionBrowOut;
    private float _gazeSquint;

    // ── Breathing state ──────────────────────────────────────────────────────────
    private Transform  _waistBone, _chestBone;
    private Quaternion _waistRest, _chestRest;
    private Transform  _neckBone, _lClavicle, _rClavicle, _spine01;
    private Quaternion _neckRest, _lClavRest, _rClavRest, _spine01Rest;
    private float _swayA, _swayB;
    private float _shiftCurrent, _shiftTarget, _shiftTimer, _shiftNextInterval;
    private float _speechEnergy, _speechAccent;
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
    private float _saccadeStartV, _saccadeStartH;
    private float _saccadeMoveTimer, _saccadeMoveDuration;
    private float _nextSaccadeTime;

    // Conversational gaze phase FSM (center / away)
    private enum GazePhase { Center, Away }
    private GazePhase _gazePhase = GazePhase.Center;
    private float _gazePhaseTimer, _gazePhaseDuration;
    private float _gazeTargetH, _gazeTargetV;
    private float _gazeCurrentH, _gazeCurrentV;
    // Lagged copy the HEAD follows, so it trails the ballistic eye motion.
    private float _gazeHeadH, _gazeHeadV;
    private float _lookAtHeadPitch, _lookAtHeadYaw;
    // Ballistic-saccade traversal between _gazeStart* and _gazeTarget*.
    private float _gazeStartH, _gazeStartV;
    private float _gazeMoveTimer, _gazeMoveDuration;
    private float _utteranceRemaining;
    private bool  _yieldArmed;

    // Listening nod
    private float _nodCurrent, _nodTarget;
    private float _nodTimer, _nodNextInterval, _nodHoldTimer, _nodHoldDuration;
    private bool  _nodHolding;

    private IdleMoment _idleTiltMoment;
    private float _idleTiltSign;

    // Final composed head rotation (radians), consumed in LateUpdate
    private float _headPitch, _headYaw, _headRoll;
    // Final composed eye-look blendshape weights, per eye (they differ by the
    // vergence angle, so L and R can no longer share one value).
    private float _eyeUpL, _eyeDownL, _eyeLeftL, _eyeRightL;
    private float _eyeUpR, _eyeDownR, _eyeLeftR, _eyeRightR;
    // Upper-lid follow and pupil response, driven off the composed gaze.
    private float _upperLidUp, _lidFollowDown, _pupilWide, _pupilNarrow;

    [Header("Gaze target")]
    [Tooltip("Camera the avatar looks at. Left empty, resolves to Camera.main, " +
             "then the scene's render_focus_, then any enabled camera.")]
    public Camera gazeCamera;

    private Transform _leftEyeBone, _rightEyeBone;
    private float _ipd = DEFAULT_IPD;
    private float _lookAtPitch, _lookAtYaw, _vergence;
    // Same aim as _lookAt*, but measured from the head's REST pose instead of its
    // live one, so driving the head with it is not a feedback loop. See UpdateLookAt.
    private float _headAimPitch, _headAimYaw;
    private float _pupilPhase, _pupilTarget;

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

        // Postural chain. All optional — a character missing any of these just
        // loses that one channel rather than throwing.
        _spine01   = _avatar.FindBone("CC_Base_Spine01",     out _spine01Rest);
        _neckBone  = _avatar.FindBone("CC_Base_NeckTwist01", out _neckRest);
        _lClavicle = _avatar.FindBone("CC_Base_L_Clavicle",  out _lClavRest);
        _rClavicle = _avatar.FindBone("CC_Base_R_Clavicle",  out _rClavRest);
        _shiftNextInterval = Random.Range(SHIFT_INT_MIN, SHIFT_INT_MAX);

        _headBone = _avatar.FindBone("CC_Base_Head", out _headRest);
        if (_headBone == null) Debug.LogWarning("[IdleAnimator] 'CC_Base_Head' not found — gaze/tilt head motion disabled.");

        // Eye bones don't deform this mesh (gaze is blendshape-driven), but their
        // transforms are still the correct place to read the eye positions from —
        // that gives the look-at its origin and the true interpupillary distance
        // rather than an assumed one.
        Quaternion ignored;
        _leftEyeBone  = _avatar.FindBone("CC_Base_L_Eye", out ignored);
        _rightEyeBone = _avatar.FindBone("CC_Base_R_Eye", out ignored);
        if (_leftEyeBone != null && _rightEyeBone != null)
            _ipd = Vector3.Distance(_leftEyeBone.position, _rightEyeBone.position);
        else
            Debug.LogWarning("[IdleAnimator] eye bones not found — using default IPD and head origin for look-at.");

        _idleTiltMoment = new IdleMoment(IDLE_TILT_AMP, IDLE_TILT_HOLD_MIN, IDLE_TILT_HOLD_MAX, IDLE_TILT_INT_MIN, IDLE_TILT_INT_MAX, IDLE_TILT_SPEED,
            onTrigger: () => _idleTiltSign = Random.value < 0.5f ? 1f : -1f);

        _nextSaccadeTime = Random.Range(SACCADE_MIN_IDLE, SACCADE_MAX_IDLE);
        _gazePhaseDuration = Random.Range(GAZE_HOLD_MIN, GAZE_HOLD_MIN + GAZE_HOLD_RANGE);
        _nodNextInterval = Random.Range(NOD_INT_MIN, NOD_INT_MAX);

    }

    // Start, not Awake: AvatarController builds its shape map in its own Awake,
    // and Unity does not order Awake between components on the same GameObject.
    // Start always follows every Awake, so the map is populated by here.
    void Start()
    {
        _avatar.AuditShapes(DrivenShapeNames, nameof(IdleAnimator));
        // The blink FSM already produces its own 75/30/180 ms envelope; running it
        // through the generic smoother on top capped closure at 0.93, so the eyes
        // never fully shut. These channels are written verbatim instead.
        _avatar.SetUnsmoothedShapes(UnsmoothedShapeNames);
    }

    static readonly string[] UnsmoothedShapeNames =
    {
        "Eye_Blink_L", "Eye_Blink_R",
        "Eye_LowerLid_Up_L", "Eye_LowerLid_Up_R",
    };

    /// <summary>
    /// Every blendshape this component writes. Audited at startup so a typo or a
    /// character missing a shape surfaces as a warning instead of a channel that
    /// silently never moves.
    /// </summary>
    static readonly string[] DrivenShapeNames =
    {
        "Eye_Blink_L", "Eye_Blink_R",
        "Mouth_Corner_Pull_L", "Mouth_Corner_Pull_R",
        "Eye_Cheek_Raise_L", "Eye_Cheek_Raise_R",
        "Eye_Squint_Inner_L", "Eye_Squint_Inner_R",
        "Eye_Squint_L", "Eye_Squint_R",
        "Brow_Raise_In_L", "Brow_Raise_In_R",
        "Brow_Down_L", "Brow_Down_R",
        "Brow_Raise_Outer_L", "Brow_Raise_Outer_R",
        "Eye_Look_Up_L", "Eye_Look_Up_R",
        "Eye_Look_Down_L", "Eye_Look_Down_R",
        "Eye_Look_Left_L", "Eye_Look_Left_R",
        "Eye_Look_Right_L", "Eye_Look_Right_R",
        "Eye_UpperLid_Up_L", "Eye_UpperLid_Up_R",
        "Eye_LowerLid_Up_L", "Eye_LowerLid_Up_R",
        "Eye_Pupil_Wide_L", "Eye_Pupil_Wide_R",
        "Eye_Pupil_Narrow_L", "Eye_Pupil_Narrow_R",
        "C_CheekRaiseL_CornerPullL", "C_CheekRaiseR_CornerPullR",
        "C_CheekRaiseL_SquintInnerL", "C_CheekRaiseR_SquintInnerR",
        "C_BlinkL_CheekRaiseL", "C_BlinkR_CheekRaiseR",
        "C_BlinkL_SquintInnerL", "C_BlinkR_SquintInnerR",
        "C_BlinkL_SquintInnerL_CheekRaiseL", "C_BlinkR_SquintInnerR_CheekRaiseR",
    };

    void Update()
    {
        float dt = Time.deltaTime;
        _elapsed += dt;

        UpdateStateBlends(dt);
        // Gaze first: it feeds the brow-follow coupling below, and a gaze shift can
        // trigger a blink that should be serviced on the SAME frame.
        UpdateGaze(dt);
        UpdateBlink(dt);
        UpdateEmotion(dt);
        UpdateSmile(dt);
        UpdateEyebrows(dt);
        UpdateBreathing(dt);
        UpdatePosture(dt);
        UpdateSpeechMotion(dt);

        float cheekRaise  = Mathf.Clamp01(_smileEyeOutput * DUCHENNE_CHEEK_RAISE + _emotionRelaxLift);
        float squintInner = Mathf.Clamp01(_smileEyeOutput * DUCHENNE_SQUINT_INNER + _emotionRelaxLift * 0.5f);

        // A downward gaze partially closes the lid, so the blink shape carries both
        // the blink itself and the lid-follow. Max, not sum: a blink already at 1.0
        // can't close further.
        float lidL = Mathf.Clamp01(Mathf.Max(_blinkValue,  _lidFollowDown));
        float lidR = Mathf.Clamp01(Mathf.Max(_blinkValueR, _lidFollowDown));

        _idleWeights.Clear();
        _idleWeights["Eye_Blink_L"]          = lidL;
        _idleWeights["Eye_Blink_R"]          = lidR;
        // The rest of the blink: the brow dips and the lower lid rises with it.
        _idleWeights["Eye_LowerLid_Up_L"]    = _blinkValue  * BLINK_LOWER_LID;
        _idleWeights["Eye_LowerLid_Up_R"]    = _blinkValueR * BLINK_LOWER_LID;
        _idleWeights["Mouth_Corner_Pull_L"]  = _smileMouthOutput;
        _idleWeights["Mouth_Corner_Pull_R"]  = _smileMouthOutput;
        // AU6 — the half of a smile that makes it read as felt rather than posed.
        _idleWeights["Eye_Cheek_Raise_L"]    = cheekRaise;
        _idleWeights["Eye_Cheek_Raise_R"]    = cheekRaise;
        _idleWeights["Eye_Squint_Inner_L"]   = squintInner;
        _idleWeights["Eye_Squint_Inner_R"]   = squintInner;
        // Lateral gaze narrows the eye — a separate shape from the Duchenne
        // inner-squint, so the two don't fight over one channel.
        _idleWeights["Eye_Squint_L"]         = _gazeSquint;
        _idleWeights["Eye_Squint_R"]         = _gazeSquint;
        _idleWeights["Brow_Raise_In_L"]      = _browInnerOutput;
        _idleWeights["Brow_Raise_In_R"]      = _browInnerOutput;
        _idleWeights["Brow_Down_L"]          = Mathf.Clamp01(_browDownOutput + _blinkValue  * BLINK_BROW_DIP);
        _idleWeights["Brow_Down_R"]          = Mathf.Clamp01(_browDownOutput + _blinkValueR * BLINK_BROW_DIP);
        _idleWeights["Brow_Raise_Outer_L"]   = _browOuterOutput;
        _idleWeights["Brow_Raise_Outer_R"]   = _browOuterOutput;
        _idleWeights["Eye_Look_Up_L"]        = _eyeUpL;
        _idleWeights["Eye_Look_Up_R"]        = _eyeUpR;
        _idleWeights["Eye_Look_Down_L"]      = _eyeDownL;
        _idleWeights["Eye_Look_Down_R"]      = _eyeDownR;
        _idleWeights["Eye_Look_Left_L"]      = _eyeLeftL;
        _idleWeights["Eye_Look_Left_R"]      = _eyeLeftR;
        _idleWeights["Eye_Look_Right_L"]     = _eyeRightL;
        _idleWeights["Eye_Look_Right_R"]     = _eyeRightR;
        // Upper lid tracks vertical gaze; pupils respond to arousal.
        _idleWeights["Eye_UpperLid_Up_L"]    = _upperLidUp;
        _idleWeights["Eye_UpperLid_Up_R"]    = _upperLidUp;
        _idleWeights["Eye_Pupil_Wide_L"]     = _pupilWide;
        _idleWeights["Eye_Pupil_Wide_R"]     = _pupilWide;
        _idleWeights["Eye_Pupil_Narrow_L"]   = _pupilNarrow;
        _idleWeights["Eye_Pupil_Narrow_R"]   = _pupilNarrow;
        ApplyCombinationCorrectives(cheekRaise, squintInner);
        _avatar.SetIdleWeights(_idleWeights);
    }

    /// <summary>
    /// Drives the CC5 "C_*" combination shapes for the pairs we now co-activate.
    ///
    /// These correctives exist because two shapes fired together deform the same
    /// vertices twice — a cheek raise under a corner pull, or a blink over a
    /// raised cheek, buckles the skin without them. Reallusion's own expression
    /// system drives them; since we drive the base shapes ourselves, we own these
    /// too. Weight is the product of the drivers, which is the CC convention and
    /// is safely 0 whenever either driver is 0.
    /// </summary>
    void ApplyCombinationCorrectives(float cheekRaise, float squintInner)
    {
        float blink  = _blinkValue;
        float blinkR = _blinkValueR;
        float pull   = _smileMouthOutput;

        _idleWeights["C_CheekRaiseL_CornerPullL"] = cheekRaise * pull;
        _idleWeights["C_CheekRaiseR_CornerPullR"] = cheekRaise * pull;
        _idleWeights["C_CheekRaiseL_SquintInnerL"] = cheekRaise * squintInner;
        _idleWeights["C_CheekRaiseR_SquintInnerR"] = cheekRaise * squintInner;

        // Blinking through a held smile is constant, so these two matter as much
        // as the smile pair itself.
        _idleWeights["C_BlinkL_CheekRaiseL"]  = blink  * cheekRaise;
        _idleWeights["C_BlinkR_CheekRaiseR"]  = blinkR * cheekRaise;
        _idleWeights["C_BlinkL_SquintInnerL"] = blink  * squintInner;
        _idleWeights["C_BlinkR_SquintInnerR"] = blinkR * squintInner;
        _idleWeights["C_BlinkL_SquintInnerL_CheekRaiseL"] = blink  * squintInner * cheekRaise;
        _idleWeights["C_BlinkR_SquintInnerR_CheekRaiseR"] = blinkR * squintInner * cheekRaise;
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
        UpdateBlinkFsm(dt);
        FinishBlink(dt);
    }

    void UpdateBlinkFsm(float dt)
    {
        _blinkTimer += dt;
        switch (_blinkPhase)
        {
            case BlinkPhase.Idle:
                _blinkValue = 0f;
                if (_blinkTimer >= _blinkNext)
                {
                    _blinkIsDouble = Random.value < BLINK_DOUBLE_PROB;
                    _blinkAmp = RollBlinkAmplitude();
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

    /// <summary>Applies per-blink depth jitter and lags the right lid slightly.</summary>
    void FinishBlink(float dt)
    {
        _blinkValue *= _blinkAmp;
        _blinkValueR = ExpLerp(_blinkValueR, _blinkValue, dt, BLINK_R_LAG_TAU);
    }

    void RerollBlinkNext()
    {
        // Conversation RAISES blink rate (Bentivoglio 1997: 17/min at rest ->
        // 26/min in conversation). Listening counts as conversation too.
        float conversational = Mathf.Max(speakBlend, listenBlend);
        float mean = Mathf.Lerp(BLINK_MEAN_REST, BLINK_MEAN_SPEAK, conversational);
        // Log-normal with the requested MEAN: median = mean / exp(sigma^2 / 2).
        float median = mean / Mathf.Exp(BLINK_SIGMA * BLINK_SIGMA * 0.5f);
        _blinkNext = Mathf.Max(BLINK_MIN_GAP, median * Mathf.Exp(NextGaussian() * BLINK_SIGMA));
        _blinkAmp  = RollBlinkAmplitude();
    }

    /// <summary>Full closure by default; occasionally a genuine partial blink.</summary>
    static float RollBlinkAmplitude()
        => Random.value < BLINK_PARTIAL_PROB
            ? Random.Range(BLINK_PARTIAL_MIN, BLINK_PARTIAL_MAX)
            : 1f;

    /// <summary>Standard normal via Box-Muller. Unity only ships uniform Random.</summary>
    static float NextGaussian()
    {
        float u1 = Mathf.Max(1e-6f, Random.value);
        float u2 = Random.value;
        return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
    }

    /// <summary>
    /// Fires a blink now, unless one is already running. Called when the gaze
    /// makes a large shift — blinks and saccades are coupled in life, and an
    /// uncoupled blink timer is one of the things that reads as mechanical.
    /// </summary>
    void TriggerBlink()
    {
        if (_blinkPhase != BlinkPhase.Idle) return;
        _blinkIsDouble = Random.value < BLINK_DOUBLE_PROB;
        _blinkAmp = RollBlinkAmplitude();
        Advance(BlinkPhase.Closing);
    }

    // ── Smile ────────────────────────────────────────────────────────────────────

    void UpdateSmile(float dt)
    {
        float idleSmileOutput = _idleSmileMoment.Tick(dt, enabled: IsIdleEnough);
        float smileTarget = Mathf.Lerp(ACTIVE_SMILE_MIN, IDLE_SMILE, 1f - activeBlend * 0.75f);
        // Sentence sentiment lifts the smile on top of the resting/idle value, so a
        // positive line is actually spoken with a smile rather than a neutral face.
        _smileOutput = Mathf.Min(1f, Mathf.Max(smileTarget, idleSmileOutput) + _emotionSmileLift);

        // AU6 rides the smile signal directly; AU12 lags behind it, so the cheeks
        // and eyes lead the mouth into the expression the way a genuine smile does.
        _smileEyeOutput   = _smileOutput;
        _smileMouthOutput = ExpLerp(_smileMouthOutput, _smileOutput, dt, SMILE_MOUTH_LAG_TAU);
    }

    // ── Eyebrows ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the sentiment of the sentence about to be spoken. Called from the
    /// bridge when a play message arrives. Accepts the wire strings produced by
    /// detectSentiment; anything unrecognised falls back to neutral rather than
    /// throwing on the UnitySendMessage path.
    /// </summary>
    public void SetSpeechEmotion(string emotion)
    {
        switch (emotion)
        {
            case "positive": _speechEmotion = SpeechEmotion.Positive; break;
            case "warm":     _speechEmotion = SpeechEmotion.Warm;     break;
            case "concern":  _speechEmotion = SpeechEmotion.Concern;  break;
            case "question": _speechEmotion = SpeechEmotion.Question; break;
            default:         _speechEmotion = SpeechEmotion.Neutral;  break;
        }
    }

    /// <summary>
    /// Resolves the sentence-sentiment layer for this frame. Runs BEFORE the smile
    /// and brow passes so they consume this frame's values, not last frame's.
    /// </summary>
    void UpdateEmotion(float dt)
    {
        // Emotion only reads while the avatar is actually speaking, and decays
        // slowly afterwards so consecutive sentences stay emotionally continuous.
        bool voicing = _avatar.IsLipSyncActive || speakBlend > 0.5f;
        _emotionBlend = ExpLerp(_emotionBlend, voicing ? 1f : 0f, dt,
                                voicing ? EMOTION_RISE_TAU : EMOTION_FALL_TAU);
        float eb = _emotionBlend * speakBlend * EMOTION_SCALE;

        _emotionSmileLift = 0f; _emotionRelaxLift = 0f;
        _emotionBrowIn = 0f; _emotionBrowDown = 0f; _emotionBrowOut = 0f;
        if (eb <= 0.01f) return;

        switch (_speechEmotion)
        {
            case SpeechEmotion.Positive:
                _emotionSmileLift = EMO_POSITIVE_SMILE * eb;
                _emotionRelaxLift = EMO_POSITIVE_RELAX * eb;
                break;
            case SpeechEmotion.Warm:
                _emotionBrowIn    = EMO_WARM_BROW_IN * eb;
                _emotionRelaxLift = EMO_WARM_RELAX * eb;
                break;
            case SpeechEmotion.Concern:
                _emotionBrowIn   = EMO_CONCERN_BROW_IN * eb;
                _emotionBrowDown = EMO_CONCERN_BROW_DOWN * eb;
                break;
            case SpeechEmotion.Question:
                _emotionBrowOut = EMO_QUESTION_BROW_OUT * eb;
                _emotionBrowIn  = EMO_QUESTION_BROW_IN * eb;
                break;
        }
    }

    void UpdateEyebrows(float dt)
    {
        // Brows ride the eyes: up-gaze lifts them, down-gaze lowers them.
        float gazeUp   = Mathf.Max(_eyeUpL, _eyeUpR);
        float gazeDown = Mathf.Max(_eyeDownL, _eyeDownR);
        float lateral  = Mathf.Max(Mathf.Max(_eyeLeftL, _eyeRightL), Mathf.Max(_eyeLeftR, _eyeRightR));

        float browInner = listenBlend * LISTEN_BROW_INNER + empathyBlend * EMPATHY_BROW_INNER
                        + _emotionBrowIn;
        float browDown  = thinkBlend * THINK_BROW_DOWN + _emotionBrowDown
                        + gazeDown * GAZE_BROW_DOWN;
        float browOuter = _emotionBrowOut + gazeUp * GAZE_BROW_UP;
        _gazeSquint = ExpLerp(_gazeSquint, lateral * GAZE_SQUINT, dt, 0.08f);

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

    /// <summary>
    /// Postural life below the neck: continuous low sway plus an occasional weight
    /// shift. Cheap, and the difference between a person standing there and a
    /// mannequin with a talking head.
    /// </summary>
    void UpdatePosture(float dt)
    {
        // Two incommensurate frequencies so the sway never visibly loops.
        _swayA += dt * SWAY_RATE_A;
        _swayB += dt * SWAY_RATE_B;

        _shiftTimer += dt;
        if (_shiftTimer >= _shiftNextInterval)
        {
            _shiftTimer = 0f;
            _shiftNextInterval = Random.Range(SHIFT_INT_MIN, SHIFT_INT_MAX);
            // Shift to the other side, not a random one — you don't shift onto the
            // foot you were already on.
            _shiftTarget = Mathf.Abs(_shiftCurrent) < SHIFT_AMP * 0.5f
                ? (Random.value < 0.5f ? -SHIFT_AMP : SHIFT_AMP)
                : -Mathf.Sign(_shiftCurrent) * SHIFT_AMP;
        }
        _shiftCurrent = ExpLerp(_shiftCurrent, _shiftTarget, dt, SHIFT_TAU);
    }

    /// <summary>
    /// Head motion coupled to what is actually being said. Rides the articulation
    /// envelope the lip-sync engine is already producing, so wide-open stressed
    /// syllables get a small head accent. No prosody model is invented here — the
    /// signal is one the pipeline already computes.
    /// </summary>
    void UpdateSpeechMotion(float dt)
    {
        float openness = _avatar.GetCurrentWeight("jaw_drive");
        if (openness <= 0f) openness = _avatar.GetCurrentWeight("Merged_Open_Mouth");
        _speechEnergy = ExpLerp(_speechEnergy, Mathf.Clamp01(openness), dt, 0.05f);
        // The head follows the mouth with a little mass behind it.
        _speechAccent = ExpLerp(_speechAccent, _speechEnergy * speakBlend, dt, SPEECH_ACCENT_TAU);
    }

    // ── Gaze / saccades / head tilts ─────────────────────────────────────────────
    //
    // Eyes: this rig's eye bones don't drive any visible mesh deformation (see
    // task-6 finding — confirmed via BakeMesh vertex comparison), so gaze is
    // expressed as Eye_Look_Up/Down/Left/Right blendshapes instead of bone
    // rotation. Head: bone-driven, using the confirmed axis mapping — local X =
    // pitch (+ = down), Y = yaw, Z = roll.
    /// <summary>
    /// Where the viewer actually is, expressed in head-local pitch/yaw, plus the
    /// convergence angle each eye needs at that distance. Recomputed every frame
    /// off the LIVE head transform, so head motion is automatically compensated.
    /// </summary>
    void UpdateLookAt()
    {
        _lookAtPitch = 0f; _lookAtYaw = 0f; _vergence = 0f;
        _headAimPitch = 0f; _headAimYaw = 0f;
        var cam = ResolveGazeCamera();
        if (cam == null || _headBone == null) return;

        Vector3 eyeMid = EyeMidpoint();
        Vector3 toCam  = cam.transform.position - eyeMid;
        float distance = toCam.magnitude;
        if (distance < 1e-4f) return;

        Vector3 dir = toCam / distance;

        // ── Two frames, because the eyes and the head need different questions
        // answered ───────────────────────────────────────────────────────────
        // EYES: "where is the viewer relative to where my head is pointing RIGHT
        // NOW?" That has to be the live head transform — it is what gives the
        // eyes a vestibulo-ocular reflex, counter-rotating as the head moves so
        // the gaze stays put.
        //
        // HEAD: the same question is a FEEDBACK LOOP. The head's target was being
        // measured in the head's own moving frame, so every roll, breath and nod
        // changed the head's own target and it spent its time chasing itself.
        // Measured in Idle after the head-assist gate landed: the largest
        // remaining head motion was 0.43 deg of pitch at ~0.25 Hz that was not
        // locked to the gaze cadence at all — this loop. The head is asked
        // instead where the viewer is relative to its REST pose, which nothing
        // the animator does can move.
        Vector3 local = _headBone.InverseTransformDirection(dir);
        // Positive pitch = looking DOWN, matching the Eye_Look_Down sign convention.
        _lookAtPitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)), -LOOKAT_MAX_PITCH, LOOKAT_MAX_PITCH);
        _lookAtYaw   = Mathf.Clamp(Mathf.Atan2(local.x, Mathf.Max(local.z, 0.01f)), -LOOKAT_MAX_YAW, LOOKAT_MAX_YAW);

        // The head's rest frame: what the head bone's orientation would be if the
        // animator wrote nothing this frame. Follows the body, ignores the head.
        Quaternion restWorld = _headBone.parent != null
            ? _headBone.parent.rotation * _headRest
            : _headRest;
        Vector3 stable = Quaternion.Inverse(restWorld) * dir;
        _headAimPitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(stable.y, -1f, 1f)), -LOOKAT_MAX_PITCH, LOOKAT_MAX_PITCH);
        _headAimYaw   = Mathf.Clamp(Mathf.Atan2(stable.x, Mathf.Max(stable.z, 0.01f)), -LOOKAT_MAX_YAW, LOOKAT_MAX_YAW);
        // Each eye rotates inward by atan(halfIPD / distance).
        _vergence    = Mathf.Min(Mathf.Atan2(_ipd * 0.5f, distance), VERGENCE_MAX);
    }

    Vector3 EyeMidpoint()
    {
        if (_leftEyeBone != null && _rightEyeBone != null)
            return (_leftEyeBone.position + _rightEyeBone.position) * 0.5f;
        return _headBone != null ? _headBone.position : transform.position;
    }

    Camera ResolveGazeCamera()
    {
        if (gazeCamera != null) return gazeCamera;
        if (Camera.main != null) { gazeCamera = Camera.main; return gazeCamera; }
        // The scene's camera is deliberately untagged, so Camera.main is null here.
        var byName = GameObject.Find("render_focus_");
        if (byName != null) { gazeCamera = byName.GetComponent<Camera>(); if (gazeCamera != null) return gazeCamera; }
        var all = Camera.allCameras;
        if (all.Length > 0) gazeCamera = all[0];
        return gazeCamera;
    }

    void UpdateGaze(float dt)
    {
        UpdateLookAt();
        UpdateSaccades(dt);
        UpdateGazePhase(dt);
        UpdateNod(dt);

        float idleTiltOutput = _idleTiltMoment.Tick(dt, enabled: IsIdleEnough) * _idleTiltSign;

        // Aversion offsets ride ON TOP of the look-at aim: "centre" is the viewer,
        // and an aversion is a departure from them, not from head-forward.
        // The aversion offset, before it is split between the eyes and the head.
        float aversionV = Mathf.Lerp(_gazeCurrentV, THINK_GAZE_V, thinkBlend);
        float aversionH = Mathf.Lerp(_gazeCurrentH, THINK_GAZE_H, thinkBlend);

        // How much of THIS shift the head is allowed to take. Small aversions get
        // nothing, which is what keeps the head still instead of restless.
        // Deliberately keyed off the aversion alone: the look-at aim is a sustained
        // POSTURE, not a shift, and a posture the head does settle into — holding
        // the eyes eccentric for minutes is fatiguing in a way a 300 ms glance
        // is not.
        float assist = HeadAssist(new Vector2(aversionH, aversionV).magnitude * Mathf.Rad2Deg);

        // The head tracks a LAGGED, amplitude-gated copy of the same aim, so it
        // arrives after the eyes instead of snapping with them.
        _gazeHeadV = ExpLerp(_gazeHeadV, aversionV * assist, dt, HEAD_FOLLOW_TAU);
        _gazeHeadH = ExpLerp(_gazeHeadH, aversionH * assist, dt, HEAD_FOLLOW_TAU);
        _lookAtHeadPitch = ExpLerp(_lookAtHeadPitch, _headAimPitch, dt, HEAD_FOLLOW_TAU);
        _lookAtHeadYaw   = ExpLerp(_lookAtHeadYaw,   _headAimYaw,   dt, HEAD_FOLLOW_TAU);
        float headLookV = _lookAtHeadPitch + _gazeHeadV;

        _headPitch = headLookV * HEAD_V_SHARE
                   + _saccadeCurrentV * HEAD_SACCADE_SHARE
                   + empathyBlend * EMPATHY_TILT_X
                   + listenBlend * LISTEN_TILT_X
                   + _nodCurrent * listenBlend
                   // Stressed syllables pull the head down a touch, as they do in life.
                   + _speechAccent * SPEECH_ACCENT_GAIN;

        // Horizontal (yaw): same routing split for the head's share of gaze.
        float headLookH = _lookAtHeadYaw + _gazeHeadH;
        float yawSign = gazeHorizontalFlip ? -1f : 1f;
        _headYaw = yawSign * (headLookH * HEAD_H_SHARE + _saccadeCurrentH * HEAD_SACCADE_SHARE);

        // Roll: ambient sine + per-state tilt biases + idle tilt moments.
        _headRoll = Mathf.Sin(_elapsed * HEAD_ROLL_FREQ) * HEAD_ROLL_AMP
                  + Mathf.Sin(_swayB * 1.7f) * SPEECH_SWAY_GAIN * speakBlend
                  + thinkBlend * THINK_TILT_Z
                  + empathyBlend * EMPATHY_TILT_Z
                  + waitBlend * WAIT_TILT_Z
                  + idleTiltOutput;

        // Eyes get the majority share of gaze/saccade, expressed as blendshapes.
        // The eye share is what the head did NOT take, so the two always sum to
        // the full look-at angle and the gaze lands on the viewer.
        // Whatever share of the aversion the head declined, the eyes pick up, so
        // gating the head never quietly shortens the aversion itself — a 10 deg
        // look away stays a 10 deg look away, it just stops dragging the neck
        // along with it.
        float eyeV = _lookAtPitch * EYE_V_SCALE
                   + aversionV * (1f - HEAD_V_SHARE * assist)
                   + _saccadeCurrentV * EYE_SACCADE;
        float eyeH = yawSign * (_lookAtYaw * EYE_H_SCALE
                   + aversionH * (1f - HEAD_H_SHARE * assist)
                   + _saccadeCurrentH * EYE_SACCADE);

        // Converge: the left eye (at -x) rotates toward +x and the right toward
        // -x, by _vergence each.
        float eyeHL = eyeH + yawSign * _vergence;
        float eyeHR = eyeH - yawSign * _vergence;

        _eyeDownL  = Mathf.Clamp01( eyeV  / EYE_LOOK_MAX_ANGLE);
        _eyeUpL    = Mathf.Clamp01(-eyeV  / EYE_LOOK_MAX_ANGLE);
        _eyeRightL = Mathf.Clamp01( eyeHL / EYE_LOOK_MAX_ANGLE);
        _eyeLeftL  = Mathf.Clamp01(-eyeHL / EYE_LOOK_MAX_ANGLE);

        _eyeDownR  = Mathf.Clamp01( eyeV  / EYE_LOOK_MAX_ANGLE);
        _eyeUpR    = Mathf.Clamp01(-eyeV  / EYE_LOOK_MAX_ANGLE);
        _eyeRightR = Mathf.Clamp01( eyeHR / EYE_LOOK_MAX_ANGLE);
        _eyeLeftR  = Mathf.Clamp01(-eyeHR / EYE_LOOK_MAX_ANGLE);

        UpdateLidAndPupil(dt, eyeV);
    }

    /// <summary>
    /// Two channels the rig has always carried and nothing has ever driven.
    ///
    /// Lid follow: the upper lid tracks vertical gaze — it rides up when the eyes
    /// go up and droops when they go down. A lid line that stays put wherever the
    /// eyes point is a strong doll cue, and it is the reason the avatar reads as
    /// wide-eyed when it looks down.
    ///
    /// Pupil: dilates with cognitive load and arousal. Slow, small, and never
    /// consciously noticed — but its absence is part of why still eyes look inert.
    /// </summary>
    void UpdateLidAndPupil(float dt, float eyeV)
    {
        float up   = Mathf.Clamp01(-eyeV / EYE_LOOK_MAX_ANGLE);
        float down = Mathf.Clamp01( eyeV / EYE_LOOK_MAX_ANGLE);
        _upperLidUp    = ExpLerp(_upperLidUp,    up   * LID_FOLLOW_UP,   dt, LID_FOLLOW_TAU);
        _lidFollowDown = ExpLerp(_lidFollowDown, down * LID_FOLLOW_DOWN, dt, LID_FOLLOW_TAU);

        // Arousal proxy: thinking and speaking dilate, resting idles back down.
        float arousal = Mathf.Clamp01(thinkBlend * 0.9f + speakBlend * 0.55f + listenBlend * 0.35f);
        _pupilPhase += dt * PUPIL_DRIFT_RATE;
        float drift = Mathf.Sin(_pupilPhase) * PUPIL_DRIFT_AMP;
        float target = arousal * PUPIL_AROUSAL_GAIN + drift;
        _pupilTarget = ExpLerp(_pupilTarget, target, dt, PUPIL_TAU);
        _pupilWide   = Mathf.Clamp01( _pupilTarget);
        _pupilNarrow = Mathf.Clamp01(-_pupilTarget);
    }

    void UpdateSaccades(float dt)
    {
        if (_elapsed > _nextSaccadeTime)
        {
            _saccadeStartV = _saccadeCurrentV;
            _saccadeStartH = _saccadeCurrentH;

            // Two populations, not one. Micro-saccades keep a fixating eye alive;
            // feature saccades are the eye-to-eye-to-mouth scan people actually do
            // when looking at a face. The old single population was +/-0.8 deg,
            // which after the EYE_LOOK_MAX_ANGLE divide came out at a blendshape
            // weight of 0.02 — literally invisible.
            bool feature = Random.value < SACCADE_FEATURE_PROB;
            float ampDeg = feature
                ? Random.Range(SACCADE_FEATURE_MIN_DEG, SACCADE_FEATURE_MAX_DEG)
                : Random.Range(SACCADE_MICRO_MIN_DEG, SACCADE_MICRO_MAX_DEG);
            // Human saccades are strongly HORIZONTALLY dominant; the shipped
            // constants had the vertical amplitude larger, which is backwards.
            float angle = Random.value * Mathf.PI * 2f;
            float amp   = ampDeg * Mathf.Deg2Rad;
            _saccadeTargetH = Mathf.Cos(angle) * amp;
            _saccadeTargetV = Mathf.Sin(angle) * amp * SACCADE_VERTICAL_RATIO;

            // Ballistic, on the main sequence — same law as the gaze-shift saccades.
            float travelDeg = new Vector2(_saccadeTargetH - _saccadeStartH, _saccadeTargetV - _saccadeStartV).magnitude * Mathf.Rad2Deg;
            _saccadeMoveDuration = SaccadeDuration(travelDeg);
            _saccadeMoveTimer = 0f;

            float min = listenBlend > 0.5f ? SACCADE_MIN_LISTEN : SACCADE_MIN_IDLE;
            float max = listenBlend > 0.5f ? SACCADE_MAX_LISTEN : SACCADE_MAX_IDLE;
            _nextSaccadeTime = _elapsed + min + Random.value * (max - min);
        }

        if (_saccadeMoveTimer < _saccadeMoveDuration)
        {
            _saccadeMoveTimer += dt;
            float s = MinJerk(Mathf.Clamp01(_saccadeMoveTimer / _saccadeMoveDuration));
            _saccadeCurrentV = Mathf.Lerp(_saccadeStartV, _saccadeTargetV, s);
            _saccadeCurrentH = Mathf.Lerp(_saccadeStartH, _saccadeTargetH, s);
        }
        else
        {
            _saccadeCurrentV = _saccadeTargetV;
            _saccadeCurrentH = _saccadeTargetH;
        }
    }

    /// <summary>
    /// Called by the bridge when an utterance starts playing. Kicks the
    /// turn-taking aversion and arms the turn-yield return.
    /// </summary>
    public void OnUtteranceStart(float duration)
    {
        _utteranceRemaining = Mathf.Max(0f, duration);
        _yieldArmed = duration > TURN_YIELD_LEAD * 2f;   // too short to be worth it
        if (Random.value >= TURN_AVERT_PROB) return;
        ForceGazeAway(TURN_AVERT_DUR);
    }

    /// <summary>Called by the bridge on stop / utterance end.</summary>
    public void OnUtteranceEnd()
    {
        _utteranceRemaining = 0f;
        _yieldArmed = false;
    }

    /// <summary>What an aversion is FOR. Andrist et al. measured direction
    /// separately per function, so the caller has to say which one it is.</summary>
    enum AversionFunction { Cognitive, Intimacy, TurnTaking }

    /// <summary>
    /// Chooses where this aversion lands. Returns true if it was a wider "wander".
    ///
    /// Every aversion used to move BOTH axes with an independently randomised
    /// sign, so each one landed in one of four diagonal quadrants and consecutive
    /// aversions hopped between them — no real gaze does that. Andrist et al.
    /// measured aversion direction as a CATEGORICAL choice — up, down, or to the
    /// side — in proportions that depend on what the aversion is for:
    ///
    ///                  up      down    side
    ///     cognitive    39.3%   29.4%   31.3%   (thinking through an answer)
    ///     intimacy     13.7%   28.8%   57.5%   (the ordinary hold/away cycle)
    ///     turn-taking  21.3%   29.5%   49.2%   (at utterance start)
    ///
    /// Besides being what the measurements say, this halves the number of axes a
    /// single aversion disturbs: a "side" aversion has no vertical component for
    /// the head to follow, and an "up" one has no horizontal component.
    /// </summary>
    bool PickAwayTarget(AversionFunction fn)
    {
        bool wander   = Random.value < GAZE_WANDER_PROB;
        bool thinking = fn == AversionFunction.Cognitive;
        float h = wander ? GAZE_WANDER_H : GAZE_AWAY_H;
        float v = wander ? GAZE_WANDER_V : GAZE_AWAY_V;
        if (thinking) { h *= THINK_AWAY_SCALE; v *= THINK_AWAY_SCALE; }

        float pUp, pDown;
        switch (fn)
        {
            case AversionFunction.Cognitive:  pUp = 0.393f; pDown = 0.294f; break;
            case AversionFunction.TurnTaking: pUp = 0.213f; pDown = 0.295f; break;
            default:                          pUp = 0.137f; pDown = 0.288f; break;
        }

        float r = Random.value;
        if      (r < pUp)         { _gazeTargetH = 0f; _gazeTargetV = -v; }  // -V is up
        else if (r < pUp + pDown) { _gazeTargetH = 0f; _gazeTargetV =  v; }
        else                      { _gazeTargetH = (Random.value < 0.5f ? -1f : 1f) * h;
                                    _gazeTargetV = 0f; }

        // The thinking skew rides on top of whichever direction was drawn — the
        // recognisable "working it out" look is up-AND-away, not merely up.
        if (thinking) _gazeTargetV -= THINK_AWAY_UP_BIAS;
        return wander;
    }

    void ForceGazeAway(float seconds)
    {
        _gazePhase = GazePhase.Away;
        _gazePhaseTimer = 0f;
        _gazePhaseDuration = seconds;
        PickAwayTarget(AversionFunction.TurnTaking);
        BeginGazeSaccade();
    }

    /// <param name="holdSeconds">Explicit hold. Negative uses the normal randomised
    /// hold for the current state.</param>
    void ForceGazeCentre(float holdSeconds = -1f)
    {
        _gazePhase = GazePhase.Center;
        _gazePhaseTimer = 0f;
        _gazePhaseDuration = holdSeconds >= 0f
            ? holdSeconds
            : (GAZE_HOLD_MIN + Random.value * GAZE_HOLD_RANGE) * HoldScale();
        _gazeTargetH = 0f; _gazeTargetV = 0f;
        BeginGazeSaccade();
    }

    void UpdateGazePhase(float dt)
    {
        // Turn-yield: come back to the viewer just before finishing speaking. This
        // is the gaze cue that hands the turn over, and it has to pre-empt the
        // ordinary hold/away cycle rather than wait for it.
        if (_utteranceRemaining > 0f)
        {
            _utteranceRemaining -= dt;
            if (_yieldArmed && _utteranceRemaining <= TURN_YIELD_LEAD)
            {
                _yieldArmed = false;
                // Hold the viewer until the utterance is actually over, plus a beat.
                // The first version handed this back to the ordinary hold/away cycle,
                // whose randomised duration could expire mid-yield and pull the gaze
                // away again before the sentence finished — measured landing only
                // 8 times out of 15.
                ForceGazeCentre(_utteranceRemaining + TURN_YIELD_HOLD_AFTER);
            }
        }

        _gazePhaseTimer += dt;
        if (_gazePhaseTimer >= _gazePhaseDuration)
        {
            _gazePhaseTimer = 0f;
            if (_gazePhase == GazePhase.Center)
            {
                _gazePhase = GazePhase.Away;
                // Thinking aversions are cognitive; everything else in the
                // ordinary hold/away cycle is intimacy regulation.
                bool wander = PickAwayTarget(thinkBlend > 0.5f
                    ? AversionFunction.Cognitive
                    : AversionFunction.Intimacy);
                float awayRange = wander ? GAZE_AWAY_RANGE * 1.6f : GAZE_AWAY_RANGE;
                _gazePhaseDuration = (GAZE_AWAY_MIN + Random.value * awayRange) * AwayScale();
            }
            else
            {
                _gazePhase = GazePhase.Center;
                _gazeTargetH = 0f;
                _gazeTargetV = 0f;
                _gazePhaseDuration = (GAZE_HOLD_MIN + Random.value * GAZE_HOLD_RANGE) * HoldScale();
            }
            BeginGazeSaccade();
            // Blinks cluster on gaze shifts. Only for shifts big enough to be worth
            // one — a 2 deg correction doesn't earn a blink.
            float shiftDeg = new Vector2(_gazeTargetH - _gazeStartH, _gazeTargetV - _gazeStartV).magnitude * Mathf.Rad2Deg;
            if (shiftDeg > 6f && Random.value < BLINK_ON_GAZE_SHIFT_PROB) TriggerBlink();
        }

        // Traverse ballistically, then sit exactly on the target until the next
        // phase change — so a "centre" phase really is centred.
        if (_gazeMoveTimer < _gazeMoveDuration)
        {
            _gazeMoveTimer += dt;
            float u = Mathf.Clamp01(_gazeMoveTimer / _gazeMoveDuration);
            float s = MinJerk(u);
            _gazeCurrentH = Mathf.Lerp(_gazeStartH, _gazeTargetH, s);
            _gazeCurrentV = Mathf.Lerp(_gazeStartV, _gazeTargetV, s);
        }
        else
        {
            _gazeCurrentH = _gazeTargetH;
            _gazeCurrentV = _gazeTargetV;
        }
    }

    /// <summary>
    /// Starts a ballistic traversal from wherever the gaze currently is to the
    /// freshly-chosen target, with a main-sequence duration for that amplitude.
    /// </summary>
    void BeginGazeSaccade()
    {
        _gazeStartH = _gazeCurrentH;
        _gazeStartV = _gazeCurrentV;
        float amplitudeDeg = new Vector2(_gazeTargetH - _gazeStartH, _gazeTargetV - _gazeStartV).magnitude * Mathf.Rad2Deg;
        _gazeMoveDuration = SaccadeDuration(amplitudeDeg);
        _gazeMoveTimer = 0f;
    }

    /// <summary>
    /// Main-sequence saccade duration for an amplitude, with a floor of two frames.
    ///
    /// A real 60 ms saccade spans 3-4 frames at 60fps and reads as fast but
    /// continuous. Below ~20fps the same 60 ms lands inside a single frame and the
    /// eye appears to teleport, which reads as a twitch rather than a saccade. The
    /// two-frame floor is inert at healthy frame rates (33 ms at 60fps, under every
    /// real saccade duration) and only engages when the renderer cannot resolve the
    /// motion — degrading to "fast" instead of "instant".
    /// </summary>
    static float SaccadeDuration(float amplitudeDeg)
    {
        float physiological = Mathf.Max(SACCADE_MIN_DUR, SACCADE_BASE_DUR + SACCADE_DUR_PER_DEG * amplitudeDeg);
        return Mathf.Max(physiological, 2f * Time.deltaTime);
    }

    /// <summary>Minimum-jerk position profile (smootherstep) — the standard
    /// approximation to a saccade's velocity curve: accelerate, peak, decelerate
    /// onto the target with zero terminal velocity.</summary>
    static float MinJerk(float u)
    {
        u = Mathf.Clamp01(u);
        return u * u * u * (u * (u * 6f - 15f) + 10f);
    }

    // Thinking wins over speaking wins over listening: the states are mutually
    // exclusive at the source, but the blends cross over during a transition and
    // the ordering decides which side of that crossover the next phase samples.
    // Internal (not private) so the EditMode tests can assert the resulting
    // aversion ratios against the literature targets.
    internal float HoldScale()
        => thinkBlend  > 0.5f ? GAZE_HOLD_SCALE_THINK
         : speakBlend  > 0.5f ? GAZE_HOLD_SCALE_SPEAK
         : listenBlend > 0.5f ? GAZE_HOLD_SCALE_LISTEN
         : GAZE_HOLD_SCALE_IDLE;

    internal float AwayScale()
        => thinkBlend  > 0.5f ? GAZE_AWAY_SCALE_THINK
         : speakBlend  > 0.5f ? GAZE_AWAY_SCALE_SPEAK
         : listenBlend > 0.5f ? GAZE_AWAY_SCALE_LISTEN
         : GAZE_AWAY_SCALE_IDLE;

    /// <summary>
    /// Expected fraction of time spent looking away from the viewer, from the
    /// current state's scales and the base hold/away distributions. Exposed for
    /// the tests and for the avatar debug overlay; not used by the animation.
    /// </summary>
    /// <summary>
    /// True while the avatar is meant to be looking AT the viewer and has finished
    /// travelling there. Lets a test separate "the look-at is aimed wrong" from
    /// "the avatar is deliberately looking away right now", which are otherwise
    /// indistinguishable in an averaged gaze-error measurement.
    /// </summary>
    internal bool IsHoldingMutualGaze =>
        _gazePhase == GazePhase.Center && _gazeMoveTimer >= _gazeMoveDuration;

    internal float ExpectedAversionRatio()
    {
        // Mean of the away duration, accounting for the wander branch widening
        // the range by 1.6x with probability GAZE_WANDER_PROB.
        float meanAwayBase = (1f - GAZE_WANDER_PROB) * (GAZE_AWAY_MIN + GAZE_AWAY_RANGE * 0.5f)
                           + GAZE_WANDER_PROB * (GAZE_AWAY_MIN + GAZE_AWAY_RANGE * 1.6f * 0.5f);
        float meanHold = (GAZE_HOLD_MIN + GAZE_HOLD_RANGE * 0.5f) * HoldScale();
        float meanAway = meanAwayBase * AwayScale();
        return meanAway / (meanHold + meanAway);
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
        // Sway scales up with engagement — a person talking moves more than one
        // standing idle. Two frequencies so it never visibly repeats.
        float swayAmp = speakBlend > 0.5f ? SWAY_SPEAK
                      : activeBlend > 0.5f ? SWAY_ACTIVE : SWAY_IDLE;
        float swayZ = (Mathf.Sin(_swayA * Mathf.PI * 2f) * 0.7f
                     + Mathf.Sin(_swayB * Mathf.PI * 2f) * 0.3f) * swayAmp;

        if (_waistBone != null)
            _waistBone.localRotation = _waistRest * Quaternion.Euler(
                _breathValue * BREATH_SPINE_AMP * Mathf.Rad2Deg,
                0f,
                (swayZ + _shiftCurrent) * Mathf.Rad2Deg);
        if (_spine01 != null)
            _spine01.localRotation = _spine01Rest * Quaternion.Euler(
                0f, 0f, -swayZ * 0.45f * Mathf.Rad2Deg);   // counter-curve, as a spine does
        if (_chestBone != null)
            _chestBone.localRotation = _chestRest * Quaternion.Euler(
                _breathValue * BREATH_CHEST_AMP * Mathf.Rad2Deg, 0f, 0f);
        // Breath travels up the neck and out into the shoulders and arms.
        if (_neckBone != null)
            _neckBone.localRotation = _neckRest * Quaternion.Euler(
                _breathValue * BREATH_NECK_AMP * Mathf.Rad2Deg, 0f, 0f);
        if (_lClavicle != null)
            _lClavicle.localRotation = _lClavRest * Quaternion.Euler(_breathValue * CLAVICLE_BREATH * Mathf.Rad2Deg, 0f, 0f);
        if (_rClavicle != null)
            _rClavicle.localRotation = _rClavRest * Quaternion.Euler(_breathValue * CLAVICLE_BREATH * Mathf.Rad2Deg, 0f, 0f);
        // The upper arms are deliberately NOT driven here. WebArmPoser
        // (WebGLCameraFraming.cs) rewrites CC_Base_L/R_Upperarm every LateUpdate on
        // web builds to swing the T-pose arms down, and LateUpdate order between
        // components on one GameObject is undefined — writing them from here would
        // intermittently undo that pose and snap the arms back out sideways.
        // The clavicle carries the shoulder rise, which is the visible part anyway.
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
