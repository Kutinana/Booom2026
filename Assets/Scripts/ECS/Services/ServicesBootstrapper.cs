using QFramework;
using UnityEngine;

[DisallowMultipleComponent]
public class ServicesBootstrapper : MonoSingleton<ServicesBootstrapper>
{
    private void Awake()
    {
        ServiceBase.Get<PlayerService>();
        ServiceBase.Get<PushableBoxService>();
        ServiceBase.Get<PhysicalBoxService>();
        ServiceBase.Get<SceneMovableInteractionService>();
        ServiceBase.Get<WorldBoxExitBlockerService>();
        ServiceBase.Get<PressurePlateService>();
    }
}
