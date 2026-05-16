using System;
using Kuchinashi.SceneFlow;
using QFramework;
using UnityEngine;

public class GameManager : MonoSingleton<GameManager>
{
    public const string StartContentSceneName = "World 1";
    public const string World2ContentSceneName = "World 2";

    private const string LevelScenePrefix = "Level ";
    private const string Level1_6SceneName = "Level 1-6";

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
        if (IsWorldHubScene(content))
        {
            TypeEventSystem.Global.Send(new TryQuitGameRequestedEvent());
            return;
        }

        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        string targetWorld = ResolveWorldSceneForLevel(content);
        flow.TryRequestSwitchContent(targetWorld);
    }

    public static bool IsWorldHubScene(string contentSceneName)
    {
        return contentSceneName == StartContentSceneName
            || contentSceneName == World2ContentSceneName;
    }

    /// <summary>
    /// 通关后应进入的世界场景；在 <see cref="ResolveWorldSceneForLevel"/> 基础上含特例（如 <c>Level 1-6</c> → <see cref="World2ContentSceneName"/>）。
    /// </summary>
    public static string ResolveWorldSceneAfterLevelComplete(string levelSceneName)
    {
        if (!string.IsNullOrWhiteSpace(levelSceneName)
            && string.Equals(levelSceneName.Trim(), Level1_6SceneName, StringComparison.Ordinal))
        {
            return World2ContentSceneName;
        }

        return ResolveWorldSceneForLevel(levelSceneName);
    }

    /// <summary>
    /// 按关卡场景名 <c>Level 1-X</c> / <c>Level 2-X</c>（X 为正整数）解析对应世界场景；不匹配时回退 <see cref="StartContentSceneName"/>。
    /// </summary>
    public static string ResolveWorldSceneForLevel(string levelSceneName)
    {
        if (string.IsNullOrWhiteSpace(levelSceneName))
        {
            return StartContentSceneName;
        }

        levelSceneName = levelSceneName.Trim();
        if (!levelSceneName.StartsWith(LevelScenePrefix, StringComparison.Ordinal))
        {
            return StartContentSceneName;
        }

        string afterPrefix = levelSceneName.Substring(LevelScenePrefix.Length);
        int dashIndex = afterPrefix.IndexOf('-');
        if (dashIndex <= 0 || dashIndex >= afterPrefix.Length - 1)
        {
            return StartContentSceneName;
        }

        ReadOnlySpan<char> worldPart = afterPrefix.AsSpan(0, dashIndex);
        ReadOnlySpan<char> levelPart = afterPrefix.AsSpan(dashIndex + 1);
        if (worldPart.Length != 1 || (worldPart[0] != '1' && worldPart[0] != '2'))
        {
            return StartContentSceneName;
        }

        if (!IsPositiveIntegerSpan(levelPart))
        {
            return StartContentSceneName;
        }

        return worldPart[0] == '2' ? World2ContentSceneName : StartContentSceneName;
    }

    private static bool IsPositiveIntegerSpan(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }
}
