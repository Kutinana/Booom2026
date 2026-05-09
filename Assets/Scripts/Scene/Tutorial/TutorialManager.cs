using Kuchinashi.Utils.Progressable;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Playables;

/// <summary>
/// 教程场景：整段对话期间暂停 Timeline，对话结束后恢复。
/// </summary>
public class TutorialManager : MonoBehaviour
{
    [SerializeField] PlayableDirector timelineDirector;
    [SerializeField] DialogueSystem dialogueSystem;
    [SerializeField] private Progressable blackEdges;
    [SerializeField] PlayerController player;
    [SerializeField] UnityEvent onPlayerTapped;

    bool timelinePausedForDialogue;
    bool tapSubscribed;

    void OnEnable()
    {
        if (timelineDirector != null)
            timelineDirector.stopped += OnTimelineDirectorStopped;

        if (dialogueSystem != null)
        {
            dialogueSystem.OnDialogueStarted += OnDialogueStarted;
            dialogueSystem.OnDialogueEnded += OnDialogueEnded;
        }

        StartTutorial();
    }

    void OnDisable()
    {
        if (timelineDirector != null)
            timelineDirector.stopped -= OnTimelineDirectorStopped;
    }

    void OnTimelineDirectorStopped(PlayableDirector director)
    {
        if (director != timelineDirector)
            return;

        OnTutorialFinished();
    }

    void OnTutorialFinished()
    {
        if (dialogueSystem != null)
        {
            dialogueSystem.OnDialogueStarted -= OnDialogueStarted;
            dialogueSystem.OnDialogueEnded -= OnDialogueEnded;
        }

        UnsubscribePlayerTap();
        timelinePausedForDialogue = false;

        if (player != null)
            player.MovementInputDisabled = false;
    }

    public void StartTutorial()
    {
        TrySubscribePlayerTap();
        player.MovementInputDisabled = true;

        timelineDirector.time = 0f;
        timelineDirector.Play();
    }

    public void Pause()
    {
        if (timelineDirector == null)
            return;

        timelineDirector.Pause();
    }

    public void Resume()
    {
        if (timelineDirector == null)
            return;

        timelineDirector.Resume();
    }

    void OnDialogueStarted()
    {
        if (timelineDirector == null)
            return;

        if (timelineDirector.state == PlayState.Playing)
        {
            timelineDirector.Pause();
            timelinePausedForDialogue = true;
        }
    }

    void OnDialogueEnded()
    {
        if (!timelinePausedForDialogue || timelineDirector == null)
            return;

        timelineDirector.Resume();
        timelinePausedForDialogue = false;
    }

    void HandlePlayerTapped()
    {
        onPlayerTapped?.Invoke();
    }

    void TrySubscribePlayerTap()
    {
        if (tapSubscribed)
            return;

        if (player == null && ServiceBase.TryGet(out PlayerService playerService))
            player = playerService.Player;

        if (player == null)
            return;

        player.OnTapped += HandlePlayerTapped;
        tapSubscribed = true;
    }

    void UnsubscribePlayerTap()
    {
        if (!tapSubscribed || player == null)
        {
            tapSubscribed = false;
            return;
        }

        player.OnTapped -= HandlePlayerTapped;
        tapSubscribed = false;
    }
}
