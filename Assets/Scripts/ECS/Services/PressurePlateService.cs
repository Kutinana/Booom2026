using System.Collections.Generic;
using QFramework;
using UnityEngine;

public class PressurePlateService : ServiceBase<PressurePlate>
{
    private float activationPadding = 0.04f;

    private readonly List<PressurePlate> plateSnapshot = new List<PressurePlate>(16);
    private readonly List<ISceneMovableItem> movableHits = new List<ISceneMovableItem>(16);
    private IUnRegister sceneMovableChangedUnRegister;

    protected override void Awake()
    {
        base.Awake();
        if (!IsActiveService)
        {
            return;
        }

        sceneMovableChangedUnRegister = RegisterEvent<SceneMovableItemsChangedEvent>(OnSceneMovablesChanged);
    }

    protected override void OnDestroy()
    {
        sceneMovableChangedUnRegister?.UnRegister();
        sceneMovableChangedUnRegister = null;
        base.OnDestroy();
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

    private void OnSceneMovablesChanged(SceneMovableItemsChangedEvent e)
    {
        RefreshAll();
    }

    private void RefreshAll()
    {
        ServiceBase.TryGet(out SceneMovableInteractionService sceneMovableService);
        plateSnapshot.Clear();
        foreach (PressurePlate pressurePlate in RegisteredComponents)
        {
            if (pressurePlate == null)
            {
                continue;
            }

            plateSnapshot.Add(pressurePlate);
        }

        for (int i = 0; i < plateSnapshot.Count; i++)
        {
            PressurePlate pressurePlate = plateSnapshot[i];
            bool pressed = sceneMovableService != null && IsPressedBySceneMovable(pressurePlate, sceneMovableService);
            pressurePlate.SetPressed(pressed);
        }
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
