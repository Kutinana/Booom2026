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

    private readonly Dictionary<StandardBox, float> fallSpeeds = new Dictionary<StandardBox, float>();
    private readonly RaycastHit2D[] hits2D = new RaycastHit2D[8];
    private readonly RaycastHit[] hits3D = new RaycastHit[8];
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
    }

    private void OnPushAttempted(BoxPushAttemptEvent e)
    {
        if (!e.CanPush || e.Box == null || !IsRegistered(e.Box))
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

        Collider2D boxCollider2D = box.Collider2D;
        if (boxCollider2D != null)
        {
            int hitCount = Physics2D.RaycastNonAlloc((Vector2)origin, (Vector2)direction, hits2D, distance, box.CollisionMask);
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit2D hit2D = hits2D[i];
                if (hit2D.collider != null && !hit2D.collider.isTrigger && hit2D.collider != boxCollider2D && hit2D.distance < bestDistance)
                {
                    bestDistance = hit2D.distance;
                    hit = new RayHit(bestDistance, hit2D.collider.GetComponentInParent<PlayerController>());
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
                if (hit3D.collider != null && hit3D.collider != boxCollider3D && hit3D.distance < bestDistance)
                {
                    bestDistance = hit3D.distance;
                    hit = new RayHit(bestDistance, hit3D.collider.GetComponentInParent<PlayerController>());
                }
            }
        }

        return bestDistance < float.PositiveInfinity;
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
