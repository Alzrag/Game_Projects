using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.Serialization;

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

    [Tooltip("Speed at which the camera transitions into and out of the zoom.")]
    public float zoomTransitionSpeed = 10f;

    [Header("Combat Stats")]
    public float damage = 10f;
    public float spreadX = 0.1f;
    public float spreadY = 0.1f;

    [Tooltip("Magazine size. If greater than 100, ammunition is infinite and does not count down (the HUD then shows infinity).")]
    public int magazineSize = 30;

    // ---- Crosshair aiming
    [Header("Aiming (bolts go where the crosshair points)")]
    [Tooltip("On: a ray from the centre of the camera finds the impact point, and every bolt flies from its fire point to that point, so the reticle is always accurate. Off: bolts fly straight out of the fire points.")]
    public bool aimAtCrosshair = true;
    [Tooltip("At or below this distance (metres, impact point to fire point) bolts fly straight out of the fire point. Beyond it they blend towards the crosshair point over Aim Blend Distance. 0 = always aim at the crosshair point.")]
    public float minAimDistance = 0.25f;
    [Tooltip("Metres over which the bolt direction blends from 'straight out of the fire point' to 'at the crosshair point', starting at Min Aim Distance. 0 = a hard switch.")]
    public float aimBlendDistance = 1f;
    [Tooltip("The most a bolt may be turned away from straight out of the fire point to reach the crosshair point, in degrees. 90 = it may go sideways to hit the point; 180 = no limit (it can even turn round); 0 = never turn. Beyond this the bolt goes straight out.")]
    [Range(0f, 180f)] public float maxAimBendAngle = 90f;
    [Tooltip("Degrees below the max angle over which the turn fades out (so there is no snap at the limit).")]
    [Range(0f, 90f)] public float aimBendFalloff = 20f;
    [Tooltip("If the fire point has been pushed inside a wall or other solid (for example you are standing right up against it) bolts are stopped by it instead of coming out the other side: the shot hits the surface in front of the fire point.")]
    public bool blockShotsThroughWalls = true;
    [Tooltip("Layers that count as solid for the check above. Your own colliders and triggers are always ignored.")]
    public LayerMask muzzleBlockMask = ~0;
    [Tooltip("How far in front of the blocking surface (metres) the stopped bolt appears.")]
    public float muzzleSurfaceOffset = 0.03f;
    [Tooltip("How far the aim ray looks. If it hits nothing, bolts converge on a point this far ahead.")]
    public float aimMaxDistance = 500f;
    [Tooltip("Which layers the aim ray can hit. Your own colliders and triggers are always ignored.")]
    public LayerMask aimRayMask = ~0;

    // ---- Ammo, reload and on-screen ammo counter
    [Header("Reload and Ammo HUD")]
    [Tooltip("Seconds a reload takes. You cannot fire while reloading. Also the reload time bots use (saved to the loadout CSV). 0 = instant.")]
    [FormerlySerializedAs("botReloadTime")]
    public float reloadTime = 2f;
    [Tooltip("Start reloading by itself when the magazine runs dry (R also reloads any time).")]
    public bool autoReloadWhenEmpty = true;
    [Tooltip("Optional. An Animator to play the reload animation on (for example on the weapon model or the arms).")]
    public Animator reloadAnimator;
    [Tooltip("Animator Trigger parameter fired when a reload starts.")]
    public string reloadTrigger = "Reload";
    [Tooltip("Optional Animator Float parameter set so the reload clip lasts exactly Reload Time (value = Reload Animation Length / Reload Time). Leave empty to skip.")]
    public string reloadSpeedParam = "";
    [Tooltip("Length in seconds of your reload animation clip (only used with Reload Speed Param).")]
    public float reloadAnimationLength = 1f;
    [Tooltip("Optional sound played when a reload starts.")]
    public AudioClip reloadSound;
    [Range(0f, 1f)] public float reloadSoundVolume = 1f;
    [Tooltip("Show the ammo counter on screen (only for the weapon currently in hand).")]
    public bool showAmmoOnScreen = true;
    public enum HudCorner { BottomRight, BottomLeft, TopRight, TopLeft }
    public HudCorner ammoHudCorner = HudCorner.BottomRight;
    [Tooltip("Distance from the screen corner, in pixels.")]
    public Vector2 ammoHudMargin = new Vector2(20f, 20f);

    // ---- Scope and reticle UI: assign the sprites, the script builds and centres everything itself (no Canvas or Image objects to make).
    [Header("Scope and Reticle UI (auto-generated: just assign sprites)")]
    [Tooltip("Crosshair / reticle sprite. Drawn dead centre of the screen. Leave empty for none. (Import it as Texture Type: Sprite (2D and UI).)")]
    public Sprite reticleSprite;
    [Tooltip("Also show the reticle when not aiming (hip-fire crosshair). Off = only while aiming.")]
    public bool showReticleWhenNotAiming = true;
    public Color reticleColor = Color.white;
    [Tooltip("1 = the sprite's own pixel size on a 1920x1080 screen; scales with resolution.")]
    public float reticleScale = 1f;
    [Tooltip("Nudge from screen centre, in 1080p pixels (e.g. to match a weapon that fires slightly off-centre).")]
    public Vector2 reticleOffset = Vector2.zero;
    [Tooltip("Scope overlay size. 1 = fits the full screen height.")]
    public float scopeScale = 1f;
    [Tooltip("On: the scope sprite keeps its shape, fits the screen height and black bars fill the rest of the screen (classic sniper scope). Off: the sprite is stretched over the whole screen.")]
    public bool scopeBlackBars = true;
    public Color scopeBarColor = Color.black;
    [Tooltip("How fast the scope fades in and out.")]
    public float scopeFadeSpeed = 14f;
    [Tooltip("Draw order of the generated UI (higher = on top of your other UI).")]
    public int uiSortingOrder = 100;

    [Header("Projectiles and Spawns")]
    [Tooltip("List of points where projectiles spawn. Fires from all points at once.")]
    public List<Transform> firePoints = new List<Transform>();

    [Tooltip("Number of projectiles spawned per shot per fire point.")]
    public int projectilesPerShot = 1;

    [Tooltip("Prefab for the projectile. Must have a movement component that reads speed and damage.")]
    public GameObject bulletPrefab;

    [Tooltip("Speed inherited by the bullet prefab.")]
    public float bulletSpeed = 50f;

    [Tooltip("Extra local rotation applied to spawned projectiles.")]
    public Vector3 projectileRotationOffset = new Vector3(90f, 0f, 0f);

    [Header("Input Tuning")]
    [Tooltip("Analog trigger value above which a gamepad trigger counts as 'pressed'.")]
    [Range(0.05f, 0.95f)]
    public float triggerThreshold = 0.1f;

    [Header("BF3AI bot loadout (used by this component's menu: Save As Bot Loadout)")]
    [Tooltip("Row id in Assets/StreamingAssets/BF3AI_Loadouts.csv. Saving again with the same id replaces that row. Empty = the weapon prefab's name.")]
    public string botLoadoutId = "";
    [Tooltip("Which bot class may be given this weapon. Any = used only by classes that have no weapon of their own.")]
    public BF3AI.LoadoutClass botClass = BF3AI.LoadoutClass.Any;
    [Tooltip("Relative chance of being picked among the rows of its class (0 disables the row).")]
    public float botWeight = 1f;
    [Tooltip("Max effective range for bots, in metres. 0 = the class's normal range.")]
    public float botRange = 0f;

    // Private runtime variables
    private int currentAmmo;
    private bool isReloading;
    private float reloadStartTime;

    public int CurrentAmmo { get { return currentAmmo; } }
    public bool IsReloading { get { return isReloading; } }
    public bool InfiniteAmmo { get { return magazineSize > 100; } }
    public float ReloadProgress { get { return isReloading && reloadTime > 0f ? Mathf.Clamp01((Time.time - reloadStartTime) / reloadTime) : 0f; } }
    private float nextFireTime;
    private float currentSpoolTime;
    private bool isSpooledUp;
    private bool isBursting;
    private bool isAiming;

    // BF3AI: the Combatant (on the player) that owns this weapon; used for team, kill credit and gunshot noise
    private BF3AI.Combatant owner;

    // Camera reference variables for zooming
    private Camera playerCamera;
    private float defaultFOV = 60f;
    private float targetFOV = 60f;

    private void Awake()
    {
        owner = GetComponentInParent<BF3AI.Combatant>();
        currentAmmo = magazineSize;   // set here (not Start) so a weapon that starts hidden, or is swapped away from, keeps its own count
    }

    private void Start()
    {
        BuildScopeUI();

        // Automatically find the main camera for zooming
        playerCamera = Camera.main;
        if (playerCamera != null)
        {
            defaultFOV = playerCamera.fieldOfView;
            targetFOV = defaultFOV;
        }
    }

    private void Update()
    {
        HandleAimingInput();
        UpdateScopeUI();
        HandleSpooling();
        HandleFiringInput();
        HandleReloadInput();
        if (autoReloadWhenEmpty && !InfiniteAmmo && currentAmmo <= 0 && !isReloading && !isBursting) Reload();
        UpdateZoom();
    }

    // =====================================================================================================
    //  Scope + reticle UI (generated at runtime, centred automatically at any resolution or aspect ratio)
    // =====================================================================================================
    private const float RefW = 1920f, RefH = 1080f;
    private GameObject uiRoot;
    private CanvasGroup scopeGroup, reticleGroup;
    private float scopeAlpha, reticleAlpha;

    private static Image MakeImage(string n, Transform parent, Sprite sprite, Color c)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);
        Image im = go.AddComponent<Image>();
        im.sprite = sprite; im.color = c; im.raycastTarget = false;
        return im;
    }

    private static RectTransform Anchor(Graphic g, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        RectTransform rt = g.rectTransform;
        rt.anchorMin = anchor; rt.anchorMax = anchor; rt.pivot = pivot;
        rt.anchoredPosition = pos; rt.sizeDelta = size;
        return rt;
    }

    [ContextMenu("BF3AI/Rebuild Scope UI")]
    public void BuildScopeUI()
    {
        if (uiRoot != null) { Destroy(uiRoot); uiRoot = null; }
        scopeGroup = null; reticleGroup = null;
        Sprite scope = aimSettings.isScopable ? aimSettings.scopeUIEffect : null;
        if (scope == null && reticleSprite == null) return;

        // Its own overlay canvas, not parented to the weapon (weapons get hidden and swapped), scaled so the screen is always 1920x1080 units tall.
        uiRoot = new GameObject(gameObject.name + "_ScopeUI");
        Canvas cv = uiRoot.AddComponent<Canvas>();
        cv.renderMode = RenderMode.ScreenSpaceOverlay; cv.sortingOrder = uiSortingOrder;
        CanvasScaler cs = uiRoot.AddComponent<CanvasScaler>();
        cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        cs.referenceResolution = new Vector2(RefW, RefH);
        cs.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        cs.matchWidthOrHeight = 1f;          // match height: a scope sized to the screen height fits any aspect ratio
        Vector2 mid = new Vector2(0.5f, 0.5f);

        if (scope != null)
        {
            GameObject sg = new GameObject("Scope");
            sg.transform.SetParent(uiRoot.transform, false);
            sg.AddComponent<RectTransform>();
            scopeGroup = sg.AddComponent<CanvasGroup>();
            scopeGroup.alpha = 0f; scopeGroup.interactable = false; scopeGroup.blocksRaycasts = false;

            Image im = MakeImage("Overlay", sg.transform, scope, Color.white);
            if (scopeBlackBars)
            {
                float h = RefH * scopeScale;
                float w = h * (scope.rect.width / Mathf.Max(1f, scope.rect.height));
                Anchor(im, mid, mid, Vector2.zero, new Vector2(w, h));
                Vector2 big = new Vector2(6000f, 6000f);
                Anchor(MakeImage("BarLeft", sg.transform, null, scopeBarColor), mid, new Vector2(1f, 0.5f), new Vector2(-w * 0.5f, 0f), big);
                Anchor(MakeImage("BarRight", sg.transform, null, scopeBarColor), mid, new Vector2(0f, 0.5f), new Vector2(w * 0.5f, 0f), big);
                Anchor(MakeImage("BarTop", sg.transform, null, scopeBarColor), mid, new Vector2(0.5f, 0f), new Vector2(0f, h * 0.5f), big);
                Anchor(MakeImage("BarBottom", sg.transform, null, scopeBarColor), mid, new Vector2(0.5f, 1f), new Vector2(0f, -h * 0.5f), big);
            }
            else
            {
                RectTransform rt = im.rectTransform;
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                rt.localScale = new Vector3(scopeScale, scopeScale, 1f);
            }
            sg.SetActive(false);
        }

        if (reticleSprite != null)
        {
            GameObject rg = new GameObject("Reticle");
            rg.transform.SetParent(uiRoot.transform, false);
            rg.AddComponent<RectTransform>();
            reticleGroup = rg.AddComponent<CanvasGroup>();
            reticleGroup.alpha = 0f; reticleGroup.interactable = false; reticleGroup.blocksRaycasts = false;
            Image im = MakeImage("Image", rg.transform, reticleSprite, reticleColor);
            Anchor(im, mid, mid, reticleOffset, reticleSprite.rect.size * reticleScale);
            rg.SetActive(false);
        }
        uiRoot.SetActive(isActiveAndEnabled);
    }

    private void UpdateScopeUI()
    {
        if (uiRoot == null) return;
        float step = Mathf.Clamp01(Time.unscaledDeltaTime * scopeFadeSpeed);
        if (scopeGroup != null)
        {
            scopeAlpha = Mathf.MoveTowards(scopeAlpha, isAiming ? 1f : 0f, step);
            scopeGroup.alpha = scopeAlpha;
            bool on = scopeAlpha > 0.01f;
            if (scopeGroup.gameObject.activeSelf != on) scopeGroup.gameObject.SetActive(on);
        }
        if (reticleGroup != null)
        {
            reticleAlpha = Mathf.MoveTowards(reticleAlpha, (isAiming || showReticleWhenNotAiming) ? 1f : 0f, step);
            reticleGroup.alpha = reticleAlpha;
            bool on = reticleAlpha > 0.01f;
            if (reticleGroup.gameObject.activeSelf != on) reticleGroup.gameObject.SetActive(on);
        }
    }

    private void OnEnable() { if (uiRoot != null) uiRoot.SetActive(true); }
    private void OnDisable() { CancelReload(); if (uiRoot != null) uiRoot.SetActive(false); isAiming = false; scopeAlpha = 0f; reticleAlpha = 0f; }
    private void OnDestroy() { if (uiRoot != null) Destroy(uiRoot); }

    private void HandleAimingInput()
    {
        if (!aimSettings.canAim)
        {
            isAiming = false;
            return;
        }

        bool aimInput = false;

        // Right Mouse Button for Mouse & Keyboard
        if (Mouse.current != null)
        {
            aimInput |= Mouse.current.rightButton.isPressed;
        }

        // Left Trigger for Xbox-style Controller
        if (Gamepad.current != null)
        {
            float leftTriggerValue = Gamepad.current.leftTrigger.ReadValue();
            aimInput |= leftTriggerValue > triggerThreshold;
        }

        isAiming = aimInput;

        // Calculate target field of view based on zoom factor
        if (isAiming && playerCamera != null)
        {
            // Higher zoom factor means a smaller, more magnified field of view
            float zoomMultiplier = aimSettings.zoomFactor > 0f ? aimSettings.zoomFactor : 2f;
            targetFOV = defaultFOV / zoomMultiplier;
        }
        else if (playerCamera != null)
        {
            targetFOV = defaultFOV;
        }
    }

    private void UpdateZoom()
    {
        if (playerCamera == null || !aimSettings.canAim) return;

        // Smoothly interpolate the camera field of view toward the target value
        playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, targetFOV, Time.deltaTime * zoomTransitionSpeed);
    }

    private void HandleSpooling()
    {
        if (!spoolSettings.isSpoolable) return;

        // Spool input: Right Shoulder/Bumper on Gamepad (since Right Click is now used for Aiming)
        bool spoolInput = false;

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
        bool triggerPressed = false; 
        bool triggerPulled = false;  

        if (Mouse.current != null)
        {
            triggerPressed |= Mouse.current.leftButton.wasPressedThisFrame;
            triggerPulled |= Mouse.current.leftButton.isPressed;
        }

        if (Gamepad.current != null)
        {
            float rightTriggerValue = Gamepad.current.rightTrigger.ReadValue();
            triggerPressed |= Gamepad.current.rightTrigger.wasPressedThisFrame;
            triggerPulled |= rightTriggerValue > triggerThreshold;
        }

        if (isReloading) return;                                   // no firing while reloading
        if (spoolSettings.isSpoolable && !isSpooledUp) return;

        if (isBurstable)
        {
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

            if (isReloading) break;
            if (!isInfiniteAmmo && currentAmmo <= 0)
            {
                if (autoReloadWhenEmpty) { isBursting = false; Reload(); }
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

        if (isReloading) return;
        if (!isInfiniteAmmo && currentAmmo <= 0)
        {
            if (autoReloadWhenEmpty) Reload();
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

        // BF3AI: let nearby bots hear the shot
        BF3AI.SoundBus.Emit(firePoints[0].position, 70f, owner != null ? owner.team : 0, BF3AI.SoundType.Gunshot);

        // Where is the crosshair pointing? One ray from the camera centre per shot; every projectile aims at that point.
        Vector3 aimPoint;
        bool hasAim = TryGetCrosshairPoint(out aimPoint);

        foreach (Transform firePoint in firePoints)
        {
            // Is the fire point inside (or behind) a solid? Then the bolt hits that surface instead of coming out the other side.
            RaycastHit blockHit = default(RaycastHit);
            bool muzzleBlocked = blockShotsThroughWalls && TryMuzzleBlock(firePoint.position, out blockHit);
            Vector3 aimDir = ResolveAimDirection(firePoint.position, firePoint.forward, aimPoint, hasAim, minAimDistance, aimBlendDistance, maxAimBendAngle, aimBendFalloff);
            if (muzzleBlocked) aimDir = -blockHit.normal;                       // fly straight into the surface that is in the way
            Vector3 spawnPos = muzzleBlocked ? blockHit.point + blockHit.normal * Mathf.Max(0.005f, muzzleSurfaceOffset) : firePoint.position;
            Quaternion baseRotation = (hasAim || muzzleBlocked) ? Quaternion.LookRotation(aimDir, firePoint.up) : firePoint.rotation;

            for (int i = 0; i < projectilesPerShot; i++)
            {
                float randomSpreadX = Random.Range(-spreadX, spreadX);
                float randomSpreadY = Random.Range(-spreadY, spreadY);

                Quaternion aimRotation = muzzleBlocked ? baseRotation : baseRotation * Quaternion.Euler(randomSpreadX, randomSpreadY, 0f);
                Quaternion visualRotation = aimRotation * Quaternion.Euler(projectileRotationOffset);

                GameObject bulletInstance = Instantiate(bulletPrefab, spawnPos, visualRotation);

                Lazers bolt = bulletInstance.GetComponent<Lazers>();
                if (bolt != null)
                {
                    bolt.Initialize(bulletSpeed, damage, aimRotation * Vector3.forward);
                    bolt.SetOwner(owner);   // BF3AI: credit kills to the shooter and tag the bolt with its team so it never hurts teammates
                }
                else
                {
                    Debug.LogWarning("bulletPrefab has no Lazers component attached — it will spawn but not move.", bulletInstance);
                }
            }
        }
    }

    private static readonly RaycastHit[] aimHits = new RaycastHit[32];

    /// <summary>
    /// Casts a ray from the screen centre (shifted by Reticle Offset so the reticle is still exact) and returns the point it hits.
    /// Your own colliders and triggers are skipped. Hits nothing = a point Aim Max Distance ahead. Returns false if there is no camera or aiming is off.
    /// </summary>
    private bool TryGetCrosshairPoint(out Vector3 point)
    {
        point = Vector3.zero;
        if (!aimAtCrosshair) return false;
        if (playerCamera == null) playerCamera = Camera.main;
        if (playerCamera == null) return false;

        // The reticle sits at the screen centre plus Reticle Offset (given in 1080p-height units); convert that to a viewport position.
        float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
        Vector3 vp = new Vector3(0.5f + reticleOffset.x / (RefH * aspect), 0.5f + reticleOffset.y / RefH, 0f);
        Ray ray = playerCamera.ViewportPointToRay(vp);

        float maxD = Mathf.Max(1f, aimMaxDistance);
        int n = Physics.RaycastNonAlloc(ray, aimHits, maxD, aimRayMask, QueryTriggerInteraction.Ignore);
        Transform skip = owner != null ? owner.transform : transform.root;
        float best = float.MaxValue;
        point = ray.origin + ray.direction * maxD;
        for (int i = 0; i < n; i++)
        {
            Collider c = aimHits[i].collider;
            if (c == null) continue;
            Transform t = c.transform;
            if (t.IsChildOf(skip) || t.IsChildOf(transform)) continue;   // the player and this weapon never block the aim ray
            if (aimHits[i].distance < best) { best = aimHits[i].distance; point = aimHits[i].point; }
        }
        return true;
    }

    /// <summary>
    /// Direction a projectile takes from a fire point: a smooth blend between "straight out of the fire point" and "at the aim point".
    /// Distance weight: 0 at or below minDist, rising smoothly to 1 at minDist + blendDist (0 blendDist = hard switch).
    /// Angle weight: 1 while the turn needed is at most (maxBend - falloff) degrees, fading to 0 at maxBend. maxBend 180 = no limit.
    /// No aim point = straight out.
    /// </summary>
    public static Vector3 ResolveAimDirection(Vector3 firePos, Vector3 fireForward, Vector3 aimPoint, bool hasAim, float minDist, float blendDist, float maxBendDeg, float falloffDeg)
    {
        if (!hasAim) return fireForward;
        Vector3 fwd = fireForward.normalized;
        Vector3 d = aimPoint - firePos;
        float m = d.magnitude;
        if (m < 0.0001f) return fireForward;
        d /= m;

        float wd = blendDist > 0.0001f ? Mathf.Clamp01((m - minDist) / blendDist) : (m > minDist ? 1f : 0f);
        wd = wd * wd * (3f - 2f * wd);                                   // smoothstep

        float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(d, fwd), -1f, 1f)) * 57.29578f;
        float wa;
        if (maxBendDeg >= 179.5f) wa = 1f;                                // no limit
        else if (falloffDeg > 0.0001f) wa = Mathf.Clamp01((maxBendDeg - angle) / falloffDeg);
        else wa = angle <= maxBendDeg ? 1f : 0f;

        float w = wd * wa;
        if (w <= 0f) return fireForward;
        Vector3 r = fwd * (1f - w) + d * w;
        if (r.sqrMagnitude < 0.000001f) return fireForward;               // exactly opposite: no sensible blend
        return r.normalized;
    }

    /// <summary>
    /// True if a solid sits between the camera (your eyes) and the fire point, i.e. the fire point is inside or behind a wall.
    /// Returns the first such surface. Your own colliders and triggers never count.
    /// </summary>
    private bool TryMuzzleBlock(Vector3 firePos, out RaycastHit block)
    {
        block = default(RaycastHit);
        if (playerCamera == null) playerCamera = Camera.main;
        if (playerCamera == null) return false;
        Vector3 origin = playerCamera.transform.position;
        // Third-person: the camera floats behind the player, so check from the player's own eyes instead.
        if (owner != null && (origin - owner.Eye).sqrMagnitude > 1f) origin = owner.Eye;
        Vector3 d = firePos - origin;
        float dist = d.magnitude;
        if (dist < 0.02f) return false;

        int n = Physics.RaycastNonAlloc(new Ray(origin, d / dist), aimHits, dist, muzzleBlockMask, QueryTriggerInteraction.Ignore);
        Transform skip = owner != null ? owner.transform : transform.root;
        float best = float.MaxValue; bool found = false;
        for (int i = 0; i < n; i++)
        {
            Collider c = aimHits[i].collider;
            if (c == null) continue;
            Transform t = c.transform;
            if (t.IsChildOf(skip) || t.IsChildOf(transform)) continue;
            if (aimHits[i].distance < best) { best = aimHits[i].distance; block = aimHits[i]; found = true; }
        }
        return found;
    }

    // Right-click this component's header (or the three dots) > BF3AI > Save As Bot Loadout.
    // Works on the weapon prefab in the Project window (edit mode) or in play mode.
    [ContextMenu("BF3AI/Save As Bot Loadout (CSV)")]
    private void SaveAsBotLoadout()
    {
        string weaponName = gameObject.name.Replace("(Clone)", "").Trim();
        BF3AI.Loadout lo = new BF3AI.Loadout
        {
            id = string.IsNullOrWhiteSpace(botLoadoutId) ? weaponName : botLoadoutId.Trim(),
            cls = botClass,
            weight = botWeight,
            weapon = weaponName,
            fireRate = fireRate,
            damage = damage,
            spread = (spreadX + spreadY) * 0.5f,
            magazine = Mathf.Max(1, magazineSize),
            reload = reloadTime,
            range = botRange,
            bulletSpeed = bulletSpeed,
            projectiles = Mathf.Max(1, projectilesPerShot),
            burst = isBurstable ? Mathf.Max(1, volleysPerBurst) : 1,
            burstDelay = burstDelay,
            spool = spoolSettings.isSpoolable ? spoolSettings.spoolTime : 0f,
            zoom = aimSettings.canAim ? (aimSettings.zoomFactor > 0f ? aimSettings.zoomFactor : 2f) : 1f
        };

        string file;
        try { file = BF3AI.LoadoutStore.Save(lo); }
        catch (System.Exception e) { Debug.LogError("[Blaster] Could not save the bot loadout: " + e.Message, this); return; }

        Debug.Log("[Blaster] Saved bot loadout '" + lo.id + "' (" + lo.cls + ") to " + file + ". Bots pick it at random among the " + lo.cls + " rows each life.", this);
        if (!string.IsNullOrEmpty(BF3AI.LoadoutStore.lastSaveNote)) Debug.LogWarning("[Blaster] " + BF3AI.LoadoutStore.lastSaveNote, this);
        if (Resources.Load<GameObject>(BF3AI.LoadoutStore.weaponsPath + "/" + weaponName) == null)
            Debug.LogWarning("[Blaster] No prefab 'Resources/" + BF3AI.LoadoutStore.weaponsPath + "/" + weaponName + "' was found, so bots using this row will fire generated tracers and show no weapon. Put the weapon prefab in a Resources/" + BF3AI.LoadoutStore.weaponsPath + " folder (the same one WeaponManager loads from).", this);
    }

    /// <summary>Starts a reload (takes Reload Time seconds, firing is blocked meanwhile). Ignored if full, infinite or already reloading.</summary>
    public void Reload()
    {
        if (InfiniteAmmo || isReloading || currentAmmo >= magazineSize || !isActiveAndEnabled) return;
        if (reloadTime <= 0f) { currentAmmo = magazineSize; return; }
        StartCoroutine(ReloadRoutine());
    }

    private IEnumerator ReloadRoutine()
    {
        isReloading = true;
        reloadStartTime = Time.time;
        if (reloadAnimator != null)
        {
            if (!string.IsNullOrEmpty(reloadSpeedParam)) reloadAnimator.SetFloat(reloadSpeedParam, reloadAnimationLength / Mathf.Max(0.05f, reloadTime));
            if (!string.IsNullOrEmpty(reloadTrigger)) reloadAnimator.SetTrigger(reloadTrigger);
        }
        if (reloadSound != null) AudioSource.PlayClipAtPoint(reloadSound, transform.position, reloadSoundVolume);

        yield return new WaitForSeconds(reloadTime);

        currentAmmo = magazineSize;
        isReloading = false;
    }

    // A reload or burst that was cut off (weapon swapped away, player died) is cancelled: the magazine is NOT refilled.
    private void CancelReload()
    {
        StopAllCoroutines();
        isReloading = false; isBursting = false;
        if (reloadAnimator != null && !string.IsNullOrEmpty(reloadTrigger)) reloadAnimator.ResetTrigger(reloadTrigger);
    }

    private GUIStyle ammoStyle;

    private void OnGUI()
    {
        if (!showAmmoOnScreen) return;
        if (ammoStyle == null) { ammoStyle = new GUIStyle(GUI.skin.label); ammoStyle.fontStyle = FontStyle.Bold; }
        ammoStyle.fontSize = Mathf.Max(14, Screen.height / 28);
        bool right = ammoHudCorner == HudCorner.BottomRight || ammoHudCorner == HudCorner.TopRight;
        bool bottom = ammoHudCorner == HudCorner.BottomRight || ammoHudCorner == HudCorner.BottomLeft;
        ammoStyle.alignment = right ? TextAnchor.UpperRight : TextAnchor.UpperLeft;

        string text;
        Color col;
        if (InfiniteAmmo) { text = "AMMO  \u221E"; col = Color.white; }
        else if (isReloading) { text = "RELOADING"; col = Color.yellow; }
        else { text = "AMMO  " + currentAmmo + " / " + magazineSize; col = currentAmmo <= Mathf.Max(1, magazineSize / 4) ? Color.red : Color.white; }

        float w = 360f, h = ammoStyle.fontSize * 1.5f;
        float x = right ? Screen.width - ammoHudMargin.x - w : ammoHudMargin.x;
        float y = bottom ? Screen.height - ammoHudMargin.y - h - 14f : ammoHudMargin.y;
        GUI.color = new Color(0f, 0f, 0f, 0.8f); GUI.Label(new Rect(x + 2f, y + 2f, w, h), text, ammoStyle);   // drop shadow
        GUI.color = col; GUI.Label(new Rect(x, y, w, h), text, ammoStyle);

        if (isReloading)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.6f); GUI.DrawTexture(new Rect(x, y + h, w, 8f), Texture2D.whiteTexture);
            GUI.color = Color.yellow; GUI.DrawTexture(new Rect(x, y + h, w * ReloadProgress, 8f), Texture2D.whiteTexture);
        }
        GUI.color = Color.white;
    }
}