using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;


public class DialogueSystem : MonoBehaviour
{
    [Header("Auto Size")]
    public RectTransform dialoguePanelRect;
    public Vector2 padding = new Vector2(40, 30); // 内边距
    public float maxWidth = 800f;

    [Header("UI")]
    public TMP_Text textBox;
    public GameObject dialoguePanel;

    [Header("Audio")]
    public AudioClip typingClip;
    public AudioSourceGroup audioSourceGroup;

    private DialogueVertexAnimator animator;

    private Queue<string> dialogueQueue = new Queue<string>();
    private Coroutine typingCoroutine;

    private bool isPlaying = false;

    [Header("自动播放相关")]
    private bool autoAdvance;
    private float autoDelay;

    [Header("订单选择相关")]
    public System.Action onAccept;
    public System.Action onReject;

    void Awake()
    {
        animator = new DialogueVertexAnimator(textBox, audioSourceGroup);
    }

    void Update()
    {
        if (!isPlaying) return;

        if (Input.GetMouseButtonDown(0))
        {
            OnClick();
        }
        if (!animator.textAnimating)
            EndALine();

    }

    // 开始对话
    public void StartDialogue(DialogueData data)
    {
        dialoguePanel.SetActive(true);
        dialogueQueue.Clear();

        foreach (var line in data.lines)
        {
           

            dialogueQueue.Enqueue(line);
        }

        isPlaying = true;
        PlayNextLine();
    }

    void OnClick()
    {
        if (animator.textAnimating)
        {
            animator.SkipToEndOfCurrentMessage();
        }
        else
        {
            PlayNextLine();
        }
    }

    void PlayNextLine()
    {
        if (dialogueQueue.Count == 0)
        {
            EndDialogue();
            return;
        }
        

        string line = dialogueQueue.Dequeue();
        autoAdvance = DialogueTagParser.TryParseAutoTag(ref line, out autoDelay);

        this.EnsureCoroutineStopped(ref typingCoroutine);
        animator.textAnimating = false;

        List<DialogueCommand> commands =
            DialogueUtility.ProcessInputString(line, out string processedText);

      
        

        typingCoroutine = StartCoroutine(
            animator.AnimateTextIn(commands, processedText, typingClip, null)
        );
    }

    void EndALine()
    {
        if(autoAdvance)
        PlayNextLine();
    }


    void EndDialogue()
    {
        isPlaying = false;
        dialoguePanel.SetActive(false);
    }

    
    

}