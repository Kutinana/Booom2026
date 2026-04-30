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
    private static readonly int StencilCompId = Shader.PropertyToID("_StencilComp");
    private static readonly int StencilReadMaskId = Shader.PropertyToID("_StencilReadMask");
    private static readonly int StencilPassId = Shader.PropertyToID("_StencilPass");

    public Settings settings;
    private Material depthClearMaterial;

    public PortalPass(Settings settings)
    {
        this.settings = settings;
    }

    private void DrawSceneWithStencil(
        ScriptableRenderContext context,
        CullingResults cullResults,
        Camera camera,
        int stencilReference)
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
            stencilReference = stencilReference,
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

    private static Matrix4x4 GetCameraViewMatrix(Matrix4x4 cameraWorldMatrix)
    {
        return Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * cameraWorldMatrix.inverse;
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

        Plane[] cullingPlanes = GeometryUtility.CalculateFrustumPlanes(cullingMatrix);
        cullingParams.cullingPlaneCount = cullingPlanes.Length;
        for (int i = 0; i < cullingPlanes.Length; i++)
        {
            cullingParams.SetCullingPlane(i, cullingPlanes[i]);
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

    public void Dispose()
    {
        if (depthClearMaterial == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Object.Destroy(depthClearMaterial);
        }
        else
        {
            Object.DestroyImmediate(depthClearMaterial);
        }

        depthClearMaterial = null;
    }

    public override void Execute(
        ScriptableRenderContext context,
        ref RenderingData renderingData)
    {
        if (settings == null)
        {
            return;
        }

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
            Matrix4x4 proj = cam.projectionMatrix;
            Matrix4x4 cameraWorld = cam.transform.localToWorldMatrix;
            Matrix4x4 portalStep = settings.outerTransform.localToWorldMatrix * settings.transform.worldToLocalMatrix;
            int maxDepth = settings.maxDepth < 1 ? 2 : settings.maxDepth;

            if (!IsFinite(cameraWorld) || !IsFinite(portalStep) || !IsFinite(proj))
            {
                return;
            }

            for (int depth = 1; depth <= maxDepth; depth++)
            {
                cmd.SetGlobalInt(StencilRefId, depth == 1 ? 1 : depth - 1);
                cmd.SetGlobalInt(StencilReadMaskId, 255);
                cmd.SetGlobalInt(StencilCompId, depth == 1 ? (int)CompareFunction.Always : (int)CompareFunction.Equal);
                cmd.SetGlobalInt(StencilPassId, depth == 1 ? (int)StencilOp.Replace : (int)StencilOp.IncrementSaturate);
                cmd.DrawMesh(
                    settings.mesh,
                    portalMatrix,
                    settings.material,
                    0,
                    0
                );
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                cmd.SetGlobalInt(StencilRefId, depth);
                cmd.DrawMesh(
                    settings.mesh,
                    portalMatrix,
                    depthClearMaterial,
                    0,
                    0
                );
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                cameraWorld = portalStep * cameraWorld;
                Vector3 portalCameraPosition = cameraWorld.GetColumn(3);
                Matrix4x4 view = GetCameraViewMatrix(cameraWorld);

                if (!IsFinite(cameraWorld) || !IsFinite(view))
                {
                    return;
                }

                if (!TryCullPortalView(context, cam, view, proj, portalCameraPosition, out CullingResults portalCullResults))
                {
                    return;
                }

                cmd.SetViewProjectionMatrices(view, proj);
                cmd.SetGlobalVector("_WorldSpaceCameraPos", portalCameraPosition);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                DrawSceneWithStencil(
                    context,
                    portalCullResults,
                    cam,
                    depth
                );
            }

            cmd.SetViewProjectionMatrices(cam.worldToCameraMatrix, proj);
            cmd.SetGlobalVector("_WorldSpaceCameraPos", cam.transform.position);
            cmd.SetGlobalInt(StencilCompId, (int)CompareFunction.Always);
            cmd.SetGlobalInt(StencilReadMaskId, 255);
            cmd.SetGlobalInt(StencilPassId, (int)StencilOp.Replace);
            context.ExecuteCommandBuffer(cmd);
        }
        finally
        {
            CommandBufferPool.Release(cmd);
        }
    }
}
