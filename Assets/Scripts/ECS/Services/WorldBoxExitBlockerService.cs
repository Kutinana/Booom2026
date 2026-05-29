using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

public class WorldBoxExitBlockerService : ServiceBase
{
    private const string PlatformTag = "Platform";
    private const string SceneBricksLayerName = "SceneBricks";
    private const string UntaggedTag = "Untagged";

    [SerializeField, Min(0f)] private float innerCheckPadding = 0.1f;
    [SerializeField, Min(0.01f)] private float fallbackCellSize = 0.5f;
    [SerializeField, Min(0.01f)] private float fallbackDepth = 1f;
    [SerializeField, Min(0.01f)] private float minimumColliderSize = 0.05f;
    [SerializeField, Min(0.02f)] private float keepAliveDuration = 0.15f;

    #region Debug Fields

    [Header("Debug")]
    [SerializeField] private bool logInnerTargetOverlapHits = false;
    [SerializeField] private bool drawInnerTargetOverlapGizmos = false;
    [SerializeField, Min(0f)] private float innerTargetOverlapGizmoDuration = 1f;

    private readonly Dictionary<WorldBox, DebugOverlapQuery2D> debugOverlapQueries2D = new Dictionary<WorldBox, DebugOverlapQuery2D>();
    private readonly List<WorldBox> expiredDebugOverlapOwners = new List<WorldBox>(16);

    #endregion

    private readonly Collider2D[] overlapHits2D = new Collider2D[32];
    private readonly Collider[] overlapHits3D = new Collider[32];
    private readonly Dictionary<BlockerKey, TemporaryWall> walls = new Dictionary<BlockerKey, TemporaryWall>();
    private readonly List<BlockerKey> expiredWallKeys = new List<BlockerKey>(16);

    public bool TryRefreshBlockerForStaticInnerHit(
        WorldBox worldBox,
        BoxPushDirection direction,
        Bounds outerBounds,
        Bounds innerTargetBounds,
        Bounds playerBounds,
        LayerMask blockingMask,
        bool use2D,
        bool use3D)
    {
        return TryRefreshBlockerForStaticInnerHit(
            worldBox,
            worldBox,
            direction,
            outerBounds,
            innerTargetBounds,
            playerBounds,
            blockingMask,
            use2D,
            use3D);
    }

    public bool TryRefreshBlockerForStaticInnerHit(
        WorldBox worldBox,
        UnityEngine.Object owner,
        BoxPushDirection direction,
        Bounds outerBounds,
        Bounds innerTargetBounds,
        Bounds actorBounds,
        LayerMask blockingMask,
        bool use2D,
        bool use3D)
    {
        if (worldBox == null || outerBounds.size == Vector3.zero || innerTargetBounds.size == Vector3.zero)
        {
            return false;
        }

        if (!use2D && !use3D)
        {
            use2D = true;
        }

        if (!TryGetStaticBlockingColliderTag(innerTargetBounds, worldBox, direction, blockingMask, use2D, use3D, out string blockingTag))
        {
            return false;
        }

        if (IsPlatformTag(blockingTag) && ShouldSkipOuterPlatformBlocker(direction, outerBounds, actorBounds))
        {
            return false;
        }

        RefreshWall(owner != null ? owner : worldBox, worldBox, direction, outerBounds, actorBounds, use2D, use3D, blockingTag);
        return true;
    }

    /// <summary>
    /// 只读判定内侧落点是否有静态阻挡（不创建临时墙），用于推箱子过程中检测出入口开闭状态。
    /// </summary>
    public bool QueryInnerExitStaticallyBlocked(
        WorldBox worldBox,
        BoxPushDirection direction,
        Bounds outerBounds,
        Bounds innerTargetBounds,
        Bounds playerBounds,
        LayerMask blockingMask,
        bool use2D,
        bool use3D)
    {
        if (worldBox == null || outerBounds.size == Vector3.zero || innerTargetBounds.size == Vector3.zero)
        {
            return false;
        }

        if (!use2D && !use3D)
        {
            use2D = true;
        }

        if (!TryGetStaticBlockingColliderTag(innerTargetBounds, worldBox, direction, blockingMask, use2D, use3D, out string blockingTag))
        {
            return false;
        }

        if (IsPlatformTag(blockingTag) && ShouldSkipOuterPlatformBlocker(direction, outerBounds, playerBounds))
        {
            return false;
        }

        return true;
    }

    public bool TryGetTeleportTargetStandardBoxBlocker(
        Bounds targetBounds,
        WorldBox worldBox,
        StandardBox ignoredBox,
        LayerMask blockingMask,
        bool use2D,
        bool use3D,
        out StandardBox blocker)
    {
        return TryGetTeleportTargetStandardBoxBlockerInternal(targetBounds, worldBox, ignoredBox, blockingMask, use2D, use3D, false, out blocker);
    }

    private bool TryGetTeleportTargetStandardBoxBlockerInternal(
        Bounds targetBounds,
        WorldBox worldBox,
        StandardBox ignoredBox,
        LayerMask blockingMask,
        bool use2D,
        bool use3D,
        bool checkingInner,
        out StandardBox blocker)
    {
        blocker = null;
        if (targetBounds.size == Vector3.zero)
        {
            return false;
        }

        if (!use2D && !use3D)
        {
            use2D = true;
        }

        Bounds queryBounds = targetBounds;
        queryBounds.Expand(-innerCheckPadding * 2f);

        Physics.SyncTransforms();
        Physics2D.SyncTransforms();

        if (use2D && TryGetTeleportTargetStandardBoxBlocker2D(queryBounds, worldBox, ignoredBox, blockingMask, checkingInner, out blocker))
        {
            return true;
        }

        if (use3D && TryGetTeleportTargetStandardBoxBlocker3D(queryBounds, worldBox, ignoredBox, blockingMask, checkingInner, out blocker))
        {
            return true;
        }

        return false;
    }

    public bool TryGetTeleportTargetStandardBoxBlocker(
        Bounds targetBounds,
        WorldBox worldBox,
        StandardBox ignoredBox,
        LayerMask blockingMask,
        bool use2D,
        bool use3D,
        bool checkingInner,
        out StandardBox blocker)
    {
        return TryGetTeleportTargetStandardBoxBlockerInternal(targetBounds, worldBox, ignoredBox, blockingMask, use2D, use3D, checkingInner, out blocker);
    }

    public void Clear(WorldBox worldBox)
    {
        if (worldBox == null)
        {
            return;
        }

        expiredWallKeys.Clear();
        foreach (KeyValuePair<BlockerKey, TemporaryWall> pair in walls)
        {
            if (pair.Key.MatchesWorldBox(worldBox))
            {
                expiredWallKeys.Add(pair.Key);
            }
        }

        for (int i = 0; i < expiredWallKeys.Count; i++)
        {
            RemoveWall(expiredWallKeys[i]);
        }

        expiredWallKeys.Clear();
    }

    public void Clear(WorldBox worldBox, UnityEngine.Object owner)
    {
        if (worldBox == null || owner == null)
        {
            return;
        }

        RemoveWall(new BlockerKey(worldBox, owner));
    }

    protected override void OnDestroy()
    {
        foreach (KeyValuePair<BlockerKey, TemporaryWall> pair in walls)
        {
            DestroyWallObject(pair.Value);
        }

        walls.Clear();
        expiredWallKeys.Clear();
        debugOverlapQueries2D.Clear();
        expiredDebugOverlapOwners.Clear();
        base.OnDestroy();
    }

    private void LateUpdate()
    {
        expiredWallKeys.Clear();
        foreach (KeyValuePair<BlockerKey, TemporaryWall> pair in walls)
        {
            TemporaryWall wall = pair.Value;
            if (pair.Key.WorldBox == null ||
                pair.Key.Owner == null ||
                wall == null ||
                wall.GameObject == null ||
                Time.time > wall.ExpireAt)
            {
                expiredWallKeys.Add(pair.Key);
            }
        }

        for (int i = 0; i < expiredWallKeys.Count; i++)
        {
            RemoveWall(expiredWallKeys[i]);
        }

        PruneDebugOverlapQueries();
    }

    private bool TryGetStaticBlockingColliderTag(
        Bounds targetBounds,
        WorldBox worldBox,
        BoxPushDirection direction,
        LayerMask blockingMask,
        bool check2D,
        bool check3D,
        out string blockingTag)
    {
        blockingTag = null;
        Bounds queryBounds = targetBounds;
        queryBounds.Expand(-innerCheckPadding * 2f);

        Physics.SyncTransforms();
        Physics2D.SyncTransforms();

        if (check2D && TryGetStaticBlockingColliderTag2D(queryBounds, worldBox, direction, blockingMask, out blockingTag))
        {
            return true;
        }

        if (check3D && TryGetStaticBlockingColliderTag3D(queryBounds, worldBox, blockingMask, out blockingTag))
        {
            return true;
        }

        return false;
    }

    private bool TryGetStaticBlockingColliderTag2D(
        Bounds queryBounds,
        WorldBox worldBox,
        BoxPushDirection direction,
        LayerMask blockingMask,
        out string blockingTag)
    {
        blockingTag = null;
        Vector2 size = new Vector2(
            Mathf.Max(minimumColliderSize, queryBounds.size.x),
            Mathf.Max(minimumColliderSize, queryBounds.size.y));

        Bounds actualQueryBounds = Create2DOverlapQueryBounds(queryBounds, size);
        int hitCount = Physics2D.OverlapBoxNonAlloc((Vector2)actualQueryBounds.center, size, 0f, overlapHits2D, blockingMask);
        RecordDebugOverlapQuery2D(worldBox, direction, actualQueryBounds, blockingMask, hitCount);
        LogTargetScan("2D", worldBox, actualQueryBounds, blockingMask, hitCount);
        for (int i = 0; i < hitCount; i++)
        {
            Collider2D hit = overlapHits2D[i];
            bool accepted = IsStaticBlockingCollider(
    hit,
    worldBox,
    direction,
    queryBounds,
    out string rejectReason);
            LogTargetHit("2D", worldBox, hit, accepted, rejectReason);
            if (accepted)
            {
                blockingTag = GetBlockingTag(hit.transform);
                return true;
            }
        }

        return false;
    }

    private bool TryGetStaticBlockingColliderTag3D(
        Bounds queryBounds,
        WorldBox worldBox,
        LayerMask blockingMask,
        out string blockingTag)
    {
        blockingTag = null;
        Vector3 halfExtents = queryBounds.extents;
        halfExtents.x = Mathf.Max(minimumColliderSize * 0.5f, halfExtents.x);
        halfExtents.y = Mathf.Max(minimumColliderSize * 0.5f, halfExtents.y);
        halfExtents.z = Mathf.Max(fallbackDepth * 0.5f, halfExtents.z);

        int hitCount = Physics.OverlapBoxNonAlloc(
            queryBounds.center,
            halfExtents,
            overlapHits3D,
            Quaternion.identity,
            blockingMask,
            QueryTriggerInteraction.Ignore);

        LogTargetScan("3D", worldBox, queryBounds, blockingMask, hitCount);
        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = overlapHits3D[i];
            bool accepted = IsStaticBlockingCollider(hit, worldBox, out string rejectReason);
            LogTargetHit("3D", worldBox, hit, accepted, rejectReason);
            if (accepted)
            {
                blockingTag = GetBlockingTag(hit.transform);
                return true;
            }
        }

        return false;
    }

    private bool TryGetTeleportTargetStandardBoxBlocker2D(
        Bounds queryBounds,
        WorldBox worldBox,
        StandardBox ignoredBox,
        LayerMask blockingMask,
        bool checkingInner,
        out StandardBox blocker)
    {
        blocker = null;
        Vector2 size = new Vector2(
            Mathf.Max(minimumColliderSize, queryBounds.size.x),
            Mathf.Max(minimumColliderSize, queryBounds.size.y));

        Bounds actualQueryBounds = Create2DOverlapQueryBounds(queryBounds, size);
        int hitCount = Physics2D.OverlapBoxNonAlloc((Vector2)actualQueryBounds.center, size, 0f, overlapHits2D, blockingMask);
        for (int i = 0; i < hitCount; i++)
        {
            Collider2D hit = overlapHits2D[i];
            if (hit == null || !hit.enabled || hit.isTrigger)
            {
                continue;
            }

            StandardBox candidate = hit.GetComponentInParent<StandardBox>();
            if (!IsTeleportTargetStandardBoxBlocker(candidate, worldBox, ignoredBox, checkingInner))
            {
                continue;
            }

            blocker = candidate;
            return true;
        }

        return false;
    }

    private bool TryGetTeleportTargetStandardBoxBlocker3D(
        Bounds queryBounds,
        WorldBox worldBox,
        StandardBox ignoredBox,
        LayerMask blockingMask,
        bool checkingInner,
        out StandardBox blocker)
    {
        blocker = null;
        Vector3 halfExtents = queryBounds.extents;
        halfExtents.x = Mathf.Max(minimumColliderSize * 0.5f, halfExtents.x);
        halfExtents.y = Mathf.Max(minimumColliderSize * 0.5f, halfExtents.y);
        halfExtents.z = Mathf.Max(fallbackDepth * 0.5f, halfExtents.z);

        int hitCount = Physics.OverlapBoxNonAlloc(
            queryBounds.center,
            halfExtents,
            overlapHits3D,
            Quaternion.identity,
            blockingMask,
            QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = overlapHits3D[i];
            if (hit == null || !hit.enabled)
            {
                continue;
            }

            StandardBox candidate = hit.GetComponentInParent<StandardBox>();
            if (!IsTeleportTargetStandardBoxBlocker(candidate, worldBox, ignoredBox, checkingInner))
            {
                continue;
            }

            blocker = candidate;
            return true;
        }

        return false;
    }

    private static bool IsTeleportTargetStandardBoxBlocker(StandardBox candidate, WorldBox worldBox, StandardBox ignoredBox, bool checkingInner)
    {
        if (candidate == null || candidate == ignoredBox || candidate is WorldBox)
        {
            return false;
        }

        if (!candidate.IsSceneMovableActive)
        {
            return false;
        }

        bool owned = StandardBox.IsOwnedByWorldBox(candidate.transform, worldBox);
        if (checkingInner)
        {
            if (!owned) return false;
        }
        else
        {
            if (owned) return false;
        }

        return candidate.Bounds.size != Vector3.zero;
    }


    private bool IsStaticBlockingCollider(
    Collider2D hit,
    WorldBox worldBox,
    BoxPushDirection direction,
    Bounds? queryBounds,
    out string rejectReason)
    {
        rejectReason = GetStaticBlockingRejectReason(hit, worldBox, queryBounds);

        if (rejectReason == "scene movable item")
        {
            StandardBox standardBox = hit.GetComponentInParent<StandardBox>();

            // 允许普通箱子作为“向下离开世界”时的阻挡
            if (direction == BoxPushDirection.Down &&
                standardBox != null &&
                !(standardBox is WorldBox))
            {
                rejectReason = null;
            }
        }

        return rejectReason == null;
    }

    private bool IsStaticBlockingCollider(Collider2D hit, WorldBox worldBox, Bounds? queryBounds, out string rejectReason)
    {
        rejectReason = GetStaticBlockingRejectReason(hit, worldBox, queryBounds);
        return rejectReason == null;
    }



    private bool IsStaticBlockingCollider(Collider hit, WorldBox worldBox, out string rejectReason)
    {
        rejectReason = GetStaticBlockingRejectReason(hit, worldBox);
        return rejectReason == null;
    }

    private Bounds CalculateOuterWallBounds(WorldBox worldBox, BoxPushDirection direction, Bounds outerBounds, Bounds playerBounds)
    {
        Vector3 cellSize = GetCellSize(worldBox != null ? worldBox.Grid : null);
        Vector3 halfCell = cellSize * 0.5f;
        Vector3 point = playerBounds.center;

        switch (direction)
        {
            case BoxPushDirection.Left:
                point.x = outerBounds.min.x - halfCell.x;
                point.y = Mathf.Clamp(playerBounds.center.y, outerBounds.min.y, outerBounds.max.y);
                break;
            case BoxPushDirection.Right:
                point.x = outerBounds.max.x + halfCell.x;
                point.y = Mathf.Clamp(playerBounds.center.y, outerBounds.min.y, outerBounds.max.y);
                break;
            case BoxPushDirection.Down:
                point.x = Mathf.Clamp(playerBounds.center.x, outerBounds.min.x, outerBounds.max.x);
                point.y = outerBounds.min.y - halfCell.y;
                break;
            case BoxPushDirection.Up:
                point.x = Mathf.Clamp(playerBounds.center.x, outerBounds.min.x, outerBounds.max.x);
                point.y = outerBounds.max.y + halfCell.y;
                break;
        }

        Vector3 center = SnapToGrid(worldBox != null ? worldBox.Grid : null, point, worldBox != null ? worldBox.CellOffset : new Vector3(0.5f, 0.5f, 0f));
        center.z = playerBounds.center.z;

        Vector3 size = new Vector3(
            Mathf.Max(minimumColliderSize, cellSize.x),
            Mathf.Max(minimumColliderSize, cellSize.y),
            Mathf.Max(fallbackDepth, playerBounds.size.z));

        return new Bounds(center, size);
    }

    private void RefreshWall(
        UnityEngine.Object owner,
        WorldBox worldBox,
        BoxPushDirection direction,
        Bounds outerBounds,
        Bounds actorBounds,
        bool use2D,
        bool use3D,
        string blockingTag)
    {
        Bounds wallBounds = CalculateOuterWallBounds(worldBox, direction, outerBounds, actorBounds);
        TemporaryWall wall = GetOrCreateWall(worldBox, owner, out bool created);
        GameObject wallObject = wall.GameObject;
        bool isPlatformBlocker = IsPlatformTag(blockingTag);
        int fallbackLayer = worldBox.gameObject.layer;
        wallObject.layer = isPlatformBlocker ? fallbackLayer : GetSceneBricksLayer(fallbackLayer);
        wallObject.tag = isPlatformBlocker ? PlatformTag : UntaggedTag;
        wallObject.transform.position = wallBounds.center;
        wallObject.transform.rotation = Quaternion.identity;
        wallObject.transform.localScale = Vector3.one;

        if (use2D)
        {
            if (wall.Collider2D == null)
            {
                wall.Collider2D = wallObject.AddComponent<BoxCollider2D>();
            }

            wall.Collider2D.enabled = true;
            wall.Collider2D.isTrigger = false;
            wall.Collider2D.offset = Vector2.zero;
            wall.Collider2D.size = ToLocalSize2D(wallObject.transform, wallBounds.size);
        }
        else if (wall.Collider2D != null)
        {
            wall.Collider2D.enabled = false;
        }

        if (use3D)
        {
            if (wall.Collider3D == null)
            {
                wall.Collider3D = wallObject.AddComponent<BoxCollider>();
            }

            wall.Collider3D.enabled = true;
            wall.Collider3D.isTrigger = false;
            wall.Collider3D.center = Vector3.zero;
            wall.Collider3D.size = ToLocalSize3D(wallObject.transform, wallBounds.size);
        }
        else if (wall.Collider3D != null)
        {
            wall.Collider3D.enabled = false;
        }

        wall.Direction = direction;
        wall.ExpireAt = Time.time + keepAliveDuration;

        if (created)
        {
            if (isPlatformBlocker)
            {
                StartCoroutine(ActivateNextFrame(wallObject));
            }
            else
            {
                wallObject.SetActive(true);
            }
        }
    }

    private TemporaryWall GetOrCreateWall(WorldBox worldBox, UnityEngine.Object owner, out bool created)
    {
        created = false;
        BlockerKey key = new BlockerKey(worldBox, owner);
        if (walls.TryGetValue(key, out TemporaryWall wall) && wall != null && wall.GameObject != null)
        {
            return wall;
        }

        GameObject wallObject = new GameObject($"WorldBoxExitBlocker.{worldBox.name}.{GetOwnerName(owner)}");
        wallObject.SetActive(false);
        Scene scene = worldBox.gameObject.scene;
        if (scene.IsValid())
        {
            SceneManager.MoveGameObjectToScene(wallObject, scene);
        }

        wall = new TemporaryWall(wallObject);
        walls[key] = wall;
        created = true;
        return wall;
    }

    private IEnumerator ActivateNextFrame(GameObject wallObject)
    {
        yield return null;

        if (wallObject != null)
        {
            wallObject.SetActive(true);
        }
    }

    #region Debug

    private void LogTargetScan(string physicsType, WorldBox worldBox, Bounds queryBounds, LayerMask blockingMask, int hitCount)
    {
        if (!logInnerTargetOverlapHits)
        {
            return;
        }

#if UNITY_EDITOR
        Debug.Log(
            $"[WorldBoxExitBlocker] Target scan {physicsType}: worldBox={GetObjectPath(worldBox != null ? worldBox.transform : null)}, hitCount={hitCount}, center={queryBounds.center}, size={queryBounds.size}, mask={blockingMask.value}",
            this);
#endif
    }

    private void LogTargetHit(string physicsType, WorldBox worldBox, Collider2D hit, bool accepted, string rejectReason)
    {
        if (!logInnerTargetOverlapHits)
        {
            return;
        }

        if (hit == null)
        {
#if UNITY_EDITOR
            Debug.Log($"[WorldBoxExitBlocker] Target hit {physicsType}: null, status=ignore: {rejectReason}", this);
#endif
            return;
        }

#if UNITY_EDITOR
        Debug.Log(
            $"[WorldBoxExitBlocker] Target hit {physicsType}: status={FormatHitStatus(accepted, rejectReason)}, worldBox={GetObjectPath(worldBox != null ? worldBox.transform : null)}, collider={GetObjectPath(hit.transform)}, tag={hit.tag}, layer={GetLayerLabel(hit.gameObject.layer)}, enabled={hit.enabled}, isTrigger={hit.isTrigger}, boundsCenter={hit.bounds.center}, boundsSize={hit.bounds.size}",
            hit);
#endif
    }

    private void LogTargetHit(string physicsType, WorldBox worldBox, Collider hit, bool accepted, string rejectReason)
    {
        if (!logInnerTargetOverlapHits)
        {
            return;
        }

        if (hit == null)
        {
#if UNITY_EDITOR
            Debug.Log($"[WorldBoxExitBlocker] Target hit {physicsType}: null, status=ignore: {rejectReason}", this);
#endif
            return;
        }

#if UNITY_EDITOR
        Debug.Log(
            $"[WorldBoxExitBlocker] Target hit {physicsType}: status={FormatHitStatus(accepted, rejectReason)}, worldBox={GetObjectPath(worldBox != null ? worldBox.transform : null)}, collider={GetObjectPath(hit.transform)}, tag={hit.tag}, layer={GetLayerLabel(hit.gameObject.layer)}, enabled={hit.enabled}, isTrigger={hit.isTrigger}, boundsCenter={hit.bounds.center}, boundsSize={hit.bounds.size}",
            hit);
#endif
    }

    private void RecordDebugOverlapQuery2D(
        WorldBox worldBox,
        BoxPushDirection direction,
        Bounds actualQueryBounds,
        LayerMask blockingMask,
        int hitCount)
    {
        if (!drawInnerTargetOverlapGizmos || worldBox == null)
        {
            return;
        }

        debugOverlapQueries2D[worldBox] = new DebugOverlapQuery2D(
            actualQueryBounds,
            direction,
            blockingMask,
            hitCount,
            Time.time);
    }

    private void PruneDebugOverlapQueries()
    {
        if (debugOverlapQueries2D.Count == 0)
        {
            return;
        }

        expiredDebugOverlapOwners.Clear();
        float now = Time.time;
        foreach (KeyValuePair<WorldBox, DebugOverlapQuery2D> pair in debugOverlapQueries2D)
        {
            if (pair.Key == null ||
                (innerTargetOverlapGizmoDuration > 0f && now - pair.Value.CapturedAt > innerTargetOverlapGizmoDuration))
            {
                expiredDebugOverlapOwners.Add(pair.Key);
            }
        }

        for (int i = 0; i < expiredDebugOverlapOwners.Count; i++)
        {
            debugOverlapQueries2D.Remove(expiredDebugOverlapOwners[i]);
        }
    }

    private void OnDrawGizmos()
    {
        if (!drawInnerTargetOverlapGizmos || debugOverlapQueries2D.Count == 0)
        {
            return;
        }

        PruneDebugOverlapQueries();

        Color previousColor = Gizmos.color;
#if UNITY_EDITOR
        Color previousHandlesColor = UnityEditor.Handles.color;
#endif
        foreach (KeyValuePair<WorldBox, DebugOverlapQuery2D> pair in debugOverlapQueries2D)
        {
            DebugOverlapQuery2D query = pair.Value;
            Gizmos.color = query.HitCount > 0
                ? new Color(1f, 0.75f, 0.05f, 0.95f)
                : new Color(0f, 0.85f, 1f, 0.95f);

            DrawBoundsXY(query.ActualBounds);

#if UNITY_EDITOR
            UnityEditor.Handles.color = Gizmos.color;
            UnityEditor.Handles.Label(
                new Vector3(query.ActualBounds.min.x, query.ActualBounds.max.y, query.ActualBounds.center.z),
                FormatDebugOverlapQueryLabel(pair.Key, query));
#endif
        }

        Gizmos.color = previousColor;
#if UNITY_EDITOR
        UnityEditor.Handles.color = previousHandlesColor;
#endif
    }

    private static Bounds Create2DOverlapQueryBounds(Bounds queryBounds, Vector2 actualSize)
    {
        Vector3 center = queryBounds.center;
        Vector3 size = new Vector3(actualSize.x, actualSize.y, 0f);
        return new Bounds(center, size);
    }

    private static void DrawBoundsXY(Bounds bounds)
    {
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;
        float z = bounds.center.z;

        Vector3 bottomLeft = new Vector3(min.x, min.y, z);
        Vector3 bottomRight = new Vector3(max.x, min.y, z);
        Vector3 topRight = new Vector3(max.x, max.y, z);
        Vector3 topLeft = new Vector3(min.x, max.y, z);

        Gizmos.DrawLine(bottomLeft, bottomRight);
        Gizmos.DrawLine(bottomRight, topRight);
        Gizmos.DrawLine(topRight, topLeft);
        Gizmos.DrawLine(topLeft, bottomLeft);
    }

    private static string FormatDebugOverlapQueryLabel(WorldBox worldBox, DebugOverlapQuery2D query)
    {
        Vector3 size = query.ActualBounds.size;
        return $"OverlapBox2D {query.Direction} hits={query.HitCount} size=({size.x:F3}, {size.y:F3}) mask={query.BlockingMask.value} box={GetObjectPath(worldBox != null ? worldBox.transform : null)}";
    }

    private readonly struct DebugOverlapQuery2D
    {
        public readonly Bounds ActualBounds;
        public readonly BoxPushDirection Direction;
        public readonly LayerMask BlockingMask;
        public readonly int HitCount;
        public readonly float CapturedAt;

        public DebugOverlapQuery2D(
            Bounds actualBounds,
            BoxPushDirection direction,
            LayerMask blockingMask,
            int hitCount,
            float capturedAt)
        {
            ActualBounds = actualBounds;
            Direction = direction;
            BlockingMask = blockingMask;
            HitCount = hitCount;
            CapturedAt = capturedAt;
        }
    }

    #endregion

    private void RemoveWall(BlockerKey key)
    {
        if (!walls.TryGetValue(key, out TemporaryWall wall))
        {
            return;
        }

        DestroyWallObject(wall);
        walls.Remove(key);
    }

    private void DestroyWallObject(TemporaryWall wall)
    {
        if (wall != null && wall.GameObject != null)
        {
            Destroy(wall.GameObject);
        }
    }

    private Vector3 GetCellSize(Grid grid)
    {
        if (grid == null)
        {
            return new Vector3(fallbackCellSize, fallbackCellSize, fallbackDepth);
        }

        Vector3 cellSize = grid.cellSize;
        cellSize.x = Mathf.Max(minimumColliderSize, Mathf.Abs(cellSize.x));
        cellSize.y = Mathf.Max(minimumColliderSize, Mathf.Abs(cellSize.y));
        cellSize.z = Mathf.Max(fallbackDepth, Mathf.Abs(cellSize.z));
        return cellSize;
    }

    private static Vector3 SnapToGrid(Grid grid, Vector3 point, Vector3 cellOffset)
    {
        if (grid == null)
        {
            return point;
        }

        Vector3Int cell = grid.WorldToCell(point);
        return grid.CellToWorld(cell) + Vector3.Scale(grid.cellSize, cellOffset);
    }

    private static Vector2 ToLocalSize2D(Transform transform, Vector3 worldSize)
    {
        Vector3 scale = transform.lossyScale;
        return new Vector2(
            worldSize.x / Mathf.Max(Mathf.Abs(scale.x), 0.0001f),
            worldSize.y / Mathf.Max(Mathf.Abs(scale.y), 0.0001f));
    }

    private static Vector3 ToLocalSize3D(Transform transform, Vector3 worldSize)
    {
        Vector3 scale = transform.lossyScale;
        return new Vector3(
            worldSize.x / Mathf.Max(Mathf.Abs(scale.x), 0.0001f),
            worldSize.y / Mathf.Max(Mathf.Abs(scale.y), 0.0001f),
            worldSize.z / Mathf.Max(Mathf.Abs(scale.z), 0.0001f));
    }



    private static bool HasSceneMovableItem(Transform start)
    {
        Transform current = start;
        while (current != null)
        {
            MonoBehaviour[] behaviours = current.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is ISceneMovableItem)
                {
                    return true;
                }
            }

            current = current.parent;
        }

        return false;
    }

    private bool IsTemporaryWallCollider(Collider2D hit)
    {
        return hit != null && IsTemporaryWallTransform(hit.transform);
    }

    private bool IsTemporaryWallCollider(Collider hit)
    {
        return hit != null && IsTemporaryWallTransform(hit.transform);
    }

    private bool IsTemporaryWallTransform(Transform start)
    {
        if (start == null)
        {
            return false;
        }

        foreach (KeyValuePair<BlockerKey, TemporaryWall> pair in walls)
        {
            TemporaryWall wall = pair.Value;
            if (wall == null || wall.GameObject == null)
            {
                continue;
            }

            Transform wallTransform = wall.GameObject.transform;
            if (start == wallTransform || start.IsChildOf(wallTransform))
            {
                return true;
            }
        }

        return false;
    }

    #region String Processing

    private string GetStaticBlockingRejectReason(Collider2D hit, WorldBox worldBox, Bounds? queryBounds)
    {
        if (hit == null)
        {
            return "null";
        }

        if (!hit.enabled)
        {
            return "disabled";
        }

        if (hit.isTrigger)
        {
            return "isTrigger";
        }

        if (hit.bounds.size == Vector3.zero)
        {
            return "zero bounds";
        }

        if (IsTemporaryWallCollider(hit))
        {
            return "temporary wall";
        }

        if (StandardBox.IsOwnedByWorldBox(hit.transform, worldBox))
        {
            return "owned by WorldBox";
        }

        if (HasSceneMovableItem(hit.transform))
        {
            return "scene movable item";
        }

        if (queryBounds.HasValue && !TilemapHasTileInBounds(hit, queryBounds.Value))
        {
            return "tilemap has no tile in target bounds";
        }

        return null;
    }

    private string GetStaticBlockingRejectReason(Collider hit, WorldBox worldBox)
    {
        if (hit == null)
        {
            return "null";
        }

        if (!hit.enabled)
        {
            return "disabled";
        }

        if (hit.isTrigger)
        {
            return "isTrigger";
        }

        if (hit.bounds.size == Vector3.zero)
        {
            return "zero bounds";
        }

        if (IsTemporaryWallCollider(hit))
        {
            return "temporary wall";
        }

        if (StandardBox.IsOwnedByWorldBox(hit.transform, worldBox))
        {
            return "owned by WorldBox";
        }

        if (HasSceneMovableItem(hit.transform))
        {
            return "scene movable item";
        }

        return null;
    }

    private static string FormatHitStatus(bool accepted, string rejectReason)
    {
        return accepted ? "ACCEPT" : $"ignore: {rejectReason}";
    }

    private static string GetLayerLabel(int layer)
    {
        string layerName = LayerMask.LayerToName(layer);
        return string.IsNullOrEmpty(layerName) ? layer.ToString() : $"{layer}:{layerName}";
    }

    private static string GetObjectPath(Transform transform)
    {
        if (transform == null)
        {
            return "null";
        }

        string path = transform.name;
        Transform current = transform.parent;
        while (current != null)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }

        return path;
    }

    private static string GetOwnerName(UnityEngine.Object owner)
    {
        return owner != null ? owner.name : "Owner";
    }

    #endregion

    private static bool TilemapHasTileInBounds(Collider2D hit, Bounds queryBounds)
    {
        Tilemap tilemap = hit != null ? hit.GetComponent<Tilemap>() : null;
        if (tilemap == null && hit != null)
        {
            tilemap = hit.GetComponentInParent<Tilemap>();
        }

        if (tilemap == null)
        {
            return true;
        }

        Vector3Int minCell = tilemap.WorldToCell(new Vector3(queryBounds.min.x, queryBounds.min.y, 0f));
        Vector3Int maxCell = tilemap.WorldToCell(new Vector3(queryBounds.max.x, queryBounds.max.y, 0f));
        int minX = Mathf.Min(minCell.x, maxCell.x) - 1;
        int maxX = Mathf.Max(minCell.x, maxCell.x) + 1;
        int minY = Mathf.Min(minCell.y, maxCell.y) - 1;
        int maxY = Mathf.Max(minCell.y, maxCell.y) + 1;
        int z = minCell.z;

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                Vector3Int cell = new Vector3Int(x, y, z);
                if (!tilemap.HasTile(cell))
                {
                    continue;
                }

                Bounds tileBounds = GetTileWorldBounds(tilemap, cell);
                if (OverlapsXY(queryBounds, tileBounds))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string GetBlockingTag(Transform start)
    {
        Transform current = start;
        while (current != null)
        {
            if (current.CompareTag(PlatformTag))
            {
                return PlatformTag;
            }

            current = current.parent;
        }

        return start != null ? start.tag : null;
    }

    private static bool IsPlatformTag(string tag)
    {
        return tag == PlatformTag;
    }

    private static bool ShouldSkipOuterPlatformBlocker(BoxPushDirection direction, Bounds outerBounds, Bounds playerBounds)
    {
        return IsOuterSideOrTopEdge(direction) || IsPastOuterBottomEdge(direction, outerBounds, playerBounds);
    }

    private static bool IsOuterSideOrTopEdge(BoxPushDirection direction)
    {
        return direction == BoxPushDirection.Left ||
            direction == BoxPushDirection.Right ||
            direction == BoxPushDirection.Up;
    }

    private static bool IsPastOuterBottomEdge(BoxPushDirection direction, Bounds outerBounds, Bounds playerBounds)
    {
        return direction == BoxPushDirection.Down && playerBounds.max.y < outerBounds.min.y;
    }

    private static int GetSceneBricksLayer(int fallbackLayer)
    {
        int sceneBricksLayer = LayerMask.NameToLayer(SceneBricksLayerName);
        return sceneBricksLayer >= 0 ? sceneBricksLayer : fallbackLayer;
    }

    private static Bounds GetTileWorldBounds(Tilemap tilemap, Vector3Int cell)
    {
        GridLayout layout = tilemap.layoutGrid;
        Vector3 size = layout != null ? layout.cellSize : Vector3.one;
        size.x = Mathf.Abs(size.x);
        size.y = Mathf.Abs(size.y);
        size.z = Mathf.Max(Mathf.Abs(size.z), 0.001f);
        return new Bounds(tilemap.GetCellCenterWorld(cell), size);
    }

    private static bool OverlapsXY(Bounds a, Bounds b)
    {
        return a.min.x < b.max.x &&
            a.max.x > b.min.x &&
            a.min.y < b.max.y &&
            a.max.y > b.min.y;
    }

    private sealed class TemporaryWall
    {
        public readonly GameObject GameObject;
        public BoxCollider2D Collider2D;
        public BoxCollider Collider3D;
        public BoxPushDirection Direction;
        public float ExpireAt;

        public TemporaryWall(GameObject gameObject)
        {
            GameObject = gameObject;
        }
    }

    private readonly struct BlockerKey : IEquatable<BlockerKey>
    {
        public readonly WorldBox WorldBox;
        public readonly UnityEngine.Object Owner;
        private readonly int worldBoxId;
        private readonly int ownerId;

        public BlockerKey(WorldBox worldBox, UnityEngine.Object owner)
        {
            WorldBox = worldBox;
            Owner = owner;
            worldBoxId = worldBox != null ? worldBox.GetInstanceID() : 0;
            ownerId = owner != null ? owner.GetInstanceID() : 0;
        }

        public bool MatchesWorldBox(WorldBox worldBox)
        {
            return worldBox != null && worldBoxId == worldBox.GetInstanceID();
        }

        public bool Equals(BlockerKey other)
        {
            return worldBoxId == other.worldBoxId && ownerId == other.ownerId;
        }

        public override bool Equals(object obj)
        {
            return obj is BlockerKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (worldBoxId * 397) ^ ownerId;
            }
        }
    }
}
