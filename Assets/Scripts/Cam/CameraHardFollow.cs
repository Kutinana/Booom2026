using System.Collections;
using System.Collections.Generic;
using QFramework;
using UnityEngine;

public partial class CameraHardFollow : MonoBehaviour
{
    public Transform target;
    private Vector3 offset;
    // Start is called before the first frame update
    void Start()
    {
        offset = transform.position - target.position;
        var prc = GetComponent<PortalRenderController>();
        inner = prc.settings.transform;
        outer = prc.settings.outerTransform;
        TypeEventSystem.Global.Register<OnInnerToOuterEvent>(e => { ZoomIn(); }).UnRegisterWhenGameObjectDestroyed(this);
        TypeEventSystem.Global.Register<OnOuterToInnerEvent>(e => { ZoomOut(); }).UnRegisterWhenGameObjectDestroyed(this);
    }

    // Update is called once per frame
    void Update()
    {
        if (!isInLoopEffect)
            transform.position = target.position + offset;
    }
}
public class OnInnerToOuterEvent { }
public class OnOuterToInnerEvent { }