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

        Vector3 position = newCamMatrix.GetColumn(3);
        Vector3 forward = newCamMatrix.GetColumn(2);
        Vector3 up = newCamMatrix.GetColumn(1);
        Quaternion rotation = Quaternion.LookRotation(forward, up);

        return (position, rotation);
    }

    public static Matrix4x4 CalculateNewCameraWorldMatrix(Transform outerPortal, Transform innerPortal, Matrix4x4 cameraWorldMatrix)
    {
        Matrix4x4 innerToOuter = outerPortal.localToWorldMatrix * innerPortal.worldToLocalMatrix;
        return innerToOuter * cameraWorldMatrix;
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
