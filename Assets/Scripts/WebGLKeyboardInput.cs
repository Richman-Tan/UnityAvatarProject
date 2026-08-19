using UnityEngine;

/// <summary>
/// WebGL-only: stop Unity swallowing the page's keyboard.
///
/// Unity's WebGL input backend defaults to captureAllKeyboardInput = true, which
/// binds its key callbacks at the DOCUMENT level and calls preventDefault() with no
/// check on event.target. On a page that is mostly HTML — ours is: the avatar canvas
/// is one element among the app's chat, search and study forms — that cancels the
/// keypress default action, which IS character insertion. Every text box on the page
/// takes focus, shows a caret, and then silently drops what you type.
///
/// Measured on the deployed build before this change: keydown arrived unprevented,
/// keypress arrived defaultPrevented at the first window capture-phase listener, and
/// no beforeinput/input event ever fired. The web app boots Unity eagerly on every
/// route (apps/web/src/main.jsx), which is why it hit every screen and not just the
/// avatar ones.
///
/// With this false, Unity binds to the canvas instead, so it still receives keys when
/// the canvas has focus and the rest of the page behaves normally. The app never gives
/// the canvas keyboard focus (it carries no tabindex — see unityBridge.js), and the
/// avatar takes no keyboard input, so nothing is lost.
///
/// Applied via RuntimeInitializeOnLoadMethod, so no scene or prefab changes and the
/// mobile UaaL builds are untouched. Matches WebGLCameraFraming.cs.
/// </summary>
public static class WebGLKeyboardInput
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Apply()
    {
        WebGLInput.captureAllKeyboardInput = false;
        Debug.Log("[WebGLKeyboardInput] captureAllKeyboardInput = false (HTML inputs keep the keyboard)");
    }
#endif
}
