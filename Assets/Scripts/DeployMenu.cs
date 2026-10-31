using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace BF3AI
{
    // ====================================================================================================================================
    //  DeployMenu: the class selection / deploy screen. Everything is built at run time (no prefab or scene setup beyond adding the component).
    //  Layout: map and class selection on the left; 3D character, skin, perk cards and weapons on the right.
    // ====================================================================================================================================

    /// <summary>Colours, fonts and generated sprites for the menu (dark translucent panels, thin white/cyan lines, like the Battlefront II menus).</summary>
    public static class DeployStyle
    {
        public static readonly Color Bg = new Color(0.02f, 0.03f, 0.05f, 0.98f);
        public static readonly Color Panel = new Color(0.047f, 0.07f, 0.098f, 0.85f);
        public static readonly Color PanelLight = new Color(0.10f, 0.15f, 0.21f, 0.92f);
        public static readonly Color Line = new Color(1f, 1f, 1f, 0.16f);
        public static readonly Color LineBright = new Color(1f, 1f, 1f, 0.5f);
        public static readonly Color Accent = new Color(0.247f, 0.784f, 1f, 1f);          // #3FC8FF
        public static readonly Color AccentDim = new Color(0.12f, 0.36f, 0.5f, 1f);
        public static readonly Color Text = new Color(0.94f, 0.96f, 0.97f, 1f);
        public static readonly Color TextDim = new Color(0.47f, 0.55f, 0.62f, 1f);
        public static readonly Color Disabled = new Color(0.38f, 0.45f, 0.53f, 1f);
        public static readonly Color Ally = Accent;
        public static readonly Color Enemy = new Color(1f, 0.243f, 0.243f, 1f);           // #FF3E3E
        public static readonly Color Squad = new Color(1f, 0.816f, 0.157f, 1f);           // #FFD028

        static Font font;
        public static Font GetFont()
        {
            if (font != null) return font;
            try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { }
            if (font == null) { try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { } }
            return font;
        }

        static Sprite panel, frame, dot, ring;

        static bool InChamfer(int x, int y, int w, int h, int c)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            return x + y >= c && (w - 1 - x) + y >= c && x + (h - 1 - y) >= c && (w - 1 - x) + (h - 1 - y) >= c;
        }

        static Sprite Make(int w, int h, Func<int, int, Color> px, int border)
        {
            Texture2D t = new Texture2D(w, h);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) t.SetPixel(x, y, px(x, y));
            t.Apply();
            t.filterMode = FilterMode.Bilinear; t.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(t, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        }

        /// <summary>A filled rectangle with slightly cut corners (9-sliced: use Image Type Sliced).</summary>
        public static Sprite PanelSprite()
        {
            if (panel == null) panel = Make(16, 16, delegate (int x, int y) { return InChamfer(x, y, 16, 16, 3) ? Color.white : new Color(1, 1, 1, 0); }, 5);
            return panel;
        }

        /// <summary>The 1 pixel outline of the same shape.</summary>
        public static Sprite FrameSprite()
        {
            if (frame == null) frame = Make(16, 16, delegate (int x, int y)
            {
                bool o = InChamfer(x, y, 16, 16, 3), i = InChamfer(x - 1, y - 1, 14, 14, 2);
                return (o && !i) ? Color.white : new Color(1, 1, 1, 0);
            }, 5);
            return frame;
        }

        public static Sprite DotSprite()
        {
            if (dot == null) dot = Make(32, 32, delegate (int x, int y) { float dx = x - 15.5f, dy = y - 15.5f; float d = Mathf.Sqrt(dx * dx + dy * dy); return new Color(1, 1, 1, Mathf.Clamp01(15f - d)); }, 0);
            return dot;
        }

        public static Sprite RingSprite()
        {
            if (ring == null) ring = Make(64, 64, delegate (int x, int y) { float dx = x - 31.5f, dy = y - 31.5f; float d = Mathf.Sqrt(dx * dx + dy * dy); return new Color(1, 1, 1, Mathf.Clamp01(31f - d) * Mathf.Clamp01(d - 28f)); }, 0);
            return ring;
        }
    }

    /// <summary>A spawn point or squadmate dot on the map: hover for a name tag, click to deploy there.</summary>
    public class DeployMarker : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        [NonSerialized] public DeployMenu menu;
        [NonSerialized] public int index;
        public void OnPointerEnter(PointerEventData e) { if (menu != null) menu.MarkerHover(index, true); }
        public void OnPointerExit(PointerEventData e) { if (menu != null) menu.MarkerHover(index, false); }
        public void OnPointerClick(PointerEventData e) { if (menu != null) menu.MarkerClick(index); }
    }

    /// <summary>Click anywhere on the map image: deploy to the closest spawn point or squadmate.</summary>
    public class DeployMapClick : MonoBehaviour, IPointerClickHandler
    {
        [NonSerialized] public DeployMenu menu;
        public void OnPointerClick(PointerEventData e) { if (menu != null) menu.MapClick(e.position); }
    }

    /// <summary>
    /// A small off-screen 3D stage rendered to a texture (the character view and the two weapon views).
    /// The model is a harmless copy: its scripts never run and its colliders, cameras and rigidbodies are removed.
    /// </summary>
    public class DeployPreviewView
    {
        public RenderTexture texture;
        Camera cam;
        Transform stage, pivot, holder;
        GameObject instance;
        float spin;
        bool tall;

        public DeployPreviewView(string name, Vector3 stageOrigin, int w, int h, float spinDegPerSec, bool characterFraming)
        {
            spin = spinDegPerSec; tall = characterFraming;
            GameObject s = new GameObject("BF3AI_Preview_" + name);
            stage = s.transform; stage.position = stageOrigin;
            GameObject p = new GameObject("Pivot"); pivot = p.transform; pivot.SetParent(stage, false);

            texture = new RenderTexture(w, h, 24);
            GameObject cg = new GameObject("Camera"); cg.transform.SetParent(stage, false);
            cam = cg.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.03f, 0.07f, 0.11f, 1f);
            cam.fieldOfView = 28f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 60f;
            cam.targetTexture = texture;
            cam.allowHDR = false;

            AddLight(new Vector3(-2.5f, 2.5f, -3f), new Color(0.8f, 0.92f, 1f), 1.6f);
            AddLight(new Vector3(3f, 1.5f, 2.5f), new Color(0.25f, 0.7f, 1f), 1.2f);
            AddLight(new Vector3(0f, -1f, -3f), new Color(1f, 0.9f, 0.8f), 0.6f);
            SetActive(false);
        }

        void AddLight(Vector3 local, Color c, float intensity)
        {
            GameObject g = new GameObject("Light"); g.transform.SetParent(stage, false); g.transform.localPosition = local;
            Light l = g.AddComponent<Light>(); l.type = LightType.Point; l.color = c; l.range = 14f; l.intensity = intensity * 3f;
        }

        public void SetActive(bool on) { if (stage != null) stage.gameObject.SetActive(on); }

        public void Tick(float dt) { if (pivot != null && instance != null) pivot.Rotate(0f, spin * dt, 0f); }

        public void Clear()
        {
            if (holder != null) UnityEngine.Object.DestroyImmediate(holder.gameObject);
            holder = null; instance = null;
        }

        /// <summary>Shows a prefab (and optionally attachment prefabs on it). The prefab's scripts are never run.</summary>
        public void Show(GameObject prefab, IList<GameObject> attachments)
        {
            Clear();
            if (prefab == null) return;
            GameObject h = new GameObject("Holder");
            h.SetActive(false);                                       // instances made under an inactive parent never run Awake/OnEnable
            h.transform.SetParent(pivot, false);
            holder = h.transform;
            instance = UnityEngine.Object.Instantiate(prefab, h.transform);
            instance.transform.localPosition = Vector3.zero; instance.transform.localRotation = Quaternion.identity;
            if (attachments != null)
            {
                for (int i = 0; i < attachments.Count; i++)
                {
                    if (attachments[i] == null) continue;
                    WeaponAttachment d = attachments[i].GetComponent<WeaponAttachment>();
                    Transform mount = WeaponAttachment.FindDeep(instance.transform, d != null ? d.mountPoint : null);
                    GameObject a = UnityEngine.Object.Instantiate(attachments[i], mount != null ? mount : instance.transform);
                    a.transform.localPosition = Vector3.zero; a.transform.localRotation = Quaternion.identity;
                }
            }
            MakeInert(instance);
            h.SetActive(true);
            Frame();
        }

        // Removes everything that could do anything but be looked at (children first, so "requires" relationships are respected).
        static void MakeInert(GameObject root)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int t = 0; t < all.Length; t++)
            {
                Component[] comps = all[t].GetComponents<Component>();
                for (int i = comps.Length - 1; i >= 0; i--)
                {
                    Component c = comps[i];
                    if (c == null) continue;
                    if (c is MonoBehaviour || c is Camera || c is AudioListener || c is AudioSource || c is Rigidbody || c is Collider || c is CharacterController || c is NavMeshAgent)
                    {
                        if (c is Animator) continue;
                        UnityEngine.Object.DestroyImmediate(c);
                    }
                }
            }
        }

        void Frame()
        {
            if (instance == null) return;
            Renderer[] rs = instance.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return;
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            holder.position -= (b.center - pivot.position);                            // centre it on the pivot
            Vector3 ext = b.extents;
            float halfV = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float aspect = texture.width / (float)texture.height;
            float halfH = Mathf.Atan(Mathf.Tan(halfV) * aspect);
            float dist;
            if (tall) dist = Mathf.Max(ext.y / Mathf.Tan(halfV), Mathf.Max(ext.x, ext.z) / Mathf.Tan(halfH)) * 1.12f + Mathf.Max(ext.x, ext.z);
            else { float r = ext.magnitude; dist = Mathf.Max(r / Mathf.Sin(halfV), r / Mathf.Sin(halfH)) * 1.05f; }
            cam.transform.position = pivot.position + new Vector3(0f, tall ? ext.y * 0.08f : 0f, -dist);
            cam.transform.LookAt(pivot.position);
        }

        public void Dispose()
        {
            if (texture != null) { texture.Release(); UnityEngine.Object.Destroy(texture); }
            if (stage != null) UnityEngine.Object.Destroy(stage.gameObject);
        }
    }

    /// <summary>
    /// The deploy screen. Add this component to an empty GameObject (next to the SquadManager). It opens when the game starts and every time the player
    /// dies (PlayerDeath hands over). The player picks a class, skin, weapons and attachments, then clicks a spawn point or squadmate on the map.
    /// </summary>
    public class DeployMenu : MonoBehaviour
    {
        public static DeployMenu Instance;

        [Header("When")]
        [Tooltip("Open the menu when the scene starts (the scene's player is hidden until you deploy).")]
        public bool openOnStart = true;
        [Tooltip("Freeze the battle (Time Scale 0) while the menu is open at the start of the game. Death-screen menus never freeze it.")]
        public bool pauseAtStart = true;
        [Tooltip("Lock and hide the mouse again when the menu closes (for a first/third person player).")]
        public bool lockCursorOnClose = true;

        [Header("Teams")]
        [Tooltip("The player's team. Read from the scene's player if it has a Combatant. Spawn points of this team are blue and selectable; other teams' are red.")]
        public int playerTeam = 1;
        [Tooltip("Shown above the map. Empty = the scene's name.")]
        public string mapName = "";

        [Header("Map")]
        [Tooltip("Optional. Your own map picture. Empty = a top-down picture of the level is captured automatically.")]
        public Texture mapTexture;
        [Tooltip("Only with your own map picture: the world corners (x, z) it covers.")]
        public Vector3 mapWorldMin = new Vector3(-100, 0, -100), mapWorldMax = new Vector3(100, 0, 100);
        public int captureResolution = 1024;
        public LayerMask captureMask = ~0;
        [Tooltip("Extra space around the playable area in the captured map (0.05 = 5%).")]
        public float capturePadding = 0.05f;

        public event Action<GameObject> OnDeployed;
        public bool IsOpen { get { return open; } }

        // ------------------------------------------------------------------ state
        bool open, built, deploying;
        float prevTimeScale = 1f;
        GameObject oldPlayer;
        bool oldWasHidden;
        DeployClass curClass = DeployClass.Assault;
        PlayerLoadout lo;
        List<SkinOption> skins = new List<SkinOption>();
        List<WeaponOption>[] weapons = { new List<WeaponOption>(), new List<WeaponOption>() };     // [0] primary, [1] secondary
        readonly List<SpawnTarget> targets = new List<SpawnTarget>();
        readonly List<DeployMarker> markers = new List<DeployMarker>();
        readonly List<Image> markerImages = new List<Image>();
        MapView view;
        bool mapReady;
        float nextMateRefresh;
        int hoverIndex = -1;
        int selectedIndex = -1;
        Transform selectedTransform;
        Text spawnLabel, squadLabel, classTitle, skinPath;
        readonly List<RectTransform> pings = new List<RectTransform>();
        readonly List<float> pingBorn = new List<float>();
        readonly List<RectTransform> markerRings = new List<RectTransform>();

        // ------------------------------------------------------------------ ui
        Canvas canvas; RectTransform rootRect, mapRect;
        RawImage mapRaw; Text hintText, mapTitle, tooltipText, messageText, skinText;
        RectTransform tooltipRect;
        readonly Image[] classFill = new Image[4];
        readonly Text[] classLabel = new Text[4];
        readonly WeaponSlotUI[] wslot = new WeaponSlotUI[2];
        DeployPreviewView charView;
        DeployPreviewView[] weaponView = new DeployPreviewView[2];

        class WeaponSlotUI
        {
            public Text name, stats;
            public RawImage view;
            public Text[] attLabel = new Text[3];
            public Image[] attIcon = new Image[3];
            public Image[] attFill = new Image[3];
            public Image[] attFrame = new Image[3];
        }
        readonly Image[] classFrame = new Image[4], classBar = new Image[4];

        // picker popup
        GameObject pickerRoot; Text pickerTitle, pickerPageText;
        readonly List<Button> pickerRows = new List<Button>();
        readonly List<Text> pickerRowLabel = new List<Text>(), pickerRowSub = new List<Text>();
        readonly List<Image> pickerRowIcon = new List<Image>();
        List<PickItem> pickItems = new List<PickItem>();
        int pickPage;
        const int PickRowsPerPage = 7;

        class PickItem { public string label, sub; public Sprite icon; public Action onPick; public bool selected; }

        // =============================================================================================================================
        void Awake() { Instance = this; }
        void OnDestroy() { if (Instance == this) Instance = null; DisposeUI(); }

        void Start()
        {
            if (openOnStart) Open(null);
        }

        /// <summary>Opens the menu. Pass the dead player (PlayerDeath does) or null at the start of the game.</summary>
        public void Open(GameObject player)
        {
            if (open) return;
            try
            {
                if (!built) Build();
                EnsureEventSystem();
                bool atStart = player == null;
                oldPlayer = player != null ? player : FindScenePlayer();
                oldWasHidden = false;
                if (atStart && oldPlayer != null)
                {
                    Combatant pc = oldPlayer.GetComponentInChildren<Combatant>(true);
                    if (pc != null) playerTeam = pc.team;
                    oldPlayer.SetActive(false);                                      // hidden until you deploy
                    oldWasHidden = true;
                }
                else if (oldPlayer != null)
                {
                    Combatant pc = oldPlayer.GetComponentInChildren<Combatant>(true);
                    if (pc != null) playerTeam = pc.team;
                }
                if (!mapReady) { CaptureMap(); mapReady = true; }

                PlayerLoadoutStore.Get(DeployClass.Assault);                         // loads the saved file
                SelectClass(PlayerLoadoutStore.lastClass, false);
                RefreshTargets(true);
                SetMessage("");

                prevTimeScale = Time.timeScale;
                if (atStart && pauseAtStart) Time.timeScale = 0f;
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
                canvas.gameObject.SetActive(true);
                charView.SetActive(true); weaponView[0].SetActive(true); weaponView[1].SetActive(true);
                UpdateScale();
                open = true; deploying = false;
            }
            catch (Exception e)
            {
                Debug.LogError("[BF3AI] DeployMenu could not open: " + e);
                if (oldWasHidden && oldPlayer != null) oldPlayer.SetActive(true);
            }
        }

        public void Close()
        {
            open = false;
            ClosePicker();
            if (canvas != null) canvas.gameObject.SetActive(false);
            if (charView != null) charView.SetActive(false);
            for (int i = 0; i < 2; i++) if (weaponView[i] != null) weaponView[i].SetActive(false);
            Time.timeScale = prevTimeScale <= 0f ? 1f : prevTimeScale;
            if (lockCursorOnClose) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        }

        GameObject FindScenePlayer()
        {
            for (int i = 0; i < Combatant.All.Count; i++)
                if (Combatant.All[i] != null && Combatant.All[i].isPlayer) return Combatant.All[i].gameObject;
            return null;
        }

        void Update()
        {
            if (!open) return;
            float dt = Time.unscaledDeltaTime;
            charView.Tick(dt); weaponView[0].Tick(dt); weaponView[1].Tick(dt);
            UpdateScale();
            if (Time.unscaledTime >= nextMateRefresh) { nextMateRefresh = Time.unscaledTime + 0.4f; RefreshTargets(false); }
            UpdateMarkerPositions();
        }

        // ============================================================================================================== map
        Bounds WorldBounds()
        {
            Vector3 mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;
            try
            {
                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                if (tri.vertices != null)
                    for (int i = 0; i < tri.vertices.Length; i++) { Vector3 v = tri.vertices[i]; mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); any = true; }
            }
            catch (Exception) { }
            SquadManager sm = SquadManager.Instance;
            if (sm != null && sm.teams != null)
                for (int t = 0; t < sm.teams.Count; t++)
                    if (sm.teams[t].spawnPoints != null)
                        for (int i = 0; i < sm.teams[t].spawnPoints.Length; i++)
                        {
                            Transform sp = sm.teams[t].spawnPoints[i]; if (sp == null) continue;
                            mn = Vector3.Min(mn, sp.position - new Vector3(20, 0, 20)); mx = Vector3.Max(mx, sp.position + new Vector3(20, 0, 20)); any = true;
                        }
            if (!any) { mn = new Vector3(-100, 0, -100); mx = new Vector3(100, 20, 100); }
            Bounds b = new Bounds((mn + mx) * 0.5f, mx - mn);
            return b;
        }

        void CaptureMap()
        {
            float aspect = mapRect.sizeDelta.x / Mathf.Max(1f, mapRect.sizeDelta.y);
            if (mapTexture != null)
            {
                mapRaw.texture = mapTexture;
                float ta = mapTexture.width / (float)Mathf.Max(1, mapTexture.height);
                view = MapView.Fit(mapWorldMin, mapWorldMax, 0f, ta);
                return;
            }
            Bounds b = WorldBounds();
            view = MapView.Fit(b.min, b.max, capturePadding, aspect);
            int h = Mathf.Clamp(captureResolution, 256, 4096);
            RenderTexture rt = new RenderTexture(Mathf.RoundToInt(h * aspect), h, 24);
            GameObject cg = new GameObject("BF3AI_MapCamera");
            Camera cam = cg.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = view.sizeZ * 0.5f;                                  // the width follows from the picture's aspect
            cam.transform.position = new Vector3(view.minX + view.sizeX * 0.5f, b.max.y + 150f, view.minZ + view.sizeZ * 0.5f);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);                    // looking straight down: image right = +x, image up = +z
            cam.nearClipPlane = 1f; cam.farClipPlane = (b.max.y - b.min.y) + 400f;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.03f, 0.06f, 0.09f, 1f);
            cam.cullingMask = captureMask;
            cam.allowHDR = false;
            cam.targetTexture = rt;

            // bots would show up as specks on the picture: hide them for the one frame it takes
            List<Renderer> hidden = new List<Renderer>();
            for (int i = 0; i < Combatant.All.Count; i++)
            {
                Combatant c = Combatant.All[i]; if (c == null) continue;
                Renderer[] rs = c.GetComponentsInChildren<Renderer>();
                for (int r = 0; r < rs.Length; r++) if (rs[r] != null && rs[r].enabled) { rs[r].enabled = false; hidden.Add(rs[r]); }
            }
            try { cam.Render(); }
            catch (Exception e) { Debug.LogWarning("[BF3AI] DeployMenu map capture failed: " + e.Message); }
            for (int i = 0; i < hidden.Count; i++) if (hidden[i] != null) hidden[i].enabled = true;
            cam.targetTexture = null;
            Destroy(cg);
            mapRaw.texture = rt;
        }

        // ================================================================================================================ targets
        void RefreshTargets(bool force)
        {
            List<SpawnTarget> fresh = new List<SpawnTarget>();
            SquadManager sm = SquadManager.Instance;
            if (sm != null && sm.teams != null)
            {
                for (int t = 0; t < sm.teams.Count; t++)
                {
                    SquadManager.TeamConfig cfg = sm.teams[t];
                    if (cfg.spawnPoints == null) continue;
                    for (int i = 0; i < cfg.spawnPoints.Length; i++)
                    {
                        Transform sp = cfg.spawnPoints[i]; if (sp == null) continue;
                        fresh.Add(new SpawnTarget { position = sp.position, rotation = sp.rotation, label = sp.name, transform = sp, isEnemy = cfg.team != playerTeam });
                    }
                }
            }
            Vector3 near = oldPlayer != null ? oldPlayer.transform.position : Vector3.zero;
            List<AIUnit> mates = DeploySpawns.PickMates(playerTeam, near, 3);
            for (int i = 0; i < mates.Count; i++)
                fresh.Add(new SpawnTarget { position = mates[i].transform.position, rotation = mates[i].transform.rotation, label = DeployClassLabel(mates[i]) + " (squad)", isSquadmate = true, transform = mates[i].transform, unit = mates[i] });

            bool same = !force && fresh.Count == targets.Count;
            if (same) for (int i = 0; i < fresh.Count; i++) if (fresh[i].transform != targets[i].transform) { same = false; break; }
            if (same) return;
            targets.Clear(); targets.AddRange(fresh);
            RebuildMarkers();
            RestoreSelection();
            if (squadLabel != null) { int n = 0; for (int i = 0; i < targets.Count; i++) if (targets[i].isSquadmate) n++; squadLabel.text = "SQUAD:  " + (n + 1) + " / 4"; }
        }

        // keep the same spawn selected if it still exists; otherwise the first spawn point of the player's team
        void RestoreSelection()
        {
            selectedIndex = -1;
            if (selectedTransform != null)
                for (int i = 0; i < targets.Count; i++) if (targets[i].transform == selectedTransform && targets[i].Selectable) selectedIndex = i;
            if (selectedIndex < 0)
            {
                for (int i = 0; i < targets.Count; i++) if (targets[i].Selectable && !targets[i].isSquadmate) { selectedIndex = i; break; }
                if (selectedIndex < 0) for (int i = 0; i < targets.Count; i++) if (targets[i].Selectable) { selectedIndex = i; break; }
            }
            selectedTransform = selectedIndex >= 0 ? targets[selectedIndex].transform : null;
            UpdateSpawnLabel();
        }

        void UpdateSpawnLabel()
        {
            if (spawnLabel == null) return;
            spawnLabel.text = selectedIndex >= 0 ? targets[selectedIndex].label.ToUpperInvariant() : "NONE AVAILABLE";
        }

        void SelectTarget(int index)
        {
            if (index < 0 || index >= targets.Count) return;
            if (!targets[index].Selectable) { SetMessage("YOU CANNOT DEPLOY AT AN ENEMY SPAWN"); return; }
            SetMessage("");
            selectedIndex = index; selectedTransform = targets[index].transform;
            UpdateSpawnLabel();
        }

        /// <summary>The DEPLOY button.</summary>
        public void DeploySelected()
        {
            if (selectedIndex < 0 || selectedIndex >= targets.Count) { SetMessage("SELECT A SPAWN POINT OR SQUADMATE FIRST"); return; }
            Deploy(targets[selectedIndex]);
        }

        static string DeployClassLabel(AIUnit u)
        {
            switch (u.unitClass) { case UnitClass.Sniper: return "Specialist"; default: return u.unitClass.ToString(); }
        }

        void RebuildMarkers()
        {
            for (int i = 0; i < markers.Count; i++) if (markers[i] != null) Destroy(markers[i].gameObject);
            markers.Clear(); markerImages.Clear(); markerRings.Clear();
            for (int i = 0; i < targets.Count; i++)
            {
                SpawnTarget t = targets[i];
                float sz = t.isSquadmate ? 26f : 22f;
                RectTransform rt = NewRect("Marker" + i, mapRect, 0, 0, sz, sz);
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f); rt.pivot = new Vector2(0.5f, 0.5f);
                Image img = rt.gameObject.AddComponent<Image>();
                img.sprite = DeployStyle.DotSprite();
                img.color = t.isEnemy ? DeployStyle.Enemy : (t.isSquadmate ? DeployStyle.Squad : DeployStyle.Ally);
                RectTransform ringRt = NewRect("Ring", rt, 0, 0, sz + 10, sz + 10);
                ringRt.anchorMin = ringRt.anchorMax = new Vector2(0.5f, 0.5f); ringRt.pivot = new Vector2(0.5f, 0.5f); ringRt.anchoredPosition = Vector2.zero;
                Image ring = ringRt.gameObject.AddComponent<Image>();
                ring.sprite = DeployStyle.RingSprite(); ring.color = new Color(1, 1, 1, 0f); ring.raycastTarget = false;
                DeployMarker m = rt.gameObject.AddComponent<DeployMarker>();
                m.menu = this; m.index = i;
                markers.Add(m); markerImages.Add(img); markerRings.Add(ringRt);
            }
            UpdateMarkerPositions();
            tooltipRect.SetAsLastSibling();
        }

        void UpdateMarkerPositions()
        {
            if (mapRect == null) return;
            float w = mapRect.sizeDelta.x, h = mapRect.sizeDelta.y;
            for (int i = 0; i < targets.Count && i < markers.Count; i++)
            {
                SpawnTarget t = targets[i];
                if (t.isSquadmate && t.transform != null) t.position = t.transform.position;       // live position
                Vector2 uv = view.ToUV(t.position);
                RectTransform rt = (RectTransform)markers[i].transform;
                rt.anchoredPosition = new Vector2(Mathf.Clamp01(uv.x) * w, Mathf.Clamp01(uv.y) * h);
                bool hot = i == hoverIndex;
                rt.localScale = Vector3.one * (hot ? 1.3f : 1f);
                // the selected marker wears a pulsing white ring
                Image ringImg = markerRings[i].GetComponent<Image>();
                if (i == selectedIndex)
                {
                    float k = (Time.unscaledTime * 1.2f) % 1f;
                    markerRings[i].localScale = Vector3.one * Mathf.Lerp(0.8f, 1.5f, k);
                    ringImg.color = new Color(1f, 1f, 1f, 1f - k);
                }
                else ringImg.color = new Color(1f, 1f, 1f, 0f);
            }
            for (int p = pings.Count - 1; p >= 0; p--)
            {
                float k = (Time.unscaledTime - pingBorn[p]) / 0.6f;
                if (k >= 1f) { Destroy(pings[p].gameObject); pings.RemoveAt(p); pingBorn.RemoveAt(p); continue; }
                pings[p].localScale = Vector3.one * Mathf.Lerp(0.3f, 1.6f, k);
                pings[p].GetComponent<Image>().color = new Color(DeployStyle.Accent.r, DeployStyle.Accent.g, DeployStyle.Accent.b, 1f - k);
            }
            if (hoverIndex >= 0 && hoverIndex < markers.Count) PlaceTooltip(hoverIndex);
        }

        public void MarkerHover(int index, bool on)
        {
            if (index < 0 || index >= targets.Count) return;
            if (on) { hoverIndex = index; PlaceTooltip(index); tooltipRect.gameObject.SetActive(true); }
            else if (hoverIndex == index) { hoverIndex = -1; tooltipRect.gameObject.SetActive(false); }
        }

        void PlaceTooltip(int index)
        {
            SpawnTarget t = targets[index];
            string kind = t.isEnemy ? "ENEMY SPAWN" : (t.isSquadmate ? "SQUADMATE" : "SPAWN POINT");
            tooltipText.text = t.label.ToUpperInvariant() + "\n" + kind;
            tooltipRect.anchoredPosition = ((RectTransform)markers[index].transform).anchoredPosition + new Vector2(18f, 22f);
            tooltipRect.SetAsLastSibling();
        }

        public void MarkerClick(int index) { SelectTarget(index); }

        public void MapClick(Vector2 screenPos)
        {
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(mapRect, screenPos, null, out local)) return;
            Vector2 uv = new Vector2((local.x - mapRect.rect.xMin) / mapRect.rect.width, (local.y - mapRect.rect.yMin) / mapRect.rect.height);
            // a ripple where the player clicked
            RectTransform ping = NewRect("Ping", mapRect, 0, 0, 40, 40);
            ping.anchorMin = ping.anchorMax = Vector2.zero; ping.pivot = new Vector2(0.5f, 0.5f);
            ping.anchoredPosition = new Vector2(Mathf.Clamp01(uv.x) * mapRect.sizeDelta.x, Mathf.Clamp01(uv.y) * mapRect.sizeDelta.y);
            Image pi = ping.gameObject.AddComponent<Image>(); pi.sprite = DeployStyle.RingSprite(); pi.raycastTarget = false;
            pings.Add(ping); pingBorn.Add(Time.unscaledTime);
            int i = DeploySpawns.Nearest(targets, view.ToWorld(uv, 0f));
            if (i < 0) { SetMessage("NO SPAWN POINT AVAILABLE"); return; }
            SelectTarget(i);
        }

        // ============================================================================================================== deploy
        void SetMessage(string m) { if (messageText != null) messageText.text = m; }

        void Deploy(SpawnTarget t)
        {
            if (!open || deploying || t == null) return;
            deploying = true;
            try
            {
                Vector3 pos = t.position; Quaternion rot = Quaternion.Euler(0f, t.rotation.eulerAngles.y, 0f);
                if (t.isSquadmate && t.transform != null) pos = SafeNear(t.transform);

                PlayerLoadoutStore.lastClass = curClass;
                PlayerLoadoutStore.Set(lo);
                PlayerLoadoutStore.Save();

                SkinOption skin = CurrentSkin();
                GameObject player;
                if (skin != null && skin.prefab != null) player = SpawnFromPrefab(skin.prefab, pos, rot);
                else player = RepositionExisting(pos, rot);

                if (player == null) { SetMessage("COULD NOT SPAWN THE PLAYER (SEE CONSOLE)"); deploying = false; return; }
                Close();
                if (OnDeployed != null) OnDeployed(player);
            }
            catch (Exception e)
            {
                Debug.LogError("[BF3AI] DeployMenu deploy failed: " + e);
                SetMessage("DEPLOY FAILED (SEE CONSOLE)");
                deploying = false;
            }
        }

        static Vector3 SafeNear(Transform mate)
        {
            NavMeshHit h;
            Vector3 behind = mate.position - mate.forward * 2f;
            if (NavMesh.SamplePosition(behind, out h, 4f, NavMesh.AllAreas)) return h.position;
            if (NavMesh.SamplePosition(mate.position, out h, 6f, NavMesh.AllAreas)) return h.position;
            return mate.position;
        }

        GameObject SpawnFromPrefab(GameObject prefab, Vector3 pos, Quaternion rot)
        {
            GameObject p = Instantiate(prefab, pos, rot);
            p.SetActive(true);
            Combatant c = p.GetComponentInChildren<Combatant>(true);
            if (c != null) { c.team = playerTeam; c.isPlayer = true; }
            ApplyLoadout(p);
            CharacterController cc = p.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            p.transform.position = pos + Vector3.up * (c != null ? c.feetOffset : 0f);
            p.transform.rotation = rot;
            if (cc != null) cc.enabled = true;
            if (oldPlayer != null && oldPlayer != p) Destroy(oldPlayer);           // the dead player / the hidden scene player
            oldPlayer = null;
            return p;
        }

        // no player prefab for this class yet: bring the existing player back at the chosen spot (weapons are re-equipped)
        GameObject RepositionExisting(Vector3 pos, Quaternion rot)
        {
            if (oldPlayer == null) { Debug.LogError("[BF3AI] DeployMenu: there is no player in the scene and no prefab in Resources/" + DeployCatalog.charactersRoot + "/" + curClass + "/."); return null; }
            if (!oldPlayer.activeSelf) oldPlayer.SetActive(true);
            PlayerDeath pd = oldPlayer.GetComponent<PlayerDeath>();
            if (pd != null) pd.RespawnAt(pos, rot);
            else
            {
                CharacterController cc = oldPlayer.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false;
                oldPlayer.transform.position = pos; oldPlayer.transform.rotation = rot;
                if (cc != null) cc.enabled = true;
                Combatant c = oldPlayer.GetComponentInChildren<Combatant>(true); if (c != null) c.ResetHealth();
            }
            ApplyLoadout(oldPlayer);
            WeaponManager wm = oldPlayer.GetComponentInChildren<WeaponManager>(true);
            if (wm != null) wm.Reequip();
            GameObject p = oldPlayer; oldPlayer = null;
            return p;
        }

        void ApplyLoadout(GameObject player)
        {
            WeaponManager wm = player.GetComponentInChildren<WeaponManager>(true);
            if (wm == null) return;
            wm.selectedWeaponP = lo.primary; wm.selectedWeaponS = lo.secondary;
            wm.primaryAttachments = (string[])lo.primaryAtt.Clone(); wm.secondaryAttachments = (string[])lo.secondaryAtt.Clone();
        }

        // ================================================================================================================ selection
        SkinOption CurrentSkin()
        {
            for (int i = 0; i < skins.Count; i++) if (skins[i].name == lo.skin) return skins[i];
            return skins.Count > 0 ? skins[0] : null;
        }

        WeaponOption CurrentWeapon(int slot)
        {
            string path = slot == 0 ? lo.primary : lo.secondary;
            List<WeaponOption> l = weapons[slot];
            for (int i = 0; i < l.Count; i++) if (l[i].path == path) return l[i];
            return l.Count > 0 ? l[0] : null;
        }

        public void SelectClass(DeployClass c, bool save)
        {
            curClass = c;
            PlayerLoadoutStore.lastClass = c;
            lo = PlayerLoadoutStore.Get(c);
            skins = DeployCatalog.GetSkins(c);
            weapons[0] = DeployCatalog.GetWeapons(c, true);
            weapons[1] = DeployCatalog.GetWeapons(c, false);
            Reconcile();
            for (int i = 0; i < 4; i++) StyleClassButton(i, i == (int)c);
            RefreshSkin();
            RefreshWeapon(0); RefreshWeapon(1);
            if (save) { PlayerLoadoutStore.Set(lo); PlayerLoadoutStore.Save(); }
        }

        // saved choices that no longer exist (a prefab was removed or renamed) fall back to the first available one
        void Reconcile()
        {
            lo.cls = curClass;
            bool skinOk = false;
            for (int i = 0; i < skins.Count; i++) if (skins[i].name == lo.skin) skinOk = true;
            if (!skinOk) lo.skin = skins.Count > 0 ? skins[0].name : "";
            for (int s = 0; s < 2; s++)
            {
                string cur = s == 0 ? lo.primary : lo.secondary;
                bool ok = false;
                for (int i = 0; i < weapons[s].Count; i++) if (weapons[s][i].path == cur) ok = true;
                string pick = ok ? cur : (weapons[s].Count > 0 ? weapons[s][0].path : "");
                string[] att = s == 0 ? lo.primaryAtt : lo.secondaryAtt;
                if (!ok) for (int k = 0; k < 3; k++) att[k] = "";
                if (s == 0) lo.primary = pick; else lo.secondary = pick;
                WeaponOption w = CurrentWeapon(s);
                for (int k = 0; k < 3; k++)
                    if (!string.IsNullOrEmpty(att[k]) && (w == null || DeployCatalog.FindAttachment(w.prefab.name, (AttachmentSlot)k, att[k]) == null)) att[k] = "";
            }
        }

        void RefreshSkin()
        {
            SkinOption s = CurrentSkin();
            skinText.text = s != null ? s.name.ToUpperInvariant().Replace("_", " ") + "   v" : "NO SKINS FOUND";
            classTitle.text = curClass.ToString().ToUpperInvariant() + " CLASS";
            skinPath.text = s != null ? "Resources/" + s.path : "Resources/" + DeployCatalog.charactersRoot + "/" + curClass + "/  (no prefabs found)";
            charView.Show(s != null ? s.prefab : null, null);
        }

        void RefreshWeapon(int slot)
        {
            WeaponSlotUI ui = wslot[slot];
            WeaponOption w = CurrentWeapon(slot);
            string[] att = slot == 0 ? lo.primaryAtt : lo.secondaryAtt;
            if (w == null)
            {
                ui.name.text = "NO WEAPON FOUND";
                ui.stats.text = "Put weapon prefabs in\nResources/Weapons/" + curClass + "/" + (slot == 0 ? "Primary" : "Secondary");
                weaponView[slot].Clear();
                for (int k = 0; k < 3; k++) { ui.attLabel[k].text = "-"; ui.attIcon[k].color = new Color(0, 0, 0, 0); SetAttachmentStyle(ui, k, false); }
                return;
            }
            List<WeaponAttachment> chosen = new List<WeaponAttachment>();
            List<GameObject> chosenPrefabs = new List<GameObject>();
            for (int k = 0; k < 3; k++)
            {
                AttachmentOption a = DeployCatalog.FindAttachment(w.prefab.name, (AttachmentSlot)k, att[k]);
                ui.attLabel[k].text = a != null ? a.name.ToUpperInvariant() : "EMPTY";
                ui.attIcon[k].sprite = a != null ? a.data.icon : null;
                ui.attIcon[k].color = a != null && a.data.icon != null ? Color.white : new Color(0, 0, 0, 0);
                SetAttachmentStyle(ui, k, a != null);
                if (a != null) { chosen.Add(a.data); chosenPrefabs.Add(a.prefab); }
            }
            ui.name.text = w.name.ToUpperInvariant();
            ui.stats.text = WeaponStats.Of(w.blaster, chosen).ToString().Replace("   ", "\n");
            weaponView[slot].Show(w.prefab, chosenPrefabs);
        }

        static void SetAttachmentStyle(WeaponSlotUI ui, int k, bool active)
        {
            ui.attFill[k].color = active ? new Color(0.247f, 0.784f, 1f, 0.16f) : new Color(0.03f, 0.055f, 0.08f, 0.8f);
            ui.attFrame[k].color = active ? DeployStyle.Accent : DeployStyle.Line;
        }

        // ================================================================================================================ pickers
        void PickSkin()
        {
            List<PickItem> items = new List<PickItem>();
            for (int i = 0; i < skins.Count; i++)
            {
                SkinOption s = skins[i];
                items.Add(new PickItem { label = s.name, sub = "", selected = s.name == lo.skin, onPick = delegate { lo.skin = s.name; PlayerLoadoutStore.Set(lo); PlayerLoadoutStore.Save(); RefreshSkin(); } });
            }
            if (items.Count == 0) { SetMessage("NO SKINS: PUT PLAYER PREFABS IN RESOURCES/" + DeployCatalog.charactersRoot.ToUpperInvariant() + "/" + curClass.ToString().ToUpperInvariant() + "/"); return; }
            OpenPicker("SELECT SKIN", items);
        }

        void PickWeapon(int slot)
        {
            List<PickItem> items = new List<PickItem>();
            for (int i = 0; i < weapons[slot].Count; i++)
            {
                WeaponOption w = weapons[slot][i];
                string cur = slot == 0 ? lo.primary : lo.secondary;
                items.Add(new PickItem
                {
                    label = w.name,
                    sub = WeaponStats.Of(w.blaster, null).ToString(),
                    selected = w.path == cur,
                    onPick = delegate
                    {
                        if (slot == 0) { lo.primary = w.path; lo.primaryAtt = new string[3] { "", "", "" }; } else { lo.secondary = w.path; lo.secondaryAtt = new string[3] { "", "", "" }; }
                        PlayerLoadoutStore.Set(lo); PlayerLoadoutStore.Save(); RefreshWeapon(slot);
                    }
                });
            }
            if (items.Count == 0) { SetMessage("NO WEAPONS: PUT WEAPON PREFABS IN RESOURCES/" + DeployCatalog.weaponsRoot.ToUpperInvariant() + "/" + curClass.ToString().ToUpperInvariant() + "/" + (slot == 0 ? "PRIMARY" : "SECONDARY") + "/"); return; }
            OpenPicker(slot == 0 ? "SELECT PRIMARY WEAPON" : "SELECT SECONDARY WEAPON", items);
        }

        void PickAttachment(int slot, int attSlot)
        {
            WeaponOption w = CurrentWeapon(slot);
            if (w == null) return;
            string[] att = slot == 0 ? lo.primaryAtt : lo.secondaryAtt;
            List<AttachmentOption> opts = DeployCatalog.GetAttachments(w.prefab.name, (AttachmentSlot)attSlot);
            List<PickItem> items = new List<PickItem>();
            items.Add(new PickItem { label = "NONE", sub = "No " + ((AttachmentSlot)attSlot).ToString().ToLowerInvariant() + " attachment", selected = string.IsNullOrEmpty(att[attSlot]), onPick = delegate { att[attSlot] = ""; PlayerLoadoutStore.Save(); RefreshWeapon(slot); } });
            for (int i = 0; i < opts.Count; i++)
            {
                AttachmentOption o = opts[i];
                items.Add(new PickItem
                {
                    label = o.name, icon = o.data.icon, sub = string.IsNullOrEmpty(o.data.description) ? AttachmentSummary(o.data) : o.data.description,
                    selected = att[attSlot] == o.path,
                    onPick = delegate { att[attSlot] = o.path; PlayerLoadoutStore.Save(); RefreshWeapon(slot); }
                });
            }
            OpenPicker(((AttachmentSlot)attSlot).ToString().ToUpperInvariant() + " ATTACHMENT  -  " + w.name.ToUpperInvariant(), items);
        }

        static string AttachmentSummary(WeaponAttachment a)
        {
            List<string> p = new List<string>();
            if (a.damageMult != 1f) p.Add("DMG " + Pct(a.damageMult));
            if (a.fireRateMult != 1f) p.Add("RATE " + Pct(a.fireRateMult));
            if (a.spreadMult != 1f) p.Add("SPREAD " + Pct(a.spreadMult));
            if (a.magazineMult != 1f) p.Add("MAG " + Pct(a.magazineMult));
            if (a.reloadTimeMult != 1f) p.Add("RELOAD " + Pct(a.reloadTimeMult));
            if (a.zoomAdd != 0f) p.Add("ZOOM " + (a.zoomAdd > 0 ? "+" : "") + a.zoomAdd.ToString("0.#") + "x");
            return p.Count > 0 ? string.Join("   ", p.ToArray()) : "Cosmetic";
        }
        static string Pct(float m) { int v = Mathf.RoundToInt((m - 1f) * 100f); return (v > 0 ? "+" : "") + v + "%"; }

        void OpenPicker(string title, List<PickItem> items)
        {
            pickItems = items; pickPage = 0;
            for (int i = 0; i < items.Count; i++) if (items[i].selected) { pickPage = i / PickRowsPerPage; break; }
            pickerTitle.text = title;
            pickerRoot.SetActive(true);
            pickerRoot.transform.SetAsLastSibling();
            RenderPickerPage();
        }

        void ClosePicker() { if (pickerRoot != null) pickerRoot.SetActive(false); }

        void RenderPickerPage()
        {
            int pages = Mathf.Max(1, Mathf.CeilToInt(pickItems.Count / (float)PickRowsPerPage));
            pickPage = Mathf.Clamp(pickPage, 0, pages - 1);
            for (int r = 0; r < PickRowsPerPage; r++)
            {
                int idx = pickPage * PickRowsPerPage + r;
                Button b = pickerRows[r];
                b.onClick.RemoveAllListeners();
                if (idx >= pickItems.Count) { b.gameObject.SetActive(false); continue; }
                PickItem it = pickItems[idx];
                b.gameObject.SetActive(true);
                pickerRowLabel[r].text = it.label.ToUpperInvariant();
                pickerRowSub[r].text = it.sub ?? "";
                pickerRowIcon[r].sprite = it.icon;
                pickerRowIcon[r].color = it.icon != null ? Color.white : new Color(0, 0, 0, 0);
                b.GetComponent<Image>().color = it.selected ? new Color(0.12f, 0.36f, 0.5f, 0.95f) : new Color(0.08f, 0.12f, 0.17f, 0.9f);
                b.onClick.AddListener(delegate { ClosePicker(); it.onPick(); });
            }
            pickerPageText.text = pages > 1 ? ("PAGE " + (pickPage + 1) + " / " + pages) : "";
        }

        // ============================================================================================================== class buttons
        void StyleClassButton(int i, bool selected)
        {
            if (classFill[i] == null) return;
            classFill[i].color = selected ? new Color(0.12f, 0.19f, 0.27f, 0.95f) : DeployStyle.Panel;
            classFrame[i].color = selected ? Color.white : DeployStyle.Line;
            classBar[i].gameObject.SetActive(selected);
            classLabel[i].color = selected ? Color.white : new Color(0.8f, 0.86f, 0.9f, 1f);
        }

        // ============================================================================================================== building the ui
        void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            GameObject es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            es.AddComponent<InputSystemUIInputModule>();
#else
            es.AddComponent<StandaloneInputModule>();
#endif
        }

        void UpdateScale()
        {
            if (rootRect == null) return;
            float s = Mathf.Min(Screen.width / 1920f, Screen.height / 1080f);
            if (s <= 0.001f) s = 1f;
            rootRect.localScale = new Vector3(s, s, 1f);
        }

        RectTransform NewRect(string name, Transform parent, float x, float y, float w, float h)
        {
            GameObject g = new GameObject(name);
            RectTransform rt = g.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        Image Box(Transform parent, string name, float x, float y, float w, float h, Color fill, bool frame, Color frameColor)
        {
            RectTransform rt = NewRect(name, parent, x, y, w, h);
            Image img = rt.gameObject.AddComponent<Image>();
            img.sprite = DeployStyle.PanelSprite(); img.type = Image.Type.Sliced; img.color = fill;
            if (frame) Frame(rt, w, h, frameColor);
            return img;
        }

        Image Frame(RectTransform parent, float w, float h, Color color)
        {
            RectTransform fr = NewRect("Frame", parent, 0, 0, w, h);
            Image fi = fr.gameObject.AddComponent<Image>();
            fi.sprite = DeployStyle.FrameSprite(); fi.type = Image.Type.Sliced; fi.color = color; fi.raycastTarget = false;
            return fi;
        }

        Text Label(Transform parent, string text, int size, TextAnchor anchor, Color col, float x, float y, float w, float h, FontStyle style)
        {
            RectTransform rt = NewRect("Text", parent, x, y, w, h);
            Text t = rt.gameObject.AddComponent<Text>();
            t.font = DeployStyle.GetFont(); t.fontSize = size; t.alignment = anchor; t.color = col; t.text = text; t.fontStyle = style;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow; t.raycastTarget = false;
            return t;
        }

        Button MakeButton(Transform parent, string name, float x, float y, float w, float h, Color fill, Color frameColor, Action onClick)
        {
            Image img = Box(parent, name, x, y, w, h, fill, true, frameColor);
            Button b = img.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            ColorBlock cb = b.colors;
            cb.normalColor = Color.white; cb.highlightedColor = new Color(1.3f, 1.3f, 1.3f, 1f); cb.pressedColor = new Color(0.8f, 0.9f, 1f, 1f); cb.disabledColor = new Color(0.6f, 0.6f, 0.6f, 1f);
            cb.colorMultiplier = 1f; cb.fadeDuration = 0.06f;
            b.colors = cb;
            if (onClick != null) b.onClick.AddListener(delegate { onClick(); });
            return b;
        }

        void Build()
        {
            built = true;
            GameObject cg = new GameObject("BF3AI_DeployCanvas");
            cg.transform.SetParent(transform, false);
            canvas = cg.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;
            CanvasScaler cs = cg.AddComponent<CanvasScaler>();
            cs.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            cg.AddComponent<GraphicRaycaster>();

            // full-screen backdrop
            GameObject bg = new GameObject("Backdrop");
            RectTransform bgRt = bg.AddComponent<RectTransform>();
            bgRt.SetParent(cg.transform, false);
            bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one; bgRt.offsetMin = Vector2.zero; bgRt.offsetMax = Vector2.zero;
            Image bgImg = bg.AddComponent<Image>(); bgImg.color = DeployStyle.Bg;

            // 1920 x 1080 content, scaled to fit the screen
            GameObject root = new GameObject("Root");
            rootRect = root.AddComponent<RectTransform>();
            rootRect.SetParent(bgRt, false);
            rootRect.anchorMin = rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.sizeDelta = new Vector2(1920f, 1080f);
            rootRect.anchoredPosition = Vector2.zero;

            BuildHeaderFooter();
            BuildMap();
            BuildCharacter();
            BuildClasses();
            BuildLoadout();
            BuildPicker();

            // 3D views (the character and the two weapons)
            charView = new DeployPreviewView("Character", new Vector3(0f, 5000f, 0f), 1024, 640, 14f, true);
            weaponView[0] = new DeployPreviewView("Primary", new Vector3(200f, 5000f, 0f), 600, 260, 28f, false);
            weaponView[1] = new DeployPreviewView("Secondary", new Vector3(400f, 5000f, 0f), 600, 260, 28f, false);
            charView.SetActive(false); weaponView[0].SetActive(false); weaponView[1].SetActive(false);
            wslot[0].view.texture = weaponView[0].texture; wslot[1].view.texture = weaponView[1].texture;
            charRaw.texture = charView.texture;

            cg.SetActive(false);
        }

        RawImage charRaw;

        // ------------------------------------------------------------------------------------------------------------ header and footer
        void BuildHeaderFooter()
        {
            Box(rootRect, "HeaderBar", 0, 0, 1920, 48, new Color(0.02f, 0.03f, 0.05f, 0.7f), false, Color.white);
            Box(rootRect, "HeaderLine", 0, 47, 1920, 1, DeployStyle.Line, false, Color.white);
            Label(rootRect, "DEPLOYMENT CONTROL", 22, TextAnchor.MiddleLeft, DeployStyle.Text, 24, 0, 420, 48, FontStyle.Bold);
            Image badge = Box(rootRect, "Badge", 300, 13, 190, 22, new Color(0, 0, 0, 0), true, DeployStyle.Accent);
            Label(badge.transform, "CHOOSE LOADOUT", 12, TextAnchor.MiddleCenter, DeployStyle.Accent, 0, 0, 190, 22, FontStyle.Bold);
            squadLabel = Label(rootRect, "SQUAD:  1 / 4", 16, TextAnchor.MiddleRight, DeployStyle.TextDim, 1400, 0, 496, 48, FontStyle.Bold);

            Box(rootRect, "FooterBar", 0, 1024, 1920, 56, new Color(0.02f, 0.03f, 0.05f, 0.85f), false, Color.white);
            Box(rootRect, "FooterLine", 0, 1024, 1920, 1, DeployStyle.Line, false, Color.white);
            Label(rootRect, "SELECTED SPAWN:", 16, TextAnchor.MiddleLeft, DeployStyle.TextDim, 24, 1024, 200, 56, FontStyle.Bold);
            spawnLabel = Label(rootRect, "", 20, TextAnchor.MiddleLeft, DeployStyle.Accent, 228, 1024, 560, 56, FontStyle.Bold);
            messageText = Label(rootRect, "", 18, TextAnchor.MiddleCenter, new Color(1f, 0.65f, 0.25f, 1f), 780, 1024, 520, 56, FontStyle.Bold);
            Button deploy = MakeButton(rootRect, "DeployButton", 1696, 1032, 200, 40, Color.white, Color.white, DeploySelected);
            ColorBlock cb = deploy.colors; cb.highlightedColor = DeployStyle.Accent; deploy.colors = cb;
            Label(deploy.transform, "DEPLOY", 22, TextAnchor.MiddleCenter, Color.black, 0, 0, 200, 40, FontStyle.Bold);
        }

        // ------------------------------------------------------------------------------------------------------------ top left: map
        void BuildMap()
        {
            const float x = 24, y = 64, w = 974, h = 464;
            Box(rootRect, "MapPanel", x, y, w, h, new Color(0.03f, 0.06f, 0.09f, 1f), false, Color.white);
            mapRect = NewRect("Map", rootRect, x, y, w, h);
            mapRaw = mapRect.gameObject.AddComponent<RawImage>();
            mapRaw.color = new Color(0.85f, 0.93f, 1f, 1f);
            DeployMapClick mc = mapRect.gameObject.AddComponent<DeployMapClick>(); mc.menu = this;

            // map name and hint over the picture
            string title = string.IsNullOrEmpty(mapName) ? UnityEngine.SceneManagement.SceneManager.GetActiveScene().name : mapName;
            Image nameBox = Box(rootRect, "MapName", x + 12, y + 10, 360, 32, new Color(0.03f, 0.05f, 0.08f, 0.8f), true, DeployStyle.LineBright);
            mapTitle = Label(nameBox.transform, "MAP:  " + title.ToUpperInvariant(), 14, TextAnchor.MiddleLeft, DeployStyle.Text, 14, 0, 340, 32, FontStyle.Bold);
            Image hintBox = Box(rootRect, "MapHint", x + w - 12 - 400, y + 14, 400, 24, new Color(0, 0, 0, 0.6f), false, Color.white);
            hintText = Label(hintBox.transform, "CLICK ANYWHERE TO TARGET THE CLOSEST SPAWN", 11, TextAnchor.MiddleCenter, DeployStyle.TextDim, 0, 0, 400, 24, FontStyle.Normal);

            // legend
            Legend(x + 14, y + h - 84, DeployStyle.Squad, "SQUADMATES");
            Legend(x + 14, y + h - 60, DeployStyle.Ally, "YOUR SPAWN POINTS");
            Legend(x + 14, y + h - 36, DeployStyle.Enemy, "ENEMY SPAWN POINTS");

            RectTransform fr = NewRect("MapFrame", rootRect, x, y, w, h);
            Image fi = fr.gameObject.AddComponent<Image>(); fi.sprite = DeployStyle.FrameSprite(); fi.type = Image.Type.Sliced; fi.color = DeployStyle.Line; fi.raycastTarget = false;

            RectTransform tt = NewRect("Tooltip", mapRect, 0, 0, 230, 46);
            tt.anchorMin = tt.anchorMax = Vector2.zero; tt.pivot = new Vector2(0f, 0f);
            Image tb = tt.gameObject.AddComponent<Image>(); tb.sprite = DeployStyle.PanelSprite(); tb.type = Image.Type.Sliced; tb.color = new Color(0.03f, 0.05f, 0.08f, 0.95f); tb.raycastTarget = false;
            Frame(tt, 230, 46, Color.white);
            tooltipText = Label(tt, "", 13, TextAnchor.MiddleLeft, DeployStyle.Text, 10, 0, 214, 46, FontStyle.Bold);
            tooltipRect = tt;
            tt.gameObject.SetActive(false);
        }

        void Legend(float x, float y, Color c, string text)
        {
            RectTransform d = NewRect("LegendDot", rootRect, x, y + 5, 12, 12);
            Image i = d.gameObject.AddComponent<Image>(); i.sprite = DeployStyle.DotSprite(); i.color = c; i.raycastTarget = false;
            Label(rootRect, text, 12, TextAnchor.MiddleLeft, DeployStyle.TextDim, x + 20, y, 220, 22, FontStyle.Normal);
        }

        // ------------------------------------------------------------------------------------------------------------ top right: character
        void BuildCharacter()
        {
            const float x = 1014, y = 64, w = 882, h = 464;
            Box(rootRect, "CharPanel", x, y, w, h, new Color(0.03f, 0.06f, 0.09f, 0.9f), true, DeployStyle.Line);
            RectTransform cr = NewRect("CharView", rootRect, x + 1, y + 1, w - 2, h - 2);
            charRaw = cr.gameObject.AddComponent<RawImage>(); charRaw.color = Color.white; charRaw.raycastTarget = false;

            // skin selector (top right of the viewport)
            Box(rootRect, "SkinBox", x + w - 12 - 420, y + 12, 420, 44, new Color(0.03f, 0.055f, 0.08f, 0.88f), true, DeployStyle.Line);
            Label(rootRect, "SKIN PREFAB:", 12, TextAnchor.MiddleLeft, DeployStyle.TextDim, x + w - 12 - 410, y + 12, 110, 44, FontStyle.Bold);
            Button sb = MakeButton(rootRect, "SkinButton", x + w - 12 - 296, y + 18, 284, 32, new Color(0.05f, 0.08f, 0.12f, 1f), DeployStyle.LineBright, PickSkin);
            skinText = Label(sb.transform, "", 13, TextAnchor.MiddleCenter, Color.white, 0, 0, 284, 32, FontStyle.Bold);

            classTitle = Label(rootRect, "", 22, TextAnchor.MiddleCenter, Color.white, x, y + h - 62, w, 30, FontStyle.Bold);
            skinPath = Label(rootRect, "", 11, TextAnchor.MiddleCenter, DeployStyle.Accent, x, y + h - 32, w, 20, FontStyle.Normal);
        }

        // ------------------------------------------------------------------------------------------------------------ bottom left: classes and heroes
        void BuildClasses()
        {
            const float x = 24, w = 974;
            Label(rootRect, "CLASS", 13, TextAnchor.MiddleLeft, DeployStyle.TextDim, x, 546, 300, 22, FontStyle.Bold);
            string[] names = { "ASSAULT", "HEAVY", "SPECIALIST", "OFFICER" };
            DeployClass[] cls = { DeployClass.Assault, DeployClass.Heavy, DeployClass.Specialist, DeployClass.Officer };
            float cw = (w - 3 * 8) / 4f;
            for (int i = 0; i < 4; i++)
            {
                DeployClass c = cls[i];
                Button b = MakeButton(rootRect, "Class" + names[i], x + i * (cw + 8), 572, cw, 128, DeployStyle.Panel, DeployStyle.Line, delegate { SelectClass(c, true); });
                classFill[i] = b.GetComponent<Image>();
                classFrame[i] = b.transform.Find("Frame").GetComponent<Image>();
                classLabel[i] = Label(b.transform, names[i], 28, TextAnchor.MiddleCenter, DeployStyle.Text, 0, 30, cw, 44, FontStyle.Bold);
                Label(b.transform, DeployClassInfo.Blurb(c), 12, TextAnchor.MiddleCenter, DeployStyle.TextDim, 0, 78, cw, 22, FontStyle.Normal);
                RectTransform bar = NewRect("Bar", b.transform, 0, 124, cw, 4);
                classBar[i] = bar.gameObject.AddComponent<Image>(); classBar[i].color = Color.white; classBar[i].raycastTarget = false;
            }

            Label(rootRect, "HEROES", 13, TextAnchor.MiddleLeft, DeployStyle.TextDim, x, 714, 300, 22, FontStyle.Bold);
            string[] rows = { "TIER 2 REINFORCEMENTS (COMMANDOS / DROIDEKAS)", "BLASTER HEROES (HAN SOLO, BOBA FETT, FINN)", "FORCE / SABER HEROES (LUKE, REVAN, MAUL)" };
            string[] bp = { "1000 BP", "2000 BP", "4000 BP" };
            for (int r = 0; r < 3; r++)
            {
                float y = 740 + r * 80;
                Image left = Box(rootRect, "HeroCostL" + r, x, y, 110, 72, new Color(0.04f, 0.06f, 0.09f, 0.5f), true, new Color(1, 1, 1, 0.10f));
                Label(left.transform, bp[r], 13, TextAnchor.MiddleCenter, DeployStyle.Disabled, 0, 0, 110, 72, FontStyle.Bold);
                Image mid = Box(rootRect, "HeroRow" + r, x + 118, y, w - 236, 72, new Color(0.05f, 0.08f, 0.11f, 0.3f), true, new Color(1, 1, 1, 0.12f));
                Label(mid.transform, rows[r] + "   -   LOCKED", 13, TextAnchor.MiddleCenter, DeployStyle.Disabled, 0, 0, w - 236, 72, FontStyle.Normal);
                Image right = Box(rootRect, "HeroCostR" + r, x + w - 110, y, 110, 72, new Color(0.04f, 0.06f, 0.09f, 0.5f), true, new Color(1, 1, 1, 0.10f));
                Label(right.transform, bp[r], 13, TextAnchor.MiddleCenter, DeployStyle.Disabled, 0, 0, 110, 72, FontStyle.Bold);
            }
        }

        // ------------------------------------------------------------------------------------------------------------ bottom right: cards and weapons
        void BuildLoadout()
        {
            const float x = 1014, w = 882;
            for (int i = 0; i < 3; i++)
            {
                Image tile = Box(rootRect, "Card" + i, x + i * 297, 544, 288, 64, DeployStyle.Panel, true, DeployStyle.Line);
                Label(tile.transform, "CARD " + (i + 1), 14, TextAnchor.MiddleCenter, new Color(0.7f, 0.76f, 0.82f, 1f), 0, 8, 288, 24, FontStyle.Bold);
                Label(tile.transform, "TO BE IMPLEMENTED", 10, TextAnchor.MiddleCenter, DeployStyle.TextDim, 0, 34, 288, 20, FontStyle.Normal);
            }
            BuildWeaponSlot(0, 620, "PRIMARY WEAPON", "3 MOD SLOTS");
            BuildWeaponSlot(1, 818, "SIDEARM WEAPON", "SIDEARM MODS");
        }

        void BuildWeaponSlot(int slot, float y, string title, string modsLabel)
        {
            const float x = 1014, w = 882, h = 190;
            WeaponSlotUI ui = new WeaponSlotUI();
            wslot[slot] = ui;
            Box(rootRect, "WeaponPanel" + slot, x, y, w, h, DeployStyle.Panel, true, DeployStyle.Line);
            Label(rootRect, title, 13, TextAnchor.MiddleLeft, DeployStyle.TextDim, x + 14, y + 6, 400, 24, FontStyle.Bold);
            Label(rootRect, modsLabel, 11, TextAnchor.MiddleRight, DeployStyle.TextDim, x + w - 14 - 300, y + 6, 300, 24, FontStyle.Normal);

            // weapon selector: 3D picture, name, stats (click to change the weapon)
            int s = slot;
            Button sel = MakeButton(rootRect, "WeaponSelect" + slot, x + 12, y + 34, 498, 146, new Color(0.03f, 0.055f, 0.08f, 0.85f), DeployStyle.LineBright, delegate { PickWeapon(s); });
            RectTransform vr = NewRect("WeaponView", sel.transform, 8, 8, 300, 130);
            ui.view = vr.gameObject.AddComponent<RawImage>(); ui.view.color = Color.white; ui.view.raycastTarget = false;
            ui.name = Label(sel.transform, "", 20, TextAnchor.UpperLeft, Color.white, 318, 12, 172, 54, FontStyle.Bold);
            ui.stats = Label(sel.transform, "", 11, TextAnchor.UpperLeft, DeployStyle.TextDim, 318, 62, 172, 62, FontStyle.Normal);
            Label(sel.transform, "CHANGE WEAPON  >", 11, TextAnchor.LowerLeft, DeployStyle.Accent, 318, 118, 172, 22, FontStyle.Bold);

            // three attachment slots
            for (int k = 0; k < 3; k++)
            {
                int ak = k;
                Button ab = MakeButton(rootRect, "Att" + slot + "_" + k, x + 520 + k * 118, y + 34, 110, 146, new Color(0.03f, 0.055f, 0.08f, 0.8f), DeployStyle.Line, delegate { PickAttachment(s, ak); });
                ui.attFill[k] = ab.GetComponent<Image>();
                ui.attFrame[k] = ab.transform.Find("Frame").GetComponent<Image>();
                Label(ab.transform, ((AttachmentSlot)k).ToString().ToUpperInvariant(), 10, TextAnchor.MiddleCenter, DeployStyle.TextDim, 0, 6, 110, 18, FontStyle.Bold);
                RectTransform ir = NewRect("Icon", ab.transform, 25, 28, 60, 60);
                ui.attIcon[k] = ir.gameObject.AddComponent<Image>(); ui.attIcon[k].raycastTarget = false; ui.attIcon[k].preserveAspect = true; ui.attIcon[k].color = new Color(0, 0, 0, 0);
                ui.attLabel[k] = Label(ab.transform, "EMPTY", 11, TextAnchor.MiddleCenter, Color.white, 4, 96, 102, 44, FontStyle.Bold);
            }
        }

        // ------------------------------------------------------------------------------------------------------------ the pick list popup
        void BuildPicker()
        {
            pickerRoot = new GameObject("Picker");
            RectTransform pr = pickerRoot.AddComponent<RectTransform>();
            pr.SetParent(rootRect, false);
            pr.anchorMin = Vector2.zero; pr.anchorMax = Vector2.one; pr.offsetMin = Vector2.zero; pr.offsetMax = Vector2.zero;
            Image dim = pickerRoot.AddComponent<Image>(); dim.color = new Color(0f, 0f, 0f, 0.8f);
            Button dimBtn = pickerRoot.AddComponent<Button>(); dimBtn.targetGraphic = dim; dimBtn.onClick.AddListener(delegate { ClosePicker(); });

            float pw = 640, ph = 780, px = (1920 - pw) * 0.5f, py = (1080 - ph) * 0.5f;
            Box(pr, "PickerPanel", px, py, pw, ph, new Color(0.04f, 0.07f, 0.1f, 1f), true, Color.white);
            pickerTitle = Label(pr, "", 18, TextAnchor.MiddleLeft, Color.white, px + 24, py + 6, pw - 48, 44, FontStyle.Bold);
            for (int r = 0; r < PickRowsPerPage; r++)
            {
                Button b = MakeButton(pr, "Row" + r, px + 20, py + 58 + r * 88, pw - 40, 80, new Color(0.08f, 0.12f, 0.17f, 0.9f), DeployStyle.Line, null);
                RectTransform ir = NewRect("Icon", b.transform, 12, 10, 60, 60);
                Image icon = ir.gameObject.AddComponent<Image>(); icon.raycastTarget = false; icon.preserveAspect = true; icon.color = new Color(0, 0, 0, 0);
                Text l = Label(b.transform, "", 20, TextAnchor.MiddleLeft, Color.white, 84, 6, pw - 144, 34, FontStyle.Bold);
                Text sub = Label(b.transform, "", 12, TextAnchor.MiddleLeft, DeployStyle.TextDim, 84, 42, pw - 144, 32, FontStyle.Normal);
                pickerRows.Add(b); pickerRowLabel.Add(l); pickerRowSub.Add(sub); pickerRowIcon.Add(icon);
            }
            Button prev = MakeButton(pr, "Prev", px + 20, py + ph - 64, 80, 44, DeployStyle.Panel, DeployStyle.LineBright, delegate { pickPage--; RenderPickerPage(); });
            Label(prev.transform, "<", 22, TextAnchor.MiddleCenter, Color.white, 0, 0, 80, 44, FontStyle.Bold);
            Button next = MakeButton(pr, "Next", px + 108, py + ph - 64, 80, 44, DeployStyle.Panel, DeployStyle.LineBright, delegate { pickPage++; RenderPickerPage(); });
            Label(next.transform, ">", 22, TextAnchor.MiddleCenter, Color.white, 0, 0, 80, 44, FontStyle.Bold);
            pickerPageText = Label(pr, "", 13, TextAnchor.MiddleLeft, DeployStyle.TextDim, px + 204, py + ph - 64, 200, 44, FontStyle.Normal);
            Button close = MakeButton(pr, "Close", px + pw - 140, py + ph - 64, 120, 44, DeployStyle.Panel, DeployStyle.LineBright, delegate { ClosePicker(); });
            Label(close.transform, "CANCEL", 14, TextAnchor.MiddleCenter, Color.white, 0, 0, 120, 44, FontStyle.Bold);
            pickerRoot.SetActive(false);
        }

        void DisposeUI()
        {
            if (charView != null) charView.Dispose();
            for (int i = 0; i < 2; i++) if (weaponView[i] != null) weaponView[i].Dispose();
        }
    }
}
