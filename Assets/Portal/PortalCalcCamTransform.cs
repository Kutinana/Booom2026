using UnityEngine;

public class PortalCalcCamTransform : MonoBehaviour
{
    /// <summary>
    /// Calculates a camera transform that maps the view through innerPortal to outerPortal.
    /// newCamera = outerTransform * inverse(innerTransform) * originalCamera.
    /// </summary>
    public static (Vector3 position, Quaternion rotation) CalculateNewCameraTransform(Transform outerPortal, Transform innerPortal, Camera originalCamera)
    {
        Matrix4x4 newCamMatrix = CalculateNewCameraWorldMatrix(outerPortal, innerPortal, originalCamera.transform.localToWorldMatrix);
        return ExtractPositionAndRotation(newCamMatrix);
    }

    public static Matrix4x4 CalculateNewCameraWorldMatrix(Transform outerPortal, Transform innerPortal, Matrix4x4 cameraWorldMatrix)
    {
        Matrix4x4 innerToOuter = outerPortal.localToWorldMatrix * innerPortal.worldToLocalMatrix;
        return innerToOuter * cameraWorldMatrix;
    }

    /// <summary>
    /// Calculates the world-space matrix that advances one portal-rendering layer.
    /// If currentLayerCamera is the camera for layer 1, this returns the camera matrix for layer 2.
    /// nextLayerCamera = innerTransform * inverse(outerTransform) * currentLayerCamera.
    /// </summary>
    public static Matrix4x4 CalculateNextLayerCameraWorldMatrix(Transform outerPortal, Transform innerPortal, Matrix4x4 currentLayerCameraWorldMatrix)
    {
        Matrix4x4 currentToNextLayer = CalculateCurrentToNextLayerMatrix(outerPortal, innerPortal);
        return currentToNextLayer * currentLayerCameraWorldMatrix;
    }

    /// <summary>
    /// Calculates the camera transform for the next nested portal layer.
    /// Use this when currentLayerCamera is already shooting layer 1 and you need the layer 2 pose.
    /// </summary>
    public static (Vector3 position, Quaternion rotation) CalculateNextLayerCameraTransform(Transform outerPortal, Transform innerPortal, Camera currentLayerCamera)
    {
        Matrix4x4 nextLayerCameraMatrix = CalculateNextLayerCameraWorldMatrix(outerPortal, innerPortal, currentLayerCamera.transform.localToWorldMatrix);
        return ExtractPositionAndRotation(nextLayerCameraMatrix);
    }

    /// <summary>
    /// Returns the transform matrix that maps any current portal-rendering layer into the next one.
    /// </summary>
    public static Matrix4x4 CalculateCurrentToNextLayerMatrix(Transform outerPortal, Transform innerPortal)
    {
        return innerPortal.localToWorldMatrix * outerPortal.worldToLocalMatrix;
    }

    private static (Vector3 position, Quaternion rotation) ExtractPositionAndRotation(Matrix4x4 matrix)
    {
        Vector3 position = matrix.GetColumn(3);
        Vector3 forward = matrix.GetColumn(2);
        Vector3 up = matrix.GetColumn(1);
        Quaternion rotation = Quaternion.LookRotation(forward, up);

        return (position, rotation);
    }

    public Camera sourceCamera;
    public Camera testCamera;
    public Transform testOuterPortal;
    public Transform testInnerPortal;

    [ContextMenu("Test CalculateNewCameraTransform")]
    public void Test()
    {
        var (newPos, newRot) = CalculateNewCameraTransform(testOuterPortal, testInnerPortal, sourceCamera);
        testCamera.transform.SetPositionAndRotation(newPos, newRot);
    }
}
