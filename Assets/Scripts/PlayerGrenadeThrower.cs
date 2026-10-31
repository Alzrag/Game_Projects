using UnityEngine;
using UnityEngine.InputSystem;

namespace BF3AI
{
    /// <summary>
    /// Lets the PLAYER throw grenades (add it to the player, next to the Combatant).
    ///
    ///   Hold G (gamepad: left shoulder)   pull a grenade out and COOK it: the fuse is already burning while you hold it.
    ///   Release                           throw it at the spot under your crosshair (it arcs there).
    ///   X (gamepad: B)                    put it back without throwing.
    ///   Hold too long                     it is put away for you (it never goes off in your hand) and you keep the grenade.
    ///
    /// The thrown grenade is a GrenadeProjectile: bolts can shoot it out of the air, it leaves a glowing trail, and by default it hurts enemies
    /// and YOU (if you stand in the blast) but never your teammates.
    /// The same pattern (BeginCook / ThrowHeld / CancelCook) works for abilities: call them from your own input code.
    /// </summary>
    public class PlayerGrenadeThrower : MonoBehaviour
    {
        [Header("Grenades")]
        [Tooltip("Optional. A prefab with a GrenadeProjectile (BF3AI menu > Create Grenade Prefab). Empty = a built-in grenade.")]
        public GameObject grenadePrefab;
        public int grenades = 3;
        public int maxGrenades = 5;

        [Header("Throw")]
        [Tooltip("Optional. Where the grenade sits in your hand while cooking. Empty = a spot in front of and below the camera.")]
        public Transform handPoint;
        public Camera playerCamera;
        [Tooltip("The fastest you can throw, in m/s. Farther targets than this reaches simply fall short.")]
        public float maxThrowSpeed = 17f;
        [Tooltip("Launch angle (degrees above horizontal) used to reach the crosshair point. Higher = lobbed.")]
        public float throwArc = 28f;
        [Tooltip("How far the crosshair ray looks for the spot to throw at, in metres.")]
        public float aimDistance = 45f;
        public LayerMask aimMask = ~0;
        public Vector3 handOffset = new Vector3(0.35f, -0.3f, 0.6f);

        [Header("HUD")]
        public bool showHud = true;
        public Vector2 hudOffset = new Vector2(20f, 90f);          // from the bottom-right corner
        public Color hudColor = Color.white;

        Combatant owner;
        GrenadeProjectile held;
        float messageUntil; string message = "";
        GUIStyle style;

        public bool IsCooking { get { return held != null; } }
        /// <summary>0 to 1: how close the held grenade is to being put away.</summary>
        public float CookFraction { get { return held != null ? held.HoldFraction : 0f; } }

        void Awake()
        {
            owner = GetComponentInParent<Combatant>();
            if (owner == null) owner = GetComponentInChildren<Combatant>();
            if (playerCamera == null) playerCamera = Camera.main;
        }

        void OnDisable() { if (held != null) CancelCook(); }

        public void AddGrenades(int n) { grenades = Mathf.Clamp(grenades + n, 0, Mathf.Max(maxGrenades, grenades)); }

        void Update()
        {
            if (owner != null && !owner.IsAlive) { if (held != null) { held = null; } return; }     // a dead player drops it (the grenade handles that)
            if (held != null && held.HasDetonated) held = null;

            Keyboard kb = Keyboard.current; Gamepad gp = Gamepad.current;
            bool down = (kb != null && kb.gKey.wasPressedThisFrame) || (gp != null && gp.leftShoulder.wasPressedThisFrame);
            bool up = (kb != null && kb.gKey.wasReleasedThisFrame) || (gp != null && gp.leftShoulder.wasReleasedThisFrame);
            bool cancel = (kb != null && kb.xKey.wasPressedThisFrame) || (gp != null && gp.buttonEast.wasPressedThisFrame);

            if (down && held == null) BeginCook();
            else if (cancel && held != null) CancelCook();
            else if (up && held != null) ThrowHeld();
        }

        /// <summary>Pulls a grenade out and starts the fuse. Returns false if you have none or are already holding one.</summary>
        public bool BeginCook()
        {
            if (held != null || grenades <= 0) return false;
            if (playerCamera == null) playerCamera = Camera.main;
            Transform hand = HandTransform();
            grenades--;
            held = GrenadeProjectile.BeginHold(grenadePrefab, owner, hand);
            held.OnPutAway += HandlePutAway;
            return true;
        }

        /// <summary>Throws the held grenade at whatever the crosshair is on. The time it was held is already taken off the fuse.</summary>
        public bool ThrowHeld()
        {
            if (held == null) return false;
            GrenadeProjectile g = held; held = null;
            g.OnPutAway -= HandlePutAway;
            Vector3 from = g.transform.position;
            g.Release(AimVelocity(from));
            return true;
        }

        /// <summary>Puts the held grenade back (you keep it).</summary>
        public void CancelCook()
        {
            if (held == null) return;
            GrenadeProjectile g = held;                                 // PutAway raises OnPutAway, which gives the grenade back
            g.PutAway();
        }

        void HandlePutAway(GrenadeProjectile g)
        {
            if (g == held) { held = null; grenades++; message = "GRENADE PUT AWAY"; messageUntil = Time.time + 1.2f; }
        }

        Transform HandTransform()
        {
            if (handPoint != null) return handPoint;
            Transform cam = playerCamera != null ? playerCamera.transform : transform;
            GameObject h = new GameObject("GrenadeHand");
            h.transform.SetParent(cam, false);
            h.transform.localPosition = handOffset;
            handPoint = h.transform;
            return handPoint;
        }

        /// <summary>The launch velocity that puts the grenade on the crosshair point (or a sensible lob if nothing is hit).</summary>
        public Vector3 AimVelocity(Vector3 from)
        {
            Transform cam = playerCamera != null ? playerCamera.transform : transform;
            Vector3 fwd = cam.forward;
            Vector3 target = cam.position + fwd * aimDistance;
            if (playerCamera != null)
            {
                Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                RaycastHit hit;
                if (Physics.Raycast(ray, out hit, aimDistance, aimMask, QueryTriggerInteraction.Ignore)
                    && (owner == null || !hit.collider.transform.IsChildOf(owner.transform)))
                { target = hit.point + hit.normal * 0.15f; fwd = ray.direction; }
                else target = ray.origin + ray.direction * aimDistance;
            }
            Vector3 vel;
            if (GrenadeProjectile.SolveThrow(from, target, throwArc, out vel))
            {
                if (vel.magnitude > maxThrowSpeed) vel = vel.normalized * maxThrowSpeed;      // too far: it falls short
                return vel;
            }
            return fwd.normalized * maxThrowSpeed * 0.8f + Vector3.up * 3f;
        }

        void OnGUI()
        {
            if (!showHud) return;
            if (style == null) { style = new GUIStyle(); style.fontSize = 22; style.fontStyle = FontStyle.Bold; style.alignment = TextAnchor.LowerRight; }
            style.normal.textColor = hudColor;
            float w = 260f, h = 30f;
            float x = Screen.width - hudOffset.x - w, y = Screen.height - hudOffset.y - h;
            GUI.Label(new Rect(x, y, w, h), "GRENADES  " + grenades, style);

            if (held != null)
            {
                // cook bar: fills as the grenade is held; at the end it is put away
                float f = held.HoldFraction;
                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.6f); GUI.DrawTexture(new Rect(x + w - 160f, y - 14f, 160f, 8f), Texture2D.whiteTexture);
                GUI.color = Color.Lerp(new Color(1f, 0.8f, 0.2f), new Color(1f, 0.15f, 0.1f), f); GUI.DrawTexture(new Rect(x + w - 160f, y - 14f, 160f * f, 8f), Texture2D.whiteTexture);
                GUI.color = old;
            }
            else if (Time.time < messageUntil)
            {
                GUI.Label(new Rect(x, y - 30f, w, h), message, style);
            }
        }
    }
}
