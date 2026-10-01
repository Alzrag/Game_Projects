using System;
using System.Collections.Generic;
using UnityEngine;

namespace BF3AI
{
    /// <summary>
    /// Put this on the PLAYER and on every AI unit prefab (health/team/threat flags; also emits the player's footstep noise). Anything that can be shot has one.
    /// Player scripts: set isHealing / usingHeavyWeapon / regenPerSecond so AI threat scoring can react to them.
    /// </summary>
    public class Combatant : MonoBehaviour
    {
        public static readonly List<Combatant> All = new List<Combatant>();
        public static event Action<Combatant, Combatant> AnyDeath; // victim, killer

        public int team;
        public float maxHealth = 100f;
        public float health = 100f;
        public bool isPlayer;

        [Header("Threat flags (drive AI target priority)")]
        public float regenPerSecond;
        public bool isHealing;
        public bool usingHeavyWeapon;

        [Header("Player footsteps (only used when isPlayer)")]
        [Tooltip("Lets AI hear the player move. For gunshots call SoundBus.Emit(pos, 70f, team, SoundType.Gunshot) from your weapon script.")]
        public bool emitFootsteps = true;
        public float walkRadius = 8f, sprintRadius = 20f, sprintSpeed = 5f, footstepInterval = 0.5f;

        [Header("Body")]
        public float standingHeight = 1.8f;
        public float crouchHeight = 1.1f;
        [NonSerialized] public bool crouched;

        Vector3 lastPos; float nextStep;
        [NonSerialized] public Combatant lastAttacker;
        [NonSerialized] public float lastDamageTime = -99f;
        public event Action<Combatant> OnDied;

        public bool IsAlive { get { return health > 0f; } }
        public float CurrentHeight { get { return crouched ? crouchHeight : standingHeight; } }
        public Vector3 Chest { get { return transform.position + Vector3.up * (CurrentHeight * 0.7f); } }
        public Vector3 Eye { get { return transform.position + Vector3.up * (CurrentHeight - 0.15f); } }

        void OnEnable() { All.Add(this); lastPos = transform.position; }
        void OnDisable() { All.Remove(this); }

        void Update()
        {
            if (isPlayer && emitFootsteps) EmitFootsteps();

            if (IsAlive && regenPerSecond > 0f && Time.time - lastDamageTime > 4f)
                health = Mathf.Min(maxHealth, health + regenPerSecond * Time.deltaTime);
        }

        void EmitFootsteps()
        {
            float speed = (transform.position - lastPos).magnitude / Mathf.Max(Time.deltaTime, 0.0001f);
            lastPos = transform.position;
            if (crouched || speed < 1.5f || Time.time < nextStep) return;
            nextStep = Time.time + footstepInterval;
            SoundBus.Emit(transform.position, speed >= sprintSpeed ? sprintRadius : walkRadius, team, SoundType.Footstep);
        }

        // Handy while testing: right-click the Combatant component header in play mode.
        [ContextMenu("BF3AI/Debug: Kill")] void DebugKill() { TakeDamage(health + 1f, null); }
        [ContextMenu("BF3AI/Debug: Full Heal")] void DebugHeal() { health = maxHealth; }

        public void ResetHealth()
        {
            health = maxHealth; lastAttacker = null; lastDamageTime = -99f; crouched = false;
        }

        public void TakeDamage(float amount, Combatant attacker)
        {
            if (!IsAlive) return;
            if (float.IsNaN(amount) || amount < 0f)
            {
                BF3Debug.E(DbgCat.Combat, "Combatant '" + name + "' was asked to take invalid damage (" + amount + ") from '" + (attacker != null ? attacker.name : "null") + "'. Ignored.", this);
                return;
            }
            health -= amount;
            if (BF3Debug.On(DbgLevel.Verbose, DbgCat.Combat))
                BF3Debug.V(DbgCat.Combat, "'" + name + "' takes " + amount.ToString("F0") + " from '" + (attacker != null ? attacker.name : "?") + "' -> hp " + Mathf.Max(0f, health).ToString("F0") + "/" + maxHealth.ToString("F0"), this);
            lastAttacker = attacker;
            lastDamageTime = Time.time;
            if (health <= 0f)
            {
                health = 0f;
                if (OnDied != null) OnDied(attacker);
                if (AnyDeath != null) AnyDeath(this, attacker);
            }
        }
    }
}
