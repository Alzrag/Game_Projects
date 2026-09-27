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
    public float lookYLimit = 45f;

    public Transform fpsPos;
    public Transform tpsPos1; // First third-person position (Right shoulder)
    public Transform tpsPos2; // Second third-person position (Left shoulder)
    private int tpsIndex = 0;   // 0 for tpsPos1, 1 for tpsPos2

    public float camTransitionSpeed = 10f;

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

        float moveDirectionYStorage = moveDirection.y;
        moveDirection = (forward * currentSpeed * inputDir.y) + (right * currentSpeed * inputDir.x);

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
            
            // Pitch (vertical rotation) around the player's right axis
            Quaternion pitchRotation = Quaternion.AngleAxis(rotationX, transform.right);
            Vector3 targetTpsPos = pivot + (pitchRotation * pivotToCam);

            playerCamera.transform.position = Vector3.Lerp(playerCamera.transform.position, targetTpsPos, camTransitionSpeed * Time.deltaTime);
            
            // Look forward with the combined player yaw and camera pitch
            playerCamera.transform.rotation = Quaternion.Euler(rotationX, rotationY, 0);
        }
    }
}