using UnityEngine;
using UnityEngine.SceneManagement;

public static class PortalRenderControllerBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Initialize()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        EnsureController();
    }

    private static void OnSceneLoaded(Scene _scene, LoadSceneMode _mode)
    {
        EnsureController();
    }

    private static void OnSceneUnloaded(Scene _scene)
    {
        EnsureController();
    }

    private static void OnActiveSceneChanged(Scene _previous, Scene _next)
    {
        EnsureController();
    }

    private static void EnsureController()
    {
        // We intentionally do NOT use Camera.main here. During an additive scene transition the
        // outgoing content scene and the incoming content scene can both be loaded for one frame,
        // each carrying its own MainCamera-tagged camera. Camera.main may return the outgoing
        // camera (which has no PortalRenderController), causing ClearPortalFeature to wipe the
        // shared PortalRenderFeature.settings the new controller just set up in its Awake/OnEnable.
        // Instead, query every loaded scene for an active controller and let it own the feature.
        PortalRenderController controller = FindActiveController();
        if (controller != null)
        {
            controller.InitializePortalFeature();
            return;
        }

        Camera fallback = Camera.main;
        if (fallback != null)
        {
            PortalRenderController.ClearPortalFeature(fallback);
        }
    }

    private static PortalRenderController FindActiveController()
    {
#if UNITY_2022_2_OR_NEWER
        PortalRenderController[] controllers = Object.FindObjectsByType<PortalRenderController>(FindObjectsSortMode.None);
#else
        PortalRenderController[] controllers = Object.FindObjectsOfType<PortalRenderController>();
#endif
        for (int i = 0; i < controllers.Length; i++)
        {
            PortalRenderController controller = controllers[i];
            if (controller != null && controller.isActiveAndEnabled)
            {
                return controller;
            }
        }

        return null;
    }
}
