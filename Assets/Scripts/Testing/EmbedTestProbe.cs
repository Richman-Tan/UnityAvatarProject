using UnityEngine;

/// <summary>
/// Minimal bridge probe for the Android embed test scene (AndroidEmbedTest.unity).
/// Lives on a cube GameObject NAMED "AvatarRouter" so that
/// UnityPlayer.UnitySendMessage("AvatarRouter", "ReceiveBridgeMessage", json)
/// from the RN native module resolves without the full character rig.
/// The spin makes it obvious in-app that Unity's render loop is alive.
/// </summary>
public class EmbedTestProbe : MonoBehaviour
{
    void Update()
    {
        transform.Rotate(0f, 60f * Time.deltaTime, 20f * Time.deltaTime);
    }

    public void ReceiveBridgeMessage(string json)
    {
        Debug.Log($"[EmbedTestProbe] ReceiveBridgeMessage: {json}");
    }
}
