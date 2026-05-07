using UnityEngine;

public class PlayerService : ServiceBase
{
    public PlayerController Player { get; private set; }
    public bool HasPlayer => Player != null;

    public void Register(PlayerController player)
    {
        if (player == null)
        {
            return;
        }

        if (Player != null && Player != player)
        {
            Debug.LogWarning($"Replacing registered player '{Player.name}' with '{player.name}'.", player);
        }

        Player = player;
    }

    public void UnRegister(PlayerController player)
    {
        if (Player == player)
        {
            Player = null;
        }
    }
}
