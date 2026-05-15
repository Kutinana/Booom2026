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
