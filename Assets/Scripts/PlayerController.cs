using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public partial class PlayerController : MonoBehaviour
{
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
    [SerializeField, Min(2)] private int raysPerSide = 3;

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
    private float pushHoldTime;
    private float nextPushTime;
    
    private readonly Queue<Transform> bfsQueue = new Queue<Transform>(32);
    private readonly RaycastHit2D[] hits2D = new RaycastHit2D[8];
    private readonly RaycastHit[] hits3D = new RaycastHit[8];

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
        delta.x = ResolveAxis(Vector3.right * Mathf.Sign(delta.x), Mathf.Abs(delta.x));
        delta.y = ResolveAxis(Vector3.up * Mathf.Sign(delta.y), Mathf.Abs(delta.y));

        Vector3 next = transform.position + delta;
        if (jumping && next.y > jumpApexY)
        {
            next.y = jumpApexY;
            velocity.y = 0f;
            jumping = false;
        }

        next.z = fixedZ;
        MoveTo(next);
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
            pushHoldTime = 0f;
            return;
        }

        if (box != heldPushBox || direction != heldPushDirection)
        {
            heldPushBox = box;
            heldPushDirection = direction;
            pushHoldTime = 0f;
        }

        pushHoldTime += dt;
        if (pushHoldTime < pushHoldThreshold || Time.time < nextPushTime)
        {
            return;
        }

        box.TryPush(direction, gameObject);
        nextPushTime = Time.time + pushCooldown;
    }

    private float ResolveAxis(Vector3 direction, float distance)
    {
        if (distance <= 0f)
        {
            return 0f;
        }

        if (Cast(direction, distance + skinWidth, out RayHit hit))
        {
            if (hit.distance <= skinWidth)
            {
                if (direction.y != 0f)
                {
                    velocity.y = 0f;
                    jumping = false;
                }
                else
                {
                    velocity.x = 0f;
                }

                return 0f;
            }

            distance = Mathf.Max(0f, hit.distance - skinWidth);
            if (direction.y != 0f)
            {
                velocity.y = 0f;
                jumping = false;
            }
            else
            {
                velocity.x = 0f;
            }
        }

        return distance * Mathf.Sign(direction.x + direction.y);
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
                origin = new Vector3(Mathf.Lerp(bounds.min.x, bounds.max.x, t), direction.y > 0f ? bounds.max.y : bounds.min.y, transform.position.z);
            }
            else
            {
                origin = new Vector3(direction.x > 0f ? bounds.max.x : bounds.min.x, Mathf.Lerp(bounds.min.y, bounds.max.y, t), transform.position.z);
            }

            if (CastSingle(origin, direction, distance, out RayHit hit) && hit.distance < bestDistance)
            {
                bestDistance = hit.distance;
                bestHit = hit;
                hitAny = true;
            }
        }

        return hitAny;
    }

    private bool CastSingle(Vector3 origin, Vector3 direction, float distance, out RayHit hit)
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
                    ShouldCollideWithPlatform(platform, hit2D.normal, direction) &&
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
                    ShouldCollideWithPlatform(platform, hit3D.normal, direction) &&
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
            body2D.MovePosition((Vector2)position);
        }
        else if (body3D != null)
        {
            body3D.MovePosition(position);
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
