using System;
using UnityEngine;

#if UNITY_EDITOR
using System.Text.RegularExpressions;
using UnityEditor;
# endif

[CreateAssetMenu(fileName = "DialogueData", menuName = "Dialogue/Dialogue Data")]
public class DialogueData : ScriptableObject
{
    public DialogueLine[] lines;
}

[Serializable]
public struct DialogueLine
{
    [TextArea(2, 5)] public string text;
    public string[] options;
}

#if UNITY_EDITOR

[CustomPropertyDrawer(typeof(DialogueLine))]
public class DialogueLineDrawer : PropertyDrawer
{
    private static readonly Regex s_RichTextTagRegex = new Regex("<.*?>", RegexOptions.Compiled);

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        SerializedProperty textProperty = property.FindPropertyRelative("text");

        string title = textProperty != null
            ? StripRichTextTags(textProperty.stringValue)
            : label.text;

        if (string.IsNullOrWhiteSpace(title))
        {
            title = label.text;
        }

        title = title.Replace("\n", " ").Replace("\r", " ");
        label.text = title.Length > 40 ? title.Substring(0, 40) + "..." : title;

        EditorGUI.PropertyField(position, property, label, true);
    }

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        return EditorGUI.GetPropertyHeight(property, label, true);
    }

    private static string StripRichTextTags(string value)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : s_RichTextTagRegex.Replace(value, string.Empty);
    }
}
#endif