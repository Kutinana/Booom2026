using System.Collections.Generic;
using UnityEngine;

public class PlayerRelativePositionService : ServiceBase<Component>
{
    private const string PlayerTag = "Player";

    [Header("Probe")]
    [SerializeField, Min(0f)] private float verticalProbeDistance = 0.08f;
    [SerializeField, Min(0f)] private float contactTolerance = 0.03f;
    [SerializeField, Min(0f)] private float horizontalTolerance = 0.02f;
    [SerializeField, Range(0f, 1f)] private float minHorizontalOverlapRatio = 0.2f;

    private readonly Dictionary<Component, PlayerRelativePositionState> states = new Dictionary<Component, PlayerRelativePositionState>();
    private Transform playerTransform;
    private Collider playerCollider3D;
    private Collider2D playerCollider2D;

    public override void Register(Component component)
    {
        if (component != null && !(component is IPlayerRelativePositionTarget))
        {
            return;
        }

        base.Register(component);
        if (component != null && !states.ContainsKey(component))
        {
            states.Add(component, default);
        }
    }

    public override void UnRegister(Component component)
    {
        base.UnRegister(component);
        if (component != null)
        {
            states.Remove(component);
        }
    }

    public bool TryGetState(Component target, out PlayerRelativePositionState state)
    {
        state = default;
        IPlayerRelativePositionTarget positionTarget = target as IPlayerRelativePositionTarget;
        if (target == null || positionTarget == null || !EnsurePlayer())
        {
            return false;
        }

        state = CalculateState(positionTarget.Bounds, GetPlayerBounds());
        return true;
    }

    public bool IsPlayerAbove(Component target)
    {
        return TryGetState(target, out PlayerRelativePositionState state) && state.PlayerAbove;
    }

    public bool IsPlayerBelow(Component target)
    {
        return TryGetState(target, out PlayerRelativePositionState state) && state.PlayerBelow;
    }

    private void FixedUpdate()
    {
        if (!EnsurePlayer())
        {
            return;
        }

        Bounds playerBounds = GetPlayerBounds();
        foreach (Component target in RegisteredComponents)
        {
            IPlayerRelativePositionTarget positionTarget = target as IPlayerRelativePositionTarget;
            if (target == null || positionTarget == null)
            {
                continue;
            }

            PlayerRelativePositionState current = CalculateState(positionTarget.Bounds, playerBounds);
            PlayerRelativePositionState previous;
            states.TryGetValue(target, out previous);
            states[target] = current;

            if (current.PlayerAbove != previous.PlayerAbove || current.PlayerBelow != previous.PlayerBelow)
            {
                SendEvent(new PlayerRelativePositionEvent(target, current, previous));
            }
        }
    }

    protected override void OnDestroy()
    {
        states.Clear();
        base.OnDestroy();
    }

    private bool EnsurePlayer()
    {
        if (playerTransform != null)
        {
            return true;
        }

        GameObject playerObject = GameObject.FindGameObjectWithTag(PlayerTag);
        if (playerObject == null)
        {
            return false;
        }

        playerTransform = playerObject.transform;
        playerCollider3D = playerObject.GetComponent<Collider>();
        playerCollider2D = playerObject.GetComponent<Collider2D>();
        return true;
    }

    private PlayerRelativePositionState CalculateState(Bounds targetBounds, Bounds playerBounds)
    {
        if (targetBounds.size == Vector3.zero || playerBounds.size == Vector3.zero || !HasEnoughHorizontalOverlap(targetBounds, playerBounds))
        {
            return new PlayerRelativePositionState(false, false, targetBounds, playerBounds);
        }

        float range = verticalProbeDistance + contactTolerance;
        float distanceAbove = playerBounds.min.y - targetBounds.max.y;
        float distanceBelow = targetBounds.min.y - playerBounds.max.y;
        bool playerAbove = distanceAbove >= -contactTolerance && distanceAbove <= range;
        bool playerBelow = distanceBelow >= -contactTolerance && distanceBelow <= range;
        return new PlayerRelativePositionState(playerAbove, playerBelow, targetBounds, playerBounds);
    }

    private Bounds GetPlayerBounds()
    {
        if (playerCollider2D != null)
        {
            return playerCollider2D.bounds;
        }

        return playerCollider3D != null ? playerCollider3D.bounds : new Bounds(playerTransform.position, Vector3.zero);
    }

    private bool HasEnoughHorizontalOverlap(Bounds boxBounds, Bounds playerBounds)
    {
        float minX = Mathf.Max(boxBounds.min.x, playerBounds.min.x);
        float maxX = Mathf.Min(boxBounds.max.x, playerBounds.max.x);
        float overlap = maxX - minX + horizontalTolerance * 2f;
        if (overlap <= 0f)
        {
            return false;
        }

        float referenceWidth = Mathf.Min(boxBounds.size.x, playerBounds.size.x);
        if (referenceWidth <= Mathf.Epsilon)
        {
            return true;
        }

        return overlap / referenceWidth >= minHorizontalOverlapRatio;
    }
}
