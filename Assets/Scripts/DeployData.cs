using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace BF3AI
{
    /// <summary>The four playable classes. Specialist is the sniper class on the AI side.</summary>
    public enum DeployClass { Assault = 0, Heavy = 1, Specialist = 2, Officer = 3 }

    public static class DeployClassInfo
    {
        public static string Label(DeployClass c) { return c.ToString(); }

        /// <summary>Folder names tried under Resources/CharacterPrefabs and Resources/Weapons for this class.</summary>
        public static string[] FolderNames(DeployClass c)
        {
            if (c == DeployClass.Specialist) return new[] { "Specialist", "Sniper" };
            return new[] { c.ToString() };
        }

        public static UnitClass ToUnitClass(DeployClass c)
        {
            switch (c) { case DeployClass.Heavy: return UnitClass.Heavy; case DeployClass.Specialist: return UnitClass.Sniper; case DeployClass.Officer: return UnitClass.Officer; default: return UnitClass.Assault; }
        }

        public static string Blurb(DeployClass c)
        {
            switch (c)
            {
                case DeployClass.Heavy: return "SUPPRESSION AND DURABILITY";
                case DeployClass.Specialist: return "LONG RANGE PRECISION";
                case DeployClass.Officer: return "SQUAD SUPPORT";
                default: return "CLOSE QUARTERS";
            }
        }
    }

    public class SkinOption { public string name; public GameObject prefab; public string path; }
    public class WeaponOption { public string name; public string path; public GameObject prefab; public Blaster blaster; }
    public class AttachmentOption { public string name; public string path; public GameObject prefab; public WeaponAttachment data; }

    /// <summary>
    /// Finds what the player can pick, from folders under Resources:
    ///   CharacterPrefabs/(Class)/          full player prefabs for that class: the first (or the one named like the class) is the default, the rest are skins
    ///   Weapons/(Class)/Primary/           primary weapon prefabs for that class        (also Weapons/(Class)/Secondary/)
    ///   Weapons/                           fallback: every prefab with a Blaster anywhere under it, when a class has no Primary/Secondary folder
    ///   Attachments/(WeaponName)/ and Attachments/Common/
    /// Folder names are tried in a few spellings (Sniper for Specialist, lower case).
    /// </summary>
    public static class DeployCatalog
    {
        public static string charactersRoot = "CharacterPrefabs";
        public static string weaponsRoot = "Weapons";
        public static string attachmentsRoot = "Attachments";

        /// <summary>How prefabs are loaded (replaceable for tests).</summary>
        public static Func<string, GameObject[]> loader = delegate (string p) { return Resources.LoadAll<GameObject>(p); };

        static List<string> Variants(string root, string sub)
        {
            List<string> v = new List<string>();
            Action<string> add = delegate (string s) { if (!v.Contains(s)) v.Add(s); };
            string subL = sub != null ? sub.ToLowerInvariant() : null;
            add(sub != null ? root + "/" + sub : root);
            if (sub != null) { add(root + "/" + subL); add(root.ToLowerInvariant() + "/" + sub); add(root.ToLowerInvariant() + "/" + subL); }
            else add(root.ToLowerInvariant());
            return v;
        }

        static GameObject[] LoadFirst(List<string> paths, out string matched)
        {
            matched = null;
            for (int i = 0; i < paths.Count; i++)
            {
                GameObject[] a = loader(paths[i]);
                if (a != null && a.Length > 0) { matched = paths[i]; return a; }
            }
            return new GameObject[0];
        }

        static string BaseName(string n) { return string.IsNullOrEmpty(n) ? "" : n.Replace("(Clone)", "").Trim(); }

        // ---------------------------------------------------------------- skins
        public static List<SkinOption> GetSkins(DeployClass c)
        {
            List<string> paths = new List<string>();
            foreach (string f in DeployClassInfo.FolderNames(c)) paths.AddRange(Variants(charactersRoot, f));
            string matched;
            GameObject[] found = LoadFirst(paths, out matched);
            List<SkinOption> list = new List<SkinOption>();
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i] == null) continue;
                SkinOption s = new SkinOption { name = BaseName(found[i].name), prefab = found[i], path = matched + "/" + found[i].name };
                list.Add(s);
            }
            string label = DeployClassInfo.Label(c).ToLowerInvariant();
            list.Sort(delegate (SkinOption a, SkinOption b)
            {
                int ra = SkinRank(a.name, label), rb = SkinRank(b.name, label);
                return ra != rb ? ra.CompareTo(rb) : string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        // the generic prefab comes first: one named like the class, or "default"/"generic"/"base", then everything else A-Z
        static int SkinRank(string name, string classLabel)
        {
            string n = name.ToLowerInvariant();
            if (n == classLabel || n == classLabel + "default" || n == classLabel + "_default") return 0;
            if (n.Contains("default") || n.Contains("generic") || n.Contains("base")) return 1;
            return 2;
        }

        // ---------------------------------------------------------------- weapons
        public static List<WeaponOption> GetWeapons(DeployClass c, bool primary)
        {
            string leaf = primary ? "Primary" : "Secondary";
            List<string> paths = new List<string>();
            foreach (string f in DeployClassInfo.FolderNames(c)) paths.AddRange(Variants(weaponsRoot + "/" + f, leaf));
            string matched;
            GameObject[] found = LoadFirst(paths, out matched);
            if (found.Length == 0) { found = loader(weaponsRoot); matched = weaponsRoot; }       // fallback: every weapon prefab under Weapons
            List<WeaponOption> list = new List<WeaponOption>();
            if (found == null) return list;
            for (int i = 0; i < found.Length; i++)
            {
                GameObject go = found[i];
                if (go == null) continue;
                Blaster b = go.GetComponentInChildren<Blaster>(true);
                if (b == null) continue;                                    // not a weapon
                string rel = matched != null && matched.Length > weaponsRoot.Length ? matched.Substring(weaponsRoot.Length + 1) + "/" : "";
                list.Add(new WeaponOption { name = WeaponAttachment.Clean(go.name), path = rel + go.name, prefab = go, blaster = b });
            }
            list.Sort(delegate (WeaponOption a, WeaponOption b) { return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        // ---------------------------------------------------------------- attachments
        /// <summary>Attachments for one weapon (by its prefab name) in one slot: that weapon's own folder plus Common.</summary>
        public static List<AttachmentOption> GetAttachments(string weaponPrefabName, AttachmentSlot slot)
        {
            List<AttachmentOption> list = new List<AttachmentOption>();
            string weapon = BaseName(weaponPrefabName);
            string[] roots = { attachmentsRoot + "/" + weapon, attachmentsRoot + "/Common" };
            for (int r = 0; r < roots.Length; r++)
            {
                GameObject[] found = loader(roots[r]);
                if (found == null) continue;
                for (int i = 0; i < found.Length; i++)
                {
                    GameObject go = found[i];
                    if (go == null) continue;
                    WeaponAttachment d = go.GetComponent<WeaponAttachment>();
                    if (d == null || d.slot != slot) continue;
                    list.Add(new AttachmentOption { name = d.DisplayName, path = roots[r] + "/" + go.name, prefab = go, data = d });
                }
            }
            list.Sort(delegate (AttachmentOption a, AttachmentOption b) { return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        public static AttachmentOption FindAttachment(string weaponPrefabName, AttachmentSlot slot, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            List<AttachmentOption> l = GetAttachments(weaponPrefabName, slot);
            for (int i = 0; i < l.Count; i++) if (l[i].path == path) return l[i];
            return null;
        }
    }

    /// <summary>What the player picked for one class. One per class (like the AI's loadout rows, but remembered).</summary>
    public class PlayerLoadout
    {
        public DeployClass cls;
        public string skin = "";                          // skin prefab name
        public string primary = "";                       // weapon path relative to Resources/Weapons
        public string secondary = "";
        public string[] primaryAtt = new string[3] { "", "", "" };      // Resources paths, one per slot (Optic, Barrel, Grip)
        public string[] secondaryAtt = new string[3] { "", "", "" };
        public string[] perks = new string[3] { "", "", "" };           // placeholders for the perk cards
    }

    /// <summary>Remembers the last loadout used for each class, and the last class, in a text file in the game's save folder.</summary>
    public static class PlayerLoadoutStore
    {
        public static string fileName = "BF3AI_PlayerLoadouts.txt";
        public static string pathOverride = null;
        static Dictionary<DeployClass, PlayerLoadout> map = new Dictionary<DeployClass, PlayerLoadout>();
        static bool loaded;
        public static DeployClass lastClass = DeployClass.Assault;

        public static string FilePath { get { return pathOverride != null ? pathOverride : Path.Combine(Application.persistentDataPath, fileName); } }

        public static void Reset() { map = new Dictionary<DeployClass, PlayerLoadout>(); loaded = false; lastClass = DeployClass.Assault; }

        public static PlayerLoadout Get(DeployClass c)
        {
            EnsureLoaded();
            PlayerLoadout lo;
            if (!map.TryGetValue(c, out lo)) { lo = new PlayerLoadout { cls = c }; map[c] = lo; }
            return lo;
        }

        public static void Set(PlayerLoadout lo) { EnsureLoaded(); map[lo.cls] = lo; }

        static void EnsureLoaded() { if (!loaded) { loaded = true; Load(); } }

        static string Join(string[] a) { return string.Join(",", a ?? new string[0]); }
        static string[] Split3(string s)
        {
            string[] parts = (s ?? "").Split(',');
            string[] r = new string[3] { "", "", "" };
            for (int i = 0; i < 3 && i < parts.Length; i++) r[i] = parts[i].Trim();
            return r;
        }

        public static void Save()
        {
            EnsureLoaded();
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("lastClass\t" + lastClass);
                foreach (KeyValuePair<DeployClass, PlayerLoadout> kv in map)
                {
                    PlayerLoadout l = kv.Value;
                    sb.AppendLine(kv.Key + "\t" + l.skin + "\t" + l.primary + "\t" + Join(l.primaryAtt) + "\t" + l.secondary + "\t" + Join(l.secondaryAtt) + "\t" + Join(l.perks));
                }
                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (Exception e) { Debug.LogWarning("[BF3AI] Could not save the player loadouts: " + e.Message); }
        }

        public static void Load()
        {
            map = new Dictionary<DeployClass, PlayerLoadout>();
            loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                string[] lines = File.ReadAllLines(FilePath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string[] f = lines[i].Split('\t');
                    if (f.Length < 2) continue;
                    if (f[0] == "lastClass") { DeployClass lc; if (TryParse(f[1], out lc)) lastClass = lc; continue; }
                    DeployClass c;
                    if (!TryParse(f[0], out c) || f.Length < 6) continue;
                    PlayerLoadout l = new PlayerLoadout { cls = c, skin = f[1], primary = f[2], primaryAtt = Split3(f[3]), secondary = f[4], secondaryAtt = Split3(f[5]) };
                    if (f.Length > 6) l.perks = Split3(f[6]);
                    map[c] = l;
                }
            }
            catch (Exception e) { Debug.LogWarning("[BF3AI] Could not read the player loadouts file: " + e.Message); }
        }

        static bool TryParse(string s, out DeployClass c)
        {
            c = DeployClass.Assault;
            try { c = (DeployClass)Enum.Parse(typeof(DeployClass), s, true); return Enum.IsDefined(typeof(DeployClass), c); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Maps world positions (x, z) onto the map picture (any shape) and back.</summary>
    public struct MapView
    {
        public float minX, minZ, sizeX, sizeZ;

        public Vector2 ToUV(Vector3 world) { return new Vector2((world.x - minX) / sizeX, (world.z - minZ) / sizeZ); }
        public Vector3 ToWorld(Vector2 uv, float y) { return new Vector3(minX + uv.x * sizeX, y, minZ + uv.y * sizeZ); }

        /// <summary>A view with the picture's aspect ratio (width / height) that contains the given box with a little padding.</summary>
        public static MapView Fit(Vector3 min, Vector3 max, float padFraction, float aspect)
        {
            if (aspect < 0.1f) aspect = 1f;
            float w = Mathf.Max(1f, max.x - min.x) * (1f + 2f * padFraction), d = Mathf.Max(1f, max.z - min.z) * (1f + 2f * padFraction);
            float sx = w, sz = d;
            if (sx / sz < aspect) sx = sz * aspect; else sz = sx / aspect;      // grow the short side so the picture is not stretched
            MapView v = new MapView();
            v.sizeX = sx; v.sizeZ = sz;
            v.minX = (min.x + max.x) * 0.5f - sx * 0.5f;
            v.minZ = (min.z + max.z) * 0.5f - sz * 0.5f;
            return v;
        }
    }

    /// <summary>One place the player can appear: one of the team's spawn points or a living squadmate.</summary>
    public class SpawnTarget
    {
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public string label = "";
        public bool isSquadmate;
        public bool isEnemy;                // an enemy spawn point (shown, never selectable)
        public Transform transform;         // the spawn point, or the squadmate
        public AIUnit unit;
        public bool Selectable { get { return !isEnemy; } }
    }

    public static class DeploySpawns
    {
        /// <summary>The selectable target closest to a clicked world position (flat distance). -1 if there is none.</summary>
        public static int Nearest(IList<SpawnTarget> targets, Vector3 world)
        {
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] == null || !targets[i].Selectable) continue;
                Vector3 d = targets[i].position - world; d.y = 0f;
                float s = d.sqrMagnitude;
                if (s < bd) { bd = s; best = i; }
            }
            return best;
        }

        static Squad lastSquad;

        /// <summary>
        /// The player's squadmates: up to 'max' living bots from one friendly squad. It keeps the same squad while that squad has anyone alive;
        /// otherwise it picks the squad with the most survivors (nearest to 'near' on a tie).
        /// </summary>
        public static List<AIUnit> PickMates(int team, Vector3 near, int max)
        {
            List<AIUnit> mates = new List<AIUnit>();
            SquadManager sm = SquadManager.Instance;
            if (sm == null || sm.teams == null) return mates;
            SquadManager.TeamConfig cfg = null;
            for (int i = 0; i < sm.teams.Count; i++) if (sm.teams[i].team == team) cfg = sm.teams[i];
            if (cfg == null || cfg.squads == null) return mates;

            Squad pick = null;
            if (lastSquad != null && cfg.squads.Contains(lastSquad) && LiveCount(lastSquad) > 0) pick = lastSquad;
            else
            {
                int bestN = 0; float bestD = float.MaxValue;
                for (int i = 0; i < cfg.squads.Count; i++)
                {
                    Squad s = cfg.squads[i]; int n = LiveCount(s);
                    if (n == 0) continue;
                    float d = 0f; int k = 0;
                    for (int m = 0; m < s.members.Count; m++) if (Alive(s.members[m])) { d += (s.members[m].transform.position - near).sqrMagnitude; k++; }
                    d = k > 0 ? d / k : float.MaxValue;
                    if (n > bestN || (n == bestN && d < bestD)) { bestN = n; bestD = d; pick = s; }
                }
                lastSquad = pick;
            }
            if (pick == null) return mates;
            for (int m = 0; m < pick.members.Count && mates.Count < max; m++) if (Alive(pick.members[m])) mates.Add(pick.members[m]);
            return mates;
        }

        public static void ForgetSquad() { lastSquad = null; }
        static bool Alive(AIUnit u) { return u != null && u.isActiveAndEnabled && u.IsAlive; }
        static int LiveCount(Squad s) { int n = 0; for (int i = 0; i < s.members.Count; i++) if (Alive(s.members[i])) n++; return n; }
    }
}
