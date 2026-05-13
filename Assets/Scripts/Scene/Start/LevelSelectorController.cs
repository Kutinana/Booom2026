using System;
using System.Collections.Generic;using Kuchinashi.SceneFlow;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 在运行时根据序列化的关卡场景名列表，在指定父节点下用模板生成跳转按钮；顺序为「Level Tutorial / Level-Tutorial」优先，其余按自然序（如 9 在 10 前）；跳转走 <see cref="SceneFlowHost"/>（SceneFlow / SceneControl 管线）。
/// </summary>
public class LevelSelectorController : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private Transform buttonsParent;
    [SerializeField] private GameObject buttonTemplate;

    [Header("Scene flow")]
    [Tooltip("若为空，运行时会查找场景中的 SceneFlowHost。")]
    [SerializeField] private SceneFlowHost sceneFlowHost;

    [Header("Levels (由自定义 Inspector 从 Build Settings 多选写入)")]
    [SerializeField] private List<string> selectedLevelSceneNames = new List<string>();

    private readonly List<GameObject> spawnedInstances = new List<GameObject>();

    public IReadOnlyList<string> SelectedLevelSceneNames => selectedLevelSceneNames;

    private void Start()
    {
        RebuildButtons();
    }

    private void OnDestroy()
    {
        ClearSpawned();
    }

    /// <summary>可在运行时再次调用以刷新按钮（例如热更配置后）。</summary>
    public void RebuildButtons()
    {
        ClearSpawned();

        if (buttonsParent == null || buttonTemplate == null)
        {
            Debug.LogWarning("[LevelSelector] 请指定 Buttons Parent 与 Button Template。");
            return;
        }

        var ordered = SortLevelSceneDisplayOrder(selectedLevelSceneNames);
        foreach (string sceneName in ordered)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                continue;
            }

            var instance = Instantiate(buttonTemplate, buttonsParent, false);
            instance.name = $"LevelButton_{sceneName}";
            instance.SetActive(true);
            ApplyLabel(instance, sceneName);

            var button = instance.GetComponentInChildren<Button>(true);
            if (button != null)
            {
                string captured = sceneName;
                button.onClick.AddListener(() => OnLevelButtonClicked(captured));
            }
            else
            {
                Debug.LogWarning($"[LevelSelector] 模板上未找到 Button，场景「{sceneName}」的实例将无法点击。");
            }

            spawnedInstances.Add(instance);
        }
    }

    private void OnLevelButtonClicked(string sceneName)
    {
        var host = sceneFlowHost != null ? sceneFlowHost : FindFirstObjectByType<SceneFlowHost>();
        if (host == null)
        {
            Debug.LogError("[LevelSelector] 未找到 SceneFlowHost，无法切换场景。请在 Shell 上配置 SceneFlowHost 或在本组件上指定引用。");
            return;
        }

        if (!host.TryJumpToScene(sceneName))
        {
            Debug.LogWarning($"[LevelSelector] 跳转「{sceneName}」失败（可能未加入 Build、与 Shell 同名或正在过渡中）。");
        }
    }

    private void ClearSpawned()
    {
        for (int i = 0; i < spawnedInstances.Count; i++)
        {
            if (spawnedInstances[i] != null)
            {
                Destroy(spawnedInstances[i]);
            }
        }

        spawnedInstances.Clear();
    }

    private static void ApplyLabel(GameObject root, string sceneName)
    {
        var tmp = root.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null)
        {
            tmp.text = sceneName;
            return;
        }

        var text = root.GetComponentInChildren<Text>(true);
        if (text != null)
        {
            text.text = sceneName;
        }
    }

    private static List<string> SortLevelSceneDisplayOrder(IReadOnlyList<string> names)
    {
        var copy = new List<string>(names);
        copy.Sort(CompareLevelSceneDisplayOrder);
        return copy;
    }

    private static int CompareLevelSceneDisplayOrder(string a, string b)
    {
        int pa = TutorialFirstPriority(a);
        int pb = TutorialFirstPriority(b);
        if (pa != pb)
        {
            return pa.CompareTo(pb);
        }

        return NaturalCompareOrdinalIgnoreCase(a, b);
    }

    /// <summary>0 = 固定排最前（教学关 Level Tutorial / Level-Tutorial）。</summary>
    private static int TutorialFirstPriority(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            return 1;
        }

        if (sceneName.Equals("Level-Tutorial", StringComparison.OrdinalIgnoreCase)
            || sceneName.Equals("Level Tutorial", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return 1;
    }

    /// <summary>数字段按数值比较，避免纯字典序下 10 排在 2 前。</summary>
    private static int NaturalCompareOrdinalIgnoreCase(string a, string b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        if (a == null)
        {
            return -1;
        }

        if (b == null)
        {
            return 1;
        }

        int i = 0;
        int j = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i];
            char cb = b[j];
            bool da = char.IsDigit(ca);
            bool db = char.IsDigit(cb);
            if (da && db)
            {
                long na = 0;
                while (i < a.Length && char.IsDigit(a[i]))
                {
                    na = na * 10 + (a[i] - '0');
                    i++;
                }

                long nb = 0;
                while (j < b.Length && char.IsDigit(b[j]))
                {
                    nb = nb * 10 + (b[j] - '0');
                    j++;
                }

                int c = na.CompareTo(nb);
                if (c != 0)
                {
                    return c;
                }
            }
            else
            {
                int c = char.ToUpperInvariant(ca).CompareTo(char.ToUpperInvariant(cb));
                if (c != 0)
                {
                    return c;
                }

                i++;
                j++;
            }
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }
}
