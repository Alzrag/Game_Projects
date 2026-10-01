using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMeleeManager : MonoBehaviour
{
    [Header("Combat Settings")]
    public float damageAmount = 25f;
    public float attackCooldown = 1.0f;
    public string attackAnimationName = "AttackState";
    public string speedParameterName = "AnimSpeed";
    public LayerMask targetLayer;

    [Header("Component References")]
    public Animator animator;
    [HideInInspector] public Collider weaponCollider;

    private HashSet<GameObject> hitTargets = new HashSet<GameObject>();
    private bool isAttacking = false;
    private float lastAttackTime = -999f;

    private void Awake()
    {
        // 1. Automatically find Animator if not assigned
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        // 2. Recursively search down the child tree for the WeaponHitboxProxy and its Collider
        WeaponHitboxProxy proxy = GetComponentInChildren<WeaponHitboxProxy>();
        if (proxy != null)
        {
            weaponCollider = proxy.GetComponent<Collider>();
            // Automatically assign this manager to the proxy so it doesn't need manual setup
            proxy.playerManager = this;
        }
        else
        {
            Debug.LogWarning("PlayerMeleeManager: Could not find any WeaponHitboxProxy in child objects!", this);
        }
    }

    private void Start()
    {
        DisableWeaponCollider();
    }

    private void Update()
    {
        HandleInput();
    }

    private void HandleInput()
    {
        bool keyboardPressed = Keyboard.current != null && Keyboard.current.fKey.wasPressedThisFrame;
        bool gamepadPressed = Gamepad.current != null && Gamepad.current.rightStickButton.wasPressedThisFrame;

        if (keyboardPressed || gamepadPressed)
        {
            if (Time.time >= lastAttackTime + attackCooldown)
            {
                BeginAttack();
            }
        }
    }

    public void BeginAttack()
    {
        isAttacking = true;
        lastAttackTime = Time.time;
        hitTargets.Clear();
        EnableWeaponCollider();

        if (animator != null)
        {
            animator.SetFloat(speedParameterName, 1f);
            animator.Play(attackAnimationName, 0, 0f);
        }
    }

    public void EndAttack()
    {
        isAttacking = false;
        hitTargets.Clear();
        DisableWeaponCollider();

        if (animator != null)
        {
            animator.SetFloat(speedParameterName, 1f);
        }
    }

    public void TriggerParry()
    {
        if (!isAttacking) return;

        isAttacking = false;
        DisableWeaponCollider();

        if (animator != null)
        {
            animator.SetFloat(speedParameterName, -2f);
        }
    }

    public void HandleWeaponHit(Collider other)
    {
        if (!isAttacking) return;

        if ((targetLayer.value & (1 << other.gameObject.layer)) != 0)
        {
            GameObject recipient = other.gameObject;

            if (!hitTargets.Contains(recipient))
            {
                hitTargets.Add(recipient);
                ApplyDamage(recipient);
            }
        }
    }

    private void EnableWeaponCollider()
    {
        if (weaponCollider != null)
            weaponCollider.enabled = true;
    }

    private void DisableWeaponCollider()
    {
        if (weaponCollider != null)
            weaponCollider.enabled = false;
    }

    private void ApplyDamage(GameObject recipient)
    {
        Debug.Log("Dealt " + damageAmount + " damage to " + recipient.name);
    }
}