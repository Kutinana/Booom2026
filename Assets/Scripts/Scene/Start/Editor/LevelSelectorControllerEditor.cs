using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(LevelSelectorController))]
public class LevelSelectorControllerEditor : Editor
{
    SerializedProperty selectedLevelSceneNamesProp;
    Vector2 scroll;

    void OnEnable()
    {
        selectedLevelSceneNamesProp = serializedObject.FindProperty("selectedLevelSceneNames");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawPropertiesExcluding(serializedObject, "m_Script", "selectedLevelSceneNames");

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("关卡场景", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("下列为 Build Settings 中已启用的场景。运行时按钮顺序：Level Tutorial / Level-Tutorial 固定第一，其余为自然序（数字按数值，例如 …9、10…）。", MessageType.None);

        var buildNames = CollectEnabledBuildSceneNames();
        var selected = GetSelectedSet();

        int invalidSelected = CountInvalidSelections(selected, buildNames);
        if (invalidSelected > 0)
        {
            EditorGUILayout.HelpBox($"有 {invalidSelected} 个已选场景名已不在当前 Build Settings 中，可取消勾选或从列表中移除。", MessageType.Warning);
        }

        using (var scrollScope = new EditorGUILayout.ScrollViewScope(scroll, GUILayout.MinHeight(120f), GUILayout.MaxHeight(280f)))
        {
            scroll = scrollScope.scrollPosition;
            foreach (string name in buildNames)
            {
                bool was = selected.Contains(name);
                bool now = EditorGUILayout.ToggleLeft(name, was);
                if (now != was)
                {
                    ToggleNameInPropertyList(name, now);
                    selected = GetSelectedSet();
                }
            }

            if (buildNames.Count == 0)
            {
                EditorGUILayout.LabelField("（Build Settings 中没有已启用的场景）");
            }
        }

        EditorGUILayout.Space(4f);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("全选"))
            {
                selectedLevelSceneNamesProp.ClearArray();
                for (int i = 0; i < buildNames.Count; i++)
                {
                    selectedLevelSceneNamesProp.InsertArrayElementAtIndex(selectedLevelSceneNamesProp.arraySize);
                    selectedLevelSceneNamesProp.GetArrayElementAtIndex(selectedLevelSceneNamesProp.arraySize - 1).stringValue = buildNames[i];
                }
            }

            if (GUILayout.Button("全不选"))
            {
                selectedLevelSceneNamesProp.ClearArray();
            }
        }

        serializedObject.ApplyModifiedProperties();
    }

    static List<string> CollectEnabledBuildSceneNames()
    {
        var list = new List<string>();
        foreach (var s in EditorBuildSettings.scenes)
        {
            if (!s.enabled || string.IsNullOrEmpty(s.path))
            {
                continue;
            }

            list.Add(Path.GetFileNameWithoutExtension(s.path));
        }

        return list;
    }

    HashSet<string> GetSelectedSet()
    {
        var set = new HashSet<string>(System.StringComparer.Ordinal);
        for (int i = 0; i < selectedLevelSceneNamesProp.arraySize; i++)
        {
            string v = selectedLevelSceneNamesProp.GetArrayElementAtIndex(i).stringValue;
            if (!string.IsNullOrEmpty(v))
            {
                set.Add(v);
            }
        }

        return set;
    }

    static int CountInvalidSelections(HashSet<string> selected, List<string> buildNames)
    {
        var buildSet = new HashSet<string>(buildNames, System.StringComparer.Ordinal);
        int c = 0;
        foreach (var s in selected)
        {
            if (!buildSet.Contains(s))
            {
                c++;
            }
        }

        return c;
    }

    void ToggleNameInPropertyList(string sceneName, bool add)
    {
        if (add)
        {
            for (int i = 0; i < selectedLevelSceneNamesProp.arraySize; i++)
            {
                if (selectedLevelSceneNamesProp.GetArrayElementAtIndex(i).stringValue == sceneName)
                {
                    return;
                }
            }

            selectedLevelSceneNamesProp.InsertArrayElementAtIndex(selectedLevelSceneNamesProp.arraySize);
            selectedLevelSceneNamesProp.GetArrayElementAtIndex(selectedLevelSceneNamesProp.arraySize - 1).stringValue = sceneName;
        }
        else
        {
            for (int i = selectedLevelSceneNamesProp.arraySize - 1; i >= 0; i--)
            {
                if (selectedLevelSceneNamesProp.GetArrayElementAtIndex(i).stringValue == sceneName)
                {
                    selectedLevelSceneNamesProp.DeleteArrayElementAtIndex(i);
                    break;
                }
            }
        }
    }
}
