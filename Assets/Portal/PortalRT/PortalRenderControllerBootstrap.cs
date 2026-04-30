using UnityEngine;
using UnityEngine.SceneManagement;

public static class PortalRenderControllerBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Initialize()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        EnsureMainCameraController();
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsureMainCameraController();
    }

    private static void EnsureMainCameraController()
    {
        Camera mainCamera = Camera.main;
        if (mainCamera == null)
        {
            return;
        }

        PortalRenderController controller = mainCamera.GetComponent<PortalRenderController>();
        if (controller == null)
        {
            PortalRenderController.ClearPortalFeature(mainCamera);
            return;
        }

        if (!controller.isActiveAndEnabled)
        {
            PortalRenderController.ClearPortalFeature(mainCamera);
            return;
        }

        controller.InitializePortalFeature();
    }
}
