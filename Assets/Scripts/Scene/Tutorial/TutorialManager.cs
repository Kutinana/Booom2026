using Kuchinashi.Utils.Progressable;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Playables;
using Kuchinashi.DataSystem;

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

    [Header("Tutorials")]
    [SerializeField] private PlayableAsset tutorial;
    [SerializeField] private PlayableAsset afterTutorial;

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

        if (UserConfig.TryRead<bool>("HasAlreadyPlayedAfterTutorial", out bool flag) && flag)
            return;
        else if (UserConfig.TryRead<bool>("HasAlreadyPlayedTutorial", out var a) && a
        && UserConfig.TryRead<bool>("HasAlreadyStartedAfterTutorial", out var b) && b)
            StartTutorial(afterTutorial);
        else
        {
            StartTutorial(tutorial);
        }
    }

    void OnDisable()
    {
        if (timelineDirector != null)
            timelineDirector.stopped -= OnTimelineDirectorStopped;

        if (dialogueSystem != null)
        {
            dialogueSystem.OnDialogueStarted -= OnDialogueStarted;
            dialogueSystem.OnDialogueEnded -= OnDialogueEnded;
        }

        UnsubscribePlayerTap();
    }

    void OnTimelineDirectorStopped(PlayableDirector director)
    {
        if (director != timelineDirector)
            return;

        OnTutorialFinished();
    }

    void OnTutorialFinished()
    {
        timelinePausedForDialogue = false;

        if (player != null)
            player.MovementInputDisabled = false;

        if (timelineDirector.playableAsset is PlayableAsset tutorialAsset)
        {
            UserConfig.Write("HasAlreadyPlayedTutorial", true);
        }
        else if (timelineDirector.playableAsset is PlayableAsset afterTutorialAsset)
        {
            UserConfig.Write("HasAlreadyPlayedAfterTutorial", true);
        }
    }

    public void StartTutorial(PlayableAsset asset)
    {
        player.MovementInputDisabled = true;

        if (asset is PlayableAsset tutorialAsset)
        {
            TrySubscribePlayerTap();
        }
        else if (asset is PlayableAsset afterTutorialAsset)
        {
            UserConfig.Write("HasAlreadyStartedAfterTutorial", true);
        }

        timelineDirector.time = 0f;
        timelineDirector.Play(asset);
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

    public void UnsubscribePlayerTap()
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
