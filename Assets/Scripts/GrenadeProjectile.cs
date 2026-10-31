using System;
using System.Collections.Generic;
using UnityEngine;

namespace BF3AI
{
    /// <summary>Who a grenade's blast can hurt besides the enemy.</summary>
    public enum FriendlyFireMode
    {
        [Tooltip("Never hurts the thrower or the thrower's team.")] None,
        [Tooltip("Hurts ONLY the one who threw it (stand too close to your own grenade and it hurts). Teammates are safe.")] ThrowerOnly,
        [Tooltip("Hurts everyone in range.")] Everyone
    }

    /// <summary>
    /// A thrown grenade. Put this on a prefab (or let the game generate one: BF3AI menu > Create Grenade Prefab builds a ready-made prefab).
    /// It works for bots and, later, for player abilities: anything can call GrenadeProjectile.ThrowAt(...) or GrenadeProjectile.Launch(...).
    /// The grenade remembers its thrower and the thrower's TEAM. By default it hurts enemies and the THROWER only (never teammates),
    /// even if the thrower dies while it is in the air.
    /// It can be COOKED: held with the fuse already running, then thrown (see BeginHold / Release). Held too long it is simply put away,
    /// it never goes off in the thrower's hand. It can be SHOT out of the air (bolts and bot fire detonate it early). It leaves a glowing
    /// trail and blinks faster as the fuse runs down.
    /// </summary>
    public class GrenadeProjectile : MonoBehaviour
    {
        public static readonly List<GrenadeProjectile> All = new List<GrenadeProjectile>();

        [Header("Fuse")]
        [Tooltip("Seconds after being thrown until it explodes (always applies, even with Detonate On Impact).")]
        public float fuseTime = 2.5f;
        [Tooltip("Explode the moment it hits something hard, instead of bouncing until the fuse runs out (a frag / impact grenade).")]
        public bool detonateOnImpact = false;
        [Tooltip("Impact detonation is ignored for this many seconds after the throw, so it cannot go off in the thrower's hand.")]
        public float armTime = 0.2f;
        [Tooltip("A hit softer than this (m/s) just bounces.")]
        public float minImpactSpeed = 3f;

        [Header("Blast")]
        public float radius = 6f;
        public float maxDamage = 120f;
        [Tooltip("1 = damage falls off in a straight line from the centre to the edge. Higher = most of the damage is near the centre. 0 = full damage everywhere inside.")]
        public float falloffPower = 1f;
        [Tooltip("Solid walls between the blast and a unit protect it.")]
        public bool wallsShield = true;
        [Tooltip("Who the blast hurts besides enemies. Thrower Only (default): only the one who threw it, never teammates.")]
        public FriendlyFireMode friendlyFireMode = FriendlyFireMode.ThrowerOnly;
        [Tooltip("Enemy bots within Radius x this are suppressed (they duck and stop shooting back) even if the blast misses them. 0 = off.")]
        public float suppressionRadiusMult = 2f;
        [Tooltip("How strongly they are suppressed at the centre (0 to 1).")]
        public float suppressionAmount = 0.5f;
        [Tooltip("How far bots hear the explosion, in metres.")]
        public float soundRadius = 80f;
        [Tooltip("Optional. Shoves physics objects in the blast radius (0 = off).")]
        public float physicsForce = 0f;

        [Header("Cooking (hold before throwing)")]
        [Tooltip("The fuse starts the moment the grenade is picked up to throw. If it is held longer than this it is PUT AWAY (returned, never explodes in the hand). Always kept shorter than the fuse.")]
        public float maxHoldTime = 1.6f;

        [Header("Shooting it out of the air")]
        [Tooltip("A bolt or bullet that hits the grenade in flight detonates it immediately. The grenade's collider must be on a layer the bolts hit.")]
        public bool canBeShot = true;

        [Header("Visibility (glow, blink and trail)")]
        [Tooltip("Adds a glowing core and a small light if the prefab has none. The core blinks faster as the fuse runs down.")]
        public bool autoGlow = true;
        public Color glowColor = new Color(1f, 0.35f, 0.1f, 1f);
        public float glowLightRange = 3.5f;
        public float glowLightIntensity = 2f;
        [Tooltip("Adds a trail if the prefab has none, so the grenade is easy to follow in the air.")]
        public bool autoTrail = true;
        public float trailTime = 0.5f;
        public float trailWidth = 0.14f;
        public Color trailColor = new Color(1f, 0.55f, 0.2f, 0.85f);

        [Header("Physics")]
        [Range(0f, 1f)] public float bounciness = 0.4f;
        [Range(0f, 1f)] public float friction = 0.5f;
        [Tooltip("The grenade ignores collisions with the thrower's own body, so it cannot knock the thrower around when released.")]
        public bool ignoreThrowerCollision = true;

        [Header("Effects")]
        [Tooltip("Optional explosion effect (particles, light...). Spawned at the blast and destroyed after Effect Lifetime.")]
        public GameObject explosionEffect;
        [Tooltip("With no Explosion Effect assigned, show a simple generated fireball and flash of light.")]
        public bool spawnDefaultEffect = true;
        public float effectLifetime = 4f;
        public AudioClip explosionClip;
        public AudioClip bounceClip;
        [Range(0f, 1f)] public float volume = 1f;

        /// <summary>Who threw it (kill credit). May become null if the thrower is destroyed.</summary>
        [NonSerialized] public Combatant owner;
        [NonSerialized] public int team;
        [NonSerialized] public bool hasTeam;

        float born;
        bool detonated;
        float nextBounceSound;
        bool held;
        float holdStart;
        Transform hand;
        Collider[] heldColliders;
        TrailRenderer trail;
        Renderer glowRenderer;
        Light glowLight;
        float lightBase;

        /// <summary>Raised just before a held grenade is put away (held too long, or cancelled). Lets the thrower give the grenade back.</summary>
        public event Action<GrenadeProjectile> OnPutAway;

        public bool HasDetonated { get { return detonated; } }
        public bool IsHeld { get { return held; } }
        /// <summary>Seconds left on the fuse (the fuse runs while the grenade is held).</summary>
        public float TimeLeft { get { return Mathf.Max(0f, born + fuseTime - Time.time); } }
        /// <summary>Seconds it has been cooked so far (0 once thrown... use FuseElapsed for the total).</summary>
        public float HeldTime { get { return held ? Time.time - holdStart : 0f; } }
        /// <summary>The longest it can be held before it is put away.</summary>
        public float HoldLimit { get { return Mathf.Max(0.1f, Mathf.Min(maxHoldTime, fuseTime - 0.25f)); } }
        public float HoldFraction { get { return held ? Mathf.Clamp01(HeldTime / HoldLimit) : 0f; } }
        public Vector3 Velocity
        {
            get
            {
                Rigidbody rb = GetComponent<Rigidbody>();
                if (rb == null) return Vector3.zero;
#if UNITY_6000_0_OR_NEWER
                return rb.linearVelocity;
#else
                return rb.velocity;
#endif
            }
        }

        /// <summary>Tags the grenade with its thrower and the thrower's team.</summary>
        public void SetOwner(Combatant thrower)
        {
            owner = thrower;
            hasTeam = thrower != null;
            team = thrower != null ? thrower.team : 0;
        }

        void Awake()
        {
            born = Time.time;
            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;   // a fast throw must not tunnel through thin walls
#if UNITY_6000_0_OR_NEWER
            rb.angularDamping = Mathf.Max(rb.angularDamping, 3f);       // a grenade stops rolling instead of trundling across the map
#else
            rb.angularDrag = Mathf.Max(rb.angularDrag, 3f);
#endif

            Collider[] cols = GetComponentsInChildren<Collider>(true);
            if (cols.Length > 0 && bounciness >= 0f)
            {
#if UNITY_6000_0_OR_NEWER
                PhysicsMaterial pm = new PhysicsMaterial("BF3AI_Grenade");
#else
                PhysicMaterial pm = new PhysicMaterial("BF3AI_Grenade");
#endif
                pm.bounciness = bounciness; pm.dynamicFriction = friction; pm.staticFriction = friction;
                for (int i = 0; i < cols.Length; i++) if (cols[i] != null) cols[i].sharedMaterial = pm;
            }
            SetupVisuals();
        }

        void SetupVisuals()
        {
            trail = GetComponentInChildren<TrailRenderer>();
            if (trail == null && autoTrail) trail = AddTrail(gameObject, null, trailColor, trailTime, trailWidth);
            Transform g = transform.Find("BF3AI_Glow");
            if (g == null && autoGlow) g = AddGlow(gameObject, null, glowColor, glowLightRange, glowLightIntensity);
            if (g != null) { glowRenderer = g.GetComponent<Renderer>(); glowLight = g.GetComponent<Light>(); if (glowLight != null) lightBase = glowLight.intensity; }
        }

        static TrailRenderer AddTrail(GameObject go, Material mat, Color c, float time, float width)
        {
            TrailRenderer tr = go.AddComponent<TrailRenderer>();
            tr.time = time; tr.startWidth = width; tr.endWidth = 0f;
            tr.startColor = c; tr.endColor = new Color(c.r, c.g, c.b, 0f);
            if (mat != null) tr.sharedMaterial = mat;
            else { Shader sh = Shader.Find("Sprites/Default"); if (sh != null) tr.material = new Material(sh); }
            return tr;
        }

        static Transform AddGlow(GameObject go, Material mat, Color c, float lightRange, float lightIntensity)
        {
            GameObject core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            core.name = "BF3AI_Glow";
            Collider col = core.GetComponent<Collider>(); if (col != null) Destroy(col);          // purely visual
            core.transform.SetParent(go.transform, false);
            core.transform.localPosition = Vector3.zero;
            core.transform.localScale = Vector3.one * 0.7f;
            Renderer r = core.GetComponent<Renderer>();
            if (r != null)
            {
                if (mat != null) r.sharedMaterial = mat;
                else { Shader sh = Shader.Find("Sprites/Default"); if (sh != null) { Material m = new Material(sh); m.color = c; r.material = m; } }
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            Light l = core.AddComponent<Light>();
            l.type = LightType.Point; l.color = c; l.range = lightRange; l.intensity = lightIntensity;
            return core.transform;
        }

        void OnEnable() { All.Add(this); }
        void OnDisable() { All.Remove(this); }

        void Update()
        {
            if (detonated) return;
            if (held)
            {
                // The holder died or the hand is gone: drop it (the fuse keeps running).
                if ((owner != null && !owner.IsAlive) || hand == null) { Release(Vector3.zero); return; }
                if (Time.time - holdStart >= HoldLimit) { PutAway(); return; }   // held too long: put away, never explodes in the hand
                UpdateBlink();
                return;
            }
            if (Time.time >= born + fuseTime) { Detonate(); return; }
            UpdateBlink();
        }

        void UpdateBlink()
        {
            if (glowRenderer == null && glowLight == null) return;
            float left = TimeLeft / Mathf.Max(0.1f, fuseTime);
            float freq = Mathf.Lerp(3f, 22f, 1f - left);                 // blinks faster as the fuse runs down
            bool on = Mathf.Sin(Time.time * freq * 2f * Mathf.PI) > -0.2f || left < 0.12f;
            if (glowRenderer != null) glowRenderer.enabled = on;
            if (glowLight != null) glowLight.intensity = on ? lightBase * Mathf.Lerp(0.7f, 1.6f, 1f - left) : 0f;
        }

        // =============================================================================== cooking
        /// <summary>
        /// Picks the grenade up to throw: parents it to 'hand', switches physics off and STARTS the fuse (cooking).
        /// Call Release(velocity) to throw it. If it is held longer than HoldLimit it is put away instead (OnPutAway fires).
        /// </summary>
        public void Hold(Combatant thrower, Transform handTransform)
        {
            SetOwner(thrower);
            held = true; hand = handTransform; holdStart = Time.time; born = Time.time;
            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
#if UNITY_6000_0_OR_NEWER
                rb.linearVelocity = Vector3.zero;
#else
                rb.velocity = Vector3.zero;
#endif
                rb.isKinematic = true;
            }
            heldColliders = GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < heldColliders.Length; i++) if (heldColliders[i] != null) heldColliders[i].enabled = false;
            if (trail != null) { trail.emitting = false; trail.Clear(); }
            if (hand != null) { transform.SetParent(hand, false); transform.localPosition = Vector3.zero; }
        }

        /// <summary>Throws a held grenade with this velocity. The fuse has been running since it was picked up.</summary>
        public void Release(Vector3 velocity)
        {
            if (!held) return;
            held = false;
            transform.SetParent(null);
            if (hand != null) transform.position = hand.position;
            if (heldColliders != null) for (int i = 0; i < heldColliders.Length; i++) if (heldColliders[i] != null) heldColliders[i].enabled = true;
            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false;
#if UNITY_6000_0_OR_NEWER
                rb.linearVelocity = velocity;
#else
                rb.velocity = velocity;
#endif
            }
            IgnoreThrower();
            if (trail != null) { trail.Clear(); trail.emitting = true; }
        }

        /// <summary>Puts a held grenade away without throwing it. It does not explode. The thrower should get the grenade back.</summary>
        public void PutAway()
        {
            if (!held) return;
            held = false;
            try { if (OnPutAway != null) OnPutAway(this); }
            catch (Exception e) { BF3Debug.Exception("GrenadeProjectile.PutAway", e, null, null); }
            detonated = true;                                            // so nothing can set it off after this
            Destroy(gameObject);
        }

        void IgnoreThrower()
        {
            if (!ignoreThrowerCollision || owner == null) return;
            Collider[] mine = GetComponentsInChildren<Collider>(true);
            Collider[] theirs = owner.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < mine.Length; i++)
                for (int j = 0; j < theirs.Length; j++)
                    if (mine[i] != null && theirs[j] != null) Physics.IgnoreCollision(mine[i], theirs[j], true);
        }

        // =============================================================================== shooting
        /// <summary>Called by bolts and bot fire. Detonates the grenade now if it is in the air. Returns true if it went off.</summary>
        public bool OnShot(Combatant shooter)
        {
            if (detonated || held || !canBeShot) return false;
            BF3Debug.I(DbgCat.Grenade, (shooter != null ? shooter.name : "Something") + " shot a grenade out of the air at " + transform.position.ToString("F0") + ".");
            Detonate();
            return true;
        }

        /// <summary>For bolt scripts: true if this collider belongs to a grenade that was shot (and the bolt is used up).</summary>
        public static bool TryShoot(Collider c, Combatant shooter, out bool wasGrenade)
        {
            wasGrenade = false;
            GrenadeProjectile g = c != null ? c.GetComponentInParent<GrenadeProjectile>() : null;
            if (g == null) return false;
            wasGrenade = true;
            return g.OnShot(shooter);
        }

        void OnCollisionEnter(Collision c)
        {
            if (detonated || held) return;
            float speed = c.relativeVelocity.magnitude;
            if (detonateOnImpact && Time.time - born >= armTime && speed >= minImpactSpeed) { Detonate(); return; }
            if (bounceClip != null && speed > 1.5f && Time.time >= nextBounceSound)
            {
                nextBounceSound = Time.time + 0.15f;
                AudioSource.PlayClipAtPoint(bounceClip, transform.position, volume * Mathf.Clamp01(speed / 8f));
            }
        }

        /// <summary>Explodes now. Safe to call more than once.</summary>
        public void Detonate()
        {
            if (detonated) return;
            detonated = true; held = false;
            Vector3 pos = transform.position;
            try { Explode(pos); }
            catch (Exception e) { BF3Debug.Exception("GrenadeProjectile.Detonate", e, null, null); }
            Destroy(gameObject);
        }

        void Explode(Vector3 pos)
        {
            SoundBus.Emit(pos, soundRadius, hasTeam ? team : -1, SoundType.Explosion);
            int shielded;
            int hits = Blast(pos, owner, hasTeam, team, radius, maxDamage, falloffPower, wallsShield, friendlyFireMode, suppressionRadiusMult, suppressionAmount, physicsForce, out shielded);
            BF3Debug.I(DbgCat.Grenade, "Grenade exploded at " + pos.ToString("F0") + ": hit " + hits + ", shielded by walls " + shielded + ".");

            if (explosionEffect != null)
            {
                GameObject fx = Instantiate(explosionEffect, pos, Quaternion.identity);
                Destroy(fx, Mathf.Max(0.1f, effectLifetime));
            }
            else if (spawnDefaultEffect) GrenadeExplosionFx.Spawn(pos, radius);

            if (explosionClip != null) AudioSource.PlayClipAtPoint(explosionClip, pos, volume);
        }

        // =============================================================================== blast
        static readonly List<Combatant> copy = new List<Combatant>();

        /// <summary>
        /// Damages and suppresses everything near a point. Returns the number of units hurt; 'shielded' counts those saved by a wall.
        /// Usable on its own (mines, vehicle explosions, abilities).
        /// </summary>
        public static int Blast(Vector3 pos, Combatant owner, bool hasTeam, int team, float radius, float maxDamage, float falloffPower,
                                bool wallsShield, bool friendlyFire, float suppressionRadiusMult, float suppressionAmount, float physicsForce, out int shielded)
        {
            return Blast(pos, owner, hasTeam, team, radius, maxDamage, falloffPower, wallsShield, friendlyFire ? FriendlyFireMode.Everyone : FriendlyFireMode.None,
                         suppressionRadiusMult, suppressionAmount, physicsForce, out shielded);
        }

        /// <summary>Same, with the three-way friendly fire choice: None, ThrowerOnly (the thrower is hurt, teammates are not) or Everyone.</summary>
        public static int Blast(Vector3 pos, Combatant owner, bool hasTeam, int team, float radius, float maxDamage, float falloffPower,
                                bool wallsShield, FriendlyFireMode ff, float suppressionRadiusMult, float suppressionAmount, float physicsForce, out int shielded)
        {
            int hits = 0; shielded = 0;
            copy.Clear(); copy.AddRange(Combatant.All);          // TakeDamage can deactivate units and change the list
            for (int i = 0; i < copy.Count; i++)
            {
                Combatant c = copy[i];
                if (c == null || !c.IsAlive) continue;
                bool isThrower = owner != null && c == owner;
                bool mate = hasTeam && c.team == team && !isThrower;
                if (ff == FriendlyFireMode.None && (isThrower || mate)) continue;
                if (ff == FriendlyFireMode.ThrowerOnly && mate) continue;
                Vector3 p = c.Chest;
                float d = Vector3.Distance(pos, p);
                if (d > radius) continue;
                if (wallsShield && Physics.Linecast(pos + Vector3.up * 0.2f, p, AIWorld.Obstacles, QueryTriggerInteraction.Ignore)) { shielded++; continue; }
                float k = Mathf.Clamp01(1f - d / Mathf.Max(0.01f, radius));
                float dmg = maxDamage * (falloffPower <= 0f ? 1f : Mathf.Pow(k, falloffPower));
                c.TakeDamage(dmg, owner);
                hits++;
            }

            if (suppressionRadiusMult > 0f && suppressionAmount > 0f)
            {
                float sr = radius * suppressionRadiusMult;
                foreach (AIUnit a in AIUnit.All)
                {
                    if (a == null || !a.IsAlive) continue;
                    if (ff != FriendlyFireMode.Everyone && hasTeam && a.team == team) continue;
                    float d = Vector3.Distance(pos, a.transform.position);
                    if (d < sr) a.AddSuppression(suppressionAmount * (1f - d / sr));
                }
            }

            if (physicsForce > 0f)
            {
                Collider[] near = Physics.OverlapSphere(pos, radius);
                for (int i = 0; i < near.Length; i++)
                {
                    Rigidbody rb = near[i] != null ? near[i].attachedRigidbody : null;
                    if (rb != null && !rb.isKinematic) rb.AddExplosionForce(physicsForce, pos, radius, 0.5f, ForceMode.Impulse);
                }
            }
            return hits;
        }

        /// <summary>
        /// Creates a grenade IN THE HAND of 'thrower' with the fuse already running (cooking). Call Release(velocity) on the result to throw it.
        /// If it is held longer than its Hold Limit it puts itself away (OnPutAway) instead of exploding. prefab may be null (built-in grenade).
        /// </summary>
        public static GrenadeProjectile BeginHold(GameObject prefab, Combatant thrower, Transform hand)
        {
            GameObject go = prefab != null ? Instantiate(prefab, hand != null ? hand.position : Vector3.zero, Quaternion.identity) : BuildDefault(null, null, null);
            GrenadeProjectile gp = go.GetComponent<GrenadeProjectile>();
            if (gp == null)
            {
                gp = go.AddComponent<GrenadeProjectile>();
                gp.fuseTime = Grenade.fuse; gp.radius = Grenade.radius; gp.maxDamage = Grenade.maxDamage;
            }
            gp.Hold(thrower, hand);
            return gp;
        }

        // =============================================================================== flight prediction
        /// <summary>The predicted end of a throw. Used by the bots to plan and by anything that wants to draw a trajectory.</summary>
        public struct FlightResult
        {
            public Vector3 endPos;          // where it ends up (at rest, or where it is when the time runs out)
            public float endTime;           // seconds after release
            public float landTime;          // seconds after release when it first touches the floor (-1 = never)
            public int wallBounces;         // bounces off walls (not the floor): the "ricochet" count
            public int floorBounces;
            public bool airburst;           // still in the air when the time ran out
            public bool unreliable;         // more wall bounces than we model: the end point is a guess
            public bool impact;             // ended by an impact detonation
            public Vector3 firstWallPoint;  // where the first wall bounce happened (zero if none)
            public int traceCount;          // samples written to the trace arrays (if given)
        }

        /// <summary>Position at time t from a trace recorded by Simulate (it stays where it came to rest).</summary>
        public static Vector3 SampleTrace(Vector3[] pts, float[] ts, int n, float t)
        {
            if (n <= 0) return Vector3.zero;
            if (t <= ts[0]) return pts[0];
            for (int i = 1; i < n; i++)
            {
                if (ts[i] >= t)
                {
                    float k = Mathf.Clamp01((t - ts[i - 1]) / Mathf.Max(0.0001f, ts[i] - ts[i - 1]));
                    return Vector3.Lerp(pts[i - 1], pts[i], k);
                }
            }
            return pts[n - 1];
        }

        /// <summary>
        /// Simulates a throw with the same gravity, bounciness and friction the real grenade uses. Models up to 'maxWallBounces' bounces
        /// off walls (one level of ricochet) and floor bounces until it settles. Runs for 'maxTime' seconds (the fuse) unless an impact grenade
        /// goes off first. If 'tracePos'/'traceTime' are given they receive the path (so a planner can ask "where is it after 2.1 s?").
        /// </summary>
        public static FlightResult Simulate(Vector3 from, Vector3 velocity, float maxTime, float bounciness, float friction,
                                            bool impactDetonates, float armTime, float minImpactSpeed, int maxWallBounces)
        {
            return Simulate(from, velocity, maxTime, bounciness, friction, impactDetonates, armTime, minImpactSpeed, maxWallBounces, null, null);
        }

        public static FlightResult Simulate(Vector3 from, Vector3 velocity, float maxTime, float bounciness, float friction,
                                            bool impactDetonates, float armTime, float minImpactSpeed, int maxWallBounces,
                                            Vector3[] tracePos, float[] traceTime)
        {
            FlightResult r = new FlightResult(); r.landTime = -1f;
            Vector3 g = Physics.gravity, pos = from, v = velocity;
            float t = 0f;
            const float dt = 0.06f;
            int guard = 0, cap = (tracePos != null && traceTime != null) ? Mathf.Min(tracePos.Length, traceTime.Length) : 0;
            if (cap > 0) { tracePos[0] = pos; traceTime[0] = 0f; r.traceCount = 1; }
            bool rest = false;
            while (t < maxTime && guard++ < 300 && !rest)
            {
                float step = Mathf.Min(dt, maxTime - t);
                Vector3 vNew = v + g * step;
                Vector3 next = pos + v * step + 0.5f * g * step * step;
                Vector3 seg = next - pos; float len = seg.magnitude;
                RaycastHit h;
                if (len > 1e-4f && Physics.Raycast(pos, seg / len, out h, len + 0.1f, AIWorld.Obstacles, QueryTriggerInteraction.Ignore))
                {
                    float frac = Mathf.Clamp01(h.distance / Mathf.Max(0.0001f, len + 0.1f));
                    float tHit = t + step * frac;
                    float speed = vNew.magnitude;
                    Vector3 n = h.normal;
                    Vector3 hitPos = h.point + n * 0.1f;
                    if (impactDetonates && tHit >= armTime && speed >= minImpactSpeed) { r.endPos = hitPos; r.endTime = tHit; r.impact = true; if (r.landTime < 0f) r.landTime = tHit; return r; }
                    bool floor = n.y > 0.7f;
                    if (!floor)
                    {
                        r.wallBounces++;
                        if (r.firstWallPoint == Vector3.zero) r.firstWallPoint = h.point;
                        if (r.wallBounces > maxWallBounces) { r.unreliable = true; r.endPos = hitPos; r.endTime = tHit; return r; }
                    }
                    else { r.floorBounces++; if (r.landTime < 0f) r.landTime = tHit; }
                    // bounce: the normal part is reflected and scaled by the bounciness; friction removes tangential speed in proportion to the impact
                    float vnMag = Mathf.Abs(Vector3.Dot(vNew, n));
                    Vector3 vn = Vector3.Dot(vNew, n) * n;
                    Vector3 vt = vNew - vn;
                    float vtMag = vt.magnitude;
                    if (vtMag > 1e-4f) vt *= Mathf.Max(0f, 1f - Mathf.Clamp01(friction) * (1f + Mathf.Clamp01(bounciness)) * vnMag / vtMag);
                    vNew = vt - vn * Mathf.Clamp01(bounciness);
                    pos = hitPos; v = vNew; t = tHit;
                    if (cap > 0 && r.traceCount < cap) { tracePos[r.traceCount] = pos; traceTime[r.traceCount] = t; r.traceCount++; }
                    if (floor && (vNew.y < 1.4f || r.floorBounces >= 4)) { rest = true; }
                }
                else
                {
                    pos = next; v = vNew; t += step;
                    if (cap > 0 && r.traceCount < cap) { tracePos[r.traceCount] = pos; traceTime[r.traceCount] = t; r.traceCount++; }
                }
            }
            if (rest)
            {
                // a short roll after it stops bouncing, then it sits there
                Vector3 dir = new Vector3(v.x, 0f, v.z); float roll = Mathf.Clamp(dir.magnitude * 0.35f, 0f, 1.2f); dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.zero;
                RaycastHit sh;
                if (dir != Vector3.zero && Physics.Raycast(pos + Vector3.up * 0.05f, dir, out sh, roll, AIWorld.Obstacles, QueryTriggerInteraction.Ignore)) roll = Mathf.Max(0f, sh.distance - 0.15f);
                pos += dir * roll; t += 0.3f;
                if (cap > 0 && r.traceCount < cap) { tracePos[r.traceCount] = pos; traceTime[r.traceCount] = t; r.traceCount++; }
                r.endPos = pos; r.endTime = t;
                return r;
            }
            r.endPos = pos; r.endTime = t;
            RaycastHit gh;
            r.airburst = r.landTime < 0f && !Physics.Raycast(pos + Vector3.up * 0.05f, Vector3.down, out gh, 0.5f, AIWorld.Obstacles, QueryTriggerInteraction.Ignore);
            return r;
        }

        // =============================================================================== launching
        /// <summary>
        /// Works out the launch velocity that lands a grenade on 'to' from 'from' at the given arc (degrees above horizontal).
        /// If that arc cannot reach (target too high or too far for a flat arc) it tries steeper arcs. Returns false if nothing reaches.
        /// </summary>
        public static bool SolveThrow(Vector3 from, Vector3 to, float arcDegrees, out Vector3 velocity)
        {
            velocity = Vector3.zero;
            Vector3 flat = to - from; flat.y = 0f;
            float d = Mathf.Max(0.5f, flat.magnitude);
            float h = to.y - from.y;
            float g = Mathf.Abs(Physics.gravity.y);
            if (g < 0.01f) return false;
            Vector3 dir = flat.sqrMagnitude > 0.0001f ? flat.normalized : Vector3.forward;

            float[] arcs = { arcDegrees, arcDegrees + 12f, arcDegrees + 25f, 75f };
            for (int i = 0; i < arcs.Length; i++)
            {
                float a = Mathf.Clamp(arcs[i], 5f, 85f) * Mathf.Deg2Rad;
                float cos = Mathf.Cos(a), tan = Mathf.Tan(a);
                float denom = 2f * cos * cos * (d * tan - h);
                if (denom <= 0.01f) continue;                       // this arc cannot reach the target
                float v2 = g * d * d / denom;
                if (v2 <= 0f || float.IsNaN(v2) || float.IsInfinity(v2)) continue;
                float v = Mathf.Sqrt(v2);
                velocity = dir * (v * cos) + Vector3.up * (v * Mathf.Sin(a));
                return true;
            }
            return false;
        }

        /// <summary>
        /// Throws a grenade from 'from' so that it lands on 'to'. prefab may be null (a built-in one is generated) or any prefab:
        /// if the prefab has no GrenadeProjectile one is added with the default settings. errorMetres adds a little aim error so throws are not perfect.
        /// Returns null if the target cannot be reached.
        /// </summary>
        public static GrenadeProjectile ThrowAt(GameObject prefab, Vector3 from, Vector3 to, Combatant owner, float errorMetres = 0.4f, float arcDegrees = 45f, float cooked = 0f)
        {
            Vector3 vel;
            if (!SolveThrow(from, to, arcDegrees, out vel)) return null;
            if (errorMetres > 0f) vel += UnityEngine.Random.insideUnitSphere * errorMetres;
            return Launch(prefab, from, vel, owner, cooked);
        }

        /// <summary>Spawns a grenade moving at 'velocity' (metres per second). The building block for player abilities: throw along the camera, drop, lob.</summary>
        public static GrenadeProjectile Launch(GameObject prefab, Vector3 from, Vector3 velocity, Combatant owner)
        {
            return Launch(prefab, from, velocity, owner, 0f);
        }

        /// <summary>Same, but the fuse has already been running for 'cooked' seconds (a thrower who held it first).</summary>
        public static GrenadeProjectile Launch(GameObject prefab, Vector3 from, Vector3 velocity, Combatant owner, float cooked)
        {
            if (float.IsNaN(velocity.x) || float.IsNaN(velocity.y) || float.IsNaN(velocity.z) || float.IsInfinity(velocity.x) || float.IsInfinity(velocity.y) || float.IsInfinity(velocity.z))
            {
                BF3Debug.E(DbgCat.Grenade, "Grenade launch with an invalid velocity (" + velocity + "); discarded.");
                return null;
            }
            GameObject go = prefab != null ? Instantiate(prefab, from, Quaternion.identity) : BuildDefault(null, null);
            go.transform.position = from;

            GrenadeProjectile gp = go.GetComponent<GrenadeProjectile>();
            if (gp == null)
            {
                gp = go.AddComponent<GrenadeProjectile>();
                gp.fuseTime = Grenade.fuse; gp.radius = Grenade.radius; gp.maxDamage = Grenade.maxDamage;   // the old global defaults
            }
            gp.SetOwner(owner);
            gp.born = Time.time - Mathf.Clamp(cooked, 0f, Mathf.Max(0f, gp.fuseTime - 0.2f));

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();
            gp.IgnoreThrower();
#if UNITY_6000_0_OR_NEWER
            rb.linearVelocity = velocity;
#else
            rb.velocity = velocity;
#endif
            if (BF3Debug.On(DbgLevel.Info, DbgCat.Grenade))
                BF3Debug.I(DbgCat.Grenade, (owner != null ? owner.name : "?") + " throws a grenade (speed " + velocity.magnitude.ToString("F1") + " m/s, fuse " + gp.fuseTime.ToString("F1") + " s).");
            return gp;
        }

        /// <summary>
        /// Builds the default grenade: a small dark sphere with a rigidbody, a short trail and this component. Pass your own materials
        /// (the editor menu does, so the prefab references real assets); null = generated at run time.
        /// </summary>
        public static GameObject BuildDefault(Material bodyMaterial, Material trailMaterial)
        {
            return BuildDefault(bodyMaterial, trailMaterial, null);
        }

        /// <summary>Same, with an optional material for the glowing core (the editor menu passes one so the prefab references real assets).</summary>
        public static GameObject BuildDefault(Material bodyMaterial, Material trailMaterial, Material glowMaterial)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "BF3AI_Grenade";
            go.transform.localScale = Vector3.one * 0.2f;
            Renderer r = go.GetComponent<Renderer>();
            if (r != null)
            {
                if (bodyMaterial != null) r.sharedMaterial = bodyMaterial;
                else r.material.color = new Color(0.2f, 0.25f, 0.2f);
            }

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.4f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // add the trail and glow BEFORE the component, so its Awake finds them and does not add a second set
            Color gc = new Color(1f, 0.35f, 0.1f, 1f), tc = new Color(1f, 0.55f, 0.2f, 0.85f);
            AddTrail(go, trailMaterial, tc, 0.5f, 0.14f);
            AddGlow(go, glowMaterial, gc, 3.5f, 2f);
            GrenadeProjectile gp = go.AddComponent<GrenadeProjectile>();
            gp.glowColor = gc; gp.trailColor = tc;
            return go;
        }

        /// <summary>Destroys every grenade still in the air (used when a battle restarts).</summary>
        public static void ClearAll()
        {
            for (int i = All.Count - 1; i >= 0; i--) if (All[i] != null) Destroy(All[i].gameObject);
            All.Clear();
        }
    }

    /// <summary>The simple default explosion: an expanding, fading fireball and a flash of light. Spawned by GrenadeProjectile when no effect prefab is set.</summary>
    public class GrenadeExplosionFx : MonoBehaviour
    {
        float t0, duration, maxDiameter;
        Material mat;
        Light flash;

        public static GrenadeExplosionFx Spawn(Vector3 pos, float radius, float duration = 0.6f)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "BF3AI_ExplosionFx";
            Collider c = go.GetComponent<Collider>();
            if (c != null) Destroy(c);                                   // pure visual
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * 0.1f;

            GrenadeExplosionFx fx = go.AddComponent<GrenadeExplosionFx>();
            fx.t0 = Time.time; fx.duration = Mathf.Max(0.1f, duration); fx.maxDiameter = Mathf.Max(1f, radius * 1.1f);

            Renderer r = go.GetComponent<Renderer>();
            Shader sh = Shader.Find("Sprites/Default");                 // unlit and transparent in every render pipeline
            if (r != null)
            {
                if (sh != null) { fx.mat = new Material(sh); r.material = fx.mat; }
                else fx.mat = r.material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }

            GameObject lg = new GameObject("Flash");
            lg.transform.SetParent(go.transform, false);
            fx.flash = lg.AddComponent<Light>();
            fx.flash.type = LightType.Point; fx.flash.color = new Color(1f, 0.6f, 0.25f);
            fx.flash.range = radius * 2f; fx.flash.intensity = 6f;
            return fx;
        }

        void Update()
        {
            float k = (Time.time - t0) / duration;
            if (k >= 1f) { Destroy(gameObject); return; }
            float ease = 1f - (1f - k) * (1f - k) * (1f - k);          // fast at first, then settles
            transform.localScale = Vector3.one * Mathf.Lerp(0.1f, maxDiameter, ease);
            if (mat != null) mat.color = new Color(1f, Mathf.Lerp(0.7f, 0.25f, k), 0.1f, 0.85f * (1f - k));
            if (flash != null) flash.intensity = 6f * (1f - k);
        }
    }
}
