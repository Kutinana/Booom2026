using UnityEngine;

[DisallowMultipleComponent]
public class ServicesBootstrapper : MonoBehaviour
{
    private void Awake()
    {
        EnsureService<PushableBoxService>();
        EnsureService<PhysicalBoxService>();
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
        StandardBox[] boxes = FindObjectsOfType<StandardBox>();

        foreach (StandardBox box in boxes)
        {
            pushableBoxService?.Register(box);
            physicalBoxService?.Register(box);
        }
    }
}
