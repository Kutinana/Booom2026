using Kuchinashi.SceneFlow;
using QFramework;
using UnityEngine;

public class GameManager : MonoSingleton<GameManager>
{
    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
    }

    private void Update()
    {
        if (!Input.GetKeyDown(KeyCode.R))
        {
            return;
        }

        var flow = SceneFlowController.Instance;
        if (flow == null || !flow.IsConfigured || flow.IsTransitioning)
        {
            return;
        }

        flow.TryRequestReloadCurrentContent();
    }
}
