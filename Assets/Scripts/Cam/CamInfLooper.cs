using System.Collections;
using UnityEngine;

public partial class CameraHardFollow : MonoBehaviour
{
    Transform inner;
    Transform outer;
    bool isInLoopEffect = false;
    Coroutine currentEffect = null;
    [Header("ZoomEffect")]
    private float duration = 0.8f; // Must be larger than 0.2f
    private (Vector3 pos, Quaternion rot) GetCameraPosInner()
    {
        var cam = GetComponent<Camera>();
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 100f;
        return PortalCalcCamTransform.CalculateNextLayerCameraTransform(outer, inner, cam);
    }
    [ContextMenu("TEST-Z-IN")]
    public void ZoomIn()
    {
        // if (currentEffect != null) { return; }
        // currentEffect = StartCoroutine(IEZoomIn());
    }
    [ContextMenu("TEST-Z-OUT")]
    public void ZoomOut()
    {
        // if (currentEffect != null) { return; }
        // currentEffect = StartCoroutine(IEZoomOut());
    }
    IEnumerator IEZoomIn()
    {
        transform.GetPositionAndRotation(out Vector3 oriPos, out Quaternion oriRot);
        var (TargetPos, TargetRot) = GetCameraPosInner();
        isInLoopEffect = true;
        yield return null;
        float speed = 1f / duration;
        float progress = 0;
        while (progress <= 1)
        {
            progress += speed * Time.deltaTime;
            float t = Mathf.Clamp01(progress);
            transform.position = Vector3.Lerp(oriPos, TargetPos, Mathf.Pow(t, 0.25f));
            yield return null;
        }
        transform.position = oriPos;
        yield return null;
        isInLoopEffect = false;
        currentEffect = null;
    }
    IEnumerator IEZoomOut()
    {
        transform.GetPositionAndRotation(out Vector3 oriPos, out Quaternion oriRot);
        var (TargetPos, TargetRot) = GetCameraPosInner();
        isInLoopEffect = true;
        transform.position = TargetPos;
        float speed = 1f / duration;
        float progress = 0;
        while (progress <= 1)
        {
            progress += speed * Time.deltaTime;
            float t = Mathf.Clamp01(progress);
            transform.position = Vector3.Lerp(TargetPos, oriPos, Mathf.Pow(t, 4));
            yield return null;
        }
        transform.position = oriPos;
        yield return null;
        isInLoopEffect = false;
        currentEffect = null;
    }

}
