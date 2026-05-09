#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;

[CustomEditor(typeof(TutorialManager))]
public class TutorialManagerEditor : Editor
{
    PlayableAsset _previewAsset;
    PlayableAsset _savedPlayableAssetBeforePreview;
    bool _previewSessionActive;

    SerializedProperty _timelineDirector;

    void OnEnable()
    {
        _timelineDirector = serializedObject.FindProperty("timelineDirector");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawDefaultInspector();

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Timeline 预览（仅 Play 模式）", EditorStyles.boldLabel);

        if (!EditorApplication.isPlaying)
        {
            EditorGUILayout.HelpBox("进入 Play 模式后，可将 PlayableAsset 拖到下方并播放预览。", MessageType.Info);
            serializedObject.ApplyModifiedProperties();
            return;
        }

        EditorGUILayout.HelpBox("拖入 Timeline 等资源后播放，由 PlayableDirector 正常走时。「停止并还原」会恢复预览前的 Playable Asset；若片段已自然结束，也请按一次以还原。", MessageType.None);

        using (new EditorGUI.DisabledScope(_timelineDirector.objectReferenceValue == null))
        {
            _previewAsset = (PlayableAsset)EditorGUILayout.ObjectField("预览用 PlayableAsset", _previewAsset, typeof(PlayableAsset), false);

            var director = _timelineDirector.objectReferenceValue as PlayableDirector;
            bool hasDirector = director != null;
            bool playing = hasDirector && director.state == PlayState.Playing;
            bool hasPreviewAsset = _previewAsset != null;

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!hasDirector || !hasPreviewAsset || playing))
            {
                if (GUILayout.Button("播放预览"))
                {
                    if (!_previewSessionActive)
                        _savedPlayableAssetBeforePreview = director.playableAsset;
                    _previewSessionActive = true;
                    director.time = 0d;
                    director.Play(_previewAsset);
                }
            }

            using (new EditorGUI.DisabledScope(!hasDirector || !playing))
            {
                if (GUILayout.Button("暂停"))
                    director.Pause();
            }

            using (new EditorGUI.DisabledScope(!hasDirector || director.state != PlayState.Paused))
            {
                if (GUILayout.Button("继续"))
                    director.Resume();
            }

            using (new EditorGUI.DisabledScope(!hasDirector))
            {
                if (GUILayout.Button("停止并还原"))
                    StopPlayModePreview(director);
            }

            EditorGUILayout.EndHorizontal();
        }

        if (_timelineDirector.objectReferenceValue == null)
            EditorGUILayout.HelpBox("请指定 Timeline Director（PlayableDirector）。", MessageType.Warning);

        serializedObject.ApplyModifiedProperties();
    }

    void StopPlayModePreview(PlayableDirector director)
    {
        if (director == null)
            return;

        director.Stop();
        director.playableAsset = _savedPlayableAssetBeforePreview;
        director.RebuildGraph();
        _savedPlayableAssetBeforePreview = null;
        _previewSessionActive = false;
    }
}
#endif
