using System.Collections.Generic;
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

    [Header("Push")]
    [SerializeField, Min(0.05f), Tooltip("线性推动期间玩家水平移动速度相对 moveSpeed 的倍率；当 PhysicalBoxService 暴露 LinearPushSpeed 时优先使用其值。")]
    private float pushSpeedMultiplier = 0.4f;

    public Grid Grid => grid;
    public ContactState Contacts => contacts;
    public Vector2 Velocity => velocity;
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
        if (MovementInputDisabled)
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
        contacts.grounded = contacts.downBlocked;
        contacts.downTag = downHit.tag;
        contacts.upTag = upHit.tag;
        contacts.leftTag = leftHit.tag;
        contacts.rightTag = rightHit.tag;
        downPlatform = downHit.platform;
        upBox = upHit.box;
        downBox = downHit.box;
        leftBox = leftHit.box;
        rightBox = rightHit.box;
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
            return;
        }

        // 切换箱子或换方向：先把旧会话释放（触发 50% 规则收尾），再 InitializePush 新会话。
        if (activePushBox != box || activePushDirection != direction)
        {
            EndPushSession();
            bool canPush = box.InitializePush(direction, gameObject).CanPush;
            if (!canPush)
            {
                // !CanPush 由 WorldBox 等监听者处理（例如把玩家瞬移到外入口）；本地不进入线性会话。
                return;
            }

            activePushBox = box;
            activePushDirection = direction;
            activePushCanPush = true;
        }

        if (!activePushCanPush)
        {
            return;
        }

        if (!ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            return;
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
        delta.x = clamped;
        baseVelocity.x = clamped / Mathf.Max(dt, Mathf.Epsilon);
        velocity.x = baseVelocity.x;
    }

    private void EndPushSession()
    {
        if (activePushBox != null && ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            physicalBoxService.ReleaseLinearPush(activePushBox);
        }

        activePushBox = null;
        activePushCanPush = false;
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
                baseVelocity.x = 0f;
                velocity.x = 0f;
            }

            if (Mathf.Abs(correction.y) > 0f)
            {
                StopRetainedVelocityForCollision(movedDelta, false);
                baseVelocity.y = 0f;
                velocity.y = 0f;
                jumping = false;
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
