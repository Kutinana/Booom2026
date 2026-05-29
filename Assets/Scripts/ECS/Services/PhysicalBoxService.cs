using System.Collections.Generic;
using QFramework;
using UnityEngine;

public class PhysicalBoxService : ServiceBase<StandardBox>
{
    private const float PushContactTolerance = 0.001f;

    // 释放线性推动判定 displacement 是否过半时使用的容差（1% cell），避免墙体不完美对齐时的回退卡死问题。
    private const float PushReleaseAdvanceTolerance = 0.01f;

    // 释放过渡中 remaining 小于此阈值则直接对齐（snap），避免因微小物理偏移无法精确到达而导致死循环。
    private const float ReleaseSnapDistance = PushContactTolerance;

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

    // 纵向堆叠与横向串联推动共用的缓存和判定常量
    private readonly List<StandardBox> chainGroupScratch = new List<StandardBox>(8);
    private readonly List<StandardBox> stackGroupScratch = new List<StandardBox>(8);
    private readonly List<StandardBox> combinedIgnoreScratch = new List<StandardBox>(16);
    private readonly Collider2D[] stackOverlapHits2D = new Collider2D[16];
    private readonly Collider[] stackOverlapHits3D = new Collider[16];
    private const float StackContactEpsilon = 0.04f;
    private const float StackOverlapRatio = 0.4f;

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
            if (box == null || !box.ApplyGravity)
            {
                continue;
            }

            SimulateFall(box, dt);
        }

        UpdateLinearPushTransitions(dt);

    }

    private void OnPushAttempted(BoxPushAttemptEvent e)
    {
        if (IsFalling(e.Box))
        {
            return;
        }

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

        // Collect the chain before casting so adjacent boxes can move together.
        CollectHorizontalChain(box, axis, chainGroupScratch);
        CollectVerticalStackForChain(chainGroupScratch, stackGroupScratch);

        bool teleportedWorldBoxPusher = false;
        if (TryTeleportFirstBlockedWorldBoxPusherInGroup(box, direction, axis, chainGroupScratch, out StandardBox teleportedPusher))
        {
            if (teleportedPusher == box)
            {
                return false;
            }

            teleportedWorldBoxPusher = true;
            CollectHorizontalChain(box, axis, chainGroupScratch);
            CollectVerticalStackForChain(chainGroupScratch, stackGroupScratch);
        }

        float magnitude = distance;
        for (int i = 0; i < chainGroupScratch.Count; i++)
        {
            StandardBox member = chainGroupScratch[i];
            if (Cast(member, axis, magnitude + skinWidth, out RayHit chainHit, chainGroupScratch))
            {
                PlayerController hitPlayer = chainHit.Player;
                if (hitPlayer != null)
                {
                    TryHandlePlayerImpact(member, member.transform.position, member.transform.position + axis * magnitude, Time.fixedDeltaTime);
                }

                magnitude = Mathf.Min(magnitude, Mathf.Max(0f, chainHit.Distance - skinWidth));
                if (magnitude <= 0f)
                {
                    break;
                }
            }
        }

        if (magnitude <= 0f)
        {
            SendEvent(new BoxPhysicalPushEvent(box, direction, false, false, from, from));
            return false;
        }

        if (!teleportedWorldBoxPusher && TryTeleportFirstBlockedWorldBoxPusherInGroup(box, direction, axis, stackGroupScratch, out _))
        {
            CollectHorizontalChain(box, axis, chainGroupScratch);
            CollectVerticalStackForChain(chainGroupScratch, stackGroupScratch);
        }

        Vector3 target = from + axis * magnitude;
        for (int i = 0; i < chainGroupScratch.Count; i++)
        {
            StandardBox member = chainGroupScratch[i];
            if (member == null)
            {
                continue;
            }

            Vector3 memberFrom = member.transform.position;
            member.MoveTo(memberFrom + axis * magnitude);
            RefreshSceneMovableBaselineForStandardBox(member);
        }

        BuildCombinedIgnore(chainGroupScratch, stackGroupScratch);
        ApplyStackedFollow(stackGroupScratch, axis, magnitude, Time.fixedDeltaTime, combinedIgnoreScratch);

        if (box.Grid != null && box.AlignToGrid)
        {
            NotifyStandardBoxHorizontalGridSettled(box);
        }

        SendEvent(new BoxPhysicalPushEvent(box, direction, true, false, from, target));
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
                // 落地兜底（landed 路径已经 reset fallSpeed=0，正常情况下 wasFalling 这帧不会进；
                // 但 box 被外力瞬移、或没经过 landed 分支就直接 grounded 时这里能补上一次对齐）。
                TryQueueAlignmentRelease(box);
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
            // 下落落地后：若 X 不对齐 grid（比如下落前是 stacked 跟随脱节，或关卡设计就摆在半格上），
            // 此处给它注入一次 alignment release，让它在地面上短促滑一下完成对齐。
            // 已有 active LinearPushState（玩家推 / release transition / 之前的 alignment 还没收尾）的 box
            // 会被 TryQueueAlignmentRelease 早退出，不会被覆盖。
            TryQueueAlignmentRelease(box);
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

    private static void RefreshSceneMovableBaselineForStandardBox(StandardBox box)
    {
        if (box == null)
        {
            return;
        }

        if (ServiceBase.TryGet(out SceneMovableInteractionService sceneMovable))
        {
            sceneMovable.RefreshItemImpactBaseline(box);
        }
    }

    /// <summary>
    /// 推箱子时 pusher 站在施力侧（相对当前推动轴的“后侧”）；chain cast 可能因重叠扫到该玩家，
    /// 不应与“被挤在前方砸死”混淆。frontal &gt; slop 表示玩家明显在前进方向一侧，仍按砸落处理。
    /// </summary>
    private bool IsPusherRearRelativeToMemberAlongPushAxis(StandardBox member, Vector3 axis, PlayerController player)
    {
        if (member == null || player == null)
        {
            return false;
        }

        ISceneMovableBoundsProvider provider = player.BoundsProvider;
        if (provider == null || !provider.IsValid)
        {
            return false;
        }

        Vector2 toPlayer = (Vector2)(provider.Bounds.center - member.Bounds.center);
        Vector2 ax = new Vector2(axis.x, axis.y);
        float axMag = ax.magnitude;
        if (axMag <= Mathf.Epsilon)
        {
            return false;
        }

        float frontal = Vector2.Dot(ax / axMag, toPlayer);
        float rearSlop = Mathf.Max(0.05f, impactContactTolerance);
        return frontal <= rearSlop;
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
        return Cast(box, direction, distance, out bestHit, null);
    }

    /// <summary>
    /// 带 ignoreBoxes 的 Cast：命中的 collider 若属于 ignoreBoxes 中任一 StandardBox 则跳过——
    /// 用于推动整组同步移动时，组内成员之间不应互相阻挡。
    /// </summary>
    private bool Cast(StandardBox box, Vector3 direction, float distance, out RayHit bestHit, IList<StandardBox> ignoreBoxes)
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

            if (CastSingle(box, origin, direction, distance, out RayHit hit, ignoreBoxes) && hit.Distance < bestDistance)
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
        return CastSingle(box, origin, direction, distance, out hit, null);
    }

    public bool CastSingle(StandardBox box, Vector3 origin, Vector3 direction, float distance, out RayHit hit, IList<StandardBox> ignoreBoxes)
    {
        float bestDistance = float.PositiveInfinity;
        hit = default;

        GameObject ignoredPlayer = TryGetDyingPlayerGameObject();

        bool isExitingWorldBox = false;
        WorldBox exitingWorldBox = null;
        BoxPushDirection transitionDir = default;
        bool isEnteringWorldBox = false;
        WorldBox enteringWorldBox = null;
        if (ServiceBase.TryGet(out PushableBoxService pushableBoxService))
        {
            isExitingWorldBox = pushableBoxService.IsBoxExitingWorldBox(box, out exitingWorldBox, out transitionDir);
            isEnteringWorldBox = pushableBoxService.IsBoxEnteringWorldBox(box, out enteringWorldBox, out _);
        }

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

                if (isExitingWorldBox && StandardBox.IsOwnedByWorldBox(hit2D.collider.transform, exitingWorldBox) && IsAlongTransitionAxis(direction, transitionDir))
                {
                    continue;
                }

                if (isEnteringWorldBox && hit2D.collider != null && (hit2D.collider.gameObject == enteringWorldBox.gameObject || StandardBox.IsOwnedByWorldBox(hit2D.collider.transform, enteringWorldBox)))
                {
                    continue;
                }

                WorldBox hitWorldBox2D = hit2D.collider != null ? hit2D.collider.GetComponentInParent<WorldBox>() : null;
                if (hitWorldBox2D != null && StandardBox.IsOwnedByWorldBox(box.transform, hitWorldBox2D))
                {
                    BoxPushDirection castDir = VectorToDirection(direction);
                    if (PushableBoxService.IsTouchingInnerBoundsForExiting(hitWorldBox2D.InnerBounds, box.Bounds, castDir, 0.04f))
                    {
                        continue;
                    }
                }

                if (hitWorldBox2D != null && !StandardBox.IsOwnedByWorldBox(box.transform, hitWorldBox2D))
                {
                    BoxPushDirection pushDirFromVec = VectorToDirection(direction);
                    if (hitWorldBox2D.GetOuterEntrance(pushDirFromVec.Opposite()) != null)
                    {
                        StandardBox hitStandardBox = hit2D.collider.GetComponentInParent<StandardBox>();
                        if (hitStandardBox != null && !(hitStandardBox is WorldBox) && StandardBox.IsOwnedByWorldBox(hitStandardBox.transform, hitWorldBox2D))
                        {
                            continue;
                        }

                        bool canGroupEnter = false;
                        if (PushableBoxService.IsTouchingOuterBoundsForEntering(hitWorldBox2D.Bounds, box.Bounds, pushDirFromVec, 0.04f))
                        {
                            canGroupEnter = true;
                        }
                        else if (ignoreBoxes != null)
                        {
                            for (int idx = 0; idx < ignoreBoxes.Count; idx++)
                            {
                                StandardBox member = ignoreBoxes[idx];
                                if (member != null && PushableBoxService.IsTouchingOuterBoundsForEntering(hitWorldBox2D.Bounds, member.Bounds, pushDirFromVec, 0.04f))
                                {
                                    canGroupEnter = true;
                                    break;
                                }
                            }
                        }

                        if (canGroupEnter)
                        {
                            continue;
                        }
                    }
                }

                bool vertical = Mathf.Abs(direction.y) > 0f;

                if (!vertical && hit2D.collider.CompareTag("Platform"))
                {
                    continue;
                }

                PlayerController hitPlayer = hit2D.collider.GetComponentInParent<PlayerController>();
                if (ignoredPlayer != null && hitPlayer != null && hitPlayer.gameObject == ignoredPlayer)
                {
                    continue;
                }

                if (ignoreBoxes != null)
                {
                    StandardBox hitBox = hit2D.collider.GetComponentInParent<StandardBox>();
                    if (hitBox != null && ignoreBoxes.Contains(hitBox))
                    {
                        continue;
                    }
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

                if (isExitingWorldBox && StandardBox.IsOwnedByWorldBox(hit3D.collider.transform, exitingWorldBox) && IsAlongTransitionAxis(direction, transitionDir))
                {
                    continue;
                }

                if (isEnteringWorldBox && hit3D.collider != null && (hit3D.collider.gameObject == enteringWorldBox.gameObject || StandardBox.IsOwnedByWorldBox(hit3D.collider.transform, enteringWorldBox)))
                {
                    continue;
                }

                WorldBox hitWorldBox3D = hit3D.collider != null ? hit3D.collider.GetComponentInParent<WorldBox>() : null;
                if (hitWorldBox3D != null && StandardBox.IsOwnedByWorldBox(box.transform, hitWorldBox3D))
                {
                    BoxPushDirection castDir = VectorToDirection(direction);
                    if (PushableBoxService.IsTouchingInnerBoundsForExiting(hitWorldBox3D.InnerBounds, box.Bounds, castDir, 0.04f))
                    {
                        continue;
                    }
                }

                if (hitWorldBox3D != null && !StandardBox.IsOwnedByWorldBox(box.transform, hitWorldBox3D))
                {
                    BoxPushDirection pushDirFromVec = VectorToDirection(direction);
                    if (hitWorldBox3D.GetOuterEntrance(pushDirFromVec.Opposite()) != null)
                    {
                        StandardBox hitStandardBox = hit3D.collider.GetComponentInParent<StandardBox>();
                        if (hitStandardBox != null && !(hitStandardBox is WorldBox) && StandardBox.IsOwnedByWorldBox(hitStandardBox.transform, hitWorldBox3D))
                        {
                            continue;
                        }

                        bool canGroupEnter = false;
                        if (PushableBoxService.IsTouchingOuterBoundsForEntering(hitWorldBox3D.Bounds, box.Bounds, pushDirFromVec, 0.04f))
                        {
                            canGroupEnter = true;
                        }
                        else if (ignoreBoxes != null)
                        {
                            for (int idx = 0; idx < ignoreBoxes.Count; idx++)
                            {
                                StandardBox member = ignoreBoxes[idx];
                                if (member != null && PushableBoxService.IsTouchingOuterBoundsForEntering(hitWorldBox3D.Bounds, member.Bounds, pushDirFromVec, 0.04f))
                                {
                                    canGroupEnter = true;
                                    break;
                                }
                            }
                        }

                        if (canGroupEnter)
                        {
                            continue;
                        }
                    }
                }

                PlayerController hitPlayer = hit3D.collider.GetComponentInParent<PlayerController>();
                if (ignoredPlayer != null && hitPlayer != null && hitPlayer.gameObject == ignoredPlayer)
                {
                    continue;
                }

                if (ignoreBoxes != null)
                {
                    StandardBox hitBox = hit3D.collider.GetComponentInParent<StandardBox>();
                    if (hitBox != null && ignoreBoxes.Contains(hitBox))
                    {
                        continue;
                    }
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



    private static bool IsAlongTransitionAxis(Vector3 direction, BoxPushDirection transitionDir)
    {
        Vector3 transitionAxis = Vector3.zero;
        switch (transitionDir)
        {
            case BoxPushDirection.Left:
            case BoxPushDirection.Right:
                transitionAxis = Vector3.right;
                break;
            case BoxPushDirection.Up:
            case BoxPushDirection.Down:
                transitionAxis = Vector3.up;
                break;
        }
        return Mathf.Abs(Vector3.Dot(direction.normalized, transitionAxis)) > 0.5f;
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
    /// 从 root 出发，沿 axis 方向 BFS 收集横向连接成员（含 root）—— A 推 B 推 C 的串联推动场景。
    /// 判定规则：
    ///   - 紧贴：相邻 box 在 axis 方向上的面间隙 ≤ StackContactEpsilon；
    ///   - Y overlap：两 box 在 Y 上的重叠 > min(height) * StackOverlapRatio。
    /// </summary>
    public void CollectHorizontalChain(StandardBox root, Vector3 axis, List<StandardBox> outGroup)
    {
        outGroup.Clear();
        if (root == null)
        {
            return;
        }

        outGroup.Add(root);

        // WorldBox 自身被推时不收集已退出的独立 box
        if (root is WorldBox)
        {
            return;
        }

        // 广度优先：从 root 起逐个向 axis 方向追加紧贴的下一个成员。
        for (int i = 0; i < outGroup.Count; i++)
        {
            AppendChainInDirection(outGroup[i], axis, outGroup);
        }
    }

    /// <summary>
    /// 对给定 chain 各成员，向上 BFS 收集纵向堆叠成员（chain 自身不包含在 outGroup 里）。
    /// 用于 chain 整组推动后，让上方堆叠 box（含横跨多个 chain 成员的）按 lower_only 同步跟随。
    /// </summary>
    private void CollectVerticalStackForChain(List<StandardBox> chain, List<StandardBox> outGroup)
    {
        outGroup.Clear();
        if (chain == null || chain.Count == 0)
        {
            return;
        }

        for (int i = 0; i < chain.Count; i++)
        {
            AppendStackedAbove(chain[i], outGroup);
        }

        // BFS 向上扩展：outGroup 内的箱子可能还顶着更高一层。
        for (int i = 0; i < outGroup.Count; i++)
        {
            AppendStackedAbove(outGroup[i], outGroup);
        }
    }

    private void AppendChainInDirection(StandardBox below, Vector3 axis, List<StandardBox> outGroup)
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

        bool positive = axis.x > 0f;
        float frontX = positive ? belowBounds.max.x : belowBounds.min.x;

        Collider2D below2D = below.Collider2D;
        if (below2D != null)
        {
            // below 沿 axis 方向边缘外侧一条 epsilon 厚的薄带：高度略大于 below 高度，覆盖 Y 上相邻可能的 box。
            Vector2 stripCenter = new Vector2(frontX + (positive ? 1f : -1f) * StackContactEpsilon * 0.5f, belowBounds.center.y);
            Vector2 stripSize = new Vector2(StackContactEpsilon, belowBounds.size.y + StackContactEpsilon);
            int hitCount = Physics2D.OverlapBoxNonAlloc(stripCenter, stripSize, 0f, stackOverlapHits2D, below.CollisionMask);

            for (int i = 0; i < hitCount; i++)
            {
                Collider2D col = stackOverlapHits2D[i];
                if (col == null || col.isTrigger)
                {
                    continue;
                }

                StandardBox neighbor = col.GetComponentInParent<StandardBox>();
                if (!IsChainCandidate(neighbor, below, positive, outGroup))
                {
                    continue;
                }

                outGroup.Add(neighbor);
            }

            return;
        }

        Collider below3D = below.Collider3D;
        if (below3D != null)
        {
            Vector3 stripCenter = new Vector3(frontX + (positive ? 1f : -1f) * StackContactEpsilon * 0.5f, belowBounds.center.y, belowBounds.center.z);
            Vector3 stripHalfExtents = new Vector3(StackContactEpsilon * 0.5f, (belowBounds.size.y + StackContactEpsilon) * 0.5f, Mathf.Max(belowBounds.size.z, StackContactEpsilon) * 0.5f);
            int hitCount = Physics.OverlapBoxNonAlloc(stripCenter, stripHalfExtents, stackOverlapHits3D, Quaternion.identity, below.CollisionMask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hitCount; i++)
            {
                Collider col = stackOverlapHits3D[i];
                if (col == null)
                {
                    continue;
                }

                StandardBox neighbor = col.GetComponentInParent<StandardBox>();
                if (!IsChainCandidate(neighbor, below, positive, outGroup))
                {
                    continue;
                }

                outGroup.Add(neighbor);
            }
        }
    }

    private bool IsChainCandidate(StandardBox neighbor, StandardBox below, bool positiveDirection, List<StandardBox> outGroup)
    {
        if (neighbor == null || neighbor == below)
        {
            return false;
        }

        if (neighbor is WorldBox)
            return false;

        if (outGroup.Contains(neighbor))
        {
            return false;
        }

        Bounds neighborBounds = neighbor.Bounds;
        if (neighborBounds.size == Vector3.zero)
        {
            return false;
        }

        Bounds belowBounds = below.Bounds;

        // 紧贴：neighbor 在 below 推动方向那一面的"反面"应该贴住 below。
        float belowFront = positiveDirection ? belowBounds.max.x : belowBounds.min.x;
        float neighborBack = positiveDirection ? neighborBounds.min.x : neighborBounds.max.x;
        float gap = (neighborBack - belowFront) * (positiveDirection ? 1f : -1f);
        if (gap < -StackContactEpsilon || gap > StackContactEpsilon)
        {
            return false;
        }

        float yOverlap = Mathf.Min(neighborBounds.max.y, belowBounds.max.y) - Mathf.Max(neighborBounds.min.y, belowBounds.min.y);
        float minHeight = Mathf.Min(neighborBounds.size.y, belowBounds.size.y);
        if (yOverlap <= minHeight * StackOverlapRatio)
        {
            return false;
        }

        BoxPushDirection direction = positiveDirection ? BoxPushDirection.Right : BoxPushDirection.Left;
        if (neighbor is WorldBox worldBox && !worldBox.CanPushToward(direction))
        {
            return false;
        }

        // 若 below 是 WorldBox 自身，不收集非其子节点的 box。
        if (below is WorldBox belowWorldBox && !StandardBox.IsOwnedByWorldBox(neighbor.transform, belowWorldBox))
        {
            return false;
        }

        if (HasActiveLinearPushState(neighbor))
        {
            return false;
        }

        return true;
    }

    private bool TryTeleportFirstBlockedWorldBoxPusherInGroup(
        StandardBox root,
        BoxPushDirection direction,
        Vector3 axis,
        List<StandardBox> group,
        out StandardBox teleportedPusher)
    {
        teleportedPusher = null;
        if (group == null)
        {
            return false;
        }

        for (int i = 0; i < group.Count; i++)
        {
            StandardBox pusherBox = group[i];
            if (pusherBox == null || !IsRegistered(pusherBox))
            {
                continue;
            }

            if (!TryGetBlockedWorldBoxAhead(pusherBox, direction, axis, out WorldBox blockedWorldBox))
            {
                continue;
            }

            if (blockedWorldBox.GetOuterEntrance(direction.Opposite()) != null)
            {
                continue;
            }

            Vector3 before = pusherBox.transform.position;
            blockedWorldBox.InitializePush(direction, pusherBox.gameObject);
            if (!HasMovedFrom(pusherBox, before))
            {
                continue;
            }

            if (pusherBox == root)
            {
                linearPushes.Remove(root);
            }

            RefreshSceneMovableBaselineForStandardBox(pusherBox);
            NotifyStandardBoxHorizontalGridSettled(pusherBox);
            teleportedPusher = pusherBox;
            return true;
        }

        return false;
    }

    private bool TryGetBlockedWorldBoxAhead(StandardBox pusherBox, BoxPushDirection direction, Vector3 axis, out WorldBox blockedWorldBox)
    {
        blockedWorldBox = null;
        if (pusherBox == null)
        {
            return false;
        }

        Bounds pusherBounds = pusherBox.Bounds;
        if (pusherBounds.size == Vector3.zero)
        {
            return false;
        }

        bool positive = axis.x > 0f;
        float frontX = positive ? pusherBounds.max.x : pusherBounds.min.x;

        Collider2D pusher2D = pusherBox.Collider2D;
        if (pusher2D != null)
        {
            Vector2 stripCenter = new Vector2(frontX + (positive ? 1f : -1f) * StackContactEpsilon * 0.5f, pusherBounds.center.y);
            Vector2 stripSize = new Vector2(StackContactEpsilon, pusherBounds.size.y + StackContactEpsilon);
            int hitCount = Physics2D.OverlapBoxNonAlloc(stripCenter, stripSize, 0f, stackOverlapHits2D, pusherBox.CollisionMask);

            for (int i = 0; i < hitCount; i++)
            {
                Collider2D col = stackOverlapHits2D[i];
                if (col == null || col.isTrigger)
                {
                    continue;
                }

                WorldBox worldBox = col.GetComponentInParent<WorldBox>();
                if (!IsBlockedWorldBoxPushTarget(worldBox, pusherBox, direction, positive))
                {
                    continue;
                }

                blockedWorldBox = worldBox;
                return true;
            }

            return false;
        }

        Collider pusher3D = pusherBox.Collider3D;
        if (pusher3D != null)
        {
            Vector3 stripCenter = new Vector3(frontX + (positive ? 1f : -1f) * StackContactEpsilon * 0.5f, pusherBounds.center.y, pusherBounds.center.z);
            Vector3 stripHalfExtents = new Vector3(StackContactEpsilon * 0.5f, (pusherBounds.size.y + StackContactEpsilon) * 0.5f, Mathf.Max(pusherBounds.size.z, StackContactEpsilon) * 0.5f);
            int hitCount = Physics.OverlapBoxNonAlloc(stripCenter, stripHalfExtents, stackOverlapHits3D, Quaternion.identity, pusherBox.CollisionMask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hitCount; i++)
            {
                Collider col = stackOverlapHits3D[i];
                if (col == null)
                {
                    continue;
                }

                WorldBox worldBox = col.GetComponentInParent<WorldBox>();
                if (!IsBlockedWorldBoxPushTarget(worldBox, pusherBox, direction, positive))
                {
                    continue;
                }

                blockedWorldBox = worldBox;
                return true;
            }
        }

        return false;
    }

    private bool IsBlockedWorldBoxPushTarget(WorldBox worldBox, StandardBox pusherBox, BoxPushDirection direction, bool positiveDirection)
    {
        if (worldBox == null || worldBox == pusherBox || worldBox.CanPushToward(direction))
        {
            return false;
        }

        if (HasActiveLinearPushState(worldBox))
        {
            return false;
        }

        Bounds worldBounds = worldBox.Bounds;
        Bounds pusherBounds = pusherBox.Bounds;
        if (worldBounds.size == Vector3.zero || pusherBounds.size == Vector3.zero)
        {
            return false;
        }

        float pusherFront = positiveDirection ? pusherBounds.max.x : pusherBounds.min.x;
        float worldBack = positiveDirection ? worldBounds.min.x : worldBounds.max.x;
        float gap = (worldBack - pusherFront) * (positiveDirection ? 1f : -1f);
        if (gap < -StackContactEpsilon || gap > StackContactEpsilon)
        {
            return false;
        }

        float yOverlap = Mathf.Min(worldBounds.max.y, pusherBounds.max.y) - Mathf.Max(worldBounds.min.y, pusherBounds.min.y);
        float minHeight = Mathf.Min(worldBounds.size.y, pusherBounds.size.y);
        return yOverlap > minHeight * StackOverlapRatio;
    }

    private static bool HasMovedFrom(StandardBox box, Vector3 before)
    {
        return box != null && (box.transform.position - before).sqrMagnitude > Mathf.Epsilon;
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
    /// 同步水平位移到堆叠组成员（撞墙脱节，撞玩家触发 impact）。
    /// </summary>
    private void ApplyStackedFollow(List<StandardBox> stackGroup, Vector3 axis, float magnitude, float dt, IList<StandardBox> ignoreBoxes = null)
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
            bool clipped = false;

            if (Cast(stacked, axis, stackedMagnitude + skinWidth, out RayHit stackedHit, ignoreBoxes))
            {
                // 撞 player：先派发 impact（与 SimulateFall 撞 player 一致地传"未 clamp 的目标位置"，
                // 让 impact 能感知"如果没有 player，箱子本应走多远"），随后将 stacked 限制到 cast 距离处。
                if (stackedHit.Player != null)
                {
                    TryHandlePlayerImpact(stacked, stackedFrom, stackedFrom + axis * stackedMagnitude, dt);
                }

                float allowed = Mathf.Max(0f, stackedHit.Distance - skinWidth);
                if (allowed < stackedMagnitude - PushContactTolerance)
                {
                    clipped = true;
                }
                stackedMagnitude = allowed;
            }

            if (stackedMagnitude > 0f)
            {
                stacked.MoveTo(stackedFrom + axis * stackedMagnitude);
            }

            if (clipped)
            {
                TryQueueAlignmentRelease(stacked);
            }
        }
    }

    /// <summary>
    /// 为箱子排队一次 X 向 grid 对齐（与堆叠脱节、落地后使用的 <see cref="TryQueueAlignmentRelease"/> 相同）：
    /// 误差小于 skinWidth 则直接 snap，否则注入 <c>ReleaseTransition</c> 由 <see cref="UpdateLinearPushTransitions"/> 匀速滑过去。
    /// 用于 WorldBox 穿门等强制 <see cref="CancelLinearPush"/> 后补上未走完的 release 对齐。
    /// </summary>
    public void QueueGridAlignmentRelease(StandardBox box)
    {
        if (box == null || !IsRegistered(box))
        {
            return;
        }

        TryQueueAlignmentRelease(box);
    }

    /// <summary>
    /// 为脱离跟随的箱子尝试注入网格滑动对齐状态。
    /// </summary>
    private void TryQueueAlignmentRelease(StandardBox box)
    {
        if (box == null || !box.AlignToGrid || linearPushes.ContainsKey(box))
        {
            return;
        }

        if (!TryGetAlignedX(box, out float alignedX))
        {
            return;
        }

        Vector3 currentPos = box.transform.position;
        float deltaX = alignedX - currentPos.x;

        if (Mathf.Abs(deltaX) < skinWidth)
        {
            // 误差极小，直接 snap，不必走 release transition。
            box.MoveTo(new Vector3(alignedX, currentPos.y, currentPos.z));
            NotifyStandardBoxHorizontalGridSettled(box);
            return;
        }

        Vector3 alignedPos = new Vector3(alignedX, currentPos.y, currentPos.z);
        LinearPushState alignState = new LinearPushState
        {
            Active = true,
            Direction = deltaX > 0f ? BoxPushDirection.Right : BoxPushDirection.Left,
            OriginCellPosition = alignedPos,
            ReleaseTransition = true,
            ReleaseTargetPosition = alignedPos,
            AdvancedThisSession = false,
            Pusher = null,
            MovePusherWithBox = false,
        };
        linearPushes[box] = alignState;
    }

    private bool TryGetAlignedX(StandardBox box, out float alignedX)
    {
        alignedX = box != null ? box.transform.position.x : 0f;
        if (box == null)
        {
            return false;
        }

        if (!box.AlignToGrid)
        {
            return false;
        }

        Grid grid = box.Grid;
        if (grid == null)
        {
            return false;
        }

        float cellSize = grid.cellSize.x;
        if (cellSize <= Mathf.Epsilon)
        {
            return false;
        }

        float originX = grid.transform.position.x;
        float offsetX = box.CellOffset.x * cellSize;
        float currentX = box.transform.position.x;

        // 找最近的 grid cell 中心（含 CellOffset）：cell.x = round((currentX - originX - offsetX) / cellSize)。
        alignedX = originX + Mathf.Round((currentX - originX - offsetX) / cellSize) * cellSize + offsetX;
        return true;
    }

    /// <summary>Nearest horizontal grid-aligned world X (same as release snap).</summary>
    public bool TryGetHorizontalGridAlignedWorldX(StandardBox box, out float alignedX)
    {
        return TryGetAlignedX(box, out alignedX);
    }

    /// <summary>True if box X is within tolerance of nearest horizontal grid column center (default skinWidth*2).</summary>
    public bool IsNearHorizontalGridCenter(StandardBox box, float tolerance = -1f)
    {
        if (box == null)
        {
            return false;
        }

        if (tolerance < 0f)
        {
            tolerance = Mathf.Max(0.001f, skinWidth * 2f);
        }

        if (!TryGetAlignedX(box, out float alignedX))
        {
            return false;
        }

        return Mathf.Abs(box.transform.position.x - alignedX) <= tolerance;
    }

    /// <summary>
    /// 把 chain 与 stack 两组合并到 combinedIgnoreScratch，便于 cast 时整组忽略。
    /// </summary>
    private void BuildCombinedIgnore(List<StandardBox> chain, List<StandardBox> stacked)
    {
        combinedIgnoreScratch.Clear();
        if (chain != null)
        {
            for (int i = 0; i < chain.Count; i++)
            {
                if (chain[i] != null)
                {
                    combinedIgnoreScratch.Add(chain[i]);
                }
            }
        }

        if (stacked != null)
        {
            for (int i = 0; i < stacked.Count; i++)
            {
                if (stacked[i] != null)
                {
                    combinedIgnoreScratch.Add(stacked[i]);
                }
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

        // Q2：X 重叠 > 较窄者宽 * StackOverlapRatio。box 都对齐 grid 的前提下，X overlap 只可能是 0/0.5/1。
        // 阈值 0.4 → 完全对齐(1.0) 与 半搭横跨(0.5) 都算堆叠成员，错开(0.0) 不算。
        float centerDeltaX = Mathf.Abs(stackedBounds.center.x - below.Bounds.center.x);

       
        float alignTolerance = 0.05f;

        if (centerDeltaX > alignTolerance)
        {
            return false;
        }

        if (HasActiveLinearPushState(stacked))
        {
            return false;
        }

        return true;
    }

    private bool HasActiveLinearPushState(StandardBox box)
    {
        // 已经有自己 active linear push state 的 box 不参与组同步，避免被 root 推动与自身 release/advance
        // 同帧双重驱动导致位置抖动。罕见情况（成员还有遗留 release 没收尾就被推），让它走自己的状态收完。
        // IsFollower == true 表示这是 chain follower 轻量标记，不影响 chain 收集。
        return linearPushes.TryGetValue(box, out LinearPushState state) && state.Active && !state.IsFollower;
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
        if (IsFalling(box))
        {
            return 0f;
        }
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
                // 避免 SceneMovableInteractionService 在下一帧把 snap 当成一帧内极高速位移而误判砸死。
                RefreshSceneMovableBaselineForStandardBox(box);
                NotifyStandardBoxHorizontalGridSettled(box);
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

        // === 横向 chain（A 推 B 推 C 串联）===
        // 在 root 移动之前先固化 chain 与上方堆叠关系（box 一旦移走，紧贴判定就失效）。
        CollectHorizontalChain(box, axis, chainGroupScratch);
        CollectVerticalStackForChain(chainGroupScratch, stackGroupScratch);
        bool teleportedWorldBoxPusher = false;
        if (TryTeleportFirstBlockedWorldBoxPusherInGroup(box, direction, axis, chainGroupScratch, out StandardBox teleportedPusher))
        {
            if (teleportedPusher == box)
            {
                return 0f;
            }

            teleportedWorldBoxPusher = true;
            CollectHorizontalChain(box, axis, chainGroupScratch);
            CollectVerticalStackForChain(chainGroupScratch, stackGroupScratch);
        }

        // chain 整组阻挡：对每个 chain 成员单独 cast（互相忽略），取最小可移动距离。
        // 撞墙 / 撞地形 → 整组停下；撞 player → 触发 impact 后下一帧 cast 会过滤 dying player，可继续推。
        for (int i = 0; i < chainGroupScratch.Count; i++)
        {
            StandardBox member = chainGroupScratch[i];
            if (Cast(member, axis, magnitude + skinWidth, out RayHit chainHit, chainGroupScratch))
            {
                PlayerController hitPlayer = chainHit.Player;
                bool suppressRearPusher =
                    hitPlayer != null &&
                    pusher != null &&
                    hitPlayer.gameObject == pusher &&
                    IsPusherRearRelativeToMemberAlongPushAxis(member, axis, hitPlayer);

                if (hitPlayer != null && !suppressRearPusher)
                {
                    Vector3 memberFrom = member.transform.position;
                    TryHandlePlayerImpact(member, memberFrom, memberFrom + axis * magnitude, Time.fixedDeltaTime);
                }

                if (!suppressRearPusher)
                {
                    magnitude = Mathf.Min(magnitude, Mathf.Max(0f, chainHit.Distance - skinWidth));
                }

                if (magnitude <= 0f)
                {
                    break;
                }
            }
        }

        if (magnitude <= 0f)
        {
            linearPushes[box] = state;
            return 0f;
        }


        if (!teleportedWorldBoxPusher &&
            TryTeleportFirstBlockedWorldBoxPusherInGroup(box, direction, axis, stackGroupScratch, out _))
        {
            CollectHorizontalChain(box, axis, chainGroupScratch);
            CollectVerticalStackForChain(chainGroupScratch, stackGroupScratch);
        }

        // 整 chain 同步移动 magnitude。
        for (int i = 0; i < chainGroupScratch.Count; i++)
        {
            StandardBox member = chainGroupScratch[i];
            Vector3 memberFrom = member.transform.position;
            member.MoveTo(memberFrom + axis * magnitude);
        }

        // === 为 chain 中非 root 的成员注册 follower LinearPushState ===
        // PushableBoxService.CheckAndTryTeleportAllPushableBoxesWithOuterBoundsToInner 通过
        // TryGetLinearPushDirection 扫描，只有具备 active linear push state 的 box 才会被检测
        // 是否需要进入 WorldBox 过渡。chain follower box 由 root 带动，本身没有独立 push state，
        // 因此这里主动给它们注册一个轻量的 follower state，让扫描能找到它们。
        for (int i = 1; i < chainGroupScratch.Count; i++)
        {
            StandardBox follower = chainGroupScratch[i];
            if (follower == null || follower == box)
            {
                continue;
            }
            // 只在 follower 尚无 active state 时写入，避免覆盖 follower 自身的 push session。
            if (!linearPushes.TryGetValue(follower, out LinearPushState existingFollower) || !existingFollower.Active)
            {
                linearPushes[follower] = new LinearPushState
                {
                    Active = true,
                    Direction = direction,
                    OriginCellPosition = follower.transform.position,
                    ReleaseTransition = false,
                    ReleaseTargetPosition = follower.transform.position,
                    AdvancedThisSession = false,
                    Pusher = null,          // null 表示这是 chain follower，不是直接被推者
                    MovePusherWithBox = false,
                    IsFollower = true       // 标记为 follower，HasActiveLinearPushState 对其返回 false
                };
            }
        }

        // === 纵向堆叠（含横跨支撑）跟随 ===
        // Q3=lower_only：成员能走多少走多少，撞墙就停下（与 chain 脱节，下一帧失去支撑下落）。
        // Q4=impact：上方撞 player 触发 impact，然后停在 cast 距离处。
        // cast 互相忽略 chain + 其它 stacked 成员（避免组内自堵自）。
        BuildCombinedIgnore(chainGroupScratch, stackGroupScratch);
        ApplyStackedFollow(stackGroupScratch, axis, magnitude, Time.fixedDeltaTime, combinedIgnoreScratch);

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
            // 注：此处不再对 box 自身发送 BoxPushAttemptEvent（box 已有 active state，OnPushAttempted 会直接跳过）。
            // WorldBox 过渡由 PushableBoxService 在 CheckAndTryTeleportAllPushableBoxes 中通过 follower state 检测。
        }

        linearPushes[box] = state;
        return magnitude * sign;
    }

    /// <summary>
    /// 立即移除箱子的线性推动状态（不进入 50% release 过渡、不写 ReleaseTarget）。
    /// 用于 WorldBox 穿门等必须立刻打断 <c>linearPushes</c> 会话的场景；普通松手收尾仍用 <see cref="ReleaseLinearPush"/>。
    /// </summary>
    public void CancelLinearPush(StandardBox box)
    {
        if (box == null)
        {
            return;
        }

        // 同时清理 chain 中由 TryAdvanceLinearPush 注册的 follower state，防止孤立条目残留。
        if (linearPushes.TryGetValue(box, out LinearPushState cancelState) && cancelState.Active && !cancelState.IsFollower)
        {
            Vector3 axis = cancelState.Direction == BoxPushDirection.Right ? Vector3.right : Vector3.left;
            CollectHorizontalChain(box, axis, chainGroupScratch);
            for (int i = 1; i < chainGroupScratch.Count; i++)
            {
                StandardBox follower = chainGroupScratch[i];
                if (follower == null || follower == box)
                {
                    continue;
                }
                if (linearPushes.TryGetValue(follower, out LinearPushState fs) && fs.Active && fs.IsFollower)
                {
                    linearPushes.Remove(follower);
                }
            }
        }

        linearPushes.Remove(box);
    }

    public void RegisterFollowerState(StandardBox follower, BoxPushDirection direction)
    {
        if (follower == null)
        {
            return;
        }

        linearPushes[follower] = new LinearPushState
        {
            Active = true,
            Direction = direction,
            OriginCellPosition = follower.transform.position,
            ReleaseTransition = false,
            ReleaseTargetPosition = follower.transform.position,
            AdvancedThisSession = false,
            Pusher = null,
            MovePusherWithBox = false,
            IsFollower = true
        };
    }

    public bool TryGetLinearPushDirection(StandardBox box, out BoxPushDirection direction)
    {
        direction = default;
        if (box != null && linearPushes.TryGetValue(box, out LinearPushState state) && state.Active)
        {
            // follower state 也返回方向，让 PushableBoxService 能检测到 chain follower 是否需要进入 WorldBox 过渡。
            direction = state.Direction;
            return true;
        }
        return false;
    }

    /// <summary>Active horizontal linear push for <paramref name="pusher"/> (not in release transition).</summary>
    public bool TryGetActiveLinearHorizontalPushForPusher(GameObject pusher, out StandardBox box, out BoxPushDirection direction)
    {
        box = null;
        direction = default;
        if (pusher == null)
        {
            return false;
        }

        foreach (KeyValuePair<StandardBox, LinearPushState> entry in linearPushes)
        {
            LinearPushState state = entry.Value;
            if (!state.Active || state.ReleaseTransition || state.Pusher != pusher)
            {
                continue;
            }

            if (state.Direction != BoxPushDirection.Left && state.Direction != BoxPushDirection.Right)
            {
                continue;
            }

            box = entry.Key;
            direction = state.Direction;
            return true;
        }

        return false;
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

        // === 清除 chain follower state（由 TryAdvanceLinearPush 注册的非 root 成员状态）===
        // follower state 有 IsFollower == true 标记，直接移除即可。
        CollectHorizontalChain(box, axis, chainGroupScratch);
        for (int i = 1; i < chainGroupScratch.Count; i++)
        {
            StandardBox follower = chainGroupScratch[i];
            if (follower == null || follower == box)
            {
                continue;
            }
            if (linearPushes.TryGetValue(follower, out LinearPushState followerState)
                && followerState.Active
                && followerState.IsFollower)
            {
                linearPushes.Remove(follower);
            }
        }

        if (displacement >= halfCell - PushReleaseAdvanceTolerance)
        {
            // 通常 TryAdvance 已在 ≥50% 时推进过原点；这里覆盖刚好等于 50% 的边界情形。
            // 用 PushReleaseAdvanceTolerance 容差（不是 Mathf.Epsilon），是为了让墙体 ε 偏移
            // 导致的 displacement = halfCell - ε 场景也走前进分支——避免回退方向 release 撞玩家死锁。
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

    /// <summary>
    /// 线性推动释放过渡中箱子与 pusher 同向协同位移时，chain cast 会沿“回退”方向扫到身后的 pusher，
    /// 不应按砸落处理（例如推箱子时突然反向导致松手回退）。
    /// </summary>
    private static bool ShouldSuppressPlayerImpactDuringLinearReleaseCoMove(LinearPushState state, PlayerController hitPlayer)
    {
        return state.MovePusherWithBox &&
            state.Pusher != null &&
            hitPlayer != null &&
            hitPlayer.gameObject == state.Pusher;
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

            // remaining 已经在 ε 容差内：物理上很可能被墙/碰撞 ε 偏移卡住永远走不到精确 target，
            // 这种 case 下 chain block 的 "allowed < move - PushContactTolerance" 中止条件
            // 因为 move 本身就比 PushContactTolerance 小而恒为 false，会陷入死循环。
            // 直接 snap 到 target 完成 release（box.MoveTo 跳过物理 cast，可能有 ε 级穿模，
            // 但对单次对齐操作来说视觉上不可见，远好于死锁）。
            if (remaining < ReleaseSnapDistance)
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

            // 释放过渡阶段同样跑 chain block + vertical lower_only。
            // chain 收集要用"原推动方向"（state.Direction），不是 release 移动方向：
            //   推时 A 推 B 推 C，B/C 紧贴在 A 的右边；
            //   <50% release 回退向左时，若用 release 方向收集 chain，A 沿"左"找紧贴邻居什么也找不到，
            //   B、C 就会被遗忘留在不对齐位置。沿原推方向收集才能把 advance 阶段的 chain 成员一并带走/带回。
            Vector3 pushAxis = state.Direction == BoxPushDirection.Right ? Vector3.right : Vector3.left;
            CollectHorizontalChain(box, pushAxis, chainGroupScratch);
            CollectVerticalStackForChain(chainGroupScratch, stackGroupScratch);

            // 整 chain cast 取最小可移动距离（chain block）。
            float allowed = move;
            for (int j = 0; j < chainGroupScratch.Count; j++)
            {
                StandardBox member = chainGroupScratch[j];
                if (Cast(member, axis, allowed + skinWidth, out RayHit chainHit, chainGroupScratch))
                {
                    bool suppressPusherCoMoveHit = chainHit.Player != null &&
                        ShouldSuppressPlayerImpactDuringLinearReleaseCoMove(state, chainHit.Player);

                    // 松手/换向触发 <50% 回退时，箱子沿释放方向移向玩家一侧；Cast 从朝前的面射出会先扫到
                    // 正在协同让位的 pusher，误当成“砸落”致死。协同回退路径上跳过对 pusher 的 impact，
                    // 且不把该命中当作阻挡（否则 allowed≈0 会误删整条 release 会话）。
                    if (chainHit.Player != null && !suppressPusherCoMoveHit)
                    {
                        Vector3 memberFrom = member.transform.position;
                        TryHandlePlayerImpact(member, memberFrom, memberFrom + axis * allowed, dt);
                    }

                    if (!suppressPusherCoMoveHit)
                    {
                        allowed = Mathf.Min(allowed, Mathf.Max(0f, chainHit.Distance - skinWidth));
                    }
                    if (allowed <= 0f)
                    {
                        break;
                    }
                }
            }

            // 实际移动距离不到 move（被夹断）：把 pusher 多走的部分回滚保持贴合。
            if (pusherPlayer != null && allowed < move)
            {
                float rollback = allowed - move;
                pusherPlayer.ApplyExternalPositionDelta(new Vector3(dir * rollback, 0f, 0f));
            }

            Vector3 next = current + new Vector3(dir * allowed, 0f, 0f);

            // chain 整组同步移动 allowed。
            for (int j = 0; j < chainGroupScratch.Count; j++)
            {
                StandardBox member = chainGroupScratch[j];
                Vector3 memberFrom = member.transform.position;
                member.MoveTo(memberFrom + axis * allowed);
            }

            // vertical 跟随。
            BuildCombinedIgnore(chainGroupScratch, stackGroupScratch);
            ApplyStackedFollow(stackGroupScratch, axis, allowed, dt, combinedIgnoreScratch);

            // 释放过渡途中被夹断（chain block）：放弃后续滑行，保持当前位置；若仍未到达目标，作为最终结果（不再尝试对齐）。
            if (allowed < move - PushContactTolerance)
            {
                linearPushes.Remove(box);
                continue;
            }

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
        RefreshSceneMovableBaselineForStandardBox(box);
        NotifyStandardBoxHorizontalGridSettled(box);
    }

    private void NotifyStandardBoxHorizontalGridSettled(StandardBox box)
    {
        if (box == null)
        {
            return;
        }

        SendEvent(new StandardBoxHorizontalGridAlignedEvent(box, box.transform.position));
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
        /// <summary>
        /// true 表示这是由 TryAdvanceLinearPush 注册的 chain follower 轻量标记，
        /// 而非玩家直接驱动的推动会话。
        /// HasActiveLinearPushState 对 follower 返回 false（保持其继续参与 chain 移动），
        /// TryGetLinearPushDirection 对 follower 返回 true（让 PushableBoxService 能检测到它们）。
        /// </summary>
        public bool IsFollower;
    }

    public readonly struct RayHit
    {
        public readonly float Distance;
        public readonly PlayerController Player;

        public RayHit(float distance, PlayerController player)
        {
            Distance = distance;
            Player = player;
        }
    }
    public bool IsFalling(StandardBox box)
    {
        if (box == null)
        {
            return false;
        }

        return fallSpeeds.TryGetValue(box, out float speed) &&
               speed > PushContactTolerance;
    }

    private static BoxPushDirection VectorToDirection(Vector3 dir)
    {
        if (Mathf.Abs(dir.x) > Mathf.Abs(dir.y))
        {
            return dir.x > 0f ? BoxPushDirection.Right : BoxPushDirection.Left;
        }
        else
        {
            return dir.y > 0f ? BoxPushDirection.Up : BoxPushDirection.Down;
        }
    }

}
