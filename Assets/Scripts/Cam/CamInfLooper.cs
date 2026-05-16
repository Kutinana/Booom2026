using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public partial class CameraHardFollow : MonoBehaviour
{
    Transform inner;
    Transform outer;
    [ContextMenu("Test")]
    void Test()
    {
        var cam = GetComponent<Camera>();
        cam.nearClipPlane = 0.01f;
        var (pos, rot) = PortalCalcCamTransform.CalculateNextLayerCameraTransform(outer, inner, cam);
        transform.SetLocalPositionAndRotation(pos, rot);
    }
}
