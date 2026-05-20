using System;
using Kuchinashi.Utils.Progressable;
using UnityEngine;

/// <summary>
/// 纯对话演出：可选黑边 + 可选禁用移动输入，调用 <see cref="DialogueSystem.StartDialogue"/>。
/// 与 Timeline 解耦；同一时间只处理由本组件发起的一段对话。
/// </summary>
public sealed class DialogueSequencePlayer : MonoBehaviour
{
    [SerializeField] private DialogueSystem dialogueSystem;
    [SerializeField] private Progressable blackEdges;
    [SerializeField] private PlayerController player;
    [SerializeField] private bool lockMovementInput = true;
    [SerializeField, Min(0.01f)] private float blackEdgeFadeSeconds = 0.25f;

    bool sessionActive;
    bool movementLockedThisSession;
    bool blackEdgesUsedThisSession;

    /// <summary>由本组件发起、且对话已正常播完并完成收尾（黑边收回、输入恢复）后触发。</summary>
    public event Action SessionEnded;

    public bool IsSessionActive => sessionActive;

    /// <summary>通过 <see cref="DialogueDataTable"/> 条目 Key 开始对话；若已有本会话在播则忽略。</summary>
    public bool Play(string dialogueTableEntryKey)
    {
        if (string.IsNullOrEmpty(dialogueTableEntryKey))
        {
            Debug.LogWarning("[DialogueSequencePlayer] 对话表条目 Key 为空。", this);
            return false;
        }

        if (!EnsureDialogueSystem())
            return false;

        if (sessionActive)
        {
            Debug.LogWarning("[DialogueSequencePlayer] 已有对话会话进行中，忽略重复 Play。", this);
            return false;
        }

        BeginSession();
        dialogueSystem.OnDialogueEnded += OnHostedDialogueEnded;
        dialogueSystem.StartDialogue(dialogueTableEntryKey);
        return true;
    }

    /// <summary>开始一段对话；若已有本会话在播则忽略。成功开始则返回 true。</summary>
    public bool Play(DialogueData data)
    {
        if (data == null)
        {
            Debug.LogWarning("[DialogueSequencePlayer] DialogueData 为空。", this);
            return false;
        }

        if (!EnsureDialogueSystem())
            return false;

        if (sessionActive)
        {
            Debug.LogWarning("[DialogueSequencePlayer] 已有对话会话进行中，忽略重复 Play。", this);
            return false;
        }

        BeginSession();
        dialogueSystem.OnDialogueEnded += OnHostedDialogueEnded;
        dialogueSystem.StartDialogue(data);
        return true;
    }

    bool EnsureDialogueSystem()
    {
        if (dialogueSystem != null)
            return true;

        dialogueSystem = FindFirstObjectByType<DialogueSystem>();
        if (dialogueSystem == null)
        {
            Debug.LogError("[DialogueSequencePlayer] 未指定且找不到 DialogueSystem。", this);
            return false;
        }

        return true;
    }

    void BeginSession()
    {
        sessionActive = true;
        movementLockedThisSession = false;
        blackEdgesUsedThisSession = false;

        if (lockMovementInput)
        {
            PlayerController p = ResolvePlayer();
            if (p != null)
            {
                p.MovementInputDisabled = true;
                movementLockedThisSession = true;
            }
        }

        if (blackEdges != null)
        {
            blackEdges.LinearTransition(blackEdgeFadeSeconds);
            blackEdgesUsedThisSession = true;
        }
    }

    void OnDisable()
    {
        if (dialogueSystem != null)
            dialogueSystem.OnDialogueEnded -= OnHostedDialogueEnded;

        if (!sessionActive)
            return;

        sessionActive = false;

        if (blackEdgesUsedThisSession && blackEdges != null)
            blackEdges.InverseLinearTransition(blackEdgeFadeSeconds);

        if (movementLockedThisSession)
        {
            movementLockedThisSession = false;
            PlayerController p = ResolvePlayer();
            if (p != null)
                p.MovementInputDisabled = false;
        }
    }

    void OnHostedDialogueEnded()
    {
        if (!sessionActive)
            return;

        sessionActive = false;

        if (dialogueSystem != null)
            dialogueSystem.OnDialogueEnded -= OnHostedDialogueEnded;

        if (blackEdgesUsedThisSession && blackEdges != null)
            blackEdges.InverseLinearTransition(blackEdgeFadeSeconds);

        if (movementLockedThisSession)
        {
            movementLockedThisSession = false;
            PlayerController p = ResolvePlayer();
            if (p != null)
                p.MovementInputDisabled = false;
        }

        SessionEnded?.Invoke();
    }

    PlayerController ResolvePlayer()
    {
        if (player != null)
            return player;

        if (ServiceBase.TryGet(out PlayerService playerService) && playerService.Player != null)
            return playerService.Player;

        return FindFirstObjectByType<PlayerController>();
    }
}
