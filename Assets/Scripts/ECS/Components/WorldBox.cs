using QFramework;
using UnityEngine;

/// <summary>
/// WorldBox is world's nested rendering carrier. There is no real inside/outside distinction;
/// the entire level is one unified world.
/// <para>
/// Direction conventions (inner/outer semantics):
/// <list type="bullet">
///   <item><b>Inward (Outer to Inner)</b>: moving from WorldBox edge portal to the corresponding world-edge portal.</item>
///   <item><b>Outward (Inner to Outer)</b>: moving from world-edge portal to the WorldBox-side portal.</item>
/// </list>
/// OuterEntrances correspond to portal exit positions next to the WorldBox.
/// </para>
/// </summary>
[DefaultExecutionOrder(1100)]
public class WorldBox : StandardBox
{
    [System.Serializable]
    public struct DirectionEntrance
    {
        public BoxPushDirection Direction;
        public Transform Entrance;
        public bool HasPlatform;
    }

    public Collider OuterQuadCollider;
    public Collider InnerQuadCollider;
    public DirectionEntrance[] OuterEntrances;

    public bool HasLastExitDirection { get; private set; }
    public BoxPushDirection LastExitDirection { get; private set; }

    public Bounds InnerBounds => CalculateBounds(InnerQuadCollider);
    public Bounds OuterBounds => CalculateBounds(OuterQuadCollider);

    private Transform playerTransform;
    private Collider playerCollider3D;
    private Collider2D playerCollider2D;
    private Rigidbody playerBody3D;
    private Rigidbody2D playerBody2D;
    private PlayerController playerController;
    private bool wasPlayerInOuterBounds;
    private bool wasPlayerOutsideInnerBounds = true;
    private bool hasPreviousPlayerBounds;
    private Bounds previousPlayerBounds;
    private IUnRegister pushInitializeUnRegister;
    private IUnRegister pushAttemptUnRegister;
    private WorldBoxExitBlockerService exitBlockerService;
    private PushableBoxService pushableBoxService;

    /// <summary>
    /// Tracks direction bits dynamically added to <see cref="StandardBox.pushableFrom"/>
    /// when a teleport fails due to a blocked portal entrance.
    /// These bits must be reverted once the entrance is no longer blocked.
    /// </summary>
    private BoxPushDirectionMask dynamicPushableFromTeleportBlock;

    private void OnEnable()
    {
        pushInitializeUnRegister?.UnRegister();
        pushAttemptUnRegister?.UnRegister();
        pushInitializeUnRegister = TypeEventSystem.Global.Register<BoxPushInitializeEvent>(OnPushInitialized);
        pushAttemptUnRegister = TypeEventSystem.Global.Register<BoxPushAttemptEvent>(OnPushAttempted);

        pushableBoxService = GetPushableBoxService();
    }

    private void OnDisable()
    {
        pushInitializeUnRegister?.UnRegister();
        pushAttemptUnRegister?.UnRegister();
        pushInitializeUnRegister = null;
        pushAttemptUnRegister = null;
        ClearAllExitBlockers();
    }

    protected override void OnDestroy()
    {
        ClearAllExitBlockers();
        base.OnDestroy();
    }

    private void FixedUpdate()
    {
        if (!EnsurePlayer())
        {
            ClearExitBlocker();
            return;
        }

        RefreshDynamicPushableFromTeleportBlock();

        Bounds outerBounds = CalculateBounds(OuterQuadCollider);
        Bounds innerBounds = CalculateBounds(InnerQuadCollider);
        Bounds playerBounds = GetPlayerBounds();
        if (outerBounds.size == Vector3.zero || playerBounds.size == Vector3.zero)
        {
            ClearExitBlocker();
            return;
        }

        bool playerInOuterBounds = IntersectsXY(outerBounds, playerBounds);
        bool playerOverlapsInnerBounds = innerBounds.size != Vector3.zero && OverlapsXY(innerBounds, playerBounds);

        pushableBoxService.UpdateTransitions(this);
        pushableBoxService.TryTriggerEntering(this);
        pushableBoxService.TryTriggerExiting(this, outerBounds);
        pushableBoxService.TryTriggerGravityTransitions(this, outerBounds);

        if (TryAutoDropPlayerFromTopEntrance(playerBounds))
        {
            return;
        }

        if (playerInOuterBounds)
        {
            UpdateOuterEdgeExitBlocker(outerBounds, innerBounds, playerBounds);
            wasPlayerInOuterBounds = true;
            wasPlayerOutsideInnerBounds = !playerOverlapsInnerBounds;
            previousPlayerBounds = playerBounds;
            hasPreviousPlayerBounds = true;
            return;
        }

        if (!wasPlayerInOuterBounds || !TryGetExitDirection(outerBounds, playerBounds, out BoxPushDirection direction))
        {
            ClearExitBlocker();
            wasPlayerOutsideInnerBounds = !playerOverlapsInnerBounds;
            previousPlayerBounds = playerBounds;
            hasPreviousPlayerBounds = true;
            return;
        }

        if (TryRefreshExitBlocker(direction, outerBounds, innerBounds, playerBounds))
        {
            wasPlayerInOuterBounds = true;
            wasPlayerOutsideInnerBounds = !playerOverlapsInnerBounds;
            previousPlayerBounds = playerBounds;
            hasPreviousPlayerBounds = true;
            return;
        }

        ClearExitBlocker();
        if (MovePlayerToInnerSide(direction, outerBounds, innerBounds, playerBounds))
        {
            HasLastExitDirection = true;
            LastExitDirection = direction;
            wasPlayerInOuterBounds = false;
            wasPlayerOutsideInnerBounds = true;
            hasPreviousPlayerBounds = false;
        }
    }

    private bool EnsurePlayer()
    {
        if (playerTransform == null)
        {
            PlayerController player = ServiceBase.Get<PlayerService>()?.Player;
            if (player == null)
            {
                return false;
            }

            playerController = player;
            GameObject playerObject = player.gameObject;
            playerTransform = playerObject.transform;
            playerCollider3D = playerObject.GetComponent<Collider>();
            playerCollider2D = playerObject.GetComponent<Collider2D>();
            playerBody3D = playerObject.GetComponent<Rigidbody>();
            playerBody2D = playerObject.GetComponent<Rigidbody2D>();
        }

        return true;
    }

    private bool TryAutoDropPlayerFromTopEntrance(Bounds playerBounds)
    {
        const BoxPushDirection topSide = BoxPushDirection.Up;
        if (CanPushFrom(topSide) || HasOuterEntrancePlatform(topSide) || GetOuterEntrance(topSide) == null)
        {
            return false;
        }

        Bounds worldBoxBounds = Bounds;
        if (worldBoxBounds.size == Vector3.zero ||
            !IsTouchingTopSide(worldBoxBounds, playerBounds, outerEdgeBlockerTouchTolerance,
                topEntranceCenterXTolerance))
        {
            return false;
        }

        return TeleportPlayerToOuterEntrance(topSide);
    }

    private void OnPushInitialized(BoxPushInitializeEvent e)
    {
        if (e.Box != this || e.CanPush)
        {
            return;
        }

        MovePusherToOuterEntranceFromBlockedPush(e.Direction, e.Pusher);
    }

    private void OnPushAttempted(BoxPushAttemptEvent e)
    {
        if (e.Box != this || e.CanPush)
        {
            return;
        }

        MovePusherToOuterEntranceFromBlockedPush(e.Direction, e.Pusher);
    }

    /// <summary>
    /// Fallback when push is blocked (WorldBox is not pushable by default):
    /// try to teleport the pusher to the corresponding OuterEntrance (portal exit next to WorldBox).
    /// If teleport also fails (entrance blocked by normalbox), dynamically make WorldBox pushable from that direction.
    /// </summary>
    private void MovePusherToOuterEntranceFromBlockedPush(BoxPushDirection direction, GameObject pusher)
    {
        BoxPushDirection side = Opposite(direction);
        if (!TeleportPusherToOuterEntrance(pusher, side, direction))
        {
            // Teleport failed: entrance blocked. Dynamically make WorldBox pushable from this direction.
            BoxPushDirectionMask mask = ToMask(Opposite(direction));
            dynamicPushableFromTeleportBlock |= mask;
            AddPushableDirection(mask);
        }
    }

    public override bool HandlePlayerImpact(SceneMovablePlayerImpactContext context)
    {
        if (!EnsurePlayer())
        {
            return false;
        }

        var t = TeleportPlayerToOuterEntrance(context.ItemFace);
        if (t)
        {
            playerController?.ApplyExternalVelocity(-context.RelativeVelocity);
            return true;
        }

        if (CanPushFrom(context.ItemFace))
        {
            return base.HandlePlayerImpact(context);
        }

#if UNITY_EDITOR
        Debug.Log($"Handled player impact on WorldBox. Teleported: {t}");
#endif
        return false;
    }

    private bool TeleportPlayerToOuterEntrance(BoxPushDirection side)
    {
        if (!TryMovePlayerToOuterEntrance(side, Opposite(side)))
        {
            return false;
        }

        MarkOuterEntranceTeleport(side);
        return true;
    }

    private bool TeleportPusherToOuterEntrance(GameObject pusher, BoxPushDirection side, BoxPushDirection pushDirection)
    {
        if (!TryMovePusherToOuterEntrance(pusher, side, pushDirection))
        {
            return false;
        }

        MarkOuterEntranceTeleport(side);
        return true;
    }

    private void MarkOuterEntranceTeleport(BoxPushDirection side)
    {
        HasLastExitDirection = true;
        LastExitDirection = side;
        wasPlayerInOuterBounds = false;
        wasPlayerOutsideInnerBounds = true;
        hasPreviousPlayerBounds = false;
        ClearExitBlocker();
    }

    /// <summary>Teleport pusher to outer entrance opposite <paramref name="pushDirection"/> (blocked-push semantics).</summary>
    public bool TryTeleportPusherToOuterEntranceForPushInterrupt(BoxPushDirection pushDirection, GameObject pusher)
    {
        if ((pushableFrom & ToMask(Opposite(pushDirection))) != 0)
        {
            return false;
            
        }
        
        return TeleportPusherToOuterEntrance(pusher, Opposite(pushDirection), pushDirection);
        
    }

    private Bounds GetPlayerBounds()
    {
        if (playerCollider2D != null)
        {
            return CalculateBounds(playerCollider2D);
        }

        return playerCollider3D != null ? CalculateBounds(playerCollider3D) : new Bounds(playerTransform.position, Vector3.zero);
    }

    [SerializeField] private float paddingX = 0.03f;
    [SerializeField] private float paddingY = 0.03f;
    [SerializeField, Min(0f)] private float outerEdgeBlockerTouchTolerance = -0.1f;
    [SerializeField, Min(0f)] private float topEntranceCenterXTolerance = 0.5f;

    public float TopEntranceCenterXTolerance => topEntranceCenterXTolerance;

    private bool MovePlayerToInnerSide(BoxPushDirection direction, Bounds outerBounds, Bounds innerBounds, Bounds playerBounds)
    {
        if (!TryGetInnerTargetPosition(direction, outerBounds, innerBounds, playerBounds, out Vector3 position))
        {
            return false;
        }

        Bounds targetBounds = playerBounds;
        targetBounds.center = position;

        // 检查内侧落点是否被 StandardBox 占据（内部 + 已退出但仍在入口处）
        WorldBoxExitBlockerService blockerService = GetExitBlockerService();
        if (blockerService != null)
        {
            bool use2D = playerCollider2D != null;
            bool use3D = playerCollider3D != null;

            // 入口处有 box：尝试将其向 entry 方向推动一格；能推动则可进入，不能则拒绝。
            if (blockerService.TryGetTeleportTargetStandardBoxBlocker(
                targetBounds, this, ignoredBox: null, CollisionMask,
                use2D, use3D, out StandardBox blocker))
            {
#if UNITY_EDITOR
                Debug.Log($"[WorldBox] MovePlayerToInnerSide: blocker={blocker.name} pos={blocker.transform.position} direction={direction}", this);
#endif
                if (!TryPushEntranceBlocker(blocker, direction))
                    return false;
            }

            // 推动后复查是否还有阻挡
            if (blockerService.TryGetTeleportTargetStandardBoxBlocker(
                targetBounds, this, ignoredBox: null, CollisionMask,
                use2D, use3D,  out StandardBox stillBlocker))
            {
#if UNITY_EDITOR
                Debug.Log($"[WorldBox] MovePlayerToInnerSide: still blocked by box={stillBlocker?.name}", this);
#endif
                return false;
            }
        }

        // 若有活跃的 entering/exiting transition，入口可能有 visual clone
        // 而真实 box 尚未通过 SetParent 进入碰撞查询范围，需额外阻塞。
        if (ServiceBase.TryGet(out PushableBoxService pushableBoxService) &&
            pushableBoxService.HasActiveTransitionForWorldBox(this))
        {
            return false;
        }

        MovePlayer(position);
        TypeEventSystem.Global.Send<OnOuterToInnerEvent>();
        playerController?.ClampMotion();
        return true;
    }

    private bool TryGetInnerTargetPosition(BoxPushDirection direction, Bounds outerBounds, Bounds innerBounds, Bounds playerBounds, out Vector3 position)
    {
        position = playerTransform != null ? playerTransform.position : playerBounds.center;
        if (innerBounds.size == Vector3.zero)
        {
            return false;
        }

        Vector3 extents = playerBounds.extents;
        float innerInsideMinX = innerBounds.min.x + extents.x;
        float innerInsideMaxX = innerBounds.max.x - extents.x;
        float innerInsideMinY = innerBounds.min.y + extents.y;
        float innerInsideMaxY = innerBounds.max.y - extents.y;

        switch (direction)
        {
            case BoxPushDirection.Left:
                position.x = innerBounds.min.x - extents.x - paddingX;
                position.y = RemapClamped(playerBounds.center.y, outerBounds.min.y, outerBounds.max.y, innerInsideMinY, innerInsideMaxY) + paddingY;
                break;
            case BoxPushDirection.Right:
                position.x = innerBounds.max.x + extents.x + paddingX;
                position.y = RemapClamped(playerBounds.center.y, outerBounds.min.y, outerBounds.max.y, innerInsideMinY, innerInsideMaxY) + paddingY;
                break;
            case BoxPushDirection.Down:
                position.x = RemapClamped(playerBounds.center.x, outerBounds.min.x, outerBounds.max.x, innerInsideMinX, innerInsideMaxX);
                position.y = innerBounds.min.y - extents.y - paddingY;
                break;
            case BoxPushDirection.Up:
                position.x = RemapClamped(playerBounds.center.x, outerBounds.min.x, outerBounds.max.x, innerInsideMinX, innerInsideMaxX);
                position.y = innerBounds.max.y + extents.y + paddingY;
                break;
        }

        position = SnapToGridTargetCenter(position);
        return true;
    }

    private bool TryGetInnerTargetBounds(BoxPushDirection direction, Bounds outerBounds, Bounds innerBounds, Bounds playerBounds, out Bounds targetBounds)
    {
        targetBounds = default;
        if (!TryGetInnerTargetPosition(direction, outerBounds, innerBounds, playerBounds, out Vector3 targetPosition))
        {
            return false;
        }

        targetBounds = playerBounds;
        targetBounds.center = targetPosition;
        return true;
    }

    private Vector3 SnapToGridTargetCenter(Vector3 position)
    {
        if (!AlignToGrid)
        {
            return position;
        }

        Grid grid = Grid;
        if (grid == null)
        {
            return position;
        }

        Vector3Int cell = grid.WorldToCell(position);
        Vector3 snapped = grid.CellToWorld(cell) + Vector3.Scale(grid.cellSize, CellOffset);
        snapped.z = position.z;
        return snapped;
    }

    private void UpdateOuterEdgeExitBlocker(Bounds outerBounds, Bounds innerBounds, Bounds playerBounds)
    {
        if (!TryGetOuterContactDirection(outerBounds, playerBounds, out BoxPushDirection direction) ||
            !TryRefreshExitBlocker(direction, outerBounds, innerBounds, playerBounds))
        {
            ClearExitBlocker();
        }
    }

    private bool TryRefreshExitBlocker(BoxPushDirection direction, Bounds outerBounds, Bounds innerBounds, Bounds playerBounds)
    {
        if (!TryGetInnerTargetBounds(direction, outerBounds, innerBounds, playerBounds, out Bounds innerTargetBounds))
        {
            return false;
        }

        WorldBoxExitBlockerService service = GetExitBlockerService();
        if (service == null)
        {
            return false;
        }

        bool use2D = playerCollider2D != null;
        bool use3D = playerCollider3D != null;
        
        if (service.TryRefreshBlockerForStaticInnerHit(this, direction, outerBounds, innerTargetBounds, playerBounds, CollisionMask, use2D, use3D))
        {
            return true;
        }

        if (service.TryGetTeleportTargetStandardBoxBlocker(
            innerTargetBounds, this, ignoredBox: null, CollisionMask,
            use2D, use3D, out StandardBox blocker))
        {
            if (!CanPushEntranceBlocker(blocker, direction, out _))
            {
                service.TryRefreshBlockerForDynamicHit(this, this, direction, outerBounds, playerBounds, use2D, use3D);
                return true;
            }
        }

        return false;
    }

    private WorldBoxExitBlockerService GetExitBlockerService()
    {
        if (exitBlockerService == null)
        {
            exitBlockerService = ServiceBase.Get<WorldBoxExitBlockerService>();
        }

        return exitBlockerService;
    }
    private PushableBoxService GetPushableBoxService()
    {
        if (pushableBoxService == null)
        {
            pushableBoxService = ServiceBase.Get<PushableBoxService>();
        }

        return pushableBoxService;
    }

    /// <summary>
    /// Checks each direction in <see cref="dynamicPushableFromTeleportBlock"/> to see if
    /// the corresponding OuterEntrance (portal exit next to WorldBox) is still blocked.
    /// If the blocker normalbox has been removed, reverts the pushable direction bit,
    /// restoring teleport-on-contact behavior for that side.
    /// </summary>
    private void RefreshDynamicPushableFromTeleportBlock()
    {
        if (dynamicPushableFromTeleportBlock == BoxPushDirectionMask.None)
        {
            return;
        }

        CheckAndClearDynamicDirection(BoxPushDirection.Left);
        CheckAndClearDynamicDirection(BoxPushDirection.Right);
        CheckAndClearDynamicDirection(BoxPushDirection.Up);
        CheckAndClearDynamicDirection(BoxPushDirection.Down);
    }

    /// <summary>
    /// For a single direction: if the corresponding portal entrance is no longer blocked
    /// by a normalbox, clear its dynamic pushable bit so the WorldBox reverts to
    /// teleport-on-contact instead of push-on-contact for that side.
    /// <para>
    /// <paramref name="direction"/> is the pushableFrom direction ("pushable from this side"),
    /// which directly corresponds to the OuterEntrance side that was blocked.
    /// E.g. <c>Left</c> means OuterEntrance(Left) was blocked during the original teleport attempt.
    /// </para>
    /// </summary>
    private void CheckAndClearDynamicDirection(BoxPushDirection direction)
    {
        BoxPushDirectionMask mask = ToMask(direction);
        if ((dynamicPushableFromTeleportBlock & mask) == 0)
        {
            return;
        }

        // The bit stored in dynamicPushableFromTeleportBlock matches the side passed to
        // GetOuterEntrance() during the original failed teleport in MovePusherToOuterEntranceFromBlockedPush:
        //   pushDir=Right → side=Opposite(Right)=Left → GetOuterEntrance(Left) was blocked
        //   mask = ToMask(Left) stored in dynamicPushableFromTeleportBlock
        // So direction == Left maps directly to GetOuterEntrance(Left).
        Transform entrance = GetOuterEntrance(direction);
        if (entrance == null)
        {
            return; // No entrance on this side; keep pushable
        }

        WorldBoxExitBlockerService blockerService = GetExitBlockerService();
        if (blockerService == null)
        {
            return;
        }

        // Build a query bounds at the entrance position using the player's collider size
        Bounds targetBounds = EnsurePlayer() ? GetPlayerBounds() : new Bounds(entrance.position, Vector3.one * 0.5f);
        targetBounds.center = entrance.position;

        bool use2D = playerCollider2D != null;
        bool use3D = playerCollider3D != null;
        if (!use2D && !use3D)
        {
            use2D = true;
        }

        if (blockerService.TryGetTeleportTargetStandardBoxBlocker(
            targetBounds, this, ignoredBox: null, CollisionMask,
            use2D, use3D, out _))
        {
            return; // Still blocked => keep pushable
        }

        // Entrance unblocked => revert dynamic pushable direction
        dynamicPushableFromTeleportBlock &= ~mask;
        RemovePushableDirection(mask);
    }

    private void ClearExitBlocker()
    {
        if (exitBlockerService == null && ServiceBase.TryGet(out WorldBoxExitBlockerService service))
        {
            exitBlockerService = service;
        }

        exitBlockerService?.Clear(this, this);
    }

    private void ClearAllExitBlockers()
    {
        if (exitBlockerService == null && ServiceBase.TryGet(out WorldBoxExitBlockerService service))
        {
            exitBlockerService = service;
        }

        exitBlockerService?.Clear(this);
    }

    private bool TryPushWorldBoxFromBlockedTeleportTarget(BoxPushDirection pushDirection, GameObject pusher)
    {
        if (pusher == null || pusher.GetComponent<PlayerController>() == null)
        {
            return false;
        }

        Vector3 worldBoxPosition = transform.position;
        BoxPushAttemptEvent attempt = TryPush(pushDirection, pusher, initializedCanPush: true);
        return attempt.CanPush && (transform.position - worldBoxPosition).sqrMagnitude > Mathf.Epsilon;
    }



    private bool CanPushEntranceBlocker(StandardBox blocker, BoxPushDirection direction, out System.Collections.Generic.List<StandardBox> chain)
    {
        chain = null;
        if (blocker == null || !ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            return false;
        }

        Vector3 axis = direction == BoxPushDirection.Right ? Vector3.right :
            direction == BoxPushDirection.Left ? Vector3.left :
            direction == BoxPushDirection.Up ? Vector3.up : Vector3.down;

        chain = new System.Collections.Generic.List<StandardBox>();
        physicalBoxService.CollectHorizontalChain(blocker, axis, chain);

        var extendedIgnore = new System.Collections.Generic.List<StandardBox>();
        extendedIgnore.AddRange(chain);

        foreach (StandardBox member in chain)
        {
            if (member == null) continue;

            float memberCellSize = 1f;
            if (member.Grid != null)
                memberCellSize = Mathf.Abs(direction == BoxPushDirection.Left || direction == BoxPushDirection.Right
                    ? member.Grid.cellSize.x
                    : member.Grid.cellSize.y);

            if (physicalBoxService.CastSingle(member, member.transform.position, axis, memberCellSize, out _, extendedIgnore))
            {
                return false; // Chain is statically blocked
            }
        }

        return true;
    }

    /// <summary>
    /// 尝试推动入口处的 box 向 entry 方向移动一格。用于玩家传送进入时清理入口。
    /// </summary>
    private bool TryPushEntranceBlocker(StandardBox blocker, BoxPushDirection direction)
    {
        if (!CanPushEntranceBlocker(blocker, direction, out var chain))
        {
            return false;
        }

        if (!ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            return false;
        }

        Vector3 axis = direction == BoxPushDirection.Right ? Vector3.right :
            direction == BoxPushDirection.Left ? Vector3.left :
            direction == BoxPushDirection.Up ? Vector3.up : Vector3.down;

        // Can push! Move the entire chain instantly by one cell size.
        // This makes room for the teleporting entity (player).
        foreach (StandardBox member in chain)
        {
            if (member == null) continue;

            float memberCellSize = 1f;
            if (member.Grid != null)
                memberCellSize = Mathf.Abs(direction == BoxPushDirection.Left || direction == BoxPushDirection.Right
                    ? member.Grid.cellSize.x
                    : member.Grid.cellSize.y);

            Vector3 newPos = member.transform.position + axis * memberCellSize;
            member.MoveTo(newPos);
            physicalBoxService.QueueGridAlignmentRelease(member);

            if (ServiceBase.TryGet(out SceneMovableInteractionService sceneMovable))
            {
                sceneMovable.RefreshItemImpactBaseline(member);
            }
        }

        return true;
    }

    /// <summary>
    /// 与 <see cref="UpdateOuterEdgeExitBlocker"/> / <see cref="TryRefreshExitBlocker"/> 使用<strong>同一套</strong>外缘方向解析：
    /// 优先 <see cref="TryGetOuterContactDirection"/>（与临时墙检测一致）；若无法解析则退回 <c>Opposite(pushDirection)</c>。
    /// </summary>
    private bool TryResolveExitFaceForExitBlockerSync(
        BoxPushDirection pushDirection,
        Bounds outerBounds,
        Bounds innerBounds,
        Bounds playerBounds,
        out BoxPushDirection exitFace)
    {
        exitFace = Opposite(pushDirection);
        if (outerBounds.size == Vector3.zero || playerBounds.size == Vector3.zero)
        {
            return false;
        }

        if (!IntersectsXY(outerBounds, playerBounds))
        {
            return false;
        }

        if (TryGetOuterContactDirection(outerBounds, playerBounds, out BoxPushDirection outerDir))
        {
            exitFace = outerDir;
        }

        return true;
    }

    /// <summary>
    /// 当前外缘穿入方向（与 ExitBlocker 刷新逻辑一致）上，内侧落点是否存在静态阻挡。
    /// 不修改物理、不建临时墙。
    /// </summary>
    public bool QueryInnerExitBlockedForActivePush(BoxPushDirection pushDirection)
    {
        if (!EnsurePlayer())
        {
            return false;
        }

        Bounds outerBounds = CalculateBounds(OuterQuadCollider);
        Bounds innerBounds = CalculateBounds(InnerQuadCollider);
        Bounds playerBounds = GetPlayerBounds();
        if (!TryResolveExitFaceForExitBlockerSync(pushDirection, outerBounds, innerBounds, playerBounds, out BoxPushDirection exitFace))
        {
            return false;
        }

        TryGetInnerTargetBounds(exitFace, outerBounds, innerBounds, playerBounds, out Bounds innerTargetBounds);
        WorldBoxExitBlockerService service = GetExitBlockerService();

        bool use2D = playerCollider2D != null;
        bool use3D = playerCollider3D != null;
        bool blocked = service.QueryInnerExitStaticallyBlocked(
            this,
            exitFace,
            outerBounds,
            innerTargetBounds,
            playerBounds,
            CollisionMask,
            use2D,
            use3D);

        return blocked;
    }

    /// <summary>
    /// 线性推 WorldBox 过程中内侧静态阻挡已消失时由 <see cref="PlayerController"/> 调用：
    /// 先结束推会话再瞬移到内侧（与离开 outer 的 FixedUpdate 路径一致的状态更新）。
    /// </summary>
    public bool TryPassThroughInnerFromActivePush(BoxPushDirection pushDirection)
    {
        if (!EnsurePlayer())
        {
            return false;
        }

        Bounds outerBounds = CalculateBounds(OuterQuadCollider);
        Bounds innerBounds = CalculateBounds(InnerQuadCollider);
        Bounds playerBounds = GetPlayerBounds();
        if (outerBounds.size == Vector3.zero || innerBounds.size == Vector3.zero || playerBounds.size == Vector3.zero)
        {
            return false;
        }

        if (!TryResolveExitFaceForExitBlockerSync(pushDirection, outerBounds, innerBounds, playerBounds, out BoxPushDirection exitFace))
        {
            return false;
        }

        ClearExitBlocker();
        if (!MovePlayerToInnerSide(exitFace, outerBounds, innerBounds, playerBounds))
        {
            return false;
        }

        HasLastExitDirection = true;
        LastExitDirection = exitFace;
        wasPlayerInOuterBounds = false;
        wasPlayerOutsideInnerBounds = true;
        hasPreviousPlayerBounds = false;
        return true;
    }

    private bool TryGetOuterContactDirection(Bounds outerBounds, Bounds playerBounds, out BoxPushDirection direction)
    {
        direction = default;
        float threshold = Mathf.Max(0f, outerEdgeBlockerTouchTolerance);
        float downThreshold = Mathf.Max(threshold, GetCellAxisSize(true));
        float leftDistance = playerBounds.min.x - outerBounds.min.x;
        float rightDistance = outerBounds.max.x - playerBounds.max.x;
        float downDistance = playerBounds.min.y - outerBounds.min.y;
        float upDistance = outerBounds.max.y - playerBounds.max.y;
        bool touchesLeft = leftDistance <= threshold;
        bool touchesRight = rightDistance <= threshold;
        bool touchesDown = downDistance <= downThreshold;
        bool touchesUp = upDistance <= threshold;

        if (!touchesLeft && !touchesRight && !touchesDown && !touchesUp)
        {
            return false;
        }

        if (hasPreviousPlayerBounds)
        {
            Vector3 movement = playerBounds.center - previousPlayerBounds.center;
            if (Mathf.Abs(movement.x) >= Mathf.Abs(movement.y))
            {
                if (movement.x < -Mathf.Epsilon && touchesLeft)
                {
                    direction = BoxPushDirection.Left;
                    return true;
                }

                if (movement.x > Mathf.Epsilon && touchesRight)
                {
                    direction = BoxPushDirection.Right;
                    return true;
                }
            }
            else
            {
                if (movement.y < -Mathf.Epsilon && touchesDown)
                {
                    direction = BoxPushDirection.Down;
                    return true;
                }

                if (movement.y > Mathf.Epsilon && touchesUp)
                {
                    direction = BoxPushDirection.Up;
                    return true;
                }
            }
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

    private float GetCellAxisSize(bool vertical)
    {
        Grid grid = Grid;
        if (grid == null)
        {
            return 0f;
        }

        return Mathf.Abs(vertical ? grid.cellSize.y : grid.cellSize.x);
    }

    private bool TryMovePlayerToOuterEntrance(BoxPushDirection direction, BoxPushDirection pushDirection)
    {
        Transform entrance = GetOuterEntrance(direction);
        if (entrance == null)
        {
            return false;
        }

        Bounds targetBounds = GetPlayerBounds();
        targetBounds.center = entrance.position;

        WorldBoxExitBlockerService blockerService = GetExitBlockerService();
        if (blockerService != null)
        {
            bool use2D = playerCollider2D != null;
            bool use3D = playerCollider3D != null;

            if (blockerService.TryGetTeleportTargetStandardBoxBlocker(
                targetBounds, this, ignoredBox: null, CollisionMask,
                use2D, use3D, out StandardBox blocker))
            {
                if (!TryPushEntranceBlocker(blocker, pushDirection))
                {
                    return false;
                }
            }

            if (blockerService.TryGetTeleportTargetStandardBoxBlocker(
                targetBounds, this, ignoredBox: null, CollisionMask,
                use2D, use3D, out _))
            {
                return false;
            }
        }

        MovePlayer(entrance.position);
        TypeEventSystem.Global.Send<OnInnerToOuterEvent>();
        playerController?.ClampMotion();
        return true;
    }

    private bool TryMovePusherToOuterEntrance(GameObject pusher, BoxPushDirection direction, BoxPushDirection pushDirection)
    {
        Transform entrance = GetOuterEntrance(direction);
        if (pusher == null || entrance == null)
        {
            return false;
        }

        PlayerController pusherController = pusher.GetComponent<PlayerController>();
        Bounds targetBounds = GetPusherBounds(pusher, pusherController);
        targetBounds.center = entrance.position;

        WorldBoxExitBlockerService blockerService = GetExitBlockerService();
        if (blockerService != null)
        {
            bool use2D = pusher.GetComponent<Collider2D>() != null;
            bool use3D = pusher.GetComponent<Collider>() != null;

            if (blockerService.TryGetTeleportTargetStandardBoxBlocker(
                targetBounds, this, ignoredBox: null, CollisionMask,
                use2D, use3D, out StandardBox blocker))
            {
                if (!TryPushEntranceBlocker(blocker, pushDirection))
                {
                    return false;
                }
            }

            if (blockerService.TryGetTeleportTargetStandardBoxBlocker(
                targetBounds, this, ignoredBox: null, CollisionMask,
                use2D, use3D, out _))
            {
                return false;
            }
        }

        MovePusher(pusher, pusherController, entrance.position);
        if (pusherController != null)
        {
            TypeEventSystem.Global.Send<OnInnerToOuterEvent>();
            pusherController.ClampMotion();
        }

        return true;
    }

    private Bounds GetPusherBounds(GameObject pusher, PlayerController pusherController)
    {
        if (pusherController != null)
        {
            ISceneMovableBoundsProvider boundsProvider = pusherController.BoundsProvider;
            if (boundsProvider != null && boundsProvider.IsValid)
            {
                return boundsProvider.Bounds;
            }
        }

        StandardBox standardBox = pusher.GetComponent<StandardBox>();
        if (standardBox != null)
        {
            var bound = standardBox.Bounds;
            bound.Expand(-0.05f);
            return bound;
        }

        ISceneMovableBoundsProvider externalProvider = pusher.GetComponent<ISceneMovableBoundsProvider>();
        if (externalProvider != null && externalProvider.IsValid)
        {
            return externalProvider.Bounds;
        }

        return new Bounds(pusher.transform.position, Vector3.one);
    }

    public Transform GetOuterEntrance(BoxPushDirection direction)
    {
        if (OuterEntrances == null)
        {
            return null;
        }

        for (int i = 0; i < OuterEntrances.Length; i++)
        {
            if (OuterEntrances[i].Direction == direction)
            {
                return OuterEntrances[i].Entrance;
            }
        }

        return null;
    }

    public bool HasOuterEntrancePlatform(BoxPushDirection direction)
    {
        if (OuterEntrances == null)
        {
            return false;
        }

        for (int i = 0; i < OuterEntrances.Length; i++)
        {
            if (OuterEntrances[i].Direction == direction)
            {
                return OuterEntrances[i].HasPlatform;
            }
        }

        return false;
    }

    private void MovePlayer(Vector3 position)
    {
        position -= playerController.CenterOffset;
        if (playerBody2D != null)
        {
            playerBody2D.position = (Vector2)position;
            playerTransform.position = position;
        }
        else if (playerBody3D != null)
        {
            playerBody3D.position = position;
            playerTransform.position = position;
        }
        else
        {
            playerTransform.position = position;
        }
    }

    private static void MovePusher(GameObject pusher, PlayerController pusherController, Vector3 position)
    {
        if (pusherController != null)
        {
            position -= pusherController.CenterOffset;
        }

        Rigidbody2D body2D = pusher.GetComponent<Rigidbody2D>();
        Rigidbody body3D = pusher.GetComponent<Rigidbody>();
        StandardBox standardBox = pusherController == null ? pusher.GetComponent<StandardBox>() : null;
        if (standardBox != null)
        {
            standardBox.MoveTo(position);
            return;
        }

        Transform pusherTransform = pusher.transform;
        if (body2D != null)
        {
            body2D.position = (Vector2)position;
            pusherTransform.position = position;
        }
        else if (body3D != null)
        {
            body3D.position = position;
            pusherTransform.position = position;
        }
        else
        {
            pusherTransform.position = position;
        }
    }

    private static bool IsTouchingTopSide(Bounds lowerBounds, Bounds upperBounds, float tolerance,
        float centerXTolerance)
    {
        if (lowerBounds.size == Vector3.zero || upperBounds.size == Vector3.zero)
        {
            return false;
        }

        if (centerXTolerance > 0f &&
            Mathf.Abs(upperBounds.center.x - lowerBounds.center.x) > centerXTolerance)
        {
            return false;
        }

        float gap = upperBounds.min.y - lowerBounds.max.y;
        return gap >= -tolerance &&
               gap <= tolerance &&
               upperBounds.min.x < lowerBounds.max.x &&
               upperBounds.max.x > lowerBounds.min.x;
    }

    private static BoxPushDirection GetInnerEntryDirection(Bounds innerBounds, Bounds lastOutsidePlayerBounds)
    {
        float left = innerBounds.min.x - lastOutsidePlayerBounds.center.x;
        float right = lastOutsidePlayerBounds.center.x - innerBounds.max.x;
        float down = innerBounds.min.y - lastOutsidePlayerBounds.center.y;
        float up = lastOutsidePlayerBounds.center.y - innerBounds.max.y;
        float max = left;
        BoxPushDirection direction = BoxPushDirection.Left;

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
            direction = BoxPushDirection.Up;
        }

        return direction;
    }

    private static bool TryGetExitDirection(Bounds outerBounds, Bounds playerBounds, out BoxPushDirection direction)
    {
        float left = outerBounds.min.x - playerBounds.max.x;
        float right = playerBounds.min.x - outerBounds.max.x;
        float down = outerBounds.min.y - playerBounds.max.y;
        float up = playerBounds.min.y - outerBounds.max.y;
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

    private static bool IntersectsXY(Bounds a, Bounds b)
    {
        return a.min.x <= b.max.x && a.max.x >= b.min.x && a.min.y <= b.max.y && a.max.y >= b.min.y;
    }

    private static bool OverlapsXY(Bounds a, Bounds b)
    {
        return a.min.x < b.max.x && a.max.x > b.min.x && a.min.y < b.max.y && a.max.y > b.min.y;
    }

    private static Bounds CalculateBounds(Collider collider)
    {
        if (collider == null)
        {
            return new Bounds(Vector3.zero, Vector3.zero);
        }

        BoxCollider box = collider as BoxCollider;
        if (box != null)
        {
            return TransformLocalBounds(box.transform, new Bounds(box.center, box.size));
        }

        MeshCollider mesh = collider as MeshCollider;
        if (mesh != null && mesh.sharedMesh != null)
        {
            return TransformLocalBounds(mesh.transform, mesh.sharedMesh.bounds);
        }

        return collider.bounds;
    }

    private static Bounds CalculateBounds(Collider2D collider)
    {
        if (collider == null)
        {
            return new Bounds(Vector3.zero, Vector3.zero);
        }

        BoxCollider2D box = collider as BoxCollider2D;
        if (box != null)
        {
            return TransformLocalBounds(box.transform, new Bounds(box.offset, box.size));
        }

        return collider.bounds;
    }

    private static Bounds TransformLocalBounds(Transform transform, Bounds localBounds)
    {
        Vector3 center = transform.TransformPoint(localBounds.center);
        Vector3 extents = localBounds.extents;
        Vector3 axisX = transform.TransformVector(extents.x, 0f, 0f);
        Vector3 axisY = transform.TransformVector(0f, extents.y, 0f);
        Vector3 axisZ = transform.TransformVector(0f, 0f, extents.z);

        extents.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
        extents.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
        extents.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);
        return new Bounds(center, extents * 2f);
    }

    private static float RemapClamped(float value, float inMin, float inMax, float outMin, float outMax)
    {
        if (outMin > outMax)
        {
            return (outMin + outMax) * 0.5f;
        }

        float inputRange = inMax - inMin;
        if (Mathf.Abs(inputRange) <= Mathf.Epsilon)
        {
            return (outMin + outMax) * 0.5f;
        }

        return Mathf.Lerp(outMin, outMax, Mathf.Clamp01((value - inMin) / inputRange));
    }



}
