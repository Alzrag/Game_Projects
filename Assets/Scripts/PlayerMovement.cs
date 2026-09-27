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

    private CharacterController characterController;
    private Vector3 moveDirection = Vector3.zero;
    private float rotationX = 0f; 
    private float rotationY = 0f; 
    private float speed;
    public bool hand = true; // right hand = true (1) left = false (0);

    void Start()
    {
        characterController = GetComponent<CharacterController>();
        Cursor.lockState = CursorLockMode.Locked; 
        Cursor.visible = false;
        speed = walkSpeed;
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

        bool isRunning = (Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed) || (Gamepad.current != null && Gamepad.current.leftStickButton.isPressed);
        float currentSpeed = isRunning ? walkSpeed * runMult : walkSpeed;

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
        moveDirection.y = (isJumpPressed && characterController.isGrounded) ? jumpPower : moveDirectionYStorage;
        if (!characterController.isGrounded) { moveDirection.y -= gravity * Time.deltaTime; }

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
            Vector3 pivot = transform.position + Vector3.up * 1.5f;
            
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