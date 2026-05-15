using System.Collections.Generic;
using QFramework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

[DisallowMultipleComponent]
public partial class PlayerController : MonoBehaviour, ISceneMovableItem, IPointerClickHandler
{
    private const float AxisNormalBlockThreshold = 0.85f;
    private const int MaxOverlapResolveIterations = 6;
    private const float OverlapResolveEpsilon = 0.001f;

    [System.Serializable]
    public struct ContactState
    {
        public bool grounded;
        public bool upBlocked;
        public bool downBlocked;
        public bool leftBlocked;
        public bool rightBlocked;
        public string upTag;
        public string downTag;
        public string leftTag;
        public string rightTag;
    }

    [Header("Grid")]
    [SerializeField] private Vector3 cellOffset = new Vector3(0.5f, 0.5f, 0f);

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 4f;
    [SerializeField, Min(0.01f)] private float jumpGridHeight = 2.5f;
    [SerializeField] private float gravity = 28f;
    [SerializeField] private float maxFallSpeed = 18f;
    [SerializeField, Min(0f)] private float retainedVelocityDrag = 18f;
    [SerializeField, Min(0f)] private float retainedVelocityStopSpeed = 0.05f;

    [Header("Collision")]
    [SerializeField] private LayerMask collisionMask = ~0;
    [SerializeField] private float skinWidth = 0.02f;
    [SerializeField, Min(2)] private int raysPerSide = 2;
    [SerializeField, Min(0f), Tooltip("脚底有支撑时，若竖直速度严格大于该值则不视为 grounded（避免向上穿过单向板时脚扫到板顶假接地）。")]
    private float groundedMaxUpwardSpeedToRemainGrounded = 0.001f;

    [Header("Push")]
    [SerializeField, Min(0.05f), Tooltip("线性推动期间玩家水平移动速度相对 moveSpeed 的倍率；当 PhysicalBoxService 暴露 LinearPushSpeed 时优先使用其值。")]
    private float pushSpeedMultiplier = 0.4f;
    [SerializeField, Min(0f), Tooltip("一帧内 push 实际位移小于该阈值视为'无进展'，用于 stall 检测；建议略大于墙壁碰撞箱的 ε 偏移。")]
    private float pushStallProgressThreshold = 0.01f;
    [SerializeField, Min(0f), Tooltip("连续无进展超过这个时长就视为撞墙不动，主动结束推动会话避免动画卡 push 状态。")]
    private float pushStallTimeout = 0.1f;
    [SerializeField, Tooltip("勾选后每帧 FixedUpdate 在推 WorldBox 时打印 innerBlocked / worldBoxExitHadInnerBlock（仅用于排查穿门边沿）。")]
    private bool debugWorldBoxInnerExitEdge;

    public Grid Grid => grid;
    public ContactState Contacts => contacts;
    public Vector2 Velocity => velocity;
    /// <summary>
    /// 玩家本帧的水平/垂直输入意图（GetAxisRaw 原始值，未做平滑）。
    /// 比 Velocity 更稳定——动画状态（如 walking 是否触发）应优先使用此值，避免 ResolveTerrainOverlaps
    /// 等物理修正在某些帧把 velocity.x 短暂清零导致的动画抖动。
    /// </summary>
    public Vector2 MoveInput => moveInput;
    public GameObject Owner => gameObject;
    public ISceneMovableBoundsProvider BoundsProvider => sceneMovableBoundsProvider;
    public bool IsSceneMovableActive => isActiveAndEnabled;

    /// <summary>
    /// 玩家是否处于"推动"状态（驱动阶段或被释放过渡拖动阶段）。用于动画切换。
    /// </summary>
    public bool IsPushing
    {
        get
        {
            if (activePushBox != null && activePushCanPush)
            {
                return true;
            }

            if (ServiceBase.TryGet(out PhysicalBoxService svc) && svc.IsPusherInRelease(gameObject))
            {
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// 玩家是否处于"被砸死"过渡：动画/物理层据此完全冻结玩家位置并切换到 crashed 动画。
    /// 由 <see cref="PlayerService"/> 维护，新玩家注册时清零。
    /// </summary>
    public bool IsDying => ServiceBase.TryGet(out PlayerService ps) && ps.IsDying && ps.Player == this;

    /// <summary>
    /// 为 <c>true</c> 时不读取键盘产生的移动、跳跃与平台下落输入；重力与已有速度仍正常结算。
    /// </summary>
    public bool MovementInputDisabled { get; set; }

    private Grid grid;
    private Rigidbody body3D;
    private Rigidbody2D body2D;
    private Collider m_Collider3D;
    private Collider2D m_Collider2D;
    private ColliderSceneMovableBoundsProvider sceneMovableBoundsProvider;
    private ContactState contacts;
    private Vector2 moveInput;
    private Vector2 baseVelocity;
    private Vector2 velocity;
    private bool jumpQueued;
    private bool jumping;
    private float jumpApexY;
    private float fixedZ;
    private StandardBox leftBox;
    private StandardBox rightBox;
    private StandardBox upBox;
    private StandardBox downBox;
    private StandardBox activePushBox;
    private BoxPushDirection activePushDirection;
    private bool activePushCanPush;

    // 空中（!grounded）状态下禁止真正推动 box，但仍要触发 InitializePush 让 WorldBox 等监听者
    // 处理进入逻辑。下面这组字段对"接触相同 box+方向"做节流，避免每帧重复派发 InitializePush
    // 导致 WorldBox 每帧把玩家瞬移到 outer entrance。
    private bool hasAirborneInit;
    private StandardBox airborneInitBox;
    private BoxPushDirection airborneInitDirection;

    // Push stall 检测：箱子被推到墙边/死角时 PhysicalBoxService 每帧返回 clamped≈0，
    // 但 activePushBox 仍非空，导致 IsPushing 一直为 true、动画卡在 push。
    // 这组字段在连续无进展超过 pushStallTimeout 后主动 EndPushSession 并标记
    // (box, direction) 已 stalled，避免下一帧又重新进入推动会话造成 ping-pong。
    private float pushStallTime;
    private bool pushStalled;
    private StandardBox stalledBox;
    private BoxPushDirection stalledDirection;

    // WorldBox inner exit: static inner block cleared mid-push -> TryPassThroughInnerFromActivePush.
    private bool worldBoxExitHadInnerBlock;
    // One-frame relax after plate release when inner query lags collider disable.
    private bool worldBoxExitPendingPressureLogicalUnblock;
    private IUnRegister pressurePlateStateUnRegister;
    private bool worldBoxPressureLatchHadPlayerOnAnyPlate;
    private bool worldBoxPrevFrameBoxOnPlate;
    private bool hasRetainedVelocity;
    private Vector2 retainedVelocityAxis;
    private float retainedVelocitySpeed;
    
    private readonly Queue<Transform> bfsQueue = new Queue<Transform>(32);
    private readonly RaycastHit2D[] hits2D = new RaycastHit2D[8];
    private readonly RaycastHit[] hits3D = new RaycastHit[8];
    private readonly Collider2D[] overlapHits2D = new Collider2D[16];
    private readonly Collider[] overlapHits3D = new Collider[16];

    private void Awake()
    {
        body3D = GetComponent<Rigidbody>();
        body2D = GetComponent<Rigidbody2D>();
        m_Collider3D = GetComponent<Collider>();
        m_Collider2D = GetComponent<Collider2D>();
        sceneMovableBoundsProvider = new ColliderSceneMovableBoundsProvider(gameObject, transform, m_Collider3D, m_Collider2D);
        ServiceBase.Get<PlayerService>()?.Register(this);
        ServiceBase.Get<SceneMovableInteractionService>()?.Register(this);
        pressurePlateStateUnRegister = TypeEventSystem.Global.Register<PressurePlateStateEvent>(OnPressurePlateState);

        if (body3D != null)
        {
            body3D.useGravity = false;
            body3D.isKinematic = true;
        }

        if (body2D != null)
        {
            body2D.gravityScale = 0f;
            body2D.bodyType = RigidbodyType2D.Kinematic;
        }
    }

    private void OnDestroy()
    {
        pressurePlateStateUnRegister?.UnRegister();
        pressurePlateStateUnRegister = null;

        EndPushSession();

        if (ServiceBase.TryGet(out PlayerService playerService))
        {
            playerService.UnRegister(this);
        }

        if (ServiceBase.TryGet(out SceneMovableInteractionService sceneMovableInteractionService))
        {
            sceneMovableInteractionService.UnRegister(this);
        }
    }

    private void Start()
    {
        grid = FindSceneGrid();
        fixedZ = transform.position.z;

        if (grid == null)
        {
            return;
        }

        Vector3Int cell = grid.WorldToCell(transform.position);
        Vector3 snapped = grid.CellToWorld(cell) + Vector3.Scale(grid.cellSize, cellOffset);
        snapped.z = fixedZ;
        MoveTo(snapped);
    }

    private void Update()
    {
        if (MovementInputDisabled || IsMovementBlockedByAppearIntro())
        {
            moveInput = Vector2.zero;
            return;
        }

        moveInput.x = Input.GetAxisRaw("Horizontal");
        moveInput.y = Input.GetAxisRaw("Vertical");

        if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.UpArrow))
        {
            jumpQueued = true;
        }

        HandlePlatformInput();
    }

    /// <summary>
    /// Animator 默认入口为 Appear 等时：在该状态结束前不读取移动/跳跃/平台下落输入（与 <see cref="MovementInputDisabled"/> 独立）。
    /// </summary>
    private bool IsMovementBlockedByAppearIntro()
    {
        if (animator == null || !animator.isActiveAndEnabled)
        {
            return false;
        }

        return animator.GetCurrentAnimatorStateInfo(0).IsName("Appear");
    }

    private void FixedUpdate()
    {
        // 死亡过渡中：完全冻结 player（不解算重力/输入/碰撞），让 crashed 动画在原地播放，
        // 同时避免 ResolveTerrainOverlaps 在落下的箱子穿过 player 时把 player 沿 X 轴挤开。
        if (IsDying)
        {
            return;
        }

        RefreshContacts();
        ProcessPlatformDropRequest();

        float dt = Time.fixedDeltaTime;
        baseVelocity.x = Mathf.Clamp(moveInput.x, -1f, 1f) * moveSpeed;

        // 落地时若仍带向上的 retained，会在 base 被清零后让 velocity.y 仍为正 → 贴板小跳再下落；先清掉竖直向上的 retained。
        if (contacts.grounded &&
            !jumping &&
            hasRetainedVelocity &&
            retainedVelocityAxis.y > 0f &&
            baseVelocity.y <= 0f)
        {
            ClearRetainedVelocity();
        }

        if (contacts.grounded && baseVelocity.y < 0f)
        {
            baseVelocity.y = 0f;
            jumping = false;
        }

        if (jumpQueued && contacts.grounded)
        {
            float height = Mathf.Max(0.01f, GetCellHeight() * jumpGridHeight);
            baseVelocity.y = Mathf.Sqrt(2f * gravity * height);
            jumpApexY = transform.position.y + height;
            jumping = true;
        }

        jumpQueued = false;
        baseVelocity.y = Mathf.Max(baseVelocity.y - gravity * dt, -maxFallSpeed);
        velocity = baseVelocity + TickRetainedVelocity(dt);

        Vector3 delta = new Vector3(velocity.x * dt, velocity.y * dt, 0f);
        if (jumping && transform.position.y + delta.y > jumpApexY)
        {
            delta.y = jumpApexY - transform.position.y;
            baseVelocity.y = 0f;
            velocity.y = GetRetainedVelocity().y;
            jumping = false;
        }

        // 线性推动：先决定/维护推动会话；若处于推动，限速并先驱动箱子，再让玩家按相同位移移动。
        HandleBoxPush(dt, ref delta);

        MoveByAndResolve(new Vector3(delta.x, 0f, 0f));
        MoveByAndResolve(new Vector3(0f, delta.y, 0f));
        RefreshContacts();
        HandleWorldBoxUpPush(delta.y);
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
                    Debug.LogWarning("Multiple Grid components found in the player's scene. Using the first one.", this);
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

    private void RefreshContacts()
    {
        contacts = default;
        contacts.downBlocked = Cast(Vector3.down, skinWidth * 2f, out RayHit downHit);
        contacts.upBlocked = Cast(Vector3.up, skinWidth * 2f, out RayHit upHit);
        contacts.leftBlocked = Cast(Vector3.left, skinWidth * 2f, out RayHit leftHit);
        contacts.rightBlocked = Cast(Vector3.right, skinWidth * 2f, out RayHit rightHit);
        contacts.downTag = downHit.tag;
        contacts.upTag = upHit.tag;
        contacts.leftTag = leftHit.tag;
        contacts.rightTag = rightHit.tag;
        downPlatform = downHit.platform;
        upBox = upHit.box;
        downBox = downHit.box;
        leftBox = leftHit.box;
        rightBox = rightHit.box;
        contacts.grounded = contacts.downBlocked && velocity.y <= groundedMaxUpwardSpeedToRemainGrounded;
    }

    private void HandleWorldBoxUpPush(float verticalDelta)
    {
        if (verticalDelta <= 0f || !contacts.upBlocked)
        {
            return;
        }

        WorldBox worldBox = upBox as WorldBox;
        if (worldBox == null)
        {
            return;
        }

        worldBox.TryPush(BoxPushDirection.Up, gameObject);
    }

    private void OnPressurePlateState(PressurePlateStateEvent e)
    {
        if (!e.Pressed && e.WasPressed)
        {
            worldBoxExitPendingPressureLogicalUnblock = true;
        }
    }

    private void HandleBoxPush(float dt, ref Vector3 delta)
    {
        // 释放过渡中、且玩家被拖动时：屏蔽玩家自身的水平位移，让 PhysicalBoxService 全权驱动。
        // 垂直运动（重力/跳跃）保持正常。
        if (ServiceBase.TryGet(out PhysicalBoxService releaseLookup) && releaseLookup.IsPusherInRelease(gameObject))
        {
            delta.x = 0f;
            baseVelocity.x = 0f;
            velocity.x = 0f;
            return;
        }

        StandardBox box = null;
        BoxPushDirection direction = default;

        if (moveInput.x > 0.01f && contacts.rightBlocked && rightBox != null)
        {
            box = rightBox;
            direction = BoxPushDirection.Right;
        }
        else if (moveInput.x < -0.01f && contacts.leftBlocked && leftBox != null)
        {
            box = leftBox;
            direction = BoxPushDirection.Left;
        }

        if (box == null)
        {
            EndPushSession();
            ResetAirborneInitState();
            ResetStallState();
            return;
        }

        // 之前已对相同 (box, direction) 判定为撞墙不动：直接静默退出，让 IsPushing 变 false，
        // 动画恢复 idle。stalled 状态在玩家松开方向键 / 切换 box 或方向时清除。
        if (pushStalled && stalledBox == box && stalledDirection == direction)
        {
            return;
        }

        // 空中（跳跃/落体）状态下不允许实质性推动 box：
        //  - 起跳前已有 push 会话则触发释放过渡（按 50% 规则收尾）；
        //  - 不进入新的 linear push 会话；
        //  - 但仍调用 InitializePush，让 WorldBox 等监听者通过 BoxPushInitializeEvent.CanPush=false
        //    把玩家瞬移到 outer entrance（保留"贴 WorldBox 起跳按方向键能进入"的语义）；
        //  - 对同一 box+direction 只发一次，避免每帧重复派发导致 WorldBox 每帧瞬移玩家。
        if (!contacts.grounded)
        {
            EndPushSession();

            if (!hasAirborneInit || airborneInitBox != box || airborneInitDirection != direction)
            {
                box.InitializePush(direction, gameObject);
                hasAirborneInit = true;
                airborneInitBox = box;
                airborneInitDirection = direction;
            }

            return;
        }

        ResetAirborneInitState();

        // 切换箱子或换方向：先把旧会话释放（触发 50% 规则收尾），再 InitializePush 新会话。
        if (activePushBox != box || activePushDirection != direction)
        {
            EndPushSession();
            // 切换上下文意味着旧的 stall 判断不再适用；新 (box, direction) 重新允许尝试。
            ResetStallState();
            bool canPush = box.InitializePush(direction, gameObject).CanPush;
            if (!canPush)
            {
                // !CanPush 由 WorldBox 等监听者处理（例如把玩家瞬移到外入口）；本地不进入线性会话。
                return;
            }

            activePushBox = box;
            activePushDirection = direction;
            activePushCanPush = true;
            if (box is WorldBox worldBoxForSession)
            {
                InitializeWorldBoxPressurePlateSessionTracking(worldBoxForSession);
            }
        }

        if (!activePushCanPush)
        {
            return;
        }

        if (!ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            return;
        }

        if (box is WorldBox worldBoxForExit && activePushBox == box && activePushDirection == direction)
        {
            bool rawInnerBlocked = worldBoxForExit.QueryInnerExitBlockedForActivePush(direction);
            bool innerBlocked = rawInnerBlocked;
            if (worldBoxExitPendingPressureLogicalUnblock)
            {
                if (worldBoxExitHadInnerBlock)
                {
                    innerBlocked = false;
                }

                worldBoxExitPendingPressureLogicalUnblock = false;
            }

            if (debugWorldBoxInnerExitEdge)
            {
                Debug.Log(
                    $"[Player] WorldBox exit edge: rawInnerBlocked={rawInnerBlocked} innerBlocked={innerBlocked} hadInnerBlock={worldBoxExitHadInnerBlock} box={box.name} dir={direction}",
                    this);
            }

            if (innerBlocked)
            {
                worldBoxExitHadInnerBlock = true;
            }
            else if (worldBoxExitHadInnerBlock)
            {
                worldBoxExitHadInnerBlock = false;
                // 穿门必须立刻清掉 linearPushes；ReleaseLinearPush 只会进入 ReleaseTransition，
                // 会话在字典里仍存在，TryAdvance/过渡会与世界箱传送语义冲突。
                EndPushSession(cancelLinearPushImmediate: true);
                ResetStallState();
                worldBoxForExit.TryPassThroughInnerFromActivePush(direction);
                physicalBoxService.QueueGridAlignmentRelease(worldBoxForExit);
                delta.x = 0f;
                baseVelocity.x = 0f;
                velocity.x = 0f;
                return;
            }
        }

        float pushSpeed = physicalBoxService.LinearPushSpeed;
        if (pushSpeed <= 0f)
        {
            pushSpeed = moveSpeed * Mathf.Max(0.05f, pushSpeedMultiplier);
        }

        float dirSign = direction == BoxPushDirection.Right ? 1f : -1f;
        float maxStep = pushSpeed * dt * dirSign;

        // 在推动方向上限制玩家本帧水平位移；反方向上禁止（实际上分支已限制 moveInput 同号，这里是兜底）。
        if (dirSign > 0f)
        {
            delta.x = Mathf.Clamp(delta.x, 0f, maxStep);
        }
        else
        {
            delta.x = Mathf.Clamp(delta.x, maxStep, 0f);
        }

        float clamped = physicalBoxService.TryAdvanceLinearPush(box, direction, delta.x, gameObject);

        if (box is WorldBox worldBoxPressure && activePushBox == box && activePushDirection == direction)
        {
            if (TryHandleWorldBoxPressurePlatePushInterrupts(worldBoxPressure, direction, physicalBoxService))
            {
                delta.x = 0f;
                baseVelocity.x = 0f;
                velocity.x = 0f;
                return;
            }
        }

        // Stall 检测：墙体/死角让 magnitude 被 clamp 到 0 或 ε 抖动时，clamped 持续接近 0；
        // 累计无进展时间超过 pushStallTimeout 就强制结束推动会话，并把当前 (box, direction)
        // 标记为 stalled，让 IsPushing 立刻变 false 从而退出 push 动画。
        if (Mathf.Abs(clamped) < pushStallProgressThreshold)
        {
            pushStallTime += dt;
            if (pushStallTime >= pushStallTimeout)
            {
                EndPushSession();
                pushStalled = true;
                stalledBox = box;
                stalledDirection = direction;
                pushStallTime = 0f;
                delta.x = 0f;
                baseVelocity.x = 0f;
                velocity.x = 0f;
                return;
            }
        }
        else
        {
            pushStallTime = 0f;
        }

        delta.x = clamped;
        baseVelocity.x = clamped / Mathf.Max(dt, Mathf.Epsilon);
        velocity.x = baseVelocity.x;
    }

    private void InitializeWorldBoxPressurePlateSessionTracking(WorldBox worldBox)
    {
        worldBoxPressureLatchHadPlayerOnAnyPlate = false;
        worldBoxPrevFrameBoxOnPlate = false;
        if (!ServiceBase.TryGet(out PressurePlateService pressurePlateService))
        {
            return;
        }

        worldBoxPrevFrameBoxOnPlate = pressurePlateService.QueryWorldBoxGridSnapStablePressingAnyRegisteredPlateXY(worldBox);
        worldBoxPressureLatchHadPlayerOnAnyPlate = pressurePlateService.QueryBoundsOverlapAnyRegisteredPlateXY(GetBounds());
    }

    private bool TryHandleWorldBoxPressurePlatePushInterrupts(
        WorldBox worldBox,
        BoxPushDirection direction,
        PhysicalBoxService physicalBoxService)
    {
        if (!ServiceBase.TryGet(out PressurePlateService pressurePlateService))
        {
            return false;
        }

        Bounds playerBounds = GetBounds();
        bool playerOnPlate = pressurePlateService.QueryBoundsOverlapAnyRegisteredPlateXY(playerBounds);
        if (playerOnPlate)
        {
            worldBoxPressureLatchHadPlayerOnAnyPlate = true;
        }

        if (worldBoxPressureLatchHadPlayerOnAnyPlate && !playerOnPlate)
        {
            ApplyWorldBoxPressurePlateInterrupt(worldBox, direction, physicalBoxService, pressurePlateService, snapBoxXOnPlateAfterCancel: false);
            return true;
        }

        bool boxStrictlyOnPlate = pressurePlateService.QueryWorldBoxGridSnapStablePressingAnyRegisteredPlateXY(worldBox);
        if (!worldBoxPrevFrameBoxOnPlate && boxStrictlyOnPlate)
        {
            ApplyWorldBoxPressurePlateInterrupt(worldBox, direction, physicalBoxService, pressurePlateService, snapBoxXOnPlateAfterCancel: true);
            return true;
        }

        worldBoxPrevFrameBoxOnPlate = boxStrictlyOnPlate;
        return false;
    }

    private void ApplyWorldBoxPressurePlateInterrupt(
        WorldBox worldBox,
        BoxPushDirection direction,
        PhysicalBoxService physicalBoxService,
        PressurePlateService pressurePlateService,
        bool snapBoxXOnPlateAfterCancel)
    {
        EndPushSession(cancelLinearPushImmediate: true);
        ResetStallState();
        if (snapBoxXOnPlateAfterCancel)
        {
            pressurePlateService.SnapBoxXToNearestHorizontalGridIfOverlappingAnyPlate(worldBox);
        }

        worldBox.TryTeleportPusherToOuterEntranceForPushInterrupt(direction);
        physicalBoxService.QueueGridAlignmentRelease(worldBox);
    }

    private void ResetStallState()
    {
        pushStallTime = 0f;
        pushStalled = false;
        stalledBox = null;
    }

    private void EndPushSession(bool cancelLinearPushImmediate = false)
    {
        if (activePushBox != null && ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            if (cancelLinearPushImmediate)
            {
                physicalBoxService.CancelLinearPush(activePushBox);
            }
            else
            {
                physicalBoxService.ReleaseLinearPush(activePushBox);
            }
        }

        activePushBox = null;
        activePushCanPush = false;
        worldBoxExitHadInnerBlock = false;
        worldBoxExitPendingPressureLogicalUnblock = false;
        worldBoxPressureLatchHadPlayerOnAnyPlate = false;
        worldBoxPrevFrameBoxOnPlate = false;
    }

    private void ResetAirborneInitState()
    {
        hasAirborneInit = false;
        airborneInitBox = null;
    }

    private void MoveByAndResolve(Vector3 delta)
    {
        if (delta == Vector3.zero)
        {
            return;
        }

        Vector3 position = transform.position + delta;
        position.z = fixedZ;
        MoveTo(position);
        SyncColliderTransforms();
        ResolveTerrainOverlaps(delta);
    }

    private void ResolveTerrainOverlaps(Vector3 movedDelta)
    {
        bool skipHorizontalVelocityStopFromOverlap =
            !contacts.grounded && contacts.leftBlocked && contacts.rightBlocked;

        for (int i = 0; i < MaxOverlapResolveIterations; i++)
        {
            Bounds bounds = GetBounds();
            if (bounds.size == Vector3.zero)
            {
                return;
            }

            if (!TryFindOverlapSeparation(bounds, movedDelta, out Vector3 correction))
            {
                return;
            }

            Vector3 position = transform.position + correction;
            position.z = fixedZ;
            MoveTo(position);
            SyncColliderTransforms();

            if (Mathf.Abs(correction.x) > 0f)
            {
                StopRetainedVelocityForCollision(movedDelta, true);
                if (Mathf.Abs(movedDelta.x) > OverlapResolveEpsilon && !skipHorizontalVelocityStopFromOverlap)
                {
                    baseVelocity.x = 0f;
                    velocity.x = 0f;
                }
            }

            if (Mathf.Abs(correction.y) > 0f)
            {
                StopRetainedVelocityForCollision(movedDelta, false);
                if (Mathf.Abs(movedDelta.y) > OverlapResolveEpsilon)
                {
                    baseVelocity.y = 0f;
                    velocity.y = 0f;
                    jumping = false;
                }
            }
        }
    }

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

    private Vector3 CalculateAabbSeparation(Bounds playerBounds, Bounds otherBounds, Vector3 movedDelta)
    {
        float moveLeft = otherBounds.min.x - playerBounds.max.x - OverlapResolveEpsilon;
        float moveRight = otherBounds.max.x - playerBounds.min.x + OverlapResolveEpsilon;
        float moveDown = otherBounds.min.y - playerBounds.max.y - OverlapResolveEpsilon;
        float moveUp = otherBounds.max.y - playerBounds.min.y + OverlapResolveEpsilon;

        // 每轴内：优先沿 movedDelta 反方向回退（保留"撞墙被推回入侵前"的语义）；
        // 该轴 movedDelta 为 0 时退化为几何上较短的一侧。
        float xCorrection = movedDelta.x > 0f
            ? moveLeft
            : movedDelta.x < 0f
                ? moveRight
                : (Mathf.Abs(moveLeft) < Mathf.Abs(moveRight) ? moveLeft : moveRight);
        float yCorrection = movedDelta.y > 0f
            ? moveDown
            : movedDelta.y < 0f
                ? moveUp
                : (Mathf.Abs(moveDown) < Mathf.Abs(moveUp) ? moveDown : moveUp);

        // 跨轴：取 MTV——分离距离较小的那条轴。
        // 旧实现按 movedDelta 主轴强制选 X 或 Y，会在"头顶 ε overlap + 水平走动"时把 player
        // 沿 X 推出整整一格（"挤出那一格" bug）。改用 MTV 后，Y 轴的 ε 级修正会胜出，
        // 仅产生肉眼不可见的位移；对正常撞墙/落地场景无回归（侵入轴本身就是较短的一侧）。
        return Mathf.Abs(xCorrection) < Mathf.Abs(yCorrection)
            ? new Vector3(xCorrection, 0f, 0f)
            : new Vector3(0f, yCorrection, 0f);
    }

    private static bool OverlapsXY(Bounds a, Bounds b)
    {
        return a.min.x < b.max.x &&
            a.max.x > b.min.x &&
            a.min.y < b.max.y &&
            a.max.y > b.min.y;
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

    private void MoveTo(Vector3 position)
    {
        if (body2D != null)
        {
            body2D.position = (Vector2)position;
            transform.position = position;
        }
        else if (body3D != null)
        {
            body3D.position = position;
            transform.position = position;
        }
        else
        {
            transform.position = position;
        }
    }

    public bool HandlePlayerImpact(SceneMovablePlayerImpactContext context)
    {
        return false;
    }

    public void ApplyExternalVelocity(Vector2 newVelocity)
    {
        BeginRetainedVelocity(newVelocity);
        jumpQueued = false;
        jumping = false;
    }

    /// <summary>
    /// 由外部系统（如 <see cref="PhysicalBoxService"/> 释放过渡）施加一个绝对位移，会保持 fixedZ 并同步刚体位置。
    /// </summary>
    public void ApplyExternalPositionDelta(Vector3 delta)
    {
        if (delta.sqrMagnitude <= Mathf.Epsilon * Mathf.Epsilon)
        {
            return;
        }

        Vector3 newPos = transform.position + delta;
        newPos.z = fixedZ;
        MoveTo(newPos);
        SyncColliderTransforms();
    }

    private void BeginRetainedVelocity(Vector2 newVelocity)
    {
        float xSpeed = Mathf.Abs(newVelocity.x);
        float ySpeed = Mathf.Abs(newVelocity.y);

        if (xSpeed <= retainedVelocityStopSpeed && ySpeed <= retainedVelocityStopSpeed)
        {
            ClearRetainedVelocity();
            return;
        }

        if (xSpeed >= ySpeed)
        {
            retainedVelocityAxis = newVelocity.x >= 0f ? Vector2.right : Vector2.left;
            retainedVelocitySpeed = xSpeed;
        }
        else
        {
            retainedVelocityAxis = newVelocity.y >= 0f ? Vector2.up : Vector2.down;
            retainedVelocitySpeed = ySpeed;
        }

        hasRetainedVelocity = retainedVelocitySpeed > retainedVelocityStopSpeed;
    }

    private Vector2 TickRetainedVelocity(float dt)
    {
        if (!hasRetainedVelocity || IsRetainedVelocityBlocked())
        {
            ClearRetainedVelocity();
            return Vector2.zero;
        }

        retainedVelocitySpeed = Mathf.Max(0f, retainedVelocitySpeed - retainedVelocityDrag * dt);
        if (retainedVelocitySpeed <= retainedVelocityStopSpeed)
        {
            ClearRetainedVelocity();
            return Vector2.zero;
        }

        return GetRetainedVelocity();
    }

    private Vector2 GetRetainedVelocity()
    {
        return hasRetainedVelocity ? retainedVelocityAxis * retainedVelocitySpeed : Vector2.zero;
    }

    private bool IsRetainedVelocityBlocked()
    {
        if (!hasRetainedVelocity)
        {
            return false;
        }

        if (retainedVelocityAxis.x > 0f)
        {
            return contacts.rightBlocked;
        }

        if (retainedVelocityAxis.x < 0f)
        {
            return contacts.leftBlocked;
        }

        if (retainedVelocityAxis.y > 0f)
        {
            return contacts.upBlocked;
        }

        if (retainedVelocityAxis.y < 0f)
        {
            return contacts.downBlocked;
        }

        return false;
    }

    private void StopRetainedVelocityForCollision(Vector3 movedDelta, bool horizontal)
    {
        if (!hasRetainedVelocity)
        {
            return;
        }

        bool retainedIsHorizontal = Mathf.Abs(retainedVelocityAxis.x) > 0f;
        if (retainedIsHorizontal != horizontal)
        {
            return;
        }

        float movedAxis = horizontal ? movedDelta.x : movedDelta.y;
        float retainedAxis = horizontal ? retainedVelocityAxis.x : retainedVelocityAxis.y;
        if (movedAxis * retainedAxis > 0f)
        {
            ClearRetainedVelocity();
        }
    }

    private void ClearRetainedVelocity()
    {
        hasRetainedVelocity = false;
        retainedVelocityAxis = Vector2.zero;
        retainedVelocitySpeed = 0f;
    }

    private void SyncColliderTransforms()
    {
        Physics.SyncTransforms();
        Physics2D.SyncTransforms();
    }

    private readonly struct RayHit
    {
        public readonly float distance;
        public readonly string tag;
        public readonly StandardBox box;
        public readonly GameObject platform;

        public RayHit(float distance, string tag, StandardBox box, GameObject platform)
        {
            this.distance = distance;
            this.tag = tag;
            this.box = box;
            this.platform = platform;
        }
    }
}
