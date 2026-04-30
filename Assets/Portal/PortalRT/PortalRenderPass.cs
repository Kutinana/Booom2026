using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static PortalRenderFeature;

public class PortalPass : ScriptableRenderPass
{
    public Settings settings;

    public PortalPass(Settings settings)
    {
        this.settings = settings;
    }

    void DrawSceneWithStencil(
    ScriptableRenderContext context,
    ref RenderingData data,
    CommandBuffer cmd,
    Material overrideMaterial)
    {
        var sortingSettings = new SortingSettings(data.cameraData.camera)
        {
            criteria = SortingCriteria.CommonOpaque
        };

        var drawingSettings = new DrawingSettings(
            new ShaderTagId("UniversalForward"),
            sortingSettings)
        {
            overrideMaterial = overrideMaterial,
            overrideMaterialPassIndex = 0
        };

        var filteringSettings = new FilteringSettings(RenderQueueRange.opaque);

        context.ExecuteCommandBuffer(cmd);
        cmd.Clear();

        context.DrawRenderers(
            data.cullResults,
            ref drawingSettings,
            ref filteringSettings
        );
    }


    public override void Execute(
        ScriptableRenderContext context,
        ref RenderingData renderingData)
    {
        if (settings.mesh == null)
        {
            return;
        }
        if (settings.material == null || settings.transform == null)
        {
            Debug.LogWarning("Portal settings are not properly configured.");
            return;
        }

        CommandBuffer cmd = CommandBufferPool.Get("SimplePortalPass");

        // 使用 transform 的矩阵
        Matrix4x4 matrix = settings.transform.localToWorldMatrix;

        cmd.SetGlobalInt("_StencilRef", 1);
        cmd.DrawMesh(
            settings.mesh,
            matrix,
            settings.material,
            0,
            0 // pass index
        );
        context.ExecuteCommandBuffer(cmd);
        cmd.Clear();

        DrawSceneWithStencil(
            context,
            ref renderingData,
            cmd,
            settings.portalColorMaterial // 👈 刚才那个青色材质
        );

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }
}