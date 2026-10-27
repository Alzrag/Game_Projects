using System.Collections;
using UnityEngine;
using BF3AI;

/// <summary>
/// Handles the player's death and respawn. Put it on the same object as the player's Combatant.
/// While dead: movement, weapons and melee are switched off (so a dead player cannot keep shooting),
/// then after Respawn Delay the player is teleported to Respawn Point and brought back to full health.
/// Bots stop hunting while you are dead and resume the moment you respawn.
/// </summary>
[RequireComponent(typeof(Combatant))]
public class PlayerDeath : MonoBehaviour
{
    [Tooltip("Where the player reappears. Must be on the NavMesh side of the level (e.g. your PlayerSpawn object).")]
    public Transform respawnPoint;
    public float respawnDelay = 3f;
    [Tooltip("Optional: extra scripts to switch off while dead (animation drivers, UI, etc).")]
    public Behaviour[] alsoDisableWhileDead;

    Combatant c;
    CharacterController cc;
    Vector3 startPos;
    Quaternion startRot;
    float respawnAt = -1f;

    void Awake()
    {
        c = GetComponent<Combatant>();
        cc = GetComponent<CharacterController>();
        startPos = transform.position;
        startRot = transform.rotation;
    }

    void OnEnable() { c.OnDied += Died; }
    void OnDisable() { c.OnDied -= Died; }

    void Died(Combatant killer)
    {
        Debug.Log("[PlayerDeath] Player died" + (killer != null ? ", killed by '" + killer.name + "'" : "") + ". Respawning in " + respawnDelay + "s.");
        SetControl(false);
        respawnAt = Time.time + respawnDelay;
    }

    void Update()
    {
        if (respawnAt > 0f && Time.time >= respawnAt) { respawnAt = -1f; Respawn(); }
    }

    void Respawn()
    {
        Vector3 pos = respawnPoint != null ? respawnPoint.position : startPos;
        Quaternion rot = respawnPoint != null ? respawnPoint.rotation : startRot;
        if (respawnPoint == null)
            Debug.LogWarning("[PlayerDeath] No Respawn Point assigned; respawning at the position the game started from.");

        // The player's origin may be mid-body (see Combatant.Feet): lift the spawn so the feet land on the spawn point.
        pos += Vector3.up * c.feetOffset;

        if (cc != null) cc.enabled = false;           // a CharacterController overrides teleports unless disabled first
        transform.position = pos;
        transform.rotation = rot;
        if (cc != null) cc.enabled = true;

        c.ResetHealth();
        SetControl(true);
        Debug.Log("[PlayerDeath] Player respawned at " + pos.ToString("F1") + ".");
    }

    // Switches the player's controls and weapons on or off.
    void SetControl(bool on)
    {
        PlayerMovement mv = GetComponent<PlayerMovement>();
        if (mv != null) mv.enabled = on;

        PlayerMeleeManager melee = GetComponent<PlayerMeleeManager>();
        if (melee != null) melee.enabled = on;

        // Weapons are spawned at runtime under the hands, so search for them now (including inactive ones).
        Blaster[] blasters = GetComponentsInChildren<Blaster>(true);
        for (int i = 0; i < blasters.Length; i++) blasters[i].enabled = on;

        if (alsoDisableWhileDead != null)
            for (int i = 0; i < alsoDisableWhileDead.Length; i++)
                if (alsoDisableWhileDead[i] != null) alsoDisableWhileDead[i].enabled = on;
    }

    void OnGUI()
    {
        GUI.Label(new Rect(10, Screen.height - 30, 360, 22), c.IsAlive ? "HP " + Mathf.CeilToInt(c.health) : "DEAD - respawning...");
    }
}