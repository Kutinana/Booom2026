using System.Collections.Generic;
using Kuchinashi.SceneFlow;
using UnityEngine;

/// <summary>
/// 世界地图选关盒子：玩家在各自 <see cref="interactionRadius"/> 内时参与竞争；
/// 仅统计位于玩家<strong>视线朝向一侧</strong>（与 <see cref="PlayerAnimationController"/> 的 flip 一致）的盒子，在其中取最近者为 <see cref="CurrentFocus"/>；按 E 经 SceneFlow 进入 <see cref="levelSceneName"/>。
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(50)]
public class LevelBoxController : MonoBehaviour
{
    private const float ForwardHalfPlaneEpsilon = 0.02f;

    [SerializeField] private string levelSceneName;

    [Header("选中描边（Custom/SpriteEdgeGlow）")]
    [Tooltip("例如 Assets/Art/Shaders/Custom_SpriteEdgeGlow.mat")]
    [SerializeField] private Material edgeGlowMaterial;

    [SerializeField, Min(0.01f)]
    private float interactionRadius = 2f;

    /// <summary>本帧玩家可用的唯一焦点盒子（范围内、在玩家朝向一侧、距离最近）。</summary>
    public static LevelBoxController CurrentFocus { get; private set; }

    private static readonly List<LevelBoxController> s_Instances = new List<LevelBoxController>(16);
    private static int s_LastGlobalFrame = -1;

    /// <summary>本实例是否为当前全局焦点（供高亮 UI 等读取）。</summary>
    public bool IsFocused => CurrentFocus == this;

    /// <summary>本盒子配置的目标关卡场景名。</summary>
    public string LevelSceneName => levelSceneName;

    private SpriteRenderer spriteRenderer;
    private Material baselineSharedMaterial;
    private bool baselineCaptured;

    private void Awake()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
    }

    private void OnEnable()
    {
        if (!s_Instances.Contains(this))
        {
            s_Instances.Add(this);
        }

        CaptureBaselineMaterialIfNeeded();
    }

    private void OnDisable()
    {
        RestoreBaselineMaterial();
        baselineCaptured = false;
        baselineSharedMaterial = null;

        s_Instances.Remove(this);
        if (CurrentFocus == this)
        {
            CurrentFocus = null;
        }
    }

    private void LateUpdate()
    {
        if (Time.frameCount != s_LastGlobalFrame)
        {
            s_LastGlobalFrame = Time.frameCount;
            TickGlobalFocusAndInteract();
        }

        ApplyFocusHighlightMaterial();
    }

    private void TickGlobalFocusAndInteract()
    {
        CurrentFocus = null;

        if (!ServiceBase.TryGet(out PlayerService playerService) || playerService.Player == null)
        {
            return;
        }

        PlayerController player = playerService.Player;
        if (player.MovementInputDisabled || player.IsDying)
        {
            return;
        }

        Vector2 playerFacing = GetPlayerHorizontalFacing(player);
        Vector2 playerPos = player.transform.position;
        LevelBoxController best = null;
        float bestDistSq = float.PositiveInfinity;

        for (int i = 0; i < s_Instances.Count; i++)
        {
            LevelBoxController box = s_Instances[i];
            if (box == null || !box.isActiveAndEnabled)
            {
                continue;
            }

            float r = Mathf.Max(0.01f, box.interactionRadius);
            float rSq = r * r;
            Vector2 boxPos = box.transform.position;
            Vector2 fromPlayer = boxPos - playerPos;
            float dSq = fromPlayer.sqrMagnitude;
            if (dSq > rSq)
            {
                continue;
            }

            // 仅保留玩家「面朝」那一侧的箱子（左 / 右半平面）
            if (Vector2.Dot(fromPlayer, playerFacing) <= ForwardHalfPlaneEpsilon)
            {
                continue;
            }

            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                best = box;
            }
        }

        CurrentFocus = best;
        if (CurrentFocus == null)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.E))
        {
            CurrentFocus.TryRequestEnterLevel();
        }
    }

    /// <summary>
    /// 与 <see cref="PlayerAnimationController.UpdateFlip"/> 一致：优先用子物体 <see cref="SpriteRenderer.flipX"/>，否则用水平速度、再否则用水平输入，最后默认朝右。
    /// </summary>
    private static Vector2 GetPlayerHorizontalFacing(PlayerController player)
    {
        SpriteRenderer sr = player.GetComponentInChildren<SpriteRenderer>(true);
        if (sr != null)
        {
            return sr.flipX ? Vector2.left : Vector2.right;
        }

        float vx = player.Velocity.x;
        if (Mathf.Abs(vx) > 0.05f)
        {
            return vx < 0f ? Vector2.left : Vector2.right;
        }

        float mx = player.MoveInput.x;
        if (Mathf.Abs(mx) > 0.01f)
        {
            return mx < 0f ? Vector2.left : Vector2.right;
        }

        return Vector2.right;
    }

    private void TryRequestEnterLevel()
    {
        if (string.IsNullOrWhiteSpace(levelSceneName))
        {
            Debug.LogWarning("[LevelBox] levelSceneName 为空，无法切换场景。", this);
            return;
        }

        string target = levelSceneName.Trim();

        SceneFlowController flow = SceneFlowController.Instance;
        if (flow != null && flow.IsConfigured)
        {
            if (flow.IsTransitioning)
            {
                return;
            }

            if (flow.TryRequestSwitchContent(target, false))
            {
                return;
            }

            Debug.LogWarning($"[LevelBox] SceneFlow 未切换到「{target}」（可能已在目标场景、名称非法或管线拒绝）。", this);
            return;
        }

        SceneFlowHost host = FindFirstObjectByType<SceneFlowHost>();
        if (host != null && host.TryJumpToScene(target))
        {
            return;
        }

        Debug.LogWarning("[LevelBox] 未找到可用的 SceneFlowController / SceneFlowHost，或切换被拒绝。", this);
    }

    private void EnsureSpriteRenderer()
    {
        if (spriteRenderer == null)
        {
            spriteRenderer = GetComponent<SpriteRenderer>();
        }
    }

    private void CaptureBaselineMaterialIfNeeded()
    {
        if (baselineCaptured)
        {
            return;
        }

        EnsureSpriteRenderer();
        if (spriteRenderer == null)
        {
            return;
        }

        baselineSharedMaterial = spriteRenderer.sharedMaterial;
        baselineCaptured = baselineSharedMaterial != null;
    }

    private void RestoreBaselineMaterial()
    {
        EnsureSpriteRenderer();
        if (spriteRenderer == null || !baselineCaptured || baselineSharedMaterial == null)
        {
            return;
        }

        if (spriteRenderer.sharedMaterial != baselineSharedMaterial)
        {
            spriteRenderer.sharedMaterial = baselineSharedMaterial;
        }
    }

    private void ApplyFocusHighlightMaterial()
    {
        if (edgeGlowMaterial == null)
        {
            return;
        }

        EnsureSpriteRenderer();
        if (spriteRenderer == null)
        {
            return;
        }

        if (!baselineCaptured)
        {
            CaptureBaselineMaterialIfNeeded();
        }

        if (baselineSharedMaterial == null)
        {
            return;
        }

        bool focused = CurrentFocus == this;
        Material target = focused ? edgeGlowMaterial : baselineSharedMaterial;
        if (spriteRenderer.sharedMaterial != target)
        {
            spriteRenderer.sharedMaterial = target;
        }
    }
}
