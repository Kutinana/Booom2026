using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public partial class CameraHardFollow : MonoBehaviour
{
    public Transform target;
    private Vector3 offset;
    // Start is called before the first frame update
    void Start()
    {
        offset = transform.position - target.position;
        var prc = GetComponent<PortalRenderController>();
        inner = prc.settings.transform;
        outer = prc.settings.outerTransform;
    }

    // Update is called once per frame
    void Update()
    {
        // transform.position = target.position + offset;
    }
}
