using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

[DisallowMultipleComponent]
public partial class PlayerController : MonoBehaviour
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

    [Header("Collision")]
    [SerializeField] private LayerMask collisionMask = ~0;
    [SerializeField] private float skinWidth = 0.02f;
    [SerializeField, Min(2)] private int raysPerSide = 2;

    [Header("Push")]
    [SerializeField, Min(0f)] private float pushHoldThreshold = 0.12f;
    [SerializeField, Min(0f)] private float pushCooldown = 0.35f;

    public Grid Grid => grid;
    public ContactState Contacts => contacts;
    public Vector2 Velocity => velocity;

    private Grid grid;
    private Rigidbody body3D;
    private Rigidbody2D body2D;
    private Collider m_Collider3D;
    private Collider2D m_Collider2D;
    private ContactState contacts;
    private Vector2 moveInput;
    private Vector2 velocity;
    private bool jumpQueued;
    private bool jumping;
    private float jumpApexY;
    private float fixedZ;
    private StandardBox leftBox;
    private StandardBox rightBox;
    private StandardBox heldPushBox;
    private BoxPushDirection heldPushDirection;
    private bool heldPushInitializedCanPush;
    private bool hasHeldPushInitialization;
    private float pushHoldTime;
    private float nextPushTime;
    
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
        RefreshContacts();
        ProcessPlatformDropRequest();

        float dt = Time.fixedDeltaTime;
        velocity.x = Mathf.Clamp(moveInput.x, -1f, 1f) * moveSpeed;

        if (contacts.grounded && velocity.y < 0f)
        {
            velocity.y = 0f;
            jumping = false;
        }

        if (jumpQueued && contacts.grounded)
        {
            float height = Mathf.Max(0.01f, GetCellHeight() * jumpGridHeight);
            velocity.y = Mathf.Sqrt(2f * gravity * height);
            jumpApexY = transform.position.y + height;
            jumping = true;
        }

        jumpQueued = false;
        velocity.y = Mathf.Max(velocity.y - gravity * dt, -maxFallSpeed);

        Vector3 delta = new Vector3(velocity.x * dt, velocity.y * dt, 0f);
        if (jumping && transform.position.y + delta.y > jumpApexY)
        {
            delta.y = jumpApexY - transform.position.y;
            velocity.y = 0f;
            jumping = false;
        }

        MoveByAndResolve(new Vector3(delta.x, 0f, 0f));
        MoveByAndResolve(new Vector3(0f, delta.y, 0f));
        RefreshContacts();
        HandleBoxPush(dt);
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
        leftBox = leftHit.box;
        rightBox = rightHit.box;
    }

    private void HandleBoxPush(float dt)
    {
        StandardBox box = null;
        BoxPushDirection direction = default;

        if (moveInput.x > 0.01f && contacts.rightBlocked)
        {
            box = rightBox;
            direction = BoxPushDirection.Right;
        }
        else if (moveInput.x < -0.01f && contacts.leftBlocked)
        {
            box = leftBox;
            direction = BoxPushDirection.Left;
        }

        if (box == null)
        {
            heldPushBox = null;
            hasHeldPushInitialization = false;
            pushHoldTime = 0f;
            return;
        }

        if (box != heldPushBox || direction != heldPushDirection)
        {
            heldPushBox = box;
            heldPushDirection = direction;
            pushHoldTime = 0f;
            heldPushInitializedCanPush = box.InitializePush(direction, gameObject).CanPush;
            hasHeldPushInitialization = true;
        }

        pushHoldTime += dt;
        if (pushHoldTime < pushHoldThreshold || Time.time < nextPushTime)
        {
            return;
        }

        if (hasHeldPushInitialization)
        {
            box.TryPush(direction, gameObject, heldPushInitializedCanPush);
        }
        else
        {
            box.TryPush(direction, gameObject);
        }

        nextPushTime = Time.time + pushCooldown;
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
                velocity.x = 0f;
            }

            if (Mathf.Abs(correction.y) > 0f)
            {
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

    private Vector3 CalculateAabbSeparation(Bounds playerBounds, Bounds otherBounds, Vector3 movedDelta)
    {
        float moveLeft = otherBounds.min.x - playerBounds.max.x - OverlapResolveEpsilon;
        float moveRight = otherBounds.max.x - playerBounds.min.x + OverlapResolveEpsilon;
        float moveDown = otherBounds.min.y - playerBounds.max.y - OverlapResolveEpsilon;
        float moveUp = otherBounds.max.y - playerBounds.min.y + OverlapResolveEpsilon;

        if (Mathf.Abs(movedDelta.x) > 0f && Mathf.Abs(movedDelta.x) >= Mathf.Abs(movedDelta.y))
        {
            return new Vector3(movedDelta.x > 0f ? moveLeft : moveRight, 0f, 0f);
        }

        if (Mathf.Abs(movedDelta.y) > 0f)
        {
            return new Vector3(0f, movedDelta.y > 0f ? moveDown : moveUp, 0f);
        }

        Vector3 xCorrection = Mathf.Abs(moveLeft) < Mathf.Abs(moveRight)
            ? new Vector3(moveLeft, 0f, 0f)
            : new Vector3(moveRight, 0f, 0f);
        Vector3 yCorrection = Mathf.Abs(moveDown) < Mathf.Abs(moveUp)
            ? new Vector3(0f, moveDown, 0f)
            : new Vector3(0f, moveUp, 0f);

        return xCorrection.sqrMagnitude < yCorrection.sqrMagnitude ? xCorrection : yCorrection;
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

        int count = Mathf.Min(Mathf.Max(2, raysPerSide), 2);
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
