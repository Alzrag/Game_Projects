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
    public Transform tpsPos;
    public float camTransitionSpeed = 10f;

    private CharacterController characterController;
    private Vector3 moveDirection = Vector3.zero;
    private float rotationX = 0f; private float rotationY = 0f; private float speed;

    void Start()
    {
        characterController = GetComponent<CharacterController>();
        Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        speed = walkSpeed;
    }

    void Update()
    {
        float scroll = Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
        if (Gamepad.current != null) { if (Gamepad.current.dpad.up.wasPressedThisFrame) camMode = 0; if (Gamepad.current.dpad.down.wasPressedThisFrame) camMode = 1; }
        if (scroll > 0f) { camMode = 0; } else if (scroll < 0f) { camMode = 1; }

        bool isRunning = (Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed) || (Gamepad.current != null && Gamepad.current.leftStickButton.isPressed);
        float currentSpeed = isRunning ? walkSpeed * runMult : walkSpeed;

        float vertical = 0f; float horizontal = 0f;
        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) vertical += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) vertical -= 1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) horizontal += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) horizontal -= 1f;
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

        rotationX += -lookY * lookYSpeed; rotationX = Mathf.Clamp(rotationX, -lookYLimit, lookYLimit);
        rotationY += lookX * lookXSpeed;

        transform.rotation = Quaternion.Euler(0, rotationY, 0);

        if (camMode == 0)
        {
            playerCamera.transform.position = Vector3.Lerp(playerCamera.transform.position, fpsPos.position, camTransitionSpeed * Time.deltaTime);
            playerCamera.transform.localRotation = Quaternion.Euler(rotationX, 0, 0);
        }
        else
        {
            // True third-person orbit calculation around a pivot point above the player
            Vector3 pivot = transform.position + Vector3.up * 1.5f;
            float distance = (tpsPos != null) ? Vector3.Distance(transform.position, tpsPos.position) : 4f;
            Quaternion orbitRot = Quaternion.Euler(rotationX, rotationY, 0);
            Vector3 targetTpsPos = pivot - (orbitRot * Vector3.forward * distance);

            playerCamera.transform.position = Vector3.Lerp(playerCamera.transform.position, targetTpsPos, camTransitionSpeed * Time.deltaTime);
            playerCamera.transform.rotation = orbitRot;
        }
    }
}
