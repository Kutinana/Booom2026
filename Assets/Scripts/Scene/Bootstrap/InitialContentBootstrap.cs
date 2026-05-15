using System;
using System.IO;
using Kuchinashi.SceneFlow;
using UnityEngine;

/// <summary>
/// 壳场景首包：若存档中 <see cref="Save.FinishedLevels"/> 为空则加载 <see cref="GameConfig.Levels"/> 中 Index 为 0 的关卡场景；
/// 否则加载 <see cref="GameConfig.Worlds"/> 中 Index 为 0 的世界场景。
/// </summary>
/// <remarks>
/// 依赖 <see cref="GameConfig.Current"/>（由 <see cref="GameManager"/> 在 Awake 中注册）。请保证壳场景内 GameManager 先于本组件完成 Awake。
/// 请在同场景的 <see cref="SceneFlowHost"/> 上关闭 <c>Load Initial On Start</c>（或留空初始内容），否则会与自动首包抢顺序。
/// </remarks>
[DefaultExecutionOrder(-150)]
public class InitialContentBootstrap : MonoBehaviour
{
    #region Serialized Fields

    [Tooltip("若为空则在场景中查找 SceneFlowHost。")]
    [SerializeField] private SceneFlowHost m_SceneFlowHost;

    #endregion

    #region Unity Lifecycle

    private void Start()
    {
        GameConfig gameConfig = GameConfig.Current;
        if (gameConfig == null)
        {
            Debug.LogError("[InitialContentBootstrap] GameConfig.Current 为空，请确认 GameManager 已挂载并指定 GameConfig。", this);
            return;
        }

        var host = m_SceneFlowHost != null ? m_SceneFlowHost : FindFirstObjectByType<SceneFlowHost>();
        if (host == null)
        {
            Debug.LogError("[InitialContentBootstrap] 未找到 SceneFlowHost。", this);
            return;
        }

        if (!host.TryGetComponent(out SceneFlowController controller))
        {
            Debug.LogError("[InitialContentBootstrap] SceneFlowHost 上缺少 SceneFlowController。", host);
            return;
        }

        if (!controller.IsConfigured)
        {
            Debug.LogError("[InitialContentBootstrap] SceneFlowController 尚未 Configure。", this);
            return;
        }

        string targetSceneName = ResolveTargetSceneName(gameConfig, new Save().DeSerialize<Save>());
        if (string.IsNullOrEmpty(targetSceneName))
        {
            Debug.LogError("[InitialContentBootstrap] 无法解析目标内容场景名（请检查 GameConfig 与 ScenePath）。", this);
            return;
        }

        if (!controller.TryLoadInitialContentAdditiveDirect(targetSceneName, false))
        {
            Debug.LogWarning(
                $"[InitialContentBootstrap] 未能加载首包场景「{targetSceneName}」（可能已在内容中、正在过渡或场景名无效）。",
                this);
        }
    }

    #endregion

    #region Private Methods

    private static string ResolveTargetSceneName(GameConfig _gameConfig, Save _save)
    {
        bool finishedEmpty = _save == null ||
                             _save.FinishedLevels == null ||
                             _save.FinishedLevels.Count == 0;

        if (finishedEmpty)
        {
            return FindSceneNameByIndex(_gameConfig.Levels, _level => _level.Index, _level => _level.ScenePath);
        }

        return FindSceneNameByIndex(_gameConfig.Worlds, _world => _world.Index, _world => _world.ScenePath);
    }

    private static string FindSceneNameByIndex<T>(
        System.Collections.Generic.IReadOnlyList<T> _items,
        Func<T, int> _getIndex,
        Func<T, string> _getScenePath) where T : class
    {
        if (_items == null)
        {
            return null;
        }

        for (var i = 0; i < _items.Count; i++)
        {
            T item = _items[i];
            if (item == null)
            {
                continue;
            }

            if (_getIndex(item) == 0)
            {
                return SceneNameFromPath(_getScenePath(item));
            }
        }

        return null;
    }

    private static string SceneNameFromPath(string _scenePath)
    {
        if (string.IsNullOrWhiteSpace(_scenePath))
        {
            return null;
        }

        string trimmed = _scenePath.Trim();
        if (trimmed.IndexOf('/') < 0 && trimmed.IndexOf('\\') < 0)
        {
            return trimmed;
        }

        return Path.GetFileNameWithoutExtension(trimmed.Replace('\\', '/'));
    }

    #endregion
}
