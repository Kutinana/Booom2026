using System.Collections.Generic;
using QFramework;
using UnityEngine;

[DisallowMultipleComponent]
public class StandardBox : MonoBehaviour, ISceneMovableItem
{
    [Header("Grid")]
    [SerializeField] private Vector3 cellOffset = new Vector3(0.5f, 0.5f, 0f);
    public bool AlignToGrid = true;

    [Header("Push")]
    [SerializeField] public BoxPushDirectionMask pushableFrom = BoxPushDirectionMask.Horizontal;

    [Header("Collision")]
    [SerializeField] private LayerMask collisionMask = ~0;

    [Header("Physical Simulation")]
    public bool ApplyGravity = true;
    public bool FreezeHorizontalMovement = false;
    private float lockedX;
    private bool hasLockedX;

    /// <summary>当前所在 WorldBox（进入后设置，退出后清空），替代原来的 transform 父子关系。</summary>
    [System.NonSerialized] public WorldBox CurrentWorldBox;

    public Vector3 CellOffset => cellOffset;
    public LayerMask CollisionMask => collisionMask;
    public Grid Grid => grid;
    public Collider Collider3D => _collider3D;
    public Collider2D Collider2D => _collider2D;
    public GameObject Owner => gameObject;
    public ISceneMovableBoundsProvider BoundsProvider => sceneMovableBoundsProvider;
    public bool IsSceneMovableActive => isActiveAndEnabled;

    public Bounds Bounds
    {
        get
        {
            if (_collider2D != null)
            {
                return _collider2D.bounds;
            }

            return _collider3D != null ? _collider3D.bounds : new Bounds(transform.position, Vector3.zero);
        }
    }

    private Grid grid;
    private Rigidbody body3D;
    private Rigidbody2D body2D;
    private Collider _collider3D;
    private Collider2D _collider2D;
    private ColliderSceneMovableBoundsProvider sceneMovableBoundsProvider;
    private readonly Queue<Transform> bfsQueue = new Queue<Transform>(32);

    protected virtual void Awake()
    {
        body3D = GetComponent<Rigidbody>();
        body2D = GetComponent<Rigidbody2D>();
        _collider3D = GetComponent<Collider>();
        _collider2D = GetComponent<Collider2D>();
        sceneMovableBoundsProvider = new ColliderSceneMovableBoundsProvider(gameObject, transform, _collider3D, _collider2D);

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

        grid = FindSceneGrid();
        RegisterToServices();
        lockedX = transform.position.x;
        hasLockedX = true;
    }

    protected virtual void Start()
    {
        SnapToGrid();
    }

    protected virtual void OnDestroy()
    {
        UnRegisterFromServices();
    }

    public bool CanPushFrom(BoxPushDirection direction)
    {
        if (FreezeHorizontalMovement &&
            (direction == BoxPushDirection.Left || direction == BoxPushDirection.Right))
        {
            return false;
        }

        return (pushableFrom & ToMask(direction)) != 0;
    }

    public bool CanPushToward(BoxPushDirection direction)
    {
        return CanPushFrom(Opposite(direction));
    }

    public BoxPushInitializeEvent InitializePush(BoxPushDirection direction, GameObject pusher = null)
    {
        PushableBoxService service = ServiceBase.Get<PushableBoxService>();
        if (service != null)
        {
            return service.InitializePush(this, direction, pusher);
        }

        BoxPushInitializeEvent initialize = new BoxPushInitializeEvent(this, direction, pusher, false);
        TypeEventSystem.Global.Send(initialize);
        return initialize;
    }

    public BoxPushAttemptEvent TryPush(BoxPushDirection direction, GameObject pusher = null)
    {
        PushableBoxService service = ServiceBase.Get<PushableBoxService>();
        if (service != null)
        {
            return service.TryPush(this, direction, pusher);
        }

        BoxPushAttemptEvent attempt = new BoxPushAttemptEvent(this, direction, pusher, false);
        TypeEventSystem.Global.Send(attempt);
        return attempt;
    }

    public BoxPushAttemptEvent TryPush(BoxPushDirection direction, GameObject pusher, bool initializedCanPush)
    {
        PushableBoxService service = ServiceBase.Get<PushableBoxService>();
        if (service != null)
        {
            return service.TryPush(this, direction, pusher, initializedCanPush);
        }

        BoxPushAttemptEvent attempt = new BoxPushAttemptEvent(this, direction, pusher, false);
        TypeEventSystem.Global.Send(attempt);
        return attempt;
    }

    public void SendPushRequest(BoxPushDirection direction, GameObject pusher = null)
    {
        TypeEventSystem.Global.Send(new BoxPushRequestEvent(this, direction, pusher));
    }

    public virtual bool HandlePlayerImpact(SceneMovablePlayerImpactContext context)
    {
        TypeEventSystem.Global.Send(new PlayerDeathEvent(context.Player, "砸死", gameObject));
        return true;
    }

    public void MoveTo(Vector3 position)
    {
        if (FreezeHorizontalMovement)
        {
            if (!hasLockedX)
            {
                lockedX = transform.position.x;
                hasLockedX = true;
            }

            position.x = lockedX;
        }
        else
        {
            lockedX = position.x;
            hasLockedX = true;
        }

        // 同时更新 body 与 transform，使后续同帧内的位置读取/碰撞查询立即拿到新位置；
        // Rigidbody2D.MovePosition 是延迟到下个物理步的，会让线性推动期间玩家与箱子位置不同步而出现抖动。
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

    private void RegisterToServices()
    {
        ServiceBase.Get<PushableBoxService>()?.Register(this);
        ServiceBase.Get<PhysicalBoxService>()?.Register(this);
        ServiceBase.Get<SceneMovableInteractionService>()?.Register(this);
    }

    private void UnRegisterFromServices()
    {
        if (ServiceBase.TryGet(out PushableBoxService pushableBoxService))
        {
            pushableBoxService.UnRegister(this);
        }

        if (ServiceBase.TryGet(out PhysicalBoxService physicalBoxService))
        {
            physicalBoxService.UnRegister(this);
        }

        if (ServiceBase.TryGet(out SceneMovableInteractionService sceneMovableInteractionService))
        {
            sceneMovableInteractionService.UnRegister(this);
        }
    }

    private void SnapToGrid()
    {
        if (!AlignToGrid || grid == null)
        {
            return;
        }

        Vector3Int cell = grid.WorldToCell(transform.position);
        Vector3 snapped = grid.CellToWorld(cell) + Vector3.Scale(grid.cellSize, cellOffset);
        snapped.z = transform.position.z;
        MoveTo(snapped);
    }

    private Grid FindSceneGrid()
    {
        Grid found = null;
        bfsQueue.Clear();

        foreach (GameObject root in gameObject.scene.GetRootGameObjects())
        {
            bfsQueue.Enqueue(root.transform);
        }

        while (bfsQueue.Count > 0)
        {
            Transform current = bfsQueue.Dequeue();
            Grid candidate = current.GetComponent<Grid>();
            if (candidate != null)
            {
                return candidate;
            }

            for (int i = 0; i < current.childCount; i++)
            {
                bfsQueue.Enqueue(current.GetChild(i));
            }
        }

        return found;
    }

    public static BoxPushDirectionMask ToMask(BoxPushDirection direction)
    {
        switch (direction)
        {
            case BoxPushDirection.Left:
                return BoxPushDirectionMask.Left;
            case BoxPushDirection.Right:
                return BoxPushDirectionMask.Right;
            case BoxPushDirection.Up:
                return BoxPushDirectionMask.Up;
            case BoxPushDirection.Down:
                return BoxPushDirectionMask.Down;
            default:
                return BoxPushDirectionMask.None;
        }
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
    [Header("Test")]
    public BoxPushDirection TestDirection;
    [ContextMenu("Test")]
    public void Test()
    {
#if UNITY_EDITOR
        Debug.Log(TryPush(TestDirection));
#endif
    }

    public virtual void SetPushableFrom(BoxPushDirectionMask newMask)
    {
        pushableFrom = newMask;
    }

    public virtual void AddPushableDirection(BoxPushDirectionMask direction)
    {
        pushableFrom |= direction;
    }

    public virtual void RemovePushableDirection(BoxPushDirectionMask direction)
    {
        pushableFrom &= ~direction;
    }

    public virtual void ClearPushableDirection()
    {
        pushableFrom = BoxPushDirectionMask.None;
    }
    public void AddPushableLeft()
    {
        AddPushableDirection(BoxPushDirectionMask.Left);
    }

    public void AddPushableRight()
    {
        AddPushableDirection(BoxPushDirectionMask.Right);
    }

    public void AddPushableUp()
    {
        AddPushableDirection(BoxPushDirectionMask.Up);
    }

    public void AddPushableDown()
    {
        AddPushableDirection(BoxPushDirectionMask.Down);
    }

    public void RemovePushableLeft()
    {
        RemovePushableDirection(BoxPushDirectionMask.Left);
    }

    public void RemovePushableRight()
    {
        RemovePushableDirection(BoxPushDirectionMask.Right);
    }

    public void RemovePushableUp()
    {
        RemovePushableDirection(BoxPushDirectionMask.Up);
    }

    public void RemovePushableDown()
    {
        RemovePushableDirection(BoxPushDirectionMask.Down);
    }


}
