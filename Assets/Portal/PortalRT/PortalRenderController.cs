using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[ExecuteAlways]
public class PortalRenderController : MonoBehaviour
{
    public PortalRenderFeature.Settings settings = new PortalRenderFeature.Settings();

    private PortalRenderFeature portalFeature;
    private PortalPass portalPass;

    private void Awake()
    {
        InitializePortalFeature();
    }

    private void OnEnable()
    {
        InitializePortalFeature();
    }

    private void OnDisable()
    {
        UnregisterPortalFeature();
    }

    private void OnDestroy()
    {
        UnregisterPortalFeature();
    }

    public void InitializePortalFeature()
    {
        // No early-exit on portalPass: PortalRenderFeature.settings is shared across all cameras
        // that use the same renderer asset, so external paths (e.g. PortalRenderControllerBootstrap
        // after an additive scene load) can replace it with a default-constructed instance. We must
        // be able to re-apply our serialized settings on every Awake/OnEnable/scene-event tick.

        Camera camera = GetComponent<Camera>();
        if (camera == null)
        {
            return;
        }

        UniversalAdditionalCameraData additionalCameraData = camera.GetUniversalAdditionalCameraData();
        if (additionalCameraData == null)
        {
            return;
        }

        ScriptableRenderer renderer = additionalCameraData.scriptableRenderer;
        if (renderer == null)
        {
            return;
        }

        portalFeature = FindPortalRenderFeature(renderer);
        if (portalFeature == null)
        {
            return;
        }

        if (portalFeature.pass == null)
        {
            portalFeature.Create();
        }

        portalFeature.ApplySettings(settings);
        portalPass = portalFeature.pass;
    }

    private void UnregisterPortalFeature()
    {
        if (portalFeature != null && ReferenceEquals(portalFeature.settings, settings))
        {
            portalFeature.ApplySettings(new PortalRenderFeature.Settings());
        }

        if (portalPass != null && ReferenceEquals(portalPass.settings, settings))
        {
            portalPass.settings = portalFeature != null ? portalFeature.settings : null;
        }

        portalFeature = null;
        portalPass = null;
    }

    public static void ClearPortalFeature(Camera camera)
    {
        if (camera == null)
        {
            return;
        }

        UniversalAdditionalCameraData additionalCameraData = camera.GetUniversalAdditionalCameraData();
        if (additionalCameraData == null)
        {
            return;
        }

        PortalRenderFeature feature = FindPortalRenderFeature(additionalCameraData.scriptableRenderer);
        if (feature == null)
        {
            return;
        }

        feature.ApplySettings(new PortalRenderFeature.Settings());
    }

    private static PortalRenderFeature FindPortalRenderFeature(ScriptableRenderer renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        foreach (ScriptableRendererFeature feature in GetRendererFeatures(renderer))
        {
            if (feature is PortalRenderFeature portalFeature)
            {
                return portalFeature;
            }
        }

        return null;
    }

    private static IEnumerable<ScriptableRendererFeature> GetRendererFeatures(ScriptableRenderer renderer)
    {
        if (renderer == null)
        {
            yield break;
        }

        const BindingFlags bindingFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        object rawFeatures = null;
        PropertyInfo property = renderer.GetType().GetProperty("rendererFeatures", bindingFlags);

        if (property != null)
        {
            rawFeatures = property.GetValue(renderer);
        }
        else
        {
            FieldInfo field = renderer.GetType().GetField("m_RendererFeatures", bindingFlags) ?? renderer.GetType().GetField("rendererFeatures", bindingFlags);
            if (field != null)
            {
                rawFeatures = field.GetValue(renderer);
            }
        }

        if (rawFeatures is IEnumerable<ScriptableRendererFeature> typedFeatures)
        {
            foreach (ScriptableRendererFeature feature in typedFeatures)
            {
                yield return feature;
            }

            yield break;
        }

        if (rawFeatures is IEnumerable enumerable)
        {
            foreach (object item in enumerable)
            {
                if (item is ScriptableRendererFeature feature)
                {
                    yield return feature;
                }
            }
        }
    }
}
