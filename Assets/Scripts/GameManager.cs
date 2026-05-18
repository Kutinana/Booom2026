using System;
using Kuchinashi.SceneFlow;
using QFramework;
using UnityEngine;

public class GameManager : MonoSingleton<GameManager>
{
    public const string StartContentSceneName = "World 1";
    public const string World2ContentSceneName = "World 2";
    public const string MenuSceneName = "StartScene";

    private const string LevelScenePrefix = "Level ";
    private const string Level1_6SceneName = "Level 1-6";
    private const string Level2_6SceneName = "Level 2-6";

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
                if (IsWorldHubScene(reloadContentSceneName))
                {
                    PlayerController.ClearSavedWorldPositionAndPreventSaveThisReload();
                }
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
            || contentSceneName == World2ContentSceneName
            || contentSceneName == MenuSceneName;
    }

    /// <summary>
    /// 记录当前 Hub 场景到存档。
    /// </summary>
    public static void SetLastHubScene(string hubSceneName)
    {
        if (string.IsNullOrEmpty(hubSceneName)) return;
        Save save = new Save().DeSerialize<Save>();
        save.LastHubScene = hubSceneName;
        save.Serialize();
    }

    /// <summary>
    /// 正常中途退出（ESC）应进入的内容场景。
    /// 优先从存档读取 LastHubScene。
    /// </summary>
    public static string ResolveWorldSceneForLevel(string levelSceneName)
    {
        Save save = new Save().DeSerialize<Save>();
        if (!string.IsNullOrEmpty(save.LastHubScene))
        {
            return save.LastHubScene;
        }

        // Fallback: 如果没有 LastHubScene 记录，则使用基于场景名的默认映射
        if (string.IsNullOrWhiteSpace(levelSceneName))
        {
            return StartContentSceneName;
        }

        levelSceneName = levelSceneName.Trim();
        if (levelSceneName == "Level-Tutorial") return StartContentSceneName;

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
        if (worldPart.Length == 1)
        {
            if (worldPart[0] == '1') return StartContentSceneName;
            if (worldPart[0] == '2') return World2ContentSceneName;
        }

        return StartContentSceneName;
    }

    /// <summary>
    /// 通关后应进入的内容场景；在 <see cref="ResolveWorldSceneForLevel"/> 基础上含特例：
    /// <c>Level 1-6</c> → <see cref="World2ContentSceneName"/>，<c>Level 2-6</c> → <see cref="MenuSceneName"/>。
    /// </summary>
    public static string ResolveWorldSceneAfterLevelComplete(string levelSceneName)
    {
        Save save = new Save().DeSerialize<Save>();
        
        // 如果是从主菜单（StartScene）进入的关卡，通关后也强制回主菜单（用户需求：StartScene 入口始终回 StartScene）
        if (!string.IsNullOrEmpty(save.LastHubScene) && save.LastHubScene == MenuSceneName)
        {
            return MenuSceneName;
        }

        if (string.IsNullOrWhiteSpace(levelSceneName))
        {
            return ResolveWorldSceneForLevel(levelSceneName);
        }

        string trimmed = levelSceneName.Trim();
        // 特例：2-6 通关回主菜单（大结局）
        if (string.Equals(trimmed, Level2_6SceneName, StringComparison.Ordinal))
        {
            return MenuSceneName;
        }

        // 特例：1-6 通关进 World 2（仅限从世界地图进入时触发进度跳转）
        if (trimmed == Level1_6SceneName)
        {
            return World2ContentSceneName;
        }

        return ResolveWorldSceneForLevel(levelSceneName);
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
