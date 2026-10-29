using System;
using UnityEngine;

namespace BF3AI
{
    /// <summary>
    /// Range-dependent accuracy. Every unit has a "sweet spot": the range where its class + weapon shoot best. Hit chance is a bell
    /// curve in log-range around it, so it tapers away on BOTH sides (an assault never lands a sniper-range shot, a sniper is
    /// useless at arm's length). The weapon's own stats are folded in ONCE, here, and nowhere else:
    ///   spread      multiplies the whole curve (tight weapon = higher peak, tails stay tiny, so the prime band gains the most)
    ///   zoom        slides the sweet spot outward          (scopes help far away)
    ///   range       slides it a little outward              (long-barrelled weapons)
    ///   pellets     slides it inward and lifts the peak     (shotguns)
    ///   fire rate   widens the curve                        (faster weapons are forgiving across the middle)
    /// The same numbers drive behaviour: AIUnit/Squad read BandLo/BandHi/Fit to choose targets, who closes and who holds the line.
    /// </summary>
    public static class Marksmanship
    {
        public const float HitRadius = 0.4f;     // metres: the effective size of a body at the aim point
        public const float MaxHit = 0.97f;
        public const float MinHit = 0.01f;
        const float HalfZ = 1.1774f;             // z where the bell is at 50%

        static float BaseWidth(UnitClass c) { switch (c) { case UnitClass.Heavy: return 0.80f; case UnitClass.Sniper: return 0.65f; case UnitClass.Officer: return 0.75f; default: return 0.55f; } }
        static float BasePeak(UnitClass c) { switch (c) { case UnitClass.Heavy: return 0.60f; case UnitClass.Sniper: return 0.70f; case UnitClass.Officer: return 0.62f; default: return 0.65f; } }
        public static float ClassZoom(UnitClass c) { return c == UnitClass.Sniper ? 3f : 1f; }
        /// <summary>Seconds of steady aim at one target needed to reach full accuracy.</summary>
        public static float SettleTime(UnitClass c) { switch (c) { case UnitClass.Sniper: return 1.2f; case UnitClass.Heavy: return 0.6f; case UnitClass.Officer: return 0.4f; default: return 0.3f; } }
        static float SettleFloor(UnitClass c) { switch (c) { case UnitClass.Sniper: return 0.3f; case UnitClass.Heavy: return 0.6f; case UnitClass.Officer: return 0.75f; default: return 0.8f; } }
        static float MoveMul(UnitClass c) { switch (c) { case UnitClass.Sniper: return 0.35f; case UnitClass.Heavy: return 0.6f; case UnitClass.Officer: return 0.8f; default: return 0.85f; } }

        /// <summary>Recomputes the sweet spot, width, peak multiplier and the useful band from the profile's class and weapon stats.</summary>
        public static void Bake(ClassProfile p)
        {
            float center = Mathf.Sqrt(Mathf.Max(1f, p.idealMin) * Mathf.Max(1f, p.idealMax));
            float zoomRel = Mathf.Clamp((p.zoom > 0f ? p.zoom : ClassZoom(p.cls)) / ClassZoom(p.cls), 0.4f, 3f);
            center *= Mathf.Pow(zoomRel, 0.6f);
            float classRange = p.refRange > 0f ? p.refRange : p.viewRange * 1.2f;
            if (p.weaponRange > 0f) center *= Mathf.Pow(Mathf.Clamp(p.weaponRange / classRange, 0.5f, 1.8f), 0.5f);
            int pel = Mathf.Max(1, p.pellets);
            center /= Mathf.Clamp(1f + 0.10f * (pel - 1), 1f, 2.2f);
            p.accCenter = Mathf.Clamp(center, 3f, 220f);

            float w = BaseWidth(p.cls);
            float rateRel = p.refRate > 0f ? Mathf.Max(0.05f, p.shotsPerSecond) / p.refRate : 1f;
            w *= Mathf.Clamp(1f + 0.08f * Mathf.Log(rateRel), 0.8f, 1.35f);
            if (pel > 1) w *= 1.1f;
            p.accWidth = Mathf.Clamp(w, 0.3f, 1.5f);

            float refSpread = Mathf.Max(0.2f, p.refSpread);
            float q = 1.2f / (1f + Mathf.Max(0f, p.weaponSpread) / refSpread);
            q *= Mathf.Min(1.3f, 1f + 0.04f * (pel - 1));
            q *= Mathf.Clamp(1f + 0.04f * Mathf.Log(rateRel), 0.9f, 1.15f);
            p.accQ = q;

            p.bandLo = p.accCenter * Mathf.Exp(-HalfZ * p.accWidth);
            p.bandHi = p.accCenter * Mathf.Exp(HalfZ * p.accWidth);
        }

        /// <summary>0..1: how well the unit's class and weapon suit this range (1 = dead centre of its sweet spot).</summary>
        public static float Fit(ClassProfile p, float range)
        {
            if (range <= 0.01f) range = 0.01f;
            float z = Mathf.Log(range / p.accCenter) / p.accWidth;
            return Mathf.Exp(-0.5f * z * z);
        }

        /// <summary>Best-case hit chance at this range for planning (settled, still, calm, average marksman).</summary>
        public static float Rating(ClassProfile p, float range)
        {
            return Mathf.Clamp(BasePeak(p.cls) * p.accQ * Fit(p, range), MinHit, MaxHit);
        }

        /// <summary>Probability that one aimed shot lands on the body, including this moment's condition.</summary>
        public static float HitChance(ClassProfile p, float range, float marksmanship, float settle01, bool moving, float suppression, bool panicking)
        {
            float v = BasePeak(p.cls) * Mathf.Lerp(0.85f, 1.15f, Mathf.Clamp01(marksmanship)) * p.accQ * Fit(p, range);
            v *= Mathf.Lerp(SettleFloor(p.cls), 1f, Mathf.Clamp01(settle01));
            if (moving) v *= MoveMul(p.cls);
            v /= 1f + 1.5f * Mathf.Clamp01(suppression);
            if (panicking) v *= 0.5f;
            return Mathf.Clamp(v, MinHit, MaxHit);
        }

        /// <summary>Angular standard deviation (degrees, per axis) that gives a body-sized target this hit chance at this range.</summary>
        public static float SigmaDeg(float hitChance, float range)
        {
            float p = Mathf.Clamp(hitChance, MinHit, MaxHit);
            float sigmaM = HitRadius / Mathf.Sqrt(-2f * Mathf.Log(1f - p));
            return Mathf.Atan2(sigmaM, Mathf.Max(0.5f, range)) * Mathf.Rad2Deg;
        }
    }
}