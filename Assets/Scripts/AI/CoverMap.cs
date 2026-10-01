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
        public float height;      // barrier height above the ground at the stand position
        public float thickness;   // thin barriers allow blind fire / bullet penetration
        public int room = -1;     // index into CoverMap.rooms (-1 = not inside any room volume)

        public const float LowMax = 1.35f;   // below this the unit must crouch
        [NonSerialized] public AIUnit occupant;
        [NonSerialized] float deaths;
        [NonSerialized] float stamp;

        public bool IsLow { get { return height < LowMax; } }
        public bool IsThin { get { return thickness < 0.3f; } }
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

        [Header("Bake settings")]
        public LayerMask coverMask;
        public float sampleSpacing = 1.5f;
        public float standOffset = 0.6f;
        public float minCoverHeight = 0.6f;
        public float maxCoverHeight = 3f;
        public float perimeterOffset = 1.2f;

        [Header("Rooms / regions (every room is ONE big trigger collider on this layer)")]
        [Tooltip("Layer holding the room colliders. Keep it OUT of obstacleMask/coverMask. Colliders MUST be triggers.")]
        public LayerMask roomMask;

        [Header("Baked data")]
        public List<CoverPoint> points = new List<CoverPoint>();
        public List<RoomInfo> rooms = new List<RoomInfo>();
        public bool drawGizmos = true;

        void Awake()
        {
            I = this;
            foreach (CoverPoint p in points) p.occupant = null;
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
        [ContextMenu("Bake Cover Points")]
        public void Bake()
        {
            points.Clear(); rooms.Clear();
#if UNITY_2023_1_OR_NEWER
            Collider[] cols = FindObjectsByType<Collider>(FindObjectsSortMode.None);
#else
            Collider[] cols = FindObjectsOfType<Collider>();
#endif
            foreach (Collider c in cols)
            {
                if (c.isTrigger || ((1 << c.gameObject.layer) & coverMask.value) == 0) continue;
                Bounds b = c.bounds;
                if (b.size.y < minCoverHeight) continue;
                float y = b.min.y + Mathf.Min(0.6f, b.size.y * 0.5f);
                foreach (Vector3 p in Perimeter(b, perimeterOffset, sampleSpacing))
                    TryMakePoint(c, new Vector3(p.x, y, p.z), b);
            }
            BakeRooms(cols);
            for (int i = 0; i < points.Count; i++) points[i].room = RoomAt(points[i].position);

            BF3Debug.I(DbgCat.Setup, "CoverMap: baked " + points.Count + " cover points and " + rooms.Count + " rooms.", this);
            if (points.Count == 0)
                BF3Debug.E(DbgCat.Setup, "CoverMap: 0 cover points. Check: NavMesh baked first? coverMask set to the cover layer? Cover colliders non-trigger and >= " + minCoverHeight + " m tall?", this);
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

        static IEnumerable<Vector3> Perimeter(Bounds b, float off, float step)
        {
            float hx = b.extents.x + off, hz = b.extents.z + off; Vector3 c = b.center;
            for (float x = -hx; x <= hx; x += step)
            {
                yield return new Vector3(c.x + x, c.y, c.z - hz);
                yield return new Vector3(c.x + x, c.y, c.z + hz);
            }
            for (float z = -hz + step; z < hz; z += step)
            {
                yield return new Vector3(c.x - hx, c.y, c.z + z);
                yield return new Vector3(c.x + hx, c.y, c.z + z);
            }
        }

        void TryMakePoint(Collider c, Vector3 origin, Bounds b)
        {
            MeshCollider mc = c as MeshCollider;
            Vector3 target = (mc != null && !mc.convex) ? new Vector3(b.center.x, origin.y, b.center.z) : c.ClosestPoint(origin);
            Vector3 dir = target - origin;
            if (dir.sqrMagnitude < 0.0001f) { target = new Vector3(b.center.x, origin.y, b.center.z); dir = target - origin; }
            float len = dir.magnitude; dir /= len;

            RaycastHit hit;
            if (!c.Raycast(new Ray(origin, dir), out hit, len + 3f)) return;
            Vector3 n = hit.normal; n.y = 0f;
            if (n.sqrMagnitude < 0.5f) return;
            n.Normalize();

            NavMeshHit nh;
            if (!NavMesh.SamplePosition(hit.point + n * standOffset, out nh, 0.7f, NavMesh.AllAreas)) return;
            float height = Mathf.Min(b.max.y - nh.position.y, maxCoverHeight);
            if (height < minCoverHeight) return;
            if (Physics.CheckSphere(nh.position + Vector3.up, 0.3f, coverMask)) return;   // too cramped

            foreach (CoverPoint e in points)
                if ((e.position - nh.position).sqrMagnitude < 0.8f * 0.8f * sampleSpacing * sampleSpacing) return;

            float thickness = 1f;
            RaycastHit back;
            if (c.Raycast(new Ray(hit.point - n * 10f, n), out back, 12f)) thickness = Mathf.Max(0.05f, 10f - back.distance);

            points.Add(new CoverPoint { position = nh.position, normal = n, height = height, thickness = thickness });
        }

        // ---------------------------------------------------------------- queries
        /// <summary>
        /// Utility-scored cover search. Rewards: blocking known threats, closeness, progress toward a goal,
        /// sightlines (snipers). Penalises: distance, death-heatmap danger, thin cover, being too near threats.
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

            for (int i = 0; i < points.Count; i++)
            {
                CoverPoint p = points[i];
                if (p.occupant != null && p.occupant != u && p.occupant.IsAlive) continue;
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
                if (p.IsThin) score -= 0.25f;

                if (score > bestScore) { bestScore = score; best = p; }
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
            foreach (CoverPoint p in points)
            {
                Gizmos.color = p.IsLow ? Color.yellow : Color.cyan;
                Gizmos.DrawWireSphere(p.position + Vector3.up * 0.1f, 0.15f);
                Gizmos.DrawLine(p.position, p.position + p.normal * 0.6f);   // points to the protected side
            }
        }
    }
}
