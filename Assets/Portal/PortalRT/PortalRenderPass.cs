using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static PortalRenderFeature;

public class PortalPass : ScriptableRenderPass
{
    private static readonly ShaderTagId UniversalForwardTag = new ShaderTagId("UniversalForward");
    private static readonly ShaderTagId UniversalForwardOnlyTag = new ShaderTagId("UniversalForwardOnly");
    private static readonly ShaderTagId SRPDefaultUnlitTag = new ShaderTagId("SRPDefaultUnlit");
    private static readonly int StencilRefId = Shader.PropertyToID("_StencilRef");

    public Settings settings;
    private Material depthClearMaterial;

    public PortalPass(Settings settings)
    {
        this.settings = settings;
    }

    private void DrawSceneWithStencil(
        ScriptableRenderContext context,
        CullingResults cullResults,
        Camera camera)
    {
        var sortingSettings = new SortingSettings(camera)
        {
            criteria = SortingCriteria.CommonOpaque
        };

        var drawingSettings = new DrawingSettings(UniversalForwardTag, sortingSettings);
        drawingSettings.SetShaderPassName(1, UniversalForwardOnlyTag);
        drawingSettings.SetShaderPassName(2, SRPDefaultUnlitTag);

        var filteringSettings = new FilteringSettings(RenderQueueRange.opaque);

        var stencilState = new StencilState(
            true,
            255,
            255,
            CompareFunction.Equal,
            StencilOp.Keep,
            StencilOp.Keep,
            StencilOp.Keep
        );

        var stateBlock = new RenderStateBlock(RenderStateMask.Stencil | RenderStateMask.Depth)
        {
            depthState = new DepthState(true, CompareFunction.LessEqual),
            stencilReference = 1,
            stencilState = stencilState
        };

        context.DrawRenderers(
            cullResults,
            ref drawingSettings,
            ref filteringSettings,
            ref stateBlock
        );
    }

    private static bool IsFinite(Matrix4x4 matrix)
    {
        for (int i = 0; i < 16; i++)
        {
            float value = matrix[i];
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCullPortalView(
        ScriptableRenderContext context,
        Camera camera,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 origin,
        out CullingResults cullingResults)
    {
        cullingResults = default;

        if (!camera.TryGetCullingParameters(out ScriptableCullingParameters cullingParams))
        {
            return false;
        }

        Matrix4x4 cullingMatrix = projection * view;
        if (!IsFinite(cullingMatrix))
        {
            return false;
        }

        cullingParams.cullingMatrix = cullingMatrix;
        cullingParams.origin = origin;
        cullingResults = context.Cull(ref cullingParams);
        return true;
    }

    private bool EnsureDepthClearMaterial()
    {
        if (depthClearMaterial != null)
        {
            return true;
        }

        Shader shader = Shader.Find("Hidden/PortalDepthClear");
        if (shader == null)
        {
            Debug.LogWarning("Missing Hidden/PortalDepthClear shader.");
            return false;
        }

        depthClearMaterial = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        return true;
    }

    public override void Execute(
        ScriptableRenderContext context,
        ref RenderingData renderingData)
    {
        if (settings.mesh == null)
        {
            return;
        }

        if (settings.material == null || settings.transform == null || settings.outerTransform == null)
        {
            Debug.LogWarning("Portal settings are not properly configured.");
            return;
        }

        Camera cam = renderingData.cameraData.camera;
        if (cam == null)
        {
            return;
        }

        if (!EnsureDepthClearMaterial())
        {
            return;
        }

        CommandBuffer cmd = CommandBufferPool.Get("SimplePortalPass");

        try
        {
            Matrix4x4 portalMatrix = settings.transform.localToWorldMatrix;

            cmd.SetGlobalInt(StencilRefId, 1);
            cmd.DrawMesh(
                settings.mesh,
                portalMatrix,
                settings.material,
                0,
                0
            );
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            cmd.DrawMesh(
                settings.mesh,
                portalMatrix,
                depthClearMaterial,
                0,
                0
            );
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            var (pos, rot) = PortalCalcCamTransform.CalculateNewCameraTransform(
                settings.outerTransform,
                settings.transform,
                cam
            );

            Matrix4x4 world = Matrix4x4.TRS(pos, rot, Vector3.one);
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * world.inverse;
            Matrix4x4 proj = cam.projectionMatrix;

            if (!IsFinite(world) || !IsFinite(view) || !IsFinite(proj))
            {
                return;
            }

            if (!TryCullPortalView(context, cam, view, proj, pos, out CullingResults portalCullResults))
            {
                return;
            }

            cmd.SetViewProjectionMatrices(view, proj);
            cmd.SetGlobalVector("_WorldSpaceCameraPos", pos);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            DrawSceneWithStencil(
                context,
                portalCullResults,
                cam
            );

            cmd.SetViewProjectionMatrices(cam.worldToCameraMatrix, proj);
            cmd.SetGlobalVector("_WorldSpaceCameraPos", cam.transform.position);
            context.ExecuteCommandBuffer(cmd);
        }
        finally
        {
            CommandBufferPool.Release(cmd);
        }
    }
}
