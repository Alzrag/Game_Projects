using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class Blaster : MonoBehaviour
{
    [System.Serializable]
    public struct AimData
    {
        public bool canAim;
        public float zoomFactor;
        public bool isScopable;
        public Sprite scopeUIEffect;
    }

    [System.Serializable]
    public struct SpoolData
    {
        public bool isSpoolable;
        public float spoolTime;
    }

    [Header("Weapon Core Stats")]
    [Tooltip("Number of shots per second.")]
    public float fireRate = 5f;

    [Tooltip("Can the attack button be held down for continuous fire?")]
    public bool isHoldable = true;

    [Header("Burst Settings")]
    [Tooltip("Does the weapon fire in bursts?")]
    public bool isBurstable = false;

    [Tooltip("Number of projectiles or volleys fired per burst.")]
    public int volleysPerBurst = 3;

    [Tooltip("Time delay between individual shots within a burst.")]
    public float burstDelay = 0.08f;

    [Header("Spool and Aim")]
    public SpoolData spoolSettings;
    public AimData aimSettings;

    [Header("Combat Stats")]
    public float damage = 10f;
    public float spreadX = 0.1f;
    public float spreadY = 0.1f;

    [Tooltip("Magazine size. If greater than 100, ammunition is infinite and does not count down.")]
    public int magazineSize = 30;

    public Sprite reticleSprite;

    [Header("Projectiles and Spawns")]
    [Tooltip("List of points where projectiles spawn. Fires from all points at once.")]
    public List<Transform> firePoints = new List<Transform>();

    [Tooltip("Number of projectiles spawned per shot per fire point.")]
    public int projectilesPerShot = 1;

    [Tooltip("Prefab for the projectile. Must have a movement component that reads speed and damage.")]
    public GameObject bulletPrefab;

    [Tooltip("Speed inherited by the bullet prefab.")]
    public float bulletSpeed = 50f;

    [Tooltip("Extra local rotation applied to spawned projectiles. Use this if your bolt mesh's long axis doesn't line up with its forward direction (e.g. 90 on X if the mesh points 'up' instead of 'forward').")]
    public Vector3 projectileRotationOffset = new Vector3(90f, 0f, 0f);

    [Header("Input Tuning")]
    [Tooltip("Analog trigger value above which a gamepad trigger counts as 'pressed'.")]
    [Range(0.05f, 0.95f)]
    public float triggerThreshold = 0.1f;

    // Private runtime variables
    private int currentAmmo;
    private float nextFireTime;
    private float currentSpoolTime;
    private bool isSpooledUp;
    private bool isBursting;

    private void Start()
    {
        currentAmmo = magazineSize;
    }

    private void Update()
    {
        HandleSpooling();
        HandleFiringInput();
        HandleReloadInput();
    }

    private void HandleSpooling()
    {
        if (!spoolSettings.isSpoolable) return;

        // Spool input: Right Mouse Button or Right Shoulder/Bumper
        bool spoolInput = false;

        if (Mouse.current != null)
        {
            spoolInput |= Mouse.current.rightButton.isPressed;
        }

        if (Gamepad.current != null)
        {
            spoolInput |= Gamepad.current.rightShoulder.isPressed;
        }

        if (spoolInput)
        {
            if (currentSpoolTime < spoolSettings.spoolTime)
            {
                currentSpoolTime += Time.deltaTime;
                if (currentSpoolTime >= spoolSettings.spoolTime)
                {
                    isSpooledUp = true;
                }
            }
        }
        else
        {
            if (currentSpoolTime > 0f)
            {
                currentSpoolTime -= Time.deltaTime;
            }
            isSpooledUp = false;
        }
    }

    private void HandleFiringInput()
    {
        bool triggerPressed = false; // "just pressed this frame" - for burst trigger
        bool triggerPulled = false;  // "currently held" - for holdable fire

        if (Mouse.current != null)
        {
            triggerPressed |= Mouse.current.leftButton.wasPressedThisFrame;
            triggerPulled |= Mouse.current.leftButton.isPressed;
        }

        if (Gamepad.current != null)
        {
            // Right trigger doubles as the analog "Fire1" axis and the fire button.
            float rightTriggerValue = Gamepad.current.rightTrigger.ReadValue();
            triggerPressed |= Gamepad.current.rightTrigger.wasPressedThisFrame;
            triggerPulled |= rightTriggerValue > triggerThreshold;
        }

        if (spoolSettings.isSpoolable && !isSpooledUp) return;

        if (isBurstable)
        {
            // Burst triggers on initial press
            if (triggerPressed && !isBursting && Time.time >= nextFireTime)
            {
                StartCoroutine(ExecuteBurst());
            }
        }
        else
        {
            bool attackInput = isHoldable ? triggerPulled : triggerPressed;

            if (attackInput && Time.time >= nextFireTime)
            {
                TryFireSingleShot();
            }
        }
    }

    private void HandleReloadInput()
    {
        bool reloadPressed = false;

        if (Keyboard.current != null)
        {
            reloadPressed |= Keyboard.current.rKey.wasPressedThisFrame;
        }

        if (Gamepad.current != null)
        {
            // X button on Xbox layout / Square on PlayStation layout
            reloadPressed |= Gamepad.current.buttonWest.wasPressedThisFrame;
        }

        if (reloadPressed)
        {
            Reload();
        }
    }

    private IEnumerator ExecuteBurst()
    {
        isBursting = true;

        for (int i = 0; i < volleysPerBurst; i++)
        {
            bool isInfiniteAmmo = magazineSize > 100;

            if (!isInfiniteAmmo && currentAmmo <= 0)
            {
                Reload();
                break;
            }

            Shoot();

            if (!isInfiniteAmmo)
            {
                currentAmmo--;
            }

            if (i < volleysPerBurst - 1)
            {
                yield return new WaitForSeconds(burstDelay);
            }
        }

        nextFireTime = Time.time + (1f / fireRate);
        isBursting = false;
    }

    private void TryFireSingleShot()
    {
        bool isInfiniteAmmo = magazineSize > 100;

        if (!isInfiniteAmmo && currentAmmo <= 0)
        {
            Reload();
            return;
        }

        Shoot();

        if (!isInfiniteAmmo)
        {
            currentAmmo--;
        }

        nextFireTime = Time.time + (1f / fireRate);
    }

    private void Shoot()
    {
        if (firePoints.Count == 0 || bulletPrefab == null) return;

        foreach (Transform firePoint in firePoints)
        {
            for (int i = 0; i < projectilesPerShot; i++)
            {
                float randomSpreadX = Random.Range(-spreadX, spreadX);
                float randomSpreadY = Random.Range(-spreadY, spreadY);

                // This is the rotation actually used for aiming/movement — no mesh offset applied.
                Quaternion aimRotation = firePoint.rotation * Quaternion.Euler(randomSpreadX, randomSpreadY, 0f);

                // This is what the spawned object is rotated to, so the mesh looks right.
                Quaternion visualRotation = aimRotation * Quaternion.Euler(projectileRotationOffset);

                GameObject bulletInstance = Instantiate(bulletPrefab, firePoint.position, visualRotation);

                Lazers bolt = bulletInstance.GetComponent<Lazers>();
                if (bolt != null)
                {
                    bolt.Initialize(bulletSpeed, damage, aimRotation * Vector3.forward);
                }
                else
                {
                    Debug.LogWarning("bulletPrefab has no Lazers component attached — it will spawn but not move.", bulletInstance);
                }
            }
        }
    }

    private void Reload()
    {
        bool isInfiniteAmmo = magazineSize > 100;
        if (isInfiniteAmmo) return;

        currentAmmo = magazineSize;
    }
}