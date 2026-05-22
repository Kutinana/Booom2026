using QFramework;
using UnityEngine;

public class PushableBoxService : ServiceBase<StandardBox>
{
    private const int MaxTeleportTargetBlockerClearAttempts = 8;

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
        foreach (StandardBox box in this.components)
        {
            if (box == null)
            {
                continue;
            }

            var center = box.Bounds.center;
            if (outerBounds.Contains(center))
            {
                continue;
            }

            // Determine exit direction (prefer the largest separation)
            if (!TryGetExitDirection(outerBounds, box.Bounds, out BoxPushDirection direction))
            {
                continue;
            }

            // Compute target position inside inner area (mirror of WorldBox.TryGetInnerTargetPosition)
            if (!TryGetInnerTargetPositionForBox(direction, worldBox.Bounds, box, out Vector3 targetPosition))
            {
                continue;
            }

            Bounds targetBounds = box.Bounds;
            targetBounds.center = targetPosition;
            targetBounds.Expand(-0.05f); // Slightly shrink bounds to avoid edge cases

            // Check and try to clear blockers using WorldBoxExitBlockerService
            if (!ServiceBase.TryGet(out WorldBoxExitBlockerService blockerService))
            {
                // no blocker service, just teleport
                box.MoveTo(targetPosition);
                if (ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
                {
                    physicalBoxService.CancelLinearPush(box);
                }
                if (ServiceBase.TryGet(out SceneMovableInteractionService sceneMovable))
                {
                    sceneMovable.RefreshItemImpactBaseline(box);
                }
                TypeEventSystem.Global.Send<OnOuterToInnerEvent>();
                continue;
            }

            bool use2D = box.Collider2D != null;
            bool use3D = box.Collider3D != null;

            if (TryClearTeleportTargetStandardBoxBlockers(
                blockerService,
                targetBounds,
                worldBox,
                ignoredBox: null,
                box.CollisionMask,
                use2D,
                use3D,
                direction,
                box.Owner))
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
                continue;
            }
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

    private static bool TryGetInnerTargetPositionForBox(BoxPushDirection direction, Bounds innerBounds, StandardBox box, out Vector3 position)
    {
        position = innerBounds.center;

        Vector3 extents = box.Bounds.extents;

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
