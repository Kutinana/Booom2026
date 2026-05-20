#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(DialogueSystem))]
public class DialogueSystemEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var system = (DialogueSystem)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("调试播放", EditorStyles.boldLabel);

        bool hasDirectData = system.editorTestData != null;
        bool hasTableKey = !string.IsNullOrEmpty(system.editorTestEntryKey);

        using (new EditorGUI.DisabledScope(!Application.isPlaying || (!hasDirectData && !hasTableKey)))
        {
            if (GUILayout.Button("播放测试对话（表 Key 优先）"))
            {
                if (hasTableKey)
                    system.StartDialogue(system.editorTestEntryKey);
                else
                    system.StartDialogue(system.editorTestData);
            }
        }

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("仅在 Play Mode 下可用。", MessageType.Info);
        }
        else if (!hasDirectData && !hasTableKey)
        {
            EditorGUILayout.HelpBox(
                "请指定 editorTestEntryKey（DialogueDataTable 条目）或 editorTestData。",
                MessageType.Warning);
        }
    }
}
#endif
