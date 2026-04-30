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
        public Transform transform; // 放置这个 mesh 的位置
        public Material portalColorMaterial; // 用于绘制 portal 内部的材质
    }

    public Settings settings = new Settings();

    public PortalPass pass;

    public override void Create()
    {
        pass = new PortalPass(settings);

        // 插在不透明物体之后（方便看见）
        pass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public override void AddRenderPasses(
        ScriptableRenderer renderer,
        ref RenderingData renderingData)
    {
        renderer.EnqueuePass(pass);
    }
}
