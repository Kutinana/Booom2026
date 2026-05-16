using Kuchinashi.SceneFlow;
using QFramework;
using UnityEngine;

public class GameManager : MonoSingleton<GameManager>
{
    public const string StartContentSceneName = "World 1";

    [SerializeField] private GameConfig m_GameConfig;

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        GameConfig.SetRuntimeCurrent(m_GameConfig);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        GameConfig.ClearRuntimeCurrentIf(m_GameConfig);
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
            string reloadContentSceneName = flow.CurrentContentSceneName;
            if (flow.TryRequestReloadCurrentContent())
            {
                LevelBoxController.ResetSavedWorldPositionsForLoadedScene(reloadContentSceneName);
            }

            return;
        }

        if (!Input.GetKeyDown(KeyCode.Escape))
        {
            return;
        }

        var content = flow.CurrentContentSceneName;
        if (content == StartContentSceneName)
        {
            TypeEventSystem.Global.Send(new TryQuitGameRequestedEvent());
            return;
        }

        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        flow.TryRequestSwitchContent(StartContentSceneName);
    }
}
