using UnityEngine;

public class WeaponHitboxProxy : MonoBehaviour
{
    [Header("References")]
    public PlayerMeleeManager playerManager;

    private void Awake()
    {
        // Automatically climb up the parent-child tree to find the PlayerMeleeManager at the root if missing
        if (playerManager == null)
        {
            Transform currentParent = transform.parent;
            while (currentParent != null)
            {
                playerManager = currentParent.GetComponent<PlayerMeleeManager>();
                if (playerManager != null) break;
                currentParent = currentParent.parent;
            }
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (playerManager != null)
        {
            playerManager.HandleWeaponHit(other);
        }
    }
}