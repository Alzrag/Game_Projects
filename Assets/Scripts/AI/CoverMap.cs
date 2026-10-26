using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace BF3AI
{
    /// <summary>
    /// One baked cover node.
    /// position = where the unit stands. normal = face normal pointing OUT of the barrier toward the unit
    /// (i.e. the protected side). Enemies located "behind" the barrier (opposite the normal) are blocked.
    /// </summary>
    [Serializable]
    public class CoverPoint
    {
        public Vector3 position;
        public Vector3 normal;
        public float height;      // barrier height above the ground at the stand position (contiguous from the floor up)
        public int room = -1;     // index into CoverMap.rooms (-1 = not inside any room volume)

        public const float LowMax = 1.35f;   // below this the unit must crouch
        [NonSerialized] public AIUnit occupant;
        [NonSerialized] float deaths;
        [NonSerialized] float stamp;

        public bool IsLow { get { return height < LowMax; } }
        public Vector3 Tangent { get { return Vector3.Cross(Vector3.up, normal); } }

        /// <summary>Angle verification: compare the barrier normal with the incoming attack direction.</summary>
        public bool Blocks(Vector3 enemyPos, float minCos = 0.42f)
        {
            Vector3 d = enemyPos - position;
            float dy = d.y; d.y = 0f;
            if (d.sqrMagnitude < 0.01f) return false;
            if (dy > 2.5f && height < 2f) return false;      // shooter high above can fire over the barrier
            d.Normalize();
            return Vector3.Dot(-normal, d) >= minCos;        // enemy is on the far side of the barrier
        }

        public float BlockedFraction(List<Vector3> threats)
        {
            if (threats.Count == 0) return 1f;
            int n = 0;
            for (int i = 0; i < threats.Count; i++) if (Blocks(threats[i])) n++;
            return (float)n / threats.Count;
        }

        /// <summary>Find a spot (over low cover, or leaning left/right around full cover) with a line to the target.</summary>
        public bool TryPeek(Vector3 targetPos, float eyeHeight, out Vector3 peekPos)
        {
            Vector3 t = Tangent * 0.9f;
            for (int i = 0; i < 3; i++)
            {
                if (i == 0 && !IsLow) continue;
                Vector3 off = i == 0 ? Vector3.zero : (i == 1 ? t : -t);
                Vector3 p = position + off;
                if (!Physics.Linecast(p + Vector3.up * eyeHeight, targetPos, AIWorld.Obstacles, QueryTriggerInteraction.Ignore))
                {
                    peekPos = i == 0 ? position : Tactics.Nav(p, 1f);
                    return true;
                }
            }
            peekPos = position;
            return false;
        }

        public float DeathScore { get { return deaths * Mathf.Pow(0.5f, (Time.time - stamp) / 300f); } }
        public void RegisterDeath() { deaths = DeathScore + 1f; stamp = Time.time; }
    }

    /// <summary>One room/region. Every room is a single big trigger collider (box/sphere/capsule/convex mesh).</summary>
    [Serializable]
    public class RoomInfo
    {
        public Collider collider;
        public string name;
        public float volume;      // used so the SMALLEST containing room wins when rooms nest
    }

    /// <summary>Holds baked cover data + room volumes and answers \"where should I hide?\" / \"which room is this?\" queries.</summary>
    public class CoverMap : MonoBehaviour
    {
        public static CoverMap I;

        [Header("Bake settings (works on ONE merged scene model, many objects, terrain or organic shapes)")]
        [Tooltip("Layer(s) holding ALL solid level geometry: floor, walls, ceilings, stairs, slopes and cover. Never the Player, Bots or Room layers.")]
        public LayerMask coverMask;
        [Tooltip("Distance between probe points laid over the NavMesh. Smaller = denser cover and a slower bake.")]
        public float sampleSpacing = 1f;
        [Tooltip("Two cover points closer than this that face the same way are merged into one.")]
        public float minPointSpacing = 1.25f;
        [Tooltip("Rays fired outward from every probe point looking for a surface.")]
        public int probeDirections = 16;
        [Tooltip("How far from a probe point a surface can be and still count (metres).")]
        public float wallProbe = 1.2f;
        [Tooltip("Distance between the unit and the barrier it hides behind.")]
        public float standOffset = 0.6f;
        [Tooltip("Surfaces shorter than this are not cover (stair risers, curbs, bumps).")]
        public float minCoverHeight = 0.6f;
        public float maxCoverHeight = 3f;
        [Tooltip("Safety cap on the number of cover points (a warning is logged if it is hit).")]
        public int maxPoints = 60000;
        [Tooltip("Safety cap on probe points over the NavMesh.")]
        public int maxSamples = 120000;

        [Header("Runtime")]
        [Tooltip("When choosing cover, verify with a line cast that the best candidate really hides the unit from the nearest threat (catches angle-correct but geometrically useless spots on organic/merged models).")]
        public bool verifyOcclusion = true;
        [Tooltip("Ignore cover points more than this far above/below the unit (other floors).")]
        public float maxVerticalReach = 3f;

        [Header("Rooms / regions (every room is ONE big trigger collider on this layer)")]
        [Tooltip("Layer holding the room colliders. Keep it OUT of obstacleMask/coverMask. Colliders MUST be triggers.")]
        public LayerMask roomMask;

        [Header("Baked data")]
        public List<CoverPoint> points = new List<CoverPoint>();
        public List<RoomInfo> rooms = new List<RoomInfo>();
        public bool drawGizmos = true;
        [Tooltip("Only draw cover points within this distance of the Scene-view camera (0 = draw all). Keeps big levels responsive.")]
        public float gizmoRadius = 45f;

        void Awake()
        {
            I = this;
            foreach (CoverPoint p in points) p.occupant = null;
            BuildGrid();
            int missing = 0;
            for (int i = 0; i < rooms.Count; i++) if (rooms[i] == null || rooms[i].collider == null) missing++;
            if (missing > 0) BF3Debug.E(DbgCat.Setup, "CoverMap: " + missing + " baked room collider(s) no longer exist. Re-bake (right-click CoverMap > Bake).", this);
            BF3Debug.I(DbgCat.Setup, "CoverMap ready: " + points.Count + " cover points, " + rooms.Count + " rooms.", this);
        }

        // ---------------------------------------------------------------- rooms
        public static bool RoomsEnabled { get { return I != null && I.rooms.Count > 0; } }

        /// <summary>Room index containing p (smallest wins), or -1.</summary>
        public static int RoomOf(Vector3 p) { return (I != null && I.rooms.Count > 0) ? I.RoomAt(p) : -1; }

        public int RoomAt(Vector3 p)
        {
            int best = -1; float bv = float.MaxValue;
            Vector3 q = p + Vector3.up * 0.6f;   // sample at torso height so floor-level positions still count as "inside"
            for (int i = 0; i < rooms.Count; i++)
            {
                RoomInfo r = rooms[i];
                if (r == null || r.collider == null) continue;
                if (!r.collider.bounds.Contains(q)) continue;
                MeshCollider mc = r.collider as MeshCollider;
                if (mc == null || mc.convex)
                    if ((r.collider.ClosestPoint(q) - q).sqrMagnitude > 0.0004f) continue;   // inside the true shape, not just its AABB
                if (r.volume < bv) { bv = r.volume; best = i; }
            }
            return best;
        }

        public string RoomName(int id) { return (id >= 0 && id < rooms.Count && rooms[id] != null) ? rooms[id].name : "(open)"; }

        // ---------------------------------------------------------------- baking
        // The bake never looks at individual objects. It lays probe points over the NavMesh (everywhere a unit can
        // stand), fires rays outward from each one at the coverMask layer(s), and turns every nearby, vertical,
        // tall-enough surface into a cover point. So ONE merged model (floor + walls + ceilings + stairs + slopes
        // + props), many separate objects, terrain or organic shapes all bake the same way.
        // Floors, slopes and ceilings are ignored (their normals point up/down); stair risers and curbs are ignored
        // (too short); walls and props at least minCoverHeight tall become cover. Height is measured by a ladder of
        // rays up the barrier, so a window or arch only counts up to its sill/opening.
        class BakeStats
        {
            public int samples, wallHits, floorOrSlope, noStandSpot, cramped, tooShort, duplicate, low, full;
            public bool capped, cancelled;
        }
        Dictionary<Vector3Int, List<int>> bakeGrid;

        [ContextMenu("Bake Cover Points")]
        public void Bake()
        {
            points.Clear(); rooms.Clear(); grid = null;
#if UNITY_2023_1_OR_NEWER
            Collider[] cols = FindObjectsByType<Collider>(FindObjectsSortMode.None);
#else
            Collider[] cols = FindObjectsOfType<Collider>();
#endif
            BakePoints();
            BakeRooms(cols);
            for (int i = 0; i < points.Count; i++) points[i].room = RoomAt(points[i].position);

            BF3Debug.I(DbgCat.Setup, "CoverMap: baked " + points.Count + " cover points and " + rooms.Count + " rooms.", this);
            if (rooms.Count > 0)
            {
                int[] per = new int[rooms.Count]; int none = 0;
                for (int i = 0; i < points.Count; i++) { if (points[i].room >= 0) per[points[i].room]++; else none++; }
                for (int i = 0; i < rooms.Count; i++)
                {
                    string line = "  room[" + i + "] '" + rooms[i].name + "' size " + rooms[i].collider.bounds.size.ToString("F0") + ", cover points: " + per[i];
                    if (per[i] == 0) BF3Debug.W(DbgCat.Setup, line + "  <- no cover inside this room (bots will fight in the open here)", this);
                    else BF3Debug.I(DbgCat.Setup, line, this);
                }
                if (none > 0) BF3Debug.I(DbgCat.Setup, "  " + none + " cover points lie outside every room (treated as the open/unmapped region).", this);
            }
            else if (roomMask.value == 0) BF3Debug.W(DbgCat.Setup, "CoverMap: roomMask is empty, so no rooms were baked. 'Split across rooms' cohesion and same-room merging are disabled (distance is used instead).", this);
            else BF3Debug.W(DbgCat.Setup, "CoverMap: roomMask is set but no colliders were found on that layer.", this);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        void BakePoints()
        {
            if (coverMask.value == 0)
            {
                BF3Debug.E(DbgCat.Setup, "CoverMap: coverMask is empty. Set it to the layer your level model is on.", this);
                return;
            }
            if ((coverMask.value & roomMask.value) != 0)
                BF3Debug.W(DbgCat.Setup, "CoverMap: coverMask and roomMask share a layer. Room volumes must live on their own layer (they are triggers and are ignored by the bake).", this);

            NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
            if (tri.vertices == null || tri.vertices.Length == 0 || tri.indices == null || tri.indices.Length == 0)
            {
                BF3Debug.E(DbgCat.Setup, "CoverMap: there is no NavMesh. Bake the NavMesh first; cover is found by probing outward from where units can stand.", this);
                return;
            }

            BakeStats st = new BakeStats();
            float t0 = Time.realtimeSinceStartup;
            List<Vector3> samples = CollectSamples(tri, st);
            bakeGrid = new Dictionary<Vector3Int, List<int>>();
            int dirs = Mathf.Max(4, probeDirections);

            try
            {
                for (int i = 0; i < samples.Count; i++)
                {
#if UNITY_EDITOR
                    if ((i & 127) == 0 && UnityEditor.EditorUtility.DisplayCancelableProgressBar("BF3AI: baking cover",
                            i + " / " + samples.Count + " probe points, " + points.Count + " cover points", (float)i / samples.Count))
                    { st.cancelled = true; break; }
#endif
                    Vector3 p = samples[i];
                    for (int k = 0; k < dirs; k++)
                    {
                        Vector3 d = Quaternion.AngleAxis(k * 360f / dirs, Vector3.up) * Vector3.forward;
                        RaycastHit hit;
                        if (!Physics.Raycast(p + Vector3.up * 0.4f, d, out hit, wallProbe, coverMask.value, QueryTriggerInteraction.Ignore)) continue;
                        st.wallHits++;
                        TryAddPoint(hit, st);
                        if (points.Count >= maxPoints) { st.capped = true; break; }
                    }
                    if (st.capped) break;
                }
            }
            finally
            {
#if UNITY_EDITOR
                UnityEditor.EditorUtility.ClearProgressBar();
#endif
                bakeGrid = null;
            }

            for (int i = 0; i < points.Count; i++) { if (points[i].IsLow) st.low++; else st.full++; }
            BF3Debug.I(DbgCat.Setup, "CoverMap bake: " + st.samples + " probe points, " + st.wallHits + " surface hits -> rejected: floor/slope/ceiling " + st.floorOrSlope
                + ", no standing spot on NavMesh " + st.noStandSpot + ", cramped " + st.cramped + ", shorter than " + minCoverHeight.ToString("F1") + " m " + st.tooShort
                + ", duplicate " + st.duplicate + "  =>  " + points.Count + " cover points (" + st.low + " low/crouch, " + st.full + " full/stand) in "
                + (Time.realtimeSinceStartup - t0).ToString("F1") + " s.", this);

            if (st.cancelled) BF3Debug.W(DbgCat.Setup, "CoverMap bake was cancelled; the cover data is incomplete. Bake again.", this);
            if (st.capped) BF3Debug.W(DbgCat.Setup, "CoverMap bake stopped at maxPoints (" + maxPoints + "). Raise maxPoints, or raise sampleSpacing / minPointSpacing to thin the data.", this);
            if (points.Count == 0)
            {
                if (st.wallHits == 0)
                    BF3Debug.E(DbgCat.Setup, "CoverMap: 0 cover points and NO ray hit anything on coverMask. Check: (1) your level model is on the layer(s) in coverMask, (2) it has a Collider (a non-convex Mesh Collider is fine), (3) the collider is not a trigger, (4) the NavMesh really covers the walkable floor.", this);
                else if (st.noStandSpot > st.wallHits / 2)
                    BF3Debug.E(DbgCat.Setup, "CoverMap: 0 cover points: surfaces were found but there is no NavMesh in front of them. The NavMesh agent radius is probably too large (use 0.4 or less), so the NavMesh stops short of the walls.", this);
                else
                    BF3Debug.E(DbgCat.Setup, "CoverMap: 0 cover points. Surfaces were found but all were floors/slopes, too short (< " + minCoverHeight + " m) or cramped. Check minCoverHeight and the model scale (1 unit = 1 metre).", this);
            }
            else if (st.noStandSpot > st.wallHits * 3 / 4 && st.wallHits > 40)
                BF3Debug.W(DbgCat.Setup, "CoverMap: most surface hits had no NavMesh in front of them; the NavMesh agent radius may be too large, which hides cover.", this);
        }

        List<Vector3> CollectSamples(NavMeshTriangulation tri, BakeStats st)
        {
            List<Vector3> list = new List<Vector3>();
            HashSet<Vector3Int> seen = new HashSet<Vector3Int>();
            float step = Mathf.Max(0.25f, sampleSpacing);

            for (int t = 0; t + 2 < tri.indices.Length; t += 3)
            {
                Vector3 a = tri.vertices[tri.indices[t]], b = tri.vertices[tri.indices[t + 1]], c = tri.vertices[tri.indices[t + 2]];
                AddSample((a + b + c) / 3f, seen, list, step);       // small triangles still get one probe

                float den = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                if (Mathf.Abs(den) < 0.00001f) continue;             // degenerate (vertical or zero-area)

                float minX = Mathf.Min(Mathf.Min(a.x, b.x), c.x), maxX = Mathf.Max(Mathf.Max(a.x, b.x), c.x);
                float minZ = Mathf.Min(Mathf.Min(a.z, b.z), c.z), maxZ = Mathf.Max(Mathf.Max(a.z, b.z), c.z);
                int ix0 = Mathf.CeilToInt(minX / step), ix1 = Mathf.FloorToInt(maxX / step);
                int iz0 = Mathf.CeilToInt(minZ / step), iz1 = Mathf.FloorToInt(maxZ / step);
                for (int ix = ix0; ix <= ix1; ix++)
                    for (int iz = iz0; iz <= iz1; iz++)
                    {
                        float x = ix * step, z = iz * step;
                        float w1 = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / den;
                        float w2 = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / den;
                        float w3 = 1f - w1 - w2;
                        if (w1 < 0f || w2 < 0f || w3 < 0f) continue;
                        AddSample(new Vector3(x, w1 * a.y + w2 * b.y + w3 * c.y, z), seen, list, step);
                        if (list.Count >= maxSamples) break;
                    }
                if (list.Count >= maxSamples)
                {
                    BF3Debug.W(DbgCat.Setup, "CoverMap: reached maxSamples (" + maxSamples + ") probe points; part of the NavMesh was not probed. Raise sampleSpacing or maxSamples.", this);
                    break;
                }
            }
            st.samples = list.Count;
            return list;
        }

        static void AddSample(Vector3 p, HashSet<Vector3Int> seen, List<Vector3> list, float step)
        {
            // one probe per (cell, 1 m of height) so stacked floors and stairs each get probed
            Vector3Int key = new Vector3Int(Mathf.FloorToInt(p.x / step), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z / step));
            if (seen.Add(key)) list.Add(p);
        }

        void TryAddPoint(RaycastHit hit, BakeStats st)
        {
            Vector3 n = hit.normal;
            if (Mathf.Abs(n.y) > 0.5f) { st.floorOrSlope++; return; }       // floor, slope, ceiling: not a barrier
            n.y = 0f; n.Normalize();

            // where a unit would stand: in front of the surface, on the NavMesh
            NavMeshHit nh;
            if (!NavMesh.SamplePosition(hit.point + n * standOffset, out nh, 0.5f, NavMesh.AllAreas)) { st.noStandSpot++; return; }
            Vector3 g = nh.position;
            float front = Vector3.Dot(g - hit.point, n);
            if (front < 0.3f || Mathf.Abs(g.y - hit.point.y) > 1.0f) { st.noStandSpot++; return; }   // snapped behind the wall or onto another level

            // room to stand (stairs undersides, low ceilings)
            if (Physics.CheckCapsule(g + Vector3.up * 0.55f, g + Vector3.up * 1.45f, 0.3f, coverMask.value, QueryTriggerInteraction.Ignore)) { st.cramped++; return; }

            float height; Vector3 avgN;
            if (!MeasureBarrier(g, n, front + 0.6f, out height, out avgN) || height < minCoverHeight) { st.tooShort++; return; }
            n = avgN;

            if (IsDuplicate(g, n)) { st.duplicate++; return; }
            points.Add(new CoverPoint { position = g, normal = n, height = height });
            int idx = points.Count - 1;
            Vector3Int key = BakeKey(g);
            List<int> l;
            if (!bakeGrid.TryGetValue(key, out l)) { l = new List<int>(2); bakeGrid[key] = l; }
            l.Add(idx);
        }

        Vector3Int BakeKey(Vector3 p)
        {
            float cell = Mathf.Max(0.5f, minPointSpacing);
            return new Vector3Int(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / 2f), Mathf.FloorToInt(p.z / cell));
        }

        bool IsDuplicate(Vector3 pos, Vector3 n)
        {
            Vector3Int k0 = BakeKey(pos);
            float r2 = minPointSpacing * minPointSpacing;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        List<int> l;
                        if (!bakeGrid.TryGetValue(new Vector3Int(k0.x + dx, k0.y + dy, k0.z + dz), out l)) continue;
                        for (int i = 0; i < l.Count; i++)
                        {
                            CoverPoint e = points[l[i]];
                            if ((e.position - pos).sqrMagnitude < r2 && Vector3.Dot(e.normal, n) > 0.7f) return true;
                        }
                    }
            return false;
        }

        /// <summary>
        /// Height profile of the barrier in front of a stand spot: fire rays at the surface every 0.2 m up from the floor.
        /// Height = top of the unbroken run from the floor. Ceilings/overhangs/ledges are not vertical faces and end the run.
        /// Also returns the averaged face normal (smooths bumpy or curved surfaces).
        /// </summary>
        bool MeasureBarrier(Vector3 g, Vector3 n, float reach, out float height, out Vector3 avgNormal)
        {
            Vector3 nsum = Vector3.zero;
            height = 0f; avgNormal = n;
            const float step = 0.2f;
            float h = 0.3f;
            if (!BlockedAt(g, n, h, reach, ref nsum)) return false;
            float lastBlocked = h;
            bool open = false;
            for (h += step; h <= maxCoverHeight + 0.001f; h += step)
            {
                if (BlockedAt(g, n, h, reach, ref nsum)) { lastBlocked = h; continue; }
                float lo = lastBlocked, hi = h;                       // refine the top edge
                Vector3 dummy = Vector3.zero;
                for (int it = 0; it < 3; it++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (BlockedAt(g, n, mid, reach, ref dummy)) lo = mid; else hi = mid;
                }
                lastBlocked = lo; open = true;
                break;
            }
            height = open ? lastBlocked : maxCoverHeight;
            nsum.y = 0f;
            if (nsum.sqrMagnitude > 0.01f) avgNormal = nsum.normalized;
            return true;
        }

        bool BlockedAt(Vector3 g, Vector3 n, float h, float reach, ref Vector3 nsum)
        {
            RaycastHit r;
            if (!Physics.Raycast(g + Vector3.up * h, -n, out r, reach, coverMask.value, QueryTriggerInteraction.Ignore)) return false;
            if (Mathf.Abs(r.normal.y) > 0.5f) return false;           // an underside or a ledge top, not a vertical face
            Vector3 hn = r.normal; hn.y = 0f;
            nsum += hn.normalized;
            return true;
        }

        void BakeRooms(Collider[] cols)
        {
            if ((roomMask.value & coverMask.value) != 0)
                BF3Debug.W(DbgCat.Setup, "CoverMap: roomMask and coverMask share a layer. Room volumes must live on their own layer.", this);
            foreach (Collider c in cols)
            {
                if (((1 << c.gameObject.layer) & roomMask.value) == 0) continue;
                if (!c.isTrigger)
                    BF3Debug.E(DbgCat.Setup, "Room collider '" + c.name + "' is NOT a trigger. A solid room volume would block bullets, sight and movement. Tick Is Trigger.", c);
                MeshCollider mc = c as MeshCollider;
                if (mc != null && !mc.convex)
                    BF3Debug.W(DbgCat.Setup, "Room '" + c.name + "' is a non-convex MeshCollider; only its bounding box is used for containment. Prefer a Box collider.", c);
                Bounds b = c.bounds;
                rooms.Add(new RoomInfo { collider = c, name = c.name, volume = b.size.x * b.size.y * b.size.z });
            }
        }

        // ---------------------------------------------------------------- runtime spatial grid
        const float GridCell = 8f;
        Dictionary<Vector3Int, List<int>> grid;
        int gridBuiltFor = -1;
        static readonly List<int> scratch = new List<int>(256);

        public void BuildGrid()
        {
            grid = new Dictionary<Vector3Int, List<int>>();
            for (int i = 0; i < points.Count; i++)
            {
                Vector3Int k = new Vector3Int(Mathf.FloorToInt(points[i].position.x / GridCell), 0, Mathf.FloorToInt(points[i].position.z / GridCell));
                List<int> l;
                if (!grid.TryGetValue(k, out l)) { l = new List<int>(8); grid[k] = l; }
                l.Add(i);
            }
            gridBuiltFor = points.Count;
        }

        void Gather(Vector3 o, float r, List<int> buf)
        {
            if (grid == null || gridBuiltFor != points.Count) BuildGrid();
            buf.Clear();
            int x0 = Mathf.FloorToInt((o.x - r) / GridCell), x1 = Mathf.FloorToInt((o.x + r) / GridCell);
            int z0 = Mathf.FloorToInt((o.z - r) / GridCell), z1 = Mathf.FloorToInt((o.z + r) / GridCell);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    List<int> l;
                    if (grid.TryGetValue(new Vector3Int(x, 0, z), out l)) buf.AddRange(l);
                }
        }

        // ---------------------------------------------------------------- queries
        /// <summary>Does standing/crouching at this cover really hide the unit from a threat? (a line cast, not just the normal)</summary>
        static bool HidesFrom(CoverPoint p, Vector3 threat)
        {
            float hideH = p.IsLow ? Mathf.Max(0.5f, Mathf.Min(1.0f, p.height - 0.1f)) : 1.5f;
            return Physics.Linecast(p.position + Vector3.up * hideH, threat + Vector3.up * 1.2f, AIWorld.Obstacles, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Utility-scored cover search. Rewards: blocking known threats, closeness, progress toward a goal,
        /// sightlines (snipers). Penalises: distance, death-heatmap danger, being too near threats.
        /// The best candidate is also verified with a line cast so cover only counts if it truly hides the unit.
        /// </summary>
        public CoverPoint FindBest(AIUnit u, Vector3 origin, float radius, List<Vector3> threats,
                                   Vector3? goal, float goalWeight, float minThreatDist, bool wantSight)
        {
            CoverPoint best = null; float bestScore = float.MinValue;
            float r2 = radius * radius;
            float caution = u.personality != null ? u.personality.caution : 0.5f;
            DeathHeatmap hm = DeathHeatmap.I;

            Vector3 nearestThreat = Vector3.zero; float nt = float.MaxValue;
            for (int i = 0; i < threats.Count; i++)
            {
                float d = (threats[i] - origin).sqrMagnitude;
                if (d < nt) { nt = d; nearestThreat = threats[i]; }
            }

            Gather(origin, radius, scratch);
            for (int ii = 0; ii < scratch.Count; ii++)
            {
                CoverPoint p = points[scratch[ii]];
                if (p.occupant != null && p.occupant != u && p.occupant.IsAlive) continue;
                if (Mathf.Abs(p.position.y - origin.y) > maxVerticalReach) continue;      // another floor
                float d2 = (p.position - origin).sqrMagnitude;
                if (d2 > r2) continue;

                float block = threats.Count > 0 ? p.BlockedFraction(threats) : 0.5f;
                if (threats.Count > 0 && block < 0.34f) continue;

                float score = 2.2f * block - 0.7f * Mathf.Sqrt(d2) / radius;

                if (goal.HasValue && goalWeight > 0f)
                {
                    float g0 = Vector3.Distance(origin, goal.Value);
                    float g1 = Vector3.Distance(p.position, goal.Value);
                    score += goalWeight * Mathf.Clamp((g0 - g1) / Mathf.Max(radius, 1f), -1f, 1f);
                }

                float heat = (hm != null ? hm.GetDanger(p.position, u.team) : 0f) + Mathf.Clamp01(p.DeathScore / 3f);
                score -= 1.5f * heat * (0.5f + caution);           // learned "death zone" penalty

                if (minThreatDist > 0f)
                    for (int t = 0; t < threats.Count; t++)
                        if ((threats[t] - p.position).sqrMagnitude < minThreatDist * minThreatDist) { score -= 3f; break; }

                if (wantSight && threats.Count > 0)
                {
                    Vector3 pk;
                    if (p.TryPeek(nearestThreat + Vector3.up, 1.6f, out pk)) score += 1f;
                }

                if (u.unitClass == UnitClass.Heavy && !p.IsLow) score += 0.3f;   // heavies like tall cover

                if (score > bestScore)
                {
                    if (verifyOcclusion && threats.Count > 0 && !HidesFrom(p, nearestThreat)) continue;   // only costs a line cast for would-be winners
                    bestScore = score; best = p;
                }
            }
            return best;
        }

        void OnDrawGizmos()
        {
            if (!drawGizmos) return;
            for (int i = 0; i < rooms.Count; i++)
            {
                RoomInfo r = rooms[i];
                if (r == null || r.collider == null) continue;
                Gizmos.color = Color.HSVToRGB((i * 0.17f) % 1f, 0.6f, 1f);
                Bounds b = r.collider.bounds;
                Gizmos.DrawWireCube(b.center, b.size);
#if UNITY_EDITOR
                UnityEditor.Handles.Label(b.center + Vector3.up * (b.extents.y * 0.9f), "room[" + i + "] " + r.name);
#endif
            }
            Camera cam = Camera.current;
            bool filter = gizmoRadius > 0f && cam != null;
            Vector3 cp = filter ? cam.transform.position : Vector3.zero;
            float gr2 = gizmoRadius * gizmoRadius;
            foreach (CoverPoint p in points)
            {
                if (filter && (p.position - cp).sqrMagnitude > gr2) continue;
                Gizmos.color = p.IsLow ? Color.yellow : Color.cyan;
                Gizmos.DrawWireSphere(p.position + Vector3.up * 0.1f, 0.15f);
                Gizmos.DrawLine(p.position, p.position + p.normal * 0.6f);   // points to the protected side
                Vector3 wall = p.position - p.normal * standOffset;
                Gizmos.DrawLine(wall, wall + Vector3.up * p.height);           // the barrier's measured height
            }
        }
    }
}
