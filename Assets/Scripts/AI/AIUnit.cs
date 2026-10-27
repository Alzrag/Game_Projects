using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace BF3AI
{
    // =====================================================================================================
    //  AIUNIT.CS  -  PUT THIS ON EVERY BOT
    //  The unit's brain plus the small plain-C# pieces only a bot uses: class stats, personality, perception,
    //  threat scoring, orders and contacts. Needs Combatant + NavMeshAgent on the same object.
    // =====================================================================================================

    public enum UnitClass { Assault = 0, Heavy = 1, Sniper = 2, Officer = 3 }

    /// <summary>Per-class stats. Tweak freely.</summary>
    public class ClassProfile
    {
        public float health, walkSpeed, runSpeed, damage, shotsPerSecond, spreadDeg, idealMin, idealMax, viewRange;
        public int grenades;

        public static ClassProfile For(UnitClass c)
        {
            switch (c)
            {
                case UnitClass.Heavy:
                    return new ClassProfile { health = 200, walkSpeed = 2.6f, runSpeed = 4.2f, damage = 9, shotsPerSecond = 12f, spreadDeg = 4f, idealMin = 15, idealMax = 45, viewRange = 70, grenades = 1 };
                case UnitClass.Sniper:
                    return new ClassProfile { health = 80, walkSpeed = 3.4f, runSpeed = 5.5f, damage = 60, shotsPerSecond = 0.7f, spreadDeg = 0.25f, idealMin = 35, idealMax = 110, viewRange = 130, grenades = 0 };
                case UnitClass.Officer:
                    return new ClassProfile { health = 120, walkSpeed = 3.4f, runSpeed = 5.8f, damage = 14, shotsPerSecond = 5f, spreadDeg = 2f, idealMin = 10, idealMax = 35, viewRange = 80, grenades = 2 };
                default:
                    return new ClassProfile { health = 100, walkSpeed = 3.6f, runSpeed = 6.5f, damage = 12, shotsPerSecond = 6f, spreadDeg = 2.5f, idealMin = 6, idealMax = 22, viewRange = 70, grenades = 2 };
            }
        }
    }

    public enum OrderType { None, Advance, Hold, Overwatch, Suppress, Flank, Bait, AmbushHold, Regroup }

    /// <summary>An instruction from the squad/platoon "tactical brain" to a unit.</summary>
    public class Order
    {
        public OrderType type;
        public Vector3 point;     // where to be
        public Vector3 aimPoint;  // what to watch / shoot at
        public float expires;
        public bool Active { get { return Time.time < expires; } }

        public static Order Make(OrderType t, Vector3 p, Vector3 aim, float duration)
        {
            return new Order { type = t, point = p, aimPoint = aim, expires = Time.time + duration };
        }
    }

    /// <summary>What a unit remembers about an enemy (last-known position, not live tracking).</summary>
    public class Contact
    {
        public Combatant who;          // null for sound-only contacts
        public Vector3 lastPos, lastVel;
        public float lastSeen;
        public bool visible, heardOnly;
        public float uncertainty;
        public float Age { get { return Time.time - lastSeen; } }
        public Vector3 PredictedPos { get { return lastPos + lastVel * Mathf.Min(Age, 1.5f); } }
        public Contact Clone() { return (Contact)MemberwiseClone(); }
    }
    /// <summary>
    /// Traits are sampled from a normal distribution centred on the class baseline.
    /// Tail samples (|z| >= 2) become named quirks, e.g. a "Reckless" sniper or "Skittish" heavy.
    /// A small chance of a forced heavy-tail roll makes anomalies show up reliably.
    /// </summary>
    [Serializable]
    public class Personality
    {
        public float aggression, caution, discipline, composure, marksmanship, holdGround;
        public string quirk = "Standard";

        public override string ToString()
        {
            return quirk + " (agg " + aggression.ToString("F2") + " cau " + caution.ToString("F2") + " dis " + discipline.ToString("F2")
                 + " com " + composure.ToString("F2") + " mks " + marksmanship.ToString("F2") + " hld " + holdGround.ToString("F2") + ")";
        }

        static readonly string[] Names = { "aggression", "caution", "discipline", "composure", "marksmanship", "holdGround" };
        static readonly string[] Hi = { "Reckless", "Skittish", "By-the-book", "Stoic", "Deadeye", "Immovable" };
        static readonly string[] Lo = { "Timid", "Careless", "Maverick", "Nervous", "Shaky", "Restless" };

        // aggression, caution, discipline, composure, marksmanship, holdGround
        static float[] Baseline(UnitClass c)
        {
            switch (c)
            {
                case UnitClass.Heavy:   return new[] { 0.55f, 0.40f, 0.65f, 0.70f, 0.45f, 0.90f };
                case UnitClass.Sniper:  return new[] { 0.20f, 0.80f, 0.55f, 0.65f, 0.85f, 0.70f };
                case UnitClass.Officer: return new[] { 0.45f, 0.55f, 0.90f, 0.75f, 0.60f, 0.55f };
                default:                return new[] { 0.80f, 0.30f, 0.60f, 0.55f, 0.55f, 0.30f };
            }
        }

        public static Personality Generate(UnitClass c, float sigma = 0.18f, float anomalyChance = 0.04f)
        {
            float[] mean = Baseline(c);
            float[] v = new float[6];
            float[] z = new float[6];
            for (int i = 0; i < 6; i++)
            {
                z[i] = Tactics.Gauss();
                v[i] = Mathf.Clamp01(mean[i] + z[i] * sigma);
            }

            // rare forced anomaly on one random trait
            if (UnityEngine.Random.value < anomalyChance)
            {
                int i = UnityEngine.Random.Range(0, 6);
                z[i] = (UnityEngine.Random.value < 0.5f ? -1f : 1f) * UnityEngine.Random.Range(2.2f, 3.2f);
                v[i] = Mathf.Clamp01(mean[i] + z[i] * sigma);
            }

            string quirk = "Standard"; float worst = 1.9f;
            for (int i = 0; i < 6; i++)
            {
                if (Mathf.Abs(z[i]) >= worst) { worst = Mathf.Abs(z[i]); quirk = z[i] > 0 ? Hi[i] : Lo[i]; }
            }

            return new Personality
            {
                aggression = v[0], caution = v[1], discipline = v[2],
                composure = v[3], marksmanship = v[4], holdGround = v[5], quirk = quirk
            };
        }
    }
    /// <summary>
    /// No psychic tracking. Units see through a cone with line-of-sight checks, hear gunshots/footsteps
    /// with positional error, and remember last-known positions that go stale over time.
    /// </summary>
    public class Perception
    {
        readonly AIUnit u;
        public readonly Dictionary<Combatant, Contact> seen = new Dictionary<Combatant, Contact>();
        public readonly List<Contact> heard = new List<Contact>();
        public float fov = 130f, memorySeconds = 15f, heardMemory = 8f;
        readonly List<Combatant> stale = new List<Combatant>();

        public Perception(AIUnit unit) { u = unit; }
        public void Enable() { SoundBus.OnSound += OnSound; }
        public void Disable() { SoundBus.OnSound -= OnSound; }
        public void Clear() { seen.Clear(); heard.Clear(); }

        public bool IsVisible(Combatant c)
        {
            Contact k;
            return seen.TryGetValue(c, out k) && k.visible;
        }

        void OnSound(Vector3 pos, float radius, int team, SoundType type)
        {
            if (!u.isActiveAndEnabled || !u.IsAlive || team == u.team) return;
            float d = Vector3.Distance(u.transform.position, pos);
            if (d > radius) return;

            for (int i = 0; i < heard.Count; i++)
            {
                Contact h = heard[i];
                if ((h.lastPos - pos).sqrMagnitude < 16f)
                {
                    h.lastPos = Vector3.Lerp(h.lastPos, pos, 0.5f);
                    h.lastSeen = Time.time;
                    return;
                }
            }
            Vector3 noise = Random.insideUnitSphere * d * 0.12f; noise.y = 0f;   // farther = vaguer
            heard.Add(new Contact { lastPos = pos + noise, lastSeen = Time.time, heardOnly = true, uncertainty = d * 0.12f });
            if (BF3Debug.On(DbgLevel.Verbose, DbgCat.Perception))
                BF3Debug.V(DbgCat.Perception, u.Tag + " HEARS " + type + " " + d.ToString("F0") + " m away (team " + team + ")", u, u.uid);
            if (heard.Count > 6) heard.RemoveAt(0);
        }

        bool CanSee(Combatant c, Vector3 eye)
        {
            Vector3 to = c.Chest - eye;
            float d = to.magnitude;
            if (d > u.profile.viewRange) return false;
            if (d > 8f && Vector3.Angle(u.transform.forward, Tactics.Flat(to)) > fov * 0.5f) return false;
            if (!Physics.Linecast(eye, c.Chest, AIWorld.Obstacles, QueryTriggerInteraction.Ignore)) return true;
            return !Physics.Linecast(eye, c.Eye, AIWorld.Obstacles, QueryTriggerInteraction.Ignore);
        }

        public void Tick()
        {
            Vector3 eye = u.Eye;
            foreach (Combatant c in Combatant.All)
            {
                if (c.team == u.team || !c.IsAlive) continue;
                if (CanSee(c, eye))
                {
                    Contact ct;
                    if (!seen.TryGetValue(c, out ct)) { ct = new Contact { who = c, lastPos = c.Feet, lastSeen = -99f }; seen[c] = ct; }
                    bool wasVisible = ct.visible;
                    float dt = Time.time - ct.lastSeen;
                    ct.lastVel = (dt > 0.01f && dt < 0.6f) ? (c.Feet - ct.lastPos) / dt : Vector3.zero;
                    ct.lastPos = c.Feet;
                    ct.lastSeen = Time.time;
                    ct.visible = true;
                    if (!wasVisible && BF3Debug.On(DbgLevel.Verbose, DbgCat.Perception))
                        BF3Debug.V(DbgCat.Perception, u.Tag + " SEES '" + c.name + "' at " + Vector3.Distance(u.transform.position, c.transform.position).ToString("F0") + " m", u, u.uid);
                }
                else
                {
                    Contact ct;
                    if (seen.TryGetValue(c, out ct))
                    {
                        if (ct.visible && BF3Debug.On(DbgLevel.Verbose, DbgCat.Perception))
                            BF3Debug.V(DbgCat.Perception, u.Tag + " lost sight of '" + c.name + "'; last known " + ct.lastPos.ToString("F0"), u, u.uid);
                        ct.visible = false;
                    }
                }
            }

            stale.Clear();
            foreach (KeyValuePair<Combatant, Contact> kv in seen)
                if (kv.Key == null || !kv.Key.IsAlive || kv.Value.Age > memorySeconds) stale.Add(kv.Key);
            foreach (Combatant c in stale) seen.Remove(c);
            heard.RemoveAll(h => h.Age > heardMemory);
        }

        public void CollectKnown(List<Contact> buf)
        {
            foreach (Contact c in seen.Values) if (c.Age < memorySeconds) buf.Add(c);
            foreach (Contact h in heard) if (h.Age < heardMemory) buf.Add(h);
        }

        public Contact NewestHeard()
        {
            Contact best = null;
            foreach (Contact h in heard) if (h.Age < 6f && (best == null || h.lastSeen > best.lastSeen)) best = h;
            return best;
        }
    }
    /// <summary>
    /// Scores how much a unit wants to shoot a given enemy. Snipers and officers weight "high-value" flags
    /// (regen, healing, heavy weapon) much more than grunts do. Learned data (duel losses, focus-fire results)
    /// shifts the score. Squads average member scores into a shared matrix.
    /// </summary>
    public static class ThreatMatrix
    {
        public static float Score(AIUnit u, Contact c, bool includeSquad = true)
        {
            Combatant t = c.who;
            if (t == null) return -1f;
            TeamLearning L = LearningMemory.Get(u.team);

            bool tactical = u.unitClass == UnitClass.Sniper || u.unitClass == UnitClass.Officer;
            float w = tactical ? 1.5f : 0.5f;

            float d = Vector3.Distance(u.transform.position, c.lastPos);
            float prox = 1f - Mathf.Clamp01(d / Mathf.Max(1f, u.profile.viewRange));
            float s = prox * (u.unitClass == UnitClass.Sniper ? 0.25f : 1f);

            if (u.combatant.lastAttacker == t && Time.time - u.combatant.lastDamageTime < 4f) s += 0.8f;
            if (t.regenPerSecond > 0.5f) s += 0.5f * w;
            if (t.isHealing) s += 0.9f * w;
            if (t.usingHeavyWeapon) s += 1.0f * w;
            if (t.health < t.maxHealth * 0.35f) s += 0.4f;
            if (t.isPlayer) s += 0.15f;

            // grouped targets: enemies clustered around this one are worth focusing (weight is learned)
            int cluster = 0;
            foreach (Combatant o in Combatant.All)
                if (o != t && o.team == t.team && o.IsAlive && (o.transform.position - t.transform.position).sqrMagnitude < 36f) cluster++;
            s += Mathf.Min(cluster, 3) * 0.12f * L.FocusWeight;

            int backup = 0;
            if (includeSquad && u.squad != null)
            {
                backup = u.squad.CountTargeting(t, u);
                s += backup * 0.2f * L.FocusWeight;
                s += u.squad.PriorityOf(t) * 0.3f;
            }

            // avoid solo duels against players who keep winning
            s -= L.Respect(t) * (backup >= 1 ? 0.1f : 0.8f);

            if (!c.visible) s -= 0.5f + c.Age * 0.05f;
            return s;
        }
    }
    [RequireComponent(typeof(NavMeshAgent), typeof(Combatant))]
    public class AIUnit : MonoBehaviour
    {
        public static readonly List<AIUnit> All = new List<AIUnit>();
        public enum Mode { Idle, Advance, SeekCover, InCover, Investigate, Charge, Retreat, Flank, Relocate, Bait, Panic }
        public enum FireMode { None, Aimed, Suppress, Blind }

        [Header("Setup")]
        public UnitClass unitClass;
        public Transform muzzle;
        public GameObject grenadePrefab;

        [Header("Runtime (read-only)")]
        public Personality personality;
        [SerializeField] Mode _mode;
        public Mode mode { get { return _mode; } set { if (value != _mode) OnModeChange(_mode, value); _mode = value; } }
        public FireMode fireMode;
        public Combatant target;
        public float suppression;
        public int grenades, kills;
        public float lifeStart;

        [Header("Debug (read-only)")]
        public int uid;
        public int room = -1;
        public int shots, hits;
        [TextArea(2, 6)] public string debugState;

        [System.NonSerialized] public Squad squad;
        Order _order;
        public Order order { get { return _order; } set { if ((object)value != (object)_order) OnOrderChange(_order, value); _order = value; } }
        [System.NonSerialized] public CoverPoint cover;
        [System.NonSerialized] public ClassProfile profile;

        public Combatant combatant { get; private set; }
        public NavMeshAgent agent { get; private set; }
        public Perception perception { get; private set; }
        public int team { get { return combatant.team; } }
        public bool IsAlive { get { return combatant.IsAlive; } }
        public float Uptime { get { return Time.time - lifeStart; } }
        public Vector3 Eye { get { return combatant.Eye; } }

        // ---- internal state
        readonly List<Contact> known = new List<Contact>();
        readonly List<Vector3> threats = new List<Vector3>();
        bool hasThreat, orderAccepted, movingToCover, peeking, peekValid, holdAim, ambushTriggered;
        float nearestThreatDist;
        float nextThink, nextPerceive, nextVisit, nextShot, nextCoverSearch, coverHoldUntil, peekSwitchTime;
        float nextGrenade, nextStrafe, nextStep, lastDestTime, laneUntil, baitStart;
        int baitPhase;
        Vector3 suppressPoint, peekPos, lastDest = new Vector3(1e6f, 0, 1e6f), laneGoal, laneMid;
        bool lanePassed;

        float Agg { get { return Mathf.Clamp01(personality.aggression + LearningMemory.Get(team).aggrMod[(int)unitClass]); } }
        float Cohesion { get { return squad != null ? squad.Cohesion : 0.5f; } }
        bool Panicking
        {
            get
            {
                float th = Mathf.Lerp(0.30f, 0.10f, personality.composure);
                return Cohesion < th || (suppression > 0.85f && personality.composure < 0.35f);
            }
        }

        // ================================================================= lifecycle
        void Awake()
        {
            uid = ++uidCounter;
            combatant = GetComponent<Combatant>();
            agent = GetComponent<NavMeshAgent>();
            perception = new Perception(this);
            if (combatant == null || agent == null)
            {
                BF3Debug.E(DbgCat.Setup, "AIUnit on '" + name + "' needs " + (combatant == null ? "a Combatant " : "") + (agent == null ? "a NavMeshAgent " : "") + "on the same GameObject. This unit is disabled.", this, uid);
                enabled = false; return;
            }
            agent.updateRotation = false;
            agent.stoppingDistance = 0.15f;
        }

        void OnEnable() { All.Add(this); perception.Enable(); combatant.OnDied += HandleDeath; }
        void OnDisable() { All.Remove(this); perception.Disable(); combatant.OnDied -= HandleDeath; }

        /// <summary>Starts a new life (new generation): fresh personality, fresh health, uptime reset.</summary>
        public void BeginLife(Vector3 pos, Quaternion rot)
        {
            profile = ClassProfile.For(unitClass);
            personality = Personality.Generate(unitClass);
            combatant.maxHealth = profile.health;
            combatant.ResetHealth();
            grenades = profile.grenades; kills = 0; suppression = 0f; lifeStart = Time.time;
            order = null; cover = null; target = null; movingToCover = false;
            mode = Mode.Idle; fireMode = FireMode.None;
            if (!agent.Warp(pos))
            {
                NavMeshHit wh;
                if (NavMesh.SamplePosition(pos, out wh, 10f, NavMesh.AllAreas) && agent.Warp(wh.position))
                    BF3Debug.W(DbgCat.Spawn, Tag + " spawn point " + pos.ToString("F1") + " was off the NavMesh; moved to " + wh.position.ToString("F1") + ".", this, uid);
                else
                    BF3Debug.E(DbgCat.Spawn, Tag + " could not be placed on the NavMesh near " + pos.ToString("F1") + ". Is the NavMesh baked there? This unit will not be able to move.", this, uid);
            }
            transform.rotation = rot;
            agent.speed = profile.walkSpeed;
            perception.Clear();
            nextThink = Time.time + Random.value * 0.3f;
            errCount = 0; faultedUntil = 0f; frozen = false; respawnScheduled = false; stuckSince = -1f; stuckCount = 0;
            shots = 0; hits = 0; room = -1;
            if (BF3Debug.On(DbgLevel.Info, DbgCat.Spawn))
                BF3Debug.I(DbgCat.Spawn, Tag + " life begins at " + transform.position.ToString("F1") + "  personality " + personality + "  hp " + combatant.maxHealth + "  grenades " + grenades, this, uid);
        }

        void HandleDeath(Combatant killer)
        {
            try { HandleDeathInner(killer); }
            catch (Exception e)
            {
                BF3Debug.Exception("AIUnit.HandleDeath", e, this, this);
                try
                {
                    SquadManager m = SquadManager.Of(team);
                    if (!respawnScheduled && m != null) { respawnScheduled = true; m.ScheduleRespawn(this); }
                }
                catch (Exception e2) { BF3Debug.Exception("AIUnit.HandleDeath fallback", e2, this, this); }
                gameObject.SetActive(false);
            }
        }

        void HandleDeathInner(Combatant killer)
        {
            if (BF3Debug.On(DbgLevel.Info, DbgCat.Combat))
                BF3Debug.I(DbgCat.Combat, Tag + " DIED at " + transform.position.ToString("F1") + " room=" + (CoverMap.I != null ? CoverMap.I.RoomName(room) : "-")
                    + " after " + Uptime.ToString("F0") + "s: mode=" + mode + " order=" + (order != null ? order.type.ToString() : "none")
                    + (cover != null ? " cover=" + (cover.IsLow ? "low" : "full") + (movingToCover ? "(en route)" : "(in it)") : " no-cover")
                    + " shots/hits=" + shots + "/" + hits + " kills=" + kills + " suppression=" + suppression.ToString("F2") + " cohesion=" + Cohesion.ToString("F2")
                    + " killer=" + (killer != null ? killer.name + " at " + Vector3.Distance(killer.transform.position, transform.position).ToString("F0") + " m" : "none"), this, uid);
            LifeRecord rec = new LifeRecord
            {
                unitClass = unitClass, team = team, deathPos = transform.position, killer = killer,
                killerDist = killer != null ? Vector3.Distance(killer.transform.position, transform.position) : 0f,
                wasInCover = cover != null && !movingToCover, lifetime = Uptime, kills = kills
            };
            if (cover != null) cover.RegisterDeath();
            ReleaseCover();
            LearningMemory.Get(team).RecordLife(rec);
            if (squad != null) squad.OnMemberDied(this);
            SquadManager mgr = SquadManager.Of(team);
            respawnScheduled = true;
            if (mgr != null) mgr.ScheduleRespawn(this);
            else BF3Debug.E(DbgCat.Spawn, Tag + " died but there is no SquadManager to respawn it.", this, uid);
            gameObject.SetActive(false);
        }

        public void AddSuppression(float a) { suppression = Mathf.Clamp01(suppression + a); }

        public void SetOrder(Order o)
        {
            order = o;
            float chance = Mathf.Lerp(0.55f, 1f, personality.discipline) * (0.6f + 0.4f * Cohesion) + 0.1f;
            orderAccepted = Random.value < Mathf.Clamp01(chance);   // low-discipline mavericks sometimes ignore orders
            if (BF3Debug.On(DbgLevel.Verbose, DbgCat.Orders))
                BF3Debug.V(DbgCat.Orders, Tag + " received " + o.type + (orderAccepted ? " -> ACCEPTED" : " -> IGNORED (discipline roll failed, p=" + Mathf.Clamp01(chance).ToString("F2") + ")"), this, uid);
            baitPhase = 0; baitStart = Time.time; ambushTriggered = false;
        }
        public void ClearOrder() { order = null; }

        // ================================================================= update loop
        void Update()
        {
            if (!IsAlive) return;
            if (profile == null || personality == null)
            {
                if (BF3Debug.Allow("nobegin" + uid, 5f))
                    BF3Debug.E(DbgCat.Setup, Tag + " on scene object '" + name + "' is active but BeginLife was never called (no profile/personality). This bot was placed by hand or is a spawned bot whose spawn failed: delete it from the Hierarchy (bots must be spawned by SquadManager from the Unit Prefab), or remove the AIUnit component if this is your player.", this, uid);
                return;
            }
            if (Time.time < faultedUntil) { frozen = true; return; }
            if (frozen) { frozen = false; if (agent.isOnNavMesh) agent.isStopped = false; }
            try { Tick(); }
            catch (Exception e) { OnFault(e); }
        }

        void Tick()
        {
            suppression = Mathf.Max(0f, suppression - Time.deltaTime * 0.15f);
            if (!agent.isOnNavMesh && !agent.isOnOffMeshLink) { HandleOffMesh(); return; }

            if (Time.time >= nextPerceive)
            {
                nextPerceive = Time.time + 0.12f;
                perception.Tick();
                UpdateRoom();
                ReportIntel();
            }
            if (Time.time >= nextThink)
            {
                nextThink = Time.time + 0.2f + Random.value * 0.1f;
                Think();
                if (InspectorOn) debugState = BuildState();
            }
            if (Time.time >= nextVisit)
            {
                nextVisit = Time.time + 2f;
                if (DeathHeatmap.I != null) DeathHeatmap.I.RecordVisit(transform.position, team);
            }
            if (Time.time >= nextWatch) { nextWatch = Time.time + 1f; Watchdog(); }
            Act();
        }

        void ReportIntel()
        {
            if (squad == null) return;
            foreach (Contact c in perception.seen.Values) if (c.visible) squad.Report(c);   // pooled recon
        }

        // ================================================================= think
        void Think()
        {
            holdAim = false;
            RefreshKnowledge();
            if (Panicking) { ThinkPanic(); return; }
            if (order != null && !order.Active) order = null;
            if (order != null && orderAccepted) { ThinkOrder(); return; }
            ThinkAutonomous();
        }

        void RefreshKnowledge()
        {
            known.Clear(); threats.Clear();
            perception.CollectKnown(known);
            if (squad != null && squad.platoon != null && Cohesion > 0.2f) squad.platoon.CollectShared(known);

            nearestThreatDist = float.MaxValue;
            foreach (Contact c in known)
            {
                Vector3 p = c.PredictedPos;
                threats.Add(p);
                nearestThreatDist = Mathf.Min(nearestThreatDist, Tactics.Flat(p - transform.position).magnitude);
            }
            hasThreat = threats.Count > 0;
            target = PickTarget();
        }

        Combatant PickTarget()
        {
            Combatant best = null; float bs = float.MinValue;
            foreach (Contact c in known)
            {
                if (c.who == null || !c.who.IsAlive) continue;
                float s = ThreatMatrix.Score(this, c);
                if (c.who == target) s += 0.3f;   // stickiness
                if (s > bs) { bs = s; best = c.who; }
            }
            return best;
        }

        Contact ContactFor(Combatant c)
        {
            if (c == null) return null;
            foreach (Contact k in known) if (k.who == c) return k;
            return null;
        }

        int NearestThreatIndex()
        {
            int bi = 0; float bd = float.MaxValue;
            for (int i = 0; i < threats.Count; i++)
            {
                float d = (threats[i] - transform.position).sqrMagnitude;
                if (d < bd) { bd = d; bi = i; }
            }
            return bi;
        }

        // ----------------------------------------------------------------- autonomous behaviour
        void ThinkAutonomous()
        {
            if (!hasThreat) { ThinkNoThreat(); return; }
            if (target == null) { ThinkHeardOnly(); return; }
            switch (unitClass)
            {
                case UnitClass.Assault: ThinkAssault(); break;
                case UnitClass.Heavy: ThinkHeavy(); break;
                case UnitClass.Sniper: ThinkSniper(); break;
                default: ThinkOfficer(); break;
            }
        }

        void ThinkNoThreat()
        {
            fireMode = FireMode.None; target = null;
            if (cover != null) ReleaseCover();
            combatant.crouched = false;
            movingToCover = false;

            Contact heard = perception.NewestHeard();
            if (heard != null && unitClass != UnitClass.Sniper && Random.value < 0.35f + 0.5f * Agg - 0.3f * personality.caution)
            {
                mode = Mode.Investigate;
                MoveTo(heard.lastPos);
                if (Tactics.Flat(heard.lastPos - transform.position).magnitude < 5f) heard.lastSeen -= 10f;   // nothing here
                return;
            }

            if (squad != null)
            {
                Vector3 mates = squad.MateCenter(this);
                if (Tactics.Flat(mates - transform.position).magnitude > 15f) { mode = Mode.Advance; MoveAlongLane(mates); return; }   // rejoin squad
                if (squad.hasObjective) { mode = Mode.Advance; MoveAlongLane(squad.FormationPoint(this)); return; }
            }
            mode = Mode.Idle;
        }

        void ThinkHeardOnly()
        {
            fireMode = FireMode.None;
            Vector3 p = threats[NearestThreatIndex()];
            bool bold = Agg > 0.55f && unitClass != UnitClass.Sniper && unitClass != UnitClass.Heavy;
            if (bold) { mode = Mode.Investigate; movingToCover = false; combatant.crouched = false; MoveTo(p); return; }
            if (!CoverIsGood) RequestCover(12f, null, 0f, 0f, false, Mode.SeekCover);
            if (mode == Mode.InCover && cover != null) InCoverBehavior();
            else { suppressPoint = p + Vector3.up; holdAim = true; }
        }

        // Assault: retreats at long range, dynamically closes distance, charges aggressively.
        void ThinkAssault()
        {
            Contact tc = ContactFor(target);
            float agg = Agg;
            float hp = combatant.health / combatant.maxHealth;
            float charge = Mathf.Lerp(14f, 30f, agg), retreat = Mathf.Lerp(35f, 65f, agg);
            bool backup = squad != null && squad.AliveCount >= 3 && Cohesion > 0.5f;
            bool avoidDuel = LearningMemory.Get(team).Respect(target) > 0.35f && !backup;

            if (hp < 0.2f + 0.25f * personality.caution || nearestThreatDist > retreat) { RetreatToCover(tc); return; }
            if (nearestThreatDist <= charge && !avoidDuel) { Charge(tc); return; }
            AdvanceByCover(tc);
        }

        void RetreatToCover(Contact tc)
        {
            Vector3? g = squad != null ? (Vector3?)squad.MateCenter(this) : null;
            RequestCover(25f, g, 1f, 0f, false, Mode.Retreat);
            if (mode == Mode.InCover && cover != null) InCoverBehavior(); else fireMode = FireMode.None;
        }

        void Charge(Contact tc)
        {
            mode = Mode.Charge; movingToCover = false; combatant.crouched = false; ReleaseCover();
            Vector3 goal = tc != null ? tc.PredictedPos : threats[0];
            Vector3 to = goal - transform.position;
            float d = Tactics.Flat(to).magnitude;
            if (d < 7f && tc != null && tc.visible)
            {
                if (Time.time >= nextStrafe)   // close quarters: strafe instead of standing still
                {
                    nextStrafe = Time.time + Random.Range(0.6f, 1.4f);
                    Vector3 side = Vector3.Cross(Vector3.up, to).normalized * (Random.value < 0.5f ? 3f : -3f);
                    MoveTo(Tactics.Nav(transform.position + side + to.normalized * 1.5f, 2f));
                }
            }
            else MoveTo(goal);
            FireAtTarget(tc, true);
            TryGrenade(tc);
        }

        void AdvanceByCover(Contact tc)
        {
            bool holding = mode == Mode.InCover && Time.time < coverHoldUntil;
            if (!holding && Time.time >= nextCoverSearch)
                RequestCover(18f, tc != null ? (Vector3?)tc.PredictedPos : null, 1.5f, 6f, false, Mode.SeekCover);
            if (mode == Mode.InCover && cover != null) InCoverBehavior(); else FireAtTarget(tc, movingToCover);
        }

        // Heavy: holds ground, suppresses continuously, absorbs damage instead of dodging.
        void ThinkHeavy()
        {
            Contact tc = ContactFor(target);
            float hp = combatant.health / combatant.maxHealth;
            bool skittish = personality.holdGround < 0.35f && hp < 0.35f;   // rare cautious/skittish heavy
            Vector3? g = squad != null ? (Vector3?)squad.MateCenter(this) : null;
            if (skittish) RequestCover(20f, g, 0.8f, 0f, false, Mode.Retreat);
            else if (!CoverIsGood) RequestCover(14f, null, 0f, 0f, false, Mode.SeekCover);

            if (mode == Mode.InCover && cover != null) InCoverBehavior();
            else
            {
                if (!movingToCover) { combatant.crouched = false; mode = Mode.Idle; }
                FireAtTarget(tc, true);
            }
        }

        // Sniper: avoids close quarters, relocates instantly when flanked, pools recon.
        void ThinkSniper()
        {
            Contact tc = ContactFor(target);
            bool tooClose = nearestThreatDist < profile.idealMin * 0.5f;
            bool flanked = IsFlanked();
            bool noSight = tc == null || (!tc.visible && tc.Age > 6f);
            if (tooClose || flanked || cover == null || (mode == Mode.InCover && noSight && Time.time > coverHoldUntil))
            {
                nextCoverSearch = flanked || tooClose ? 0f : nextCoverSearch;   // flanked: re-search immediately
                if (!RequestCover(50f, null, 0f, profile.idealMin * 0.8f, true, Mode.Relocate) && tooClose)
                {
                    Vector3 away = transform.position - threats[NearestThreatIndex()]; away.y = 0f;
                    MoveTo(Tactics.Nav(transform.position + away.normalized * 20f)); mode = Mode.Relocate;
                }
            }
            if (mode == Mode.InCover && cover != null) InCoverBehavior();
            else if (!movingToCover) FireAtTarget(tc, false);
        }

        bool IsFlanked()
        {
            if (cover == null) return nearestThreatDist < 30f;
            foreach (Vector3 p in threats)
                if (Tactics.Flat(p - transform.position).sqrMagnitude < 40f * 40f && !cover.Blocks(p)) return true;
            return false;
        }

        // Officer: stays with the squad, fights from cover, throws grenades. The squad brain (Squad/Platoon) issues its orders.
        void ThinkOfficer()
        {
            Contact tc = ContactFor(target);
            float hp = combatant.health / combatant.maxHealth;
            Vector3? g = squad != null ? (Vector3?)squad.MateCenter(this) : null;
            if (hp < 0.3f + 0.2f * personality.caution) RequestCover(20f, g, 0.8f, 0f, false, Mode.Retreat);
            else if (!CoverIsGood) RequestCover(16f, g, 0.4f, 0f, false, Mode.SeekCover);

            if (mode == Mode.InCover && cover != null) InCoverBehavior(); else FireAtTarget(tc, true);
            TryGrenade(tc);
        }

        // ----------------------------------------------------------------- panic (low cohesion)
        void ThinkPanic()
        {
            mode = Mode.Panic; peeking = false;
            Contact tc = ContactFor(target);
            if (hasThreat && Random.value < 0.6f)   // abandon cover and bolt
            {
                ReleaseCover(); combatant.crouched = false;
                Vector3 away = transform.position - threats[NearestThreatIndex()]; away.y = 0f;
                if (away.sqrMagnitude < 0.1f) away = -transform.forward;
                MoveTo(Tactics.Nav(transform.position + away.normalized * 12f + Random.insideUnitSphere * 3f, 6f));
            }
            FireAtTarget(tc, true);   // spread is doubled while panicking (see HandleFire)
        }

        // ----------------------------------------------------------------- orders
        void ThinkOrder()
        {
            Contact tc = ContactFor(target);
            switch (order.type)
            {
                case OrderType.Advance: AdvanceOrder(tc); break;
                case OrderType.Hold:
                case OrderType.Overwatch:
                case OrderType.Suppress: HoldOrder(tc); break;
                case OrderType.Flank:
                    mode = Mode.Flank; movingToCover = false; combatant.crouched = false; ReleaseCover();
                    if (tc != null && tc.visible && Tactics.Flat(tc.lastPos - transform.position).magnitude < 25f) { order = null; break; }   // engaged: go autonomous
                    MoveTo(order.point);
                    if (Tactics.Flat(order.point - transform.position).magnitude < 3f) order = null;
                    FireAtTarget(tc, true);
                    break;
                case OrderType.Bait: BaitOrder(tc); break;
                case OrderType.AmbushHold: AmbushOrder(tc); break;
                case OrderType.Regroup:
                    mode = Mode.Advance; MoveAlongLane(order.point);
                    if (Tactics.Flat(order.point - transform.position).magnitude < 4f) order = null;
                    break;
                default: order = null; break;
            }
        }

        void AdvanceOrder(Contact tc)
        {
            if (Tactics.Flat(order.point - transform.position).magnitude < 3f) { order = null; return; }
            if (hasThreat && nearestThreatDist < 60f)
            {
                // cover-to-cover advance: hop to a nearer cover, pause, repeat
                bool holding = mode == Mode.InCover && Time.time < coverHoldUntil;
                if (!holding && Time.time >= nextCoverSearch)
                {
                    CoverPoint before = cover;
                    bool found = RequestCover(14f, order.point, 1.6f, 0f, false, Mode.SeekCover);
                    if (!found || cover == before) { ReleaseCover(); mode = Mode.Advance; MoveTo(order.point); }   // no better cover: push
                }
                if (mode == Mode.InCover && cover != null) InCoverBehavior(); else FireAtTarget(tc, true);
            }
            else { mode = Mode.Advance; MoveAlongLane(order.point); fireMode = FireMode.None; }
        }

        // Overwatch / Suppress / Hold: take a post near order.point and pin the enemy while others move.
        void HoldOrder(Contact tc)
        {
            bool atPost = cover != null && Tactics.Flat(cover.position - order.point).magnitude < 12f;
            if (!atPost || !CoverIsGood)
                RequestCover(10f, order.aimPoint, 0.6f, unitClass == UnitClass.Sniper ? 25f : 0f, unitClass == UnitClass.Sniper, Mode.SeekCover, order.point);

            if (mode == Mode.InCover && cover != null) InCoverBehavior();
            else if (!movingToCover) { combatant.crouched = false; FireAtTarget(tc, true); }

            if (fireMode != FireMode.Aimed && order.type != OrderType.Hold && unitClass != UnitClass.Sniper)
            {
                suppressPoint = order.aimPoint + Vector3.up;   // suppressive fire on the enemy position even with no line of sight
                fireMode = unitClass == UnitClass.Heavy ? FireMode.Suppress : FireMode.Blind;
            }
        }

        // Bait & switch: expose yourself, draw fire, then fake a panicked retreat into the pre-aimed kill zone.
        void BaitOrder(Contact tc)
        {
            mode = Mode.Bait; movingToCover = false; combatant.crouched = false; ReleaseCover();
            if (baitPhase == 0)
            {
                MoveTo(order.aimPoint);
                bool arrived = Tactics.Flat(order.aimPoint - transform.position).magnitude < 3f;
                bool spotted = Time.time - combatant.lastDamageTime < 1.5f || suppression > 0.15f;
                if (arrived || spotted || Time.time - baitStart > 8f) baitPhase = 1;
                FireAtTarget(tc, true);   // gets noticed
            }
            else
            {
                MoveTo(order.point);
                FireAtTarget(tc, false);   // wild over-the-shoulder shots
                if (Tactics.Flat(order.point - transform.position).magnitude < 2.5f) order = null;
            }
        }

        // Ambush: stay hidden and pre-aimed at the kill zone until an enemy walks into it.
        void AmbushOrder(Contact tc)
        {
            bool atPost = cover != null && Tactics.Flat(cover.position - order.point).magnitude < 8f;
            if (!atPost || !CoverIsGood)
                RequestCover(10f, order.aimPoint, 0.8f, 0f, false, Mode.SeekCover, order.point);

            foreach (Contact c in known)
                if (c.visible && c.who != null && Tactics.Flat(c.lastPos - order.aimPoint).magnitude < 14f) ambushTriggered = true;

            if (ambushTriggered)
            {
                if (mode == Mode.InCover && cover != null) InCoverBehavior(); else FireAtTarget(tc, true);
                if (unitClass == UnitClass.Heavy && fireMode == FireMode.None) { suppressPoint = order.aimPoint + Vector3.up; fireMode = FireMode.Suppress; }
            }
            else
            {
                fireMode = FireMode.None;
                suppressPoint = order.aimPoint + Vector3.up; holdAim = true;   // pre-aimed
                if (cover != null && mode == Mode.InCover) { combatant.crouched = cover.IsLow; MoveTo(cover.position); }
            }
        }

        // ================================================================= cover handling
        bool CoverIsGood { get { return cover != null && (threats.Count == 0 || cover.BlockedFraction(threats) >= 0.5f); } }

        bool RequestCover(float radius, Vector3? goal, float goalW, float minThreat, bool sight, Mode m, Vector3? origin = null)
        {
            if (CoverMap.I == null)
            {
                if (BF3Debug.Allow("nocovermap", 30f)) BF3Debug.E(DbgCat.Cover, "No CoverMap in the scene: bots cannot take cover. Add a CoverMap and bake it.", this, uid);
                return false;
            }
            if (CoverMap.I.points.Count == 0)
            {
                if (BF3Debug.Allow("nocoverpts", 30f)) BF3Debug.E(DbgCat.Cover, "CoverMap has 0 cover points. Right-click the CoverMap component > Bake Cover Points.", this, uid);
                return false;
            }
            if (Time.time < nextCoverSearch) return cover != null;
            nextCoverSearch = Time.time + 1.2f;
            CoverPoint cp = CoverMap.I.FindBest(this, origin.HasValue ? origin.Value : transform.position, radius, threats, goal, goalW, minThreat, sight);
            if (cp == null)
            {
                if (BF3Debug.On(DbgLevel.Verbose, DbgCat.Cover))
                    BF3Debug.V(DbgCat.Cover, Tag + " found NO usable cover within " + radius.ToString("F0") + " m (threats " + threats.Count + ", mode " + mode + ", wantSight " + sight + ")", this, uid);
                return false;
            }
            if (cp != cover)
            {
                if (BF3Debug.On(DbgLevel.Verbose, DbgCat.Cover))
                    BF3Debug.V(DbgCat.Cover, Tag + " takes cover at " + cp.position.ToString("F1") + " (" + (cp.IsLow ? "LOW/crouch" : "FULL/stand") + " h=" + cp.height.ToString("F1") + ", blocks "
                        + (threats.Count > 0 ? (cp.BlockedFraction(threats) * 100f).ToString("F0") + "% of threats" : "n/a") + ", " + Vector3.Distance(transform.position, cp.position).ToString("F0") + " m away, room "
                        + (CoverMap.I != null ? CoverMap.I.RoomName(cp.room) : "-") + ")", this, uid);
                ReleaseCover();
                cover = cp; cp.occupant = this;
                movingToCover = true; mode = m; peeking = false;
                MoveTo(cp.position);
            }
            return true;
        }

        void ReleaseCover()
        {
            if (cover != null && cover.occupant == this) cover.occupant = null;
            cover = null; movingToCover = false;
        }

        /// <summary>Hide / peek cycle. Low cover: crouch when hidden, stand to fire. Full cover: stay standing, lean out to fire.</summary>
        void InCoverBehavior()
        {
            if (cover == null) return;
            Contact tc = ContactFor(target);

            if (Time.time >= peekSwitchTime)
            {
                peeking = !peeking;
                float hi = Mathf.Lerp(1.6f, 3.2f, Agg), lo = Mathf.Lerp(0.6f, 2.4f, personality.caution);
                peekSwitchTime = Time.time + (peeking ? Random.Range(hi * 0.6f, hi) : Random.Range(lo * 0.5f, lo));
            }
            if (unitClass == UnitClass.Heavy && !(suppression > 0.7f && personality.holdGround < 0.4f)) peeking = true;   // holds ground

            bool exposed = peeking;
            peekValid = false;
            if (exposed && tc != null) peekValid = cover.TryPeek(tc.PredictedPos + Vector3.up, combatant.standingHeight - 0.15f, out peekPos);

            combatant.crouched = !exposed && cover.IsLow;
            MoveTo(exposed && peekValid ? peekPos : cover.position);
            FireAtTarget(tc, exposed && (peekValid || tc == null));
        }

        // ================================================================= movement helpers
        void MoveTo(Vector3 p)
        {
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) || float.IsInfinity(p.x) || float.IsInfinity(p.z))
            {
                if (BF3Debug.Allow("nandest" + uid, 3f))
                    BF3Debug.E(DbgCat.Movement, Tag + " MoveTo received an invalid destination (mode " + mode + ", order " + (order != null ? order.type.ToString() : "none") + "). Ignored.", this, uid);
                return;
            }
            if ((p - lastDest).sqrMagnitude < 0.25f && Time.time < lastDestTime + 2f) return;
            if (!agent.isOnNavMesh) return;     // HandleOffMesh deals with it
            NavMeshHit nh;
            if (!NavMesh.SamplePosition(p, out nh, 4f, NavMesh.AllAreas))
            {
                // e.g. the player stands on a ledge/prop the NavMesh doesn't cover: go to the nearest walkable spot instead of freezing
                if (!NavMesh.SamplePosition(p, out nh, 16f, NavMesh.AllAreas))
                {
                    if (BF3Debug.Allow("nonav" + uid, 3f))
                        BF3Debug.W(DbgCat.Movement, Tag + " destination " + p.ToString("F1") + " has no NavMesh within 16 m (mode " + mode + "). Ignored. Is that spot outside the baked NavMesh?", this, uid);
                    return;
                }
                if (BF3Debug.Allow("navfar" + uid, 5f))
                    BF3Debug.V(DbgCat.Movement, Tag + " destination " + p.ToString("F1") + " is off the NavMesh; using nearest walkable point " + nh.position.ToString("F1") + " (" + nh.distance.ToString("F1") + " m away).", this, uid);
            }
            lastDest = p; lastDestTime = Time.time;
            agent.isStopped = false;
            if (!agent.SetDestination(nh.position) && BF3Debug.Allow("setdest" + uid, 3f))
                BF3Debug.W(DbgCat.Movement, Tag + " SetDestination(" + nh.position.ToString("F1") + ") returned false (mode " + mode + ").", this, uid);
        }

        /// <summary>Long trips choose the lane (left/straight/right) with the lowest historical death danger.</summary>
        void MoveAlongLane(Vector3 goal)
        {
            movingToCover = false; combatant.crouched = false;
            float d = Tactics.Flat(goal - transform.position).magnitude;
            DeathHeatmap hm = DeathHeatmap.I;
            if (d < 25f || hm == null) { MoveTo(goal); return; }

            if ((laneGoal - goal).sqrMagnitude > 25f || Time.time > laneUntil)
            {
                laneGoal = goal; laneUntil = Time.time + 6f; lanePassed = false;
                Vector3 mid = Vector3.Lerp(transform.position, goal, 0.5f);
                Vector3 perp = Vector3.Cross(Vector3.up, Tactics.Flat(goal - transform.position)).normalized;
                float best = float.MaxValue; laneMid = goal;
                for (int i = -1; i <= 1; i++)
                {
                    Vector3 m = Tactics.Nav(mid + perp * (i * 14f));
                    float cost = hm.PathDanger(transform.position, m, team) + hm.PathDanger(m, goal, team) + Mathf.Abs(i) * 0.1f;
                    if (cost < best) { best = cost; laneMid = m; }
                }
            }
            if (!lanePassed && Tactics.Flat(laneMid - transform.position).magnitude < 4f) lanePassed = true;
            MoveTo(lanePassed ? goal : laneMid);
        }

        // ================================================================= act (every frame)
        void Act()
        {
            bool run = mode == Mode.Charge || mode == Mode.Retreat || mode == Mode.Flank || mode == Mode.Relocate
                    || mode == Mode.Bait || mode == Mode.Panic || (mode == Mode.Advance && hasThreat)
                    || (movingToCover && mode != Mode.InCover);
            float speed = run ? profile.runSpeed : profile.walkSpeed;
            if (combatant.crouched) speed *= 0.55f;
            speed *= Mathf.Lerp(1f, 0.6f, suppression);
            agent.speed = speed;

            if (agent.velocity.sqrMagnitude > 12f && Time.time >= nextStep)
            {
                nextStep = Time.time + 0.55f;
                SoundBus.Emit(transform.position, 12f, team, SoundType.Footstep);
            }

            // arrival at cover
            if (movingToCover && cover != null && !agent.pathPending &&
                (Vector3.Distance(transform.position, cover.position) < 0.8f || agent.remainingDistance <= agent.stoppingDistance + 0.3f))
            {
                movingToCover = false; mode = Mode.InCover;
                coverHoldUntil = Time.time + Random.Range(1.5f, 3.5f) * (0.6f + personality.caution);
                peeking = false; peekSwitchTime = Time.time + Random.Range(0.3f, 1f);
            }

            // facing
            Vector3 look = Vector3.zero;
            if (fireMode != FireMode.None || holdAim) look = AimPos() - transform.position;
            else if (agent.velocity.sqrMagnitude > 0.3f) look = agent.velocity;
            else if (mode == Mode.InCover && cover != null) look = -cover.normal;
            look.y = 0f;
            if (look.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(look), 420f * Time.deltaTime);

            HandleFire();
        }

        // ================================================================= shooting
        Vector3 AimPos()
        {
            if (fireMode == FireMode.Aimed && target != null && target.IsAlive) return target.Chest;
            return suppressPoint;
        }

        void FireAtTarget(Contact tc, bool exposed)
        {
            if (tc == null) { fireMode = FireMode.None; return; }
            suppressPoint = tc.PredictedPos + Vector3.up;
            if (tc.visible && exposed) fireMode = FireMode.Aimed;
            else if (unitClass == UnitClass.Sniper) fireMode = FireMode.None;   // snipers never spray blind
            else if (tc.Age < 8f && unitClass == UnitClass.Heavy) fireMode = FireMode.Suppress;
            else if (tc.Age < 4f) fireMode = FireMode.Blind;                   // blind-fire at the last-known position (over low cover / round corners); bullets stop on any solid
            else fireMode = FireMode.None;
        }

        void HandleFire()
        {
            if (fireMode == FireMode.None || Time.time < nextShot) return;
            Vector3 origin = muzzle != null ? muzzle.position : Eye;
            Vector3 aim = AimPos();
            Vector3 dir = aim - origin;
            float dist = dir.magnitude;
            if (dist < 0.5f) return;
            dir /= dist;

            Vector3 flat = Tactics.Flat(dir);
            if (flat.sqrMagnitude > 0.001f && Vector3.Angle(transform.forward, flat) > 12f) return;   // still turning

            float interval = 1f / profile.shotsPerSecond;
            if (fireMode == FireMode.Blind) interval *= 1.6f;
            if (unitClass == UnitClass.Sniper) interval *= Random.Range(1f, 1.4f);
            nextShot = Time.time + interval;

            float spread = profile.spreadDeg * (1.4f - personality.marksmanship * 0.8f) * (1f + suppression * 1.5f);
            if (agent.velocity.magnitude > 1f) spread *= 1.5f;
            if (fireMode == FireMode.Suppress) spread = Mathf.Max(spread, 3f);
            if (fireMode == FireMode.Blind) spread *= 3f;
            if (Panicking) spread *= 2.5f;   // panicked units miss

            dir = Quaternion.AngleAxis(Random.Range(-spread, spread), Vector3.up)
                * Quaternion.AngleAxis(Random.Range(-spread, spread), transform.right) * dir;
            shots++;
            if (AIWorld.Fire(combatant, origin, dir, profile.viewRange * 1.2f, profile.damage)) hits++;
        }

        void TryGrenade(Contact tc)
        {
            if (grenades <= 0 || tc == null || Time.time < nextGrenade) return;
            float d = Tactics.Flat(tc.lastPos - transform.position).magnitude;
            if (d < 8f || d > 32f) return;
            int cluster = 0;
            foreach (Combatant o in Combatant.All)
                if (o.team != team && o.IsAlive && (o.transform.position - tc.lastPos).sqrMagnitude < 25f) cluster++;
            bool hiding = !tc.visible && tc.Age < 5f;
            if (!(hiding || cluster >= 2) || Random.value > 0.5f + 0.4f * Agg) return;
            nextGrenade = Time.time + Random.Range(6f, 10f);
            grenades--;
            if (BF3Debug.On(DbgLevel.Info, DbgCat.Grenade))
                BF3Debug.I(DbgCat.Grenade, Tag + " throws at " + tc.lastPos.ToString("F0") + " (" + d.ToString("F0") + " m, " + (tc.visible ? "visible" : "hiding target") + ", cluster " + cluster + "); " + grenades + " left.", this, uid);
            Grenade.Throw(grenadePrefab, Eye + transform.forward * 0.5f, tc.lastPos, combatant);
        }

        // ================================================================= debug / watchdog
        static int uidCounter;
        float nextWatch, stuckSince = -1f, faultedUntil, errWindowStart;
        Vector3 stuckPos;
        int errCount, stuckCount;
        bool frozen, respawnScheduled;

        public string Tag { get { return "[T" + (combatant != null ? combatant.team : -1) + " " + unitClass + "#" + uid + "]"; } }
        bool InspectorOn { get { return SquadManager.Instance != null && SquadManager.Instance.inspectorState; } }
        static string RoomLabel(int r) { return CoverMap.I != null ? r + ":" + CoverMap.I.RoomName(r) : r.ToString(); }

        void OnModeChange(Mode from, Mode to)
        {
            if (BF3Debug.On(DbgLevel.Verbose, DbgCat.Movement))
                BF3Debug.V(DbgCat.Movement, Tag + " mode " + from + " -> " + to + (order != null ? "  [order " + order.type + "]" : "") + (target != null ? "  target '" + target.name + "'" : ""), this, uid);
        }

        void OnOrderChange(Order from, Order to)
        {
            if (to == null && from != null && BF3Debug.On(DbgLevel.Verbose, DbgCat.Orders))
                BF3Debug.V(DbgCat.Orders, Tag + " order " + from.type + " ended (" + (from.Active ? (from.expires - Time.time).ToString("F1") + "s early: completed, ignored or cancelled" : "expired") + ")", this, uid);
        }

        void UpdateRoom()
        {
            int r = CoverMap.RoomOf(transform.position);
            if (r == room) return;
            if (BF3Debug.On(DbgLevel.Verbose, DbgCat.Rooms))
                BF3Debug.V(DbgCat.Rooms, Tag + " room " + RoomLabel(room) + " -> " + RoomLabel(r), this, uid);
            room = r;
        }

        void OnFault(Exception e)
        {
            if (Time.time - errWindowStart > 10f) { errWindowStart = Time.time; errCount = 0; }
            errCount++;
            BF3Debug.Exception("AIUnit.Tick", e, this, this);
            if (errCount >= 5)
            {
                faultedUntil = Time.time + 10f; errCount = 0;
                if (agent != null && agent.isOnNavMesh) agent.isStopped = true;
                fireMode = FireMode.None; order = null;
                BF3Debug.E(DbgCat.Watchdog, Tag + " FROZEN for 10 s after 5 exceptions in 10 s (the rest of the battle keeps running). " + Snapshot(), this, uid);
            }
        }

        void HandleOffMesh()
        {
            NavMeshHit h;
            if (NavMesh.SamplePosition(transform.position, out h, 5f, NavMesh.AllAreas) && agent.Warp(h.position))
            {
                if (BF3Debug.Allow("offmesh" + uid, 5f)) BF3Debug.W(DbgCat.Movement, Tag + " was off the NavMesh; snapped back to " + h.position.ToString("F1") + ".", this, uid);
                return;
            }
            if (BF3Debug.Allow("offmeshE" + uid, 5f))
                BF3Debug.E(DbgCat.Movement, Tag + " is off the NavMesh at " + transform.position.ToString("F1") + " with no NavMesh within 5 m. Idling 3 s.", this, uid);
            faultedUntil = Time.time + 3f;
        }

        /// <summary>Once a second: catches silent failures (stuck agents, bad bookkeeping, NaN positions).</summary>
        void Watchdog()
        {
            Vector3 p = transform.position;
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z))
            {
                BF3Debug.E(DbgCat.Watchdog, Tag + " has a NaN position! " + Snapshot(), this, uid);
                faultedUntil = Time.time + 5f; return;
            }
            if (squad == null) { if (BF3Debug.Allow("nosquad" + uid, 15f)) BF3Debug.W(DbgCat.Watchdog, Tag + " is alive but belongs to no squad (no pooled recon, orders or cohesion).", this, uid); }
            else if (squad.platoon == null) { if (BF3Debug.Allow("noplat" + uid, 15f)) BF3Debug.W(DbgCat.Watchdog, Tag + " squad " + squad.Tag + " has no platoon (cohesion defaults to 0.5, no strategy).", this, uid); }

            if (cover != null && cover.occupant != this)
            {
                if (cover.occupant == null || !cover.occupant.IsAlive) cover.occupant = this;
                else { BF3Debug.V(DbgCat.Cover, Tag + " lost its cover claim to " + cover.occupant.Tag + "; releasing.", this, uid); cover = null; movingToCover = false; }
            }

            bool wantsMove = agent.hasPath && !agent.pathPending && !agent.isStopped && agent.remainingDistance > 1.5f;
            if (wantsMove && (p - stuckPos).sqrMagnitude < 0.04f && agent.velocity.sqrMagnitude < 0.04f)
            {
                if (stuckSince < 0f) stuckSince = Time.time;
                else if (Time.time - stuckSince > 3f)
                {
                    stuckCount++;
                    BF3Debug.W(DbgCat.Watchdog, Tag + " STUCK for " + (Time.time - stuckSince).ToString("F1") + "s (#" + stuckCount + ") heading to " + agent.destination.ToString("F1")
                        + ", path " + agent.pathStatus + ", remaining " + agent.remainingDistance.ToString("F1") + ". Clearing path and re-planning.", this, uid);
                    agent.ResetPath(); lastDest = new Vector3(1e6f, 0f, 1e6f); nextCoverSearch = 0f; stuckSince = -1f;
                }
            }
            else stuckSince = -1f;
            stuckPos = p;

            if (agent.hasPath && agent.pathStatus == NavMeshPathStatus.PathInvalid && BF3Debug.Allow("badpath" + uid, 5f))
                BF3Debug.W(DbgCat.Watchdog, Tag + " has an INVALID path to " + agent.destination.ToString("F1") + " (mode " + mode + ").", this, uid);
        }

        public string Snapshot()
        {
            StringBuilder sb = new StringBuilder(480);
            sb.Append(Tag).Append(" pos ").Append(transform.position.ToString("F1")).Append(" room ").Append(RoomLabel(room));
            if (combatant != null) sb.Append(" hp ").Append(combatant.health.ToString("F0")).Append('/').Append(combatant.maxHealth.ToString("F0")).Append(combatant.crouched ? " crouched" : "");
            sb.Append(" | mode ").Append(mode).Append(" fire ").Append(fireMode);
            if (order != null) sb.Append(" | order ").Append(order.type).Append(orderAccepted ? "" : "(ignored)").Append(" ").Append(Tactics.Flat(order.point - transform.position).magnitude.ToString("F0")).Append("m ").Append(order.Active ? (order.expires - Time.time).ToString("F0") + "s left" : "expired");
            else sb.Append(" | order none");
            if (cover != null) sb.Append(" | cover ").Append(cover.IsLow ? "low" : "full").Append(movingToCover ? " (en route)" : " (in)").Append(' ').Append(Vector3.Distance(transform.position, cover.position).ToString("F1")).Append("m");
            else sb.Append(" | no cover");
            if (target != null) sb.Append(" | target '").Append(target.name).Append("' ").Append(Vector3.Distance(transform.position, target.transform.position).ToString("F0")).Append("m ").Append(perception != null && perception.IsVisible(target) ? "visible" : "not visible");
            else sb.Append(" | no target");
            sb.Append(" | known ").Append(known.Count).Append(" supp ").Append(suppression.ToString("F2")).Append(" shots/hits ").Append(shots).Append('/').Append(hits).Append(" kills ").Append(kills)
              .Append(" uptime ").Append(profile != null ? Uptime.ToString("F0") : "-").Append("s");
            if (squad != null) sb.Append(" | ").Append(squad.Tag).Append(squad.platoon != null ? "/" + squad.platoon.Tag + " " + squad.platoon.Strategy + " coh " + squad.platoon.Cohesion.ToString("F2") : "/no-platoon");
            else sb.Append(" | NO SQUAD");
            if (personality != null) sb.Append(" | ").Append(personality);
            if (agent != null)
            {
                sb.Append(" | agent onMesh=").Append(agent.isOnNavMesh).Append(" hasPath=").Append(agent.hasPath).Append(" status=").Append(agent.pathStatus)
                  .Append(" pending=").Append(agent.pathPending).Append(" stopped=").Append(agent.isStopped).Append(" speed=").Append(agent.velocity.magnitude.ToString("F1"));
                if (agent.isOnNavMesh && agent.hasPath) sb.Append(" remaining=").Append(agent.remainingDistance.ToString("F1"));
            }
            if (Time.time < faultedUntil) sb.Append(" | FAULTED ").Append((faultedUntil - Time.time).ToString("F0")).Append("s");
            if (stuckCount > 0) sb.Append(" | stuck x").Append(stuckCount);
            return sb.ToString();
        }

        string BuildState()
        {
            return mode + " / fire " + fireMode + " / order " + (order != null ? order.type + (orderAccepted ? "" : " (ignored)") : "none")
                 + " / target " + (target != null ? target.name : "none") + " / " + (cover != null ? (cover.IsLow ? "low" : "full") + " cover" : "no cover")
                 + " / room " + RoomLabel(room) + " / known " + known.Count + " / supp " + suppression.ToString("F2") + " / coh " + Cohesion.ToString("F2");
        }

        static Color OrderColor(OrderType t)
        {
            switch (t)
            {
                case OrderType.Advance: return Color.green;
                case OrderType.Flank: return new Color(1f, 0.55f, 0f);
                case OrderType.Bait: return Color.magenta;
                case OrderType.AmbushHold: return Color.red;
                case OrderType.Regroup: return Color.white;
                default: return Color.cyan;       // hold / overwatch / suppress
            }
        }

        void OnDrawGizmos()
        {
            SquadManager m = SquadManager.Instance;
            if (m == null || !Application.isPlaying || combatant == null || !IsAlive) return;
            if (m.drawOrders && order != null && order.Active)
            {
                Gizmos.color = OrderColor(order.type);
                Gizmos.DrawLine(transform.position + Vector3.up, order.point + Vector3.up * 0.5f);
                Gizmos.DrawWireSphere(order.point, 0.4f);
                Gizmos.DrawWireCube(order.aimPoint, Vector3.one * 0.3f);
            }
#if UNITY_EDITOR
            if (m.unitLabels)
                UnityEditor.Handles.Label(transform.position + Vector3.up * 2.3f,
                    Tag + " " + mode + (order != null ? " /" + order.type + (orderAccepted ? "" : "(ign)") : "") + "  hp" + combatant.health.ToString("F0") + "  r" + room);
#endif
        }

        void OnDrawGizmosSelected()
        {
            if (cover != null) { Gizmos.color = Color.green; Gizmos.DrawLine(transform.position, cover.position); }
            if (target != null) { Gizmos.color = Color.red; Gizmos.DrawLine(Eye, target.Chest); }
        }
    }
}