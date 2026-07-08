using QFramework;
using UnityEngine;

public class PushableBoxService : ServiceBase<StandardBox>
{
    private const float OuterEdgeBlockerTouchTolerance = 0.04f;

    private IUnRegister pushRequestUnRegister;

    private readonly System.Collections.Generic.List<BoxTransitionState> activeTransitions =
        new System.Collections.Generic.List<BoxTransitionState>();

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

        for (int i = 0; i < activeTransitions.Count; i++)
        {
            if (activeTransitions[i].VisualClone != null)
            {
                Destroy(activeTransitions[i].VisualClone);
            }
        }

        activeTransitions.Clear();

        base.OnDestroy();
    }

    public BoxPushInitializeEvent InitializePush(StandardBox box, BoxPushDirection direction,
        GameObject pusher = null)
    {
        bool canPush = CanPush(box, direction);
        BoxPushInitializeEvent initialize = new BoxPushInitializeEvent(box, direction, pusher, canPush);
        SendEvent(initialize);
        return initialize;
    }

    public BoxPushAttemptEvent TryPush(StandardBox box, BoxPushDirection direction, GameObject pusher = null)
    {
        return TryPush(box, direction, pusher, CanPush(box, direction));
    }

    public BoxPushAttemptEvent TryPush(StandardBox box, BoxPushDirection direction, GameObject pusher,
        bool canPush)
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

    public bool IsBoxExitingWorldBox(StandardBox box, out WorldBox worldBox, out BoxPushDirection direction)
    {
        worldBox = null;
        direction = default;
        if (box == null)
        {
            return false;
        }

        for (int i = 0; i < activeTransitions.Count; i++)
        {
            var state = activeTransitions[i];
            if (state.Box == box && !state.IsEntering)
            {
                worldBox = state.WorldBox;
                direction = state.Direction;
                return true;
            }
        }

        return false;
    }

    public bool IsBoxEnteringWorldBox(StandardBox box, out WorldBox worldBox, out BoxPushDirection direction)
    {
        worldBox = null;
        direction = default;
        if (box == null)
        {
            return false;
        }

        for (int i = 0; i < activeTransitions.Count; i++)
        {
            var state = activeTransitions[i];
            if (state.Box == box && state.IsEntering)
            {
                worldBox = state.WorldBox;
                direction = state.Direction;
                return true;
            }
        }

        return false;
    }


    public bool IsBoxTransitioning(StandardBox box)
    {
        if (box == null)
        {
            return false;
        }

        for (int i = 0; i < activeTransitions.Count; i++)
        {
            if (activeTransitions[i].Box == box)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the box is in a transition that should prevent gravity simulation.
    /// Gravity-driven entering transitions allow gravity to continue (gravity IS the pusher).
    /// </summary>
    public bool ShouldSkipGravityForTransition(StandardBox box)
    {
        if (box == null)
        {
            return false;
        }

        for (int i = 0; i < activeTransitions.Count; i++)
        {
            if (activeTransitions[i].Box == box)
            {
                return !activeTransitions[i].IsGravityDriven;
            }
        }

        return false;
    }

    /// <summary>指定 WorldBox 是否有任何活跃的 entering 或 exiting transition。</summary>
    public bool HasActiveTransitionForWorldBox(WorldBox worldBox)
    {
        for (int i = 0; i < activeTransitions.Count; i++)
        {
            if (activeTransitions[i].WorldBox == worldBox)
            {
                return true;
            }
        }

        return false;
    }

    public void UpdateTransitions(WorldBox worldBox)
    {
        if (worldBox == null)
        {
            return;
        }

        for (int i = activeTransitions.Count - 1; i >= 0; i--)
        {
            var state = activeTransitions[i];
            if (state.WorldBox == worldBox)
            {
                UpdateBoxTransition(state);
            }
        }
    }

    private Vector3 GetAxis(BoxPushDirection direction)
    {
        switch (direction)
        {
            case BoxPushDirection.Right: return Vector3.right;
            case BoxPushDirection.Left: return Vector3.left;
            case BoxPushDirection.Up: return Vector3.up;
            case BoxPushDirection.Down: return Vector3.down;
            default: return Vector3.zero;
        }
    }

    private float GetCellSize(StandardBox box, BoxPushDirection direction)
    {
        if (box.Grid != null)
        {
            return Mathf.Abs(direction == BoxPushDirection.Left || direction == BoxPushDirection.Right
                ? box.Grid.cellSize.x
                : box.Grid.cellSize.y);
        }
        return 1f;
    }

    public void TryTriggerEntering(WorldBox worldBox)
    {
        if (worldBox == null) return;
        if (!ServiceBase.TryGet(out PhysicalBoxService physicalBoxService)) return;

        Bounds entryBounds = worldBox.Bounds;
        if (entryBounds.size == Vector3.zero) return;

        // We used to limit hasActiveEntering here to prevent overlap,
        // but that caused subsequent boxes in a continuous push to physically enter the portal without transitioning, getting permanently stuck.
        // Now, we allow multiple entering transitions and resolve overlaps using the phantom blocker / innerChain logic.

        Vector2 center = entryBounds.center;
        Vector2 size = entryBounds.size;
        size.x += OuterEdgeBlockerTouchTolerance * 2f;
        size.y += OuterEdgeBlockerTouchTolerance * 2f;

        Collider2D[] hits = Physics2D.OverlapBoxAll(center, size, 0f);
        if (TryTriggerTopAutoEntering(worldBox, entryBounds, hits))
        {
            return;
        }

        foreach (Collider2D hit in hits)
        {
            StandardBox box = hit.GetComponentInParent<StandardBox>();
            if (box == null || box == worldBox || box.gameObject.scene != worldBox.gameObject.scene) continue;
            if (IsBoxTransitioning(box)) continue;

            Bounds boxBounds = box.Bounds;
            if (boxBounds.size == Vector3.zero) continue;

            if (physicalBoxService.TryGetActiveLinearPushInfo(box, out BoxPushDirection pushDir, out Vector3 pushOrigin))
            {
                if (IsTouchingOuterBoundsForEntering(entryBounds, boxBounds, pushDir, OuterEdgeBlockerTouchTolerance))
                {
                    Vector3 axis = GetAxis(pushDir);
                    float cellSize = GetCellSize(box, pushDir);
                    Vector3 nextCenter = pushOrigin + axis * cellSize;

                    if (!IsInnerDestinationBlocked(box, worldBox, pushDir, entryBounds, boxBounds, null))
                    {
                        StartTransition(box, worldBox, pushDir, isEntering: true, pushOrigin, nextCenter, cellSize);
                        break;
                    }
                    else
                    {
                        // We only spawn the temporary wall if the WorldBox cannot be physically pushed.
                        // PhysicalBoxService.IsChainCandidate automatically allows pushing on entrance faces,
                        // so we just need to ensure the WorldBox is active and not frozen horizontally.
                        bool isFrozen = worldBox.FreezeHorizontalMovement && (pushDir == BoxPushDirection.Left || pushDir == BoxPushDirection.Right);
                        if (!worldBox.IsSceneMovableActive || isFrozen)
                        {
                            if (!TryRefreshOuterContactExitBlocker(entryBounds, worldBox, box, boxBounds))
                            {
                                ClearBoxExitBlocker(worldBox, box);
                            }
                        }
                        else
                        {
                            ClearBoxExitBlocker(worldBox, box);
                        }
                    }
                }
            }
        }
    }

    private bool TryTriggerTopAutoEntering(WorldBox worldBox, Bounds entryBounds, Collider2D[] hits)
    {
        if (worldBox.CanPushFrom(BoxPushDirection.Up) ||
            worldBox.HasOuterEntrancePlatform(BoxPushDirection.Up) ||
            worldBox.GetOuterEntrance(BoxPushDirection.Up) == null ||
            hits == null)
        {
            return false;
        }

        if (!ServiceBase.TryGet(out PhysicalBoxService pbService))
        {
            return false;
        }

        for (int i = 0; i < hits.Length; i++)
        {
            Collider2D hit = hits[i];
            if (hit == null || hit.isTrigger)
            {
                continue;
            }

            StandardBox box = hit.GetComponentInParent<StandardBox>();
            if (box == null ||
                box == worldBox ||
                box is WorldBox ||
                box.gameObject.scene != worldBox.gameObject.scene ||
                IsBoxTransitioning(box))
            {
                continue;
            }

            // Only trigger when the box is actively falling (has positive fallSpeed)
            if (!pbService.TryGetFallSpeed(box, out float fallSpeed) || fallSpeed <= 0f)
            {
                continue;
            }

            Bounds boxBounds = box.Bounds;
            if (boxBounds.size == Vector3.zero ||
                !IsTouchingTopEntrance(entryBounds, boxBounds, worldBox.TopEntranceCenterXTolerance))
            {
                continue;
            }

            if (TryStartTopGravityEntering(box, worldBox, entryBounds, boxBounds, fallSpeed))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryStartTopGravityEntering(StandardBox box, WorldBox worldBox, Bounds entryBounds, Bounds boxBounds, float fallSpeed)
    {
        Transform entrance = worldBox.GetOuterEntrance(BoxPushDirection.Up);
        if (entrance == null)
        {
            return false;
        }

        Vector3 targetPosition = GetSnappedEntrancePosition(box, entrance.position);
        if (IsTransitionTargetOccupied(box, targetPosition))
        {
            return false;
        }

        Bounds targetBounds = boxBounds;
        targetBounds.center = targetPosition;

        Bounds movableTargetBounds = targetBounds;
        movableTargetBounds.Expand(-0.05f);

        WorldBoxExitBlockerService blockerService = ServiceBase.Get<WorldBoxExitBlockerService>();
        if (blockerService != null)
        {
            bool use2D = box.Collider2D != null;
            bool use3D = box.Collider3D != null;

            if (blockerService.TryRefreshBlockerForStaticInnerHit(
                    worldBox,
                    box,
                    BoxPushDirection.Down,
                    entryBounds,
                    targetBounds,
                    boxBounds,
                    box.CollisionMask,
                    use2D,
                    use3D))
            {
                return false;
            }

            blockerService.Clear(worldBox, box);

            if (blockerService.TryGetTeleportTargetStandardBoxBlocker(
                    movableTargetBounds,
                    worldBox,
                    ignoredBox: box,
                    box.CollisionMask,
                    use2D,
                    use3D,
                    checkingInner: true,
                    out _))
            {
                return false;
            }
        }

        // Use a position-driven transition (like horizontal push) instead of instant teleport.
        // Gravity continues to act as the "pusher" — the clone mirrors the box's falling movement.
        float cellSize = GetCellSize(box, BoxPushDirection.Down);
        Vector3 pushOrigin = box.transform.position;
        Vector3 nextCenter = GetSnappedEntrancePosition(box, pushOrigin + Vector3.down * cellSize);

        UnityEngine.Debug.Log($"[PushableBoxService] TopGravityEntering: Box '{box.name}' falling into WorldBox '{worldBox.name}' from top with gravity-driven animation. FallSpeed: {fallSpeed}, TargetPos: {targetPosition}");
        StartTransition(box, worldBox, BoxPushDirection.Down, isEntering: true,
                        pushOrigin, nextCenter, cellSize, overrideSpeed: -1f, preservedFallSpeed: fallSpeed, isGravityDriven: true);
        return true;
    }

    private bool IsTransitionTargetOccupied(StandardBox box, Vector3 targetPosition)
    {
        for (int i = 0; i < activeTransitions.Count; i++)
        {
            var transition = activeTransitions[i];
            if (transition.Box != box &&
                Vector3.Distance(transition.P_target_end, targetPosition) < 0.1f)
            {
                return true;
            }
        }

        return false;
    }

    public void TryTriggerExiting(WorldBox worldBox, Bounds outerBounds)
    {
        if (worldBox == null || outerBounds.size == Vector3.zero) return;
        if (!ServiceBase.TryGet(out PhysicalBoxService physicalBoxService)) return;

        Vector2 center = outerBounds.center;
        Vector2 size = outerBounds.size;
        size.x += OuterEdgeBlockerTouchTolerance * 2f;
        size.y += OuterEdgeBlockerTouchTolerance * 2f;

        Collider2D[] hits = Physics2D.OverlapBoxAll(center, size, 0f);
        foreach (Collider2D hit in hits)
        {
            StandardBox box = hit.GetComponentInParent<StandardBox>();
            if (box == null || box == worldBox || box.gameObject.scene != worldBox.gameObject.scene) continue;
            if (IsBoxTransitioning(box)) continue;

            Bounds boxBounds = box.Bounds;
            if (boxBounds.size == Vector3.zero) continue;

            if (physicalBoxService.TryGetActiveLinearPushInfo(box, out BoxPushDirection pushDir, out Vector3 pushOrigin))
            {
                if (IsTouchingOuterBoundsForExiting(outerBounds, boxBounds, pushDir, OuterEdgeBlockerTouchTolerance))
                {
                    Vector3 axis = GetAxis(pushDir);
                    float cellSize = GetCellSize(box, pushDir);
                    Vector3 nextCenter = pushOrigin + axis * cellSize;

                    Bounds nextBounds = boxBounds;
                    nextBounds.center = nextCenter;

                    bool crossesOutside = false;
                    switch (pushDir)
                    {
                        case BoxPushDirection.Right:
                            crossesOutside = nextBounds.max.x > outerBounds.max.x + OuterEdgeBlockerTouchTolerance;
                            break;
                        case BoxPushDirection.Left:
                            crossesOutside = nextBounds.min.x < outerBounds.min.x - OuterEdgeBlockerTouchTolerance;
                            break;
                        case BoxPushDirection.Up:
                            crossesOutside = nextBounds.max.y > outerBounds.max.y + OuterEdgeBlockerTouchTolerance;
                            break;
                        case BoxPushDirection.Down:
                            crossesOutside = nextBounds.min.y < outerBounds.min.y - OuterEdgeBlockerTouchTolerance;
                            break;
                    }

                    if (crossesOutside)
                    {
                        if (!IsOuterDestinationBlocked(box, worldBox, pushDir, null))
                        {
                            StartTransition(box, worldBox, pushDir, isEntering: false, pushOrigin, nextCenter, cellSize);
                            break;
                        }
                        else
                        {
                            if (!TryRefreshInnerContactExitBlocker(outerBounds, worldBox, box, boxBounds, pushDir))
                            {
                                ClearBoxExitBlocker(worldBox, box);
                            }
                        }
                    }
                }
            }
        }
    }

    public void TryTriggerGravityTransitions(WorldBox worldBox, Bounds outerBounds)
    {
        if (worldBox == null) return;
        if (!ServiceBase.TryGet(out PhysicalBoxService pbService)) return;

        foreach (StandardBox box in RegisteredComponents)
        {
            if (box == null || box == worldBox || box.gameObject.scene != worldBox.gameObject.scene) continue;
            
            if (IsBoxTransitioning(box)) continue;

            if (!pbService.TryGetFallSpeed(box, out float fallSpeed) || fallSpeed <= 0f) continue;

            Bounds boxBounds = box.Bounds;
            if (boxBounds.size == Vector3.zero) continue;

            // Box is falling out of the bottom of outerBounds
            if (boxBounds.min.y <= outerBounds.min.y + OuterEdgeBlockerTouchTolerance &&
                boxBounds.min.x < outerBounds.max.x &&
                boxBounds.max.x > outerBounds.min.x)
            {
                if (IsOuterDestinationBlocked(box, worldBox, BoxPushDirection.Down, null))
                {
                    continue; 
                }
                
                float cellSize = GetCellSize(box, BoxPushDirection.Down);
                Vector3 axis = Vector3.down;
                Vector3 pushOrigin = box.transform.position; 
                Vector3 nextCenter = GetSnappedEntrancePosition(box, pushOrigin + axis * cellSize);

                StartTransition(box, worldBox, BoxPushDirection.Down, isEntering: false, 
                                pushOrigin, nextCenter, cellSize, overrideSpeed: -1f, preservedFallSpeed: fallSpeed, isGravityDriven: true);
            }
        }
    }

    public static bool IsTouchingOuterBoundsForEntering(Bounds outerBounds, Bounds boxBounds,
        BoxPushDirection direction, float threshold)
    {
        switch (direction)
        {
            case BoxPushDirection.Right:
                return Mathf.Abs(boxBounds.max.x - outerBounds.min.x) <= threshold &&
                       boxBounds.min.y < outerBounds.max.y &&
                       boxBounds.max.y > outerBounds.min.y;
            case BoxPushDirection.Left:
                return Mathf.Abs(boxBounds.min.x - outerBounds.max.x) <= threshold &&
                       boxBounds.min.y < outerBounds.max.y &&
                       boxBounds.max.y > outerBounds.min.y;
            case BoxPushDirection.Up:
                return Mathf.Abs(boxBounds.max.y - outerBounds.min.y) <= threshold &&
                       boxBounds.min.x < outerBounds.max.x &&
                       boxBounds.max.x > outerBounds.min.x;
            case BoxPushDirection.Down:
                return Mathf.Abs(boxBounds.min.y - outerBounds.max.y) <= threshold &&
                       boxBounds.min.x < outerBounds.max.x &&
                       boxBounds.max.x > outerBounds.min.x;
            default:
                return false;
        }
    }

    public static bool IsTouchingOuterBoundsForExiting(Bounds outerBounds, Bounds boxBounds, BoxPushDirection direction,
        float threshold)
    {
        switch (direction)
        {
            case BoxPushDirection.Right:
                return boxBounds.max.x >= outerBounds.max.x - threshold &&
                       boxBounds.min.y < outerBounds.max.y &&
                       boxBounds.max.y > outerBounds.min.y;
            case BoxPushDirection.Left:
                return boxBounds.min.x <= outerBounds.min.x + threshold &&
                       boxBounds.min.y < outerBounds.max.y &&
                       boxBounds.max.y > outerBounds.min.y;
            case BoxPushDirection.Up:
                return boxBounds.max.y >= outerBounds.max.y - threshold &&
                       boxBounds.min.x < outerBounds.max.x &&
                       boxBounds.max.x > outerBounds.min.x;
            case BoxPushDirection.Down:
                return boxBounds.min.y <= outerBounds.min.y + threshold &&
                       boxBounds.min.x < outerBounds.max.x &&
                       boxBounds.max.x > outerBounds.min.x;
            default:
                return false;
        }
    }


    public bool IsInnerDestinationBlocked(StandardBox box, WorldBox worldBox, BoxPushDirection direction,
        Bounds outerBounds, Bounds boxBounds, System.Collections.Generic.HashSet<StandardBox> visited = null)
    {
        if (visited == null) visited = new System.Collections.Generic.HashSet<StandardBox>();
        if (!visited.Add(box))
        {
            return false; // Loop detected, not blocked by itself
        }

        Transform entrance = worldBox.GetOuterEntrance(direction.Opposite());
        if (entrance == null)
        {
            return true;
        }

        Vector3 targetPosition = entrance.position;
        if (box.AlignToGrid && box.Grid != null)
        {
            Grid grid = box.Grid;
            Vector3Int cell = grid.WorldToCell(targetPosition);
            Vector3 snapped = grid.CellToWorld(cell) + Vector3.Scale(grid.cellSize, box.CellOffset);
            snapped.z = targetPosition.z;
            targetPosition = snapped;
        }

        Bounds targetBounds = boxBounds;
        targetBounds.center = targetPosition;

        Bounds movableTargetBounds = targetBounds;
        movableTargetBounds.Expand(-0.05f);

        WorldBoxExitBlockerService blockerService = ServiceBase.Get<WorldBoxExitBlockerService>();
        if (blockerService == null)
        {
            return false;
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
            return true;
        }

        blockerService.Clear(worldBox, box);

        var innerChain = new System.Collections.Generic.List<StandardBox>();
        var logicalPositions = new System.Collections.Generic.List<Vector3>();
        BoxTransitionState phantomTransition = null;

        for (int i = 0; i < activeTransitions.Count; i++)
        {
            var otherTransition = activeTransitions[i];
            if (otherTransition.Box != box && 
                Vector3.Distance(otherTransition.P_target_end, movableTargetBounds.center) < 0.1f)
            {
                phantomTransition = otherTransition;
                break;
            }
        }

        Vector3 axis = direction == BoxPushDirection.Right ? Vector3.right :
            direction == BoxPushDirection.Left ? Vector3.left :
            direction == BoxPushDirection.Up ? Vector3.up : Vector3.down;

        if (!ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            return true;
        }

        if (phantomTransition != null)
        {
            innerChain.Add(phantomTransition.Box);
            logicalPositions.Add(phantomTransition.P_target_end);
            
            for (int i = 0; i < phantomTransition.InnerChain.Count; i++)
            {
                StandardBox phantomMember = phantomTransition.InnerChain[i];
                innerChain.Add(phantomMember);
                
                Vector3 finalPos = phantomTransition.InnerChainStartPositions[i] + axis * phantomTransition.Width;
                if (Mathf.Abs(axis.x) > 0.5f)
                {
                    finalPos.y = phantomMember.transform.position.y;
                    finalPos.z = phantomMember.transform.position.z;
                }
                else if (Mathf.Abs(axis.y) > 0.5f)
                {
                    finalPos.x = phantomMember.transform.position.x;
                    finalPos.z = phantomMember.transform.position.z;
                }
                logicalPositions.Add(finalPos);
            }
        }
        else
        {
            // 2. Find the immediate physical blocker at the entrance inside the WorldBox
            if (!blockerService.TryGetTeleportTargetStandardBoxBlocker(
                    movableTargetBounds,
                    worldBox,
                    ignoredBox: box,
                    box.CollisionMask,
                    use2D,
                    use3D,
                    out StandardBox blocker))
            {
                return false;
            }

            if (physicalBoxService.IsFalling(blocker) || !physicalBoxService.IsGrounded(blocker))
            {
                return false;
            }
            
            physicalBoxService.CollectHorizontalChain(blocker, axis, innerChain);
            for (int i = 0; i < innerChain.Count; i++)
            {
                logicalPositions.Add(innerChain[i].transform.position);
            }
        }

        // 扩展忽略列表：合并 innerChain 与所有 WorldBox 内部的 StandardBox
        var extendedIgnore = new System.Collections.Generic.List<StandardBox>();
        extendedIgnore.AddRange(innerChain);
        foreach (StandardBox registered in this.components)
        {
            if (registered != null && !(registered is WorldBox))
            {
                if (!extendedIgnore.Contains(registered))
                    extendedIgnore.Add(registered);
            }
        }

        // Check if any member in the inner chain is blocked
        foreach (StandardBox member in innerChain)
        {
            if (member == null) continue;

            float memberCellSize = 1f;
            if (member.Grid != null)
                memberCellSize = Mathf.Abs(direction == BoxPushDirection.Left || direction == BoxPushDirection.Right
                    ? member.Grid.cellSize.x
                    : member.Grid.cellSize.y);

            Vector3 logicalPos = logicalPositions[innerChain.IndexOf(member)];
            if (physicalBoxService.CastSingle(member, logicalPos, axis, memberCellSize, out _,
                    extendedIgnore))
            {
                return true;
            }

            // If the member is at the exit boundary, check if its destination outside is blocked
            if (IsTouchingOuterBoundsForExiting(worldBox.OuterBounds, member.Bounds, direction,
                    OuterEdgeBlockerTouchTolerance))
            {
                if (IsOuterDestinationBlocked(member, worldBox, direction, visited))
                {
                    return true; // Blocked!
                }
            }
        }

        return false; // Not blocked!
    }

    private Vector3 GetOuterTargetPosition(StandardBox box, WorldBox worldBox, BoxPushDirection direction)
    {
        Vector3 axis = Vector3.zero;
        switch (direction)
        {
            case BoxPushDirection.Left: axis = Vector3.left; break;
            case BoxPushDirection.Right: axis = Vector3.right; break;
            case BoxPushDirection.Up: axis = Vector3.up; break;
            case BoxPushDirection.Down: axis = Vector3.down; break;
        }

        Vector3 overworldPos = worldBox.transform.position;
        if (box.AlignToGrid && box.Grid != null)
        {
            Grid grid = box.Grid;
            Vector3Int cell = grid.WorldToCell(overworldPos);
            Vector3 snapped = grid.CellToWorld(cell) + Vector3.Scale(grid.cellSize, box.CellOffset);
            snapped.z = overworldPos.z;
            overworldPos = snapped;
        }

        float cellSize = GetCellSize(box, direction);
        return overworldPos + axis * cellSize;
    }

    private Vector3 GetSnappedEntrancePosition(StandardBox box, Vector3 targetPosition)
    {
        if (box.AlignToGrid && box.Grid != null)
        {
            Grid grid = box.Grid;
            Vector3Int cell = grid.WorldToCell(targetPosition);
            Vector3 snapped = grid.CellToWorld(cell) + Vector3.Scale(grid.cellSize, box.CellOffset);
            snapped.z = targetPosition.z;
            return snapped;
        }

        return targetPosition;
    }

    private bool IsOuterDestinationBlocked(StandardBox box, WorldBox worldBox, BoxPushDirection direction, System.Collections.Generic.HashSet<StandardBox> visited)
    {
        if (visited == null) visited = new System.Collections.Generic.HashSet<StandardBox>();
        if (!visited.Add(box))
        {
            return false; // Loop detected, not blocked by itself
        }

        Transform entrance = worldBox.GetOuterEntrance(direction);
        if (entrance == null)
        {
            return true;
        }

        Vector3 targetPosition = GetOuterTargetPosition(box, worldBox, direction);

        Bounds boxBounds = box.Bounds;
        Bounds targetBounds = boxBounds;
        targetBounds.center = targetPosition;

        Bounds movableTargetBounds = targetBounds;
        movableTargetBounds.Expand(-0.05f);

        WorldBoxExitBlockerService blockerService = ServiceBase.Get<WorldBoxExitBlockerService>();
        if (blockerService == null)
        {
            return false;
        }

        bool use2D = box.Collider2D != null;
        bool use3D = box.Collider3D != null;

        if (blockerService.QueryInnerExitStaticallyBlocked(
                worldBox, direction, worldBox.InnerBounds, targetBounds, boxBounds, box.CollisionMask, use2D, use3D))
        {
            return true;
        }

        // 1. Check if the destination is occupied by a phantom transition (visual clone)
        var outerChain = new System.Collections.Generic.List<StandardBox>();
        var logicalPositions = new System.Collections.Generic.List<Vector3>();
        BoxTransitionState phantomTransition = null;

        for (int i = 0; i < activeTransitions.Count; i++)
        {
            var otherTransition = activeTransitions[i];
            if (otherTransition.Box != box && 
                Vector3.Distance(otherTransition.P_target_end, movableTargetBounds.center) < 0.1f)
            {
                phantomTransition = otherTransition;
                break;
            }
        }

        Vector3 axis = direction == BoxPushDirection.Right ? Vector3.right :
            direction == BoxPushDirection.Left ? Vector3.left :
            direction == BoxPushDirection.Up ? Vector3.up : Vector3.down;

        if (!ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            return true;
        }

        if (phantomTransition != null)
        {
            outerChain.Add(phantomTransition.Box);
            logicalPositions.Add(phantomTransition.P_target_end);
            
            for (int i = 0; i < phantomTransition.InnerChain.Count; i++)
            {
                StandardBox phantomMember = phantomTransition.InnerChain[i];
                outerChain.Add(phantomMember);
                
                Vector3 finalPos = phantomTransition.InnerChainStartPositions[i] + axis * phantomTransition.Width;
                if (Mathf.Abs(axis.x) > 0.5f)
                {
                    finalPos.y = phantomMember.transform.position.y;
                    finalPos.z = phantomMember.transform.position.z;
                }
                else if (Mathf.Abs(axis.y) > 0.5f)
                {
                    finalPos.x = phantomMember.transform.position.x;
                    finalPos.z = phantomMember.transform.position.z;
                }
                logicalPositions.Add(finalPos);
            }
        }
        else
        {
            // 2. Find the immediate physical blocker
            if (!blockerService.TryGetTeleportTargetStandardBoxBlocker(
                    movableTargetBounds, worldBox, ignoredBox: box, worldBox.CollisionMask, use2D, use3D, checkingInner: false, out StandardBox blocker))
            {
                return false;
            }

            if (physicalBoxService.IsFalling(blocker) || !physicalBoxService.IsGrounded(blocker))
            {
                return false;
            }

            physicalBoxService.CollectHorizontalChain(blocker, axis, outerChain);
            for (int i = 0; i < outerChain.Count; i++)
            {
                logicalPositions.Add(outerChain[i].transform.position);
            }
        }

        var extendedIgnore = new System.Collections.Generic.List<StandardBox>();
        extendedIgnore.AddRange(outerChain);

        foreach (StandardBox member in outerChain)
        {
            if (member == null) continue;

            float memberCellSize = 1f;
            if (member.Grid != null)
                memberCellSize = Mathf.Abs(direction == BoxPushDirection.Left || direction == BoxPushDirection.Right
                    ? member.Grid.cellSize.x
                    : member.Grid.cellSize.y);

            Vector3 logicalPos = logicalPositions[outerChain.IndexOf(member)];
            if (physicalBoxService.CastSingle(member, logicalPos, axis, memberCellSize, out _, extendedIgnore))
            {
                return true;
            }

            // If the member is at the entrance boundary, check if its destination inside is blocked
            if (IsTouchingOuterBoundsForEntering(worldBox.OuterBounds, member.Bounds, direction,
                    OuterEdgeBlockerTouchTolerance))
            {
                if (IsInnerDestinationBlocked(member, worldBox, direction, worldBox.OuterBounds, member.Bounds, visited))
                {
                    return true; // Blocked!
                }
            }
        }

        return false;
    }

    private void StartTransition(StandardBox box, WorldBox worldBox, BoxPushDirection direction, bool isEntering,
        Vector3 startPos, Vector3 endPos, float cellSize, float overrideSpeed = -1f, float preservedFallSpeed = 0f, bool isGravityDriven = false)
    {
        BoxTransitionState phantomTransition = null;
        System.Collections.Generic.List<StandardBox> innerChain = null;
        Bounds boxBounds = box.Bounds;
        Vector3 P_start = startPos;
        Vector3 P_end = endPos;
        Vector3 P_target_end = Vector3.zero;
        Vector3 P_target_start = Vector3.zero;
        Vector3 axis = Vector3.zero;

        switch (direction)
        {
            case BoxPushDirection.Right:
                axis = Vector3.right;
                break;
            case BoxPushDirection.Left:
                axis = Vector3.left;
                break;
            case BoxPushDirection.Up:
                axis = Vector3.up;
                break;
            case BoxPushDirection.Down:
                axis = Vector3.down;
                break;
        }
        if (isEntering)
        {
            Transform entrance = worldBox.GetOuterEntrance(direction.Opposite());
            if (entrance == null)
            {
                return;
            }

            P_target_end = entrance.position;
            if (box.AlignToGrid && box.Grid != null)
            {
                Grid grid = box.Grid;
                Vector3Int cell = grid.WorldToCell(P_target_end);
                Vector3 snapped = grid.CellToWorld(cell) + Vector3.Scale(grid.cellSize, box.CellOffset);
                snapped.z = P_target_end.z;
                P_target_end = snapped;
            }

            P_target_start = P_target_end - axis * cellSize;

            Bounds targetBounds = boxBounds;
            targetBounds.center = P_target_end;

            Bounds movableTargetBounds = targetBounds;
            movableTargetBounds.Expand(-0.05f);

            WorldBoxExitBlockerService blockerService = ServiceBase.Get<WorldBoxExitBlockerService>();
            if (blockerService != null)
            {
                bool use2D = box.Collider2D != null;
                bool use3D = box.Collider3D != null;
                // 1. Find and force-complete any phantom transition targeting the same inner entrance.
                phantomTransition = null;
                for (int i = 0; i < activeTransitions.Count; i++)
                {
                    var otherTransition = activeTransitions[i];
                    if (otherTransition.Box != box && 
                        Vector3.Distance(otherTransition.P_target_end, P_target_end) < 0.1f)
                    {
                        phantomTransition = otherTransition;
                        break;
                    }
                }

                if (phantomTransition != null)
                {
                    CompleteTransition(phantomTransition);
                    Physics2D.SyncTransforms();
                    Physics.SyncTransforms();
                }

                // 2. Query the physical world for blockers at the destination.
                if (blockerService.TryGetTeleportTargetStandardBoxBlocker(
                        movableTargetBounds,
                        worldBox,
                        ignoredBox: null,
                        box.CollisionMask,
                        use2D,
                        use3D,
                        checkingInner: true,
                        out StandardBox blocker))
                {
                    if (ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
                    {
                        if (!(physicalBoxService.IsFalling(blocker) || !physicalBoxService.IsGrounded(blocker)))
                        {
                            innerChain = new System.Collections.Generic.List<StandardBox>();
                            physicalBoxService.CollectHorizontalChain(blocker, axis, innerChain);
                            if (!innerChain.Contains(blocker))
                            {
                                innerChain.Add(blocker);
                            }

                            for (int i = 0; i < innerChain.Count; i++)
                            {
                                if (!physicalBoxService.IsGrounded(innerChain[i]))
                                {
                                    innerChain.RemoveRange(i, innerChain.Count - i);
                                    break;
                                }
                            }
                            UnityEngine.Debug.Log($"[PushableBoxService] Entering box '{box.name}' teleporting to {P_target_end}. Found blocker '{blocker.name}'. Collected chain of size {innerChain.Count}.");
                        }
                        else
                        {
                            UnityEngine.Debug.Log($"[PushableBoxService] Entering box '{box.name}' found blocker '{blocker.name}' at {P_target_end}, but it is falling or suspended. Did not collect chain.");
                        }
                    }
                }
                else
                {
                    UnityEngine.Debug.Log($"[PushableBoxService] Entering box '{box.name}' teleporting to {P_target_end}. No blocker found at destination.");
                }
            }
        }
        else
        {
            P_target_end = GetOuterTargetPosition(box, worldBox, direction);
            P_target_start = P_target_end - axis * cellSize;
            
            Bounds targetBounds = boxBounds;
            targetBounds.center = P_target_end;

            Bounds movableTargetBounds = targetBounds;
            movableTargetBounds.Expand(-0.05f);

            WorldBoxExitBlockerService blockerService = ServiceBase.Get<WorldBoxExitBlockerService>();
            if (blockerService != null)
            {
                bool use2D = box.Collider2D != null;
                bool use3D = box.Collider3D != null;

                // 1. Find and force-complete any phantom transition that is targeting our destination.
                // This ensures their physical bodies are actually at the destination so they can be picked up as blockers.
                BoxTransitionState pt = null;
                for (int i = 0; i < activeTransitions.Count; i++)
                {
                    var otherTransition = activeTransitions[i];
                    if (otherTransition.Box != box && 
                        Vector3.Distance(otherTransition.P_target_end, P_target_end) < 0.1f)
                    {
                        pt = otherTransition;
                        break;
                    }
                }

                if (pt != null)
                {
                    CompleteTransition(pt);
                    Physics2D.SyncTransforms();
                    Physics.SyncTransforms();
                }

                // 2. Now query the physics world. The phantom transition (if any) is now a real physical blocker.
                StandardBox blocker = null;
                if (blockerService.TryGetTeleportTargetStandardBoxBlocker(
                        movableTargetBounds,
                        worldBox,
                        ignoredBox: null,
                        worldBox.CollisionMask,
                        use2D,
                        use3D,
                        checkingInner: false,
                        out StandardBox physicalBlocker))
                {
                    blocker = physicalBlocker;
                }

                // 3. Collect the chain.
                if (blocker != null)
                {
                    if (ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
                    {
                        if (!(physicalBoxService.IsFalling(blocker) || !physicalBoxService.IsGrounded(blocker)))
                        {
                            innerChain = new System.Collections.Generic.List<StandardBox>();
                            physicalBoxService.CollectHorizontalChain(blocker, axis, innerChain);
                            if (!innerChain.Contains(blocker))
                            {
                                innerChain.Add(blocker);
                            }

                            for (int i = 0; i < innerChain.Count; i++)
                            {
                                if (!physicalBoxService.IsGrounded(innerChain[i]))
                                {
                                    innerChain.RemoveRange(i, innerChain.Count - i);
                                    break;
                                }
                            }
                            UnityEngine.Debug.Log($"[PushableBoxService] Exiting box '{box.name}' teleporting to {P_target_end}. Found blocker '{blocker.name}'. Collected chain of size {innerChain.Count}.");
                        }
                        else
                        {
                            UnityEngine.Debug.Log($"[PushableBoxService] Exiting box '{box.name}' found blocker '{blocker.name}' at {P_target_end}, but it is falling or suspended. Did not collect chain.");
                        }
                    }
                }
                else
                {
                    UnityEngine.Debug.Log($"[PushableBoxService] Exiting box '{box.name}' teleporting to {P_target_end}. No blocker found at destination.");
                }
            }
        }

        SpriteRenderer origRenderer = box.GetComponentInChildren<SpriteRenderer>();
        if (origRenderer == null)
        {
            return;
        }

        GameObject clone = new GameObject("BoxVisualClone");
        SpriteRenderer cloneRenderer = clone.AddComponent<SpriteRenderer>();

        cloneRenderer.sprite = origRenderer.sprite;
        cloneRenderer.color = origRenderer.color;
        cloneRenderer.material = origRenderer.material;
        cloneRenderer.sortingLayerID = origRenderer.sortingLayerID;
        cloneRenderer.sortingOrder = origRenderer.sortingOrder;
        cloneRenderer.flipX = origRenderer.flipX;
        cloneRenderer.flipY = origRenderer.flipY;

        clone.transform.localScale = origRenderer.transform.lossyScale;
        clone.transform.rotation = origRenderer.transform.rotation;
        clone.transform.position = P_target_start;

        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, box.gameObject.scene);

        BoxTransitionState state = new BoxTransitionState
        {
            Box = box,
            WorldBox = worldBox,
            Direction = direction,
            IsEntering = isEntering,
            VisualClone = clone,
            CloneRenderer = cloneRenderer,
            P_start = P_start,
            P_end = P_end,
            P_target_start = P_target_start,
            P_target_end = P_target_end,
            Width = cellSize,
            OriginalRenderer = origRenderer,
            StartCell = box.AlignToGrid && box.Grid != null ? box.Grid.WorldToCell(P_start) : default,
            OverrideSpeed = overrideSpeed,
            PreservedFallSpeed = preservedFallSpeed,
            IsGravityDriven = isGravityDriven
        };

        if (innerChain != null)
        {
            state.InnerChain.AddRange(innerChain);
            for (int i = 0; i < innerChain.Count; i++)
            {
                StandardBox member = innerChain[i];
                state.InnerChainStartPositions.Add(member.transform.position);

                if (ServiceBase.TryGet(out PhysicalBoxService pbService))
                {
                    pbService.RegisterFollowerState(member, direction);
                }
            }
        }

        UnityEngine.Debug.Log($"[PushableBoxService] StartTransition: Box '{box.name}' {(isEntering ? "entering" : "exiting")} WorldBox '{worldBox.name}' from direction {direction}. StartPos: {P_start}, TargetPos: {P_target_end}");
        activeTransitions.Add(state);

        ClearBoxExitBlocker(worldBox, box);
    }

    private void UpdateBoxTransition(BoxTransitionState state)
    {
        if (state.Box == null || state.WorldBox == null)
        {
            CancelTransition(state);
            return;
        }

        if (state.IsGravityDriven)
        {
            if (ServiceBase.TryGet(out PhysicalBoxService pbService))
            {
                if (pbService.TryGetFallSpeed(state.Box, out float currentFallSpeed))
                {
                    if (currentFallSpeed > state.PreservedFallSpeed)
                    {
                        state.PreservedFallSpeed = currentFallSpeed;
                    }
                }
            }
        }

        Vector3 totalVector = state.P_end - state.P_start;
        float totalDist = totalVector.magnitude;
        if (totalDist <= Mathf.Epsilon)
        {
            CancelTransition(state);
            return;
        }

        float progress = 0f;
        Vector3 currentPos = state.Box.transform.position;

        if (state.OverrideSpeed > 0f)
        {
            // Autonomous Gravity Transition: Progress is driven entirely by time
            state.Progress += (state.OverrideSpeed / totalDist) * Time.fixedDeltaTime;
            progress = Mathf.Clamp01(state.Progress);

            if (progress >= 0.99f)
            {
                CompleteTransition(state);
                return;
            }
        }
        else
        {
            // Linear Push Transition: Progress is driven by the physical box's movement
            Vector3 currentVector = currentPos - state.P_start;
            Vector3 totalDir = totalVector.normalized;
            float dot = Vector3.Dot(currentVector, totalDir);
            progress = Mathf.Clamp01(dot / totalDist);

            if (progress >= 0.99f)
            {
                CompleteTransition(state);
                return;
            }

            if (dot < -0.05f)
            {
                CancelTransition(state);
                return;
            }

            if (progress <= 0.05f && !state.IsGravityDriven)
            {
                if (ServiceBase.TryGet(out PhysicalBoxService pbService))
                {
                    if (!pbService.TryGetActiveLinearPushInfo(state.Box, out _, out _))
                    {
                        CancelTransition(state);
                        return;
                    }
                }
            }
            
            state.Progress = progress;
        }

        if (state.VisualClone != null)
        {
            state.VisualClone.transform.position = Vector3.Lerp(state.P_target_start, state.P_target_end, progress);
        }

        Vector3 axis = Vector3.zero;
        switch (state.Direction)
        {
            case BoxPushDirection.Right: axis = Vector3.right; break;
            case BoxPushDirection.Left: axis = Vector3.left; break;
            case BoxPushDirection.Up: axis = Vector3.up; break;
            case BoxPushDirection.Down: axis = Vector3.down; break;
        }

        for (int i = 0; i < state.InnerChain.Count; i++)
        {
            StandardBox member = state.InnerChain[i];
            if (member != null)
            {
                currentPos = member.transform.position;

                if (Mathf.Abs(axis.x) > 0.5f && state.InnerChainStartPositions[i].y - currentPos.y > 0.05f)
                {
                    for (int j = i; j < state.InnerChain.Count; j++)
                    {
                        StandardBox detached = state.InnerChain[j];
                        if (detached != null && ServiceBase.TryGet(out PhysicalBoxService pbService))
                        {
                            pbService.CancelLinearPush(detached);
                            pbService.QueueGridAlignmentRelease(detached);
                        }
                    }
                    state.InnerChain.RemoveRange(i, state.InnerChain.Count - i);
                    state.InnerChainStartPositions.RemoveRange(i, state.InnerChainStartPositions.Count - i);
                    break;
                }

                Vector3 targetPos = state.InnerChainStartPositions[i] + axis * (progress * state.Width);
                if (Mathf.Abs(axis.x) > 0.5f)
                {
                    targetPos.y = currentPos.y;
                    targetPos.z = currentPos.z;
                }
                else if (Mathf.Abs(axis.y) > 0.5f)
                {
                    targetPos.x = currentPos.x;
                    targetPos.z = currentPos.z;
                }
                member.MoveTo(targetPos);
            }
        }
    }

    private void CompleteTransition(BoxTransitionState state)
    {
        if (state.VisualClone != null)
        {
            Destroy(state.VisualClone);
        }

        activeTransitions.Remove(state);

        Vector3 axis = Vector3.zero;
        switch (state.Direction)
        {
            case BoxPushDirection.Right: axis = Vector3.right; break;
            case BoxPushDirection.Left: axis = Vector3.left; break;
            case BoxPushDirection.Up: axis = Vector3.up; break;
            case BoxPushDirection.Down: axis = Vector3.down; break;
        }

        for (int i = 0; i < state.InnerChain.Count; i++)
        {
            StandardBox member = state.InnerChain[i];
            if (member != null)
            {
                Vector3 currentPos = member.transform.position;
                Vector3 finalPos = state.InnerChainStartPositions[i] + axis * state.Width;
                if (Mathf.Abs(axis.x) > 0.5f)
                {
                    finalPos.y = currentPos.y;
                    finalPos.z = currentPos.z;
                }
                else if (Mathf.Abs(axis.y) > 0.5f)
                {
                    finalPos.x = currentPos.x;
                    finalPos.z = currentPos.z;
                }
                member.MoveTo(finalPos);
                if (ServiceBase.TryGet(out SceneMovableInteractionService sceneMovable))
                {
                    sceneMovable.RefreshItemImpactBaseline(member);
                }

                if (ServiceBase.TryGet(out PhysicalBoxService pbService))
                {
                    pbService.CancelLinearPush(member);
                    pbService.QueueGridAlignmentRelease(member);
                }
            }
        }

        if (state.Box != null && state.WorldBox != null)
        {
            UnityEngine.Debug.Log($"[PushableBoxService] CompleteTransition: Box '{state.Box.name}' finished {(state.IsEntering ? "entering" : "exiting")} WorldBox '{state.WorldBox.name}'. Teleporting to target: {state.P_target_end}. InnerChainCount: {state.InnerChain.Count}");
            if (state.IsEntering)
            {
                MoveBoxToInner(state.Box, state.P_target_end, state.WorldBox);
                TypeEventSystem.Global.Send<OnOuterToInnerEvent>();
            }
            else
            {
                MoveBoxToOuter(state.Box, state.P_target_end);
            }

            if (ServiceBase.TryGet(out PhysicalBoxService pbService))
            {
                if (state.PreservedFallSpeed > 0f)
                {
                    pbService.SetFallSpeed(state.Box, state.PreservedFallSpeed);
                }
            }
        }
    }

    private void CancelTransition(BoxTransitionState state)
    {
        if (state.VisualClone != null)
        {
            Destroy(state.VisualClone);
        }

        for (int i = 0; i < state.InnerChain.Count; i++)
        {
            StandardBox member = state.InnerChain[i];
            if (member != null)
            {
                Vector3 currentPos = member.transform.position;
                Vector3 cancelPos = state.InnerChainStartPositions[i];
                if (state.Direction == BoxPushDirection.Right || state.Direction == BoxPushDirection.Left)
                {
                    cancelPos.y = currentPos.y;
                    cancelPos.z = currentPos.z;
                }
                else if (state.Direction == BoxPushDirection.Up || state.Direction == BoxPushDirection.Down)
                {
                    cancelPos.x = currentPos.x;
                    cancelPos.z = currentPos.z;
                }
                member.MoveTo(cancelPos);
                
                if (ServiceBase.TryGet(out PhysicalBoxService pbService))
                {
                    pbService.CancelLinearPush(member);
                }
            }
        }

        activeTransitions.Remove(state);
    }

    private static void MoveBoxToOuter(StandardBox box, Vector3 targetPosition)
    {
        UnityEngine.Debug.Log($"[PushableBoxService] MoveBoxToOuter: Teleporting Box '{box.name}' to outer world target {targetPosition}");
        box.MoveTo(targetPosition);
        if (ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            physicalBoxService.CancelLinearPush(box);
            physicalBoxService.QueueGridAlignmentRelease(box);
        }

        Physics2D.SyncTransforms();
        Physics.SyncTransforms();

        if (ServiceBase.TryGet(out SceneMovableInteractionService sceneMovable))
        {
            sceneMovable.RefreshItemImpactBaseline(box);
        }

        TypeEventSystem.Global.Send<OnInnerToOuterEvent>();
    }


    private class BoxTransitionState
    {
        public StandardBox Box;
        public WorldBox WorldBox;
        public BoxPushDirection Direction;
        public bool IsEntering;
        public GameObject VisualClone;
        public SpriteRenderer CloneRenderer;

        public Vector3 P_start;
        public Vector3 P_end;
        public Vector3 P_target_start;
        public Vector3 P_target_end;
        public float Width;

        public SpriteRenderer OriginalRenderer;

        public readonly System.Collections.Generic.List<StandardBox> InnerChain =
            new System.Collections.Generic.List<StandardBox>();

        public readonly System.Collections.Generic.List<Vector3> InnerChainStartPositions =
            new System.Collections.Generic.List<Vector3>();

        // Entering 完成判定：box 的 grid cell 变化即认为已进入
        public Vector3Int StartCell;

        public float Progress = 0f;
        public float OverrideSpeed = -1f;
        public float PreservedFallSpeed = 0f;
        public bool IsGravityDriven = false;
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

        Transform entrance = worldBox.GetOuterEntrance(direction.Opposite());
        if (entrance == null)
        {
            return false;
        }

        Vector3 targetPosition = entrance.position;
        if (box.AlignToGrid && box.Grid != null)
        {
            Grid grid = box.Grid;
            Vector3Int cell = grid.WorldToCell(targetPosition);
            Vector3 snapped = grid.CellToWorld(cell) + Vector3.Scale(grid.cellSize, box.CellOffset);
            snapped.z = targetPosition.z;
            targetPosition = snapped;
        }

        Bounds targetBounds = boxBounds;
        targetBounds.center = targetPosition;

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

    private static bool TryRefreshInnerContactExitBlocker(
        Bounds outerBounds,
        WorldBox worldBox,
        StandardBox box,
        Bounds boxBounds,
        BoxPushDirection direction)
    {
        WorldBoxExitBlockerService blockerService = ServiceBase.Get<WorldBoxExitBlockerService>();
        if (blockerService == null)
        {
            return false;
        }

        bool use2D = box.Collider2D != null;
        bool use3D = box.Collider3D != null;
        return blockerService.TryRefreshBlockerForDynamicHit(
            worldBox,
            box,
            direction,
            outerBounds,
            boxBounds,
            use2D,
            use3D);
    }

    private static void MoveBoxToInner(StandardBox box, Vector3 targetPosition, WorldBox worldBox)
    {
        box.MoveTo(targetPosition);
        if (ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            physicalBoxService.CancelLinearPush(box);
            physicalBoxService.QueueGridAlignmentRelease(box);
        }

        Physics2D.SyncTransforms();
        Physics.SyncTransforms();

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

    private static bool IsTouchingTopEntrance(Bounds worldBoxBounds, Bounds boxBounds, float centerXTolerance)
    {
        if (worldBoxBounds.size == Vector3.zero || boxBounds.size == Vector3.zero)
        {
            return false;
        }

        if (centerXTolerance > 0f &&
            Mathf.Abs(boxBounds.center.x - worldBoxBounds.center.x) > centerXTolerance)
        {
            return false;
        }

        float gap = boxBounds.min.y - worldBoxBounds.max.y;
        return gap >= -OuterEdgeBlockerTouchTolerance &&
               gap <= OuterEdgeBlockerTouchTolerance &&
               boxBounds.min.x < worldBoxBounds.max.x &&
               boxBounds.max.x > worldBoxBounds.min.x;
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
}
