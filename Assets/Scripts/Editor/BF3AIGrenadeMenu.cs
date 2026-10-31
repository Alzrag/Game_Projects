#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using BF3AI;

/// <summary>
/// Adds the menu item  BF3AI > Create Grenade Prefab.
/// This file MUST live in a folder named "Editor" (Assets/Scripts/BF3AI/Editor/).
/// It builds a ready-made grenade prefab (plus its two materials) under Assets/BF3AI and plugs it into any empty Grenade Prefab slot
/// on the SquadManager in the open scene.
/// </summary>
public static class BF3AIGrenadeMenu
{
    const string Root = "Assets/BF3AI";

    [MenuItem("BF3AI/Create Grenade Prefab")]
    static void CreateGrenadePrefab()
    {
        EnsureFolder(Root);
        EnsureFolder(Root + "/Prefabs");
        EnsureFolder(Root + "/Materials");

        Material body = MakeMaterial(Root + "/Materials/GrenadeBody.mat", PickLitShader(), new Color(0.2f, 0.25f, 0.2f));
        Material trail = MakeMaterial(Root + "/Materials/GrenadeTrail.mat", Shader.Find("Sprites/Default"), Color.white);

        Material glow = MakeMaterial(Root + "/Materials/GrenadeGlow.mat", Shader.Find("Sprites/Default"), new Color(1f, 0.35f, 0.1f));

        GameObject temp = GrenadeProjectile.BuildDefault(body, trail, glow);
        string path = AssetDatabase.GenerateUniqueAssetPath(Root + "/Prefabs/Grenade.prefab");
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(temp, path);
        Object.DestroyImmediate(temp);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        int assigned = AssignToSquadManager(prefab);
        Selection.activeObject = prefab;
        EditorGUIUtility.PingObject(prefab);
        Debug.Log("[BF3AI] Grenade prefab created at " + path + ". " + (assigned > 0
            ? "Assigned it to " + assigned + " empty Grenade Prefab slot(s) on the SquadManager."
            : "Drag it into the Grenade Prefab slot of each team on your SquadManager (or leave the slot empty to use a generated grenade)."));
    }

    static int AssignToSquadManager(GameObject prefab)
    {
#if UNITY_2023_1_OR_NEWER
        SquadManager sm = Object.FindFirstObjectByType<SquadManager>();
#else
        SquadManager sm = Object.FindObjectOfType<SquadManager>();
#endif
        if (sm == null || sm.teams == null) return 0;
        int n = 0;
        for (int i = 0; i < sm.teams.Count; i++)
        {
            if (sm.teams[i] != null && sm.teams[i].grenadePrefab == null) { sm.teams[i].grenadePrefab = prefab; n++; }
        }
        if (n > 0) EditorUtility.SetDirty(sm);
        return n;
    }

    static Shader PickLitShader()
    {
        Shader s = Shader.Find("Universal Render Pipeline/Lit");
        if (s == null) s = Shader.Find("HDRP/Lit");
        if (s == null) s = Shader.Find("Standard");
        return s;
    }

    static Material MakeMaterial(string path, Shader shader, Color color)
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;
        Material m = new Material(shader);
        m.color = color;
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }
}
#endif
