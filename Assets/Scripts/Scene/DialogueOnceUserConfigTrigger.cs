using System.Collections;
using Kuchinashi.DataSystem;
using UnityEngine;

/// <summary>
/// 首次进入（或满足场景名条件）时播一段对话，结束后写入 <see cref="UserConfig"/>，避免重复。
/// </summary>
public sealed class DialogueOnceUserConfigTrigger : MonoBehaviour
{
    [SerializeField] private DialogueSequencePlayer sequencePlayer;
    [SerializeField] private DialogueData dialogueData;
    [Tooltip("UserConfig 中用于「已播过」的 bool 键。")]
    [SerializeField] private string userConfigKey = "FirstEnterWorld1DialogueShown";
    [Tooltip("非空时仅当当前激活场景名等于该字符串时才尝试播放（例如 World 1）。")]
    [SerializeField] private string onlyInActiveSceneName = "World 1";
    [Tooltip("条件满足后延迟多少秒再开始对话（0 为立即）。")]
    [SerializeField, Min(0f)] private float delay = 1f;

    bool subscribed;
    Coroutine pendingPlayRoutine;

    void Start()
    {
        if (sequencePlayer == null)
        {
            sequencePlayer = FindFirstObjectByType<DialogueSequencePlayer>();
            if (sequencePlayer == null)
            {
                Debug.LogError("[DialogueOnceUserConfigTrigger] 未指定 DialogueSequencePlayer。", this);
                return;
            }
        }

        if (dialogueData == null)
        {
            Debug.LogError("[DialogueOnceUserConfigTrigger] 未指定 DialogueData。", this);
            return;
        }

        if (!string.IsNullOrEmpty(onlyInActiveSceneName))
        {
            string active = gameObject.scene.name;
            if (!string.Equals(active, onlyInActiveSceneName, System.StringComparison.Ordinal))
                return;
        }

        if (UserConfig.TryRead<bool>(userConfigKey, out bool done) && done)
            return;

        if (delay > 0f)
            pendingPlayRoutine = StartCoroutine(PlayAfterDelayRoutine());
        else
            TrySubscribeAndPlay();
    }

    IEnumerator PlayAfterDelayRoutine()
    {
        yield return new WaitForSeconds(delay);
        pendingPlayRoutine = null;
        TrySubscribeAndPlay();
    }

    void TrySubscribeAndPlay()
    {
        sequencePlayer.SessionEnded += OnSessionEnded;
        subscribed = true;
        if (!sequencePlayer.Play(dialogueData))
        {
            sequencePlayer.SessionEnded -= OnSessionEnded;
            subscribed = false;
        }
    }

    void OnDestroy()
    {
        if (pendingPlayRoutine != null)
        {
            StopCoroutine(pendingPlayRoutine);
            pendingPlayRoutine = null;
        }

        if (subscribed && sequencePlayer != null)
            sequencePlayer.SessionEnded -= OnSessionEnded;
    }

    void OnSessionEnded()
    {
        if (sequencePlayer != null)
            sequencePlayer.SessionEnded -= OnSessionEnded;
        subscribed = false;
        UserConfig.Write(userConfigKey, true);
    }
}
