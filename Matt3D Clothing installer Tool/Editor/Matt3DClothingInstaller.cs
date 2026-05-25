using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class Matt3DClothingInstallerAutoOpen
{
    static Matt3DClothingInstallerAutoOpen()
    {
        if (!SessionState.GetBool("Matt3D_CI_Opened", false))
        {
            SessionState.SetBool("Matt3D_CI_Opened", true);
            EditorApplication.delayCall += Matt3DClothingInstaller.OpenWindow;
        }
    }
}

public class Matt3DClothingInstaller : EditorWindow
{
    // =========================================================================
    //  SETTINGS
    // =========================================================================

    const string DISCORD_URL  = "https://discord.com/invite/WqAZMhev74";
    const string SHOP_URL     = "https://www.matt3d.net/";
    const string LOGO_PATH    = "Matt3D_Logo";
    const string VERSIONS_URL = "https://api.github.com/repos/Matt2707/matt3d-versions/contents/versions.json";

    // =========================================================================
    //  DEPENDENCY LIST
    // =========================================================================

    struct Dependency
    {
        public string   Label;
        public string   URL;
        public string[] VersionAPIs;
        public Func<bool>   IsInstalled;
        public Func<string> GetLocalVersion;
    }

    static readonly Dependency[] DEPS = new Dependency[]
    {
        new Dependency
        {
            Label = "Poiyomi Toon Shader",
            URL   = "https://www.poiyomi.com/",
            VersionAPIs = new[]
            {
                "https://raw.githubusercontent.com/poiyomi/PoiyomiToonShader/master/Packages/com.poiyomi.toon/package.json",
                "https://api.github.com/repos/poiyomi/PoiyomiToonShader/releases?per_page=5",
            },
            IsInstalled = () =>
                AssetDatabase.IsValidFolder("Packages/com.poiyomi.toon") ||
                AssetDatabase.IsValidFolder("Assets/_PoiyomiShaders")    ||
                Shader.Find(".poiyomi/Poiyomi Toon") != null,
            GetLocalVersion = () =>
            {
                string v = ReadPackageVersion("com.poiyomi.toon");
                if (v != null) return v;
                string manualJson = Path.Combine(
                    Path.GetDirectoryName(Application.dataPath),
                    "Assets", "_PoiyomiShaders", "package.json");
                if (File.Exists(manualJson))
                    return ExtractJsonVersion(File.ReadAllText(manualJson));
                if (AssetDatabase.IsValidFolder("Assets/_PoiyomiShaders"))
                    return "manual";
                return null;
            }
        },

        new Dependency
        {
            Label = "VRCFury",
            URL   = "https://vrcfury.com/",
            VersionAPIs = new[]
            {
                "https://api.github.com/repos/VRCFury/VRCFury/releases?per_page=5",
            },
            IsInstalled = () =>
                AssetDatabase.IsValidFolder("Packages/com.vrcfury.vrcfury") ||
                Type.GetType("VF.Component.VRCFury, VF.Model") != null ||
                Type.GetType("VF.Model.VRCFury, VF.Model")     != null,
            GetLocalVersion = () =>
            {
                string v = ReadPackageVersion("com.vrcfury.vrcfury");
                if (v != null) return v;
                if (AssetDatabase.IsValidFolder("Packages/com.vrcfury.vrcfury"))
                    return "manual";
                return null;
            }
        },
    };

    // =========================================================================
    //  COLOURS
    // =========================================================================

    static readonly Color COL_ORANGE     = new Color(1f,    0.42f, 0f);
    static readonly Color COL_ORANGE_HOV = new Color(1f,    0.58f, 0.1f);
    static readonly Color COL_PANEL      = new Color(0.18f, 0.18f, 0.18f, 1f);
    static readonly Color COL_GRAY_TEXT  = new Color(0.65f, 0.65f, 0.65f);
    static readonly Color COL_SUCCESS    = new Color(0.25f, 0.80f, 0.40f);
    static readonly Color COL_OUTDATED   = new Color(1f,    0.80f, 0.10f);
    static readonly Color COL_ERROR      = new Color(1f,    0.30f, 0.20f);

    // =========================================================================
    //  VERSION CACHE
    // =========================================================================

    const string CACHE_KEY_VER  = "Matt3D_CI_CachedLatest_";
    const string CACHE_KEY_TIME = "Matt3D_CI_CachedTime_";
    const long   CACHE_MAX_AGE  = 86400;
    const double VERSION_FETCH_INTERVAL = 1800;

    // =========================================================================
    //  INTERNAL STATE
    // =========================================================================

    enum DepStatus { Checking, Missing, Outdated, ManualInstall, UpToDate, VerifyFailed }

    class DepState
    {
        public DepStatus Status     = DepStatus.Checking;
        public string LocalVersion  = null;
        public string LatestVersion = null;
    }

    GameObject          avatarRoot;
    GameObject          previousAvatarRoot;
    List<ClothingEntry> clothingItems = new List<ClothingEntry>();
    Vector2             scrollPos;
    bool                hasScanned;
    bool                pendingScan;

    List<GameObject> detectedAvatars       = new List<GameObject>();
    string[]         avatarDropdownOptions = new[] { "— Select Avatar —" };
    int              selectedAvatarIndex   = 0;

    DepState[] depStates;
    double     lastLocalCheckTime;
    double     lastVersionFetchTime;

    Texture2D logo;

    bool          popupActive;
    string        popupTitle;
    string        popupMessage;
    string        popupConfirmLabel;
    bool          popupHasCancel;
    Action        popupConfirmAction;

    Texture2D btnOrange, btnOrangeHov, btnGray, btnGrayHov, magGlassTex;

    bool      previewActive;
    Texture2D previewTexture;
    Texture2D previewHiRes;
    string    previewName;

    Dictionary<string, string> remoteVersions = new Dictionary<string, string>();
    bool clothingVersionsFetched;

    struct ClothingEntry
    {
        public string     displayName;
        public string     prefabPath;
        public GameObject prefabAsset;
        public string     localVersion;
        public string     markerFolder;
    }

    // =========================================================================
    //  TOAST SYSTEM
    // =========================================================================

    struct ToastInfo
    {
        public string message;
        public double startTime;
        public float  duration;
        public Color  color;
    }

    List<ToastInfo> activeToasts = new List<ToastInfo>();
    const float TOAST_DURATION  = 2.5f;
    const float TOAST_FADE_TIME = 0.4f;

    // =========================================================================
    //  ROW FLASH ANIMATION
    // =========================================================================

    Dictionary<string, double> rowFlashStartTimes = new Dictionary<string, double>();
    Dictionary<string, Color>  rowFlashColors     = new Dictionary<string, Color>();
    const float ROW_FLASH_DURATION = 1.0f;

    // =========================================================================
    //  FAVORITES
    // =========================================================================

    const string FAV_KEY_PREFIX = "Matt3D_CI_Fav_";
    HashSet<string> favoriteItems = new HashSet<string>();

    // =========================================================================
    //  THUMBNAILS
    // =========================================================================

    Dictionary<string, Texture2D> thumbnailCache = new Dictionary<string, Texture2D>();
    const float THUMB_SIZE = 32f;

    // =========================================================================
    //  OUTFIT PRESETS
    // =========================================================================

    [Serializable]
    class OutfitPreset
    {
        public string name = "";
        public List<string> clothingNames = new List<string>();
    }

    [Serializable]
    class PresetCollection
    {
        public List<OutfitPreset> presets = new List<OutfitPreset>();
    }

    bool             showPresetPanel;
    PresetCollection presetData      = new PresetCollection();
    Vector2          presetScrollPos;
    int              renamingIndex   = -1;
    string           renamingText    = "";
    string           newPresetName   = "";

    // =========================================================================
    //  MENU / OPEN
    // =========================================================================

    [MenuItem("Matt3D/Clothing Installer")]
    public static void OpenWindow()
    {
        var w = GetWindow<Matt3DClothingInstaller>("Matt3D Clothing Installer");
        w.minSize = new Vector2(420, 650);
        w.Show();
    }

    // =========================================================================
    //  LIFECYCLE
    // =========================================================================

    void OnEnable()
    {
        wantsMouseMove = true;
        string[] guids = AssetDatabase.FindAssets($"{LOGO_PATH} t:Texture2D");
        foreach (var g in guids)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(g);
            if (!assetPath.Contains("/Editor/")) continue;

            string fullPath = Path.Combine(
                Path.GetDirectoryName(Application.dataPath),
                assetPath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullPath)) continue;

            byte[] bytes = File.ReadAllBytes(fullPath);
            logo = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            logo.filterMode = FilterMode.Bilinear;
            logo.LoadImage(bytes);
            break;
        }

        const int r = 8;
        btnOrange    = RoundedRect(200, 34, r, COL_ORANGE);
        btnOrangeHov = RoundedRect(200, 34, r, COL_ORANGE_HOV);
        btnGray      = RoundedRect(200, 34, r, new Color(0.3f, 0.3f, 0.3f));
        btnGrayHov   = RoundedRect(200, 34, r, new Color(0.4f, 0.4f, 0.4f));
        magGlassTex  = GenerateMagnifyingGlass(64);

        InitDepStates();
        RunAllChecks();
        LoadPresets();
        FetchClothingVersions();
        lastLocalCheckTime = EditorApplication.timeSinceStartup;
        ScanSceneForAvatars();
        EditorApplication.update += OnEditorUpdate;
    }

    void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        foreach (var t in new[] { btnOrange, btnOrangeHov, btnGray, btnGrayHov })
            if (t != null) DestroyImmediate(t);
        if (logo != null) DestroyImmediate(logo);
        if (previewHiRes != null) DestroyImmediate(previewHiRes);
        if (magGlassTex != null) DestroyImmediate(magGlassTex);
    }

    void OnFocus()           { RunLocalChecks(); ScanSceneForAvatars(); Repaint(); }
    void OnHierarchyChange() { ScanSceneForAvatars(); Repaint(); }

    void OnEditorUpdate()
    {
        double now = EditorApplication.timeSinceStartup;

        if (now - lastLocalCheckTime >= 5.0)
        {
            lastLocalCheckTime = now;
            RunLocalChecks();
            Repaint();
        }

        if (now - lastVersionFetchTime >= VERSION_FETCH_INTERVAL)
        {
            lastVersionFetchTime = now;
            FetchAllVersions();
        }

        bool hasAnimations = activeToasts.Count > 0 || rowFlashStartTimes.Count > 0;
        if (hasAnimations)
            Repaint();
    }

    // =========================================================================
    //  GUI
    // =========================================================================

    void OnGUI()
    {
        if (Event.current.type == EventType.MouseMove)
            Repaint();

        if (pendingScan && Event.current.type == EventType.Layout)
        {
            pendingScan = false;
            EditorApplication.delayCall += ScanForClothing;
        }

        DrawHeader();
        DrawToasts();
        DrawAvatarField();
        EditorGUILayout.Space(8);
        DrawClothingList();
        GUILayout.FlexibleSpace();
        DrawPresetPanel();
        DrawDivider();
        DrawDependencies();
        DrawDivider();
        DrawBottomButtons();
        DrawFooter();

        if (previewActive)
            DrawImagePreview();

        if (popupActive)
            DrawPopup();
    }

    void DrawHeader()
    {
        var style = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize  = 15,
            alignment = TextAnchor.MiddleCenter
        };
        EditorGUILayout.Space(8);
        GUILayout.Label("Matt3D Clothing Installer", style);
        Rect line = EditorGUILayout.GetControlRect(false, 1);
        EditorGUI.DrawRect(line, new Color(1f, 0.42f, 0f, 0.5f));
    }

    void DrawAvatarField()
    {
        GUILayout.Label("Step 1 — Choose your avatar", EditorStyles.miniBoldLabel);

        GUILayout.BeginHorizontal();
        GUILayout.Space(4);
        GUILayout.Label("Avatar →", EditorStyles.boldLabel, GUILayout.Width(70));

        int newIndex = EditorGUILayout.Popup(selectedAvatarIndex, avatarDropdownOptions);
        if (newIndex != selectedAvatarIndex)
        {
            selectedAvatarIndex = newIndex;
            avatarRoot = newIndex > 0 ? detectedAvatars[newIndex - 1] : null;
        }

        if (GUILayout.Button("↻", GUILayout.Width(26), GUILayout.Height(EditorGUIUtility.singleLineHeight)))
            EditorApplication.delayCall += ScanSceneForAvatars;

        GUILayout.Space(4);
        GUILayout.EndHorizontal();

        GUILayout.Space(2);

        GUILayout.BeginHorizontal();
        GUILayout.Space(4);
        GUILayout.Space(70);
        var dragged = (GameObject)EditorGUILayout.ObjectField(avatarRoot, typeof(GameObject), true);
        if (dragged != avatarRoot)
        {
            avatarRoot = dragged;
            int found = detectedAvatars.IndexOf(dragged);
            selectedAvatarIndex = found >= 0 ? found + 1 : 0;
        }
        GUILayout.Space(4);
        GUILayout.EndHorizontal();

        if (avatarRoot != null && !avatarRoot.scene.IsValid())
        {
            EditorGUILayout.HelpBox("That looks like a project file, not a scene avatar. Please drag your avatar from the scene instead.", MessageType.Warning);
            avatarRoot = null;
            selectedAvatarIndex = 0;
        }

        if (avatarRoot != previousAvatarRoot)
        {
            previousAvatarRoot = avatarRoot;
            hasScanned    = false;
            clothingItems.Clear();
            if (avatarRoot != null)
                pendingScan = true;
        }
    }

    void DrawClothingList()
    {
        if (avatarRoot == null)
        {
            EditorGUILayout.HelpBox(
                "👆  Choose your avatar from the dropdown above.\n\n" +
                "If it doesn't appear in the list, drag it in from the Hierarchy panel into the field below the dropdown.",
                MessageType.Info);
            return;
        }

        if (!hasScanned)
        {
            EditorGUILayout.HelpBox(
                "Avatar selected! Scanning for your clothing...",
                MessageType.Info);
            return;
        }

        if (clothingItems.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "No Matt3D clothing found in this project.\n\n" +
                "Make sure you have imported your Matt3D products into Unity before using this tool.",
                MessageType.Warning);
            GUILayout.Space(4);
            if (GUILayout.Button("↻  Try Again", GUILayout.Height(28)))
                EditorApplication.delayCall += ScanForClothing;
            return;
        }

        GUILayout.Label($"Step 2 — Install your clothing  ({clothingItems.Count} item(s) found)", EditorStyles.miniBoldLabel);

        GUILayout.Space(4);

        if (GUILayout.Button("↻  Rescan Clothing", GUILayout.Height(28)))
            EditorApplication.delayCall += ScanForClothing;
        GUILayout.Space(2);
        if (GUILayout.Button("⟳  Check for Clothing Updates", GUILayout.Height(28)))
        {
            clothingVersionsFetched = false;
            EditorApplication.delayCall += ScanForClothing;
            FetchClothingVersions();
            ShowToast("Checking for updates...", COL_ORANGE);
        }
        GUILayout.Space(2);
        if (GUILayout.Button("Attach All Clothing", GUILayout.Height(28)))
            AttachAll();
        GUILayout.Space(2);
        if (GUILayout.Button("Remove All Clothing", GUILayout.Height(28)))
            ShowPopup("Remove All Clothing",
                $"This will remove all clothing from {avatarRoot.name}. Are you sure?",
                "Remove All", true, RemoveAllConfirmed);

        GUILayout.Space(6);

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        var sorted = clothingItems
            .OrderByDescending(c => IsFavorite(c.displayName))
            .ThenBy(c => c.displayName)
            .ToList();

        bool hasFavs    = sorted.Any(c => IsFavorite(c.displayName));
        bool hasNonFavs = sorted.Any(c => !IsFavorite(c.displayName));
        bool drawnSep   = false;

        foreach (var item in sorted)
        {
            if (hasFavs && hasNonFavs && !IsFavorite(item.displayName) && !drawnSep)
            {
                drawnSep = true;
                GUILayout.Space(2);
                Rect sep = EditorGUILayout.GetControlRect(false, 1f);
                sep.x += 4; sep.width -= 8;
                EditorGUI.DrawRect(sep, new Color(1f, 0.42f, 0f, 0.35f));
                GUILayout.Space(2);
            }
            DrawClothingRow(item, IsAlreadyAttached(item.displayName));
        }

        EditorGUILayout.EndScrollView();
    }

    void DrawClothingRow(ClothingEntry item, bool attached)
    {
        bool hasUpdate = HasClothingUpdate(item);

        Color bg = hasUpdate
            ? new Color(0.8f, 0.7f, 0.1f, 0.18f)
            : attached
                ? new Color(0.2f, 0.5f, 0.2f, 0.15f)
                : new Color(0.3f, 0.3f, 0.3f, 0.1f);

        if (rowFlashStartTimes.TryGetValue(item.displayName, out double flashStart))
        {
            double elapsed = EditorApplication.timeSinceStartup - flashStart;
            if (elapsed < ROW_FLASH_DURATION)
            {
                float t = 1f - (float)(elapsed / ROW_FLASH_DURATION);
                Color fc = rowFlashColors.ContainsKey(item.displayName)
                    ? rowFlashColors[item.displayName]
                    : COL_SUCCESS;
                bg = Color.Lerp(bg, new Color(fc.r, fc.g, fc.b, 0.45f), t);
            }
            else
            {
                rowFlashStartTimes.Remove(item.displayName);
                rowFlashColors.Remove(item.displayName);
            }
        }

        Rect row = EditorGUILayout.BeginHorizontal(GUILayout.MinHeight(THUMB_SIZE + 4));
        EditorGUI.DrawRect(row, bg);
        GUILayout.Space(4);

        bool isFav = IsFavorite(item.displayName);
        var starStyle = new GUIStyle(EditorStyles.label)
        {
            fontSize  = 14,
            alignment = TextAnchor.MiddleCenter,
            padding   = new RectOffset(0, 0, 0, 0)
        };
        starStyle.normal.textColor = isFav ? COL_ORANGE : COL_GRAY_TEXT;
        starStyle.hover.textColor  = COL_ORANGE;
        if (GUILayout.Button(isFav ? "★" : "☆", starStyle,
            GUILayout.Width(22), GUILayout.Height(THUMB_SIZE)))
        {
            ToggleFavorite(item.displayName);
        }

        GUILayout.Space(2);

        Texture2D thumb = GetThumbnail(item);
        if (thumb != null)
        {
            Rect tr = GUILayoutUtility.GetRect(THUMB_SIZE, THUMB_SIZE,
                GUILayout.Width(THUMB_SIZE), GUILayout.Height(THUMB_SIZE));
            GUI.DrawTexture(tr, thumb, ScaleMode.ScaleToFit);

            if (Event.current.type == EventType.Repaint && tr.Contains(Event.current.mousePosition))
            {
                EditorGUI.DrawRect(new Rect(tr.x, tr.y, tr.width, 2), COL_ORANGE);
                EditorGUI.DrawRect(new Rect(tr.x, tr.yMax - 2, tr.width, 2), COL_ORANGE);
                EditorGUI.DrawRect(new Rect(tr.x, tr.y, 2, tr.height), COL_ORANGE);
                EditorGUI.DrawRect(new Rect(tr.xMax - 2, tr.y, 2, tr.height), COL_ORANGE);

                EditorGUI.DrawRect(new Rect(tr.x + 2, tr.y + 2, tr.width - 4, tr.height - 4),
                    new Color(0, 0, 0, 0.4f));
                if (magGlassTex != null)
                    GUI.DrawTexture(tr, magGlassTex, ScaleMode.ScaleToFit, true);
            }

            if (Event.current.type == EventType.MouseDown && tr.Contains(Event.current.mousePosition))
            {
                previewActive  = true;
                previewTexture = thumb;
                previewName    = item.displayName;
                if (previewHiRes != null) { DestroyImmediate(previewHiRes); previewHiRes = null; }
                if (thumb.width < 256 && item.prefabAsset != null)
                    previewHiRes = RenderHiResPreview(item.prefabAsset, item.prefabPath);
                Event.current.Use();
                Repaint();
            }
        }
        else
        {
            GUILayoutUtility.GetRect(THUMB_SIZE, THUMB_SIZE,
                GUILayout.Width(THUMB_SIZE), GUILayout.Height(THUMB_SIZE));
        }

        GUILayout.Space(6);
        GUILayout.Label(item.displayName, GUILayout.ExpandWidth(true), GUILayout.Height(THUMB_SIZE));

        float btnH = THUMB_SIZE - 4;

        if (hasUpdate)
        {
            var warnStyle = new GUIStyle(EditorStyles.label);
            warnStyle.normal.textColor = COL_OUTDATED;
            warnStyle.fontStyle        = FontStyle.Bold;
            warnStyle.alignment        = TextAnchor.MiddleCenter;
            warnStyle.fontSize         = 12;
            GUILayout.Label("⚠", warnStyle, GUILayout.Width(18), GUILayout.Height(THUMB_SIZE));

            var msgStyle = new GUIStyle(EditorStyles.label);
            msgStyle.normal.textColor = COL_OUTDATED;
            msgStyle.fontSize         = 10;
            msgStyle.alignment        = TextAnchor.MiddleLeft;
            msgStyle.wordWrap         = true;
            GUILayout.Label("Outdated! Please get\nthe newest version.", msgStyle,
                GUILayout.Width(130), GUILayout.Height(THUMB_SIZE));
        }

        if (attached)
        {
            var s = new GUIStyle(EditorStyles.label);
            s.normal.textColor = COL_SUCCESS;
            s.alignment        = TextAnchor.MiddleCenter;
            s.fontSize         = 11;
            GUILayout.Label("✓ Attached", s, GUILayout.Width(72),
                GUILayout.Height(THUMB_SIZE));

            var removeStyle = new GUIStyle(EditorStyles.miniButton);
            removeStyle.normal.textColor = COL_ERROR;
            removeStyle.hover.textColor  = COL_ERROR;
            removeStyle.active.textColor = COL_ERROR;
            removeStyle.fontSize         = 10;
            removeStyle.alignment        = TextAnchor.MiddleCenter;
            removeStyle.padding          = new RectOffset(0, 0, 0, 0);
            removeStyle.margin           = new RectOffset(0, 0, 0, 0);
            string itemName = item.displayName;
            GUILayout.BeginVertical(GUILayout.Width(22), GUILayout.Height(THUMB_SIZE + 4));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("✕", removeStyle, GUILayout.Width(22), GUILayout.Height(18)))
                ShowPopup("Remove Clothing",
                    $"Are you sure you want to remove {itemName} from the avatar?",
                    "Remove", true, () => RemoveClothingConfirmed(itemName));
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
        }
        else
        {
            var attachItem = item;
            if (GUILayout.Button("Attach", GUILayout.Width(70), GUILayout.Height(btnH)))
            {
                if (hasUpdate)
                    ShowPopup("Outdated Clothing",
                        $"{attachItem.displayName} is outdated and may cause issues.\nAre you sure you want to attach it anyway?",
                        "Attach Anyway", true, () => AttachClothing(attachItem));
                else
                    AttachClothing(attachItem);
            }
        }

        GUILayout.Space(4);
        EditorGUILayout.EndHorizontal();
        GUILayout.Space(2);
    }

    void DrawDivider()
    {
        GUILayout.Space(2);
        Rect r = EditorGUILayout.GetControlRect(false, 1f);
        r.x += 8; r.width -= 16;
        EditorGUI.DrawRect(r, new Color(1f, 0.42f, 0f, 0.35f));
        GUILayout.Space(2);
    }

    void DrawDependencies()
    {
        if (depStates == null) return;

        GUILayout.Space(6);

        bool anyBad = false;
        foreach (var d in depStates)
            if (d.Status == DepStatus.Missing || d.Status == DepStatus.Outdated) anyBad = true;

        GUILayout.BeginHorizontal();
        GUILayout.Space(8);
        var headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
        headerStyle.normal.textColor = anyBad ? COL_ERROR : COL_SUCCESS;
        GUILayout.Label(anyBad ? "⚠  Required Plugins" : "✓  All Plugins Detected", headerStyle);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("↻ Recheck", EditorStyles.miniButton, GUILayout.Width(80)))
            RunAllChecks();
        GUILayout.Space(8);
        GUILayout.EndHorizontal();
        GUILayout.Space(6);

        for (int i = 0; i < DEPS.Length; i++)
        {
            var dep   = DEPS[i];
            var state = depStates[i];

            Color  col;
            string icon, statusText;

            switch (state.Status)
            {
                case DepStatus.UpToDate:
                    col        = COL_SUCCESS;
                    icon       = "✓";
                    statusText = state.LatestVersion != null
                        ? $"v{state.LocalVersion}  ·  Latest: v{state.LatestVersion}"
                        : $"v{state.LocalVersion}";
                    break;
                case DepStatus.Outdated:
                    col        = COL_OUTDATED;
                    icon       = "⚠";
                    statusText = $"v{state.LocalVersion}  →  v{state.LatestVersion}";
                    break;
                case DepStatus.ManualInstall:
                    col        = COL_OUTDATED;
                    icon       = "⚠";
                    statusText = "Manual install — version unknown";
                    break;
                case DepStatus.VerifyFailed:
                    col        = COL_GRAY_TEXT;
                    icon       = "?";
                    statusText = "Installed  ·  Latest: unavailable";
                    break;
                case DepStatus.Missing:
                    col        = COL_ERROR;
                    icon       = "✗";
                    statusText = "Not installed";
                    break;
                default:
                    col        = COL_GRAY_TEXT;
                    icon       = "…";
                    statusText = "Checking…";
                    break;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Space(8);

            var labelStyle = new GUIStyle(EditorStyles.label) { fontSize = 11 };
            labelStyle.normal.textColor = col;
            GUILayout.Label($"{icon}  {dep.Label}", labelStyle);
            GUILayout.FlexibleSpace();

            var statusStyle = new GUIStyle(EditorStyles.label) { fontSize = 10 };
            statusStyle.normal.textColor = col;
            GUILayout.Label(statusText, statusStyle);

            if (state.Status == DepStatus.Missing)
            {
                GUILayout.Space(6);
                if (GUILayout.Button("Download ↗", EditorStyles.miniButton, GUILayout.Width(85)))
                    Application.OpenURL(dep.URL);
            }
            else if (state.Status == DepStatus.Outdated     ||
                     state.Status == DepStatus.ManualInstall ||
                     state.Status == DepStatus.VerifyFailed)
            {
                GUILayout.Space(6);
                if (GUILayout.Button("Update ↗", EditorStyles.miniButton, GUILayout.Width(67)))
                    Application.OpenURL(dep.URL);
            }

            GUILayout.Space(8);
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
        }

        GUILayout.Space(4);
    }

    // =========================================================================
    //  CUSTOM POPUP
    // =========================================================================

    void ShowPopup(string title, string message, string confirmLabel, bool hasCancel, Action onConfirm)
    {
        popupTitle         = title;
        popupMessage       = message;
        popupConfirmLabel  = confirmLabel;
        popupHasCancel     = hasCancel;
        popupConfirmAction = onConfirm;
        popupActive        = true;
        Repaint();
    }

    void ShowInfoPopup(string title, string message)
    {
        ShowPopup(title, message, "OK", false, null);
    }

    void DrawPopup()
    {
        EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), new Color(0, 0, 0, 0.6f));

        float msgW = 300f - 32f;
        var msgStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
        {
            fontSize  = 11,
            alignment = TextAnchor.MiddleCenter
        };
        msgStyle.normal.textColor = Color.white;
        float msgH = msgStyle.CalcHeight(new GUIContent(popupMessage ?? ""), msgW);

        const float logoH    = 88f;
        const float titleH   = 22f;
        const float btnH     = 34f;
        const float padTop   = 10f;
        const float padTitle = 8f;
        const float padMsg   = 12f;
        const float padBot   = 14f;

        float panelW = 300f;
        float panelH = logo != null
            ? 2 + logoH + padTop + titleH + padTitle + msgH + padMsg + btnH + padBot
            : 22 + titleH + padTitle + msgH + padMsg + btnH + padBot;

        float panelX = (position.width  - panelW) / 2f;
        float panelY = (position.height - panelH) / 2f;
        Rect  panel  = new Rect(panelX, panelY, panelW, panelH);

        EditorGUI.DrawRect(panel, COL_PANEL);

        EditorGUI.DrawRect(new Rect(panel.x,        panel.y,        panelW, 2),      COL_ORANGE);
        EditorGUI.DrawRect(new Rect(panel.x,        panel.yMax - 2, panelW, 2),      COL_ORANGE);
        EditorGUI.DrawRect(new Rect(panel.x,        panel.y,        2,      panelH), COL_ORANGE);
        EditorGUI.DrawRect(new Rect(panel.xMax - 2, panel.y,        2,      panelH), COL_ORANGE);

        float contentY;
        if (logo != null)
        {
            GUI.DrawTexture(new Rect(panelX + 2, panelY + 2, panelW - 4, logoH), logo, ScaleMode.ScaleToFit, true);
            contentY = panelY + 2 + logoH + padTop;
        }
        else
        {
            contentY = panelY + 22f;
        }

        var titleStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize  = 13,
            alignment = TextAnchor.MiddleCenter,
        };
        titleStyle.normal.textColor = COL_ORANGE;
        GUI.Label(new Rect(panelX + 16, contentY, panelW - 32, titleH), popupTitle, titleStyle);
        contentY += titleH + padTitle;

        GUI.Label(new Rect(panelX + 16, contentY, msgW, msgH), popupMessage, msgStyle);
        contentY += msgH + padMsg;

        float btnW   = popupHasCancel ? 110f : 140f;
        float btnGap = 10f;
        float totalBtnW = popupHasCancel ? btnW * 2 + btnGap : btnW;
        float btnStartX = panelX + (panelW - totalBtnW) / 2f;

        var confirmStyle = new GUIStyle(GUIStyle.none)
        {
            fontSize    = 12,
            fontStyle   = FontStyle.Bold,
            fixedHeight = btnH,
            alignment   = TextAnchor.MiddleCenter
        };
        confirmStyle.normal.background  = btnOrange;
        confirmStyle.normal.textColor   = Color.white;
        confirmStyle.hover.background   = btnOrangeHov;
        confirmStyle.hover.textColor    = Color.white;
        confirmStyle.active.background  = btnOrangeHov;
        confirmStyle.active.textColor   = Color.white;

        if (GUI.Button(new Rect(btnStartX, contentY, btnW, btnH), popupConfirmLabel, confirmStyle))
        {
            popupActive = false;
            popupConfirmAction?.Invoke();
            Repaint();
        }

        if (popupHasCancel)
        {
            var cancelStyle = new GUIStyle(GUIStyle.none)
            {
                fontSize    = 12,
                fontStyle   = FontStyle.Bold,
                fixedHeight = btnH,
                alignment   = TextAnchor.MiddleCenter
            };
            cancelStyle.normal.background = btnGray;
            cancelStyle.normal.textColor  = Color.white;
            cancelStyle.hover.background  = btnGrayHov;
            cancelStyle.hover.textColor   = Color.white;
            cancelStyle.active.background = btnGrayHov;
            cancelStyle.active.textColor  = Color.white;

            if (GUI.Button(new Rect(btnStartX + btnW + btnGap, contentY, btnW, btnH), "Cancel", cancelStyle))
            {
                popupActive = false;
                Repaint();
            }
        }

        if (Event.current.type != EventType.Repaint && Event.current.type != EventType.Layout)
            Event.current.Use();
    }

    // =========================================================================
    //  IMAGE PREVIEW OVERLAY
    // =========================================================================

    void DrawImagePreview()
    {
        EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), new Color(0, 0, 0, 0.7f));

        float imgSize = Mathf.Min(position.width - 60f, position.height - 100f, 512f);

        float panelW = imgSize + 32f;
        float titleH = 24f;
        float padTop = 12f;
        float padBot = 14f;
        float panelH = padTop + titleH + 8f + imgSize + padBot;

        float panelX = (position.width  - panelW) / 2f;
        float panelY = (position.height - panelH) / 2f;
        Rect  panel  = new Rect(panelX, panelY, panelW, panelH);

        EditorGUI.DrawRect(panel, COL_PANEL);
        EditorGUI.DrawRect(new Rect(panel.x,        panel.y,        panelW, 2),      COL_ORANGE);
        EditorGUI.DrawRect(new Rect(panel.x,        panel.yMax - 2, panelW, 2),      COL_ORANGE);
        EditorGUI.DrawRect(new Rect(panel.x,        panel.y,        2,      panelH), COL_ORANGE);
        EditorGUI.DrawRect(new Rect(panel.xMax - 2, panel.y,        2,      panelH), COL_ORANGE);

        var titleStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize  = 12,
            alignment = TextAnchor.MiddleCenter
        };
        titleStyle.normal.textColor = COL_ORANGE;
        GUI.Label(new Rect(panelX + 16, panelY + padTop, panelW - 32, titleH), previewName, titleStyle);

        float imgX = panelX + (panelW - imgSize) / 2f;
        float imgY = panelY + padTop + titleH + 8f;
        Texture2D displayTex = previewHiRes != null ? previewHiRes : previewTexture;
        if (displayTex != null)
            GUI.DrawTexture(new Rect(imgX, imgY, imgSize, imgSize), displayTex, ScaleMode.ScaleToFit);

        if (Event.current.type == EventType.MouseDown)
        {
            previewActive = false;
            if (previewHiRes != null) { DestroyImmediate(previewHiRes); previewHiRes = null; }
            Event.current.Use();
            Repaint();
        }

        if (Event.current.type != EventType.Repaint && Event.current.type != EventType.Layout)
            Event.current.Use();
    }

    // =========================================================================
    //  TOAST SYSTEM
    // =========================================================================

    void ShowToast(string message, Color color, float duration = TOAST_DURATION)
    {
        activeToasts.Add(new ToastInfo
        {
            message   = message,
            startTime = EditorApplication.timeSinceStartup,
            duration  = duration,
            color     = color
        });
        Repaint();
    }

    void DrawToasts()
    {
        GUILayout.Space(4);
        Rect r = EditorGUILayout.GetControlRect(false, 26);
        r.x += 8; r.width -= 16;

        double now = EditorApplication.timeSinceStartup;
        activeToasts.RemoveAll(t => now - t.startTime > t.duration);
        if (activeToasts.Count == 0) return;

        var toast   = activeToasts[activeToasts.Count - 1];
        float elapsed = (float)(now - toast.startTime);
        float alpha   = 1f;

        if (elapsed < 0.15f)
            alpha = elapsed / 0.15f;
        else if (elapsed > toast.duration - TOAST_FADE_TIME)
            alpha = 1f - ((elapsed - (toast.duration - TOAST_FADE_TIME)) / TOAST_FADE_TIME);

        alpha = Mathf.Clamp01(alpha);

        EditorGUI.DrawRect(
            new Rect(r.x + 1, r.y + 1, r.width, r.height),
            new Color(0, 0, 0, alpha * 0.25f));

        Color bg = EditorGUIUtility.isProSkin
            ? new Color(toast.color.r * 0.25f, toast.color.g * 0.25f, toast.color.b * 0.25f, alpha * 0.92f)
            : new Color(toast.color.r * 0.15f + 0.85f, toast.color.g * 0.15f + 0.85f, toast.color.b * 0.15f + 0.85f, alpha * 0.95f);
        EditorGUI.DrawRect(r, bg);

        EditorGUI.DrawRect(
            new Rect(r.x, r.y, 3, r.height),
            new Color(toast.color.r, toast.color.g, toast.color.b, alpha));

        var style = new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize  = 11
        };
        style.normal.textColor = EditorGUIUtility.isProSkin
            ? new Color(1f, 1f, 1f, alpha)
            : new Color(0.1f, 0.1f, 0.1f, alpha);
        GUI.Label(r, toast.message, style);
    }

    // =========================================================================
    //  ROW FLASH
    // =========================================================================

    void FlashRow(string itemName, Color color)
    {
        rowFlashStartTimes[itemName] = EditorApplication.timeSinceStartup;
        rowFlashColors[itemName]     = color;
    }

    // =========================================================================
    //  FAVORITES
    // =========================================================================

    bool IsFavorite(string name) => favoriteItems.Contains(name);

    void ToggleFavorite(string name)
    {
        if (favoriteItems.Contains(name))
        {
            favoriteItems.Remove(name);
            EditorPrefs.DeleteKey(FAV_KEY_PREFIX + name);
        }
        else
        {
            favoriteItems.Add(name);
            EditorPrefs.SetBool(FAV_KEY_PREFIX + name, true);
        }
        Repaint();
    }

    void LoadFavorites()
    {
        favoriteItems.Clear();
        foreach (var item in clothingItems)
            if (EditorPrefs.GetBool(FAV_KEY_PREFIX + item.displayName, false))
                favoriteItems.Add(item.displayName);
    }

    // =========================================================================
    //  THUMBNAILS
    // =========================================================================

    Texture2D GetThumbnail(ClothingEntry item)
    {
        if (thumbnailCache.TryGetValue(item.prefabPath, out var cached) && cached != null)
            return cached;

        string folder = Path.GetDirectoryName(item.prefabPath).Replace("\\", "/");
        string[] names = { "preview.png", "Preview.png", "preview.jpg", "Preview.jpg",
                           "thumbnail.png", "Thumbnail.png" };

        foreach (string n in names)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + n);
            if (tex != null)
            {
                thumbnailCache[item.prefabPath] = tex;
                return tex;
            }
        }

        var assetPreview = AssetPreview.GetAssetPreview(item.prefabAsset);
        if (assetPreview != null && !AssetPreview.IsLoadingAssetPreview(item.prefabAsset.GetInstanceID()))
            thumbnailCache[item.prefabPath] = assetPreview;

        return assetPreview;
    }

    Texture2D RenderHiResPreview(GameObject prefab, string path)
    {
        try
        {
            var editor = Editor.CreateEditor(prefab);
            if (editor == null) return null;
            var tex = editor.RenderStaticPreview(path, null, 512, 512);
            DestroyImmediate(editor);
            if (tex != null && tex.width > 1)
                return tex;
            if (tex != null) DestroyImmediate(tex);
            return null;
        }
        catch
        {
            return null;
        }
    }

    // =========================================================================
    //  PRESET PANEL
    // =========================================================================

    void DrawPresetPanel()
    {
        if (avatarRoot == null || !hasScanned || clothingItems.Count == 0) return;

        EditorGUILayout.Space(4);
        DrawDivider();

        GUILayout.BeginHorizontal();
        GUILayout.Space(8);
        showPresetPanel = EditorGUILayout.Foldout(showPresetPanel, "Outfit Presets", true, EditorStyles.foldoutHeader);
        GUILayout.Space(8);
        GUILayout.EndHorizontal();

        if (!showPresetPanel) return;

        GUILayout.Space(4);

        GUILayout.BeginHorizontal();
        GUILayout.Space(12);
        GUILayout.Label("Name:", GUILayout.Width(42));
        newPresetName = EditorGUILayout.TextField(newPresetName);
        EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(newPresetName));
        if (GUILayout.Button("Save Preset", GUILayout.Width(100)))
        {
            SaveCurrentAsPreset(newPresetName.Trim());
            newPresetName = "";
            GUI.FocusControl(null);
        }
        EditorGUI.EndDisabledGroup();
        GUILayout.Space(12);
        GUILayout.EndHorizontal();

        GUILayout.Space(4);

        if (presetData.presets.Count == 0)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(12);
            EditorGUILayout.HelpBox(
                "No presets saved yet. Attach some clothing and save your first outfit!",
                MessageType.Info);
            GUILayout.Space(12);
            GUILayout.EndHorizontal();
        }
        else
        {
            presetScrollPos = EditorGUILayout.BeginScrollView(presetScrollPos);
            for (int i = 0; i < presetData.presets.Count; i++)
                DrawPresetRow(i);
            EditorGUILayout.EndScrollView();
        }

        GUILayout.Space(4);
    }

    void DrawPresetRow(int index)
    {
        var preset = presetData.presets[index];

        GUILayout.BeginHorizontal();
        GUILayout.Space(12);


        if (renamingIndex == index)
        {
            renamingText = EditorGUILayout.TextField(renamingText, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("✓", EditorStyles.miniButton, GUILayout.Width(22)))
            {
                if (!string.IsNullOrWhiteSpace(renamingText))
                {
                    preset.name = renamingText.Trim();
                    SavePresets();
                }
                renamingIndex = -1;
            }
            if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
                renamingIndex = -1;
        }
        else
        {
            var nameStyle = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold };
            GUILayout.Label(preset.name, nameStyle, GUILayout.ExpandWidth(true));


            if (GUILayout.Button("Apply", EditorStyles.miniButton, GUILayout.Width(55)))
                ApplyPreset(preset);

            if (GUILayout.Button("Rename", EditorStyles.miniButton, GUILayout.Width(62)))
            {
                renamingIndex = index;
                renamingText  = preset.name;
            }


            var delStyle = new GUIStyle(EditorStyles.miniButton);
            delStyle.normal.textColor = COL_ERROR;
            if (GUILayout.Button("✕", delStyle, GUILayout.Width(22)))
                DeletePreset(index);
        }

        GUILayout.Space(12);
        GUILayout.EndHorizontal();
        GUILayout.Space(2);
    }

    // =========================================================================
    //  PRESET LOGIC
    // =========================================================================

    static string GetPresetPath()
    {
        return Path.Combine(
            Path.GetDirectoryName(Application.dataPath),
            "ProjectSettings", "Matt3DClothingPresets.json");
    }

    void LoadPresets()
    {
        string path = GetPresetPath();
        if (File.Exists(path))
        {
            try
            {
                presetData = JsonUtility.FromJson<PresetCollection>(File.ReadAllText(path))
                    ?? new PresetCollection();
            }
            catch
            {
                presetData = new PresetCollection();
            }
        }
        else
        {
            presetData = new PresetCollection();
        }
    }

    void SavePresets()
    {
        File.WriteAllText(GetPresetPath(), JsonUtility.ToJson(presetData, true));
    }

    void SaveCurrentAsPreset(string name)
    {
        var preset = new OutfitPreset { name = name };
        foreach (var item in clothingItems)
            if (IsAlreadyAttached(item.displayName))
                preset.clothingNames.Add(item.displayName);

        if (preset.clothingNames.Count == 0)
        {
            ShowToast("No clothing attached to save!", COL_ERROR);
            return;
        }

        presetData.presets.Add(preset);
        SavePresets();
        ShowToast($"Preset \"{name}\" saved ({preset.clothingNames.Count} items)", COL_SUCCESS);
    }

    void ApplyPreset(OutfitPreset preset)
    {
        if (avatarRoot == null) return;

        EditorApplication.delayCall += () =>
        {
            if (avatarRoot == null) return;

            int attached = 0, removed = 0;

            foreach (var item in clothingItems)
            {
                bool shouldBeOn = preset.clothingNames.Contains(item.displayName);
                bool isOn       = IsAlreadyAttached(item.displayName);

                if (isOn && !shouldBeOn)
                {
                    foreach (Transform child in avatarRoot.transform)
                    {
                        if (child.name == item.displayName)
                        {
                            Undo.DestroyObjectImmediate(child.gameObject);
                            FlashRow(item.displayName, COL_ERROR);
                            removed++;
                            break;
                        }
                    }
                }
            }

            foreach (var item in clothingItems)
            {
                if (preset.clothingNames.Contains(item.displayName) && !IsAlreadyAttached(item.displayName))
                {
                    DoAttach(item, silent: true);
                    FlashRow(item.displayName, COL_SUCCESS);
                    attached++;
                }
            }

            ShowToast($"Preset \"{preset.name}\" applied (+{attached} / -{removed})", COL_ORANGE);
            Repaint();
        };
    }

    void DuplicatePreset(int index)
    {
        var src = presetData.presets[index];
        var dup = new OutfitPreset
        {
            name           = src.name + " (Copy)",
            clothingNames  = new List<string>(src.clothingNames)
        };
        presetData.presets.Insert(index + 1, dup);
        SavePresets();
        ShowToast($"Duplicated \"{src.name}\"", COL_ORANGE);
    }

    void DeletePreset(int index)
    {
        string name = presetData.presets[index].name;
        ShowPopup("Delete Preset",
            $"Are you sure you want to delete preset \"{name}\"?",
            "Delete", true, () =>
            {
                presetData.presets.RemoveAt(index);
                if (renamingIndex == index) renamingIndex = -1;
                else if (renamingIndex > index) renamingIndex--;
                SavePresets();
                ShowToast($"Preset \"{name}\" deleted", COL_ERROR);
            });
    }

    // =========================================================================
    //  AVATAR SCENE DETECTION
    // =========================================================================

    void ScanSceneForAvatars()
    {
        detectedAvatars.Clear();

        Scene scene = SceneManager.GetActiveScene();
        foreach (var root in scene.GetRootGameObjects())
        {
            if (!root.activeInHierarchy) continue;
            if (root.GetComponent("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor") != null)
                detectedAvatars.Add(root);
        }

        var nameCount = new Dictionary<string, int>();
        foreach (var a in detectedAvatars)
        {
            string n = a.name;
            nameCount[n] = nameCount.ContainsKey(n) ? nameCount[n] + 1 : 1;
        }
        var nameIndex = new Dictionary<string, int>();
        var names     = new List<string>();
        foreach (var a in detectedAvatars)
        {
            string n = a.name;
            if (nameCount[n] > 1)
            {
                nameIndex[n] = nameIndex.ContainsKey(n) ? nameIndex[n] + 1 : 1;
                names.Add($"{n} ({nameIndex[n]})");
            }
            else
            {
                names.Add(n);
            }
        }

        var options = new List<string> { "— Select Avatar —" };
        options.AddRange(names);
        avatarDropdownOptions = options.ToArray();

        if (detectedAvatars.Count == 1 && avatarRoot == null)
        {
            selectedAvatarIndex = 1;
            avatarRoot          = detectedAvatars[0];
            previousAvatarRoot  = null;
            pendingScan         = true;
        }

        if (avatarRoot != null)
        {
            int idx = detectedAvatars.IndexOf(avatarRoot);
            selectedAvatarIndex = idx >= 0 ? idx + 1 : 0;
        }
    }

    // =========================================================================
    //  CLOTHING LOGIC
    // =========================================================================

    void ScanForClothing()
    {
        clothingItems.Clear();
        thumbnailCache.Clear();
        hasScanned = true;

        string[] markerGuids = AssetDatabase.FindAssets("Matt3D_ClothingMarker t:TextAsset");

        foreach (string guid in markerGuids)
        {
            string markerPath = AssetDatabase.GUIDToAssetPath(guid);
            if (markerPath.Contains("/Editor/")) continue;

            string markerVersion = null;
            var markerAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(markerPath);
            if (markerAsset != null)
            {
                foreach (string line in markerAsset.text.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                    {
                        markerVersion = trimmed.Substring(8).Trim();
                        break;
                    }
                }
            }

            string   folderPath  = Path.GetDirectoryName(markerPath).Replace("\\", "/");
            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { folderPath });

            foreach (string prefabGuid in prefabGuids)
            {
                string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuid);
                if (Path.GetDirectoryName(prefabPath).Replace("\\", "/") != folderPath) continue;

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null) continue;

                clothingItems.Add(new ClothingEntry
                {
                    displayName  = prefab.name,
                    prefabPath   = prefabPath,
                    prefabAsset  = prefab,
                    localVersion = markerVersion,
                    markerFolder = folderPath
                });
            }
        }

        clothingItems.Sort((a, b) => string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase));
        LoadFavorites();
        Repaint();
    }

    void AttachAll()
    {
        ValidatePlugins(() =>
        {
            int count = 0;
            foreach (var item in clothingItems)
            {
                if (!IsAlreadyAttached(item.displayName))
                {
                    DoAttach(item, silent: true);
                    FlashRow(item.displayName, COL_SUCCESS);
                    count++;
                }
            }

            if (count == 0)
                ShowToast("All clothing is already attached!", COL_ORANGE);
            else
                ShowToast($"{count} clothing piece(s) attached to {avatarRoot.name}", COL_SUCCESS);

            Repaint();
        });
    }

    void RemoveAllConfirmed()
    {
        EditorApplication.delayCall += () =>
        {
            if (avatarRoot == null) return;
            int count = 0;
            foreach (var item in clothingItems)
            {
                foreach (Transform child in avatarRoot.transform)
                {
                    if (child.name == item.displayName)
                    {
                        Undo.DestroyObjectImmediate(child.gameObject);
                        FlashRow(item.displayName, COL_ERROR);
                        count++;
                        break;
                    }
                }
            }
            ShowToast($"{count} clothing piece(s) removed from {avatarRoot.name}", COL_ERROR);
            Repaint();
        };
    }

    void AttachClothing(ClothingEntry item)
    {
        if (avatarRoot == null)
        {
            ShowInfoPopup("No Avatar", "Please select your avatar first.");
            return;
        }

        if (IsAlreadyAttached(item.displayName))
        {
            ShowToast($"{item.displayName} is already attached", COL_ORANGE);
            return;
        }

        ValidatePlugins(() =>
        {
            DoAttach(item, silent: false);
            FlashRow(item.displayName, COL_SUCCESS);
        });
    }

    void DoAttach(ClothingEntry item, bool silent)
    {
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(item.prefabAsset, avatarRoot.transform);
        instance.name                    = item.displayName;
        instance.transform.localPosition = Vector3.zero;
        instance.transform.localRotation = Quaternion.identity;

        Undo.RegisterCreatedObjectUndo(instance, $"Attach {item.displayName}");

        if (!silent)
            ShowToast($"{item.displayName} attached to {avatarRoot.name}", COL_SUCCESS);

        Repaint();
    }

    void ValidatePlugins(Action onAllClear)
    {
        if (depStates == null) { onAllClear(); return; }

        var missing  = new List<int>();
        var outdated = new List<int>();

        for (int i = 0; i < DEPS.Length; i++)
        {
            if (depStates[i].Status == DepStatus.Missing)
                missing.Add(i);
            else if (depStates[i].Status == DepStatus.Outdated)
                outdated.Add(i);
        }

        if (missing.Count > 0)
        {
            if (missing.Count == 1)
            {
                string label = DEPS[missing[0]].Label;
                string url   = DEPS[missing[0]].URL;
                ShowPopup(
                    "Required Plugin Missing",
                    $"{label} is not installed.\n\nThis plugin is required for your clothing to work correctly in VRChat. Please install it before attaching clothing.",
                    "Download Now ↗", true,
                    () => Application.OpenURL(url));
            }
            else
            {
                string names = string.Join(" and ", missing.ConvertAll(i => DEPS[i].Label));
                ShowInfoPopup(
                    "Required Plugins Missing",
                    $"{names} are not installed.\n\nThese plugins are required for your clothing to work correctly in VRChat. Please install them using the download buttons below.");
            }
            return;
        }

        if (outdated.Count > 0)
        {
            string names = outdated.Count == 1
                ? DEPS[outdated[0]].Label
                : string.Join(" and ", outdated.ConvertAll(i => DEPS[i].Label));

            ShowPopup(
                "Plugin Update Recommended",
                $"{names} {(outdated.Count == 1 ? "is" : "are")} outdated.\n\nYour clothing may still work, but could cause issues in VRChat. We recommend updating before continuing.",
                "Continue Anyway", true,
                onAllClear);
            return;
        }

        onAllClear();
    }

    void RemoveClothingConfirmed(string clothingName)
    {
        EditorApplication.delayCall += () =>
        {
            if (avatarRoot == null) return;
            foreach (Transform child in avatarRoot.transform)
            {
                if (child.name == clothingName)
                {
                    Undo.DestroyObjectImmediate(child.gameObject);
                    FlashRow(clothingName, COL_ERROR);
                    ShowToast($"{clothingName} removed", COL_ERROR);
                    Repaint();
                    return;
                }
            }
        };
    }

    bool IsAlreadyAttached(string clothingName)
    {
        if (avatarRoot == null) return false;
        foreach (Transform child in avatarRoot.transform)
            if (child.name == clothingName) return true;
        return false;
    }

    // =========================================================================
    //  DEPENDENCY CHECK LOGIC
    // =========================================================================

    void InitDepStates()
    {
        depStates = new DepState[DEPS.Length];
        for (int i = 0; i < DEPS.Length; i++)
            depStates[i] = new DepState();
    }

    void RunLocalChecks()
    {
        if (depStates == null) InitDepStates();
        for (int i = 0; i < DEPS.Length; i++)
        {
            if (!DEPS[i].IsInstalled())
            {
                depStates[i].Status        = DepStatus.Missing;
                depStates[i].LocalVersion  = null;
                depStates[i].LatestVersion = null;
            }
            else if (depStates[i].Status == DepStatus.Missing)
            {
                depStates[i].Status        = DepStatus.Checking;
                depStates[i].LocalVersion  = null;
                depStates[i].LatestVersion = null;
                FetchAllVersions();
            }
        }
    }

    void RunAllChecks()
    {
        if (depStates == null) InitDepStates();
        RunLocalChecks();
        lastVersionFetchTime = EditorApplication.timeSinceStartup;
        FetchAllVersions();
    }

    void FetchAllVersions()
    {
        if (depStates == null) return;

        for (int i = 0; i < DEPS.Length; i++)
        {
            var state = depStates[i];
            if (state.Status == DepStatus.Missing) continue;

            string localVer = DEPS[i].GetLocalVersion();
            state.LocalVersion = localVer;

            if (localVer == "manual") { state.Status = DepStatus.ManualInstall; continue; }

            string cachedLatest  = EditorPrefs.GetString(CACHE_KEY_VER  + i, "");
            string cachedTimeStr = EditorPrefs.GetString(CACHE_KEY_TIME + i, "0");
            long.TryParse(cachedTimeStr, out long cachedTime);

            if (!string.IsNullOrEmpty(cachedLatest) && (GetUnixTime() - cachedTime) < CACHE_MAX_AGE)
            {
                ApplyVersionResult(state, CleanVersion(localVer ?? ""), cachedLatest);
                continue;
            }

            state.Status = DepStatus.Checking;
            int      idx   = i;
            int      delay = i * 2500;
            string[] apis  = DEPS[i].VersionAPIs;
            string   local = localVer;

            Task.Run(() =>
            {
                if (delay > 0) System.Threading.Thread.Sleep(delay);
                FetchLatestVersion(idx, apis, local);
            });
        }

        Repaint();
    }

    void FetchLatestVersion(int idx, string[] urls, string localVersion)
    {
        string latest = null;

        foreach (string url in urls)
        {
            string json = TryDownload(url);
            if (string.IsNullOrEmpty(json)) continue;
            var candidates = ExtractVersions(json);
            latest = HighestVersion(candidates);
            if (!string.IsNullOrEmpty(latest)) break;
        }

        if (string.IsNullOrEmpty(latest))
        {
            string stale = EditorPrefs.GetString(CACHE_KEY_VER + idx, "");
            EditorApplication.delayCall += () =>
            {
                if (depStates == null || idx >= depStates.Length) return;
                var st = depStates[idx];
                if (!string.IsNullOrEmpty(stale))
                    ApplyVersionResult(st, CleanVersion(localVersion ?? ""), stale);
                else
                    st.Status = DepStatus.VerifyFailed;
                Repaint();
            };
            return;
        }

        string localClean = CleanVersion(localVersion ?? "");
        EditorApplication.delayCall += () =>
        {
            if (depStates == null || idx >= depStates.Length) return;
            EditorPrefs.SetString(CACHE_KEY_VER  + idx, latest);
            EditorPrefs.SetString(CACHE_KEY_TIME + idx, GetUnixTime().ToString());
            ApplyVersionResult(depStates[idx], localClean, latest);
            Repaint();
        };
    }

    static void ApplyVersionResult(DepState state, string local, string latest)
    {
        state.LocalVersion  = local;
        state.LatestVersion = latest;

        if (string.IsNullOrEmpty(local)) { state.Status = DepStatus.UpToDate; return; }

        bool parsedLocal  = Version.TryParse(local,  out var vLocal);
        bool parsedLatest = Version.TryParse(latest, out var vLatest);

        bool upToDate = (parsedLocal && parsedLatest)
            ? vLocal >= vLatest
            : CompareVersionStrings(local, latest) >= 0;

        state.Status = upToDate ? DepStatus.UpToDate : DepStatus.Outdated;
    }

    // =========================================================================
    //  INTERNET / VERSION UTILITIES
    // =========================================================================

    void FetchClothingVersions()
    {
        Task.Run(() =>
        {
            string url = VERSIONS_URL + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string response = TryDownload(url);
            if (string.IsNullOrEmpty(response)) return;

            string json = response;
            var contentMatch = Regex.Match(response, "\"content\"\\s*:\\s*\"([^\"]+)\"");
            if (contentMatch.Success)
            {
                try
                {
                    string base64 = contentMatch.Groups[1].Value.Replace("\\n", "");
                    json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                }
                catch { }
            }

            var parsed = new Dictionary<string, string>();
            var matches = Regex.Matches(json, "\"([^\"]+)\"\\s*:\\s*\"([^\"]+)\"");
            foreach (Match m in matches)
            {
                string key = m.Groups[1].Value;
                if (key == "content" || key == "encoding" || key == "sha" ||
                    key == "name" || key == "path" || key == "url" ||
                    key == "git_url" || key == "html_url" || key == "download_url" ||
                    key == "type" || key == "node_id" || key == "size")
                    continue;
                parsed[key] = m.Groups[2].Value;
            }

            EditorApplication.delayCall += () =>
            {
                remoteVersions = parsed;
                clothingVersionsFetched = true;
                Repaint();
            };
        });
    }

    bool HasClothingUpdate(ClothingEntry item)
    {
        if (string.IsNullOrEmpty(item.localVersion)) return false;
        if (!clothingVersionsFetched) return false;

        string remoteVersion = null;
        foreach (var sibling in clothingItems)
        {
            if (sibling.markerFolder != item.markerFolder) continue;
            foreach (var kvp in remoteVersions)
            {
                if (string.Equals(kvp.Key.Trim(), sibling.displayName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    remoteVersion = kvp.Value;
                    break;
                }
            }
            if (remoteVersion != null) break;
        }
        if (remoteVersion == null) return false;

        return CompareVersions(remoteVersion.Trim(), item.localVersion.Trim()) > 0;
    }

    static int CompareVersions(string a, string b)
    {
        string[] partsA = a.Split('.');
        string[] partsB = b.Split('.');
        int len = Mathf.Max(partsA.Length, partsB.Length);
        for (int i = 0; i < len; i++)
        {
            int numA = 0, numB = 0;
            if (i < partsA.Length) int.TryParse(partsA[i].Trim(), out numA);
            if (i < partsB.Length) int.TryParse(partsB[i].Trim(), out numB);
            if (numA != numB) return numA.CompareTo(numB);
        }
        return 0;
    }

    static string TryDownload(string url)
    {
        try
        {
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12;
            System.Net.ServicePointManager.ServerCertificateValidationCallback =
                (sender, cert, chain, errors) => true;

            using (var client = new System.Net.WebClient())
            {
                client.Headers.Add("User-Agent",    "Matt3D-ClothingInstaller/1.0");
                client.Headers.Add("Accept",        "application/vnd.github+json");
                client.Headers.Add("Cache-Control", "no-cache, no-store");
                client.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                return client.DownloadString(url);
            }
        }
        catch (Exception ex)
        {
            return null;
        }
    }

    static string ReadPackageVersion(string packageId)
    {
        string root = Path.GetDirectoryName(Application.dataPath);

        string embedded = Path.Combine(root, "Packages", packageId, "package.json");
        if (File.Exists(embedded))
            return ExtractJsonVersion(File.ReadAllText(embedded));

        string cacheDir = Path.Combine(root, "Library", "PackageCache");
        if (Directory.Exists(cacheDir))
        {
            foreach (var dir in Directory.GetDirectories(cacheDir, packageId + "@*"))
            {
                string pkgJson = Path.Combine(dir, "package.json");
                if (File.Exists(pkgJson))
                    return ExtractJsonVersion(File.ReadAllText(pkgJson));
            }
        }

        return null;
    }

    static string CleanVersion(string v)
    {
        if (string.IsNullOrEmpty(v)) return v;
        v = v.TrimStart('v').Trim();
        int cut = v.IndexOfAny(new[] { '-', '+' });
        return cut >= 0 ? v.Substring(0, cut) : v;
    }

    static string ExtractJsonVersion(string json)
    {
        var m = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    static List<string> ExtractVersions(string json)
    {
        var list = new List<string>();

        foreach (Match m in Regex.Matches(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\""))
            list.Add(CleanVersion(m.Groups[1].Value));

        foreach (Match m in Regex.Matches(json, "\"name\"\\s*:\\s*\"([^\"]+)\""))
        {
            Match vn = Regex.Match(m.Groups[1].Value, @"(\d+\.\d+[\d.]*)");
            if (vn.Success) list.Add(CleanVersion(vn.Groups[1].Value));
        }

        foreach (Match m in Regex.Matches(json, "\"(\\d+\\.\\d+\\.\\d+(?:\\.\\d+)?)\"\\s*:\\s*\\{"))
            list.Add(m.Groups[1].Value);

        var vm = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
        if (vm.Success) list.Add(CleanVersion(vm.Groups[1].Value));

        return list;
    }

    static string HighestVersion(List<string> versions)
    {
        string  bestRaw    = null;
        Version bestParsed = null;

        foreach (var v in versions)
        {
            if (string.IsNullOrEmpty(v)) continue;
            if (!Regex.IsMatch(v, @"^\d+\.\d+")) continue;

            if (Version.TryParse(v, out var parsed))
            {
                if (bestParsed == null || parsed > bestParsed)
                    { bestParsed = parsed; bestRaw = v; }
            }
            else if (bestParsed == null)
            {
                if (bestRaw == null || CompareVersionStrings(v, bestRaw) > 0) bestRaw = v;
            }
        }

        return bestRaw;
    }

    static int CompareVersionStrings(string a, string b)
    {
        var pa  = a.Split('.');
        var pb  = b.Split('.');
        int len = Mathf.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int na = i < pa.Length && int.TryParse(pa[i], out int x) ? x : 0;
            int nb = i < pb.Length && int.TryParse(pb[i], out int y) ? y : 0;
            if (na != nb) return na.CompareTo(nb);
        }
        return 0;
    }

    static long GetUnixTime() =>
        (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;

    void DrawBottomButtons()
    {
        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        GUILayout.Space(8);
        if (GUILayout.Button("Visit Matt3D Shop ↗", GUILayout.Height(28)))
            Application.OpenURL(SHOP_URL);
        GUILayout.Space(6);
        if (GUILayout.Button("Join Discord ↗", GUILayout.Height(28)))
            Application.OpenURL(DISCORD_URL);
        GUILayout.Space(8);
        GUILayout.EndHorizontal();
        GUILayout.Space(4);
    }

    void DrawFooter()
    {
        DrawDivider();
        GUILayout.Label("© Matt3D — All rights reserved", EditorStyles.centeredGreyMiniLabel);
        GUILayout.Space(6);
    }

    // =========================================================================
    //  MAGNIFYING GLASS ICON
    // =========================================================================

    static Texture2D GenerateMagnifyingGlass(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
        tex.filterMode = FilterMode.Bilinear;
        var pixels = new Color[size * size];

        float cx = size * 0.42f;
        float cy = size * 0.42f;
        float radius = size * 0.25f;
        float ringThick = size * 0.06f;
        float handleThick = size * 0.07f;
        float handleAngle = Mathf.PI * 0.25f;
        float handleStart = radius + ringThick * 0.3f;
        float handleEnd = handleStart + size * 0.25f;
        float hdx = Mathf.Cos(handleAngle);
        float hdy = Mathf.Sin(handleAngle);
        Color col = new Color(1f, 1f, 1f, 0.92f);

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float px = x + 0.5f;
            float py = (size - 1 - y) + 0.5f;

            float dist = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            float ringDist = Mathf.Abs(dist - radius) - ringThick * 0.5f;
            float ringAlpha = Mathf.Clamp01(1f - ringDist);

            float proj = (px - cx) * hdx + (py - cy) * hdy;
            proj = Mathf.Clamp(proj, handleStart, handleEnd);
            float hClosestX = cx + hdx * proj;
            float hClosestY = cy + hdy * proj;
            float hDist = Mathf.Sqrt((px - hClosestX) * (px - hClosestX) + (py - hClosestY) * (py - hClosestY));
            float handleAlpha = Mathf.Clamp01(1f - (hDist - handleThick * 0.5f));

            float a = Mathf.Max(ringAlpha, handleAlpha);
            pixels[y * size + x] = a > 0.01f ? new Color(col.r, col.g, col.b, col.a * a) : Color.clear;
        }

        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    // =========================================================================
    //  ROUNDED BUTTON TEXTURE
    // =========================================================================

    static Texture2D RoundedRect(int w, int h, int radius, Color color)
    {
        var tex    = new Texture2D(w, h, TextureFormat.ARGB32, false);
        tex.filterMode = FilterMode.Bilinear;
        var pixels = new Color[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float a = RoundedAlpha(x, y, w, h, radius);
            pixels[y * w + x] = new Color(color.r, color.g, color.b, color.a * a);
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    static float RoundedAlpha(int px, int py, int w, int h, int r)
    {
        int cx = -1, cy = -1;
        if      (px < r      && py < r)      { cx = r;     cy = r;     }
        else if (px >= w - r && py < r)      { cx = w-r-1; cy = r;     }
        else if (px < r      && py >= h - r) { cx = r;     cy = h-r-1; }
        else if (px >= w - r && py >= h - r) { cx = w-r-1; cy = h-r-1; }
        if (cx < 0) return 1f;
        float dist = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        return Mathf.Clamp01(r - dist + 0.5f);
    }
}
