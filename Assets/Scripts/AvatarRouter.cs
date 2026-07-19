using UnityEngine;

/// <summary>
/// Single UnitySendMessage target for the native UaaL bridge — replaces the
/// old direct binding to the HD_Aaron GameObject so one scene can host
/// multiple characters.
///
/// Called from native (Swift) via:
///   UnityFramework.sendMessageToGOWithName("AvatarRouter", "ReceiveBridgeMessage", json)
///
/// Handles ONE message type itself:
///   { "type": "setCharacter", "id": "aaron" | "ariana" }
/// — idempotent: a repeat for the already-active character is a no-op (the
/// Swift side re-sends the selection before every play and on delayed boot
/// retries, because UnitySendMessage drops messages sent before the first
/// scene loads). A real switch stops the outgoing character, toggles the two
/// roots' active states, and persists the choice to PlayerPrefs so the next
/// cold boot shows the right character before any native message arrives.
///
/// EVERY other message type (play/stop/future) is forwarded verbatim to the
/// active character's <see cref="NativeBridgeReceiver"/> — the router never
/// interprets payloads, so protocol additions flow through untouched.
/// </summary>
public class AvatarRouter : MonoBehaviour
{
    const string PrefsKey  = "avatar.characterId";
    const string DefaultId = "aaron";

    [Header("Character roots (scene siblings)")]
    public GameObject aaronRoot;
    public GameObject arianaRoot;

    NativeBridgeReceiver _aaronReceiver;
    NativeBridgeReceiver _arianaReceiver;
    string _activeId;

    void Awake()
    {
        // includeInactive: the deselected character's root ships disabled in
        // the baked scene, and GetComponentInChildren skips disabled objects
        // by default.
        _aaronReceiver  = aaronRoot  != null ? aaronRoot.GetComponentInChildren<NativeBridgeReceiver>(true)  : null;
        _arianaReceiver = arianaRoot != null ? arianaRoot.GetComponentInChildren<NativeBridgeReceiver>(true) : null;

        // Apply the persisted selection before first render so relaunching
        // with the non-default character selected never flashes the wrong one.
        Apply(PlayerPrefs.GetString(PrefsKey, DefaultId), persist: false);
    }

    /// <summary>Entry point invoked by the native bridge via UnitySendMessage.</summary>
    public void ReceiveBridgeMessage(string json)
    {
        string type = CC4MessageProtocol.ParseStringField(json, "type");
        if (type == "setCharacter")
        {
            Apply(CC4MessageProtocol.ParseStringField(json, "id"), persist: true);
            return;
        }

        var receiver = ActiveReceiver();
        if (receiver == null)
        {
            Debug.LogWarning($"[AvatarRouter] No active character receiver for message type '{type}'.");
            return;
        }
        receiver.ReceiveBridgeMessage(json);
    }

    NativeBridgeReceiver ActiveReceiver()
        => _activeId == "ariana" ? _arianaReceiver : _aaronReceiver;

    void Apply(string id, bool persist)
    {
        GameObject incoming = id == "aaron" ? aaronRoot : id == "ariana" ? arianaRoot : null;
        if (incoming == null)
        {
            Debug.LogWarning($"[AvatarRouter] Unknown character id '{id}' — keeping '{_activeId ?? DefaultId}'.");
            return;
        }
        if (id == _activeId) return; // idempotent — Swift re-sends freely

        // Stop the outgoing character first so no stale lip targets survive
        // its next activation. Direct C# call — works even while inactive.
        if (_activeId != null)
            ActiveReceiver()?.ReceiveBridgeMessage("{\"type\":\"stop\"}");

        GameObject other = incoming == aaronRoot ? arianaRoot : aaronRoot;
        if (other != null) other.SetActive(false);
        incoming.SetActive(true);

        _activeId = id;
        if (persist)
        {
            PlayerPrefs.SetString(PrefsKey, id);
            PlayerPrefs.Save();
        }
        Debug.Log($"[AvatarRouter] Active character -> '{id}'.");
    }
}
