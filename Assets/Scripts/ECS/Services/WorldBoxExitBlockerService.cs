using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class WorldBoxExitBlockerService : ServiceBase
{
    private const string PlatformTag = "Platform";
    private const string SceneBricksLayerName = "SceneBricks";
    private const string UntaggedTag = "Untagged";

    [SerializeField, Min(0f)] private float innerCheckPadding = 0.02f;
    [SerializeField, Min(0.01f)] private float fallbackCellSize = 0.5f;
    [SerializeField, Min(0.01f)] private float fallbackDepth = 1f;
    [SerializeField, Min(0.01f)] private float minimumColliderSize = 0.05f;
    [SerializeField, Min(0.02f)] private float keepAliveDuration = 0.15f;

    private readonly Collider2D[] overlapHits2D = new Collider2D[32];
    private readonly Collider[] overlapHits3D = new Collider[32];
    private readonly Dictionary<WorldBox, TemporaryWall> walls = new Dictionary<WorldBox, TemporaryWall>();
    private readonly List<WorldBox> expiredWallOwners = new List<WorldBox>(16);

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
        if (worldBox == null || outerBounds.size == Vector3.zero || innerTargetBounds.size == Vector3.zero)
        {
            return false;
        }

        if (!use2D && !use3D)
        {
            use2D = true;
        }

        if (!TryGetStaticBlockingColliderTag(innerTargetBounds, worldBox, blockingMask, use2D, use3D, out string blockingTag))
        {
            return false;
        }

        if (IsPlatformTag(blockingTag) && IsPastOuterBottomEdge(direction, outerBounds, playerBounds))
        {
            return false;
        }

        Bounds wallBounds = CalculateOuterWallBounds(worldBox, direction, outerBounds, playerBounds);
        RefreshWall(worldBox, direction, wallBounds, use2D, use3D, blockingTag);
        return true;
    }

    public void Clear(WorldBox worldBox)
    {
        if (worldBox == null)
        {
            return;
        }

        RemoveWall(worldBox);
    }

    protected override void OnDestroy()
    {
        foreach (KeyValuePair<WorldBox, TemporaryWall> pair in walls)
        {
            DestroyWallObject(pair.Value);
        }

        walls.Clear();
        expiredWallOwners.Clear();
        base.OnDestroy();
    }

    private void LateUpdate()
    {
        expiredWallOwners.Clear();
        foreach (KeyValuePair<WorldBox, TemporaryWall> pair in walls)
        {
            TemporaryWall wall = pair.Value;
            if (pair.Key == null || wall == null || wall.GameObject == null || Time.time > wall.ExpireAt)
            {
                expiredWallOwners.Add(pair.Key);
            }
        }

        for (int i = 0; i < expiredWallOwners.Count; i++)
        {
            RemoveWall(expiredWallOwners[i]);
        }
    }

    private bool TryGetStaticBlockingColliderTag(
        Bounds targetBounds,
        WorldBox worldBox,
        LayerMask blockingMask,
        bool check2D,
        bool check3D,
        out string blockingTag)
    {
        blockingTag = null;
        Bounds queryBounds = targetBounds;
        queryBounds.Expand(innerCheckPadding * 2f);

        Physics.SyncTransforms();
        Physics2D.SyncTransforms();

        if (check2D && TryGetStaticBlockingColliderTag2D(queryBounds, worldBox, blockingMask, out blockingTag))
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
        LayerMask blockingMask,
        out string blockingTag)
    {
        blockingTag = null;
        Vector2 size = new Vector2(
            Mathf.Max(minimumColliderSize, queryBounds.size.x),
            Mathf.Max(minimumColliderSize, queryBounds.size.y));

        int hitCount = Physics2D.OverlapBoxNonAlloc((Vector2)queryBounds.center, size, 0f, overlapHits2D, blockingMask);
        for (int i = 0; i < hitCount; i++)
        {
            Collider2D hit = overlapHits2D[i];
            if (IsStaticBlockingCollider(hit, worldBox))
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

        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = overlapHits3D[i];
            if (IsStaticBlockingCollider(hit, worldBox))
            {
                blockingTag = GetBlockingTag(hit.transform);
                return true;
            }
        }

        return false;
    }

    private bool IsStaticBlockingCollider(Collider2D hit, WorldBox worldBox)
    {
        return hit != null &&
            hit.enabled &&
            !hit.isTrigger &&
            hit.bounds.size != Vector3.zero &&
            !IsOwnedByWorldBox(hit.transform, worldBox) &&
            !HasSceneMovableItem(hit.transform);
    }

    private bool IsStaticBlockingCollider(Collider hit, WorldBox worldBox)
    {
        return hit != null &&
            hit.enabled &&
            !hit.isTrigger &&
            hit.bounds.size != Vector3.zero &&
            !IsOwnedByWorldBox(hit.transform, worldBox) &&
            !HasSceneMovableItem(hit.transform);
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
        WorldBox worldBox,
        BoxPushDirection direction,
        Bounds wallBounds,
        bool use2D,
        bool use3D,
        string blockingTag)
    {
        TemporaryWall wall = GetOrCreateWall(worldBox);
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
    }

    private TemporaryWall GetOrCreateWall(WorldBox worldBox)
    {
        if (walls.TryGetValue(worldBox, out TemporaryWall wall) && wall != null && wall.GameObject != null)
        {
            return wall;
        }

        GameObject wallObject = new GameObject($"WorldBoxExitBlocker.{worldBox.name}");
        Scene scene = worldBox.gameObject.scene;
        if (scene.IsValid())
        {
            SceneManager.MoveGameObjectToScene(wallObject, scene);
        }

        wall = new TemporaryWall(wallObject);
        walls[worldBox] = wall;
        return wall;
    }

    private void RemoveWall(WorldBox worldBox)
    {
        if (!walls.TryGetValue(worldBox, out TemporaryWall wall))
        {
            return;
        }

        DestroyWallObject(wall);
        walls.Remove(worldBox);
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

    private static bool IsOwnedByWorldBox(Transform start, WorldBox worldBox)
    {
        return start != null && worldBox != null && (start == worldBox.transform || start.IsChildOf(worldBox.transform));
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

    private static bool IsPastOuterBottomEdge(BoxPushDirection direction, Bounds outerBounds, Bounds playerBounds)
    {
        return direction == BoxPushDirection.Down && playerBounds.max.y < outerBounds.min.y;
    }

    private static int GetSceneBricksLayer(int fallbackLayer)
    {
        int sceneBricksLayer = LayerMask.NameToLayer(SceneBricksLayerName);
        return sceneBricksLayer >= 0 ? sceneBricksLayer : fallbackLayer;
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
}
