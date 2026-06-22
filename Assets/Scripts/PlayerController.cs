using System;
using System.Collections;
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
    [Header("Center")]
    public Vector3 CenterOffset;
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 4f;
    [SerializeField, Min(0.01f)] private float jumpGridHeight = 2.5f;
    [SerializeField] private float gravity = 28f;
    [SerializeField] private float maxFallSpeed = 18f;
    [SerializeField, Min(0f)] private float retainedVelocityDrag = 18f;
    [SerializeField, Min(0f)] private float retainedVelocityStopSpeed = 0.05f;
    [SerializeField, Min(0f)]
    private float coyoteTime = 0.08f;

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
    public bool IsPushing => m_CachedIsPushing;

    /// <summary>
    /// 玩家是否处于"被砸死"过渡：动画/物理层据此完全冻结玩家位置并切换到 crashed 动画。
    /// 由 <see cref="PlayerService"/> 维护，新玩家注册时清零。
    /// </summary>
    public bool IsDying => m_CachedIsDying;

    /// <summary>
    /// 当玩家输入被全局服务或自身动画阻止时返回 true。不再支持外部直接设置，应使用 <see cref="PlayerService.RetainDisableMovementInput"/>。
    /// </summary>
    public bool MovementInputDisabled => m_PlayerService != null && m_PlayerService.IsMovementInputDisabled;

    private Grid grid;
    private Rigidbody body3D;
    private Rigidbody2D body2D;
    private Collider m_Collider3D;
    private Collider2D m_Collider2D;
    private ColliderSceneMovableBoundsProvider sceneMovableBoundsProvider;
    private ContactState contacts;
    private Vector2 moveInput;
    private StandardBox leftBox;
    private StandardBox rightBox;
    private StandardBox upBox;
    private StandardBox downBox;

    private IUnRegister pressurePlateStateUnRegister;
    private PlayerService m_PlayerService;
    private PhysicalBoxService m_PhysicalBoxService;
    private bool m_CachedIsPushing;
    private bool m_CachedIsDying;

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
        ServiceBase.TryGet(out m_PlayerService);
        ServiceBase.TryGet(out m_PhysicalBoxService);
        m_PlayerService ??= ServiceBase.Get<PlayerService>();
        m_PlayerService?.Register(this);
        m_PhysicalBoxService ??= ServiceBase.Get<PhysicalBoxService>();
        ServiceBase.Get<SceneMovableInteractionService>()?.Register(this);
        pressurePlateStateUnRegister = TypeEventSystem.Global.Register<PressurePlateStateEvent>(OnPressurePlateState);
        RefreshCachedMovementStateFlags();

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
        PersistWorldPlayerPositionIfLeavingWorld();

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
        AudioMng.Instance.Player = transform;
        grid = FindSceneGrid();
        fixedZ = transform.position.z;

        string sceneName = gameObject.scene.name;
        if (GameManager.IsWorldHubScene(sceneName))
        {
            var world = FindWorldData(sceneName);
            if (world != null)
            {
                m_CurrentWorldIndex = world.Index;
                Save bootSave = new Save().DeSerialize<Save>();
                if (bootSave.WorldPlayerLastPositions != null && bootSave.WorldPlayerLastPositions.ContainsKey(m_CurrentWorldIndex))
                {
                    StartCoroutine(RestoreWorldPlayerPositionDeferred());
                    return;
                }
            }
        }

        if (grid == null)
        {
            return;
        }

        Vector3Int cell = grid.WorldToCell(transform.position);
        Vector3 snapped = grid.CellToWorld(cell) + Vector3.Scale(grid.cellSize, cellOffset);
        snapped.z = fixedZ;
        MoveTo(snapped);
    }

    private void OnApplicationQuit()
    {
        PersistWorldPlayerPositionIfLeavingWorld();
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

    private void Update()
    {
        RefreshCachedMovementStateFlags();

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
        RefreshCachedMovementStateFlags();

        if (IsDying)
        {
            return;
        }

        RefreshContacts();
        ProcessPlatformDropRequest();

        float dt = Time.fixedDeltaTime;
        if (contacts.grounded)
        {
            coyoteTimeLeft = coyoteTime;
        }
        else
        {
            coyoteTimeLeft = Mathf.Max(0f, coyoteTimeLeft - dt);
        }
        baseVelocity.x = Mathf.Clamp(moveInput.x, -1f, 1f) * moveSpeed;

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

        if (jumpQueued && (contacts.grounded || coyoteTimeLeft > 0f))
        {
            float height = Mathf.Max(0.01f, GetCellHeight() * jumpGridHeight);
            baseVelocity.y = Mathf.Sqrt(2f * gravity * height);
            jumpApexY = transform.position.y + height;
            jumping = true;
            coyoteTimeLeft = 0f;
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

        HandleBoxPush(dt, ref delta);

        MoveByAndResolve(new Vector3(delta.x, 0f, 0f));
        MoveByAndResolve(new Vector3(0f, delta.y, 0f));
        RefreshContacts();
        HandleWorldBoxUpPush(delta.y);
        RefreshCachedMovementStateFlags();
    }

    private void RefreshCachedMovementStateFlags()
    {
        m_CachedIsDying = m_PlayerService != null && m_PlayerService.IsDying && m_PlayerService.Player == this;

        if (activePushBox != null && activePushCanPush)
        {
            m_CachedIsPushing = true;
            return;
        }

        m_CachedIsPushing =
            m_PhysicalBoxService != null && m_PhysicalBoxService.IsPusherInRelease(gameObject);
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
