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

public abstract class ServiceBase : MonoBehaviour
{
    private static readonly Dictionary<Type, ServiceBase> Services = new Dictionary<Type, ServiceBase>();

    public static T Get<T>() where T : ServiceBase
    {
        ServiceBase service;
        if (Services.TryGetValue(typeof(T), out service))
        {
            return service as T;
        }

        return FindObjectOfType<T>();
    }

    protected virtual void Awake()
    {
        Services[GetType()] = this;
    }

    protected virtual void OnDestroy()
    {
        ServiceBase service;
        if (Services.TryGetValue(GetType(), out service) && service == this)
        {
            Services.Remove(GetType());
        }
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
