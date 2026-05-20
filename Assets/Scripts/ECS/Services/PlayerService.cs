using System.Collections;
using Kuchinashi.SceneFlow;
using QFramework;
using UnityEngine;

public class PlayerService : ServiceBase
{
    [SerializeField, Min(0f), Tooltip("crashed 死亡动画播完后再额外等待的秒数；这段时间用于让砸扁定格停留在屏幕上，到时触发场景 reload。")]
    private float deathReloadDelay = 0.5f;

    [SerializeField, Tooltip("PlayerController 上 Animator 中死亡 state 对应的剪辑名（用于按 clip.length 等待动画播完）。")]
    private string crashedClipName = "Crashed";

    [SerializeField, Min(0f), Tooltip("找不到匹配剪辑时的回退动画时长。")]
    private float crashedFallbackDuration = 0.5f;

    public PlayerController Player { get; private set; }
    public bool HasPlayer => Player != null;

    /// <summary>与 <see cref="PlayerAnimationController"/> 朝向判定一致的子级 <see cref="SpriteRenderer"/>。</summary>
    public SpriteRenderer PlayerSpriteRenderer { get; private set; }

    public Collider2D PlayerCollider2D { get; private set; }
    public Collider PlayerCollider3D { get; private set; }

    public bool TryGetPlayerWorldBounds(out Bounds bounds)
    {
        bounds = default;
        if (Player == null)
        {
            return false;
        }

        if (PlayerCollider2D != null)
        {
            bounds = PlayerCollider2D.bounds;
            return bounds.size != Vector3.zero;
        }

        if (PlayerCollider3D != null)
        {
            bounds = PlayerCollider3D.bounds;
            return bounds.size != Vector3.zero;
        }

        return false;
    }

    /// <summary>
    /// 玩家正处于死亡过渡中（已收到 PlayerDeathEvent，正在等待场景 reload）。
    /// 期间禁用玩家输入，并供物理/碰撞模块跳过后续 impact 检测，避免重复触发。
    /// </summary>
    public bool IsDying { get; private set; }

    private IUnRegister deathUnRegister;
    private Coroutine pendingReload;

    protected override void Awake()
    {
        base.Awake();
        if (!IsActiveService)
        {
            return;
        }

        deathUnRegister = RegisterEvent<PlayerDeathEvent>(OnPlayerDeath);
    }

    protected override void OnDestroy()
    {
        deathUnRegister?.UnRegister();
        deathUnRegister = null;
        if (pendingReload != null)
        {
            StopCoroutine(pendingReload);
            pendingReload = null;
        }
        base.OnDestroy();
    }

    public void Register(PlayerController player)
    {
        if (player == null)
        {
            return;
        }

        if (Player != null && Player != player)
        {
#if UNITY_EDITOR
            Debug.LogWarning($"Replacing registered player '{Player.name}' with '{player.name}'.", player);
#endif
        }

        Player = player;
        GameObject playerObject = player.gameObject;
        PlayerSpriteRenderer = player.GetComponentInChildren<SpriteRenderer>(true);
        PlayerCollider2D = playerObject.GetComponent<Collider2D>();
        PlayerCollider3D = playerObject.GetComponent<Collider>();

        // 场景 reload 后新 player 注册：清掉 dying 状态与悬挂的延迟 reload 协程；
        // 否则下一关一开始就被锁住输入，或者旧定时器到点触发了一次多余的 reload。
        IsDying = false;
        if (pendingReload != null)
        {
            StopCoroutine(pendingReload);
            pendingReload = null;
        }
    }

    public void UnRegister(PlayerController player)
    {
        if (Player == player)
        {
            Player = null;
            PlayerSpriteRenderer = null;
            PlayerCollider2D = null;
            PlayerCollider3D = null;
        }
    }

    private void OnPlayerDeath(PlayerDeathEvent e)
    {
        if (IsDying)
        {
            return;
        }

        // 防御性：在没有玩家时收到的死亡事件忽略；指明的玩家不是当前注册者也忽略。
        if (Player == null || (e.Player != null && e.Player != Player))
        {
            return;
        }

        IsDying = true;
        Player.MovementInputDisabled = true;
#if UNITY_EDITOR
        Debug.Log($"[PlayerService] Player died: {e.Reason} (source: {(e.Source != null ? e.Source.name : "null")})");
#endif

        if (pendingReload != null)
        {
            StopCoroutine(pendingReload);
        }
        pendingReload = StartCoroutine(DelayedReload());
    }

    private IEnumerator DelayedReload()
    {
        // 1) 先等 crashed 动画整段播完。Crashed state 在 controller 里没有任何转出过渡，
        //    play 进去之后就稳定停留在最后一帧——按 clip.length 等同于"动画放完"。
        float animationDuration = TryGetCrashedAnimationDuration();
        if (animationDuration > 0f)
        {
            yield return new WaitForSeconds(animationDuration);
        }

        // 2) 动画结束后再额外停留 deathReloadDelay 秒，让砸扁定格被玩家看清。
        if (deathReloadDelay > 0f)
        {
            yield return new WaitForSeconds(deathReloadDelay);
        }

        pendingReload = null;

        // 复用 GameManager 中按 R 的同一条重载路径，保持单一入口。
        SceneFlowController flow = SceneFlowController.Instance;
        if (flow != null && flow.IsConfigured && !flow.IsTransitioning)
        {
            string currentScene = flow.CurrentContentSceneName;
            if (flow.TryRequestReloadCurrentContent())
            {
                // 如果在世界地图中死亡，为了打破可能的出生死亡循环，
                // 重载时应像按 R 一样重置位置（回到默认点）。
                if (GameManager.IsWorldHubScene(currentScene))
                {
                    PlayerController.ClearSavedWorldPositionAndPreventSaveThisReload(currentScene);
                }
            }
        }
    }

    private float TryGetCrashedAnimationDuration()
    {
        if (Player == null || string.IsNullOrEmpty(crashedClipName))
        {
            return crashedFallbackDuration;
        }

        Animator animator = Player.GetComponentInChildren<Animator>();
        if (animator == null || animator.runtimeAnimatorController == null)
        {
            return crashedFallbackDuration;
        }

        AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
        for (int i = 0; i < clips.Length; i++)
        {
            AnimationClip clip = clips[i];
            if (clip != null && clip.name == crashedClipName)
            {
                return clip.length;
            }
        }

        return crashedFallbackDuration;
    }
}
