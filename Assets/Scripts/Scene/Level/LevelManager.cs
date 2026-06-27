using System;
using System.Collections;
using System.Collections.Generic;
using Kuchinashi.DataSystem;
using Kuchinashi.SceneFlow;
using QFramework;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 挂在关卡内容场景中，集中处理本关生命周期与 <see cref="TypeEventSystem.Global"/> 等监听。
/// 内置默认目标：同场景内收集满 <see cref="starsRequired"/> 颗 <see cref="CollectiveStar"/> 后触发一次事件；默认再播放玩家 Happy、等待 <see cref="delayBeforeReturnToStartSeconds"/> 秒后切回对应内容场景（见 <see cref="GameManager.ResolveWorldSceneAfterLevelComplete"/>，含 <c>Level 1-6</c> → World 2、<c>Level 2-6</c> → StartScene 等特例）。
/// 用法：本类挂在关卡根物体上（建议每关仅一个）；复杂逻辑可继承并重写虚方法，或在 Inspector 绑定 <see cref="UnityEvent"/>。
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-50)]
public class LevelManager : MonoBehaviour
{
    [Header("关卡配置")]
    [Tooltip(">=0 时从 GameConfig.Current.Levels 按 Index 取条目；<0 时按本场景名与 LevelData.ScenePath（即场景名）字符串相等匹配。")]
    [SerializeField] private int m_LevelIndexOverride = -1;

    [SerializeField] private bool enableStarCollectGoal = true;
    [SerializeField, Min(1)] private int starsRequired = 3;
    [SerializeField] private UnityEvent onStarsRequirementMet;
    [SerializeField] private UnityEventInt onStarsProgress;
    [SerializeField, Min(0f)] private float delayBeforeReturnToStartSeconds = 2f;

    [Tooltip("若留空，运行时查找场景中的 SceneFlowHost（与选关界面一致）。")]
    [SerializeField] private SceneFlowHost sceneFlowHost;
    [SerializeField] private bool disablePlayerInputWhenStarsComplete = true;

    [Header("Inspector 钩子（无需写子类时可直接绑事件）")]
    [SerializeField] private UnityEvent onLevelAwake;
    [SerializeField] private UnityEvent onLevelStart;
    [SerializeField] private UnityEvent onLevelDestroy;

    private readonly List<IUnRegister> m_GlobalListenerTokens = new List<IUnRegister>(8);

    private int m_StarsCollectedInLevel;
    private bool m_StarsRequirementMet;
    private Coroutine m_PostAllStarsRoutine;
    private LevelData m_LevelData;

    /// <summary>本关已计入目标的星星数（同场景、去重后的事件次数）。</summary>
    public int StarsCollectedInLevel => m_StarsCollectedInLevel;

    /// <summary>是否已达到 <see cref="starsRequired"/> 并已触发过达成逻辑。</summary>
    public bool IsStarsRequirementMet => m_StarsRequirementMet;

    /// <summary>由 <see cref="ResolveLevelData"/> 从 <see cref="GameConfig.Current"/> 解析得到的本关数据；未匹配则为 null。</summary>
    public LevelData LevelData => m_LevelData;

    private void Awake()
    {
        m_StarsCollectedInLevel = 0;
        m_StarsRequirementMet = false;
        ResolveLevelData();
        OnLevelAwake();
        onLevelAwake?.Invoke();
    }

    private void Start()
    {
        OnLevelStart();
        onLevelStart?.Invoke();
    }

    private void OnEnable()
    {
        RegisterBuiltInLevelEventListeners();
        RegisterLevelEventListeners();
    }

    private void OnDisable()
    {
        if (ServiceBase.TryGet(out PlayerService service))
        {
            service.ReleaseDisableMovementInput(this);
        }

        if (GameManager.Instance != null)
        {
            GameManager.Instance.ReleaseLevelResetLock(this);
        }

        if (m_PostAllStarsRoutine != null)
        {
            StopCoroutine(m_PostAllStarsRoutine);
            m_PostAllStarsRoutine = null;
        }

        UnregisterAllGlobalListeners();
        UnregisterLevelEventListeners();
    }

    private void OnDestroy()
    {
        OnLevelTeardown();
        onLevelDestroy?.Invoke();
    }

    /// <summary>关卡物体 Awake 时调用，早于本物体 OnEnable。</summary>
    protected virtual void OnLevelAwake()
    {
    }

    /// <summary>首帧 Start 时调用，适合依赖其他组件初始化完成之后的逻辑。</summary>
    protected virtual void OnLevelStart()
    {
    }

    /// <summary>
    /// 在此订阅全局事件；请优先使用 <see cref="ListenGlobal{T}"/>，以便在 OnDisable 时自动注销。
    /// 若有手动订阅，请在 <see cref="UnregisterLevelEventListeners"/> 中成对释放。
    /// </summary>
    protected virtual void RegisterLevelEventListeners()
    {
    }

    /// <summary>清理 <see cref="ListenGlobal{T}"/> 之外的监听或句柄。</summary>
    protected virtual void UnregisterLevelEventListeners()
    {
    }

    /// <summary>物体销毁前调用，用于本关持久化或收尾（早于场景内子物体销毁顺序不保证）。</summary>
    protected virtual void OnLevelTeardown()
    {
    }

    /// <summary>当本关累计收集达到 <see cref="starsRequired"/> 时调用一次；可与 <see cref="onStarsRequirementMet"/> 同时使用。</summary>
    protected virtual void OnLevelStarsRequirementMet()
    {
    }

    private void RegisterBuiltInLevelEventListeners()
    {
        if (!enableStarCollectGoal || starsRequired < 1)
        {
            return;
        }

        ListenGlobal<CollectiveStarCollectedEvent>(OnCollectiveStarCollectedGlobal);
    }

    private void OnCollectiveStarCollectedGlobal(CollectiveStarCollectedEvent e)
    {
        if (m_StarsRequirementMet)
        {
            return;
        }

        CollectiveStar star = e.Star;
        if (star == null || star.gameObject.scene != gameObject.scene)
        {
            return;
        }

        m_StarsCollectedInLevel++;
        onStarsProgress?.Invoke(m_StarsCollectedInLevel);
        if (m_StarsCollectedInLevel < starsRequired)
        {
            return;
        }

        m_StarsRequirementMet = true;

        if (GameManager.Instance != null)
        {
            GameManager.Instance.RetainLevelResetLock(this);
        }

        PersistLevelFinishedToSave();
        OnLevelStarsRequirementMet();
        onStarsRequirementMet?.Invoke();
        m_PostAllStarsRoutine ??= StartCoroutine(PostAllStarsHappyThenReturnRoutine());
    }

    private IEnumerator PostAllStarsHappyThenReturnRoutine()
    {
        yield return new WaitForSeconds(0.5f);

        if (ServiceBase.TryGet(out PlayerService playerService) && playerService.Player != null)
        {
            PlayerController player = playerService.Player;
            if (disablePlayerInputWhenStarsComplete)
            {
                playerService.RetainDisableMovementInput(this);
            }

            player.SwitchAnimationTo("Happy");
        }

        if (delayBeforeReturnToStartSeconds > 0f)
        {
            yield return new WaitForSeconds(delayBeforeReturnToStartSeconds);
        }

        TryRequestSwitchToStartScene();
        m_PostAllStarsRoutine = null;
    }

    /// <summary>
    /// 按当前关卡场景名解析通关后世界场景（见 <see cref="GameManager.ResolveWorldSceneAfterLevelComplete"/>）。
    /// </summary>
    private void TryRequestSwitchToStartScene()
    {
        string target = GameManager.ResolveWorldSceneAfterLevelComplete(gameObject.scene.name);
        if (string.IsNullOrWhiteSpace(target))
        {
            Debug.LogWarning("[LevelManager] 通关后目标场景名为空。", this);
            return;
        }

        target = target.Trim();
        SceneFlowController flow = SceneFlowController.Instance;
        if (flow != null && flow.IsConfigured)
        {
            if (flow.TryRequestSwitchContent(target, false))
            {
                return;
            }

            Debug.LogWarning($"[LevelManager] SceneFlow 未切换到「{target}」（可能正在转场、已在目标内容场景或名称非法）。");
            return;
        }

        SceneFlowHost host = sceneFlowHost != null ? sceneFlowHost : FindFirstObjectByType<SceneFlowHost>();
        if (host != null && host.TryJumpToScene(target))
        {
            return;
        }

        Debug.LogWarning("[LevelManager] 未找到可用的 SceneFlowController / SceneFlowHost，或切换被拒绝。");
    }

    private void ResolveLevelData()
    {
        m_LevelData = null;
        GameConfig config = GameConfig.Current;
        if (config == null || config.Levels == null)
        {
            Debug.LogWarning("[LevelManager] GameConfig.Current 或 Levels 为空，无法解析关卡数据。", this);
            return;
        }

        if (m_LevelIndexOverride >= 0)
        {
            for (var i = 0; i < config.Levels.Count; i++)
            {
                LevelData row = config.Levels[i];
                if (row != null && row.Index == m_LevelIndexOverride)
                {
                    m_LevelData = row;
                    return;
                }
            }

            Debug.LogWarning($"[LevelManager] Levels 中不存在 Index={m_LevelIndexOverride}。", this);
            return;
        }

        string sceneName = gameObject.scene.name;
        for (var i = 0; i < config.Levels.Count; i++)
        {
            LevelData row = config.Levels[i];
            if (row == null || string.IsNullOrEmpty(row.ScenePath))
            {
                continue;
            }

            if (string.Equals(row.ScenePath.Trim(), sceneName, StringComparison.Ordinal))
            {
                m_LevelData = row;
                return;
            }
        }

        Debug.LogWarning($"[LevelManager] Levels 中无 ScenePath 与当前场景名「{sceneName}」一致的条目。", this);
    }

    private void PersistLevelFinishedToSave()
    {
        if (m_LevelData == null)
        {
            return;
        }

        Save save = new Save().DeSerialize<Save>();
        save.FinishedLevels ??= new List<int>();
        if (!save.FinishedLevels.Contains(m_LevelData.Index))
        {
            save.FinishedLevels.Add(m_LevelData.Index);
        }

        save.Serialize();
    }

    /// <summary>订阅 <see cref="TypeEventSystem.Global"/>；在 OnDisable 时由基类统一 <c>UnRegister</c>。</summary>
    protected void ListenGlobal<T>(Action<T> onEvent)
    {
        if (onEvent == null)
        {
            return;
        }

        m_GlobalListenerTokens.Add(TypeEventSystem.Global.Register(onEvent));
    }

    private void UnregisterAllGlobalListeners()
    {
        for (int i = 0; i < m_GlobalListenerTokens.Count; i++)
        {
            m_GlobalListenerTokens[i]?.UnRegister();
        }

        m_GlobalListenerTokens.Clear();
    }
}

/// <summary>可在 Inspector 中绑定带 <c>int</c> 参数的动态方法（例如更新星星进度 UI）。</summary>
[Serializable]
public class UnityEventInt : UnityEvent<int>
{
}
