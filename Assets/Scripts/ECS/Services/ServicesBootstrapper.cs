using UnityEngine;

[DisallowMultipleComponent]
public class ServicesBootstrapper : MonoBehaviour
{
    private void Awake()
    {
        EnsureService<PushableBoxService>();
        EnsureService<PhysicalBoxService>();
        EnsureService<PlayerRelativePositionService>();
    }

    private void Start()
    {
        RegisterExistingBoxes();
    }

    private void EnsureService<T>() where T : ServiceBase
    {
        if (GetComponent<T>() == null)
        {
            gameObject.AddComponent<T>();
        }
    }

    private void RegisterExistingBoxes()
    {
        PushableBoxService pushableBoxService = ServiceBase.Get<PushableBoxService>();
        PhysicalBoxService physicalBoxService = ServiceBase.Get<PhysicalBoxService>();
        PlayerRelativePositionService playerRelativePositionService = ServiceBase.Get<PlayerRelativePositionService>();
        StandardBox[] boxes = FindObjectsOfType<StandardBox>();
        PressurePlate[] pressurePlates = FindObjectsOfType<PressurePlate>();

        foreach (StandardBox box in boxes)
        {
            pushableBoxService?.Register(box);
            physicalBoxService?.Register(box);
            playerRelativePositionService?.Register(box);
        }

        foreach (PressurePlate pressurePlate in pressurePlates)
        {
            playerRelativePositionService?.Register(pressurePlate);
        }
    }
}
