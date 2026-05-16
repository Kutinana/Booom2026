using QFramework;
using UnityEngine;

public class triggerAnim : MonoBehaviour
{
    public GameObject playertoOff;
    public GameObject playertoOn;
    public FollowTransform followTransform;
    // Start is called before the first frame update
    void Awake()
    {
        TypeEventSystem.Global.Register<PlayW1TEvent>(e =>
        {
            playertoOff.SetActive(false);
            playertoOn.SetActive(true);
            followTransform.target = playertoOn.transform;
        }).UnRegisterWhenGameObjectDestroyed(this);
    }
}
public class PlayW1TEvent { }