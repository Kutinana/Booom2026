using QFramework;
using UnityEngine;

public class TutorialBoxController : MonoBehaviour
{
    [SerializeField] float stuckThresholdMaxX;
    [SerializeField] float stuckThresholdMinX;

    bool _sent;
    void Update()
    {
        if (_sent)
            return;

        if (transform.position.x > stuckThresholdMaxX || transform.position.x < stuckThresholdMinX)
        {
            _sent = true;
            TypeEventSystem.Global.Send(new OnTutorialBoxStuckedEvent(gameObject));
        }
    }
}

public readonly struct OnTutorialBoxStuckedEvent
{
    public readonly GameObject Box;

    public OnTutorialBoxStuckedEvent(GameObject box)
    {
        Box = box;
    }
}
