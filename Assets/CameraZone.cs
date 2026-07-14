using UnityEngine;

[RequireComponent(typeof(Collider2D))]
public class CameraZone : MonoBehaviour
{
    [SerializeField] private Transform player;
    [SerializeField] private Transform cameraPos;

    private Transform cameraTarget;
    private Collider2D zoneCollider;

    private bool playerInside = false;

    private void Awake()
    {
        cameraTarget = transform.GetChild(0);
        zoneCollider = GetComponent<Collider2D>();

        player = GameObject.FindGameObjectWithTag("Player").transform;
    }

    private void Update()
    {
        if (player == null)
            return;

        bool inside = zoneCollider.OverlapPoint(player.position);

        // 玩家中心第一次进入
        if (inside && !playerInside)
        {
            playerInside = true;
            Debug.Log("进入Camera Zone");

            cameraPos.position = cameraTarget.position;
        }
        // 玩家中心离开
        else if (!inside && playerInside)
        {
            playerInside = false;
            Debug.Log("离开Camera Zone");
        }
    }
}