using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PortalRenderController : MonoBehaviour
{
    [SerializeField]
    private PortalRenderFeature.Settings settings = new PortalRenderFeature.Settings();

    private PortalRenderFeature portalFeature;
    private PortalPass portalPass;

    [ExecuteAlways]
    private void Awake()
    {
        InitializePortalFeature();
    }

    private void InitializePortalFeature()
    {
        if (portalPass != null)
        {
            return;
        }

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

        portalFeature.settings = settings;

        if (portalFeature.pass == null)
        {
            portalFeature.Create();
        }

        portalPass = portalFeature.pass;
        if (portalPass != null)
        {
            portalPass.settings = settings;
        }
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
