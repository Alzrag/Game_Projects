using System;
using System.Collections.Generic;
using UnityEngine;

namespace BF3AI
{
    /// <summary>The numbers a grenade plan needs. Read from the grenade prefab, so a bot plans with the real fuse, radius and bounce of the grenade it carries.</summary>
    public class GrenadeSpec
    {
        public float fuse = 2.5f, radius = 6f, maxDamage = 120f, falloffPower = 1f;
        public float bounciness = 0.4f, friction = 0.5f;
        public float maxHold = 1.6f;
        public bool wallsShield = true;
        public bool detonateOnImpact = false;
        public float armTime = 0.2f, minImpactSpeed = 3f;
        public FriendlyFireMode friendlyFire = FriendlyFireMode.ThrowerOnly;
        public float suppressionRadiusMult = 2f;

        public float HoldLimit { get { return Mathf.Max(0.1f, Mathf.Min(maxHold, fuse - 0.25f)); } }

        /// <summary>Reads the settings from a prefab's GrenadeProjectile (or the global defaults when the prefab is null or has none).</summary>
        public static GrenadeSpec For(GameObject prefab)
        {
            GrenadeSpec s = new GrenadeSpec();
            s.fuse = Grenade.fuse; s.radius = Grenade.radius; s.maxDamage = Grenade.maxDamage;
            GrenadeProjectile gp = prefab != null ? prefab.GetComponent<GrenadeProjectile>() : null;
            if (gp != null)
            {
                s.fuse = gp.fuseTime; s.radius = gp.radius; s.maxDamage = gp.maxDamage; s.falloffPower = gp.falloffPower;
                s.bounciness = gp.bounciness; s.friction = gp.friction; s.maxHold = gp.maxHoldTime;
                s.wallsShield = gp.wallsShield; s.detonateOnImpact = gp.detonateOnImpact; s.armTime = gp.armTime; s.minImpactSpeed = gp.minImpactSpeed;
                s.friendlyFire = gp.friendlyFireMode; s.suppressionRadiusMult = gp.suppressionRadiusMult;
            }
            return s;
        }
    }

    /// <summary>One enemy (or probable enemy position) the planner may try to hit.</summary>
    public struct GrenadeTarget
    {
        public Vector3 pos;       // feet position
        public float hp, maxHp;
        public float weight;      // 1 = we can see it right now, lower = old or uncertain information
        public bool covered;      // sitting in cover: a grenade is the way to flush it out
    }

    /// <summary>A throw the planner liked.</summary>
    public class GrenadePlan
    {
        public Vector3 from;
        public Vector3 velocity;
        public Vector3 endPos;          // where it will go off
        public float cook;              // seconds to hold it first (0 = throw immediately)
        public float flightTime;        // seconds from release until it settles
        public int bounces;             // wall ricochets used (0 or 1)
        public float value;             // roughly "enemies worth of damage": 1.0 is about one dead enemy
        public float danger;            // expected harm to the thrower and (if friendly fire is on) the team
        public string kind;             // direct / over the cover / bank shot
        public int hits;                // enemies inside the blast
        public override string ToString()
        {
            return kind + " to " + endPos.ToString("F0") + "  value " + value.ToString("F2") + "  hits " + hits + "  cook " + cook.ToString("F1") + " s  bounces " + bounces;
        }
    }

    /// <summary>
    /// Works out where a bot should throw a grenade. It looks at the enemies it knows about, finds the spot that does the most damage, and then
    /// checks several kinds of throw by simulating them with real gravity and real bounces:
    ///   direct       a lob onto the enemy
    ///   over cover   a lob that lands a few metres BEHIND the enemy, to catch it from the back and flush it out
    ///   bank shot    one level of ricochet: aims at the mirror image of the target in a nearby wall so the grenade bounces off it
    ///                and lands behind cover, around a corner or inside a room it cannot see into
    /// It also works out how long to HOLD (cook) the grenade so it goes off just after it lands instead of giving the enemy time to run.
    /// </summary>
    public static class GrenadePlanner
    {
        /// <summary>Most flight simulations per Plan call (each is about 20-60 short raycasts).</summary>
        public static int maxSims = 64;
        public static float maxThrowSpeed = 18f;
        /// <summary>Seconds between the grenade settling and going off.</summary>
        public static float reactionDelay = 0.3f;

        static readonly Vector3[] tracePos = new Vector3[160];
        static readonly float[] traceTime = new float[160];
        static readonly float[] DirectArcs = { 38f, 52f, 68f };
        static readonly float[] BankArcs = { 35f, 55f };
        static readonly Vector3[] WallProbes = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };

        /// <summary>
        /// Finds the best throw, or null if nothing reaches anyone. 'allowCook' false means throw immediately (cook 0).
        /// 'urgent' also forces an immediate throw. The caller decides whether plan.value is worth a grenade.
        /// </summary>
        public static GrenadePlan Plan(Vector3 from, Combatant thrower, List<GrenadeTarget> targets, GrenadeSpec spec, bool allowCook, bool urgent)
        {
            if (targets == null || targets.Count == 0 || spec == null) return null;

            // 1. candidate aim spots: the middle of the biggest groups, plus a point behind each
            List<Vector3> spots = new List<Vector3>(8);
            List<Vector3> centres = new List<Vector3>(3);
            List<float> centreScore = new List<float>(3);
            for (int i = 0; i < targets.Count; i++)
            {
                Vector3 sum = Vector3.zero; float wsum = 0f;
                for (int j = 0; j < targets.Count; j++)
                {
                    if ((targets[j].pos - targets[i].pos).sqrMagnitude > spec.radius * spec.radius * 0.36f) continue;
                    sum += targets[j].pos * targets[j].weight; wsum += targets[j].weight;
                }
                if (wsum <= 0.01f) continue;
                Vector3 c = sum / wsum;
                bool dup = false;
                for (int k = 0; k < centres.Count; k++) if ((centres[k] - c).sqrMagnitude < 6f) { dup = true; if (wsum > centreScore[k]) { centres[k] = c; centreScore[k] = wsum; } }
                if (!dup) { centres.Add(c); centreScore.Add(wsum); }
            }
            // best 3 groups
            for (int pass = 0; pass < 3 && centres.Count > 0; pass++)
            {
                int best = 0; for (int k = 1; k < centres.Count; k++) if (centreScore[k] > centreScore[best]) best = k;
                Vector3 c = centres[best]; centres.RemoveAt(best); centreScore.RemoveAt(best);
                Vector3 away = c - from; away.y = 0f; away = away.sqrMagnitude > 0.01f ? away.normalized : Vector3.forward;
                spots.Add(c);
                spots.Add(c + away * 2.5f);          // behind them
            }

            // 2. try throws at each spot
            GrenadePlan best1 = null;
            int sims = 0;
            for (int si = 0; si < spots.Count && sims < maxSims; si++)
            {
                Vector3 spot = spots[si];
                bool behind = (si % 2) == 1;
                for (int a = 0; a < DirectArcs.Length && sims < maxSims; a++)
                    TryThrow(from, spot, DirectArcs[a], behind ? "over the cover" : "direct", thrower, targets, spec, allowCook && !urgent, ref best1, ref sims);

                // bank shots: mirror the spot in each nearby wall
                if (behind) continue;                                       // (banks are tried for the direct spot only)
                for (int w = 0; w < WallProbes.Length && sims < maxSims; w++)
                {
                    RaycastHit wh;
                    Vector3 o = spot + Vector3.up * 1f;
                    if (!Physics.Raycast(o, WallProbes[w], out wh, 8f, AIWorld.Obstacles, QueryTriggerInteraction.Ignore)) continue;
                    if (wh.distance < 1.2f || Mathf.Abs(wh.normal.y) > 0.5f) continue;
                    Vector3 n = wh.normal;
                    Vector3 mirror = spot - 2f * Vector3.Dot(spot - wh.point, n) * n;   // the target as seen "through" the wall
                    mirror.y = spot.y;
                    // the throw has to travel towards the wall first: the mirror image must be roughly beyond the wall from us
                    if (Vector3.Dot((wh.point - from).normalized, (mirror - from).normalized) < 0.2f) continue;
                    for (int a = 0; a < BankArcs.Length && sims < maxSims; a++)
                        TryThrow(from, mirror, BankArcs[a], "bank shot", thrower, targets, spec, allowCook && !urgent, ref best1, ref sims);
                }
            }
            return best1;
        }

        static void TryThrow(Vector3 from, Vector3 aim, float arc, string kind, Combatant thrower, List<GrenadeTarget> targets, GrenadeSpec spec,
                             bool allowCook, ref GrenadePlan best, ref int sims)
        {
            Vector3 vel;
            if (!GrenadeProjectile.SolveThrow(from, aim, arc, out vel)) return;
            if (vel.magnitude > maxThrowSpeed) return;
            sims++;
            int n;
            GrenadeProjectile.FlightResult r = GrenadeProjectile.Simulate(from, vel, spec.fuse, spec.bounciness, spec.friction, spec.detonateOnImpact, spec.armTime, spec.minImpactSpeed, 1, tracePos, traceTime);
            n = r.traceCount;
            if (r.unreliable || r.wallBounces > 1) return;
            if (kind == "bank shot" && r.wallBounces == 0) kind = "direct";         // it never touched the wall: it is just a lob

            float cook = 0f; Vector3 endPos; float flight;
            if (r.impact) { endPos = r.endPos; flight = r.endTime; }
            else
            {
                if (r.landTime < 0f || r.airburst) return;                           // it would still be in the air when the fuse runs out
                // go off a moment after it first lands: hold the grenade for the rest of the fuse (up to the hold limit)
                if (allowCook) cook = Mathf.Clamp(spec.fuse - r.landTime - reactionDelay, 0f, spec.HoldLimit);
                float det = spec.fuse - cook;                                         // seconds after release
                if (det < r.landTime + 0.05f) return;
                endPos = GrenadeProjectile.SampleTrace(tracePos, traceTime, n, det);
                flight = r.landTime;
            }

            int hits; float danger;
            float value = Evaluate(endPos, thrower, targets, spec, out hits, out danger);
            if (r.wallBounces == 1) value *= 0.88f;                                   // ricochets are less certain
            float score = value - danger;
            if (best == null || score > best.value - best.danger)
            {
                if (best == null) best = new GrenadePlan();
                best.from = from; best.velocity = vel; best.endPos = endPos; best.cook = cook; best.flightTime = flight; best.bounces = r.wallBounces;
                best.value = value; best.danger = danger; best.kind = kind; best.hits = hits;
            }
        }

        /// <summary>How much good a blast at 'pos' would do, and how much harm to the thrower (and teammates if friendly fire is on).</summary>
        public static float Evaluate(Vector3 pos, Combatant thrower, List<GrenadeTarget> targets, GrenadeSpec spec, out int hits, out float danger)
        {
            float total = 0f; hits = 0; danger = 0f;
            float r = Mathf.Max(0.5f, spec.radius), sr = r * Mathf.Max(1f, spec.suppressionRadiusMult);
            Vector3 up = Vector3.up * 0.2f;
            for (int i = 0; i < targets.Count; i++)
            {
                GrenadeTarget t = targets[i];
                Vector3 chest = t.pos + Vector3.up * 1.1f;
                float d = Vector3.Distance(pos, chest);
                if (d > sr) continue;
                bool shielded = spec.wallsShield && Physics.Linecast(pos + up, chest, AIWorld.Obstacles, QueryTriggerInteraction.Ignore);
                if (d <= r && !shielded)
                {
                    float k = Mathf.Clamp01(1f - d / r);
                    float dmg = spec.maxDamage * (spec.falloffPower <= 0f ? 1f : Mathf.Pow(k, spec.falloffPower));
                    float hp = Mathf.Max(1f, t.hp), max = Mathf.Max(1f, t.maxHp);
                    float v = Mathf.Min(dmg, hp) / max;
                    if (dmg >= hp) v += 0.35f;                                         // a kill
                    if (t.covered) v += 0.15f;                                         // flushes it out of cover
                    total += v * t.weight; hits++;
                }
                else if (d <= sr)
                {
                    total += 0.08f * t.weight * (1f - d / sr);                         // suppression: they duck even if it misses
                }
            }

            if (thrower != null)
            {
                if (spec.friendlyFire != FriendlyFireMode.None)
                    danger += HarmTo(pos, thrower.Chest, spec) * 3f;                   // hurting ourselves is very bad
                if (spec.friendlyFire == FriendlyFireMode.Everyone)
                {
                    for (int i = 0; i < Combatant.All.Count; i++)
                    {
                        Combatant c = Combatant.All[i];
                        if (c == null || c == thrower || !c.IsAlive || c.team != thrower.team) continue;
                        danger += HarmTo(pos, c.Chest, spec) * 1.5f;
                    }
                }
            }
            return total;
        }

        static float HarmTo(Vector3 pos, Vector3 chest, GrenadeSpec spec)
        {
            float d = Vector3.Distance(pos, chest);
            float r = spec.radius * 1.1f;
            if (d > r) return 0f;
            if (spec.wallsShield && Physics.Linecast(pos + Vector3.up * 0.2f, chest, AIWorld.Obstacles, QueryTriggerInteraction.Ignore)) return 0f;
            return Mathf.Clamp01(spec.maxDamage * (1f - d / r) / 100f) + 0.3f;
        }
    }
}
