using System.Collections.Generic;
using QFramework;
using UnityEngine;

[DefaultExecutionOrder(1200)]
public class PressurePlateService : ServiceBase<PressurePlate>
{
    private float activationPadding = 0.04f;

    private readonly List<PressurePlate> plateSnapshot = new List<PressurePlate>(16);
    private readonly List<ISceneMovableItem> movableHits = new List<ISceneMovableItem>(16);
    private readonly Dictionary<PressurePlate, bool> worldBoxHorizontalPushPlayerEverOnPlate = new Dictionary<PressurePlate, bool>(16);

    protected override void OnDestroy()
    {
        worldBoxHorizontalPushPlayerEverOnPlate.Clear();
        base.OnDestroy();
    }

    private void FixedUpdate()
    {
        if (!IsActiveService)
        {
            return;
        }

        RefreshAll();
    }

    public override void Register(PressurePlate component)
    {
        base.Register(component);
        RefreshAll();
    }

    public override void UnRegister(PressurePlate component)
    {
        base.UnRegister(component);
        RefreshAll();
    }

    private void RefreshAll()
    {
        ServiceBase.TryGet(out SceneMovableInteractionService sceneMovableService);
        bool worldBoxHorizontalPush = false;
        Bounds playerBounds = default;
        bool hasPlayerBounds = false;
        if (ServiceBase.TryGet(out PhysicalBoxService physicalBoxService) &&
            ServiceBase.TryGet(out PlayerService playerService) &&
            playerService.Player != null &&
            physicalBoxService.TryGetActiveLinearHorizontalPushForPusher(playerService.Player.gameObject, out StandardBox pushedBox, out _) &&
            pushedBox is WorldBox)
        {
            worldBoxHorizontalPush = true;
            hasPlayerBounds = playerService.TryGetPlayerWorldBounds(out playerBounds);
        }

        if (!worldBoxHorizontalPush)
        {
            worldBoxHorizontalPushPlayerEverOnPlate.Clear();
        }

        FillPlateSnapshot();

        for (int i = 0; i < plateSnapshot.Count; i++)
        {
            PressurePlate pressurePlate = plateSnapshot[i];
            Bounds plateBounds = ExpandTowardLocalUp(pressurePlate.transform, pressurePlate.Bounds, activationPadding);
            bool playerOnThis = worldBoxHorizontalPush && hasPlayerBounds && IntersectsXY(playerBounds, plateBounds);
            if (worldBoxHorizontalPush && playerOnThis)
            {
                worldBoxHorizontalPushPlayerEverOnPlate[pressurePlate] = true;
            }

            bool hadPlayerEverOnThisPlate = worldBoxHorizontalPushPlayerEverOnPlate.TryGetValue(pressurePlate, out bool had) && had;
            bool defaultPressed = sceneMovableService != null && IsPressedBySceneMovable(pressurePlate, sceneMovableService);
            bool pressed;
            if (worldBoxHorizontalPush && hadPlayerEverOnThisPlate && !playerOnThis)
            {
                pressed = false;
            }
            else
            {
                pressed = defaultPressed || (worldBoxHorizontalPush && playerOnThis);
            }

            pressurePlate.SetPressed(pressed);
        }
    }

    public bool QueryWorldBoxGridSnapStablePressingAnyRegisteredPlateXY(StandardBox box)
    {
        if (box == null || box.Bounds.size == Vector3.zero)
        {
            return false;
        }

        if (box.Grid == null || !box.AlignToGrid)
        {
            return QueryBoundsOverlapAnyRegisteredPlateXY(box.Bounds);
        }

        if (!ServiceBase.TryGet(out PhysicalBoxService physicalBoxService) || !physicalBoxService.IsNearHorizontalGridCenter(box))
        {
            return false;
        }

        return QueryBoundsOverlapAnyRegisteredPlateXY(box.Bounds);
    }

    public void SnapBoxXToNearestHorizontalGridIfOverlappingAnyPlate(StandardBox box)
    {
        if (box == null || !box.AlignToGrid || !QueryBoundsOverlapAnyRegisteredPlateXY(box.Bounds))
        {
            return;
        }

        if (!ServiceBase.TryGet(out PhysicalBoxService physicalBoxService) ||
            !physicalBoxService.TryGetHorizontalGridAlignedWorldX(box, out float alignedX))
        {
            return;
        }

        Vector3 position = box.transform.position;
        position.x = alignedX;
        box.MoveTo(position);
    }

    public bool QueryBoundsOverlapAnyRegisteredPlateXY(Bounds worldBounds)
    {
        if (worldBounds.size == Vector3.zero)
        {
            return false;
        }

        FillPlateSnapshot();

        for (int i = 0; i < plateSnapshot.Count; i++)
        {
            PressurePlate pressurePlate = plateSnapshot[i];
            Bounds plateBounds = ExpandTowardLocalUp(pressurePlate.transform, pressurePlate.Bounds, activationPadding);
            if (IntersectsXY(worldBounds, plateBounds))
            {
                return true;
            }
        }

        return false;
    }

    private void FillPlateSnapshot()
    {
        plateSnapshot.Clear();
        foreach (PressurePlate pressurePlate in RegisteredComponents)
        {
            if (pressurePlate != null)
            {
                plateSnapshot.Add(pressurePlate);
            }
        }
    }

    private static bool IntersectsXY(Bounds a, Bounds b)
    {
        return a.min.x <= b.max.x && a.max.x >= b.min.x && a.min.y <= b.max.y && a.max.y >= b.min.y;
    }

    private bool IsPressedBySceneMovable(PressurePlate pressurePlate, SceneMovableInteractionService sceneMovableService)
    {
        Bounds plateBounds = pressurePlate.Bounds;
        if (plateBounds.size == Vector3.zero)
        {
            return false;
        }

        plateBounds = ExpandTowardLocalUp(pressurePlate.transform, plateBounds, activationPadding);
        sceneMovableService.QueryOverlapping(plateBounds, movableHits);
        for (int i = 0; i < movableHits.Count; i++)
        {
            ISceneMovableItem item = movableHits[i];
            if (item != null && item.Owner != pressurePlate.gameObject)
            {
                return true;
            }
        }

        return false;
    }

    private static Bounds ExpandTowardLocalUp(Transform transform, Bounds bounds, float distance)
    {
        if (transform == null || distance <= 0f)
        {
            return bounds;
        }

        Vector3 localUp = transform.up;
        Vector3 offset = localUp * distance;
        bounds.Encapsulate(bounds.min + offset);
        bounds.Encapsulate(bounds.max + offset);
        return bounds;
    }
}
