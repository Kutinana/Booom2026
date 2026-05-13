using QFramework;
using UnityEngine;

[DefaultExecutionOrder(1100)]
public class WorldBox : StandardBox
{
    [System.Serializable]
    public struct DirectionEntrance
    {
        public BoxPushDirection Direction;
        public Transform Entrance;
    }

    public Collider OuterQuadCollider;
    public Collider InnerQuadCollider;
    public DirectionEntrance[] OuterEntrances;

    public bool HasLastExitDirection { get; private set; }
    public BoxPushDirection LastExitDirection { get; private set; }

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

    private void OnEnable()
    {
        pushInitializeUnRegister?.UnRegister();
        pushAttemptUnRegister?.UnRegister();
        pushInitializeUnRegister = TypeEventSystem.Global.Register<BoxPushInitializeEvent>(OnPushInitialized);
        pushAttemptUnRegister = TypeEventSystem.Global.Register<BoxPushAttemptEvent>(OnPushAttempted);
    }

    private void OnDisable()
    {
        pushInitializeUnRegister?.UnRegister();
        pushAttemptUnRegister?.UnRegister();
        pushInitializeUnRegister = null;
        pushAttemptUnRegister = null;
        ClearExitBlocker();
    }

    protected override void OnDestroy()
    {
        ClearExitBlocker();
        base.OnDestroy();
    }

    private void FixedUpdate()
    {
        if (!EnsurePlayer())
        {
            ClearExitBlocker();
            return;
        }

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

        // if (playerOverlapsInnerBounds && wasPlayerOutsideInnerBounds)
        // {
        //     BoxPushDirection t_direction = GetInnerEntryDirection(innerBounds, hasPreviousPlayerBounds ? previousPlayerBounds : playerBounds);
        //     if (TryMovePlayerToOuterEntrance(t_direction))
        //     {
        //         HasLastExitDirection = true;
        //         LastExitDirection = t_direction;
        //         wasPlayerInOuterBounds = false;
        //         wasPlayerOutsideInnerBounds = true;
        //         hasPreviousPlayerBounds = false;
        //         return;
        //     }
        // }

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
        HasLastExitDirection = true;
        LastExitDirection = direction;
        wasPlayerInOuterBounds = false;
        MovePlayerToInnerSide(direction, outerBounds, innerBounds, playerBounds);
        wasPlayerOutsideInnerBounds = true;
        hasPreviousPlayerBounds = false;
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

    private void OnPushInitialized(BoxPushInitializeEvent e)
    {
        if (e.Box != this || e.CanPush || !EnsurePlayer())
        {
            return;
        }

        MovePlayerToOuterEntranceFromBlockedPush(e.Direction);
    }

    private void OnPushAttempted(BoxPushAttemptEvent e)
    {
        if (e.Box != this || e.CanPush || !EnsurePlayer())
        {
            return;
        }

        MovePlayerToOuterEntranceFromBlockedPush(e.Direction);
    }

    private void MovePlayerToOuterEntranceFromBlockedPush(BoxPushDirection direction)
    {
        BoxPushDirection side = Opposite(direction);
        TeleportPlayerToOuterEntrance(side);
    }

    public override bool HandlePlayerImpact(SceneMovablePlayerImpactContext context)
    {
        if (CanPushFrom(context.ItemFace))
        {
            return base.HandlePlayerImpact(context);
        }

        if (!EnsurePlayer())
        {
            return false;
        }

        var t = TeleportPlayerToOuterEntrance(context.ItemFace);
        if (t)
        {
            playerController?.ApplyExternalVelocity(-context.RelativeVelocity);
        }

        Debug.Log($"Handled player impact on WorldBox. Teleported: {t}");
        return t;
    }

    private bool TeleportPlayerToOuterEntrance(BoxPushDirection side)
    {
        if (!TryMovePlayerToOuterEntrance(side))
        {
            return false;
        }

        HasLastExitDirection = true;
        LastExitDirection = side;
        wasPlayerInOuterBounds = false;
        wasPlayerOutsideInnerBounds = true;
        hasPreviousPlayerBounds = false;
        ClearExitBlocker();
        return true;
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
    [SerializeField, Min(0f)] private float outerEdgeBlockerTouchTolerance = 0.04f;

    private void MovePlayerToInnerSide(BoxPushDirection direction, Bounds outerBounds, Bounds innerBounds, Bounds playerBounds)
    {
        if (!TryGetInnerTargetPosition(direction, outerBounds, innerBounds, playerBounds, out Vector3 position))
        {
            return;
        }

        MovePlayer(position);
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
                position.y = RemapClamped(playerBounds.center.y, outerBounds.min.y, outerBounds.max.y, innerInsideMinY, innerInsideMaxY)+ paddingY;
                break;
            case BoxPushDirection.Right:
                position.x = innerBounds.max.x + extents.x + paddingX;
                position.y = RemapClamped(playerBounds.center.y, outerBounds.min.y, outerBounds.max.y, innerInsideMinY, innerInsideMaxY) + paddingY;
                break;
            case BoxPushDirection.Down:
                position.x = RemapClamped(playerBounds.center.x, outerBounds.min.x, outerBounds.max.x, innerInsideMinX, innerInsideMaxX);
                position.y = innerBounds.min.y - extents.y;
                break;
            case BoxPushDirection.Up:
                position.x = RemapClamped(playerBounds.center.x, outerBounds.min.x, outerBounds.max.x, innerInsideMinX, innerInsideMaxX);
                position.y = innerBounds.max.y + extents.y;
                break;
        }

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
        if (playerTransform != null)
        {
            targetBounds.center += targetPosition - playerTransform.position;
        }
        else
        {
            targetBounds.center = targetPosition;
        }

        return true;
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
        return service.TryRefreshBlockerForStaticInnerHit(this, direction, outerBounds, innerTargetBounds, playerBounds, CollisionMask, use2D, use3D);
    }

    private WorldBoxExitBlockerService GetExitBlockerService()
    {
        if (exitBlockerService == null)
        {
            exitBlockerService = ServiceBase.Get<WorldBoxExitBlockerService>();
        }

        return exitBlockerService;
    }

    private void ClearExitBlocker()
    {
        if (exitBlockerService == null && ServiceBase.TryGet(out WorldBoxExitBlockerService service))
        {
            exitBlockerService = service;
        }

        exitBlockerService?.Clear(this);
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

    private bool TryMovePlayerToOuterEntrance(BoxPushDirection direction)
    {
        Transform entrance = GetOuterEntrance(direction);
        if (entrance == null)
        {
            return false;
        }

        MovePlayer(entrance.position);
        return true;
    }

    private Transform GetOuterEntrance(BoxPushDirection direction)
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

    private void MovePlayer(Vector3 position)
    {
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
