using UnityEngine;

[DisallowMultipleComponent]
public class ServicesBootstrapper : MonoBehaviour
{
    private void Awake()
    {
        ServiceBase.Get<PlayerService>();
        ServiceBase.Get<PushableBoxService>();
        ServiceBase.Get<PhysicalBoxService>();
        ServiceBase.Get<PressurePlateService>();
    }
}
