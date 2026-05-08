using UnityEngine;

public interface ISceneMovableItem
{
    GameObject Owner { get; }
    ISceneMovableBoundsProvider BoundsProvider { get; }
    bool IsSceneMovableActive { get; }
    bool HandlePlayerImpact(SceneMovablePlayerImpactContext context);
}

public interface ISceneMovableBoundsProvider
{
    GameObject Owner { get; }
    Bounds Bounds { get; }
    bool IsValid { get; }
}

public sealed class ColliderSceneMovableBoundsProvider : ISceneMovableBoundsProvider
{
    private readonly GameObject owner;
    private readonly Transform fallbackTransform;
    private readonly Collider collider3D;
    private readonly Collider2D collider2D;

    public ColliderSceneMovableBoundsProvider(GameObject owner, Transform fallbackTransform, Collider collider3D, Collider2D collider2D)
    {
        this.owner = owner;
        this.fallbackTransform = fallbackTransform;
        this.collider3D = collider3D;
        this.collider2D = collider2D;
    }

    public GameObject Owner => owner;

    public Bounds Bounds
    {
        get
        {
            if (collider2D != null)
            {
                return collider2D.bounds;
            }

            if (collider3D != null)
            {
                return collider3D.bounds;
            }

            return fallbackTransform != null ? new Bounds(fallbackTransform.position, Vector3.zero) : new Bounds(Vector3.zero, Vector3.zero);
        }
    }

    public bool IsValid
    {
        get
        {
            if (owner == null)
            {
                return false;
            }

            return collider2D != null || collider3D != null || fallbackTransform != null;
        }
    }
}

public readonly struct SceneMovablePlayerImpactContext
{
    public readonly ISceneMovableItem Item;
    public readonly PlayerController Player;
    public readonly BoxPushDirection ItemFace;
    public readonly Vector2 RelativeVelocity;
    public readonly Vector2 ItemVelocity;
    public readonly Vector2 PlayerVelocity;
    public readonly Bounds ItemBounds;
    public readonly Bounds PlayerBounds;

    public SceneMovablePlayerImpactContext(
        ISceneMovableItem item,
        PlayerController player,
        BoxPushDirection itemFace,
        Vector2 relativeVelocity,
        Vector2 itemVelocity,
        Vector2 playerVelocity,
        Bounds itemBounds,
        Bounds playerBounds)
    {
        Item = item;
        Player = player;
        ItemFace = itemFace;
        RelativeVelocity = relativeVelocity;
        ItemVelocity = itemVelocity;
        PlayerVelocity = playerVelocity;
        ItemBounds = itemBounds;
        PlayerBounds = playerBounds;
    }
}

public readonly struct SceneMovablePlayerImpactEvent
{
    public readonly SceneMovablePlayerImpactContext Context;
    public readonly bool Handled;

    public SceneMovablePlayerImpactEvent(SceneMovablePlayerImpactContext context, bool handled)
    {
        Context = context;
        Handled = handled;
    }
}

public readonly struct SceneMovableItemsChangedEvent
{
}

public readonly struct PlayerDeathEvent
{
    public readonly PlayerController Player;
    public readonly string Reason;
    public readonly GameObject Source;

    public PlayerDeathEvent(PlayerController player, string reason, GameObject source)
    {
        Player = player;
        Reason = reason;
        Source = source;
    }
}
