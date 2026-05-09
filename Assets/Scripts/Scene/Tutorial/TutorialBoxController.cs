using QFramework;
using UnityEngine;

public class TutorialBoxController : MonoBehaviour
{
    [SerializeField] float stuckThresholdX;

    bool _sent;
    void Update()
    {
        if (_sent)
            return;

        if (transform.position.x > stuckThresholdX)
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
