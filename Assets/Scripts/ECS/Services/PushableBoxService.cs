using QFramework;
using UnityEngine;

public class PushableBoxService : ServiceBase<StandardBox>
{
    private const int MaxTeleportTargetBlockerClearAttempts = 8;
    private const float OuterEdgeBlockerTouchTolerance = 0.04f;

    private IUnRegister pushRequestUnRegister;

    protected override void Awake()
    {
        base.Awake();
        if (!IsActiveService)
        {
            return;
        }

        pushRequestUnRegister = RegisterEvent<BoxPushRequestEvent>(OnPushRequested);
    }

    protected override void OnDestroy()
    {
        pushRequestUnRegister?.UnRegister();
        pushRequestUnRegister = null;
        base.OnDestroy();
    }

    public BoxPushInitializeEvent InitializePush(StandardBox box, BoxPushDirection direction, UnityEngine.GameObject pusher = null)
    {
        bool canPush = CanPush(box, direction);
        BoxPushInitializeEvent initialize = new BoxPushInitializeEvent(box, direction, pusher, canPush);
        SendEvent(initialize);
        return initialize;
    }

    public BoxPushAttemptEvent TryPush(StandardBox box, BoxPushDirection direction, UnityEngine.GameObject pusher = null)
    {
        return TryPush(box, direction, pusher, CanPush(box, direction));
    }

    public BoxPushAttemptEvent TryPush(StandardBox box, BoxPushDirection direction, UnityEngine.GameObject pusher, bool canPush)
    {
        canPush = canPush && box != null && IsRegistered(box);
        BoxPushAttemptEvent attempt = new BoxPushAttemptEvent(box, direction, pusher, canPush);
        SendEvent(attempt);
        return attempt;
    }

    private void OnPushRequested(BoxPushRequestEvent e)
    {
        TryPush(e.Box, e.Direction, e.Pusher);
    }

    private bool CanPush(StandardBox box, BoxPushDirection direction)
    {
        return box != null && IsRegistered(box) && box.CanPushToward(direction);
    }

    public void CheckAndTryTeleportAllPushableBoxesWithOuterBoundsToInner(Bounds outerBounds, WorldBox worldBox)
    {
        if (worldBox == null)
        {
            return;
        }

        foreach (StandardBox box in this.components)
        {
            if (box == null)
            {
                continue;
            }

            if (box == worldBox)
            {
                continue;
            }

            if (box.gameObject.scene != worldBox.gameObject.scene)
            {
                continue;
            }

            Bounds boxBounds = box.Bounds;
            if (boxBounds.size == Vector3.zero)
            {
                ClearBoxExitBlocker(worldBox, box);
                continue;
            }

            var center = boxBounds.center;
            if (outerBounds.Contains(center))
            {
                if (!TryRefreshOuterContactExitBlocker(outerBounds, worldBox, box, boxBounds))
                {
                    ClearBoxExitBlocker(worldBox, box);
                }

                continue;
            }

            // Determine exit direction (prefer the largest separation)
            if (!TryGetExitDirection(outerBounds, boxBounds, out BoxPushDirection direction))
            {
                ClearBoxExitBlocker(worldBox, box);
                continue;
            }

            // Compute target position inside inner area (mirror of WorldBox.TryGetInnerTargetPosition)
            if (!TryGetInnerTargetBoundsForBox(direction, worldBox.Bounds, box, boxBounds, out Vector3 targetPosition, out Bounds targetBounds))
            {
                ClearBoxExitBlocker(worldBox, box);
                continue;
            }

            Bounds movableTargetBounds = targetBounds;
            movableTargetBounds.Expand(-0.05f); // Slightly shrink bounds to avoid edge cases

            WorldBoxExitBlockerService blockerService = ServiceBase.Get<WorldBoxExitBlockerService>();
            if (blockerService == null)
            {
                // no blocker service, just teleport
                MoveBoxToInner(box, targetPosition);
                TypeEventSystem.Global.Send<OnOuterToInnerEvent>();
                continue;
            }

            bool use2D = box.Collider2D != null;
            bool use3D = box.Collider3D != null;
            if (blockerService.TryRefreshBlockerForStaticInnerHit(
                worldBox,
                box,
                direction,
                outerBounds,
                targetBounds,
                boxBounds,
                box.CollisionMask,
                use2D,
                use3D))
            {
                continue;
            }

            blockerService.Clear(worldBox, box);

            if (TryClearTeleportTargetStandardBoxBlockers(
                blockerService,
                movableTargetBounds,
                worldBox,
                ignoredBox: null,
                box.CollisionMask,
                use2D,
                use3D,
                direction,
                box.Owner))
            {
                MoveBoxToInner(box, targetPosition);
                continue;
            }
        }
    }

    private static bool TryRefreshOuterContactExitBlocker(
        Bounds outerBounds,
        WorldBox worldBox,
        StandardBox box,
        Bounds boxBounds)
    {
        if (!TryGetOuterContactDirection(outerBounds, boxBounds, box, out BoxPushDirection direction))
        {
            return false;
        }

        if (!TryGetInnerTargetBoundsForBox(direction, worldBox.Bounds, box, boxBounds, out _, out Bounds targetBounds))
        {
            return false;
        }

        WorldBoxExitBlockerService blockerService = ServiceBase.Get<WorldBoxExitBlockerService>();
        if (blockerService == null)
        {
            return false;
        }

        bool use2D = box.Collider2D != null;
        bool use3D = box.Collider3D != null;
        return blockerService.TryRefreshBlockerForStaticInnerHit(
            worldBox,
            box,
            direction,
            outerBounds,
            targetBounds,
            boxBounds,
            box.CollisionMask,
            use2D,
            use3D);
    }

    private static void MoveBoxToInner(StandardBox box, Vector3 targetPosition)
    {
        box.MoveTo(targetPosition);
        if (ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            physicalBoxService.CancelLinearPush(box);
        }

        if (ServiceBase.TryGet(out SceneMovableInteractionService sceneMovable))
        {
            sceneMovable.RefreshItemImpactBaseline(box);
        }
    }

    private static void ClearBoxExitBlocker(WorldBox worldBox, StandardBox box)
    {
        if (worldBox == null || box == null)
        {
            return;
        }

        if (ServiceBase.TryGet(out WorldBoxExitBlockerService blockerService))
        {
            blockerService.Clear(worldBox, box);
        }
    }

    private static bool TryClearTeleportTargetStandardBoxBlockers(
        WorldBoxExitBlockerService blockerService,
        Bounds targetBounds,
        WorldBox worldBox,
        StandardBox ignoredBox,
        LayerMask blockingMask,
        bool use2D,
        bool use3D,
        BoxPushDirection direction,
        UnityEngine.GameObject pusher)
    {
        if (blockerService == null)
        {
            return true;
        }

        StandardBox[] attemptedBlockers = new StandardBox[MaxTeleportTargetBlockerClearAttempts];
        for (int attemptIndex = 0; attemptIndex < MaxTeleportTargetBlockerClearAttempts; attemptIndex++)
        {
            if (!blockerService.TryGetTeleportTargetStandardBoxBlocker(
                targetBounds,
                worldBox,
                ignoredBox,
                blockingMask,
                use2D,
                use3D,
                out StandardBox blocker))
            {
                return true;
            }

            for (int previousIndex = 0; previousIndex < attemptIndex; previousIndex++)
            {
                if (attemptedBlockers[previousIndex] == blocker)
                {
                    return false;
                }
            }

            attemptedBlockers[attemptIndex] = blocker;
            Vector3 blockerPosition = blocker.transform.position;
            BoxPushAttemptEvent attempt = blocker.TryPush(direction, pusher);
            if (!attempt.CanPush || !HasMovedFrom(blocker, blockerPosition))
            {
                return false;
            }
        }

        return !blockerService.TryGetTeleportTargetStandardBoxBlocker(
            targetBounds,
            worldBox,
            ignoredBox,
            blockingMask,
            use2D,
            use3D,
            out _);
    }

    private static bool HasMovedFrom(StandardBox box, Vector3 position)
    {
        return box != null && (box.transform.position - position).sqrMagnitude > Mathf.Epsilon;
    }

    private static bool TryGetOuterContactDirection(
        Bounds outerBounds,
        Bounds itemBounds,
        StandardBox box,
        out BoxPushDirection direction)
    {
        direction = default;
        float threshold = OuterEdgeBlockerTouchTolerance;
        float downThreshold = Mathf.Max(threshold, GetCellAxisSize(box, true));
        float leftDistance = itemBounds.min.x - outerBounds.min.x;
        float rightDistance = outerBounds.max.x - itemBounds.max.x;
        float downDistance = itemBounds.min.y - outerBounds.min.y;
        float upDistance = outerBounds.max.y - itemBounds.max.y;
        bool touchesLeft = leftDistance <= threshold;
        bool touchesRight = rightDistance <= threshold;
        bool touchesDown = downDistance <= downThreshold;
        bool touchesUp = upDistance <= threshold;

        if (!touchesLeft && !touchesRight && !touchesDown && !touchesUp)
        {
            return false;
        }

        bool found = false;
        float bestDistance = float.PositiveInfinity;
        if (touchesLeft)
        {
            direction = BoxPushDirection.Left;
            bestDistance = leftDistance;
            found = true;
        }

        if (touchesRight && rightDistance < bestDistance)
        {
            direction = BoxPushDirection.Right;
            bestDistance = rightDistance;
            found = true;
        }

        if (touchesDown && downDistance < bestDistance)
        {
            direction = BoxPushDirection.Down;
            bestDistance = downDistance;
            found = true;
        }

        if (touchesUp && upDistance < bestDistance)
        {
            direction = BoxPushDirection.Up;
            found = true;
        }

        return found;
    }

    private static float GetCellAxisSize(StandardBox box, bool vertical)
    {
        Grid grid = box != null ? box.Grid : null;
        if (grid == null)
        {
            return 0f;
        }

        return Mathf.Abs(vertical ? grid.cellSize.y : grid.cellSize.x);
    }

    private static bool TryGetExitDirection(Bounds outerBounds, Bounds itemBounds, out BoxPushDirection direction)
    {
        float left = outerBounds.min.x - itemBounds.center.x;
        float right = itemBounds.min.x - outerBounds.center.x;
        float down = outerBounds.min.y - itemBounds.center.y;
        float up = itemBounds.min.y - outerBounds.center.y;
        float max = left;
        direction = BoxPushDirection.Left;

        if (right > max)
        {
            max = right;
            direction = BoxPushDirection.Right;
        }

        if (down > max)
        {
            max = down;
            direction = BoxPushDirection.Down;
        }

        if (up > max)
        {
            max = up;
            direction = BoxPushDirection.Up;
        }

        return max > 0f;
    }

    private static bool TryGetInnerTargetBoundsForBox(
        BoxPushDirection direction,
        Bounds innerBounds,
        StandardBox box,
        Bounds boxBounds,
        out Vector3 position,
        out Bounds targetBounds)
    {
        targetBounds = default;
        if (!TryGetInnerTargetPositionForBox(direction, innerBounds, box, boxBounds, out position))
        {
            return false;
        }

        targetBounds = boxBounds;
        targetBounds.center = position;
        return true;
    }

    private static bool TryGetInnerTargetPositionForBox(BoxPushDirection direction, Bounds innerBounds, StandardBox box, Bounds boxBounds, out Vector3 position)
    {
        position = innerBounds.center;
        if (innerBounds.size == Vector3.zero || boxBounds.size == Vector3.zero)
        {
            return false;
        }

        Vector3 extents = boxBounds.extents;

        switch (direction)
        {
            case BoxPushDirection.Left:
                position.x = innerBounds.min.x - extents.x;
                position.y = innerBounds.center.y;
                break;
            case BoxPushDirection.Right:
                position.x = innerBounds.max.x + extents.x;
                position.y = innerBounds.center.y;
                break;
            case BoxPushDirection.Down:
                position.x = innerBounds.center.x;
                position.y = innerBounds.min.y - extents.y;
                break;
            case BoxPushDirection.Up:
                position.x = innerBounds.center.x;
                position.y = innerBounds.max.y + extents.y;
                break;
        }

        // Snap to grid if needed
        if (box != null && box.AlignToGrid && box.Grid != null)
        {
            Grid grid = box.Grid;
            Vector3Int cell = grid.WorldToCell(position);
            Vector3 snapped = grid.CellToWorld(cell) + Vector3.Scale(grid.cellSize, box.CellOffset);
            snapped.z = position.z;
            position = snapped;
        }

        return true;
    }
}
