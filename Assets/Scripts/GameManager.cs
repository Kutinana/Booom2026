using Kuchinashi.SceneFlow;
using QFramework;
using UnityEngine;

public class GameManager : MonoSingleton<GameManager>
{
    private const string StartContentSceneName = "World 1";

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
    }

    private void Update()
    {
        var flow = SceneFlowController.Instance;
        if (flow == null || !flow.IsConfigured || flow.IsTransitioning)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.R))
        {
            flow.TryRequestReloadCurrentContent();
            return;
        }

        if (!Input.GetKeyDown(KeyCode.Escape))
        {
            return;
        }

        if (string.IsNullOrEmpty(flow.CurrentContentSceneName) ||
            flow.CurrentContentSceneName == StartContentSceneName)
        {
            return;
        }

        flow.TryRequestSwitchContent(StartContentSceneName);
    }
}
