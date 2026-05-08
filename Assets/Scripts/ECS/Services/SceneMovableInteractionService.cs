using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(1000)]
public class SceneMovableInteractionService : ServiceBase
{
    private const float BoundsEpsilon = 0.001f;

    [SerializeField, Min(0f)] private float contactTolerance = 0.04f;
    [SerializeField, Min(0f)] private float minImpactSpeed = 0.1f;
    [SerializeField, Min(0f)] private float impactCooldown = 0.15f;
    [SerializeField, Min(1)] private int quadTreeCapacity = 8;
    [SerializeField, Min(0)] private int quadTreeMaxDepth = 6;

    private readonly HashSet<ISceneMovableItem> items = new HashSet<ISceneMovableItem>();
    private readonly List<ISceneMovableItem> itemSnapshot = new List<ISceneMovableItem>(32);
    private readonly List<ISceneMovableItem> candidates = new List<ISceneMovableItem>(16);
    private readonly Dictionary<ISceneMovableItem, Bounds> previousItemBounds = new Dictionary<ISceneMovableItem, Bounds>();
    private readonly Dictionary<ISceneMovableItem, float> nextImpactTimes = new Dictionary<ISceneMovableItem, float>();
    private Bounds previousPlayerBounds;
    private bool hasPreviousPlayerBounds;
    private PlayerBoundsProvider playerBoundsProvider;
    private QuadTree quadTree;

    public void Register(ISceneMovableItem item)
    {
        if (item == null)
        {
            return;
        }

        items.Add(item);
        SendEvent(new SceneMovableItemsChangedEvent());
    }

    public void UnRegister(ISceneMovableItem item)
    {
        if (item == null)
        {
            return;
        }

        items.Remove(item);
        previousItemBounds.Remove(item);
        nextImpactTimes.Remove(item);
        SendEvent(new SceneMovableItemsChangedEvent());
    }

    public int QueryOverlapping(Bounds queryBounds, List<ISceneMovableItem> results)
    {
        if (results == null)
        {
            return 0;
        }

        results.Clear();
        if (queryBounds.size == Vector3.zero)
        {
            return 0;
        }

        QuadTree queryTree = BuildCurrentBoundsQuadTree(queryBounds);
        queryTree.Query(queryBounds, results);
        return results.Count;
    }

    protected override void OnDestroy()
    {
        items.Clear();
        itemSnapshot.Clear();
        candidates.Clear();
        previousItemBounds.Clear();
        nextImpactTimes.Clear();
        base.OnDestroy();
    }

    private void FixedUpdate()
    {
        bool sceneMovableBoundsChanged = CollectSceneMovableSnapshot();
        if (sceneMovableBoundsChanged)
        {
            SendEvent(new SceneMovableItemsChangedEvent());
        }

        if (!ServiceBase.TryGet(out PlayerService playerService) || playerService.Player == null)
        {
            hasPreviousPlayerBounds = false;
            StoreItemBounds();
            return;
        }

        PlayerController player = playerService.Player;
        if (playerBoundsProvider == null || playerBoundsProvider.Player != player)
        {
            playerBoundsProvider = new PlayerBoundsProvider(player);
            hasPreviousPlayerBounds = false;
        }

        Bounds playerBounds = playerBoundsProvider.Bounds;
        if (playerBounds.size == Vector3.zero)
        {
            hasPreviousPlayerBounds = false;
            return;
        }

        if (!hasPreviousPlayerBounds)
        {
            PrimePreviousBounds(playerBounds);
            return;
        }

        BuildQuadTree(playerBounds);

        Bounds playerQueryBounds = EncapsulateXY(previousPlayerBounds, playerBounds);
        playerQueryBounds.Expand(contactTolerance * 2f);

        candidates.Clear();
        quadTree.Query(playerQueryBounds, candidates);

        float dt = Mathf.Max(Time.fixedDeltaTime, Mathf.Epsilon);
        Vector2 playerVelocity = (Vector2)(playerBounds.center - previousPlayerBounds.center) / dt;

        for (int i = 0; i < candidates.Count; i++)
        {
            ISceneMovableItem item = candidates[i];
            if (!IsValid(item) || Time.time < GetNextImpactTime(item))
            {
                continue;
            }

            Bounds itemBounds = item.BoundsProvider.Bounds;
            if (!previousItemBounds.TryGetValue(item, out Bounds previousBounds))
            {
                continue;
            }

            Vector2 itemVelocity = (Vector2)(itemBounds.center - previousBounds.center) / dt;
            Vector2 relativeDelta = ((Vector2)(itemBounds.center - previousBounds.center)) - ((Vector2)(playerBounds.center - previousPlayerBounds.center));
            Vector2 relativeVelocity = itemVelocity - playerVelocity;
            if (relativeVelocity.sqrMagnitude < minImpactSpeed * minImpactSpeed)
            {
                continue;
            }

            if (!TryGetSweptImpactFace(previousBounds, previousPlayerBounds, relativeDelta, out BoxPushDirection impactFace))
            {
                continue;
            }

            SceneMovablePlayerImpactContext context = new SceneMovablePlayerImpactContext(
                item,
                player,
                impactFace,
                relativeVelocity,
                itemVelocity,
                playerVelocity,
                itemBounds,
                playerBounds);

            bool handled = item.HandlePlayerImpact(context);
            SendEvent(new SceneMovablePlayerImpactEvent(context, handled));
            nextImpactTimes[item] = Time.time + impactCooldown;
        }

        StoreCurrentBounds(playerBounds);
    }

    private bool CollectSceneMovableSnapshot()
    {
        bool anyBoundsChanged = false;
        itemSnapshot.Clear();
        foreach (ISceneMovableItem item in items)
        {
            if (!IsValid(item))
            {
                continue;
            }

            Bounds bounds = item.BoundsProvider.Bounds;
            if (bounds.size == Vector3.zero)
            {
                continue;
            }

            if (!previousItemBounds.TryGetValue(item, out Bounds previousBounds) || HasBoundsChanged(previousBounds, bounds))
            {
                anyBoundsChanged = true;
            }

            itemSnapshot.Add(item);
        }

        return anyBoundsChanged;
    }

    private void PrimePreviousBounds(Bounds playerBounds)
    {
        previousPlayerBounds = playerBounds;
        hasPreviousPlayerBounds = true;
        StoreItemBounds();
    }

    private void StoreCurrentBounds(Bounds playerBounds)
    {
        previousPlayerBounds = playerBounds;
        hasPreviousPlayerBounds = true;
        StoreItemBounds();
    }

    private void StoreItemBounds()
    {
        for (int i = 0; i < itemSnapshot.Count; i++)
        {
            ISceneMovableItem item = itemSnapshot[i];
            if (IsValid(item))
            {
                previousItemBounds[item] = item.BoundsProvider.Bounds;
            }
        }
    }

    private bool IsValid(ISceneMovableItem item)
    {
        return item != null &&
            item.Owner != null &&
            item.IsSceneMovableActive &&
            item.BoundsProvider != null &&
            item.BoundsProvider.IsValid;
    }

    private float GetNextImpactTime(ISceneMovableItem item)
    {
        if (nextImpactTimes.TryGetValue(item, out float nextTime))
        {
            return nextTime;
        }

        return 0f;
    }

    private void BuildQuadTree(Bounds playerBounds)
    {
        Bounds rootBounds = playerBounds;
        for (int i = 0; i < itemSnapshot.Count; i++)
        {
            ISceneMovableItem item = itemSnapshot[i];
            if (!IsValid(item))
            {
                continue;
            }

            rootBounds.Encapsulate(GetTreeBounds(item));
        }

        rootBounds.Expand(1f);
        quadTree = new QuadTree(rootBounds, quadTreeCapacity, quadTreeMaxDepth);
        for (int i = 0; i < itemSnapshot.Count; i++)
        {
            ISceneMovableItem item = itemSnapshot[i];
            if (IsValid(item))
            {
                quadTree.Insert(item, GetTreeBounds(item));
            }
        }
    }

    private QuadTree BuildCurrentBoundsQuadTree(Bounds seedBounds)
    {
        Bounds rootBounds = seedBounds;
        foreach (ISceneMovableItem item in items)
        {
            if (!IsValid(item))
            {
                continue;
            }

            Bounds itemBounds = item.BoundsProvider.Bounds;
            if (itemBounds.size == Vector3.zero)
            {
                continue;
            }

            rootBounds.Encapsulate(itemBounds);
        }

        rootBounds.Expand(1f);
        QuadTree tree = new QuadTree(rootBounds, quadTreeCapacity, quadTreeMaxDepth);
        foreach (ISceneMovableItem item in items)
        {
            if (!IsValid(item))
            {
                continue;
            }

            Bounds itemBounds = item.BoundsProvider.Bounds;
            if (itemBounds.size != Vector3.zero)
            {
                tree.Insert(item, itemBounds);
            }
        }

        return tree;
    }

    private Bounds GetTreeBounds(ISceneMovableItem item)
    {
        Bounds bounds = item.BoundsProvider.Bounds;
        if (previousItemBounds.TryGetValue(item, out Bounds previousBounds))
        {
            bounds = EncapsulateXY(bounds, previousBounds);
        }

        bounds.Expand(contactTolerance * 2f);
        return bounds;
    }

    private bool TryGetSweptImpactFace(Bounds previousItem, Bounds previousPlayer, Vector2 relativeDelta, out BoxPushDirection face)
    {
        face = default;

        Bounds playerTarget = previousPlayer;
        playerTarget.Expand(contactTolerance * 2f);

        if (OverlapsXY(previousItem, playerTarget))
        {
            if (Mathf.Abs(relativeDelta.x) >= Mathf.Abs(relativeDelta.y))
            {
                if (Mathf.Abs(relativeDelta.x) <= BoundsEpsilon)
                {
                    return false;
                }

                face = relativeDelta.x > 0f ? BoxPushDirection.Right : BoxPushDirection.Left;
                return true;
            }

            if (Mathf.Abs(relativeDelta.y) <= BoundsEpsilon)
            {
                return false;
            }

            face = relativeDelta.y > 0f ? BoxPushDirection.Up : BoxPushDirection.Down;
            return true;
        }

        float xEntry;
        float xExit;
        if (relativeDelta.x > BoundsEpsilon)
        {
            xEntry = (playerTarget.min.x - previousItem.max.x) / relativeDelta.x;
            xExit = (playerTarget.max.x - previousItem.min.x) / relativeDelta.x;
        }
        else if (relativeDelta.x < -BoundsEpsilon)
        {
            xEntry = (playerTarget.max.x - previousItem.min.x) / relativeDelta.x;
            xExit = (playerTarget.min.x - previousItem.max.x) / relativeDelta.x;
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
        if (relativeDelta.y > BoundsEpsilon)
        {
            yEntry = (playerTarget.min.y - previousItem.max.y) / relativeDelta.y;
            yExit = (playerTarget.max.y - previousItem.min.y) / relativeDelta.y;
        }
        else if (relativeDelta.y < -BoundsEpsilon)
        {
            yEntry = (playerTarget.max.y - previousItem.min.y) / relativeDelta.y;
            yExit = (playerTarget.min.y - previousItem.max.y) / relativeDelta.y;
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
            face = relativeDelta.x > 0f ? BoxPushDirection.Right : BoxPushDirection.Left;
        }
        else
        {
            face = relativeDelta.y > 0f ? BoxPushDirection.Up : BoxPushDirection.Down;
        }

        return true;
    }

    private static Bounds EncapsulateXY(Bounds a, Bounds b)
    {
        Bounds result = a;
        result.Encapsulate(b);
        return result;
    }

    private static bool OverlapsXY(Bounds a, Bounds b)
    {
        return a.min.x < b.max.x &&
            a.max.x > b.min.x &&
            a.min.y < b.max.y &&
            a.max.y > b.min.y;
    }

    private static bool HasBoundsChanged(Bounds a, Bounds b)
    {
        return (a.center - b.center).sqrMagnitude > BoundsEpsilon * BoundsEpsilon ||
            (a.size - b.size).sqrMagnitude > BoundsEpsilon * BoundsEpsilon;
    }

    private sealed class PlayerBoundsProvider
    {
        private readonly Collider collider3D;
        private readonly Collider2D collider2D;

        public PlayerBoundsProvider(PlayerController player)
        {
            Player = player;
            collider3D = player.GetComponent<Collider>();
            collider2D = player.GetComponent<Collider2D>();
        }

        public PlayerController Player { get; }

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

                return Player != null ? new Bounds(Player.transform.position, Vector3.zero) : new Bounds(Vector3.zero, Vector3.zero);
            }
        }
    }

    private sealed class QuadTree
    {
        private readonly Bounds bounds;
        private readonly int capacity;
        private readonly int maxDepth;
        private readonly int depth;
        private readonly List<Entry> entries = new List<Entry>();
        private QuadTree[] children;

        public QuadTree(Bounds bounds, int capacity, int maxDepth)
            : this(bounds, capacity, maxDepth, 0)
        {
        }

        private QuadTree(Bounds bounds, int capacity, int maxDepth, int depth)
        {
            this.bounds = bounds;
            this.capacity = Mathf.Max(1, capacity);
            this.maxDepth = Mathf.Max(0, maxDepth);
            this.depth = depth;
        }

        public void Insert(ISceneMovableItem item, Bounds itemBounds)
        {
            if (children != null && TryGetContainingChild(itemBounds, out int childIndex))
            {
                children[childIndex].Insert(item, itemBounds);
                return;
            }

            entries.Add(new Entry(item, itemBounds));
            if (children == null && entries.Count > capacity && depth < maxDepth)
            {
                Subdivide();
            }
        }

        public void Query(Bounds queryBounds, List<ISceneMovableItem> results)
        {
            if (!IntersectsXY(bounds, queryBounds))
            {
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                if (IntersectsXY(entries[i].Bounds, queryBounds))
                {
                    results.Add(entries[i].Item);
                }
            }

            if (children == null)
            {
                return;
            }

            for (int i = 0; i < children.Length; i++)
            {
                children[i].Query(queryBounds, results);
            }
        }

        private void Subdivide()
        {
            children = new QuadTree[4];
            Vector3 center = bounds.center;
            Vector3 childSize = bounds.size;
            childSize.x *= 0.5f;
            childSize.y *= 0.5f;

            children[0] = new QuadTree(new Bounds(new Vector3(center.x - childSize.x * 0.25f, center.y - childSize.y * 0.25f, center.z), childSize), capacity, maxDepth, depth + 1);
            children[1] = new QuadTree(new Bounds(new Vector3(center.x + childSize.x * 0.25f, center.y - childSize.y * 0.25f, center.z), childSize), capacity, maxDepth, depth + 1);
            children[2] = new QuadTree(new Bounds(new Vector3(center.x - childSize.x * 0.25f, center.y + childSize.y * 0.25f, center.z), childSize), capacity, maxDepth, depth + 1);
            children[3] = new QuadTree(new Bounds(new Vector3(center.x + childSize.x * 0.25f, center.y + childSize.y * 0.25f, center.z), childSize), capacity, maxDepth, depth + 1);

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry entry = entries[i];
                if (TryGetContainingChild(entry.Bounds, out int childIndex))
                {
                    entries.RemoveAt(i);
                    children[childIndex].Insert(entry.Item, entry.Bounds);
                }
            }
        }

        private bool TryGetContainingChild(Bounds itemBounds, out int childIndex)
        {
            childIndex = -1;
            if (children == null)
            {
                return false;
            }

            for (int i = 0; i < children.Length; i++)
            {
                if (ContainsXY(children[i].bounds, itemBounds))
                {
                    childIndex = i;
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsXY(Bounds container, Bounds item)
        {
            return item.min.x >= container.min.x &&
                item.max.x <= container.max.x &&
                item.min.y >= container.min.y &&
                item.max.y <= container.max.y;
        }

        private static bool IntersectsXY(Bounds a, Bounds b)
        {
            return a.min.x <= b.max.x &&
                a.max.x >= b.min.x &&
                a.min.y <= b.max.y &&
                a.max.y >= b.min.y;
        }

        private readonly struct Entry
        {
            public readonly ISceneMovableItem Item;
            public readonly Bounds Bounds;

            public Entry(ISceneMovableItem item, Bounds bounds)
            {
                Item = item;
                Bounds = bounds;
            }
        }
    }
}
