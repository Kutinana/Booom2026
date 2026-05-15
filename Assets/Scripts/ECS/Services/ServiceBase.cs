using System;
using System.Collections.Generic;
using QFramework;
using UnityEngine;

public enum BoxPushDirection
{
    Left,
    Right,
    Up,
    Down
}

[Flags]
public enum BoxPushDirectionMask
{
    None = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Up = 1 << 2,
    Down = 1 << 3,
    Horizontal = Left | Right,
    Vertical = Up | Down,
    All = Left | Right | Up | Down
}

public readonly struct BoxPushRequestEvent
{
    public readonly StandardBox Box;
    public readonly BoxPushDirection Direction;
    public readonly GameObject Pusher;

    public BoxPushRequestEvent(StandardBox box, BoxPushDirection direction, GameObject pusher)
    {
        Box = box;
        Direction = direction;
        Pusher = pusher;
    }
}

public readonly struct BoxPushInitializeEvent
{
    public readonly StandardBox Box;
    public readonly BoxPushDirection Direction;
    public readonly GameObject Pusher;
    public readonly bool CanPush;

    public BoxPushInitializeEvent(StandardBox box, BoxPushDirection direction, GameObject pusher, bool canPush)
    {
        Box = box;
        Direction = direction;
        Pusher = pusher;
        CanPush = canPush;
    }

    public override string ToString()
    {
        return $"BoxPushInitializeEvent(Box={Box.name}, Direction={Direction}, Pusher={Pusher?.name ?? "null"}, CanPush={CanPush})";
    }
}

public readonly struct BoxPushAttemptEvent
{
    public readonly StandardBox Box;
    public readonly BoxPushDirection Direction;
    public readonly GameObject Pusher;
    public readonly bool CanPush;

    public BoxPushAttemptEvent(StandardBox box, BoxPushDirection direction, GameObject pusher, bool canPush)
    {
        Box = box;
        Direction = direction;
        Pusher = pusher;
        CanPush = canPush;
    }
    public override string ToString()
    {
        return $"BoxPushAttemptEvent(Box={Box.name}, Direction={Direction}, Pusher={Pusher?.name ?? "null"}, CanPush={CanPush})";
    }
}

public readonly struct BoxPhysicalPushEvent
{
    public readonly StandardBox Box;
    public readonly BoxPushDirection Direction;
    public readonly bool Moved;
    public readonly bool IgnoredByPhysicalAxis;
    public readonly Vector3 From;
    public readonly Vector3 To;

    public BoxPhysicalPushEvent(
        StandardBox box,
        BoxPushDirection direction,
        bool moved,
        bool ignoredByPhysicalAxis,
        Vector3 from,
        Vector3 to)
    {
        Box = box;
        Direction = direction;
        Moved = moved;
        IgnoredByPhysicalAxis = ignoredByPhysicalAxis;
        From = from;
        To = to;
    }
    public override string ToString()
    {
        return $"BoxPhysicalPushEvent(Box={Box.name}, Direction={Direction}, Moved={Moved}, IgnoredByPhysicalAxis={IgnoredByPhysicalAxis}, From={From}, To={To})";
    }
}

public readonly struct BoxFallStateEvent
{
    public readonly StandardBox Box;
    public readonly bool Falling;
    public readonly bool Landed;
    public readonly Vector3 Position;

    public BoxFallStateEvent(StandardBox box, bool falling, bool landed, Vector3 position)
    {
        Box = box;
        Falling = falling;
        Landed = landed;
        Position = position;
    }
}

/// <summary>
/// <see cref="PhysicalBoxService"/> 在箱子完成「水平方向与 Grid 列对齐」后派发（整格推动、对齐释放过渡、落地后 X 对齐等）。
/// </summary>
public readonly struct StandardBoxHorizontalGridAlignedEvent
{
    public readonly StandardBox Box;
    public readonly Vector3 Position;

    public StandardBoxHorizontalGridAlignedEvent(StandardBox box, Vector3 position)
    {
        Box = box;
        Position = position;
    }
}

public interface IPlayerRelativePositionTarget
{
    Bounds Bounds { get; }
}

public readonly struct PressurePlateStateEvent
{
    public readonly PressurePlate PressurePlate;
    public readonly bool Pressed;
    public readonly bool WasPressed;

    public PressurePlateStateEvent(PressurePlate pressurePlate, bool pressed, bool wasPressed)
    {
        PressurePlate = pressurePlate;
        Pressed = pressed;
        WasPressed = wasPressed;
    }
}

public abstract class ServiceBase : MonoBehaviour
{
    private static readonly Dictionary<Type, ServiceBase> Services = new Dictionary<Type, ServiceBase>();

    protected bool IsActiveService { get; private set; }

    public static T Get<T>() where T : ServiceBase
    {
        ServiceBase service;
        if (Services.TryGetValue(typeof(T), out service) && service != null)
        {
            return service as T;
        }

        GameObject serviceObject = new GameObject($"Service.{typeof(T).Name}");
        return serviceObject.AddComponent<T>();
    }

    public static bool TryGet<T>(out T service) where T : ServiceBase
    {
        service = null;
        ServiceBase existing;
        if (!Services.TryGetValue(typeof(T), out existing) || existing == null)
        {
            return false;
        }

        service = existing as T;
        return service != null;
    }

    protected virtual void Awake()
    {
        Type serviceType = GetType();
        ServiceBase existing;
        if (Services.TryGetValue(serviceType, out existing) && existing != null && existing != this)
        {
            Destroy(this);
            return;
        }

        IsActiveService = true;
        Services[serviceType] = this;
        if (transform.parent != null)
        {
            transform.SetParent(null);
        }

        DontDestroyOnLoad(gameObject);
    }

    protected virtual void OnDestroy()
    {
        if (!IsActiveService)
        {
            return;
        }

        ServiceBase service;
        if (Services.TryGetValue(GetType(), out service) && service == this)
        {
            Services.Remove(GetType());
        }

        IsActiveService = false;
    }

    protected IUnRegister RegisterEvent<TEvent>(Action<TEvent> onEvent)
    {
        return TypeEventSystem.Global.Register(onEvent);
    }

    protected void SendEvent<TEvent>(TEvent e)
    {
        TypeEventSystem.Global.Send(e);
    }
}

public abstract class ServiceBase<TComponent> : ServiceBase where TComponent : Component
{
    private readonly HashSet<TComponent> components = new HashSet<TComponent>();

    public IReadOnlyCollection<TComponent> RegisteredComponents => components;

    public bool IsRegistered(TComponent component)
    {
        return component != null && components.Contains(component);
    }

    public virtual void Register(TComponent component)
    {
        if (component == null)
        {
            return;
        }

        components.Add(component);
    }

    public virtual void UnRegister(TComponent component)
    {
        if (component == null)
        {
            return;
        }

        components.Remove(component);
    }

    protected override void OnDestroy()
    {
        components.Clear();
        base.OnDestroy();
    }
}
