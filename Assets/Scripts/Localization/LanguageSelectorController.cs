using System.Collections;
using Kuchinashi.DataSystem;
using Kuchinashi.Utils.Progressable;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;
/// <summary>
/// 首次启动时根据 <see cref="LocalizationSettings"/> 可用 Locale 生成语言按钮；
/// 选定后写入 <see cref="UserConfig"/>，淡出面板并延迟后再解除首包加载阻塞。
/// </summary>
[DefaultExecutionOrder(-200)]
public class LanguageSelectorController : MonoBehaviour
{
    public const string SelectedLocaleConfigKey = "SelectedLocale";
    const string LocaleLabelTable = "CommonStringsTable";
    const string LocaleLabelKey = "str_Locale";

    [SerializeField] private GameObject template;
    [SerializeField] private CanvasGroupAlphaProgressable progressable;

    static LanguageSelectorController s_Instance;

    bool m_AwaitingSelection;
    bool m_IsFinishingSelection;

    const float HideTransitionSeconds = 0.2f;
    const float DelayBeforeContinueSeconds = 1f;

    /// <summary>存在语言选择器且仍在等待玩家首次选语言时为 true。</summary>
    public static bool IsAwaitingFirstLaunchSelection =>
        s_Instance != null && s_Instance.m_AwaitingSelection;

    void Awake()
    {
        s_Instance = this;
        progressable ??= GetComponent<CanvasGroupAlphaProgressable>();

        if (template == null)
        {
            Transform templateTransform = transform.Find("Template");
            if (templateTransform != null)
                template = templateTransform.gameObject;
        }

        if (template != null)
            template.SetActive(false);

        m_AwaitingSelection = !UserConfig.Contains(SelectedLocaleConfigKey);
        if (!m_AwaitingSelection && progressable != null)
            progressable.Progress = 0f;
    }

    void OnDestroy()
    {
        if (s_Instance == this)
            s_Instance = null;
    }

    void Start()
    {
        StartCoroutine(InitializeRoutine());
    }

    IEnumerator InitializeRoutine()
    {
        if (!LocalizationSettings.InitializationOperation.IsDone)
            yield return LocalizationSettings.InitializationOperation;

        if (!m_AwaitingSelection)
        {
            ApplySavedLocaleIfAny();
            yield break;
        }

        if (progressable != null)
            progressable.Progress = 1f;

        yield return BuildLocaleButtonsRoutine();
    }

    IEnumerator BuildLocaleButtonsRoutine()
    {
        if (template == null)
        {
#if UNITY_EDITOR
            Debug.LogError("[LanguageSelectorController] 未指定 Template。", this);
#endif
            m_AwaitingSelection = false;
            yield break;
        }

        var locales = LocalizationSettings.AvailableLocales?.Locales;
        if (locales == null || locales.Count == 0)
        {
#if UNITY_EDITOR
            Debug.LogError("[LanguageSelectorController] 没有可用的 Locale。", this);
#endif
            m_AwaitingSelection = false;
            yield break;
        }

        for (var i = 0; i < locales.Count; i++)
        {
            Locale locale = locales[i];
            if (locale == null)
                continue;

            GameObject instance = Instantiate(template, transform);
            instance.name = $"Language_{locale.Identifier.Code}";
            instance.SetActive(true);

            var label = instance.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                var handle = LocalizationSettings.StringDatabase.GetLocalizedStringAsync(
                    LocaleLabelTable, LocaleLabelKey, locale);
                yield return handle;

                if (handle.Status == AsyncOperationStatus.Succeeded && !string.IsNullOrEmpty(handle.Result))
                    label.text = handle.Result;
                else
                {
#if UNITY_EDITOR
                    Debug.LogWarning(
                        $"[LanguageSelectorController] 无法读取 {LocaleLabelTable}.{LocaleLabelKey}（{locale.Identifier.Code}），使用 LocaleName 回退。",
                        instance);
#endif
                    label.text = locale.LocaleName;
                }

                if (handle.IsValid())
                    handle.Release();
            }

            if (!instance.TryGetComponent(out Button button))
            {
#if UNITY_EDITOR
                Debug.LogWarning(
                    $"[LanguageSelectorController] 按钮缺少 Button 组件：{locale.LocaleName}",
                    instance);
#endif
                continue;
            }

            Locale captured = locale;
            button.onClick.AddListener(() => OnLocaleSelected(captured));
        }
    }

    void OnLocaleSelected(Locale locale)
    {
        if (locale == null || !m_AwaitingSelection || m_IsFinishingSelection)
            return;

        m_IsFinishingSelection = true;
        LocalizationSettings.SelectedLocale = locale;
        UserConfig.Write(SelectedLocaleConfigKey, locale.Identifier.Code);
        SetButtonsInteractable(false);
        StartCoroutine(FinishSelectionRoutine());
    }

    IEnumerator FinishSelectionRoutine()
    {
        if (progressable != null)
        {
            progressable.InverseLinearTransition(HideTransitionSeconds);
            yield return new WaitForSeconds(HideTransitionSeconds);
        }

        yield return new WaitForSeconds(DelayBeforeContinueSeconds);

        m_AwaitingSelection = false;
        m_IsFinishingSelection = false;
    }

    void SetButtonsInteractable(bool interactable)
    {
        var buttons = GetComponentsInChildren<Button>(true);
        for (var i = 0; i < buttons.Length; i++)
        {
            if (template != null && buttons[i].gameObject == template)
                continue;

            buttons[i].interactable = interactable;
        }
    }

    void ApplySavedLocaleIfAny()
    {
        if (!UserConfig.TryRead(SelectedLocaleConfigKey, out string code) || string.IsNullOrEmpty(code))
            return;

        Locale locale = LocalizationSettings.AvailableLocales?.GetLocale(new LocaleIdentifier(code));
        if (locale == null)
        {
#if UNITY_EDITOR
            Debug.LogWarning(
                $"[LanguageSelectorController] 存档语言「{code}」不在 AvailableLocales 中。",
                this);
#endif
            return;
        }

        LocalizationSettings.SelectedLocale = locale;
    }
}
