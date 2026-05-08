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

        using (new EditorGUI.DisabledScope(!Application.isPlaying || system.editorTestData == null))
        {
            if (GUILayout.Button("播放选中的 DialogueData"))
            {
                system.StartDialogue(system.editorTestData);
            }
        }

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("仅在 Play Mode 下可用。", MessageType.Info);
        }
        else if (system.editorTestData == null)
        {
            EditorGUILayout.HelpBox("请先在上方 \"Editor 测试\" 中指定一个 DialogueData。", MessageType.Warning);
        }
    }
}
#endif
