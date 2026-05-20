using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

public partial class PlayerController
{
    private bool TryFindOverlapSeparation(Bounds bounds, Vector3 movedDelta, out Vector3 correction)
    {
        correction = Vector3.zero;
        float bestMagnitude = float.PositiveInfinity;

        if (m_Collider2D != null)
        {
            int hitCount = Physics2D.OverlapBoxNonAlloc((Vector2)bounds.center, (Vector2)bounds.size, 0f, overlapHits2D, collisionMask);
            for (int i = 0; i < hitCount; i++)
            {
                Collider2D other = overlapHits2D[i];
                if (!IsValidOverlapCollider(other))
                {
                    continue;
                }

                if (TryEvaluateTilemapOverlaps(other, bounds, movedDelta, ref bestMagnitude, ref correction))
                {
                    continue;
                }

                Bounds otherBounds = other.bounds;
                GameObject platform = GetPlatformObject(other);
                if (!ShouldResolveOverlap(platform, bounds, otherBounds, movedDelta))
                {
                    continue;
                }

                Vector3 candidate = CalculateAabbSeparation(bounds, otherBounds, movedDelta);
                float magnitude = candidate.sqrMagnitude;
                if (magnitude > 0f && magnitude < bestMagnitude)
                {
                    bestMagnitude = magnitude;
                    correction = candidate;
                }
            }
        }

        if (m_Collider3D != null)
        {
            int hitCount = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents, overlapHits3D, Quaternion.identity, collisionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hitCount; i++)
            {
                Collider other = overlapHits3D[i];
                if (!IsValidOverlapCollider(other))
                {
                    continue;
                }

                Bounds otherBounds = other.bounds;
                GameObject platform = GetPlatformObject(other);
                if (!ShouldResolveOverlap(platform, bounds, otherBounds, movedDelta))
                {
                    continue;
                }

                Vector3 candidate = CalculateAabbSeparation(bounds, otherBounds, movedDelta);
                float magnitude = candidate.sqrMagnitude;
                if (magnitude > 0f && magnitude < bestMagnitude)
                {
                    bestMagnitude = magnitude;
                    correction = candidate;
                }
            }
        }

        return bestMagnitude < float.PositiveInfinity;
    }

    private bool TryEvaluateTilemapOverlaps(Collider2D tileCollider, Bounds playerBounds, Vector3 movedDelta, ref float bestMagnitude, ref Vector3 correction)
    {
        Tilemap tilemap = tileCollider.GetComponent<Tilemap>();
        if (tilemap == null)
        {
            return false;
        }

        Vector3Int minCell = tilemap.WorldToCell(new Vector3(playerBounds.min.x - OverlapResolveEpsilon, playerBounds.min.y - OverlapResolveEpsilon, 0f));
        Vector3Int maxCell = tilemap.WorldToCell(new Vector3(playerBounds.max.x + OverlapResolveEpsilon, playerBounds.max.y + OverlapResolveEpsilon, 0f));
        GameObject platform = GetPlatformObject(tileCollider);

        for (int x = minCell.x - 1; x <= maxCell.x + 1; x++)
        {
            for (int y = minCell.y - 1; y <= maxCell.y + 1; y++)
            {
                Vector3Int cell = new Vector3Int(x, y, minCell.z);
                if (!tilemap.HasTile(cell))
                {
                    continue;
                }

                Bounds tileBounds = GetTileWorldBounds(tilemap, cell);
                if (!ShouldResolveOverlap(platform, playerBounds, tileBounds, movedDelta))
                {
                    continue;
                }

                Vector3 candidate = CalculateAabbSeparation(playerBounds, tileBounds, movedDelta);
                float magnitude = candidate.sqrMagnitude;
                if (magnitude > 0f && magnitude < bestMagnitude)
                {
                    bestMagnitude = magnitude;
                    correction = candidate;
                }
            }
        }

        return true;
    }

    private static Bounds GetTileWorldBounds(Tilemap tilemap, Vector3Int cell)
    {
        GridLayout layout = tilemap.layoutGrid;
        Vector3 size = layout != null ? layout.cellSize : Vector3.one;
        size.x = Mathf.Abs(size.x);
        size.y = Mathf.Abs(size.y);
        size.z = Mathf.Max(Mathf.Abs(size.z), OverlapResolveEpsilon);
        return new Bounds(tilemap.GetCellCenterWorld(cell), size);
    }

    private bool IsValidOverlapCollider(Collider2D other)
    {
        return other != null &&
            !other.isTrigger &&
            other != m_Collider2D &&
            other.bounds.size != Vector3.zero;
    }

    private bool IsValidOverlapCollider(Collider other)
    {
        return other != null &&
            other != m_Collider3D &&
            other.bounds.size != Vector3.zero;
    }

    private bool ShouldResolveOverlap(GameObject platform, Bounds playerBounds, Bounds otherBounds, Vector3 movedDelta)
    {
        if (!OverlapsXY(playerBounds, otherBounds))
        {
            return false;
        }

        if (IsWalkableTopSideContact(playerBounds, otherBounds, movedDelta))
        {
            return false;
        }

        if (platform == null)
        {
            return true;
        }

        if (movedDelta.y >= 0f)
        {
            return false;
        }

        if (platform == ignoredPlatform && Time.time < platformDropUntil)
        {
            return false;
        }

        float previousBottom = playerBounds.min.y - movedDelta.y;
        float tolerance = Mathf.Max(platformLandingTolerance, skinWidth * 2f);
        return previousBottom >= otherBounds.max.y - tolerance;
    }

    private bool IsWalkableTopSideContact(Bounds playerBounds, Bounds otherBounds, Vector3 direction)
    {
        if (Mathf.Abs(direction.x) <= 0f || Mathf.Abs(direction.x) < Mathf.Abs(direction.y))
        {
            return false;
        }

        float tolerance = Mathf.Max(platformLandingTolerance, skinWidth * 2f);
        return playerBounds.min.y >= otherBounds.max.y - tolerance;
    }

    private bool Cast(Vector3 direction, float distance, out RayHit bestHit)
    {
        bestHit = default;
        Bounds bounds = GetBounds();
        if (bounds.size == Vector3.zero)
        {
            return false;
        }

        bounds.Expand(-skinWidth * 2f);

        int count = Mathf.Max(2, raysPerSide);
        float bestDistance = float.PositiveInfinity;
        bool hitAny = false;
        bool vertical = Mathf.Abs(direction.y) > 0f;

        for (int i = 0; i < count; i++)
        {
            float t = count == 1 ? 0.5f : i / (float)(count - 1);
            Vector3 origin;

            if (vertical)
            {
                origin = new Vector3(Mathf.Lerp(bounds.min.x, bounds.max.x, t), direction.y > 0f ? bounds.max.y : bounds.min.y, transform.position.z);
            }
            else
            {
                float minY = bounds.min.y + skinWidth;
                float maxY = bounds.max.y - skinWidth;
                float originY = minY <= maxY ? Mathf.Lerp(minY, maxY, t) : bounds.center.y;
                origin = new Vector3(direction.x > 0f ? bounds.max.x : bounds.min.x, originY, transform.position.z);
            }

            if (CastSingle(bounds, origin, direction, distance, out RayHit hit) && hit.distance < bestDistance)
            {
                bestDistance = hit.distance;
                bestHit = hit;
                hitAny = true;
            }
        }

        return hitAny;
    }

    private static bool IsBlockingAxisNormal(Vector3 normal, Vector3 direction)
    {
        if (direction.x > 0f)
        {
            return normal.x <= -AxisNormalBlockThreshold;
        }

        if (direction.x < 0f)
        {
            return normal.x >= AxisNormalBlockThreshold;
        }

        if (direction.y > 0f)
        {
            return normal.y <= -AxisNormalBlockThreshold;
        }

        if (direction.y < 0f)
        {
            return normal.y >= AxisNormalBlockThreshold;
        }

        return false;
    }

    private bool CastSingle(Bounds bounds, Vector3 origin, Vector3 direction, float distance, out RayHit hit)
    {
        float bestDistance = float.PositiveInfinity;
        hit = default;

        if (m_Collider2D != null)
        {
            int hitCount = Physics2D.RaycastNonAlloc((Vector2)origin, (Vector2)direction, hits2D, distance, collisionMask);
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit2D hit2D = hits2D[i];
                GameObject platform = GetPlatformObject(hit2D.collider);
                if (hit2D.collider != null &&
                    !hit2D.collider.isTrigger &&
                    hit2D.collider != m_Collider2D &&
                    !IsWalkableTopSideContact(bounds, hit2D.collider.bounds, direction) &&
                    ShouldCollideWithPlatform(platform, hit2D.point.y, bounds, hit2D.normal, direction) &&
                    IsBlockingAxisNormal(hit2D.normal, direction) &&
                    hit2D.distance < bestDistance)
                {
                    bestDistance = hit2D.distance;
                    hit = new RayHit(hit2D.distance, hit2D.collider.tag, hit2D.collider.GetComponentInParent<StandardBox>(), platform);
                }
            }
        }

        if (m_Collider3D != null)
        {
            int hitCount = Physics.RaycastNonAlloc(origin, direction, hits3D, distance, collisionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit3D = hits3D[i];
                GameObject platform = GetPlatformObject(hit3D.collider);
                if (hit3D.collider != null &&
                    hit3D.collider != m_Collider3D &&
                    !IsWalkableTopSideContact(bounds, hit3D.collider.bounds, direction) &&
                    ShouldCollideWithPlatform(platform, hit3D.point.y, bounds, hit3D.normal, direction) &&
                    IsBlockingAxisNormal(hit3D.normal, direction) &&
                    hit3D.distance < bestDistance)
                {
                    bestDistance = hit3D.distance;
                    hit = new RayHit(hit3D.distance, hit3D.collider.tag, hit3D.collider.GetComponentInParent<StandardBox>(), platform);
                }
            }
        }

        return bestDistance < float.PositiveInfinity;
    }

    private bool CastRetainedVelocity(Vector2 direction, float distance, out float hitDistance)
    {
        hitDistance = float.PositiveInfinity;
        Bounds bounds = GetBounds();

        if (bounds.size == Vector3.zero || distance <= 0f)
        {
            return false;
        }

        Vector2 origin;
        if (Mathf.Abs(direction.x) > 0f)
        {
            origin = new Vector2(direction.x > 0f ? bounds.max.x : bounds.min.x, transform.position.y);
        }
        else
        {
            origin = new Vector2(transform.position.x, direction.y > 0f ? bounds.max.y : bounds.min.y);
        }

        bool hitAny = false;
        float bestDistance = float.PositiveInfinity;

        if (m_Collider2D != null)
        {
            int hitCount = Physics2D.RaycastNonAlloc(origin, direction, hits2D, distance, collisionMask);
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit2D hit2D = hits2D[i];
                GameObject platform = GetPlatformObject(hit2D.collider);
                if (hit2D.collider != null &&
                    !hit2D.collider.isTrigger &&
                    hit2D.collider != m_Collider2D &&
                    !IsWalkableTopSideContact(bounds, hit2D.collider.bounds, direction) &&
                    ShouldCollideWithPlatform(platform, hit2D.point.y, bounds, hit2D.normal, direction) &&
                    IsBlockingAxisNormal(hit2D.normal, direction) &&
                    hit2D.distance < bestDistance)
                {
                    bestDistance = hit2D.distance;
                    hitAny = true;
                }
            }
        }

        if (m_Collider3D != null)
        {
            int hitCount = Physics.RaycastNonAlloc(origin, direction, hits3D, distance, collisionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit3D = hits3D[i];
                GameObject platform = GetPlatformObject(hit3D.collider);
                if (hit3D.collider != null &&
                    hit3D.collider != m_Collider3D &&
                    !IsWalkableTopSideContact(bounds, hit3D.collider.bounds, direction) &&
                    ShouldCollideWithPlatform(platform, hit3D.point.y, bounds, hit3D.normal, direction) &&
                    IsBlockingAxisNormal(hit3D.normal, direction) &&
                    hit3D.distance < bestDistance)
                {
                    bestDistance = hit3D.distance;
                    hitAny = true;
                }
            }
        }

        hitDistance = bestDistance;
        return hitAny;
    }

    private Bounds GetBounds()
    {
        if (m_Collider2D != null)
        {
            return m_Collider2D.bounds;
        }

        return m_Collider3D != null ? m_Collider3D.bounds : new Bounds(transform.position, Vector3.zero);
    }

    private float GetCellHeight()
    {
        if (grid == null)
        {
            return 1f;
        }

        return Mathf.Max(0.01f, Mathf.Abs(grid.cellSize.y));
    }

    private Grid FindSceneGrid()
    {
        Scene scene = gameObject.scene;
        Grid found = null;
        bfsQueue.Clear();

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            bfsQueue.Enqueue(root.transform);
        }

        while (bfsQueue.Count > 0)
        {
            Transform current = bfsQueue.Dequeue();
            Grid candidate = current.GetComponent<Grid>();
            if (candidate != null)
            {
                if (found != null)
                {
#if UNITY_EDITOR
                    Debug.LogWarning("Multiple Grid components found in the player's scene. Using the first one.", this);
#endif
                    return found;
                }

                found = candidate;
            }

            for (int i = 0; i < current.childCount; i++)
            {
                bfsQueue.Enqueue(current.GetChild(i));
            }
        }

        return found;
    }
}
