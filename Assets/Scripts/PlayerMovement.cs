using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : MonoBehaviour
{
    public Camera playerCamera;

    public int camMode = 0; // 0 for first-person, 1 for third-person

    public float walkSpeed = 6f;
    public float runMult = 2f;
    public float jumpPower = 7f;
    public float gravity = 9.8f;

    public float lookXSpeed = 2f;
    public float lookYSpeed = 2f;

    [Tooltip("Vertical look clamp, applied the same way in both FPS and TPS since they share the same pitch value.")]
    public float lookYLimit = 90f;

    public Transform fpsPos;
    public Transform tpsPos1; // First third-person position (Right shoulder)
    public Transform tpsPos2; // Second third-person position (Left shoulder)
    private int tpsIndex = 0;   // 0 for tpsPos1, 1 for tpsPos2

    public float camTransitionSpeed = 10f;

    [Header("Dash")]
    [Tooltip("Horizontal speed while dashing.")]
    public float dashSpeed = 20f;
    [Tooltip("How long the dash lasts, in seconds.")]
    public float dashDuration = 0.2f;
    [Tooltip("Time after a dash ends before another can be triggered.")]
    public float dashCooldown = 1f;

    private float dashTimer = 0f;
    private float dashCooldownTimer = 0f;
    private Vector3 dashDirection;

    [Tooltip("Max time between two Left Alt presses for it to count as a double-tap.")]
    public float dashDoubleTapWindow = 0.3f;
    private float lastAltPressTime = -10f;

    /// <summary>True for the duration of an active dash. Handy for other scripts (e.g. disabling fire while dashing).</summary>
    public bool IsDashing => dashTimer > 0f;

    [Header("Camera Collision (Third-Person)")]
    [Tooltip("Radius of the camera's collision probe. Roughly matches the near clip plane so the camera doesn't visibly clip into geometry.")]
    public float cameraCollisionRadius = 0.2f;
    [Tooltip("Layers the camera will pull in front of. Exclude the Player layer or you'll get false hits against your own body.")]
    public LayerMask cameraCollisionMask = ~0;
    [Tooltip("How quickly the camera eases back outward once no longer obstructed. Pulling inward on collision is instant, this only controls the rebound.")]
    public float cameraReboundSpeed = 5f;

    private float currentCamDistance = -1f; // -1 = not yet initialized

    [Header("Weapon")]
    [Tooltip("Assign a transform (parented under this GameObject, same as fpsPos) that should always pitch with the camera so the gun points at screen center.")]
    public Transform weaponHolder;

    [Header("Crouch")]
    [Tooltip("Off: hold the crouch key. On: press once to crouch, again to stand.")]
    public bool crouchToggle = false;
    [Tooltip("Crouched size as a fraction of standing size (0.6 = 60%). With no Crouch Animator the whole player is scaled down by this factor (fallback). With an animator, the collider's height shrinks by this factor instead.")]
    [Range(0.2f, 1f)] public float crouchScaleFactor = 0.6f;
    [Tooltip("Walk speed multiplier while crouched (running is disabled).")]
    public float crouchSpeedMult = 0.5f;
    [Tooltip("How fast the crouch happens. Higher = faster (10 = about a tenth of a second).")]
    public float crouchTransitionSpeed = 10f;
    [Tooltip("Optional. If assigned, crouching plays your animation (the Bool parameter below is set) and the player is NOT scaled. Leave empty to use the scale fallback.")]
    public Animator crouchAnimator;
    [Tooltip("Animator Bool parameter that is true while crouching.")]
    public string crouchBoolParam = "Crouching";
    [Tooltip("Layers that stop you standing up when something is above your head.")]
    public LayerMask standUpCheckMask = ~0;

    [Header("Stairs and Slopes")]
    [Tooltip("Tallest step the player can walk up without jumping, in metres (the CharacterController's Step Offset).")]
    public float stepHeight = 0.5f;
    [Tooltip("Steepest slope the player can walk up, in degrees.")]
    public float slopeLimit = 50f;
    [Tooltip("Small downward push while standing on the ground. Keeps the controller glued to stairs and slopes so 'grounded' does not flicker. Leave at 2 unless you see sticking.")]
    public float groundedStickForce = 2f;

    /// <summary>True while crouching (or being forced to stay crouched under a low ceiling).</summary>
    public bool IsCrouching { get { return crouching; } }
    /// <summary>0 = standing, 1 = fully crouched.</summary>
    public float CrouchAmount { get { return crouchT; } }

    private bool crouching, crouchToggled;
    private float crouchT, currentScaleFactor = 1f;
    private Vector3 baseScale = Vector3.one;
    private float standHeight, standCenterY;
    private bool animatorHasParam;
    private BF3AI.Combatant combatant;

    private CharacterController characterController;
    private Vector3 moveDirection = Vector3.zero;
    private float rotationX = 0f; 
    private float rotationY = 0f; 
    private float speed;
    public bool hand = true; // right hand = true (1) left = false (0);

    void Start()
    {
        characterController = GetComponent<CharacterController>();
        combatant = GetComponent<BF3AI.Combatant>();
        baseScale = transform.localScale;
        standHeight = characterController.height;
        standCenterY = characterController.center.y;
        ApplyStepSettings();
        if (combatant != null) combatant.crouchHeight = combatant.standingHeight * crouchScaleFactor;   // hit height while crouched matches the visual
        if (crouchAnimator != null)
        {
            foreach (AnimatorControllerParameter p in crouchAnimator.parameters)
                if (p.name == crouchBoolParam && p.type == AnimatorControllerParameterType.Bool) animatorHasParam = true;
            if (!animatorHasParam)
                Debug.LogWarning("[PlayerMovement] The Crouch Animator has no Bool parameter named '" + crouchBoolParam + "', so no crouch animation will play. The collider still shrinks.", this);
        }
        Cursor.lockState = CursorLockMode.Locked; 
        Cursor.visible = false;
        speed = walkSpeed;
    }

    // Sets the CharacterController's step and slope limits from the settings above (also callable from the Inspector menu).
    [ContextMenu("Apply Step Settings")]
    void ApplyStepSettings()
    {
        if (characterController == null) characterController = GetComponent<CharacterController>();
        if (characterController == null) return;
        // Unity needs Step Offset to be smaller than the controller's height, so cap it.
        characterController.stepOffset = Mathf.Clamp(stepHeight, 0f, Mathf.Max(0.05f, characterController.height * 0.9f));
        characterController.slopeLimit = Mathf.Clamp(slopeLimit, 0f, 89f);
    }

    void OnValidate()
    {
        if (Application.isPlaying) ApplyStepSettings();
    }

    // ------------------------------------------------------------------ crouch
    void UpdateCrouch()
    {
        bool held = (Keyboard.current != null && Keyboard.current.leftCtrlKey.isPressed) || (Gamepad.current != null && Gamepad.current.rightStickButton.isPressed);
        bool pressed = (Keyboard.current != null && Keyboard.current.leftCtrlKey.wasPressedThisFrame) || (Gamepad.current != null && Gamepad.current.rightStickButton.wasPressedThisFrame);

        bool want;
        if (crouchToggle) { if (pressed) crouchToggled = !crouchToggled; want = crouchToggled; }
        else want = held;

        if (!want && crouching && !CanStandUp()) { want = true; if (crouchToggle) crouchToggled = true; }   // low ceiling: stay down
        crouching = want;

        crouchT = Mathf.MoveTowards(crouchT, crouching ? 1f : 0f, Mathf.Max(0.01f, crouchTransitionSpeed) * Time.deltaTime);

        if (crouchAnimator != null)
        {
            if (animatorHasParam) crouchAnimator.SetBool(crouchBoolParam, crouching);
            ShapeCollider(crouchT);                       // animation drives the visuals, the collider shrinks to match
        }
        else ApplyScale(crouchT);                         // fallback: scale the whole player

        if (combatant != null) combatant.crouched = crouchT > 0.5f;
    }

    // Animator route: shrink the CharacterController from the top, keeping the soles where they are.
    void ShapeCollider(float t)
    {
        float minH = characterController.radius * 2f + 0.01f;
        float h = Mathf.Max(minH, Mathf.Lerp(standHeight, standHeight * crouchScaleFactor, t));
        float soleLocal = standCenterY - standHeight * 0.5f;
        characterController.height = h;
        Vector3 c = characterController.center;
        c.y = soleLocal + h * 0.5f;
        characterController.center = c;
    }

    // Fallback route: scale the whole player about its feet (the origin may be mid-body, so move it to keep the soles on the floor).
    void ApplyScale(float t)
    {
        float f = Mathf.Lerp(1f, crouchScaleFactor, t);
        if (Mathf.Abs(f - currentScaleFactor) < 0.0001f) return;
        float feetLocal = standHeight * 0.5f - standCenterY;            // origin to sole, in local units
        float dy = feetLocal * baseScale.y * (f - currentScaleFactor);   // how far the origin must move to keep the soles fixed
        transform.localScale = baseScale * f;
        characterController.Move(Vector3.up * dy);
        currentScaleFactor = f;
    }

    // Is there room above the head to stand back up?
    bool CanStandUp()
    {
        float sy = Mathf.Max(0.0001f, transform.lossyScale.y);
        float curH = characterController.height * sy;
        float standWorldH = standHeight * baseScale.y;
        if (standWorldH - curH <= 0.02f) return true;

        float soleY = transform.position.y + (characterController.center.y - characterController.height * 0.5f) * sy;
        float r = characterController.radius * sy * 0.9f;
        Vector3 c = transform.TransformPoint(new Vector3(characterController.center.x, 0f, characterController.center.z));
        float topNow = soleY + curH, topStand = soleY + standWorldH;
        Vector3 p1 = new Vector3(c.x, topNow + 0.02f + r, c.z);
        Vector3 p2 = new Vector3(c.x, Mathf.Max(p1.y, topStand - r), c.z);
        return !Physics.CheckCapsule(p1, p2, r, standUpCheckMask, QueryTriggerInteraction.Ignore);
    }

    void Update()
    {
        // Toggle between First-Person and Third-Person using 'C' key OR D-pad Up
        bool camTogglePressed = (Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame) || 
                                (Gamepad.current != null && Gamepad.current.dpad.up.wasPressedThisFrame);
        if (camTogglePressed)
        {
            camMode = (camMode == 0) ? 1 : 0;
        }

        // Swap between TPS Position 1 and TPS Position 2 using 'V' key OR D-pad Left
        bool tpsSwapPressed = (Keyboard.current != null && Keyboard.current.vKey.wasPressedThisFrame) || 
                              (Gamepad.current != null && Gamepad.current.dpad.left.wasPressedThisFrame);
        if (tpsSwapPressed)
        {
            tpsIndex = (tpsIndex == 0) ? 1 : 0;
            hand = !hand; // Toggle hand status to match shoulder view
        }

        UpdateCrouch();

        bool isRunning = ((Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed) || (Gamepad.current != null && Gamepad.current.leftStickButton.isPressed)) && crouchT < 0.1f;
        float currentSpeed = isRunning ? walkSpeed * runMult : walkSpeed;
        currentSpeed *= Mathf.Lerp(1f, crouchSpeedMult, crouchT);

        float vertical = 0f; float horizontal = 0f;
        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed) vertical += 1f;
            if (Keyboard.current.sKey.isPressed) vertical -= 1f;
            if (Keyboard.current.dKey.isPressed) horizontal += 1f;
            if (Keyboard.current.aKey.isPressed) horizontal -= 1f;
        }
        if (Gamepad.current != null)
        {
            Vector2 stick = Gamepad.current.leftStick.ReadValue();
            horizontal += stick.x; vertical += stick.y;
        }

        Vector2 inputDir = new Vector2(horizontal, vertical);
        if (inputDir.sqrMagnitude > 1f) { inputDir.Normalize(); }

        Vector3 forward = transform.TransformDirection(Vector3.forward);
        Vector3 right = transform.TransformDirection(Vector3.right);

        // --- Dash ---
        if (dashCooldownTimer > 0f)
        {
            dashCooldownTimer -= Time.deltaTime;
        }

        // Track Alt taps every frame, independent of whether a dash can currently trigger,
        // so double-tap timing stays accurate across consecutive dashes/cooldowns.
        bool altDoubleTapped = false;
        if (Keyboard.current != null && Keyboard.current.leftAltKey.wasPressedThisFrame)
        {
            altDoubleTapped = (Time.time - lastAltPressTime) <= dashDoubleTapWindow;
            lastAltPressTime = Time.time;
        }

        bool gamepadDashPressed = Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame;
        bool hasDirectionInput = inputDir.sqrMagnitude > 0.01f;

        if ((altDoubleTapped || gamepadDashPressed) && hasDirectionInput && dashTimer <= 0f && dashCooldownTimer <= 0f)
        {
            Vector3 dashInputDirection = (forward * inputDir.y) + (right * inputDir.x);
            dashDirection = dashInputDirection.normalized;
            dashTimer = dashDuration;
            dashCooldownTimer = dashCooldown;
        }

        float moveDirectionYStorage = moveDirection.y;

        if (dashTimer > 0f)
        {
            moveDirection = dashDirection * dashSpeed;
            dashTimer -= Time.deltaTime;
        }
        else
        {
            moveDirection = (forward * currentSpeed * inputDir.y) + (right * currentSpeed * inputDir.x);
        }

        bool isJumpPressed = (Keyboard.current != null && Keyboard.current.spaceKey.isPressed) || (Gamepad.current != null && Gamepad.current.buttonSouth.isPressed);
        bool grounded = characterController.isGrounded;
        moveDirection.y = (isJumpPressed && grounded && crouchT < 0.5f) ? jumpPower : moveDirectionYStorage;
        if (grounded && moveDirection.y < 0f) { moveDirection.y = -groundedStickForce; }   // stay glued to the ground so stairs and slopes work
        else if (!grounded) { moveDirection.y -= gravity * Time.deltaTime; }

        characterController.Move(moveDirection * Time.deltaTime);

        Vector2 mouseDelta = Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
        Vector2 gamepadLook = Gamepad.current != null ? Gamepad.current.rightStick.ReadValue() * 120f * Time.deltaTime : Vector2.zero;

        float lookX = (mouseDelta.x * 0.1f) + gamepadLook.x;
        float lookY = (mouseDelta.y * 0.1f) + gamepadLook.y;

        rotationX += -lookY * lookYSpeed; 
        rotationX = Mathf.Clamp(rotationX, -lookYLimit, lookYLimit);
        rotationY += lookX * lookXSpeed;

        transform.rotation = Quaternion.Euler(0, rotationY, 0);

        // Weapon holder always pitches with the camera (yaw is inherited automatically
        // since it should be parented under this transform, same as fpsPos).
        if (weaponHolder != null)
        {
            weaponHolder.localRotation = Quaternion.Euler(rotationX, 0f, 0f);
        }

        if (camMode == 0)
        {
            playerCamera.transform.position = Vector3.Lerp(playerCamera.transform.position, fpsPos.position, camTransitionSpeed * Time.deltaTime);
            playerCamera.transform.localRotation = Quaternion.Euler(rotationX, 0, 0);
        }
        else
        {
            // Use the active TPS transform's exact world position as the resting baseline
            Transform activeTpsPos = (tpsIndex == 0) ? tpsPos1 : tpsPos2;
            
            // Pivot point around the upper body of the player
            Vector3 pivot = transform.position + Vector3.up * 1.5f * transform.lossyScale.y;   // follows the crouch scale
            
            Vector3 restPos = (activeTpsPos != null) ? activeTpsPos.position : pivot + new Vector3(0.5f, 0f, -3f);
            
            // Calculate vector from pivot to the resting shoulder position
            Vector3 pivotToCam = restPos - pivot;
            Vector3 pivotToCamDir = pivotToCam.normalized;
            float maxDistance = pivotToCam.magnitude;

            if (currentCamDistance < 0f)
            {
                currentCamDistance = maxDistance;
            }

            // Pitch (vertical rotation) around the player's right axis
            Quaternion pitchRotation = Quaternion.AngleAxis(rotationX, transform.right);
            Vector3 rotatedDir = pitchRotation * pivotToCamDir;

            // --- Camera collision ---
            float desiredDistance = maxDistance;
            if (Physics.SphereCast(pivot, cameraCollisionRadius, rotatedDir, out RaycastHit camHit, maxDistance, cameraCollisionMask, QueryTriggerInteraction.Ignore))
            {
                desiredDistance = Mathf.Max(camHit.distance - cameraCollisionRadius, 0.1f);
            }

            if (desiredDistance < currentCamDistance)
            {
                // Snap inward immediately so the camera never sees through geometry, even for a single frame.
                currentCamDistance = desiredDistance;
            }
            else
            {
                // Ease back outward, capped at the normal resting distance.
                currentCamDistance = Mathf.MoveTowards(currentCamDistance, desiredDistance, cameraReboundSpeed * Time.deltaTime);
            }

            Vector3 targetTpsPos = pivot + rotatedDir * currentCamDistance;

            playerCamera.transform.position = Vector3.Lerp(playerCamera.transform.position, targetTpsPos, camTransitionSpeed * Time.deltaTime);
            
            // Look forward with the combined player yaw and camera pitch
            playerCamera.transform.rotation = Quaternion.Euler(rotationX, rotationY, 0);
        }
    }
}