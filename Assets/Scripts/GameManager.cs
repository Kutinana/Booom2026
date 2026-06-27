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
    private const string TutorialSceneName = "Level-Tutorial";

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
#if EXHIBITION_BUILD
        if (!m_IsExhibitionResetting && 
            (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && 
            (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
        {
            if (Input.GetKeyDown(KeyCode.R))
            {
                StartCoroutine(ExhibitionResetRoutine());
                return;
            }

            var flowForExhibition = SceneFlowController.Instance;
            if (flowForExhibition != null && flowForExhibition.IsConfigured && !flowForExhibition.IsTransitioning)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1))
                {
                    flowForExhibition.TryRequestSwitchContent(StartContentSceneName);
                    return;
                }
                if (Input.GetKeyDown(KeyCode.Alpha2))
                {
                    flowForExhibition.TryRequestSwitchContent(World2ContentSceneName);
                    return;
                }
                if (Input.GetKeyDown(KeyCode.Alpha3))
                {
                    flowForExhibition.TryRequestSwitchContent(MenuSceneName);
                    return;
                }
            }
        }
#endif

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
                    PlayerController.ClearSavedWorldPositionAndPreventSaveThisReload(reloadContentSceneName);
                }
            }

            return;
        }

        if (!Input.GetKeyDown(KeyCode.Escape))
        {
            return;
        }

        if (IsCinematicLocked)
        {
            TrySkipCinematic();
            return;
        }

        var content = flow.CurrentContentSceneName;
        if (IsWorldHubScene(content))
        {
            TypeEventSystem.Global.Send(new TryQuitGameRequestedEvent());
            return;
        }

        if (content == TutorialSceneName)
        {
            return;
        }

        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        string targetWorld = ResolveWorldSceneForLevel(content);
        flow.TryRequestSwitchContent(targetWorld);
    }

    private readonly System.Collections.Generic.HashSet<object> m_CinematicLocks = new System.Collections.Generic.HashSet<object>();

    public bool IsCinematicLocked => m_CinematicLocks.Count > 0;

    public void RetainCinematicLock(object token)
    {
        if (token != null) m_CinematicLocks.Add(token);
    }

    public void ReleaseCinematicLock(object token)
    {
        if (token != null) m_CinematicLocks.Remove(token);
    }

    private void TrySkipCinematic()
    {
        // TODO: 之后加跳过对话或 timeline 的功能
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

        // 特例：如已播放转场动画，则1-6 通关进 World 2；否则去World1走转场动画切换流程
        if (trimmed == Level1_6SceneName)
        {
            if (save.HasCGPlayed == true)
            {
                return World2ContentSceneName;
            }
            else
            {
                return StartContentSceneName;
            }

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

#if EXHIBITION_BUILD
    private bool m_IsExhibitionResetting = false;

    private System.Collections.IEnumerator ExhibitionResetRoutine()
    {
        m_IsExhibitionResetting = true;

        // 1. 用 SceneFlow 的过渡动画黑屏
        var view = FindObjectOfType<Kuchinashi.SceneFlow.SceneTransitionViewBehaviour>();
        if (view != null)
        {
            yield return view.EnterCover();
        }

        yield return new WaitForSeconds(1f);

        // 清理本地数据
        var path = Application.persistentDataPath;
        if (System.IO.Directory.Exists(path))
        {
            foreach (var dir in System.IO.Directory.GetDirectories(path))
            {
                try
                {
                    System.IO.Directory.Delete(dir, true);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Exhibition] Skip deleting directory {dir}: {e.Message}");
                }
            }
            foreach (var file in System.IO.Directory.GetFiles(path))
            {
                try
                {
                    System.IO.File.Delete(file);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Exhibition] Skip deleting file {file}: {e.Message}");
                }
            }
        }

        Kuchinashi.DataSystem.UserConfig.Clear();

        // 2. 重新加载 BaseScene
        // 使用 Single 模式加载，Unity 会在底层自动卸载当前所有的其他场景，从而完美避开“不能卸载最后一个场景”的限制。
        yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        yield return UnityEngine.SceneManagement.SceneManager.LoadSceneAsync("BaseScene", UnityEngine.SceneManagement.LoadSceneMode.Single);

        // 如果需要控制退出黑屏，可以在 BaseScene 中自己处理，或者这里强行抛弃 view（view 很可能已经随着旧场景卸载被销毁了）。
        // 因此全程黑屏完美过渡到了 BaseScene 的初始状态。
        m_IsExhibitionResetting = false;
    }
#endif
}
