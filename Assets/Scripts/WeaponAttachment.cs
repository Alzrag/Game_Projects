using System;
using System.Collections.Generic;
using UnityEngine;

namespace BF3AI
{
    public enum AttachmentSlot { Optic = 0, Barrel = 1, Grip = 2 }

    /// <summary>
    /// Put this on an attachment prefab (a scope, a suppressor, a grip...) and drop the prefab in
    ///   Resources/Attachments/(WeaponPrefabName)/     attachments for one weapon
    ///   Resources/Attachments/Common/                 attachments every weapon can use
    /// The deploy menu lists them by slot. When the weapon is equipped the attachment is spawned on it (at the child called Mount Point, or at
    /// the weapon's origin) and its stat changes are applied to the weapon's Blaster.
    /// Multipliers: 1 = no change. Example: a suppressor = Damage Mult 0.9, Spread Mult 0.8.
    /// </summary>
    public class WeaponAttachment : MonoBehaviour
    {
        [Tooltip("Name shown in the menu. Empty = the prefab's name.")]
        public string displayName;
        public AttachmentSlot slot = AttachmentSlot.Optic;
        [Tooltip("Icon for the menu's attachment slot (optional; the name is shown if empty).")]
        public Sprite icon;
        [TextArea] public string description;
        [Tooltip("Name of a child object on the weapon prefab where this is mounted (for example 'OpticMount'). Empty = the weapon's origin.")]
        public string mountPoint;

        [Header("Stat changes (1 = no change)")]
        public float damageMult = 1f;
        public float fireRateMult = 1f;
        [Tooltip("Below 1 = tighter spread = more accurate.")]
        public float spreadMult = 1f;
        public float magazineMult = 1f;
        [Tooltip("Below 1 = faster reload.")]
        public float reloadTimeMult = 1f;
        public float bulletSpeedMult = 1f;
        [Tooltip("Added to the aim zoom (only on weapons that can aim).")]
        public float zoomAdd = 0f;

        public string DisplayName { get { return string.IsNullOrEmpty(displayName) ? Clean(name) : displayName; } }

        public static string Clean(string n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            return n.Replace("(Clone)", "").Replace("_", " ").Trim();
        }

        /// <summary>Applies this attachment's multipliers to a weapon. Call once per attachment.</summary>
        public void ApplyTo(Blaster b)
        {
            if (b == null) return;
            b.damage *= damageMult;
            b.fireRate *= fireRateMult;
            b.spreadX *= spreadMult; b.spreadY *= spreadMult;
            if (b.magazineSize <= 100) b.magazineSize = Mathf.Max(1, Mathf.RoundToInt(b.magazineSize * magazineMult));
            b.reloadTime *= reloadTimeMult;
            b.bulletSpeed *= bulletSpeedMult;
            if (zoomAdd != 0f && b.aimSettings.canAim) b.aimSettings.zoomFactor = Mathf.Max(1f, (b.aimSettings.zoomFactor > 0f ? b.aimSettings.zoomFactor : 2f) + zoomAdd);
        }

        /// <summary>
        /// Spawns the attachments (Resources paths, empty entries skipped) on a weapon that is already in the scene and applies their stat changes.
        /// Only one attachment per slot is used. The weapon's ammo is refilled to the new magazine size.
        /// </summary>
        public static void AttachAll(GameObject weapon, string[] resourcePaths)
        {
            if (weapon == null || resourcePaths == null) return;
            Blaster b = weapon.GetComponentInChildren<Blaster>(true);
            bool[] usedSlot = new bool[8];
            for (int i = 0; i < resourcePaths.Length; i++)
            {
                if (string.IsNullOrEmpty(resourcePaths[i])) continue;
                GameObject prefab = Resources.Load<GameObject>(resourcePaths[i]);
                if (prefab == null) { Debug.LogWarning("[BF3AI] Attachment not found: Resources/" + resourcePaths[i]); continue; }
                WeaponAttachment data = prefab.GetComponent<WeaponAttachment>();
                int slotIndex = data != null ? (int)data.slot : 7;
                if (data != null && usedSlot[slotIndex]) continue;
                usedSlot[slotIndex] = true;

                Transform mount = FindDeep(weapon.transform, data != null ? data.mountPoint : null);
                GameObject inst = Instantiate(prefab, mount != null ? mount : weapon.transform);
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.identity;
                WeaponAttachment live = inst.GetComponent<WeaponAttachment>();
                if (live != null) live.ApplyTo(b);
            }
            if (b != null) b.RefillAmmo();
        }

        public static Transform FindDeep(Transform root, string childName)
        {
            if (root == null || string.IsNullOrEmpty(childName)) return null;
            if (root.name == childName) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform f = FindDeep(root.GetChild(i), childName);
                if (f != null) return f;
            }
            return null;
        }
    }

    /// <summary>A weapon's headline numbers, with attachments folded in. Used by the menu's stat line.</summary>
    public struct WeaponStats
    {
        public float damage, fireRate, spread, reload, zoom;
        public int magazine, pellets;

        public static WeaponStats Of(Blaster b, IList<WeaponAttachment> attachments)
        {
            WeaponStats s = new WeaponStats();
            if (b == null) return s;
            float dmg = b.damage, rate = b.fireRate, sx = b.spreadX, sy = b.spreadY, rel = b.reloadTime;
            int mag = b.magazineSize;
            float zoom = b.aimSettings.canAim ? (b.aimSettings.zoomFactor > 0f ? b.aimSettings.zoomFactor : 2f) : 1f;
            if (attachments != null)
            {
                for (int i = 0; i < attachments.Count; i++)
                {
                    WeaponAttachment a = attachments[i];
                    if (a == null) continue;
                    dmg *= a.damageMult; rate *= a.fireRateMult; sx *= a.spreadMult; sy *= a.spreadMult; rel *= a.reloadTimeMult;
                    if (mag <= 100) mag = Mathf.Max(1, Mathf.RoundToInt(mag * a.magazineMult));
                    if (a.zoomAdd != 0f && b.aimSettings.canAim) zoom = Mathf.Max(1f, zoom + a.zoomAdd);
                }
            }
            s.damage = dmg; s.fireRate = rate; s.spread = (sx + sy) * 0.5f; s.reload = rel; s.magazine = mag; s.zoom = zoom;
            s.pellets = Mathf.Max(1, b.projectilesPerShot);
            return s;
        }

        public override string ToString()
        {
            return "DMG " + damage.ToString("0.#") + (pellets > 1 ? "x" + pellets : "") + "   RATE " + fireRate.ToString("0.#") + "/s   MAG " + (magazine > 100 ? "INF" : magazine.ToString())
                 + "   SPREAD " + spread.ToString("0.00") + "   ZOOM " + zoom.ToString("0.#") + "x";
        }
    }
}
