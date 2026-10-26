using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace BF3AI
{
    // =====================================================================================================
    //  SQUADMANAGER.CS  -  DURING-GAME DYNAMIC STATE
    //  Everything that changes while the match runs: the manager, squads, platoons, cohesion, death heatmap,
    //  in-match learning, sounds, shooting and grenades. Only SquadManager is a component; the rest is plain C#.
    // =====================================================================================================

    // =====================================================================================================
    //  BF3Debug - logging + black-box recorder.
    //  Every important decision is logged by category and level into a ring buffer (last N events). On any
    //  error the SquadManager can write a full report (settings, validation results, every platoon/squad/unit
    //  snapshot, and the log history) to a text file, so a problem can be traced back to the moment it began.
    //  Hotkeys (in play mode): F3 overlay, F8 cycle log level, F9 write report file.
    // =====================================================================================================
    [Flags]
    public enum DbgCat
    {
        None = 0, Setup = 1, Spawn = 2, Perception = 4, Cover = 8, Orders = 16, Movement = 32, Combat = 64,
        Squad = 128, Platoon = 256, Cohesion = 512, Learning = 1024, Rooms = 2048, Grenade = 4096,
        Watchdog = 8192, Exception = 16384, All = ~0
    }

    public enum DbgLevel { Off = 0, Error = 1, Warn = 2, Info = 3, Verbose = 4 }

    public static class BF3Debug
    {
        public static DbgLevel level = DbgLevel.Info;
        public static DbgCat mask = DbgCat.All;
        public static bool echoToConsole = true;
        public static int errors, warnings, exceptions;
        public static string lastError = "";

        public struct Entry { public float time; public int frame; public DbgLevel lvl; public DbgCat cat; public int uid; public string msg; }
        static Entry[] ring = new Entry[3000];
        static int head, count;

        class ExInfo { public float last; public int suppressed; }
        static readonly Dictionary<string, ExInfo> exInfo = new Dictionary<string, ExInfo>();
        static readonly Dictionary<string, float> throttle = new Dictionary<string, float>();

        public static void Reset()
        {
            head = 0; count = 0; errors = 0; warnings = 0; exceptions = 0; lastError = "";
            exInfo.Clear(); throttle.Clear();
        }

        public static void SetCapacity(int n)
        {
            n = Mathf.Max(200, n);
            if (n == ring.Length) return;
            Entry[] old = ring; int oc = count, oh = head;
            ring = new Entry[n]; head = 0; count = 0;
            for (int i = Mathf.Max(0, oc - n); i < oc; i++) Store(old[(oh - oc + i + old.Length * 2) % old.Length]);
        }

        /// <summary>Use before building an expensive message: if (BF3Debug.On(DbgLevel.Verbose, DbgCat.Cover)) ...</summary>
        public static bool On(DbgLevel l, DbgCat c) { return l <= level && (mask & c) != 0; }

        /// <summary>Rate limiter for repeating warnings. True = go ahead and log.</summary>
        public static bool Allow(string key, float seconds)
        {
            float now = Time.realtimeSinceStartup, last;
            if (throttle.TryGetValue(key, out last) && now - last < seconds) return false;
            throttle[key] = now;
            return true;
        }

        static void Store(Entry e)
        {
            ring[head] = e; head = (head + 1) % ring.Length;
            if (count < ring.Length) count++;
        }

        public static void Log(DbgLevel l, DbgCat c, string msg, UnityEngine.Object ctx = null, int uid = 0)
        {
            bool force = l <= DbgLevel.Warn;                 // errors and warnings are ALWAYS recorded, whatever the filter says
            if (!force && !On(l, c)) return;
            if (l == DbgLevel.Error) { errors++; lastError = msg; }
            else if (l == DbgLevel.Warn) warnings++;
            Store(new Entry { time = Time.time, frame = Time.frameCount, lvl = l, cat = c, uid = uid, msg = msg });

            if (!echoToConsole || l > level) return;
            string line = "[BF3AI/" + c + "] " + msg;
            if (l == DbgLevel.Error) Debug.LogError(line, ctx);
            else if (l == DbgLevel.Warn) Debug.LogWarning(line, ctx);
            else Debug.Log(line, ctx);
        }

        /// <summary>Always recorded and always echoed, but is NOT counted as an error/warning. For "report written" style notices.</summary>
        public static void Note(DbgCat c, string msg, UnityEngine.Object ctx = null)
        {
            Store(new Entry { time = Time.time, frame = Time.frameCount, lvl = DbgLevel.Info, cat = c, uid = 0, msg = msg });
            if (echoToConsole) Debug.Log("[BF3AI/" + c + "] " + msg, ctx);
        }

        public static void E(DbgCat c, string m, UnityEngine.Object ctx = null, int uid = 0) { Log(DbgLevel.Error, c, m, ctx, uid); }
        public static void W(DbgCat c, string m, UnityEngine.Object ctx = null, int uid = 0) { Log(DbgLevel.Warn, c, m, ctx, uid); }
        public static void I(DbgCat c, string m, UnityEngine.Object ctx = null, int uid = 0) { Log(DbgLevel.Info, c, m, ctx, uid); }
        public static void V(DbgCat c, string m, UnityEngine.Object ctx = null, int uid = 0) { Log(DbgLevel.Verbose, c, m, ctx, uid); }

        /// <summary>Logs an exception with stack trace + a snapshot of the unit. Identical repeats within 2 s are counted, not spammed.</summary>
        public static void Exception(string where, Exception e, AIUnit u, UnityEngine.Object ctx)
        {
            exceptions++;
            string top = "";
            if (e.StackTrace != null) { int nl = e.StackTrace.IndexOf('\n'); top = nl > 0 ? e.StackTrace.Substring(0, nl) : e.StackTrace; }
            string key = where + "|" + e.GetType().Name + "|" + top;
            ExInfo ei;
            if (!exInfo.TryGetValue(key, out ei)) { ei = new ExInfo(); exInfo[key] = ei; }
            float now = Time.realtimeSinceStartup;
            if (now - ei.last < 2f) { ei.suppressed++; return; }
            int sup = ei.suppressed; ei.suppressed = 0; ei.last = now;

            string msg = where + " threw " + e.GetType().Name + ": " + e.Message
                       + (sup > 0 ? "  (+" + sup + " identical suppressed)" : "")
                       + "\n" + e.StackTrace
                       + (u != null ? "\n  UNIT: " + u.Snapshot() : "");
            Log(DbgLevel.Error, DbgCat.Exception, msg, ctx, u != null ? u.uid : 0);
        }

        public static string Format(Entry e)
        {
            return e.time.ToString("F2").PadLeft(8) + "s f" + e.frame + " " + e.lvl.ToString()[0] + " " + e.cat.ToString().PadRight(10)
                 + (e.uid != 0 ? " #" + e.uid : "") + " " + e.msg;
        }

        /// <summary>Newest-last list of the last n entries at or above minLevel (optionally only for one unit uid).</summary>
        public static void Tail(StringBuilder sb, int n, DbgLevel minLevel, int uid = 0)
        {
            int found = 0; int[] idx = new int[n];
            for (int k = 0; k < count && found < n; k++)
            {
                int i = (head - 1 - k + ring.Length * 2) % ring.Length;
                Entry e = ring[i];
                if (e.lvl > minLevel) continue;
                if (uid != 0 && e.uid != uid) continue;
                idx[found++] = i;
            }
            for (int k = found - 1; k >= 0; k--) sb.Append(Format(ring[idx[k]])).Append('\n');
        }

        public static void AppendAll(StringBuilder sb)
        {
            for (int k = count - 1; k >= 0; k--)
            {
                int i = (head - 1 - k + ring.Length * 2) % ring.Length;
                sb.Append(Format(ring[i])).Append('\n');
            }
        }

        public static string WriteFile(string content)
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "BF3AI_report_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllText(path, content);
                return path;
            }
            catch (Exception ex) { Debug.LogError("[BF3AI] could not write report file: " + ex.Message); return null; }
        }
    }

    // =====================================================================================================
    //  SquadManager - the one component that runs the whole battle. Put ONE in the scene (empty GameObject).
    //  It owns: layer masks, the death heatmap, learning memory, spawning, respawning, objectives,
    //  squad -> platoon merging/splitting, grenade ticking, setup validation and the debug overlay/report.
    // =====================================================================================================
    public class SquadManager : MonoBehaviour
    {
        [Serializable]
        public class TeamConfig
        {
            public int team = 1;
            [Tooltip("Prefab with Combatant + AIUnit + NavMeshAgent (+ a collider). Same prefab is used for all four classes.")]
            public GameObject unitPrefab;
            [Tooltip("Optional. Visual for thrown grenades. If empty, a small sphere is generated.")]
            public GameObject grenadePrefab;
            public Transform[] spawnPoints;
            [Tooltip("Objectives this team pushes through in order (capture points, etc). Empty = hunt the nearest enemy.")]
            public Transform[] objectives;
            [Range(1, 3)] public int squadsPerTeam = 3;
            public float respawnDelay = 6f;
            [HideInInspector] public List<Platoon> platoons = new List<Platoon>();
            [HideInInspector] public List<Squad> squads = new List<Squad>();
        }

        public static SquadManager Instance;

        [Header("Teams")]
        public List<TeamConfig> teams = new List<TeamConfig>();

        [Header("Physics layers (required)")]
        [Tooltip("Everything solid in the level (the same layer(s) as the CoverMap's coverMask, e.g. your merged scene model). Blocks sight AND bullets completely. Must NOT include the Player, Bots or Room layers.")]
        public LayerMask obstacleMask = ~0;

        [Header("Platoon merging")]
        public float mergeRadius = 18f;
        [Tooltip("Squads in the SAME room volume may merge from this far apart (rooms can be big).")]
        public float sameRoomMergeDistance = 40f;
        public float splitDistance = 60f;
        public float objectiveRadius = 12f, objectiveHoldSeconds = 10f;

        [Header("Death heatmap")]
        public float heatCellSize = 4f;
        public float heatHalfLifeSeconds = 300f;
        public float heatDeathsForMax = 4f;
        public bool drawHeatmap = true;

        [Header("Match")]
        [Tooltip("Wipe learned data (heatmap, strategy stats, duel records) when the scene starts.")]
        public bool resetLearningOnStart = true;

        [Header("Debug  (F3 overlay, F8 cycle log level, F9 write report file)")]
        public DbgLevel logLevel = DbgLevel.Info;
        public DbgCat logCategories = DbgCat.All;
        public bool echoToConsole = true;
        [Tooltip("How many log events the black-box recorder keeps.")]
        public int logHistory = 3000;
        public bool validateOnStart = true;
        public bool overlay = true;
        [Tooltip("Scene-view labels above every bot (mode / order / hp / room) and order lines.")]
        public bool unitLabels = true;
        public bool drawOrders = true;
        [Tooltip("Writes a readable state string into each AIUnit's inspector ('Debug State').")]
        public bool inspectorState = true;
        [Tooltip("Write a full report file automatically when the first error appears (and on quit if errors occurred).")]
        public bool autoDumpOnError = true;
        [Tooltip("Optional: drag a bot here to always show its snapshot + log in the overlay.")]
        public AIUnit focusUnit;

        float nextMgmt, nextOverlay, nextHeartbeat, nextAutoDump;
        int errorsAtLastDump;
        string overlayText = "";
        string lastReportPath = "";
        GUIStyle style; Texture2D bg;

        public static SquadManager Of(int team) { return Instance; }

        TeamConfig Config(int team)
        {
            for (int i = 0; i < teams.Count; i++) if (teams[i].team == team) return teams[i];
            return null;
        }

        // ================================================================= setup
        void Awake()
        {
            if (Instance != null && Instance != this)
                BF3Debug.E(DbgCat.Setup, "A second SquadManager exists in the scene. Only one may run; remove '" + name + "'.", this);
            Instance = this;
            BF3Debug.Reset();
            ApplyDebugSettings();
            AIWorld.Obstacles = obstacleMask;
            DeathHeatmap.I = new DeathHeatmap(heatCellSize, heatHalfLifeSeconds, heatDeathsForMax);
            if (resetLearningOnStart) LearningMemory.ResetAll();
            LearningMemory.Hook();
            Grenade.Clear();
            BF3Debug.I(DbgCat.Setup, "SquadManager awake. Obstacles=" + MaskName(obstacleMask), this);
        }

        void Start()
        {
            if (validateOnStart) Validate();
            for (int t = 0; t < teams.Count; t++)
            {
                try { SpawnTeam(teams[t]); }
                catch (Exception e) { BF3Debug.Exception("SpawnTeam(team " + teams[t].team + ")", e, null, this); }
            }
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        void OnApplicationQuit()
        {
            if (autoDumpOnError && BF3Debug.errors > errorsAtLastDump) DumpReport("quit with errors");
        }

        void ApplyDebugSettings()
        {
            BF3Debug.level = logLevel; BF3Debug.mask = logCategories; BF3Debug.echoToConsole = echoToConsole;
            BF3Debug.SetCapacity(logHistory);
        }

        static string MaskName(LayerMask m)
        {
            if (m.value == 0) return "(nothing)";
            if (m.value == ~0) return "(everything)";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 32; i++)
                if ((m.value & (1 << i)) != 0) { string n = LayerMask.LayerToName(i); sb.Append(string.IsNullOrEmpty(n) ? "L" + i : n).Append(' '); }
            return sb.ToString().Trim();
        }

        static T[] FindAll<T>() where T : UnityEngine.Object
        {
#if UNITY_2023_1_OR_NEWER
            return FindObjectsByType<T>(FindObjectsSortMode.None);
#else
            return FindObjectsOfType<T>();
#endif
        }

        // ================================================================= validation
        readonly List<string> validation = new List<string>();
        int vErr, vWarn;

        void VErr(string m) { vErr++; validation.Add("ERROR  " + m); BF3Debug.E(DbgCat.Setup, m, this); }
        void VWarn(string m) { vWarn++; validation.Add("WARN   " + m); BF3Debug.W(DbgCat.Setup, m, this); }
        void VInfo(string m) { validation.Add("ok     " + m); BF3Debug.I(DbgCat.Setup, m, this); }

        [ContextMenu("BF3AI/Run Validation")]
        public void Validate()
        {
            validation.Clear(); vErr = 0; vWarn = 0;

            if (FindAll<SquadManager>().Length > 1) VErr("More than one SquadManager in the scene.");

            // ---- NavMesh
            NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
            if (tri.vertices == null || tri.vertices.Length == 0)
                VErr("No NavMesh found. Bake one (Window > AI > Navigation) or no bot can move.");
            else VInfo("NavMesh: " + tri.vertices.Length + " vertices, " + (tri.indices.Length / 3) + " triangles.");

            // ---- CoverMap / rooms
            CoverMap cm = CoverMap.I;
            if (cm == null) { CoverMap[] f = FindAll<CoverMap>(); if (f.Length > 0) cm = f[0]; }
            if (cm == null) VErr("No CoverMap in the scene. Bots will never take cover. Add one and right-click > Bake.");
            else
            {
                if (cm.points.Count == 0) VErr("CoverMap has 0 baked cover points. Bake after the NavMesh exists and coverMask is set.");
                else
                {
                    int off = 0, n = 0, step = Mathf.Max(1, cm.points.Count / 300);
                    for (int i = 0; i < cm.points.Count; i += step)
                    {
                        n++; NavMeshHit h;
                        if (!NavMesh.SamplePosition(cm.points[i].position, out h, 0.6f, NavMesh.AllAreas)) off++;
                    }
                    if (off > 0) VWarn("CoverMap: " + off + " of " + n + " sampled cover points are not on the NavMesh. Re-bake after changing the NavMesh.");
                    else VInfo("CoverMap: " + cm.points.Count + " cover points (sampled points all on NavMesh).");
                }
                if (cm.rooms.Count == 0)
                    VWarn("CoverMap has no rooms baked: 'split across rooms' cohesion and same-room merging fall back to distance. Put big TRIGGER colliders on a Room layer, set roomMask, re-bake.");
                else
                {
                    for (int i = 0; i < cm.rooms.Count; i++)
                    {
                        RoomInfo r = cm.rooms[i];
                        if (r == null || r.collider == null) { VErr("Room[" + i + "] collider is missing. Re-bake."); continue; }
                        if (!r.collider.isTrigger) VErr("Room '" + r.name + "' is not a trigger collider. It will block bullets/sight/movement.");
                        if (!r.collider.gameObject.activeInHierarchy || !r.collider.enabled) VWarn("Room '" + r.name + "' is disabled; units will never be 'inside' it.");
                        if (((1 << r.collider.gameObject.layer) & obstacleMask.value) != 0)
                            VErr("Room '" + r.name + "' is on a layer included in obstacleMask: it would block line of sight.");
                    }
                    VInfo("Rooms: " + cm.rooms.Count + " volumes.");
                }
            }

            // ---- character layers vs obstacleMask
            if (obstacleMask.value == 0) VWarn("obstacleMask is empty: bots can see and shoot through walls.");
            if (cm != null && cm.coverMask.value != 0 && (obstacleMask.value & cm.coverMask.value) == 0)
                VErr("obstacleMask does not include the CoverMap's coverMask layer(s) (" + MaskName(cm.coverMask) + "). Bots would see and shoot through your level geometry, and cover would not hide anyone. Add the level layer to obstacleMask.");
            if (cm != null && cm.coverMask.value != 0 && (obstacleMask.value & cm.coverMask.value) != cm.coverMask.value)
                VWarn("obstacleMask only covers part of the CoverMap's coverMask (" + MaskName(obstacleMask) + " vs " + MaskName(cm.coverMask) + "). Geometry on the missing layer(s) gives cover that does not block sight.");
            for (int t = 0; t < teams.Count; t++)
            {
                TeamConfig cfg = teams[t];
                if (cfg.unitPrefab != null && ((1 << cfg.unitPrefab.layer) & obstacleMask.value) != 0)
                    VErr("obstacleMask includes layer '" + LayerMask.LayerToName(cfg.unitPrefab.layer) + "' used by team " + cfg.team + " bots. Units would block each other's sight. Restrict obstacleMask to static geometry.");
            }
            foreach (Combatant c in Combatant.All)
                if (c.isPlayer && ((1 << c.gameObject.layer) & obstacleMask.value) != 0)
                    VErr("obstacleMask includes the player's layer '" + LayerMask.LayerToName(c.gameObject.layer) + "'. Bots could never see you. Restrict obstacleMask to static geometry.");

            // ---- teams
            if (teams.Count == 0) VErr("SquadManager has no teams configured.");
            for (int t = 0; t < teams.Count; t++)
            {
                TeamConfig cfg = teams[t]; string tn = "Team " + cfg.team + ": ";
                if (cfg.team == 0) VErr(tn + "team 0 is reserved for the player. Bots on team 0 would be friendly to you.");
                for (int j = t + 1; j < teams.Count; j++) if (teams[j].team == cfg.team) VWarn(tn + "duplicate team id in the Teams list.");

                if (cfg.unitPrefab == null) { VErr(tn + "unitPrefab is empty."); }
                else
                {
                    if (cfg.unitPrefab.GetComponent<AIUnit>() == null) VErr(tn + "prefab '" + cfg.unitPrefab.name + "' has no AIUnit.");
                    if (cfg.unitPrefab.GetComponent<Combatant>() == null) VErr(tn + "prefab has no Combatant.");
                    if (cfg.unitPrefab.GetComponent<NavMeshAgent>() == null) VErr(tn + "prefab has no NavMeshAgent.");
                    if (cfg.unitPrefab.GetComponentInChildren<Collider>() == null) VErr(tn + "prefab has no Collider; bullets can never hit it.");
                }

                if (cfg.spawnPoints == null || cfg.spawnPoints.Length == 0) VErr(tn + "no spawn points.");
                else
                    for (int i = 0; i < cfg.spawnPoints.Length; i++)
                    {
                        Transform sp = cfg.spawnPoints[i];
                        if (sp == null) { VErr(tn + "spawnPoints[" + i + "] is empty."); continue; }
                        NavMeshHit h;
                        if (!NavMesh.SamplePosition(sp.position, out h, 3f, NavMesh.AllAreas))
                            VErr(tn + "spawn point '" + sp.name + "' is not within 3 m of the NavMesh.");
                    }

                if (cfg.objectives != null)
                    for (int i = 0; i < cfg.objectives.Length; i++)
                    {
                        if (cfg.objectives[i] == null) { VWarn(tn + "objectives[" + i + "] is empty."); continue; }
                        NavMeshHit h;
                        if (!NavMesh.SamplePosition(cfg.objectives[i].position, out h, 8f, NavMesh.AllAreas))
                            VWarn(tn + "objective '" + cfg.objectives[i].name + "' is not near the NavMesh; squads may never reach it.");
                    }
                if (cfg.objectives == null || cfg.objectives.Length == 0) VInfo(tn + "no objectives: this team will hunt the nearest enemy.");
            }

            // ---- player
            bool player = false;
            foreach (Combatant c in Combatant.All)
            {
                if (!c.isPlayer) continue;
                player = true;
                for (int t = 0; t < teams.Count; t++)
                    if (teams[t].team == c.team) VErr("Player Combatant is on team " + c.team + ", the same as bot team " + teams[t].team + ". Bots won't attack you.");
                if (c.GetComponentInChildren<Collider>() == null) VWarn("Player has no Collider: bot bullets cannot hit you.");
            }
            if (!player) VWarn("No Combatant with isPlayer ticked in the scene. Bots will only fight each other (or hunt nothing).");

            if (Time.timeScale == 0f) VWarn("Time.timeScale is 0: nothing will update.");

            string summary = "Validation finished: " + vErr + " error(s), " + vWarn + " warning(s).";
            if (vErr > 0) BF3Debug.E(DbgCat.Setup, summary + " Fix the ERROR lines above before trusting the AI.", this);
            else BF3Debug.I(DbgCat.Setup, summary, this);
        }

        // ================================================================= spawning
        void SpawnTeam(TeamConfig cfg)
        {
            if (cfg.unitPrefab == null || cfg.spawnPoints == null || cfg.spawnPoints.Length == 0)
            {
                BF3Debug.E(DbgCat.Spawn, "Team " + cfg.team + " cannot spawn: needs a unitPrefab and at least one spawn point.", this);
                return;
            }
            UnitClass[] roster = { UnitClass.Assault, UnitClass.Heavy, UnitClass.Sniper, UnitClass.Officer };

            for (int s = 0; s < cfg.squadsPerTeam; s++)
            {
                Squad squad = new Squad(cfg.team);
                Transform sp = cfg.spawnPoints[s % cfg.spawnPoints.Length];
                if (sp == null) { BF3Debug.E(DbgCat.Spawn, "Team " + cfg.team + " spawnPoints[" + (s % cfg.spawnPoints.Length) + "] is null.", this); continue; }
                for (int k = 0; k < 4; k++)
                {
                    try
                    {
                        Vector3 pos = Tactics.Nav(sp.position + sp.right * ((k - 1.5f) * 1.5f), 3f);
                        GameObject go = Instantiate(cfg.unitPrefab, pos, sp.rotation);
                        AIUnit u = go.GetComponent<AIUnit>();
                        if (u == null) { BF3Debug.E(DbgCat.Spawn, "Spawned prefab has no AIUnit; destroying it.", go); Destroy(go); continue; }
                        u.unitClass = roster[k];
                        u.combatant.team = cfg.team;
                        if (cfg.grenadePrefab != null) u.grenadePrefab = cfg.grenadePrefab;
                        go.name = "T" + cfg.team + "_S" + squad.id + "_" + roster[k];
                        u.BeginLife(pos, sp.rotation);
                        squad.Add(u);
                    }
                    catch (Exception e) { BF3Debug.Exception("SpawnTeam unit " + roster[k], e, null, this); }
                }
                Platoon p = new Platoon(cfg.team);
                p.squads.Add(squad); squad.platoon = p;
                p.RecomputeCommander();
                cfg.squads.Add(squad); cfg.platoons.Add(p);
                BF3Debug.I(DbgCat.Spawn, "Spawned " + squad.Tag + " in " + p.Tag + " (" + squad.members.Count + " units) at '" + sp.name + "'.", this);
            }
        }

        public void ScheduleRespawn(AIUnit u)
        {
            TeamConfig cfg = Config(u.team);
            if (cfg == null) { BF3Debug.E(DbgCat.Spawn, u.Tag + " died but no TeamConfig exists for team " + u.team + "; it will not respawn.", u, u.uid); return; }
            BF3Debug.V(DbgCat.Spawn, u.Tag + " respawn in " + cfg.respawnDelay.ToString("F1") + "s.", u, u.uid);
            StartCoroutine(Respawn(u, cfg.respawnDelay));
        }

        IEnumerator Respawn(AIUnit u, float delay)
        {
            yield return new WaitForSeconds(delay);
            try
            {
                if (u == null) { BF3Debug.W(DbgCat.Spawn, "Respawn aborted: unit object was destroyed while dead.", this); yield break; }
                TeamConfig cfg = Config(u.team);
                if (cfg == null || cfg.spawnPoints == null || cfg.spawnPoints.Length == 0) { BF3Debug.E(DbgCat.Spawn, u.Tag + " cannot respawn: no spawn points for team " + u.team + ".", u, u.uid); yield break; }

                Transform sp = cfg.spawnPoints[Random.Range(0, cfg.spawnPoints.Length)];
                Vector3 pos = Tactics.Nav(sp.position + Random.insideUnitSphere * 2f, 4f);
                u.gameObject.SetActive(true);
                u.BeginLife(pos, sp.rotation);       // new generation: new personality, learned data carries over
                if (u.squad != null && u.squad.platoon != null) u.squad.platoon.RecomputeCommander();
                BF3Debug.I(DbgCat.Spawn, u.Tag + " respawned at '" + sp.name + "', rejoining " + (u.squad != null ? u.squad.Tag + " (" + u.squad.AliveCount + " alive)" : "NO SQUAD") + ".", u, u.uid);
            }
            catch (Exception e) { BF3Debug.Exception("Respawn", e, u, u); }
        }

        // ================================================================= per-frame
        void Update()
        {
            ApplyDebugSettings();
            HandleHotkeys();

            float dt = Time.deltaTime;
            for (int t = 0; t < teams.Count; t++)
                for (int i = 0; i < teams[t].platoons.Count; i++)
                {
                    Platoon p = teams[t].platoons[i];
                    try { p.Tick(dt); }
                    catch (Exception e) { BF3Debug.Exception("Platoon.Tick " + p.Tag, e, null, this); }
                }
            try { Grenade.Tick(); }
            catch (Exception e) { BF3Debug.Exception("Grenade.Tick", e, null, this); }

            if (Time.time >= nextMgmt)
            {
                nextMgmt = Time.time + 1f;
                for (int t = 0; t < teams.Count; t++)
                {
                    try { UpdateObjectives(teams[t]); }
                    catch (Exception e) { BF3Debug.Exception("UpdateObjectives team " + teams[t].team, e, null, this); }
                    try { MergeAndSplit(teams[t]); }
                    catch (Exception e) { BF3Debug.Exception("MergeAndSplit team " + teams[t].team, e, null, this); }
                }
            }

            if (Time.time >= nextHeartbeat && BF3Debug.On(DbgLevel.Info, DbgCat.Setup))
            {
                nextHeartbeat = Time.time + 30f;
                StringBuilder sb = new StringBuilder("heartbeat: ");
                for (int t = 0; t < teams.Count; t++)
                {
                    int alive = 0, total = 0;
                    for (int s = 0; s < teams[t].squads.Count; s++) { alive += teams[t].squads[s].AliveCount; total += teams[t].squads[s].members.Count; }
                    sb.Append("T").Append(teams[t].team).Append(' ').Append(alive).Append('/').Append(total).Append(" alive in ").Append(teams[t].platoons.Count).Append(" platoon(s); ");
                }
                sb.Append("errors=").Append(BF3Debug.errors).Append(" warnings=").Append(BF3Debug.warnings).Append(" exceptions=").Append(BF3Debug.exceptions);
                BF3Debug.I(DbgCat.Setup, sb.ToString(), this);
            }

            if (autoDumpOnError && BF3Debug.errors > errorsAtLastDump && Time.unscaledTime >= nextAutoDump)
            {
                nextAutoDump = Time.unscaledTime + 20f;
                DumpReport("auto: error logged");
            }

            if (Time.unscaledTime >= nextOverlay)
            {
                nextOverlay = Time.unscaledTime + 0.25f;
                if (overlay) { try { overlayText = BuildOverlay(); } catch (Exception e) { overlayText = "overlay error: " + e.Message; } }
            }
        }

        void HandleHotkeys()
        {
#if ENABLE_INPUT_SYSTEM
            UnityEngine.InputSystem.Keyboard kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            if (kb.f3Key.wasPressedThisFrame) overlay = !overlay;
            if (kb.f8Key.wasPressedThisFrame) CycleLevel();
            if (kb.f9Key.wasPressedThisFrame) DumpReport("hotkey F9");
#else
            if (Input.GetKeyDown(KeyCode.F3)) overlay = !overlay;
            if (Input.GetKeyDown(KeyCode.F8)) CycleLevel();
            if (Input.GetKeyDown(KeyCode.F9)) DumpReport("hotkey F9");
#endif
        }

        void CycleLevel()
        {
            logLevel = logLevel >= DbgLevel.Verbose ? DbgLevel.Error : (DbgLevel)((int)logLevel + 1);
            if (logLevel == DbgLevel.Off) logLevel = DbgLevel.Error;
            ApplyDebugSettings();
            BF3Debug.Note(DbgCat.Setup, "log level -> " + logLevel, this);
        }

        // ================================================================= objectives
        void UpdateObjectives(TeamConfig cfg)
        {
            for (int i = 0; i < cfg.platoons.Count; i++)
            {
                Platoon p = cfg.platoons[i];
                if (p.AliveUnits() == 0) continue;
                Vector3 pc = p.Center();

                if (cfg.objectives != null && cfg.objectives.Length > 0)
                {
                    if (p.objectiveIndex < 0 || p.objectiveIndex >= cfg.objectives.Length)
                    {
                        float bd = float.MaxValue; int pick = -1;
                        for (int k = 0; k < cfg.objectives.Length; k++)
                        {
                            if (cfg.objectives[k] == null) continue;
                            float d = Tactics.Flat(cfg.objectives[k].position - pc).sqrMagnitude;
                            if (d < bd) { bd = d; pick = k; }
                        }
                        if (pick < 0) { p.hasObjective = false; continue; }
                        p.objectiveIndex = pick;
                        BF3Debug.I(DbgCat.Platoon, p.Tag + " objective -> [" + pick + "] '" + cfg.objectives[pick].name + "'.", this);
                    }
                    Transform o = cfg.objectives[p.objectiveIndex];
                    if (o == null) { p.objectiveIndex = -1; continue; }
                    p.objective = Tactics.Nav(o.position, 8f); p.hasObjective = true;

                    if (Tactics.Flat(o.position - pc).magnitude < objectiveRadius)
                    {
                        p.objectiveTimer += 1f;
                        if (p.objectiveTimer >= objectiveHoldSeconds)
                        {
                            p.objectiveTimer = 0f;
                            int next = (p.objectiveIndex + 1) % cfg.objectives.Length;
                            BF3Debug.I(DbgCat.Platoon, p.Tag + " secured '" + o.name + "' (+0.15 cohesion); next objective [" + next + "].", this);
                            p.objectiveIndex = next;
                            p.Add(0.15f);         // objective progress lifts morale
                        }
                    }
                    else p.objectiveTimer = Mathf.Max(0f, p.objectiveTimer - 1f);
                }
                else
                {
                    Combatant best = null; float bd = float.MaxValue;
                    foreach (Combatant c in Combatant.All)
                    {
                        if (c.team == cfg.team || !c.IsAlive) continue;
                        float d = Tactics.Flat(c.transform.position - pc).sqrMagnitude;
                        if (d < bd) { bd = d; best = c; }
                    }
                    p.hasObjective = best != null;
                    if (best != null) p.objective = Tactics.Nav(best.transform.position, 8f);
                    else if (BF3Debug.Allow("noenemy" + p.id, 15f)) BF3Debug.W(DbgCat.Platoon, p.Tag + " has no objectives configured and no living enemy to hunt: it will idle.", this);
                }
            }
        }

        // ================================================================= merge / split (rooms are giant colliders)
        void MergeAndSplit(TeamConfig cfg)
        {
            bool rooms = CoverMap.RoomsEnabled;

            // ---- split: broken morale, or a squad wandering off alone / into another room
            for (int i = cfg.platoons.Count - 1; i >= 0; i--)
            {
                Platoon p = cfg.platoons[i];
                if (p.squads.Count < 2) continue;
                Vector3 pc = p.Center();
                for (int s = p.squads.Count - 1; s >= 0 && p.squads.Count > 1; s--)
                {
                    Squad sq = p.squads[s];
                    if (sq.AliveCount == 0) continue;
                    float dist = Tactics.Flat(sq.Center() - pc).magnitude;
                    bool far = dist > splitDistance;
                    bool broken = p.Cohesion < 0.15f;
                    if (broken || far)
                    {
                        BF3Debug.I(DbgCat.Platoon, "SPLIT " + sq.Tag + " off " + p.Tag + " (" + (broken ? "cohesion " + p.Cohesion.ToString("F2") + " < 0.15" : "distance " + dist.ToString("F0") + " m > " + splitDistance) + ").", this);
                        cfg.platoons.Add(p.Detach(sq));
                    }
                }
            }

            // ---- merge: same zone (same room volume, else close + walkable), not broken, total <= 3 squads
            for (int i = 0; i < cfg.platoons.Count; i++)
            {
                Platoon a = cfg.platoons[i];
                if (a.AliveUnits() == 0 || a.Cohesion <= 0.3f) continue;
                for (int j = cfg.platoons.Count - 1; j > i; j--)
                {
                    Platoon b = cfg.platoons[j];
                    if (b.AliveUnits() == 0 || b.Cohesion <= 0.3f) continue;
                    if (a.squads.Count + b.squads.Count > 3) continue;

                    Vector3 ca = a.Center(), cb = b.Center();
                    float dist = Tactics.Flat(ca - cb).magnitude;
                    int ra = rooms ? a.CurrentRoom() : -1, rb = rooms ? b.CurrentRoom() : -1;
                    bool sameRoom = ra >= 0 && ra == rb;
                    bool differentRooms = ra >= 0 && rb >= 0 && ra != rb;
                    if (differentRooms) continue;                                       // separate rooms = separate zones
                    float maxDist = sameRoom ? Mathf.Max(mergeRadius, sameRoomMergeDistance) : mergeRadius;
                    if (dist > maxDist) continue;

                    float len = Squad.PathLength(ca, cb);
                    if (len < 0f || len > maxDist * 1.6f)
                    {
                        if (BF3Debug.On(DbgLevel.Verbose, DbgCat.Platoon) && BF3Debug.Allow("mergepath" + a.id + "_" + b.id, 5f))
                            BF3Debug.V(DbgCat.Platoon, "merge " + a.Tag + "+" + b.Tag + " rejected: path " + (len < 0f ? "unreachable" : len.ToString("F0") + " m") + " (limit " + (maxDist * 1.6f).ToString("F0") + ").", this);
                        continue;
                    }
                    BF3Debug.I(DbgCat.Platoon, "MERGE " + b.Tag + " into " + a.Tag + " (" + (sameRoom ? "same room '" + CoverMap.I.RoomName(ra) + "'" : "no room data, " + dist.ToString("F0") + " m apart") + ", path " + len.ToString("F0") + " m). Now " + (a.squads.Count + b.squads.Count) + " squads.", this);
                    a.Absorb(b);
                    cfg.platoons.RemoveAt(j);
                }
            }
            for (int i = cfg.platoons.Count - 1; i >= 0; i--)
                if (cfg.platoons[i].squads.Count == 0) cfg.platoons.RemoveAt(i);
        }

        // ================================================================= report + overlay
        [ContextMenu("BF3AI/Write Report File")]
        void DumpFromMenu() { DumpReport("context menu"); }

        [ContextMenu("BF3AI/Print All Units To Console")]
        void PrintUnits()
        {
            for (int t = 0; t < teams.Count; t++)
                for (int s = 0; s < teams[t].squads.Count; s++)
                    for (int m = 0; m < teams[t].squads[s].members.Count; m++)
                    {
                        AIUnit u = teams[t].squads[s].members[m];
                        Debug.Log(u != null ? u.Snapshot() : "(destroyed unit)");
                    }
        }

        public void DumpReport(string why)
        {
            errorsAtLastDump = BF3Debug.errors;
            string path = BF3Debug.WriteFile(BuildReport(why));
            if (path != null)
            {
                lastReportPath = path;
                GUIUtility.systemCopyBuffer = path;
                BF3Debug.Note(DbgCat.Setup, "REPORT WRITTEN (" + why + "): " + path + "   (path copied to clipboard)", this);
            }
        }

        string BuildReport(string why)
        {
            StringBuilder sb = new StringBuilder(32768);
            sb.Append("BF3AI REPORT  (").Append(why).Append(")\n");
            sb.Append("Unity ").Append(Application.unityVersion).Append("  scene '").Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)
              .Append("'  t=").Append(Time.time.ToString("F1")).Append("s  frame ").Append(Time.frameCount)
              .Append("  timeScale ").Append(Time.timeScale).Append("  fps~").Append((1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f)).ToString("F0")).Append('\n');
            sb.Append("errors=").Append(BF3Debug.errors).Append(" warnings=").Append(BF3Debug.warnings).Append(" exceptions=").Append(BF3Debug.exceptions)
              .Append("\nlast error: ").Append(BF3Debug.lastError).Append('\n');
            sb.Append("obstacleMask=").Append(MaskName(obstacleMask))
              .Append("  logLevel=").Append(logLevel).Append("  categories=").Append(logCategories).Append('\n');
            CoverMap cm = CoverMap.I;
            sb.Append("CoverMap: ").Append(cm == null ? "MISSING" : cm.points.Count + " points, " + cm.rooms.Count + " rooms").Append('\n');

            sb.Append("\n==== SETUP VALIDATION ====\n");
            if (validation.Count == 0) sb.Append("(not run)\n");
            for (int i = 0; i < validation.Count; i++) sb.Append(validation[i]).Append('\n');

            sb.Append("\n==== PLATOONS / SQUADS / UNITS ====\n");
            for (int t = 0; t < teams.Count; t++)
            {
                sb.Append("-- team ").Append(teams[t].team).Append('\n');
                for (int i = 0; i < teams[t].platoons.Count; i++)
                {
                    Platoon p = teams[t].platoons[i];
                    sb.Append(p.Describe()).Append('\n');
                    for (int s = 0; s < p.squads.Count; s++)
                    {
                        Squad sq = p.squads[s];
                        sb.Append("    ").Append(sq.Describe()).Append('\n');
                        for (int m = 0; m < sq.members.Count; m++)
                        {
                            AIUnit u = sq.members[m];
                            if (u == null) sb.Append("      (destroyed)\n");
                            else if (!u.isActiveAndEnabled) sb.Append("      ").Append(u.Tag).Append(" DEAD / waiting to respawn\n");
                            else sb.Append("      ").Append(u.Snapshot()).Append('\n');
                        }
                    }
                }
            }

            sb.Append("\n==== LEARNING ====\n");
            for (int t = 0; t < teams.Count; t++)
            {
                TeamLearning L = LearningMemory.Get(teams[t].team);
                sb.Append("team ").Append(teams[t].team).Append(": generation ").Append(L.generation)
                  .Append(" aggrMod[A,H,S,O]=").Append(L.aggrMod[0].ToString("F2")).Append(',').Append(L.aggrMod[1].ToString("F2")).Append(',').Append(L.aggrMod[2].ToString("F2")).Append(',').Append(L.aggrMod[3].ToString("F2"))
                  .Append(" focusKills=").Append(L.focusKills).Append(" soloKills=").Append(L.soloKills).Append(" focusWeight=").Append(L.FocusWeight.ToString("F2")).Append('\n');
            }

            sb.Append("\n==== LOG HISTORY (oldest first) ====\n");
            BF3Debug.AppendAll(sb);
            return sb.ToString();
        }

        AIUnit LookedAt()
        {
            if (focusUnit != null && focusUnit.isActiveAndEnabled) return focusUnit;
            Camera cam = Camera.main;
            if (cam == null) return null;
            RaycastHit h;
            if (Physics.SphereCast(cam.transform.position, 0.6f, cam.transform.forward, out h, 250f, ~0, QueryTriggerInteraction.Ignore))
                return h.collider.GetComponentInParent<AIUnit>();
            return null;
        }

        string BuildOverlay()
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("<b>BF3AI debug</b>   F3 overlay | F8 log level [").Append(logLevel).Append("] | F9 write report\n");
            string ec = BF3Debug.errors > 0 ? "#ff6060" : "#80ff80";
            sb.Append("<color=").Append(ec).Append(">errors ").Append(BF3Debug.errors).Append("</color>  warnings ").Append(BF3Debug.warnings).Append("  exceptions ").Append(BF3Debug.exceptions);
            if (BF3Debug.errors > 0) sb.Append("\n<color=#ff9090>last error: ").Append(Trim(BF3Debug.lastError, 150)).Append("</color>");
            if (lastReportPath.Length > 0) sb.Append("\nlast report: ").Append(lastReportPath);
            CoverMap cm = CoverMap.I;
            sb.Append("\nCoverMap ").Append(cm == null ? "<color=#ff6060>MISSING</color>" : cm.points.Count + " pts / " + cm.rooms.Count + " rooms").Append('\n');

            for (int t = 0; t < teams.Count; t++)
            {
                int alive = 0, total = 0;
                for (int s = 0; s < teams[t].squads.Count; s++) { alive += teams[t].squads[s].AliveCount; total += teams[t].squads[s].members.Count; }
                sb.Append("\n<b>Team ").Append(teams[t].team).Append("</b>  ").Append(alive).Append('/').Append(total).Append(" alive, ").Append(teams[t].platoons.Count).Append(" platoon(s)\n");
                for (int i = 0; i < teams[t].platoons.Count; i++)
                {
                    Platoon p = teams[t].platoons[i];
                    sb.Append(' ').Append(p.Describe()).Append('\n');
                    sb.Append("    cohesion/s: drift ").Append(p.dDrift.ToString("+0.000;-0.000")).Append(" spread ").Append(p.dSpread.ToString("+0.000;-0.000"))
                      .Append(" supp ").Append(p.dSupp.ToString("+0.000;-0.000")).Append(" split ").Append(p.dSplit.ToString("+0.000;-0.000"))
                      .Append(" rooms ").Append(p.dRoom.ToString("+0.000;-0.000")).Append(" progress ").Append(p.dProgress.ToString("+0.000;-0.000")).Append('\n');
                    for (int s = 0; s < p.squads.Count; s++) sb.Append("    ").Append(p.squads[s].Describe()).Append('\n');
                }
            }

            AIUnit look = LookedAt();
            if (look != null)
            {
                sb.Append("\n<b>Watching</b> ").Append(look.Snapshot()).Append('\n');
                BF3Debug.Tail(sb, 8, DbgLevel.Verbose, look.uid);
            }
            sb.Append("\n<b>Log tail</b>\n");
            BF3Debug.Tail(sb, 12, logLevel == DbgLevel.Off ? DbgLevel.Error : logLevel);
            return sb.ToString();
        }

        static string Trim(string s, int n) { if (s == null) return ""; s = s.Replace('\n', ' '); return s.Length <= n ? s : s.Substring(0, n) + "..."; }

        void OnGUI()
        {
            if (!overlay || string.IsNullOrEmpty(overlayText)) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true, wordWrap = false };
                style.normal.textColor = Color.white;
                bg = new Texture2D(1, 1); bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.7f)); bg.Apply();
            }
            Vector2 size = style.CalcSize(new GUIContent(overlayText));
            Rect r = new Rect(8, 8, Mathf.Min(size.x + 14f, Screen.width - 16f), Mathf.Min(size.y + 10f, Screen.height - 16f));
            GUI.DrawTexture(r, bg);
            GUI.Label(new Rect(r.x + 7f, r.y + 5f, r.width, r.height), overlayText, style);
        }

        // ================================================================= gizmos
        void OnDrawGizmos()
        {
            if (drawHeatmap && Application.isPlaying && DeathHeatmap.I != null) DeathHeatmap.I.DrawGizmos();
            if (!Application.isPlaying) return;
            for (int t = 0; t < teams.Count; t++)
                for (int i = 0; i < teams[t].platoons.Count; i++)
                {
                    Platoon p = teams[t].platoons[i];
                    if (p.AliveUnits() == 0) continue;
                    Vector3 pc = p.Center() + Vector3.up * 2f;
                    Gizmos.color = Color.Lerp(Color.red, Color.green, p.Cohesion);
                    Gizmos.DrawWireSphere(pc, 1f);
                    if (p.Commander != null) Gizmos.DrawLine(pc, p.Commander.transform.position + Vector3.up * 2.5f);
                    for (int s = 0; s < p.squads.Count; s++)
                        if (p.squads[s].AliveCount > 0) Gizmos.DrawLine(pc, p.squads[s].Center() + Vector3.up * 2f);
                    if (p.hasObjective) { Gizmos.color = Color.magenta; Gizmos.DrawLine(pc, p.objective + Vector3.up); }
                }
        }
    }

    /// <summary>Global sound events. AI "hears" these instead of psychically tracking you.</summary>
    public static class SoundBus
    {
        public static event Action<Vector3, float, int, SoundType> OnSound; // pos, radius, sourceTeam, type
        public static void Emit(Vector3 pos, float radius, int team, SoundType type) { OnSound?.Invoke(pos, radius, team, type); }
    }
    public enum SoundType { Gunshot, Footstep, Explosion }

    /// <summary>Global settings + hitscan shooting shared by AI and anything else that wants to fire.</summary>
    public static class AIWorld
    {
        /// <summary>Static geometry that blocks vision and bullets. Must NOT include character layers.</summary>
        public static LayerMask Obstacles = ~0;

        static readonly RaycastHit[] buf = new RaycastHit[16];
        static readonly HitComparer cmp = new HitComparer();
        sealed class HitComparer : IComparer<RaycastHit>
        {
            public int Compare(RaycastHit a, RaycastHit b) { return a.distance.CompareTo(b.distance); }
        }

        public static bool Fire(Combatant shooter, Vector3 origin, Vector3 dir, float range, float damage)
        {
            if (shooter == null || float.IsNaN(dir.x) || float.IsNaN(dir.y) || float.IsNaN(dir.z) || float.IsNaN(origin.x))
            {
                if (BF3Debug.Allow("badfire", 3f)) BF3Debug.E(DbgCat.Combat, "AIWorld.Fire called with " + (shooter == null ? "a null shooter" : "a NaN origin/direction") + ". Shot ignored.");
                return false;
            }
            SoundBus.Emit(origin, 70f, shooter.team, SoundType.Gunshot);
            int n = Physics.RaycastNonAlloc(origin, dir, buf, range, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(buf, 0, n, cmp);

            Vector3 end = origin + dir * range;
            bool hit = false;
            // every solid stops a bullet completely: there is no see-through or penetrable cover
            float dmg = damage;
            for (int i = 0; i < n; i++)
            {
                RaycastHit h = buf[i];
                if (h.collider.transform.IsChildOf(shooter.transform)) continue;
                Combatant c = h.collider.GetComponentInParent<Combatant>();
                if (c != null)
                {
                    if (c.team == shooter.team || !c.IsAlive) continue;
                    end = h.point; hit = true;
                    c.TakeDamage(dmg, shooter);
                    break;
                }
                end = h.point;
                break;
            }

            // near-miss suppression on enemy AI
            Vector3 ab = end - origin;
            float abSq = Mathf.Max(ab.sqrMagnitude, 0.0001f);
            foreach (AIUnit a in AIUnit.All)
            {
                if (a.team == shooter.team || !a.IsAlive) continue;
                Vector3 p = a.combatant.Chest;
                float t = Mathf.Clamp01(Vector3.Dot(p - origin, ab) / abSq);
                float d = Vector3.Distance(origin + ab * t, p);
                if (d < 2.5f) a.AddSuppression(0.12f * (1f - d / 2.5f));
            }
            Debug.DrawLine(origin, end, Color.yellow, 0.05f);
            return hit;
        }
    }

    public static class Tactics
    {
        /// <summary>Standard normal sample (Box-Muller).</summary>
        public static float Gauss()
        {
            float u1 = 1f - Random.value, u2 = Random.value;
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        }

        public static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        public static Vector3 Nav(Vector3 p, float radius = 6f)
        {
            NavMeshHit h;
            return NavMesh.SamplePosition(p, out h, radius, NavMesh.AllAreas) ? h.position : p;
        }

        /// <summary>Pick a step toward a target, choosing the lane (straight or +/-35 deg) with the lowest historical death danger.</summary>
        public static Vector3 SafeStep(Vector3 from, Vector3 toward, float step, int team, float minEnemyDist)
        {
            Vector3 dir = Flat(toward - from);
            float dist = dir.magnitude;
            step = Mathf.Clamp(dist - minEnemyDist, 0f, step);
            if (step < 1f) return from;
            dir /= dist;

            Vector3 best = from; float bestCost = float.MaxValue;
            for (int i = -1; i <= 1; i++)
            {
                Vector3 d = Quaternion.AngleAxis(i * 35f, Vector3.up) * dir;
                Vector3 p = Nav(from + d * step);
                float cost = Mathf.Abs(i) * 0.15f;
                DeathHeatmap hm = DeathHeatmap.I;
                if (hm != null) cost += hm.PathDanger(from, p, team) + hm.GetDanger(p, team);
                if (cost < bestCost) { bestCost = cost; best = p; }
            }
            return best;
        }
    }
    /// <summary>
    /// Thrown grenades. No MonoBehaviour or prefab component needed: SquadManager ticks the live list.
    /// 45 degree ballistic throw, 2.5 s fuse, 6 m radius, 120 damage with falloff, never hurts the thrower's team.
    /// </summary>
    public static class Grenade
    {
        public static float fuse = 2.5f, radius = 6f, maxDamage = 120f;

        class Live { public GameObject go; public Rigidbody rb; public float boom; public Combatant owner; }
        static readonly List<Live> live = new List<Live>();
        static readonly List<Combatant> copy = new List<Combatant>();

        public static void Clear() { live.Clear(); }

        public static void Throw(GameObject prefab, Vector3 from, Vector3 to, Combatant owner)
        {
            GameObject go;
            if (prefab != null) go = UnityEngine.Object.Instantiate(prefab, from, Quaternion.identity);
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.transform.position = from; go.transform.localScale = Vector3.one * 0.2f;
                go.GetComponent<Renderer>().material.color = new Color(0.2f, 0.25f, 0.2f);
            }
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();

            // 45 deg launch: v^2 = g d^2 / (d - h)
            Vector3 flat = Tactics.Flat(to - from);
            float d = Mathf.Max(1f, flat.magnitude);
            float h = to.y - from.y;
            float g = Mathf.Abs(Physics.gravity.y);
            float v = Mathf.Sqrt(g * d * d / Mathf.Max(0.5f, d - h));
            Vector3 vel = flat.normalized * (v * 0.7071f) + Vector3.up * (v * 0.7071f);
            vel += Random.insideUnitSphere * 0.4f;     // a little error so throws aren't laser-perfect
            if (float.IsNaN(vel.x) || float.IsNaN(vel.y) || float.IsNaN(vel.z) || float.IsInfinity(vel.x) || float.IsInfinity(vel.y) || float.IsInfinity(vel.z))
            {
                BF3Debug.E(DbgCat.Grenade, "Grenade throw produced an invalid velocity (from " + from.ToString("F1") + " to " + to.ToString("F1") + "); grenade discarded.");
                UnityEngine.Object.Destroy(go);
                return;
            }
            if (BF3Debug.On(DbgLevel.Info, DbgCat.Grenade))
                BF3Debug.I(DbgCat.Grenade, (owner != null ? owner.name : "?") + " throws a grenade " + d.ToString("F0") + " m (speed " + vel.magnitude.ToString("F1") + " m/s).");
#if UNITY_6000_0_OR_NEWER
            rb.linearVelocity = vel;
#else
            rb.velocity = vel;
#endif
            live.Add(new Live { go = go, rb = rb, boom = Time.time + fuse, owner = owner });
        }

        public static void Tick()
        {
            for (int i = live.Count - 1; i >= 0; i--)
            {
                Live g = live[i];
                if (g.go == null) { live.RemoveAt(i); continue; }
                if (Time.time < g.boom) continue;
                Vector3 pos = g.go.transform.position; Combatant ow = g.owner; GameObject go = g.go;
                live.RemoveAt(i);                                   // remove first so a failing Explode can never loop forever
                UnityEngine.Object.Destroy(go);
                try { Explode(pos, ow); }
                catch (Exception e) { BF3Debug.Exception("Grenade.Explode", e, null, null); }
            }
        }

        static void Explode(Vector3 pos, Combatant owner)
        {
            int team = owner != null ? owner.team : -1;
            SoundBus.Emit(pos, 80f, team, SoundType.Explosion);

            copy.Clear(); copy.AddRange(Combatant.All);     // TakeDamage can deactivate units and mutate the list
            int hits = 0, shielded = 0;
            for (int i = 0; i < copy.Count; i++)
            {
                Combatant c = copy[i];
                if (c == null || !c.IsAlive || c.team == team) continue;
                Vector3 p = c.Chest;
                float d = Vector3.Distance(pos, p);
                if (d > radius) continue;
                if (Physics.Linecast(pos + Vector3.up * 0.2f, p, AIWorld.Obstacles, QueryTriggerInteraction.Ignore)) { shielded++; continue; }   // walls shield
                c.TakeDamage(maxDamage * (1f - d / radius), owner);
                hits++;
            }
            foreach (AIUnit a in AIUnit.All)
                if (a.team != team && a.IsAlive)
                {
                    float d = Vector3.Distance(pos, a.transform.position);
                    if (d < radius * 2f) a.AddSuppression(0.5f * (1f - d / (radius * 2f)));
                }
            BF3Debug.I(DbgCat.Grenade, "Grenade exploded at " + pos.ToString("F0") + ": hit " + hits + ", shielded by walls " + shielded + ".");
        }
    }

    /// <summary>
    /// Spatial heatmap of where units die (per team). Hallways and cover nodes with high casualty rates
    /// get a high "danger" score, which becomes negative utility in cover selection, lane choice and flank planning.
    /// Values decay slowly so old mistakes fade but repeated failures across generations stay hot.
    /// </summary>
    public class DeathHeatmap
    {
        public static DeathHeatmap I;

        public float cellSize = 4f;
        public float halfLifeSeconds = 300f;
        public float deathsForMaxDanger = 4f;

        struct Cell { public float deaths, visits, stamp; }
        readonly Dictionary<Vector3Int, Cell> cells = new Dictionary<Vector3Int, Cell>();

        public DeathHeatmap(float cellSize, float halfLifeSeconds, float deathsForMaxDanger)
        {
            this.cellSize = cellSize; this.halfLifeSeconds = halfLifeSeconds; this.deathsForMaxDanger = deathsForMaxDanger;
        }

        Vector3Int Key(Vector3 p, int team)
        {
            return new Vector3Int(Mathf.FloorToInt(p.x / cellSize), team, Mathf.FloorToInt(p.z / cellSize));
        }

        Cell Decay(Cell c)
        {
            float dt = Time.time - c.stamp;
            if (dt > 0f)
            {
                float f = Mathf.Pow(0.5f, dt / halfLifeSeconds);
                c.deaths *= f; c.visits *= f;
            }
            c.stamp = Time.time;
            return c;
        }

        public void RecordDeath(Vector3 p, int team)
        {
            Vector3Int k0 = Key(p, team);
            float dBefore = GetDanger(p, team);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    Vector3Int k = new Vector3Int(k0.x + dx, team, k0.z + dz);
                    Cell c;
                    c = cells.TryGetValue(k, out c) ? Decay(c) : new Cell { stamp = Time.time };
                    c.deaths += (dx == 0 && dz == 0) ? 1f : 0.35f;
                    cells[k] = c;
                }
            if (BF3Debug.On(DbgLevel.Verbose, DbgCat.Learning))
                BF3Debug.V(DbgCat.Learning, "heatmap: team " + team + " death at " + p.ToString("F0") + "; danger there " + dBefore.ToString("F2") + " -> " + GetDanger(p, team).ToString("F2"));
        }

        /// <summary>Units passing through a cell. Lets us tell "deadly" from merely "busy".</summary>
        public void RecordVisit(Vector3 p, int team)
        {
            Vector3Int k = Key(p, team);
            Cell c;
            c = cells.TryGetValue(k, out c) ? Decay(c) : new Cell { stamp = Time.time };
            c.visits += 1f;
            cells[k] = c;
        }

        /// <summary>0 = safe, 1 = proven death trap.</summary>
        public float GetDanger(Vector3 p, int team)
        {
            Cell c;
            if (!cells.TryGetValue(Key(p, team), out c)) return 0f;
            c = Decay(c);
            float rate = c.deaths / (c.deaths + c.visits * 0.25f + 1f);
            return Mathf.Clamp01(c.deaths / deathsForMaxDanger) * Mathf.Lerp(0.5f, 1f, rate);
        }

        /// <summary>Danger of a hallway/route between two points.</summary>
        public float PathDanger(Vector3 a, Vector3 b, int team)
        {
            float d = Vector3.Distance(a, b);
            int n = Mathf.Max(1, Mathf.CeilToInt(d / cellSize));
            float sum = 0f, max = 0f;
            for (int i = 0; i <= n; i++)
            {
                float v = GetDanger(Vector3.Lerp(a, b, (float)i / n), team);
                sum += v; if (v > max) max = v;
            }
            return max * 0.6f + (sum / (n + 1)) * 0.4f;
        }

        public void Clear() { cells.Clear(); }

        /// <summary>Called from SquadManager.OnDrawGizmos.</summary>
        public void DrawGizmos()
        {
            foreach (KeyValuePair<Vector3Int, Cell> kv in cells)
            {
                Cell c = Decay(kv.Value);
                float d = Mathf.Clamp01(c.deaths / deathsForMaxDanger);
                if (d < 0.05f) continue;
                Gizmos.color = new Color(1f, 1f - d, 0f, 0.15f + 0.4f * d);
                Vector3 center = new Vector3((kv.Key.x + 0.5f) * cellSize, 0.5f, (kv.Key.z + 0.5f) * cellSize);
                Gizmos.DrawCube(center, new Vector3(cellSize, 1f, cellSize));
            }
        }
    }
    /// <summary>Everything worth remembering about one AI life (= one generation).</summary>
    public struct LifeRecord
    {
        public UnitClass unitClass;
        public int team;
        public Vector3 deathPos;
        public Combatant killer;
        public float killerDist;
        public bool wasInCover;
        public float lifetime;
        public int kills;
    }

    /// <summary>Per-team knowledge that carries from one life to the next during a match.</summary>
    public class TeamLearning
    {
        public int generation;
        public readonly float[] aggrMod = new float[4];          // per-class aggression shift, learned
        public readonly float[] lifeEma = { 30f, 30f, 30f, 30f };  // smoothed lifespan per class
        public float focusKills, soloKills;

        readonly Dictionary<Combatant, Vector2> duels = new Dictionary<Combatant, Vector2>();      // x = AI wins, y = AI losses
        readonly Dictionary<string, Vector2> strategies = new Dictionary<string, Vector2>();       // x = attempts, y = successes

        /// <summary>How strongly to favour focus fire / grouped targets (0.5 .. 1.5).</summary>
        public float FocusWeight { get { return 0.5f + (focusKills + 1f) / (focusKills + soloKills + 2f); } }

        public void RecordDuelWin(Combatant enemy)
        {
            if (enemy == null) return;
            Vector2 d; duels.TryGetValue(enemy, out d); d.x += 1f; duels[enemy] = d;
        }

        public void RecordDuelLoss(Combatant enemy)
        {
            if (enemy == null) return;
            Vector2 d; duels.TryGetValue(enemy, out d); d.y += 1f; duels[enemy] = d;
        }

        /// <summary>0..1. High = this enemy keeps beating us in duels, so avoid fighting them alone.</summary>
        public float Respect(Combatant enemy)
        {
            Vector2 d;
            if (enemy == null || !duels.TryGetValue(enemy, out d)) return 0f;
            float n = d.x + d.y;
            if (n < 2f) return 0f;
            return Mathf.Clamp01((d.y - d.x) / (n + 1f));
        }

        public void RecordStrategy(string name, bool success)
        {
            Vector2 s; strategies.TryGetValue(name, out s);
            s.x += 1f; if (success) s.y += 1f;
            strategies[name] = s;
        }

        /// <summary>1.0 when untested; rises/falls with observed success.</summary>
        public float StrategyWeight(string name)
        {
            Vector2 s; strategies.TryGetValue(name, out s);
            return Mathf.Clamp(((s.y + 1f) / (s.x + 2f)) * 2f, 0.25f, 2f);
        }

        public void RecordLife(LifeRecord r)
        {
            generation++;
            int c = (int)r.unitClass;
            lifeEma[c] = Mathf.Lerp(lifeEma[c], r.lifetime, 0.3f);

            // Die fast in the open -> get less reckless. Live long -> get a bit bolder.
            float m = aggrMod[c];
            if (r.lifetime < 12f) m -= r.wasInCover ? 0.02f : 0.04f;
            else if (r.lifetime > 45f) m += 0.02f;
            float oldMod = aggrMod[c];
            aggrMod[c] = Mathf.Clamp(m, -0.3f, 0.2f);
            DbgLevel lv = Mathf.Abs(aggrMod[c] - oldMod) > 0.0001f ? DbgLevel.Info : DbgLevel.Verbose;
            if (BF3Debug.On(lv, DbgCat.Learning))
                BF3Debug.Log(lv, DbgCat.Learning, "team " + r.team + " gen " + generation + ": " + r.unitClass + " lived " + r.lifetime.ToString("F0") + "s ("
                    + (r.wasInCover ? "in cover" : "exposed") + ", " + r.kills + " kills), killed by " + (r.killer != null ? r.killer.name : "nobody") + " at " + r.killerDist.ToString("F0")
                    + " m. aggrMod " + oldMod.ToString("F2") + " -> " + aggrMod[c].ToString("F2") + ", life EMA " + lifeEma[c].ToString("F0") + "s");

            if (r.killer != null) RecordDuelLoss(r.killer);
            if (DeathHeatmap.I != null) DeathHeatmap.I.RecordDeath(r.deathPos, r.team);
        }
    }

    public static class LearningMemory
    {
        static readonly Dictionary<int, TeamLearning> teams = new Dictionary<int, TeamLearning>();

        public static TeamLearning Get(int team)
        {
            TeamLearning t;
            if (!teams.TryGetValue(team, out t)) { t = new TeamLearning(); teams[team] = t; }
            return t;
        }

        public static void ResetAll()
        {
            teams.Clear();
            if (DeathHeatmap.I != null) DeathHeatmap.I.Clear();
        }

        /// <summary>Idempotent. Called by SquadManager.</summary>
        public static void Hook()
        {
            Combatant.AnyDeath -= OnAnyDeath;
            Combatant.AnyDeath += OnAnyDeath;
        }

        static void OnAnyDeath(Combatant victim, Combatant killer)
        {
            if (killer == null) return;
            AIUnit ku = killer.GetComponent<AIUnit>();
            if (ku == null) return;

            ku.kills++;
            if (ku.squad != null) ku.squad.OnKill(ku, victim);

            TeamLearning L = Get(ku.team);
            L.RecordDuelWin(victim);

            int focus = 0;
            foreach (AIUnit u in AIUnit.All)
                if (u != ku && u.team == ku.team && u.target == victim) focus++;
            if (focus >= 1) L.focusKills++; else L.soloKills++;
            if (BF3Debug.On(DbgLevel.Verbose, DbgCat.Combat))
                BF3Debug.V(DbgCat.Combat, ku.Tag + " killed '" + victim.name + "' (" + (focus >= 1 ? "focus fire x" + (focus + 1) : "solo") + "). Team focusWeight now " + L.FocusWeight.ToString("F2") + ".", ku, ku.uid);
        }
    }
    /// <summary>What the platoon commander asks a whole squad to do. The squad turns this into per-unit Orders.</summary>
    public enum SquadTask { Advance, Bound, Fix, FlankLeft, FlankRight, Ambush }

    /// <summary>
    /// Four units (Assault, Heavy, Sniper, Officer). Members persist across deaths: a dead member is simply not "live"
    /// until SquadManager respawns it, at which point it rejoins the surviving squadmates.
    /// Cohesion is owned by the Platoon (a lone squad is a platoon of one).
    /// </summary>
    public class Squad
    {
        public readonly List<AIUnit> members = new List<AIUnit>(4);
        public Platoon platoon;
        public int team;

        // pooled recon: everything any member has actually SEEN (sound-only contacts are not shared)
        readonly Dictionary<Combatant, Contact> pool = new Dictionary<Combatant, Contact>();
        readonly Dictionary<Combatant, float> priority = new Dictionary<Combatant, float>();
        readonly List<Combatant> tmpKeys = new List<Combatant>();
        float nextMatrix;
        Vector3 lastCenter;

        // task state
        SquadTask lastTask = (SquadTask)(-1);
        float lastPlan = -99f;
        Vector3 holdPoint;
        int boundPhase; bool boundStarted, boundBlocked; float phaseStart; Vector3 boundPoint;
        bool ambushSet, baitIssued, baitDone; Vector3 ambushAnchor, ambushKill, ambushExpose; AIUnit baitUnit;

        static int idCounter;
        public readonly int id;
        public string Tag { get { return "S" + id; } }
        public SquadTask Task { get { return lastTask; } }
        public Squad(int team) { this.team = team; id = ++idCounter; }

        /// <summary>Room (index) most living members stand in, -1 = open/unmapped or nobody alive.</summary>
        public static int ModalRoom(List<AIUnit> list, out int inModal, out int live)
        {
            live = 0; inModal = 0; int best = -1;
            for (int i = 0; i < list.Count; i++) if (Live(list[i])) live++;
            for (int i = 0; i < list.Count; i++)
            {
                if (!Live(list[i])) continue;
                int r = list[i].room, c = 0;
                for (int j = 0; j < list.Count; j++) if (Live(list[j]) && list[j].room == r) c++;
                if (c > inModal) { inModal = c; best = r; }
            }
            return best;
        }
        public int ModalRoom() { int a, b; return ModalRoom(members, out a, out b); }

        public string Describe()
        {
            return Tag + " " + AliveCount + "/" + members.Count + " alive  task=" + ((int)lastTask < 0 ? "-" : lastTask.ToString())
                 + "  room=" + (CoverMap.I != null ? CoverMap.I.RoomName(ModalRoom()) : "-") + "  pooled enemies=" + pool.Count;
        }

        // ================================================================= basic queries
        public float Cohesion { get { return platoon != null ? platoon.Cohesion : 0.5f; } }
        public bool hasObjective { get { return platoon != null && platoon.hasObjective; } }
        public Vector3 objective { get { return platoon != null ? platoon.objective : lastCenter; } }
        public IEnumerable<Contact> PooledContacts { get { return pool.Values; } }

        public static bool Live(AIUnit u) { return u != null && u.isActiveAndEnabled && u.IsAlive; }

        public void Add(AIUnit u) { if (!members.Contains(u)) members.Add(u); u.squad = this; }

        public int AliveCount
        {
            get { int n = 0; for (int i = 0; i < members.Count; i++) if (Live(members[i])) n++; return n; }
        }

        public Vector3 Center()
        {
            Vector3 sum = Vector3.zero; int n = 0;
            for (int i = 0; i < members.Count; i++)
                if (Live(members[i])) { sum += members[i].transform.position; n++; }
            if (n > 0) lastCenter = sum / n;
            return lastCenter;
        }

        /// <summary>Centre of everyone alive except 'self'. Falls back to self's position so "rejoin" logic stays quiet.</summary>
        public Vector3 MateCenter(AIUnit self)
        {
            Vector3 sum = Vector3.zero; int n = 0;
            for (int i = 0; i < members.Count; i++)
            {
                AIUnit m = members[i];
                if (m == self || !Live(m)) continue;
                sum += m.transform.position; n++;
            }
            return n > 0 ? sum / n : self.transform.position;
        }

        public AIUnit NearestMate(AIUnit u)
        {
            AIUnit best = null; float bd = float.MaxValue;
            for (int i = 0; i < members.Count; i++)
            {
                AIUnit m = members[i];
                if (m == u || !Live(m)) continue;
                float d = (m.transform.position - u.transform.position).sqrMagnitude;
                if (d < bd) { bd = d; best = m; }
            }
            return best;
        }

        /// <summary>Slot around the objective: assaults lead, heavy/officer/sniper trail.</summary>
        public Vector3 FormationPoint(AIUnit u)
        {
            Vector3 obj = objective;
            Vector3 dir = Tactics.Flat(obj - Center());
            if (dir.sqrMagnitude < 4f) return Tactics.Nav(obj);
            dir.Normalize();
            return Tactics.Nav(obj + Slot(u, dir), 4f);
        }

        Vector3 Slot(AIUnit u, Vector3 dir)
        {
            Vector3 side = Vector3.Cross(Vector3.up, dir);
            int idx = Mathf.Max(0, members.IndexOf(u));
            float back = u.unitClass == UnitClass.Assault ? 0f : u.unitClass == UnitClass.Heavy ? 3f : u.unitClass == UnitClass.Officer ? 4f : 6f;
            return side * ((idx - 1.5f) * 2.5f) - dir * back;
        }

        // ================================================================= pooled recon + target matrix
        public void Report(Contact c)
        {
            if (c.who == null || c.heardOnly) return;
            Contact p;
            if (pool.TryGetValue(c.who, out p))
            {
                if (p.lastSeen >= c.lastSeen) return;
                p.lastPos = c.lastPos; p.lastVel = c.lastVel; p.lastSeen = c.lastSeen; p.uncertainty = c.uncertainty;
            }
            else
            {
                Contact k = c.Clone();
                k.visible = false;      // for the receiver this is "last known", not live sight
                k.heardOnly = false;
                pool[c.who] = k;
                if (BF3Debug.On(DbgLevel.Verbose, DbgCat.Perception))
                    BF3Debug.V(DbgCat.Perception, Tag + " pooled new enemy '" + c.who.name + "' at " + c.lastPos.ToString("F0") + ".");
            }
        }

        public int CountTargeting(Combatant t, AIUnit except)
        {
            int n = 0;
            for (int i = 0; i < members.Count; i++)
                if (members[i] != except && Live(members[i]) && members[i].target == t) n++;
            return n;
        }

        /// <summary>0..1 squad-wide priority for a target (from the shared threat matrix).</summary>
        public float PriorityOf(Combatant t)
        {
            float v; return t != null && priority.TryGetValue(t, out v) ? v : 0f;
        }

        public void Tick(float dt)
        {
            if (Time.time < nextMatrix) return;
            nextMatrix = Time.time + 1f;

            tmpKeys.Clear();
            foreach (KeyValuePair<Combatant, Contact> kv in pool)
                if (kv.Key == null || !kv.Key.IsAlive || kv.Value.Age > 10f) tmpKeys.Add(kv.Key);
            for (int i = 0; i < tmpKeys.Count; i++) pool.Remove(tmpKeys[i]);

            // rebuild the shared threat matrix: average each member's (squad-free) score for every pooled enemy
            priority.Clear(); tmpKeys.Clear();
            float min = float.MaxValue, max = float.MinValue;
            foreach (KeyValuePair<Combatant, Contact> kv in pool)
            {
                float s = 0f; int n = 0;
                for (int i = 0; i < members.Count; i++)
                    if (Live(members[i])) { s += ThreatMatrix.Score(members[i], kv.Value, false); n++; }
                if (n == 0) continue;
                s /= n;
                priority[kv.Key] = s; tmpKeys.Add(kv.Key);
                if (s < min) min = s;
                if (s > max) max = s;
            }
            float range = Mathf.Max(0.001f, max - min);
            for (int i = 0; i < tmpKeys.Count; i++) priority[tmpKeys[i]] = (priority[tmpKeys[i]] - min) / range;
        }

        // ================================================================= events (feed platoon cohesion)
        public void OnMemberDied(AIUnit u)
        {
            BF3Debug.I(DbgCat.Squad, Tag + " lost " + u.Tag + "; " + AliveCount + "/" + members.Count + " alive.", u, u.uid);
            if (platoon != null) platoon.RegisterCasualty();
            else BF3Debug.W(DbgCat.Squad, Tag + " has no platoon; casualty not counted for cohesion.", u, u.uid);
        }
        public void OnKill(AIUnit u, Combatant victim)
        {
            if (platoon != null) platoon.RegisterKill();
        }

        // ================================================================= task -> orders
        void StartTask(SquadTask task)
        {
            if (BF3Debug.On(DbgLevel.Info, DbgCat.Squad))
                BF3Debug.I(DbgCat.Squad, Tag + " task " + ((int)lastTask < 0 ? "-" : lastTask.ToString()) + " -> " + task + "  (" + AliveCount + " alive, cohesion " + Cohesion.ToString("F2") + ")");
            lastTask = task;
            holdPoint = Center();
            boundPhase = 0; boundStarted = false; boundBlocked = false;
            ambushSet = false; baitUnit = null; baitIssued = false; baitDone = false;
        }

        /// <summary>
        /// Issue or refresh an order. If the unit already has an active order of the same type it is updated in place,
        /// so multi-phase orders (Bait) keep their state and the unit's accept/ignore roll isn't re-rolled every replan.
        /// </summary>
        void Issue(AIUnit u, OrderType t, Vector3 point, Vector3 aim, float duration, bool updatePoint = true, bool updateAim = true)
        {
            Order o = u.order;
            if (o != null && o.Active && o.type == t)
            {
                o.expires = Time.time + duration;
                if (updatePoint) o.point = point;
                if (updateAim) o.aimPoint = aim;
                return;
            }
            if (BF3Debug.On(DbgLevel.Verbose, DbgCat.Orders))
                BF3Debug.V(DbgCat.Orders, Tag + " -> " + u.Tag + " " + t + " point " + point.ToString("F0") + " aim " + aim.ToString("F0") + " for " + duration.ToString("F0") + "s", u, u.uid);
            u.SetOrder(Order.Make(t, point, aim, duration));
        }

        Vector3 HoldPointFor(AIUnit u, Vector3 side, bool crossfire)
        {
            Vector3 p = holdPoint;
            if (crossfire) p += side * ((members.IndexOf(u) % 2 == 0) ? -8f : 8f);   // converging lanes of fire
            return Tactics.Nav(p, 8f);
        }

        static bool IsAssaultOrOfficer(AIUnit u) { return u.unitClass == UnitClass.Assault || u.unitClass == UnitClass.Officer; }

        public void PlanLocal(SquadTask task, Vector3 enemy, bool crossfire, bool allowBait = false)
        {
            if (AliveCount == 0) return;
            if (task != lastTask || Time.time - lastPlan > 6f) StartTask(task);
            lastPlan = Time.time;

            Vector3 c = Center();
            Vector3 to = Tactics.Flat(enemy - c);
            float dist = to.magnitude;
            Vector3 dir = dist > 0.1f ? to / dist : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, dir);

            switch (task)
            {
                case SquadTask.Advance: PlanAdvance(c, enemy, dir, side, crossfire); break;
                case SquadTask.Bound: PlanBound(c, enemy, dir, side, crossfire); break;
                case SquadTask.Fix: PlanFix(enemy, side, crossfire); break;
                case SquadTask.FlankLeft: PlanFlank(-1f, enemy, dir, side, crossfire); break;
                case SquadTask.FlankRight: PlanFlank(1f, enemy, dir, side, crossfire); break;
                case SquadTask.Ambush: PlanAmbush(c, enemy, dist, dir, side, allowBait); break;
            }
        }

        void PlanAdvance(Vector3 c, Vector3 enemy, Vector3 dir, Vector3 side, bool crossfire)
        {
            Vector3 step = Tactics.SafeStep(c, enemy, 15f, team, 25f);
            if ((step - c).sqrMagnitude < 1f) { PlanFix(enemy, side, crossfire); return; }   // close enough: stop and fight
            for (int i = 0; i < members.Count; i++)
            {
                AIUnit u = members[i]; if (!Live(u)) continue;
                Issue(u, OrderType.Advance, Tactics.Nav(step + Slot(u, dir), 4f), enemy, 3f);
            }
        }

        // Bounding overwatch: one fire team moves, the other covers, then they swap.
        void PlanBound(Vector3 c, Vector3 enemy, Vector3 dir, Vector3 side, bool crossfire)
        {
            bool moverExists = false;
            for (int i = 0; i < members.Count; i++)
                if (Live(members[i]) && IsMover(members[i])) moverExists = true;

            if (!boundStarted)
            {
                boundStarted = true; phaseStart = Time.time;
                NewBoundPoint(c, enemy);
            }
            else if (!moverExists || Time.time - phaseStart > 5f)
            {
                if (!boundBlocked) holdPoint = boundPoint;   // the team that just moved now covers from where it stopped
                boundPhase = 1 - boundPhase; phaseStart = Time.time;
                if (BF3Debug.On(DbgLevel.Verbose, DbgCat.Squad))
                    BF3Debug.V(DbgCat.Squad, Tag + " bound swap: " + (boundPhase == 0 ? "assault/officer" : "heavy/sniper") + " move, others overwatch.");
                NewBoundPoint(c, enemy);
            }

            for (int i = 0; i < members.Count; i++)
            {
                AIUnit u = members[i]; if (!Live(u)) continue;
                if (IsMover(u))
                {
                    if (boundBlocked) Issue(u, OrderType.Suppress, HoldPointFor(u, side, crossfire), enemy, 3f, false, true);
                    else Issue(u, OrderType.Advance, Tactics.Nav(boundPoint + Slot(u, dir), 4f), enemy, 3f, false, true);
                }
                else Issue(u, OrderType.Overwatch, HoldPointFor(u, side, crossfire), enemy, 3f, false, true);
            }
        }

        bool IsMover(AIUnit u) { return (boundPhase == 0) == IsAssaultOrOfficer(u); }

        void NewBoundPoint(Vector3 c, Vector3 enemy)
        {
            boundPoint = Tactics.SafeStep(c, enemy, 12f, team, 10f);
            boundBlocked = (boundPoint - c).sqrMagnitude < 2.25f;   // already at the enemy's doorstep
        }

        // Fix: pin the enemy in place. Heavy/Sniper overwatch, Assault/Officer suppress. Crossfire splits them left/right.
        void PlanFix(Vector3 enemy, Vector3 side, bool crossfire)
        {
            for (int i = 0; i < members.Count; i++)
            {
                AIUnit u = members[i]; if (!Live(u)) continue;
                bool support = u.unitClass == UnitClass.Heavy || u.unitClass == UnitClass.Sniper;
                Issue(u, support ? OrderType.Overwatch : OrderType.Suppress, HoldPointFor(u, side, crossfire), enemy, 3f, false, true);
            }
        }

        // Flank: assaults swing around the enemy's side while everyone else keeps their head down.
        void PlanFlank(float s, Vector3 enemy, Vector3 dir, Vector3 side, bool crossfire)
        {
            bool hasAssault = false;
            for (int i = 0; i < members.Count; i++)
                if (Live(members[i]) && members[i].unitClass == UnitClass.Assault) hasAssault = true;
            if (!hasAssault) { PlanFix(enemy, side, crossfire); return; }

            Vector3 fp = enemy + side * s * 20f;
            int k = 0;
            for (int i = 0; i < members.Count; i++)
            {
                AIUnit u = members[i]; if (!Live(u)) continue;
                if (u.unitClass == UnitClass.Assault)
                {
                    Vector3 p = Tactics.Nav(fp + dir * (k * 2.5f), 8f); k++;
                    Issue(u, OrderType.Flank, p, enemy, 5f, false, true);
                }
                else
                {
                    bool support = u.unitClass == UnitClass.Heavy || u.unitClass == UnitClass.Sniper;
                    Issue(u, support ? OrderType.Overwatch : OrderType.Suppress, HoldPointFor(u, side, crossfire), enemy, 3f, false, true);
                }
            }
        }

        // Ambush: pull back to an anchor, pre-aim at a kill zone. One aggressive assault (if allowed) baits the enemy in.
        void PlanAmbush(Vector3 c, Vector3 enemy, float dist, Vector3 dir, Vector3 side, bool allowBait)
        {
            if (!ambushSet)
            {
                ambushSet = true;
                ambushAnchor = Tactics.Nav(c - dir * 8f, 10f);
                ambushKill = ambushAnchor + dir * 12f;
                ambushExpose = Tactics.Nav(c + dir * Mathf.Clamp(dist * 0.6f, 12f, 30f), 10f);

                if (allowBait)
                {
                    float best = -1f;
                    for (int i = 0; i < members.Count; i++)
                    {
                        AIUnit u = members[i];
                        if (!Live(u) || u.unitClass != UnitClass.Assault) continue;
                        if (u.personality.aggression > best) { best = u.personality.aggression; baitUnit = u; }
                    }
                }
            }

            for (int i = 0; i < members.Count; i++)
            {
                AIUnit u = members[i]; if (!Live(u)) continue;

                if (u == baitUnit && !baitDone)
                {
                    bool active = u.order != null && u.order.Active && u.order.type == OrderType.Bait;
                    if (baitIssued && !active) { baitDone = true; BF3Debug.I(DbgCat.Squad, Tag + " bait " + u.Tag + " finished (fell back / order ended); squad now holds the ambush."); }
                    else
                    {
                        Issue(u, OrderType.Bait, ambushAnchor, ambushExpose, 8f, false, false);   // point = fallback, aim = exposure point
                        if (!baitIssued) BF3Debug.I(DbgCat.Squad, Tag + " ambush: " + u.Tag + " (aggression " + u.personality.aggression.ToString("F2") + ") baits at " + ambushExpose.ToString("F0") + ", falls back to " + ambushAnchor.ToString("F0") + "; kill zone " + ambushKill.ToString("F0") + ".");
                        baitIssued = true;
                        continue;
                    }
                }
                Vector3 p = Tactics.Nav(ambushAnchor + side * ((members.IndexOf(u) - 1.5f) * 2.5f), 6f);
                Issue(u, OrderType.AmbushHold, p, ambushKill, 6f, false, false);
            }
        }

        // ================================================================= flank helpers
        /// <summary>Pick the flank side with the lower historical death danger and the more direct walking route.</summary>
        public SquadTask BestFlankSide(Vector3 enemy)
        {
            Vector3 c = Center();
            Vector3 dir = Tactics.Flat(enemy - c);
            if (dir.sqrMagnitude < 1f) return SquadTask.FlankRight;
            dir.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, dir);
            float costL = FlankCost(c, Tactics.Nav(enemy - side * 20f, 8f));
            float costR = FlankCost(c, Tactics.Nav(enemy + side * 20f, 8f));
            return costL < costR ? SquadTask.FlankLeft : SquadTask.FlankRight;
        }

        float FlankCost(Vector3 from, Vector3 p)
        {
            float cost = 0f;
            DeathHeatmap hm = DeathHeatmap.I;
            if (hm != null) cost += hm.PathDanger(from, p, team) + hm.GetDanger(p, team);
            float len = PathLength(from, p);
            float straight = Mathf.Max(1f, Vector3.Distance(from, p));
            cost += len < 0f ? 5f : 0.5f * (len / straight - 1f);
            return cost;
        }

        static NavMeshPath pathBuf;
        /// <summary>NavMesh walking distance, or -1 when unreachable.</summary>
        public static float PathLength(Vector3 a, Vector3 b)
        {
            if (pathBuf == null) pathBuf = new NavMeshPath();
            if (!NavMesh.CalculatePath(Tactics.Nav(a), Tactics.Nav(b), NavMesh.AllAreas, pathBuf) || pathBuf.status != NavMeshPathStatus.PathComplete)
                return -1f;
            Vector3[] cs = pathBuf.corners;
            float len = 0f;
            for (int i = 0; i < cs.Length - 1; i++) len += Vector3.Distance(cs[i], cs[i + 1]);
            return len;
        }
    }
    public enum PlatoonStrategy { Idle, Advance, Bounding, Flank, Pincer, Ambush }

    /// <summary>
    /// One to three squads in the same zone (up to 12 units). The alive Officer with the longest uptime is the commander;
    /// when he dies the next-longest takes over instantly. No officer alive = no plans, units act autonomously.
    /// Cohesion (0..1) is the platoon's morale: kills and objective progress raise it, casualties, splits, suppression lower it.
    ///   below 0.25  fractured: no plans, units panic
    ///   0.25..0.65  Bounding / Flank only
    ///   above 0.65  Pincer, Ambush, crossfire, grenade sharing unlocked
    /// </summary>
    public class Platoon
    {
        public const float HighCohesion = 0.65f, FracturedCohesion = 0.25f;

        public readonly List<Squad> squads = new List<Squad>(3);
        public int team;
        public float Cohesion = 0.6f;
        public AIUnit Commander;
        public PlatoonStrategy Strategy = PlatoonStrategy.Idle;

        public Vector3 objective; public bool hasObjective;
        public int objectiveIndex = -1; public bool objectiveDone; public float objectiveTimer;

        // episode tracking (what happened since the current strategy started)
        int epKills, epDeaths;
        float strategyUntil, nextPlan, lastObjDist = -1f;
        Vector3 lastCenter;

        // ---- debug / diagnostics
        static int idCounter;
        public readonly int id = ++idCounter;
        public string Tag { get { return "P" + id; } }
        public float dDrift, dSpread, dSupp, dSplit, dRoom, dProgress, dBleed;   // cohesion change per second, by cause
        public int modalRoom = -1; public float outsideRoomFrac;
        float cohAcc, nextCohLog; int lastTier = -1;
        string lastIdleWhy = "";
        readonly List<AIUnit> allLive = new List<AIUnit>();

        // scratch
        readonly List<Contact> shared = new List<Contact>();
        readonly List<Squad> sorted = new List<Squad>(3);
        bool hasEnemy; Vector3 enemyPos; float enemyDist;

        public Platoon(int team) { this.team = team; }

        // ================================================================= queries
        public int CurrentRoom()
        {
            allLive.Clear();
            for (int s = 0; s < squads.Count; s++)
                for (int i = 0; i < squads[s].members.Count; i++) if (Squad.Live(squads[s].members[i])) allLive.Add(squads[s].members[i]);
            int a, b; return Squad.ModalRoom(allLive, out a, out b);
        }

        public string Describe()
        {
            string tier = Cohesion < FracturedCohesion ? "FRACTURED" : Cohesion >= HighCohesion ? "HIGH" : "NORMAL";
            int bars = Mathf.Clamp(Mathf.RoundToInt(Cohesion * 10f), 0, 10);
            string cmd = Commander != null ? Commander.Tag + " up " + Commander.Uptime.ToString("F0") + "s" : "none";
            string room = CoverMap.I != null ? CoverMap.I.RoomName(CurrentRoom()) : "-";
            return Tag + " " + squads.Count + " squad(s), " + AliveUnits() + " alive  cohesion " + Cohesion.ToString("F2") + " [" + new string('#', bars) + new string('-', 10 - bars) + "] " + tier
                 + "  strategy=" + Strategy + "  cmd=" + cmd + "  room=" + room
                 + (hasObjective ? "  obj#" + objectiveIndex + " hold " + objectiveTimer.ToString("F0") + "s" : "  obj=none") + "  ep k/d " + epKills + "/" + epDeaths;
        }

        public int AliveUnits()
        {
            int n = 0;
            for (int i = 0; i < squads.Count; i++) n += squads[i].AliveCount;
            return n;
        }

        public Vector3 Center()
        {
            Vector3 sum = Vector3.zero; int n = 0;
            for (int s = 0; s < squads.Count; s++)
                for (int i = 0; i < squads[s].members.Count; i++)
                {
                    AIUnit u = squads[s].members[i];
                    if (Squad.Live(u)) { sum += u.transform.position; n++; }
                }
            if (n > 0) lastCenter = sum / n;
            return lastCenter;
        }

        /// <summary>Merge every squad's pooled sightings into buf (newest wins). Used by units to see what the platoon knows.</summary>
        public void CollectShared(List<Contact> buf)
        {
            for (int s = 0; s < squads.Count; s++)
            {
                foreach (Contact c in squads[s].PooledContacts)
                {
                    if (c.who == null || !c.who.IsAlive || c.Age > 8f) continue;
                    int existing = -1;
                    for (int i = 0; i < buf.Count; i++) if (buf[i].who == c.who) { existing = i; break; }
                    if (existing < 0) buf.Add(c);
                    else if (buf[existing].lastSeen < c.lastSeen) buf[existing] = c;
                }
            }
        }

        // ================================================================= cohesion
        public void Add(float delta) { Cohesion = Mathf.Clamp01(Cohesion + delta); }
        public void RegisterKill()
        {
            Add(0.06f); epKills++;
            BF3Debug.V(DbgCat.Cohesion, Tag + " kill +0.06 -> cohesion " + Cohesion.ToString("F2"));
        }
        public void RegisterCasualty()
        {
            Add(-0.10f); epDeaths++;
            BF3Debug.I(DbgCat.Cohesion, Tag + " casualty -0.10 -> cohesion " + Cohesion.ToString("F2") + ", " + AliveUnits() + " alive.");
            RecomputeCommander();
        }

        void UpdateCohesion(float dt)
        {
            cohAcc += dt;
            if (cohAcc < 0.25f) return;      // evaluated 4x/second; dt is the accumulated time
            dt = cohAcc; cohAcc = 0f;

            int total = 0, splitSquads = 0;
            float spread = 0f, supp = 0f;
            bool useRooms = CoverMap.RoomsEnabled;

            allLive.Clear();
            for (int s = 0; s < squads.Count; s++)
                for (int i = 0; i < squads[s].members.Count; i++)
                {
                    AIUnit u = squads[s].members[i]; total++;
                    if (Squad.Live(u)) allLive.Add(u);
                }
            int alive = allLive.Count;
            if (alive == 0) return;

            Vector3 pc = Center();
            int inModal, liveCount;
            modalRoom = Squad.ModalRoom(allLive, out inModal, out liveCount);
            outsideRoomFrac = (useRooms && alive > 1) ? 1f - (float)inModal / alive : 0f;   // fraction standing in a different room than the majority

            for (int s = 0; s < squads.Count; s++)
            {
                Squad sq = squads[s];
                if (sq.AliveCount == 0) continue;
                Vector3 sc = sq.Center();
                bool far = Tactics.Flat(sc - pc).magnitude > 40f;
                bool otherRoom = false;
                if (useRooms && squads.Count > 1) { int ins, lv; int sr = Squad.ModalRoom(sq.members, out ins, out lv); otherRoom = lv > 0 && sr != modalRoom; }
                if (far || otherRoom) splitSquads++;
                for (int i = 0; i < sq.members.Count; i++)
                {
                    AIUnit u = sq.members[i];
                    if (!Squad.Live(u)) continue;
                    spread += Mathf.Clamp01((Tactics.Flat(u.transform.position - sc).magnitude - 15f) / 25f);
                    supp += u.suppression;
                }
            }

            dDrift = (0.5f - Cohesion) * 0.02f;                 // passive drift toward neutral
            dSpread = -0.04f * spread / alive;                  // scattered within squads
            dSupp = -0.05f * supp / alive;                      // pinned down
            dSplit = -0.03f * splitSquads;                      // squads in another room or far away
            dRoom = -0.05f * outsideRoomFrac;                   // individual units split across rooms
            dBleed = (alive / (float)total < 0.5f) ? -0.02f : 0f;
            dProgress = 0f;

            if (hasObjective)
            {
                float od = Tactics.Flat(pc - objective).magnitude;
                if (lastObjDist >= 0f)
                {
                    float closing = (lastObjDist - od) / dt;
                    if (closing > 0.5f) dProgress = 0.02f * Mathf.Clamp01(closing / 3f);   // making progress toward the objective
                }
                lastObjDist = od;
            }
            Add((dDrift + dSpread + dSupp + dSplit + dRoom + dBleed + dProgress) * dt);

            int tier = Cohesion < FracturedCohesion ? 0 : Cohesion >= HighCohesion ? 2 : 1;
            if (tier != lastTier)
            {
                string[] names = { "FRACTURED", "NORMAL", "HIGH" };
                if (lastTier >= 0)
                    BF3Debug.I(DbgCat.Cohesion, Tag + " cohesion tier " + names[lastTier] + " -> " + names[tier] + " (" + Cohesion.ToString("F2") + "). Per second: drift " + dDrift.ToString("+0.000;-0.000") + " spread " + dSpread.ToString("+0.000;-0.000")
                        + " supp " + dSupp.ToString("+0.000;-0.000") + " split " + dSplit.ToString("+0.000;-0.000") + " rooms " + dRoom.ToString("+0.000;-0.000") + " progress " + dProgress.ToString("+0.000;-0.000"));
                lastTier = tier;
            }
            if (Time.time >= nextCohLog && BF3Debug.On(DbgLevel.Verbose, DbgCat.Cohesion))
            {
                nextCohLog = Time.time + 3f;
                BF3Debug.V(DbgCat.Cohesion, Tag + " cohesion " + Cohesion.ToString("F2") + "  /s: drift " + dDrift.ToString("+0.000;-0.000") + " spread " + dSpread.ToString("+0.000;-0.000") + " supp " + dSupp.ToString("+0.000;-0.000")
                    + " split " + dSplit.ToString("+0.000;-0.000") + " rooms " + dRoom.ToString("+0.000;-0.000") + " bleed " + dBleed.ToString("+0.000;-0.000") + " progress " + dProgress.ToString("+0.000;-0.000")
                    + "  | modal room " + (CoverMap.I != null ? CoverMap.I.RoomName(modalRoom) : "-") + ", " + (outsideRoomFrac * 100f).ToString("F0") + "% of units elsewhere, " + splitSquads + " squad(s) split");
            }
        }

        // ================================================================= commander
        public void RecomputeCommander()
        {
            AIUnit best = null; float bu = -1f;
            for (int s = 0; s < squads.Count; s++)
                for (int i = 0; i < squads[s].members.Count; i++)
                {
                    AIUnit u = squads[s].members[i];
                    if (!Squad.Live(u) || u.unitClass != UnitClass.Officer) continue;
                    if (u.Uptime > bu) { bu = u.Uptime; best = u; }
                }
            if (best != Commander)
            {
                BF3Debug.I(DbgCat.Platoon, Tag + " commander " + (Commander != null ? Commander.Tag : "none") + " -> "
                    + (best != null ? best.Tag + " (uptime " + best.Uptime.ToString("F0") + "s)" : "NONE (no living officer; units act autonomously)"), best);
                Commander = best;
            }
        }

        // ================================================================= merge / split
        public void Absorb(Platoon o)
        {
            int a = Mathf.Max(1, AliveUnits()), b = Mathf.Max(1, o.AliveUnits());
            Cohesion = (Cohesion * a + o.Cohesion * b) / (a + b);
            for (int i = 0; i < o.squads.Count; i++) { o.squads[i].platoon = this; squads.Add(o.squads[i]); }
            o.squads.Clear();
            if (!hasObjective && o.hasObjective) { objective = o.objective; hasObjective = true; objectiveIndex = o.objectiveIndex; }
            epKills += o.epKills; epDeaths += o.epDeaths;
            strategyUntil = 0f; nextPlan = 0f;
            RecomputeCommander();
        }

        public Platoon Detach(Squad s)
        {
            squads.Remove(s);
            Platoon p = new Platoon(team);
            p.Cohesion = Mathf.Clamp01(Cohesion * 0.85f);
            p.objective = objective; p.hasObjective = hasObjective; p.objectiveIndex = objectiveIndex;
            p.squads.Add(s); s.platoon = p;
            p.RecomputeCommander();
            Add(-0.08f);                        // splitting hurts morale
            strategyUntil = 0f; nextPlan = 0f;
            RecomputeCommander();
            return p;
        }

        // ================================================================= update
        public void Tick(float dt)
        {
            for (int i = 0; i < squads.Count; i++) squads[i].Tick(dt);
            UpdateCohesion(dt);
            if (Time.time >= nextPlan) { nextPlan = Time.time + 1.5f; Plan(); }
        }

        void Plan()
        {
            RecomputeCommander();
            if (Commander == null) { GoIdle("no officer alive: units act autonomously"); return; }
            if (Cohesion < FracturedCohesion) { GoIdle("cohesion " + Cohesion.ToString("F2") + " < " + FracturedCohesion.ToString("F2") + " (fractured)"); return; }

            BuildEnemy();
            if (!hasEnemy) { GoIdle("no enemy known"); return; }

            if (enemyDist > 60f)
            {
                if (Strategy != PlatoonStrategy.Advance) { CloseEpisode(); SetStrategy(PlatoonStrategy.Advance, "nearest enemy " + enemyDist.ToString("F0") + " m away"); }
            }
            else PickStrategy();

            Apply();
            if (Cohesion >= HighCohesion) DistributeGrenades();
        }

        void GoIdle(string why)
        {
            CloseEpisode();
            if (Strategy != PlatoonStrategy.Idle || why != lastIdleWhy) BF3Debug.I(DbgCat.Platoon, Tag + " idle: " + why, Commander);
            Strategy = PlatoonStrategy.Idle; lastIdleWhy = why;
        }

        void SetStrategy(PlatoonStrategy s, string detail)
        {
            if (s != Strategy) BF3Debug.I(DbgCat.Platoon, Tag + " strategy " + Strategy + " -> " + s + " (" + detail + ")", Commander);
            Strategy = s; lastIdleWhy = "";
        }

        void BuildEnemy()
        {
            shared.Clear();
            CollectShared(shared);
            Vector3 pc = Center();
            Contact nearest = null; float bd = float.MaxValue;
            for (int i = 0; i < shared.Count; i++)
            {
                float d = Tactics.Flat(shared[i].PredictedPos - pc).magnitude;
                if (d < bd) { bd = d; nearest = shared[i]; }
            }
            hasEnemy = nearest != null;
            if (!hasEnemy) return;

            Vector3 np = nearest.PredictedPos, sum = Vector3.zero; int n = 0;
            for (int i = 0; i < shared.Count; i++)
                if (Tactics.Flat(shared[i].PredictedPos - np).magnitude < 12f) { sum += shared[i].PredictedPos; n++; }
            enemyPos = sum / n;
            enemyDist = bd;
        }

        void PickStrategy()
        {
            bool tactical = Strategy >= PlatoonStrategy.Bounding;
            if (tactical && Time.time < strategyUntil) return;   // commit for a while

            CloseEpisode();

            TeamLearning L = LearningMemory.Get(team);
            Personality cp = Commander.personality;
            float danger = DeathHeatmap.I != null ? DeathHeatmap.I.GetDanger(Center(), team) : 0f;
            bool high = Cohesion >= HighCohesion;

            float wBound = 1.0f * L.StrategyWeight("Bounding") * (high ? 1f : 1.3f);
            float wFlank = 0.8f * L.StrategyWeight("Flank") * (0.7f + 0.6f * cp.aggression) * (enemyDist > 20f ? 1f : 0.5f);
            float wPincer = (high && squads.Count >= 2) ? 0.9f * L.StrategyWeight("Pincer") * (0.7f + 0.6f * cp.aggression) : 0f;
            float wAmbush = high ? 0.5f * L.StrategyWeight("Ambush") * (0.7f + 0.6f * cp.caution) * (1f + 2f * danger) : 0f;   // hot ground -> retreat to ambush

            float total = wBound + wFlank + wPincer + wAmbush;
            float r = Random.value * total;
            PlatoonStrategy pick;
            if ((r -= wBound) < 0f) pick = PlatoonStrategy.Bounding;
            else if ((r -= wFlank) < 0f) pick = PlatoonStrategy.Flank;
            else if ((r -= wPincer) < 0f) pick = PlatoonStrategy.Pincer;
            else pick = wAmbush > 0f ? PlatoonStrategy.Ambush : PlatoonStrategy.Bounding;
            SetStrategy(pick, "weights bound " + wBound.ToString("F2") + " flank " + wFlank.ToString("F2") + " pincer " + wPincer.ToString("F2") + " ambush " + wAmbush.ToString("F2")
                + "; enemy " + enemyDist.ToString("F0") + " m, cohesion " + Cohesion.ToString("F2") + ", local danger " + danger.ToString("F2") + ", " + squads.Count + " squad(s)");

            strategyUntil = Time.time + Random.Range(12f, 20f);
        }

        void CloseEpisode()
        {
            if (Strategy >= PlatoonStrategy.Bounding && epKills + epDeaths > 0)
            {
                bool ok = epKills - 0.7f * epDeaths > 0f;
                TeamLearning L = LearningMemory.Get(team);
                L.RecordStrategy(Strategy.ToString(), ok);
                BF3Debug.I(DbgCat.Learning, Tag + " episode " + Strategy + " ended: kills " + epKills + ", deaths " + epDeaths + " -> " + (ok ? "SUCCESS" : "FAILURE") + "; learned weight now " + L.StrategyWeight(Strategy.ToString()).ToString("F2"), Commander);
            }
            epKills = 0; epDeaths = 0;
        }

        void Apply()
        {
            bool cross = Cohesion >= HighCohesion;

            sorted.Clear();
            for (int i = 0; i < squads.Count; i++)
                if (squads[i].AliveCount > 0) sorted.Add(squads[i]);
            for (int i = 1; i < sorted.Count; i++)      // insertion sort: nearest to the enemy first
            {
                Squad s = sorted[i]; float d = Tactics.Flat(s.Center() - enemyPos).sqrMagnitude; int j = i - 1;
                while (j >= 0 && Tactics.Flat(sorted[j].Center() - enemyPos).sqrMagnitude > d) { sorted[j + 1] = sorted[j]; j--; }
                sorted[j + 1] = s;
            }
            if (sorted.Count == 0) return;

            switch (Strategy)
            {
                case PlatoonStrategy.Advance:
                    for (int i = 0; i < sorted.Count; i++) sorted[i].PlanLocal(SquadTask.Advance, enemyPos, false);
                    break;

                case PlatoonStrategy.Bounding:
                    for (int i = 0; i < sorted.Count; i++) sorted[i].PlanLocal(SquadTask.Bound, enemyPos, cross);
                    break;

                case PlatoonStrategy.Flank:
                    if (sorted.Count == 1) sorted[0].PlanLocal(sorted[0].BestFlankSide(enemyPos), enemyPos, cross);
                    else
                    {
                        sorted[0].PlanLocal(SquadTask.Fix, enemyPos, cross);                         // nearest squad pins
                        for (int i = 1; i < sorted.Count; i++) sorted[i].PlanLocal(sorted[i].BestFlankSide(enemyPos), enemyPos, cross);
                    }
                    break;

                case PlatoonStrategy.Pincer:
                    if (sorted.Count < 2) { goto case PlatoonStrategy.Flank; }
                    sorted[0].PlanLocal(SquadTask.FlankLeft, enemyPos, cross);
                    sorted[1].PlanLocal(SquadTask.FlankRight, enemyPos, cross);
                    if (sorted.Count > 2) sorted[2].PlanLocal(SquadTask.Fix, enemyPos, cross);
                    break;

                case PlatoonStrategy.Ambush:
                    for (int i = 0; i < sorted.Count; i++) sorted[i].PlanLocal(SquadTask.Ambush, enemyPos, cross, i == 0);   // nearest squad supplies the bait
                    break;
            }
        }

        // ================================================================= grenade sharing (high cohesion only)
        void DistributeGrenades()
        {
            for (int s = 0; s < squads.Count; s++)
                for (int i = 0; i < squads[s].members.Count; i++)
                {
                    AIUnit u = squads[s].members[i];
                    if (!Squad.Live(u) || u.grenades > 0) continue;

                    AIUnit donor = null;
                    for (int t = 0; t < squads.Count && donor == null; t++)
                        for (int k = 0; k < squads[t].members.Count; k++)
                        {
                            AIUnit d = squads[t].members[k];
                            if (d == u || !Squad.Live(d) || d.grenades < 2) continue;
                            if ((d.transform.position - u.transform.position).sqrMagnitude > 225f) continue;
                            donor = d; break;
                        }
                    if (donor != null)
                    {
                        donor.grenades--; u.grenades++;
                        BF3Debug.I(DbgCat.Grenade, Tag + " grenade shared: " + donor.Tag + " -> " + u.Tag, u, u.uid);
                    }
                }
        }
    }
}
