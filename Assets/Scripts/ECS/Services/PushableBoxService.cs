using QFramework;
using UnityEngine;

public class PushableBoxService : ServiceBase<StandardBox>
{
    private const int MaxTeleportTargetBlockerClearAttempts = 8;
    private const float OuterEdgeBlockerTouchTolerance = 0.04f;

    private IUnRegister pushRequestUnRegister;
    private readonly System.Collections.Generic.List<BoxTransitionState> activeTransitions = new System.Collections.Generic.List<BoxTransitionState>();

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

    private bool IsBoxTransitioning(StandardBox box)
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

    public void CheckAndTryTeleportAllPushableBoxesWithOuterBoundsToInner(Bounds outerBounds, WorldBox worldBox)
    {
        if (worldBox == null)
        {
            return;
        }

        // 1. Update existing transitions
        for (int i = activeTransitions.Count - 1; i >= 0; i--)
        {
            var state = activeTransitions[i];
            if (state.WorldBox == worldBox)
            {
                UpdateBoxTransition(state);
            }
        }

        // Get services
        if (!ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            return;
        }
        
        Bounds innerBounds = worldBox.InnerBounds;
        Bounds entryBounds = worldBox.Bounds; // 使用 2D 物理 Bounds 确保精确判定

        // 2. Scan all registered boxes for new transitions.
        // 阻止同 WorldBox 的新 entering transition 在上一 transition 完成前启动。
        bool hasActiveEntering = false;
        for (int i = 0; i < activeTransitions.Count; i++)
        {
            if (activeTransitions[i].WorldBox == worldBox && activeTransitions[i].IsEntering)
            {
                hasActiveEntering = true;
                break;
            }
        }

        foreach (StandardBox box in this.components)
        {
            if (box == null || box == worldBox || box.gameObject.scene != worldBox.gameObject.scene)
            {
                continue;
            }

            if (IsBoxTransitioning(box))
            {
                continue;
            }

            Bounds boxBounds = box.Bounds;
            if (boxBounds.size == Vector3.zero)
            {
                continue;
            }

            var center = boxBounds.center;

            // --- Outer -> Inner (Entering) Transition Detection ---
            if (physicalBoxService.TryGetLinearPushDirection(box, out BoxPushDirection pushDir))
            {
                Vector3 axis = Vector3.zero;
                float cellSize = 1f;
                
                switch (pushDir)
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
                
                if (box.Grid != null)
                {
                    cellSize = Mathf.Abs(pushDir == BoxPushDirection.Left || pushDir == BoxPushDirection.Right ? box.Grid.cellSize.x : box.Grid.cellSize.y);
                }
                
                Vector3 nextCenter = center + axis * cellSize;

                if (!IsOwnedByWorldBox(box.transform, worldBox))
                {
                    if (!hasActiveEntering && IsTouchingOuterBoundsForEntering(entryBounds, boxBounds, pushDir, OuterEdgeBlockerTouchTolerance))
                    {
                        if (!IsInnerDestinationBlocked(box, worldBox, pushDir, entryBounds, boxBounds))
                        {
                            StartTransition(box, worldBox, pushDir, isEntering: true, center, nextCenter, cellSize);
                        }
                        else
                        {
                            if (!TryRefreshOuterContactExitBlocker(entryBounds, worldBox, box, boxBounds))
                            {
                                ClearBoxExitBlocker(worldBox, box);
                            }
                        }
                    }
                }
                // --- Inner -> Outer (Exiting) Transition Detection ---
                else if (innerBounds.size != Vector3.zero && IsOwnedByWorldBox(box.transform, worldBox))
                {
                    if (IsTouchingInnerBoundsForExiting(innerBounds, boxBounds, pushDir, OuterEdgeBlockerTouchTolerance))
                    {
                        if (!IsOuterDestinationBlocked(box, worldBox, pushDir))
                        {
                            StartTransition(box, worldBox, pushDir, isEntering: false, center, nextCenter, cellSize);
                        }
                    }
                }
            }
        }
    }

    private static bool IsTouchingOuterBoundsForEntering(Bounds outerBounds, Bounds boxBounds, BoxPushDirection direction, float threshold)
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

    public static bool IsTouchingInnerBoundsForExiting(Bounds innerBounds, Bounds boxBounds, BoxPushDirection direction, float threshold)
    {
        switch (direction)
        {
            case BoxPushDirection.Right:
                return Mathf.Abs(boxBounds.max.x - innerBounds.max.x) <= threshold &&
                       boxBounds.min.y < innerBounds.max.y &&
                       boxBounds.max.y > innerBounds.min.y;
            case BoxPushDirection.Left:
                return Mathf.Abs(boxBounds.min.x - innerBounds.min.x) <= threshold &&
                       boxBounds.min.y < innerBounds.max.y &&
                       boxBounds.max.y > innerBounds.min.y;
            case BoxPushDirection.Up:
                return Mathf.Abs(boxBounds.max.y - innerBounds.max.y) <= threshold &&
                       boxBounds.min.x < innerBounds.max.x &&
                       boxBounds.max.x > innerBounds.min.x;
            case BoxPushDirection.Down:
                return Mathf.Abs(boxBounds.min.y - innerBounds.min.y) <= threshold &&
                       boxBounds.min.x < innerBounds.max.x &&
                       boxBounds.max.x > innerBounds.min.x;
            default:
                return false;
        }
    }

    private static bool IsPushingOutOfRoom(Vector3 center, Bounds innerBounds, BoxPushDirection pushDir)
    {
        switch (pushDir)
        {
            case BoxPushDirection.Right:
                return center.x > innerBounds.max.x - 0.1f;
            case BoxPushDirection.Left:
                return center.x < innerBounds.min.x + 0.1f;
            case BoxPushDirection.Up:
                return center.y > innerBounds.max.y - 0.1f;
            case BoxPushDirection.Down:
                return center.y < innerBounds.min.y + 0.1f;
            default:
                return false;
        }
    }

    private bool IsInnerDestinationBlocked(StandardBox box, WorldBox worldBox, BoxPushDirection direction, Bounds outerBounds, Bounds boxBounds)
    {
        Transform entrance = worldBox.GetOuterEntrance(Opposite(direction));
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

        // Find the immediate blocker at the entrance inside the WorldBox
        if (!blockerService.TryGetTeleportTargetStandardBoxBlocker(
            movableTargetBounds,
            worldBox,
            ignoredBox: null,
            box.CollisionMask,
            use2D,
            use3D,
            checkingInner: true,
            out StandardBox blocker))
        {
            // No blocker!
            return false;
        }

        // Collect the inner chain starting from the blocker
        Vector3 axis = direction == BoxPushDirection.Right ? Vector3.right :
                       direction == BoxPushDirection.Left ? Vector3.left :
                       direction == BoxPushDirection.Up ? Vector3.up : Vector3.down;

        if (!ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            return true;
        }

        var innerChain = new System.Collections.Generic.List<StandardBox>();
        physicalBoxService.CollectHorizontalChain(blocker, axis, innerChain);

        // 扩展忽略列表：合并 innerChain 与所有 WorldBox 内部的 StandardBox
        var extendedIgnore = new System.Collections.Generic.List<StandardBox>();
        extendedIgnore.AddRange(innerChain);
        foreach (StandardBox registered in this.components)
        {
            if (registered != null && !(registered is WorldBox) && IsOwnedByWorldBox(registered.transform, worldBox))
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
                memberCellSize = Mathf.Abs(direction == BoxPushDirection.Left || direction == BoxPushDirection.Right ? member.Grid.cellSize.x : member.Grid.cellSize.y);

            if (physicalBoxService.CastSingle(member, member.transform.position, axis, memberCellSize, out _, extendedIgnore))
                return true;

            // If the member is at the exit boundary, check if its destination outside is blocked
            if (IsTouchingInnerBoundsForExiting(worldBox.InnerBounds, member.Bounds, direction, OuterEdgeBlockerTouchTolerance))
            {
                if (IsOuterDestinationBlocked(member, worldBox, direction))
                {
                    return true; // Blocked!
                }
            }
        }

        return false; // Not blocked!
    }

    private bool IsOuterDestinationBlocked(StandardBox box, WorldBox worldBox, BoxPushDirection direction)
    {
        if (worldBox.GetOuterEntrance(direction) == null)
        {
            return true;
        }

        Bounds boxBounds = box.Bounds;
        if (!TryGetInnerTargetPositionForBox(direction, worldBox.Bounds, box, boxBounds, out Vector3 targetStart))
        {
            return true;
        }

        Vector3 axis = Vector3.zero;
        float cellSize = 1f;
        switch (direction)
        {
            case BoxPushDirection.Right: axis = Vector3.right; break;
            case BoxPushDirection.Left: axis = Vector3.left; break;
            case BoxPushDirection.Up: axis = Vector3.up; break;
            case BoxPushDirection.Down: axis = Vector3.down; break;
        }
        if (box.Grid != null)
        {
            cellSize = Mathf.Abs(direction == BoxPushDirection.Left || direction == BoxPushDirection.Right ? box.Grid.cellSize.x : box.Grid.cellSize.y);
        }

        Vector3 targetPosition = targetStart + axis * cellSize;
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
        
        return !TryClearTeleportTargetStandardBoxBlockers(
            blockerService,
            movableTargetBounds,
            worldBox,
            ignoredBox: null,
            box.CollisionMask,
            use2D,
            use3D,
            direction,
            box.Owner,
            checkingInner: false);
    }

    private void StartTransition(StandardBox box, WorldBox worldBox, BoxPushDirection direction, bool isEntering, Vector3 startPos, Vector3 endPos, float cellSize)
    {
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

        System.Collections.Generic.List<StandardBox> innerChain = null;
        if (isEntering)
        {
            Transform entrance = worldBox.GetOuterEntrance(Opposite(direction));
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
                        innerChain = new System.Collections.Generic.List<StandardBox>();
                        physicalBoxService.CollectHorizontalChain(blocker, axis, innerChain);
                    }
                }
            }
        }
        else
        {
            if (!TryGetInnerTargetPositionForBox(direction, worldBox.Bounds, box, boxBounds, out P_target_start))
            {
                return;
            }
            P_target_end = P_target_start + axis * cellSize;
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
            StartCell = box.AlignToGrid && box.Grid != null ? box.Grid.WorldToCell(P_start) : default
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
        
        activeTransitions.Add(state);
    }

    private void UpdateBoxTransition(BoxTransitionState state)
    {
        if (state.Box == null || state.WorldBox == null)
        {
            CancelTransition(state);
            return;
        }

        Vector3 currentPos = state.Box.transform.position;
        Vector3 totalVector = state.P_end - state.P_start;
        float totalDist = totalVector.magnitude;
        
        if (totalDist <= Mathf.Epsilon)
        {
            CancelTransition(state);
            return;
        }
        
        Vector3 currentVector = currentPos - state.P_start;
        Vector3 totalDir = totalVector.normalized;
        float dot = Vector3.Dot(currentVector, totalDir);
        float progress = Mathf.Clamp01(dot / totalDist);

        // Entering 和 Exiting：grid cell 变化 + 进度兜底
        if (state.Box.AlignToGrid && state.Box.Grid != null)
        {
            Vector3Int currentCell = state.Box.Grid.WorldToCell(state.Box.transform.position);
            if (currentCell != state.StartCell && progress >= 0.8f)
            {
                CompleteTransition(state);
                return;
            }
        }

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
                member.MoveTo(state.InnerChainStartPositions[i] + axis * (progress * state.Width));
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
                if (IsOwnedByWorldBox(member.transform, state.WorldBox))
                {
                    Vector3 finalPos = state.InnerChainStartPositions[i] + axis * state.Width;
                    member.MoveTo(finalPos);
                    if (ServiceBase.TryGet(out SceneMovableInteractionService sceneMovable))
                    {
                        sceneMovable.RefreshItemImpactBaseline(member);
                    }
                }

                if (ServiceBase.TryGet(out PhysicalBoxService pbService))
                {
                    pbService.QueueGridAlignmentRelease(member);
                }
            }
        }
        
        if (state.Box != null && state.WorldBox != null)
        {
            if (state.IsEntering)
            {
                MoveBoxToInner(state.Box, state.P_target_end, state.WorldBox);
                TypeEventSystem.Global.Send<OnOuterToInnerEvent>();
            }
            else
            {
                MoveBoxToOuter(state.Box, state.P_target_end);
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
        box.CurrentWorldBox = null;
        box.MoveTo(targetPosition);
        if (ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            physicalBoxService.CancelLinearPush(box);
        }

        if (ServiceBase.TryGet(out SceneMovableInteractionService sceneMovable))
        {
            sceneMovable.RefreshItemImpactBaseline(box);
        }
        
        TypeEventSystem.Global.Send<OnInnerToOuterEvent>();
    }

    private static bool IsOwnedByWorldBox(Transform start, WorldBox worldBox)
    {
        if (start == null || worldBox == null) return false;
        StandardBox box = start.GetComponentInParent<StandardBox>();
        return box != null && box.CurrentWorldBox == worldBox;
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

        public readonly System.Collections.Generic.List<StandardBox> InnerChain = new System.Collections.Generic.List<StandardBox>();
        public readonly System.Collections.Generic.List<Vector3> InnerChainStartPositions = new System.Collections.Generic.List<Vector3>();

        // Entering 完成判定：box 的 grid cell 变化即认为已进入
        public Vector3Int StartCell;
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

        Transform entrance = worldBox.GetOuterEntrance(Opposite(direction));
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

    private static void MoveBoxToInner(StandardBox box, Vector3 targetPosition, WorldBox worldBox)
    {
        box.CurrentWorldBox = worldBox;
        box.MoveTo(targetPosition);
        if (ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            physicalBoxService.QueueGridAlignmentRelease(box);
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
        UnityEngine.GameObject pusher,
        bool checkingInner)
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
                checkingInner,
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
        float right = itemBounds.center.x - outerBounds.max.x;
        float down = outerBounds.min.y - itemBounds.center.y;
        float up = itemBounds.center.y - outerBounds.max.y;
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

    private static BoxPushDirection Opposite(BoxPushDirection direction)
    {
        switch (direction)
        {
            case BoxPushDirection.Left:
                return BoxPushDirection.Right;
            case BoxPushDirection.Right:
                return BoxPushDirection.Left;
            case BoxPushDirection.Up:
                return BoxPushDirection.Down;
            case BoxPushDirection.Down:
                return BoxPushDirection.Up;
            default:
                return direction;
        }
    }
}
