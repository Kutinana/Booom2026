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
        float resolved = ResolveVertical(box, distance);
        Vector3 to = from + Vector3.down * resolved;
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

    private float ResolveVertical(StandardBox box, float distance)
    {
        if (distance <= 0f)
        {
            return 0f;
        }

        if (Cast(box, Vector3.down, distance + skinWidth, out RayHit hit))
        {
            return Mathf.Max(0f, hit.Distance - skinWidth);
        }

        return distance;
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
                    hit = new RayHit(bestDistance);
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
                    hit = new RayHit(bestDistance);
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

        public RayHit(float distance)
        {
            Distance = distance;
        }
    }
}
