using UnityEngine;
using UnityEngine.InputSystem;

public class WeaponManager : MonoBehaviour
{
    public string weaponsPath = "Weapons"; // Folder inside Resources
    public string selectedWeaponP;
    public string selectedWeaponS;
    
    public Transform leftHand;
    public Transform rightHand;
    
    private GameObject primaryWeapon;
    private GameObject secondaryWeapon;
    
    public PlayerMovement playerMov;
    public bool activeHand;
    public bool activeWeapon = true; // true = primary active, false = secondary active
    private bool oldActiveHand;

    private BF3AI.Combatant combatant;   // BF3AI: the player's Combatant

    void Start()
    {
        combatant = GetComponentInParent<BF3AI.Combatant>();

        if (playerMov != null)
        {
            activeHand = playerMov.hand;
        }
        oldActiveHand = activeHand;
        
        LoadSelectedWeapon();
        UpdateThreatFlags();   // BF3AI: after the weapons exist
    }

    void Update()
    {
        if (playerMov != null)
        {
            oldActiveHand = activeHand;
            activeHand = playerMov.hand;
            
            // If hand preference changed, update weapon parents
            if (oldActiveHand != activeHand)
            {
                SwapHands();
            }
        }

        // Check for Mouse Scroll OR Gamepad Y/X button (buttonNorth)
        float scroll = Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
        bool gamepadSwapPressed = Gamepad.current != null && Gamepad.current.buttonNorth.wasPressedThisFrame;

        if (scroll != 0f || gamepadSwapPressed)
        {
            activeWeapon = !activeWeapon; // Toggle between primary and secondary
            SwapWeapons();
        }

    }

    void SwapHands()
    {
        Transform targetHand = activeHand ? rightHand : leftHand;
        
        if (primaryWeapon != null)
        {
            primaryWeapon.transform.SetParent(targetHand);
            primaryWeapon.transform.localPosition = Vector3.zero;
            primaryWeapon.transform.localRotation = Quaternion.identity;
        }
        
        if (secondaryWeapon != null)
        {
            secondaryWeapon.transform.SetParent(targetHand);
            secondaryWeapon.transform.localPosition = Vector3.zero;
            secondaryWeapon.transform.localRotation = Quaternion.identity;
        }
    }

    void SwapWeapons()
    {
        if (primaryWeapon != null) primaryWeapon.SetActive(activeWeapon);
        if (secondaryWeapon != null) secondaryWeapon.SetActive(!activeWeapon);
        UpdateThreatFlags();   // BF3AI
    }

    // BF3AI: bots prioritise players holding a heavy (spool-up) weapon
    void UpdateThreatFlags()
    {
        if (combatant == null) return;
        GameObject w = activeWeapon ? primaryWeapon : secondaryWeapon;
        Blaster b = w != null ? w.GetComponentInChildren<Blaster>() : null;
        combatant.usingHeavyWeapon = b != null && b.spoolSettings.isSpoolable;
    }

    void LoadSelectedWeapon()
    {
        Transform initialHand = activeHand ? rightHand : leftHand;

        // Load Primary Weapon
        if (!string.IsNullOrEmpty(selectedWeaponP))
        {
            string fullPathP = $"{weaponsPath}/{selectedWeaponP}";
            GameObject weaponPrefabP = Resources.Load<GameObject>(fullPathP);

            if (weaponPrefabP != null)
            {
                primaryWeapon = Instantiate(weaponPrefabP, initialHand);
                primaryWeapon.transform.localPosition = Vector3.zero;
                primaryWeapon.transform.localRotation = Quaternion.identity;
            }
            else
            {
                Debug.LogWarning($"Could not find primary weapon at path: {fullPathP}");
            }
        }

        // Load Secondary Weapon
        if (!string.IsNullOrEmpty(selectedWeaponS))
        {
            string fullPathS = $"{weaponsPath}/{selectedWeaponS}";
            GameObject weaponPrefabS = Resources.Load<GameObject>(fullPathS);

            if (weaponPrefabS != null)
            {
                secondaryWeapon = Instantiate(weaponPrefabS, initialHand);
                secondaryWeapon.transform.localPosition = Vector3.zero;
                secondaryWeapon.transform.localRotation = Quaternion.identity;
                secondaryWeapon.SetActive(false); // Start with secondary hidden
            }
            else
            {
                Debug.LogWarning($"Could not find secondary weapon at path: {fullPathS}");
            }
        }
    }
}