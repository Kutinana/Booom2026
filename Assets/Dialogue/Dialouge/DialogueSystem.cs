using System;
using System.Collections;
using System.Collections.Generic;
using Kuchinashi.Utils.Progressable;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

public class DialogueSystem : MonoBehaviour
{
    [Header("Auto Size")]
    public RectTransform dialoguePanelRect;
    public Vector2 padding = new Vector2(40, 30);
    public float minWidth = 200f;
    public float maxWidth = 800f;
    public float minHeight = 100f;
    public float maxHeight = 400f;

    [Header("UI")]
    public TMP_Text textBox;
    public GameObject dialoguePanel;
    public ScaleProgressable progressable;

    [Header("Audio")]
    public AudioClip typingClip;
    public AudioSourceGroup audioSourceGroup;

    private DialogueVertexAnimator animator;

    private Queue<DialogueLine> dialogueQueue = new Queue<DialogueLine>();
    private Coroutine typingCoroutine;

    private bool isPlaying = false;

    [Header("Auto Play")]
    private bool autoAdvance;
    private float autoDelay;

    [Header("Option")]
    [SerializeField] private GameObject optionTemplate;
    [Tooltip("选项列表父节点；留空则在运行时挂在对话框下")]
    [SerializeField] private RectTransform optionArea;
    [SerializeField] private float optionGap = 12f;
    [SerializeField] private float optionAreaPadding = 25f;

    [Header("台词触发的角色动画")]
    [Tooltip("在句首或句中写 <anim=状态名>，本行开始展示前会调用 SwitchAnimationTo；可为空。")]
    [SerializeField] private PlayerController dialogueAnimPlayer;

    [Header("Localization")]
    [SerializeField] private TableReference dialogueDataTable;
    const string DefaultDialogueTableName = "DialogueDataTable";

    [Header("Editor Test")]
    public DialogueData editorTestData;
    [Tooltip("Play Mode 下通过 DialogueDataTable 测试播放；优先于 editorTestData。")]
    public string editorTestEntryKey = "Tutorial";

    private DialogueLine activeLine;
    private Coroutine loadLocalizedDialogueCoroutine;
    private bool awaitingOptionPick;
    private bool activeLineHasOptions;

    /// <summary>一段对话从 <see cref="StartDialogue"/> 调用起，到队列播完内部结束时触发对应结束事件。</summary>
    public event Action OnDialogueStarted;
    public event Action OnDialogueEnded;

    /// <summary>仅在「等待选项」状态变化时触发。</summary>
    public event Action<bool> OnAwaitingOptionPickChanged;

    void SetAwaitingOptionPick(bool value)
    {
        if (awaitingOptionPick == value)
            return;
        awaitingOptionPick = value;
        OnAwaitingOptionPickChanged?.Invoke(value);
    }

    public bool IsAwaitingOptionPick => awaitingOptionPick;
    private readonly List<GameObject> spawnedOptions = new List<GameObject>();

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

    /// <summary>
    /// 按 <see cref="DialogueDataTable"/> 中的条目 Key 加载当前语言的 <see cref="DialogueData"/> 并播放。
    /// 可供 UnityEvent（字符串参数）或 Timeline 调用。
    /// </summary>
    public void StartDialogue(string entryKey)
    {
        if (string.IsNullOrEmpty(entryKey))
        {
            Debug.LogError("DialogueSystem: 对话表条目 Key 为空。", this);
            return;
        }

        TableEntryReference entry = entryKey;
        StartDialogue(entry);
    }

    /// <summary>使用表内条目引用（与 Inspector 下拉一致）开始对话。</summary>
    public void StartDialogue(TableEntryReference entry)
    {
        if (entry.KeyId == 0 && string.IsNullOrEmpty(entry.Key))
        {
            Debug.LogError("DialogueSystem: TableEntryReference 无效。", this);
            return;
        }

        EnsureDialogueTableReference();
        this.EnsureCoroutineStopped(ref loadLocalizedDialogueCoroutine);
        loadLocalizedDialogueCoroutine = StartCoroutine(LoadAndStartDialogueRoutine(entry));
    }

    IEnumerator LoadAndStartDialogueRoutine(TableEntryReference entry)
    {
        if (!LocalizationSettings.InitializationOperation.IsDone)
            yield return LocalizationSettings.InitializationOperation;

        var handle = LocalizationSettings.AssetDatabase.GetLocalizedAssetAsync<DialogueData>(
            dialogueDataTable, entry);

        yield return handle;

        string entryLabel = !string.IsNullOrEmpty(entry.Key) ? entry.Key : entry.KeyId.ToString();

        if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
        {
            Debug.LogError(
                $"DialogueSystem: 无法从 DialogueDataTable 加载「{entryLabel}」(status={handle.Status})。",
                this);
            if (handle.IsValid())
                handle.Release();
            yield break;
        }

        var data = handle.Result;
        if (handle.IsValid())
            handle.Release();

        StartDialogue(data);
    }

    void EnsureDialogueTableReference()
    {
        if (!string.IsNullOrEmpty(dialogueDataTable.TableCollectionName))
            return;
        if (dialogueDataTable.TableCollectionNameGuid != System.Guid.Empty)
            return;

        dialogueDataTable = DefaultDialogueTableName;
    }

    // 开始对话（直接传入资源，不经过本地化表）
    public void StartDialogue(DialogueData data)
    {
        if (data == null)
        {
            Debug.LogError("DialogueSystem: DialogueData 为空。", this);
            return;
        }

        progressable.LinearTransition(0.1f);
        dialogueQueue.Clear();
        ClearOptionsUI();

        foreach (var line in data.lines)
        {
            dialogueQueue.Enqueue(line);
        }

        isPlaying = true;
        OnDialogueStarted?.Invoke();
        PlayNextLine();
    }

    void OnClick()
    {
        if (awaitingOptionPick)
            return;

        if (animator.textAnimating)
        {
            animator.SkipToEndOfCurrentMessage();
        }
        else
        {
            PlayNextLine();
        }
    }

    void ResizeDialogueBox(string text)
    {
        Vector2 preferredSize = textBox.GetPreferredValues(text, maxWidth, 0);

        float width = Mathf.Clamp(preferredSize.x, minWidth, maxWidth);
        float height = Mathf.Clamp(preferredSize.y, minHeight, maxHeight);

        width += padding.x;
        height += padding.y;

        dialoguePanelRect.sizeDelta = new Vector2(width, height);
    }

    void PlayNextLine()
    {
        if (dialogueQueue.Count == 0)
        {
            EndDialogue();
            return;
        }

        ClearOptionsUI();
        SetAwaitingOptionPick(false);

        activeLine = dialogueQueue.Dequeue();
        string line = activeLine.text;

        if (dialogueAnimPlayer != null)
        {
            while (DialogueTagParser.TryParseAnimTag(ref line, out string animState))
                dialogueAnimPlayer.SwitchAnimationTo(animState);
        }
        else
        {
            while (DialogueTagParser.TryParseAnimTag(ref line, out _)) { }
        }

        autoAdvance = DialogueTagParser.TryParseAutoTag(ref line, out autoDelay);

        activeLineHasOptions =
            activeLine.options != null && activeLine.options.Length > 0;

        this.EnsureCoroutineStopped(ref typingCoroutine);
        animator.textAnimating = false;

        List<DialogueCommand> commands =
            DialogueUtility.ProcessInputString(line, out string processedText);

        ResizeDialogueBox(processedText);

        typingCoroutine = StartCoroutine(
            animator.AnimateTextIn(commands, processedText, typingClip, () =>
                OnTextRevealComplete(processedText))
        );
    }

    void OnTextRevealComplete(string processedText)
    {
        if (!isPlaying)
            return;

        if (!activeLineHasOptions)
            return;

        if (optionTemplate == null)
        {
            Debug.LogError("DialogueSystem: 当前行包含选项但未指定 Option Template。", this);
            return;
        }

        ShowOptions(processedText);
        SetAwaitingOptionPick(spawnedOptions.Count > 0);
    }

    void ShowOptions(string processedText)
    {
        foreach (GameObject o in spawnedOptions)
            Destroy(o);
        spawnedOptions.Clear();

        int validOptionCount = 0;
        if (activeLine.options != null)
        {
            foreach (string opt in activeLine.options)
            {
                if (!string.IsNullOrEmpty(opt))
                    validOptionCount++;
            }
        }

        if (validOptionCount == 0)
        {
            optionArea.gameObject.SetActive(false);
            return;
        }

        float innerWidth = Mathf.Clamp(
            textBox.GetPreferredValues(processedText, maxWidth, 0).x,
            minWidth,
            maxWidth);

        foreach (string opt in activeLine.options)
        {
            if (string.IsNullOrEmpty(opt))
                continue;

            GameObject row = Instantiate(optionTemplate, optionArea);
            row.SetActive(true);
            row.GetComponent<RectTransform>().sizeDelta = new Vector2(dialoguePanelRect.sizeDelta.x, 100f);

            var btn = row.GetComponent<Button>();
            var label = row.GetComponentInChildren<TMP_Text>();
            if (label != null)
                label.text = opt;

            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(OnOptionClicked);
            }

            spawnedOptions.Add(row);
        }

        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(optionArea);

        optionArea.gameObject.SetActive(true);
        optionArea.anchoredPosition = new Vector2(0, - validOptionCount * 100);
    }

    void OnOptionClicked()
    {
        if (!awaitingOptionPick)
            return;
        AudioMng.Instance.PlaySfx("Select", 0.2f);
        SetAwaitingOptionPick(false);
        ClearOptionsUI();
        PlayNextLine();
    }

    void ClearOptionsUI()
    {
        foreach (GameObject o in spawnedOptions)
            Destroy(o);
        spawnedOptions.Clear();

        if (optionArea != null)
            optionArea.gameObject.SetActive(false);
    }

    void EndALine()
    {
        if (awaitingOptionPick)
            return;

        if (autoAdvance)
            PlayNextLine();
    }

    void EndDialogue()
    {
        isPlaying = false;
        SetAwaitingOptionPick(false);
        ClearOptionsUI();

        progressable.InverseLinearTransition(0.1f);
        OnDialogueEnded?.Invoke();
    }
}
