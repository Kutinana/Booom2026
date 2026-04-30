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
        public Material portalColorMaterial;
        [Min(1)]
        public int maxDepth = 2;
    }

    public Settings settings = new Settings();

    public PortalPass pass;

    public override void Create()
    {
        pass = new PortalPass(settings);
        pass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public override void AddRenderPasses(
        ScriptableRenderer renderer,
        ref RenderingData renderingData)
    {
        renderer.EnqueuePass(pass);
    }
}
