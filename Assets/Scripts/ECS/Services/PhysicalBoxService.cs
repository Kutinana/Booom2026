using System.Collections.Generic;
using QFramework;
using UnityEngine;

public class PhysicalBoxService : ServiceBase<StandardBox>
{
    private const float PushContactTolerance = 0.001f;

    [SerializeField] private float gravity = 28f;
    [SerializeField] private float maxFallSpeed = 18f;
    [SerializeField] private float skinWidth = 0.02f;
    [SerializeField, Min(2)] private int raysPerSide = 3;
    [SerializeField, Min(0f)] private float impactContactTolerance = 0.04f;
    [SerializeField, Min(0f)] private float minPlayerImpactSpeed = 0.1f;
    [SerializeField, Range(0f, 1f)] private float minPlayerImpactFaceOverlapFraction = 0.25f;

    [Header("Linear Push")]
    [SerializeField, Min(0.01f)] private float linearPushSpeed = 1.6f;

    public float LinearPushSpeed => linearPushSpeed;

    private readonly Dictionary<StandardBox, float> fallSpeeds = new Dictionary<StandardBox, float>();
    private readonly Dictionary<StandardBox, LinearPushState> linearPushes = new Dictionary<StandardBox, LinearPushState>();
    private readonly List<StandardBox> linearPushScratch = new List<StandardBox>(8);
    private readonly RaycastHit2D[] hits2D = new RaycastHit2D[8];
    private readonly RaycastHit[] hits3D = new RaycastHit[8];

    // 纵向堆叠联动（推下方箱体时上方紧贴的箱体一并平移）的复用 buffer。
    // StackContactEpsilon：判定"紧贴"的最大允许竖向间隙；StackHorizontalOverlapRatio：要求的最小 X 重叠占比（取较小者半宽）。
    private readonly List<StandardBox> stackGroupScratch = new List<StandardBox>(8);
    private readonly Collider2D[] stackOverlapHits2D = new Collider2D[16];
    private readonly Collider[] stackOverlapHits3D = new Collider[16];
    private const float StackContactEpsilon = 0.04f;
    private const float StackHorizontalOverlapRatio = 0.5f;

    private IUnRegister pushAttemptUnRegister;

    protected override void Awake()
    {
        base.Awake();
        if (!IsActiveService)
        {
            return;
        }

        pushAttemptUnRegister = RegisterEvent<BoxPushAttemptEvent>(OnPushAttempted);
    }

    protected override void OnDestroy()
    {
        pushAttemptUnRegister?.UnRegister();
        pushAttemptUnRegister = null;
        fallSpeeds.Clear();
        linearPushes.Clear();
        base.OnDestroy();
    }

    public override void Register(StandardBox component)
    {
        base.Register(component);
        if (component != null && !fallSpeeds.ContainsKey(component))
        {
            fallSpeeds.Add(component, 0f);
        }
    }

    public override void UnRegister(StandardBox component)
    {
        base.UnRegister(component);
        if (component != null)
        {
            fallSpeeds.Remove(component);
            linearPushes.Remove(component);
        }
    }

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        foreach (StandardBox box in RegisteredComponents)
        {
            if (box == null)
            {
                continue;
            }

            SimulateFall(box, dt);
        }

        UpdateLinearPushTransitions(dt);
    }

    private void OnPushAttempted(BoxPushAttemptEvent e)
    {
        if (!e.CanPush || e.Box == null || !IsRegistered(e.Box))
        {
            return;
        }

        // 处于线性推动会话中（无论是玩家驱动还是释放过渡），由 TryAdvanceLinearPush / UpdateLinearPushTransitions 直接驱动；
        // 不再走整格瞬移，避免与线性推动相互覆盖。
        if (linearPushes.TryGetValue(e.Box, out LinearPushState existing) && existing.Active)
        {
            return;
        }

        TryApplyPush(e.Box, e.Direction);
    }

    private bool TryApplyPush(StandardBox box, BoxPushDirection direction)
    {
        Vector3 from = box.transform.position;

        if (direction == BoxPushDirection.Up || direction == BoxPushDirection.Down)
        {
            SendEvent(new BoxPhysicalPushEvent(box, direction, false, true, from, from));
            return false;
        }

        Vector3 axis = direction == BoxPushDirection.Right ? Vector3.right : Vector3.left;
        float distance = GetCellDistance(box, axis);

        float allowedContactDistance = distance + skinWidth;
        if (Cast(box, axis, allowedContactDistance, out RayHit hit) && hit.Distance < allowedContactDistance - PushContactTolerance)
        {
            if (hit.Player != null)
            {
                TryHandlePlayerImpact(box, from, from + axis * distance, Time.fixedDeltaTime);
            }

            SendEvent(new BoxPhysicalPushEvent(box, direction, false, false, from, from));
            return false;
        }

        Vector3 to = from + axis * distance;
        Grid grid = box.Grid;
        if (grid != null)
        {
            Vector3Int cell = grid.WorldToCell(to);
            to = grid.CellToWorld(cell) + Vector3.Scale(grid.cellSize, box.CellOffset);
            to.y = from.y;
            to.z = from.z;
        }

        TryHandlePlayerImpact(box, from, to, dt: Time.fixedDeltaTime);
        box.MoveTo(to);
        SendEvent(new BoxPhysicalPushEvent(box, direction, true, false, from, to));
        return true;
    }

    private void SimulateFall(StandardBox box, float dt)
    {
        if (IsGrounded(box))
        {
            bool wasFalling = fallSpeeds.TryGetValue(box, out float oldSpeed) && oldSpeed > 0f;
            fallSpeeds[box] = 0f;
            if (wasFalling)
            {
                SendEvent(new BoxFallStateEvent(box, false, true, box.transform.position));
            }

            return;
        }

        float fallSpeed = 0f;
        fallSpeeds.TryGetValue(box, out fallSpeed);
        fallSpeed = Mathf.Min(maxFallSpeed, fallSpeed + gravity * dt);
        fallSpeeds[box] = fallSpeed;

        float distance = fallSpeed * dt;
        Vector3 from = box.transform.position;
        float resolved = ResolveVertical(box, distance, out RayHit verticalHit);
        Vector3 to = from + Vector3.down * resolved;
        if (verticalHit.Player != null)
        {
            TryHandlePlayerImpact(box, from, from + Vector3.down * distance, dt);

            // 致死的那一帧：impact 已经把 PlayerService.IsDying 翻成 true（事件是同步派发的），
            // 此时 Cast 会过滤掉 player，重新 resolve 一遍可以让箱子在死亡帧本身就开始穿过 player
            // 继续下落，而不是被 clamp 到 player 头顶停一帧。这样"砸落速度不变"才贯通：
            // 撞死 player → 同帧穿过 → 受重力继续加速 → 一路落到真实地面。
            if (ServiceBase.TryGet(out PlayerService deathService) && deathService.IsDying)
            {
                resolved = ResolveVertical(box, distance, out _);
                to = from + Vector3.down * resolved;
            }
        }
        else
        {
            TryHandlePlayerImpact(box, from, to, dt);
        }

        box.MoveTo(to);

        bool landed = resolved < distance;
        if (landed)
        {
            fallSpeeds[box] = 0f;
        }

        SendEvent(new BoxFallStateEvent(box, !landed, landed, box.transform.position));
    }

    private bool IsGrounded(StandardBox box)
    {
        return Cast(box, Vector3.down, skinWidth * 2f, out RayHit hit);
    }

    private float ResolveVertical(StandardBox box, float distance, out RayHit hit)
    {
        hit = default;
        if (distance <= 0f)
        {
            return 0f;
        }

        if (Cast(box, Vector3.down, distance + skinWidth, out hit))
        {
            return Mathf.Max(0f, hit.Distance - skinWidth);
        }

        return distance;
    }

    private bool TryHandlePlayerImpact(StandardBox box, Vector3 from, Vector3 to, float dt)
    {
        Vector3 delta = to - from;
        if (box == null || delta == Vector3.zero)
        {
            return false;
        }

        if (!ServiceBase.TryGet(out PlayerService playerService) || playerService.Player == null)
        {
            return false;
        }

        // 死亡过渡中：跳过 impact 处理，避免在 reload 期间被多个箱子重复砸。
        if (playerService.IsDying)
        {
            return false;
        }

        PlayerController player = playerService.Player;
        if (box.Owner == player.gameObject)
        {
            return false;
        }

        Bounds previousItemBounds = box.Bounds;
        ISceneMovableBoundsProvider playerBoundsProvider = player.BoundsProvider;
        if (previousItemBounds.size == Vector3.zero || playerBoundsProvider == null || !playerBoundsProvider.IsValid)
        {
            return false;
        }

        Bounds playerBounds = playerBoundsProvider.Bounds;
        if (playerBounds.size == Vector3.zero)
        {
            return false;
        }

        if (!TryGetSweptImpactFace(previousItemBounds, playerBounds, (Vector2)delta, out BoxPushDirection impactFace))
        {
            return false;
        }

        Vector2 itemVelocity = (Vector2)delta / Mathf.Max(dt, Mathf.Epsilon);
        if (!IsActivePlayerImpact(previousItemBounds, playerBounds, itemVelocity, impactFace))
        {
            return false;
        }

        Bounds itemBounds = previousItemBounds;
        itemBounds.center += delta;
        Vector2 playerVelocity = player.Velocity;
        SceneMovablePlayerImpactContext context = new SceneMovablePlayerImpactContext(
            box,
            player,
            impactFace,
            itemVelocity - playerVelocity,
            itemVelocity,
            playerVelocity,
            itemBounds,
            playerBounds);

        bool handled = box.HandlePlayerImpact(context);
        SendEvent(new SceneMovablePlayerImpactEvent(context, handled));
        if (ServiceBase.TryGet(out SceneMovableInteractionService sceneMovableService))
        {
            sceneMovableService.ApplyImpactCooldown(box);
        }

        return handled;
    }

    private bool TryGetSweptImpactFace(Bounds previousItem, Bounds playerBounds, Vector2 itemDelta, out BoxPushDirection face)
    {
        face = default;
        Bounds playerTarget = playerBounds;
        playerTarget.Expand(impactContactTolerance * 2f);

        if (OverlapsXY(previousItem, playerTarget))
        {
            if (Mathf.Abs(itemDelta.x) >= Mathf.Abs(itemDelta.y))
            {
                if (Mathf.Abs(itemDelta.x) <= PushContactTolerance)
                {
                    return false;
                }

                face = GetOverlappingImpactFace(previousItem, playerBounds, true, itemDelta.x);
                return true;
            }

            if (Mathf.Abs(itemDelta.y) <= PushContactTolerance)
            {
                return false;
            }

            face = GetOverlappingImpactFace(previousItem, playerBounds, false, itemDelta.y);
            return true;
        }

        float xEntry;
        float xExit;
        if (itemDelta.x > PushContactTolerance)
        {
            xEntry = (playerTarget.min.x - previousItem.max.x) / itemDelta.x;
            xExit = (playerTarget.max.x - previousItem.min.x) / itemDelta.x;
        }
        else if (itemDelta.x < -PushContactTolerance)
        {
            xEntry = (playerTarget.max.x - previousItem.min.x) / itemDelta.x;
            xExit = (playerTarget.min.x - previousItem.max.x) / itemDelta.x;
        }
        else
        {
            if (previousItem.max.x < playerTarget.min.x || previousItem.min.x > playerTarget.max.x)
            {
                return false;
            }

            xEntry = float.NegativeInfinity;
            xExit = float.PositiveInfinity;
        }

        float yEntry;
        float yExit;
        if (itemDelta.y > PushContactTolerance)
        {
            yEntry = (playerTarget.min.y - previousItem.max.y) / itemDelta.y;
            yExit = (playerTarget.max.y - previousItem.min.y) / itemDelta.y;
        }
        else if (itemDelta.y < -PushContactTolerance)
        {
            yEntry = (playerTarget.max.y - previousItem.min.y) / itemDelta.y;
            yExit = (playerTarget.min.y - previousItem.max.y) / itemDelta.y;
        }
        else
        {
            if (previousItem.max.y < playerTarget.min.y || previousItem.min.y > playerTarget.max.y)
            {
                return false;
            }

            yEntry = float.NegativeInfinity;
            yExit = float.PositiveInfinity;
        }

        float entryTime = Mathf.Max(xEntry, yEntry);
        float exitTime = Mathf.Min(xExit, yExit);
        if (entryTime > exitTime || entryTime < 0f || entryTime > 1f)
        {
            return false;
        }

        if (xEntry > yEntry)
        {
            face = itemDelta.x > 0f ? BoxPushDirection.Right : BoxPushDirection.Left;
        }
        else
        {
            face = itemDelta.y > 0f ? BoxPushDirection.Up : BoxPushDirection.Down;
        }

        return true;
    }

    private static BoxPushDirection GetOverlappingImpactFace(Bounds previousItem, Bounds playerBounds, bool horizontalAxis, float itemDelta)
    {
        if (horizontalAxis)
        {
            if (Mathf.Abs(previousItem.center.x - playerBounds.center.x) > PushContactTolerance)
            {
                return previousItem.center.x < playerBounds.center.x ? BoxPushDirection.Right : BoxPushDirection.Left;
            }

            return itemDelta > 0f ? BoxPushDirection.Right : BoxPushDirection.Left;
        }

        if (Mathf.Abs(previousItem.center.y - playerBounds.center.y) > PushContactTolerance)
        {
            return previousItem.center.y < playerBounds.center.y ? BoxPushDirection.Up : BoxPushDirection.Down;
        }

        return itemDelta > 0f ? BoxPushDirection.Up : BoxPushDirection.Down;
    }

    private bool IsActivePlayerImpact(Bounds itemBounds, Bounds playerBounds, Vector2 itemVelocity, BoxPushDirection impactFace)
    {
        return GetImpactFaceSpeed(itemVelocity, impactFace) >= minPlayerImpactSpeed &&
            HasMinimumImpactFaceOverlap(itemBounds, playerBounds, impactFace);
    }

    private static float GetImpactFaceSpeed(Vector2 itemVelocity, BoxPushDirection impactFace)
    {
        switch (impactFace)
        {
            case BoxPushDirection.Left:
                return -itemVelocity.x;
            case BoxPushDirection.Right:
                return itemVelocity.x;
            case BoxPushDirection.Up:
                return itemVelocity.y;
            case BoxPushDirection.Down:
                return -itemVelocity.y;
            default:
                return 0f;
        }
    }

    private bool HasMinimumImpactFaceOverlap(Bounds itemBounds, Bounds playerBounds, BoxPushDirection impactFace)
    {
        bool verticalFace = impactFace == BoxPushDirection.Up || impactFace == BoxPushDirection.Down;
        float overlap = verticalFace ? GetHorizontalOverlap(itemBounds, playerBounds) : GetVerticalOverlap(itemBounds, playerBounds);
        float itemSpan = verticalFace ? itemBounds.size.x : itemBounds.size.y;
        float playerSpan = verticalFace ? playerBounds.size.x : playerBounds.size.y;
        float required = Mathf.Max(PushContactTolerance, Mathf.Min(itemSpan, playerSpan) * minPlayerImpactFaceOverlapFraction);
        return overlap >= required;
    }

    private static float GetHorizontalOverlap(Bounds a, Bounds b)
    {
        return Mathf.Max(0f, Mathf.Min(a.max.x, b.max.x) - Mathf.Max(a.min.x, b.min.x));
    }

    private static float GetVerticalOverlap(Bounds a, Bounds b)
    {
        return Mathf.Max(0f, Mathf.Min(a.max.y, b.max.y) - Mathf.Max(a.min.y, b.min.y));
    }

    private static bool OverlapsXY(Bounds a, Bounds b)
    {
        return a.min.x < b.max.x &&
            a.max.x > b.min.x &&
            a.min.y < b.max.y &&
            a.max.y > b.min.y;
    }

    private bool Cast(StandardBox box, Vector3 direction, float distance, out RayHit bestHit)
    {
        bestHit = default;
        Bounds bounds = box.Bounds;
        if (bounds.size == Vector3.zero)
        {
            return false;
        }

        bounds.Expand(-skinWidth * 2f);

        bool vertical = Mathf.Abs(direction.y) > 0f;
        int count = Mathf.Max(2, raysPerSide);
        float bestDistance = float.PositiveInfinity;
        bool hitAny = false;

        for (int i = 0; i < count; i++)
        {
            float t = count == 1 ? 0.5f : i / (float)(count - 1);
            Vector3 origin;

            if (vertical)
            {
                origin = new Vector3(Mathf.Lerp(bounds.min.x, bounds.max.x, t), direction.y > 0f ? bounds.max.y : bounds.min.y, box.transform.position.z);
            }
            else
            {
                origin = new Vector3(direction.x > 0f ? bounds.max.x : bounds.min.x, Mathf.Lerp(bounds.min.y, bounds.max.y, t), box.transform.position.z);
            }

            if (CastSingle(box, origin, direction, distance, out RayHit hit) && hit.Distance < bestDistance)
            {
                bestDistance = hit.Distance;
                bestHit = hit;
                hitAny = true;
            }
        }

        return hitAny;
    }

    private bool CastSingle(StandardBox box, Vector3 origin, Vector3 direction, float distance, out RayHit hit)
    {
        float bestDistance = float.PositiveInfinity;
        hit = default;

        // 死亡过渡中：让箱子的 cast 看不到当前 player，使 SimulateFall 不再被 player 头顶卡住，
        // 自然继续受重力加速、穿过 player、直到落到真实地面，匹配"砸落速度不变"的需求。
        GameObject ignoredPlayer = TryGetDyingPlayerGameObject();

        Collider2D boxCollider2D = box.Collider2D;
        if (boxCollider2D != null)
        {
            int hitCount = Physics2D.RaycastNonAlloc((Vector2)origin, (Vector2)direction, hits2D, distance, box.CollisionMask);
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit2D hit2D = hits2D[i];
                if (hit2D.collider == null || hit2D.collider.isTrigger || hit2D.collider == boxCollider2D)
                {
                    continue;
                }

                PlayerController hitPlayer = hit2D.collider.GetComponentInParent<PlayerController>();
                if (ignoredPlayer != null && hitPlayer != null && hitPlayer.gameObject == ignoredPlayer)
                {
                    continue;
                }

                if (hit2D.distance < bestDistance)
                {
                    bestDistance = hit2D.distance;
                    hit = new RayHit(bestDistance, hitPlayer);
                }
            }
        }

        Collider boxCollider3D = box.Collider3D;
        if (boxCollider3D != null)
        {
            int hitCount = Physics.RaycastNonAlloc(origin, direction, hits3D, distance, box.CollisionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit3D = hits3D[i];
                if (hit3D.collider == null || hit3D.collider == boxCollider3D)
                {
                    continue;
                }

                PlayerController hitPlayer = hit3D.collider.GetComponentInParent<PlayerController>();
                if (ignoredPlayer != null && hitPlayer != null && hitPlayer.gameObject == ignoredPlayer)
                {
                    continue;
                }

                if (hit3D.distance < bestDistance)
                {
                    bestDistance = hit3D.distance;
                    hit = new RayHit(bestDistance, hitPlayer);
                }
            }
        }

        return bestDistance < float.PositiveInfinity;
    }

    private GameObject TryGetDyingPlayerGameObject()
    {
        if (ServiceBase.TryGet(out PlayerService playerService) && playerService.IsDying && playerService.Player != null)
        {
            return playerService.Player.gameObject;
        }

        return null;
    }

    /// <summary>
    /// 从 root 出发，BFS 向上收集"被 root（或 root 间接支撑的箱体）"竖直支撑的堆叠成员。
    /// 不含 root 自身；输出顺序为底→顶（每一层先于上一层）。
    /// 判定规则：
    ///   - 紧贴：上方 box.min.y 与下方 box.max.y 之间的差 ≤ StackContactEpsilon；
    ///   - X overlap：两 box 在 X 上的重叠 > min(width) * StackHorizontalOverlapRatio。
    /// </summary>
    private void CollectVerticalStack(StandardBox root, List<StandardBox> outGroup)
    {
        outGroup.Clear();
        if (root == null)
        {
            return;
        }

        // 广度优先：处理 root 与已加入 outGroup 的成员，依次为每个成员追加其顶面紧贴的更上层箱体。
        AppendStackedAbove(root, outGroup);
        for (int i = 0; i < outGroup.Count; i++)
        {
            AppendStackedAbove(outGroup[i], outGroup);
        }
    }

    private void AppendStackedAbove(StandardBox below, List<StandardBox> outGroup)
    {
        if (below == null)
        {
            return;
        }

        Bounds belowBounds = below.Bounds;
        if (belowBounds.size == Vector3.zero)
        {
            return;
        }

        float top = belowBounds.max.y;

        Collider2D below2D = below.Collider2D;
        if (below2D != null)
        {
            // 在 below 顶面正上方一条 epsilon 厚的薄带里 OverlapBox：捕获所有底面与 below 顶面紧贴的候选 box。
            Vector2 stripCenter = new Vector2(belowBounds.center.x, top + StackContactEpsilon * 0.5f);
            Vector2 stripSize = new Vector2(belowBounds.size.x + StackContactEpsilon, StackContactEpsilon);
            int hitCount = Physics2D.OverlapBoxNonAlloc(stripCenter, stripSize, 0f, stackOverlapHits2D, below.CollisionMask);

            for (int i = 0; i < hitCount; i++)
            {
                Collider2D col = stackOverlapHits2D[i];
                if (col == null || col.isTrigger)
                {
                    continue;
                }

                StandardBox stacked = col.GetComponentInParent<StandardBox>();
                if (!IsStackCandidate(stacked, below, top, outGroup))
                {
                    continue;
                }

                outGroup.Add(stacked);
            }

            return;
        }

        Collider below3D = below.Collider3D;
        if (below3D != null)
        {
            Vector3 stripCenter = new Vector3(belowBounds.center.x, top + StackContactEpsilon * 0.5f, belowBounds.center.z);
            Vector3 stripHalfExtents = new Vector3((belowBounds.size.x + StackContactEpsilon) * 0.5f, StackContactEpsilon * 0.5f, Mathf.Max(belowBounds.size.z, StackContactEpsilon) * 0.5f);
            int hitCount = Physics.OverlapBoxNonAlloc(stripCenter, stripHalfExtents, stackOverlapHits3D, Quaternion.identity, below.CollisionMask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hitCount; i++)
            {
                Collider col = stackOverlapHits3D[i];
                if (col == null)
                {
                    continue;
                }

                StandardBox stacked = col.GetComponentInParent<StandardBox>();
                if (!IsStackCandidate(stacked, below, top, outGroup))
                {
                    continue;
                }

                outGroup.Add(stacked);
            }
        }
    }

    /// <summary>
    /// 把 root 的水平位移 (axis * magnitude) 同步到给定堆叠组成员；按 Q3=lower_only 处理：
    /// 成员能走多少走多少，撞墙就停在 cast 距离处与 root 脱节。撞 player 时按 Q4 触发 impact。
    /// </summary>
    private void ApplyStackedFollow(List<StandardBox> stackGroup, Vector3 axis, float magnitude, float dt)
    {
        if (stackGroup == null || stackGroup.Count == 0 || magnitude <= 0f)
        {
            return;
        }

        for (int i = 0; i < stackGroup.Count; i++)
        {
            StandardBox stacked = stackGroup[i];
            if (stacked == null)
            {
                continue;
            }

            Vector3 stackedFrom = stacked.transform.position;
            float stackedMagnitude = magnitude;

            if (Cast(stacked, axis, stackedMagnitude + skinWidth, out RayHit stackedHit))
            {
                // 撞 player：先派发 impact（与 SimulateFall 撞 player 一致地传"未 clamp 的目标位置"，
                // 让 impact 能感知"如果没有 player，箱子本应走多远"），随后将 stacked 限制到 cast 距离处。
                if (stackedHit.Player != null)
                {
                    TryHandlePlayerImpact(stacked, stackedFrom, stackedFrom + axis * stackedMagnitude, dt);
                }

                stackedMagnitude = Mathf.Max(0f, stackedHit.Distance - skinWidth);
            }

            if (stackedMagnitude > 0f)
            {
                stacked.MoveTo(stackedFrom + axis * stackedMagnitude);
            }
        }
    }

    private bool IsStackCandidate(StandardBox stacked, StandardBox below, float belowTop, List<StandardBox> outGroup)
    {
        if (stacked == null || stacked == below)
        {
            return false;
        }

        if (outGroup.Contains(stacked))
        {
            return false;
        }

        Bounds stackedBounds = stacked.Bounds;
        if (stackedBounds.size == Vector3.zero)
        {
            return false;
        }

        // Q1：紧贴——stacked 底面与 below 顶面之差必须 ≤ epsilon（同时排除明显穿插）。
        float gap = stackedBounds.min.y - belowTop;
        if (gap < -StackContactEpsilon || gap > StackContactEpsilon)
        {
            return false;
        }

        // Q2：X 重叠 > 较窄者半宽。box 都贴 grid 的前提下，正常堆叠 → overlap = full width，正好半格错位 → overlap = halfWidth（< 严格不通过）。
        float xOverlap = Mathf.Min(stackedBounds.max.x, below.Bounds.max.x) - Mathf.Max(stackedBounds.min.x, below.Bounds.min.x);
        float minWidth = Mathf.Min(stackedBounds.size.x, below.Bounds.size.x);
        if (xOverlap <= minWidth * StackHorizontalOverlapRatio)
        {
            return false;
        }

        // 已经有自己 active linear push state 的 box 不参与堆叠跟随，避免被 root 推动与自身 release/advance
        // 同帧双重驱动导致位置抖动。罕见情况（成员还有遗留 release 没收尾就被推），让它走自己的状态收完。
        if (linearPushes.TryGetValue(stacked, out LinearPushState memberState) && memberState.Active)
        {
            return false;
        }

        return true;
    }

    private float GetCellDistance(StandardBox box, Vector3 axis)
    {
        Grid grid = box.Grid;
        if (grid == null)
        {
            return 1f;
        }

        return Mathf.Max(0.01f, Mathf.Abs(axis.x) > 0f ? grid.cellSize.x : grid.cellSize.y);
    }

    // ===== 线性推动 API =====

    /// <summary>
    /// 玩家在 FixedUpdate 中驱动一次线性推动位移；返回箱子（与玩家）实际可推动的轴向位移量。
    /// </summary>
    public float TryAdvanceLinearPush(StandardBox box, BoxPushDirection direction, float requestedDelta, GameObject pusher)
    {
        if (box == null || !IsRegistered(box))
        {
            return 0f;
        }

        if (direction != BoxPushDirection.Left && direction != BoxPushDirection.Right)
        {
            return 0f;
        }

        if (Mathf.Abs(requestedDelta) <= Mathf.Epsilon)
        {
            return 0f;
        }

        Vector3 axis = direction == BoxPushDirection.Right ? Vector3.right : Vector3.left;
        float sign = direction == BoxPushDirection.Right ? 1f : -1f;

        // 防御：传入与方向不一致的位移，按 0 处理。
        if (requestedDelta * sign < 0f)
        {
            return 0f;
        }

        if (!linearPushes.TryGetValue(box, out LinearPushState state))
        {
            state = default;
        }

        bool needReset = !state.Active || state.Direction != direction || state.ReleaseTransition;
        if (needReset)
        {
            Vector3 currentPos = box.transform.position;

            // 若上一会话还在释放过渡中，先把箱子直接对齐到目标格，保证新原点是网格对齐的；
            // 否则后续 50% 阈值检测会基于一个非对齐的原点，最终位置也无法对齐。
            if (state.ReleaseTransition)
            {
                Vector3 snap = state.ReleaseTargetPosition;
                snap.y = currentPos.y;
                snap.z = currentPos.z;
                box.MoveTo(snap);
                currentPos = snap;
            }

            state = new LinearPushState
            {
                Active = true,
                Direction = direction,
                OriginCellPosition = currentPos,
                ReleaseTransition = false,
                ReleaseTargetPosition = currentPos,
                AdvancedThisSession = false,
                Pusher = pusher,
                MovePusherWithBox = false
            };
        }
        else
        {
            state.Pusher = pusher;
        }

        Vector3 from = box.transform.position;
        float magnitude = Mathf.Abs(requestedDelta);
        if (Cast(box, axis, magnitude + skinWidth, out RayHit hit))
        {
            magnitude = Mathf.Max(0f, hit.Distance - skinWidth);
        }

        if (magnitude <= 0f)
        {
            linearPushes[box] = state;
            return 0f;
        }

        // 纵向堆叠联动：在 root 实际移动之前先固化堆叠关系——root 一旦移走，顶面就不再
        // 紧贴上方 box，AppendStackedAbove 的紧贴判定就会失效。先快照成员列表，再各自移动。
        CollectVerticalStack(box, stackGroupScratch);

        Vector3 to = from + axis * magnitude;
        box.MoveTo(to);

        // Q3=lower_only：成员能走多少走多少，撞墙就停（与 root 脱节，下一帧自然下落）；
        // Q4：撞 player 触发 impact（路径与 root 自身一致）。
        ApplyStackedFollow(stackGroupScratch, axis, magnitude, Time.fixedDeltaTime);

        // 越过 50% 阈值则推进原点；允许一次跨越多次（防止 dt 过大）。
        float cellSize = GetCellDistance(box, axis);
        float halfCell = cellSize * 0.5f;
        while (true)
        {
            float displacement = (box.transform.position.x - state.OriginCellPosition.x) * sign;
            if (displacement < halfCell + Mathf.Epsilon)
            {
                break;
            }

            Vector3 oldOrigin = state.OriginCellPosition;
            Vector3 newOrigin = oldOrigin + axis * cellSize;
            state.OriginCellPosition = newOrigin;
            state.AdvancedThisSession = true;

            SendEvent(new BoxPhysicalPushEvent(box, direction, true, false, oldOrigin, newOrigin));
            SendEvent(new BoxPushAttemptEvent(box, direction, pusher, true));
        }

        linearPushes[box] = state;
        return magnitude * sign;
    }

    /// <summary>
    /// 通知线性推动会话结束，进入释放过渡阶段；按 50% 规则确定目标格并匀速滑过去。
    /// 当目标为"返回原格"（≤50%）时，pusher 会被同步拖动以避免阻挡。
    /// </summary>
    public void ReleaseLinearPush(StandardBox box)
    {
        if (box == null)
        {
            return;
        }

        if (!linearPushes.TryGetValue(box, out LinearPushState state) || !state.Active || state.ReleaseTransition)
        {
            return;
        }

        Vector3 axis = state.Direction == BoxPushDirection.Right ? Vector3.right : Vector3.left;
        float sign = state.Direction == BoxPushDirection.Right ? 1f : -1f;
        float cellSize = GetCellDistance(box, axis);
        float halfCell = cellSize * 0.5f;
        float displacement = (box.transform.position.x - state.OriginCellPosition.x) * sign;

        if (displacement >= halfCell - Mathf.Epsilon)
        {
            // 通常 TryAdvance 已在 ≥50% 时推进过原点；这里覆盖刚好等于 50% 的边界情形。
            // 前进方向 snap：箱子向远离 pusher 的方向走，pusher 不需要被拖动。
            state.ReleaseTargetPosition = state.OriginCellPosition + axis * cellSize;
            state.MovePusherWithBox = false;
        }
        else
        {
            // 回退方向 snap：箱子向 pusher 一侧走，pusher 必须随箱子一起移动以让出空间。
            state.ReleaseTargetPosition = state.OriginCellPosition;
            state.MovePusherWithBox = state.Pusher != null;
        }

        state.ReleaseTargetPosition.y = box.transform.position.y;
        state.ReleaseTargetPosition.z = box.transform.position.z;
        state.ReleaseTransition = true;
        linearPushes[box] = state;
    }

    /// <summary>
    /// 查询某个 GameObject 是否正在被某个箱子的释放过渡拖动；PlayerController 可据此屏蔽水平输入。
    /// </summary>
    public bool IsPusherInRelease(GameObject pusher)
    {
        if (pusher == null)
        {
            return false;
        }

        foreach (KeyValuePair<StandardBox, LinearPushState> entry in linearPushes)
        {
            LinearPushState state = entry.Value;
            if (state.ReleaseTransition && state.MovePusherWithBox && state.Pusher == pusher)
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateLinearPushTransitions(float dt)
    {
        if (linearPushes.Count == 0)
        {
            return;
        }

        linearPushScratch.Clear();
        foreach (StandardBox box in linearPushes.Keys)
        {
            linearPushScratch.Add(box);
        }

        float step = linearPushSpeed * dt;
        for (int i = 0; i < linearPushScratch.Count; i++)
        {
            StandardBox box = linearPushScratch[i];
            if (box == null)
            {
                linearPushes.Remove(box);
                continue;
            }

            if (!linearPushes.TryGetValue(box, out LinearPushState state) || !state.ReleaseTransition)
            {
                continue;
            }

            Vector3 current = box.transform.position;
            Vector3 target = state.ReleaseTargetPosition;
            target.y = current.y;
            target.z = current.z;

            float remaining = Mathf.Abs(target.x - current.x);
            if (remaining <= Mathf.Epsilon)
            {
                CompleteRelease(box, target);
                continue;
            }

            float move = Mathf.Min(step, remaining);
            float dir = Mathf.Sign(target.x - current.x);
            Vector3 axis = dir > 0f ? Vector3.right : Vector3.left;

            // 回退 snap：先把 pusher（玩家）按相同位移移开，避免 pusher 阻挡箱子的回退路径。
            PlayerController pusherPlayer = null;
            if (state.MovePusherWithBox && state.Pusher != null)
            {
                pusherPlayer = state.Pusher.GetComponent<PlayerController>();
                if (pusherPlayer != null)
                {
                    pusherPlayer.ApplyExternalPositionDelta(new Vector3(dir * move, 0f, 0f));
                }
            }

            // 堆叠组同步：释放过渡阶段同样按 Q3=lower_only / Q4=impact 联动。先固化关系（root 移动前），
            // 再让 root 与成员各自 MoveTo。
            CollectVerticalStack(box, stackGroupScratch);

            Vector3 next = current + new Vector3(dir * move, 0f, 0f);

            // 释放过渡途中如果撞到东西也夹断，直接停在当前位置并清状态。
            if (Cast(box, axis, move + skinWidth, out RayHit hit) && hit.Distance < move + skinWidth - PushContactTolerance)
            {
                float allowed = Mathf.Max(0f, hit.Distance - skinWidth);
                // pusher 已经按 move 移走，但箱子被夹断只能走 allowed；把 pusher 多移走的部分回滚以保持贴合。
                if (pusherPlayer != null && allowed < move)
                {
                    float rollback = allowed - move; // 负数（反向回滚）
                    pusherPlayer.ApplyExternalPositionDelta(new Vector3(dir * rollback, 0f, 0f));
                }

                next = current + new Vector3(dir * allowed, 0f, 0f);
                box.MoveTo(next);
                ApplyStackedFollow(stackGroupScratch, axis, allowed, dt);
                // 撞到障碍物：放弃后续滑行，保持当前位置；若仍未到达目标，作为最终结果（不再尝试对齐）。
                linearPushes.Remove(box);
                continue;
            }

            box.MoveTo(next);
            ApplyStackedFollow(stackGroupScratch, axis, move, dt);

            if (Mathf.Abs(target.x - next.x) <= Mathf.Epsilon)
            {
                CompleteRelease(box, target);
            }
            else
            {
                linearPushes[box] = state;
            }
        }

        linearPushScratch.Clear();
    }

    private void CompleteRelease(StandardBox box, Vector3 target)
    {
        box.MoveTo(target);
        linearPushes.Remove(box);
    }

    private struct LinearPushState
    {
        public bool Active;
        public BoxPushDirection Direction;
        public Vector3 OriginCellPosition;
        public bool ReleaseTransition;
        public Vector3 ReleaseTargetPosition;
        public bool AdvancedThisSession;
        public GameObject Pusher;
        public bool MovePusherWithBox;
    }

    private readonly struct RayHit
    {
        public readonly float Distance;
        public readonly PlayerController Player;

        public RayHit(float distance, PlayerController player)
        {
            Distance = distance;
            Player = player;
        }
    }
}
