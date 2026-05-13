using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PortalRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public Mesh mesh;
        public Material material;
        public Transform transform;
        public Transform outerTransform;
        [Min(1)]
        public int maxDepth = 2;
    }

    [System.NonSerialized]
    public Settings settings = new Settings();

    public PortalPass pass;
    private Portal2DPass pass2D;

    public override void Create()
    {
        pass = new PortalPass(settings);
        pass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;

        pass2D = new Portal2DPass(settings);
        pass2D.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
    }

    public void ApplySettings(Settings newSettings)
    {
        settings = newSettings ?? new Settings();

        if (pass != null)
        {
            pass.settings = settings;
        }

        if (pass2D != null)
        {
            pass2D.settings = settings;
        }
    }

    protected override void Dispose(bool disposing)
    {
        pass?.Dispose();
        pass2D?.Dispose();
        pass = null;
        pass2D = null;
    }

    public override void AddRenderPasses(
        ScriptableRenderer renderer,
        ref RenderingData renderingData)
    {
        if (pass == null || pass2D == null)
        {
            Create();
        }

        ApplySettings(settings);

        if (IsRenderer2D(renderer))
        {
            renderer.EnqueuePass(pass2D);
        }
        else
        {
            renderer.EnqueuePass(pass);
        }
    }

    private static bool IsRenderer2D(ScriptableRenderer renderer)
    {
        return renderer != null && renderer.GetType().Name.Contains("2D");
    }
}
