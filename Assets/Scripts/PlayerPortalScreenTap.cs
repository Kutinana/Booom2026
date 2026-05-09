using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 在 Portal（世界盒）视觉效果下，屏幕上的像素对应的是<strong>变换后的虚拟相机</strong>看到的画面，
/// 普通射线 / PhysicsRaycaster 打在真实世界上无法点到嵌套画面里的角色。
/// 本组件复现 <see cref="PortalPass"/> / <see cref="Portal2DPass"/> 中的
/// <c>portalStep</c> 与 <c>cameraWorld</c> 迭代，把主相机的屏幕坐标转成嵌套世界射线再检测 Player。
/// <para>
/// 请在 Inspector 里填入与 PortalRenderController（Portal Feature Settings）相同的
/// <c>transform</c>（内门户）与 <c>outerTransform</c>，以及挂载了 Portal 的那台 <see cref="Camera"/>。
/// </para>
/// </summary>
[DisallowMultipleComponent]
public class PlayerPortalScreenTap : MonoBehaviour
{
    [SerializeField] PlayerController targetPlayer;
    [SerializeField] Camera viewCamera;
    [SerializeField] Transform portalInner;
    [SerializeField] Transform portalOuter;
    [Tooltip("与 PortalPass 中递归一致：第 1 层嵌套为 1，再往内递增")]
    [SerializeField, Min(1)] int nestingDepth = 1;
    [SerializeField] float maxRayDistance = 80f;
    [SerializeField] LayerMask raycastLayers = ~0;
    [SerializeField] bool requirePortalPlaneHit = true;
    [Tooltip("若平面检测总是失败或误通过，尝试反转法线")]
    [SerializeField] bool invertPortalPlaneNormal;
    [Tooltip("在门户局部 XY 上的半尺寸；取得很大可近似关闭窗口裁剪")]
    [SerializeField] Vector2 portalHalfExtentsLocal = new Vector2(50f, 50f);

    void Reset()
    {
        targetPlayer = GetComponent<PlayerController>();
        viewCamera = Camera.main;
    }

    void Update()
    {
        if (!Input.GetMouseButtonDown(0) || targetPlayer == null)
            return;

        if (viewCamera == null || portalInner == null || portalOuter == null)
            return;

        Vector2 screenPos = Input.mousePosition;

        if (!TryComputeNestedWorldRay(screenPos, out Ray innerRay))
            return;

        if (TryHitPlayer(innerRay))
            targetPlayer.TriggerTapAnimation();
    }

    bool TryHitPlayer(Ray innerRay)
    {
        Collider2D c2d = targetPlayer.GetComponent<Collider2D>();
        if (c2d != null)
        {
            Vector2 origin = innerRay.origin;
            Vector2 dir = innerRay.direction;
            if (dir.sqrMagnitude < 1e-8f)
                return false;
            dir.Normalize();

            RaycastHit2D[] hits = Physics2D.RaycastAll(origin, dir, maxRayDistance, raycastLayers);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (RaycastHit2D h in hits)
            {
                if (h.collider != null && h.collider.GetComponentInParent<PlayerController>() == targetPlayer)
                    return true;
            }

            return false;
        }

        Collider c3d = targetPlayer.GetComponent<Collider>();
        if (c3d != null &&
            Physics.Raycast(innerRay, out RaycastHit hit3d, maxRayDistance, raycastLayers, QueryTriggerInteraction.Ignore) &&
            hit3d.collider.GetComponentInParent<PlayerController>() == targetPlayer)
            return true;

        return false;
    }

    /// <summary>
    /// 与 PortalPass 一致：<c>portalStep = outer.worldToLocal の逆 * inner の逆</c> 即代码中的乘法顺序。
    /// </summary>
    bool TryComputeNestedWorldRay(Vector2 screenPos, out Ray innerRay)
    {
        innerRay = default;
        Ray outerRay = viewCamera.ScreenPointToRay(screenPos);

        if (requirePortalPlaneHit)
        {
            Vector3 n = invertPortalPlaneNormal ? -portalInner.forward : portalInner.forward;
            var plane = new Plane(n, portalInner.position);
            if (!plane.Raycast(outerRay, out float enter))
                return false;

            Vector3 hitPt = outerRay.GetPoint(enter);
            Vector3 local = portalInner.InverseTransformPoint(hitPt);
            if (Mathf.Abs(local.x) > portalHalfExtentsLocal.x || Mathf.Abs(local.y) > portalHalfExtentsLocal.y)
                return false;
        }

        Matrix4x4 portalStep = portalOuter.localToWorldMatrix * portalInner.worldToLocalMatrix;
        Matrix4x4 camWorld = viewCamera.transform.localToWorldMatrix;
        for (int i = 0; i < nestingDepth; i++)
            camWorld = portalStep * camWorld;

        Vector3 nearWorld = viewCamera.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, viewCamera.nearClipPlane));
        Vector3 dirWorldMain = (nearWorld - viewCamera.transform.position).normalized;
        Vector3 dirLocal = viewCamera.transform.InverseTransformDirection(dirWorldMain);

        Vector3 origin = camWorld.MultiplyPoint3x4(Vector3.zero);
        Vector3 dir = camWorld.MultiplyVector(dirLocal).normalized;
        innerRay = new Ray(origin, dir);
        return true;
    }
}
