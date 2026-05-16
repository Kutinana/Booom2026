using UnityEngine;

/// <summary>
/// 在 Portal（世界盒）视觉效果下，屏幕像素对应嵌套虚拟相机画面。
/// 外层：主相机 <see cref="Camera.ScreenToWorldPoint"/> 点在玩家 Z 平面；
/// 门户内：与 <see cref="PortalPass"/> / <see cref="Portal2DPass"/> 相同地迭代 <c>portalStep * cameraWorld</c>，
/// 按各层 depth 做 VP 反投影后在玩家 Z 平面上 <see cref="Collider2D.OverlapPoint"/>。
/// </summary>
[DisallowMultipleComponent]
public class PlayerPortalScreenTap : MonoBehaviour
{
    private static readonly Matrix4x4 ViewZFlip = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));

    [SerializeField] PlayerController targetPlayer;
    [SerializeField] Camera viewCamera;
    [SerializeField] Transform portalInner;
    [SerializeField] Transform portalOuter;
    [Tooltip("未找到 PortalRenderController 时，门户嵌套检测的最大层数回退值（应与相机 Portal maxDepth 一致）")]
    [SerializeField, Min(1)] int nestingDepth = 1;
    [SerializeField] float maxRayDistance = 80f;
    [SerializeField] LayerMask raycastLayers = ~0;
    [SerializeField] bool requirePortalPlaneHit = true;
    [SerializeField] bool invertPortalPlaneNormal;
    [SerializeField] Vector2 portalHalfExtentsLocal = new Vector2(50f, 50f);

    Collider2D playerCollider2D;
    Collider playerCollider3D;

    /// <summary>为 true 时由本组件独占点击，<see cref="PlayerController"/> 的 IPointerClickHandler 不再触发。</summary>
    public bool HandlesScreenTapForConfiguredPortal =>
        isActiveAndEnabled && viewCamera != null && portalInner != null && portalOuter != null;

    void Reset()
    {
        targetPlayer = GetComponent<PlayerController>();
        viewCamera = Camera.main;
    }

    void Awake()
    {
        CachePlayerColliders();
    }

    void CachePlayerColliders()
    {
        if (targetPlayer == null)
        {
            playerCollider2D = null;
            playerCollider3D = null;
            return;
        }

        playerCollider2D = targetPlayer.GetComponent<Collider2D>();
        playerCollider3D = targetPlayer.GetComponent<Collider>();
    }

    void Update()
    {
        if (!Input.GetMouseButtonDown(0) || targetPlayer == null)
            return;

        if (viewCamera == null || portalInner == null || portalOuter == null)
            return;

        if (playerCollider2D == null && playerCollider3D == null)
            CachePlayerColliders();

        Vector2 screenPos = Input.mousePosition;

        if (TryPickOuterPlayer(screenPos))
            targetPlayer.TriggerTapAnimation();
        else if (TryPickNestedPortalPlayer(screenPos))
            targetPlayer.TriggerTapAnimation();
    }

    /// <summary>主相机直投（盒子外 / 未被门户遮住的玩家）。</summary>
    bool TryPickOuterPlayer(Vector2 screenPos)
    {
        if (playerCollider2D != null)
        {
            float depth = GetScreenToWorldDepthForPlayer();
            Vector3 world = viewCamera.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, depth));
            return playerCollider2D.OverlapPoint(world);
        }

        Ray ray = viewCamera.ScreenPointToRay(screenPos);
        return playerCollider3D != null &&
               Physics.Raycast(ray, out RaycastHit hit3d, maxRayDistance, raycastLayers, QueryTriggerInteraction.Ignore) &&
               hit3d.collider.GetComponentInParent<PlayerController>() == targetPlayer;
    }

    /// <summary>点击落在门户窗口内时，按渲染层 depth 1..maxDepth 逐层反投影拾取。</summary>
    bool TryPickNestedPortalPlayer(Vector2 screenPos)
    {
        if (requirePortalPlaneHit && !TryPassPortalScreenWindow(screenPos))
            return false;

        int maxDepth = ResolvePortalMaxDepth();
        for (int depth = maxDepth; depth >= 1; depth--)
        {
            if (TryPickPlayerAtNestedDepth(screenPos, depth))
                return true;
        }

        return false;
    }

    bool TryPickPlayerAtNestedDepth(Vector2 screenPos, int depth)
    {
        if (!TryBuildNestedViewMatrices(depth, out Matrix4x4 view, out Matrix4x4 proj))
            return false;

        if (playerCollider2D != null)
        {
            if (!TryUnprojectScreenToWorldRay(viewCamera, screenPos, view, proj, out Ray ray))
                return false;

            if (!TryIntersectPlayerZPlane(ray, out Vector2 worldXY))
                return false;

            return playerCollider2D.OverlapPoint(worldXY);
        }

        if (!TryUnprojectScreenToWorldRay(viewCamera, screenPos, view, proj, out Ray pickRay))
            return false;

        return playerCollider3D != null &&
               Physics.Raycast(pickRay, out RaycastHit hit3d, maxRayDistance, raycastLayers, QueryTriggerInteraction.Ignore) &&
               hit3d.collider.GetComponentInParent<PlayerController>() == targetPlayer;
    }

    int ResolvePortalMaxDepth()
    {
        if (viewCamera != null &&
            viewCamera.TryGetComponent(out PortalRenderController portalController) &&
            portalController.settings != null)
        {
            int configured = portalController.settings.maxDepth;
            return configured < 1 ? 1 : configured;
        }

        return Mathf.Max(1, nestingDepth);
    }

    float GetScreenToWorldDepthForPlayer()
    {
        return Mathf.Abs(viewCamera.transform.position.z - targetPlayer.transform.position.z);
    }

    bool TryIntersectPlayerZPlane(Ray ray, out Vector2 worldXY)
    {
        worldXY = default;
        float planeZ = targetPlayer.transform.position.z;
        float dz = ray.direction.z;
        if (Mathf.Abs(dz) < 1e-6f)
            return false;

        float t = (planeZ - ray.origin.z) / dz;
        if (t < 0f)
            return false;

        Vector3 hit = ray.origin + ray.direction * t;
        worldXY = hit;
        return true;
    }

    bool TryBuildNestedViewMatrices(int depth, out Matrix4x4 view, out Matrix4x4 proj)
    {
        view = default;
        proj = default;

        if (depth < 1)
            return false;

        Matrix4x4 portalStep = portalOuter.localToWorldMatrix * portalInner.worldToLocalMatrix;
        Matrix4x4 cameraWorld = viewCamera.transform.localToWorldMatrix;
        for (int i = 0; i < depth; i++)
            cameraWorld = portalStep * cameraWorld;

        view = ViewZFlip * cameraWorld.inverse;
        proj = viewCamera.projectionMatrix;
        return true;
    }

    bool TryPassPortalScreenWindow(Vector2 screenPos)
    {
        Ray outerRay = viewCamera.ScreenPointToRay(screenPos);
        Vector3 n = invertPortalPlaneNormal ? -portalInner.forward : portalInner.forward;
        var plane = new Plane(n, portalInner.position);
        if (!plane.Raycast(outerRay, out float enter))
            return false;

        Vector3 hitPt = outerRay.GetPoint(enter);
        Vector3 local = portalInner.InverseTransformPoint(hitPt);
        return Mathf.Abs(local.x) <= portalHalfExtentsLocal.x && Mathf.Abs(local.y) <= portalHalfExtentsLocal.y;
    }

    static bool TryUnprojectScreenToWorldRay(Camera cam, Vector2 screenPos, Matrix4x4 view, Matrix4x4 proj, out Ray ray)
    {
        ray = default;
        if (cam == null)
            return false;

        Matrix4x4 invVp = (proj * view).inverse;
        float x = screenPos.x / Mathf.Max(1f, cam.pixelWidth) * 2f - 1f;
        float y = screenPos.y / Mathf.Max(1f, cam.pixelHeight) * 2f - 1f;

        Vector4 worldNear = invVp * new Vector4(x, y, -1f, 1f);
        Vector4 worldFar = invVp * new Vector4(x, y, 1f, 1f);
        if (Mathf.Abs(worldNear.w) < 1e-8f || Mathf.Abs(worldFar.w) < 1e-8f)
            return false;

        worldNear /= worldNear.w;
        worldFar /= worldFar.w;

        Vector3 direction = worldFar - worldNear;
        if (direction.sqrMagnitude < 1e-12f)
            return false;

        ray = new Ray(worldNear, direction.normalized);
        return true;
    }
}
