using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WebMap
{
    // What the companion page shows: who it follows, which marker kinds are hidden, POI labels.
    // Changed from the in-game panel (map screen) or the page; main thread only. Every change goes to the page.
    // Everything except the follow target is saved between sessions.
    public static class CompanionSettings
    {
        public const string FollowSelf = "self";
        public const string FollowFree = "free";

        // Marker categories (NavMirror.CategoryOf) and their names in the panel
        public static readonly string[] Categories = { "players", "waypoints", "base", "loot", "vehicles", "quests", "creatures", "other" };
        public static readonly string[] CategoryNames = { "Players", "Waypoints", "Bedroll & land claims", "Backpack & loot", "Vehicles", "Quests & traders", "Animals & zombies", "Other" };

        public const int MaxTier = 5;

        // Page zoom steps, screen pixels per block: 1/16 shows the whole map. The page's wheel and buttons use the same steps.
        public static readonly double[] ZoomSteps = { 0.0625, 0.125, 0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6, 8 };
        private const int DefaultZoom = 5;  // 1x

        // Text and icon sizes on the page, in percent
        public static readonly string[] SizeKeys = { "poi", "marker", "player", "icons" };
        public static readonly string[] SizeNames = { "POI labels", "Marker labels", "Your name", "Icons" };
        public static readonly int[] SizeSteps = { 75, 100, 125, 150, 200 };
        private static readonly int[] sizes = { 100, 100, 100, 100 };

        // "self", "free" or a player's entity id
        public static string Follow { get; private set; } = FollowSelf;
        public static bool ShowPois { get; private set; } = true;
        public static int PoiMinTier { get; private set; } = 1;
        // Index into ZoomSteps
        public static int Zoom { get; private set; } = DefaultZoom;

        // Workstations sidebar: shown at all, only active ones, and which types (WorkstationTracker.Types plus "other") are hidden
        public static bool ShowWorkstations { get; private set; } = true;
        public static bool WorkstationsActiveOnly { get; private set; }
        public const string OtherWorkstation = "other";
        private static readonly HashSet<string> hiddenWorkstations = new HashSet<string>();

        // Bumped on every change, so the panel knows to refresh
        public static int Version { get; private set; }

        private static readonly HashSet<string> hidden = new HashSet<string>();
        private static bool loaded;

        private static string FilePath => Path.Combine(GameIO.GetUserGameDataDir(), "WebMap.txt");
        // Settings file from before the mod was renamed, read when there is no new one yet
        private static string OldFilePath => Path.Combine(GameIO.GetUserGameDataDir(), "WebCompanion.txt");

        public static bool IsHidden(string _category)
        {
            return hidden.Contains(_category);
        }

        public static void SetFollow(string _follow)
        {
            if (_follow != FollowSelf && _follow != FollowFree)
            {
                int id;
                if (!int.TryParse(_follow, out id)) return;
                _follow = id.ToString();
            }
            if (_follow == Follow) return;
            Follow = _follow;
            Changed(false);
        }

        public static void SetHidden(string _category, bool _hide)
        {
            if (Array.IndexOf(Categories, _category) < 0) return;
            if (_hide ? !hidden.Add(_category) : !hidden.Remove(_category)) return;
            Changed(true);
        }

        public static bool IsWorkstationHidden(string _type)
        {
            return hiddenWorkstations.Contains(_type);
        }

        public static void SetShowWorkstations(bool _show)
        {
            if (_show == ShowWorkstations) return;
            ShowWorkstations = _show;
            Changed(true);
        }

        public static void SetWorkstationsActiveOnly(bool _activeOnly)
        {
            if (_activeOnly == WorkstationsActiveOnly) return;
            WorkstationsActiveOnly = _activeOnly;
            Changed(true);
        }

        public static void SetWorkstationHidden(string _type, bool _hide)
        {
            if (_type != OtherWorkstation && Array.IndexOf(WorkstationTracker.Types, _type) < 0) return;
            if (_hide ? !hiddenWorkstations.Add(_type) : !hiddenWorkstations.Remove(_type)) return;
            Changed(true);
        }

        public static void SetShowPois(bool _show)
        {
            if (_show == ShowPois) return;
            ShowPois = _show;
            Changed(true);
        }

        public static void SetPoiMinTier(int _tier)
        {
            _tier = Math.Max(0, Math.Min(MaxTier, _tier));
            if (_tier == PoiMinTier) return;
            PoiMinTier = _tier;
            Changed(true);
        }

        public static void SetZoom(int _zoom)
        {
            _zoom = Math.Max(0, Math.Min(ZoomSteps.Length - 1, _zoom));
            if (_zoom == Zoom) return;
            Zoom = _zoom;
            Changed(true);
        }

        public static int GetSize(int _index)
        {
            return sizes[_index];
        }

        public static void SetSize(int _index, int _percent)
        {
            if (_index < 0 || _index >= sizes.Length || Array.IndexOf(SizeSteps, _percent) < 0) return;
            if (sizes[_index] == _percent) return;
            sizes[_index] = _percent;
            Changed(true);
        }

        // The step closest to a scale
        public static int NearestZoom(double _scale)
        {
            int best = DefaultZoom;
            for (int i = 0; i < ZoomSteps.Length; i++)
            {
                if (Math.Abs(Math.Log(ZoomSteps[i] / _scale)) < Math.Abs(Math.Log(ZoomSteps[best] / _scale))) best = i;
            }
            return best;
        }

        public static string ZoomName(int _zoom)
        {
            double s = ZoomSteps[_zoom];
            double inverse = 1 / s;
            if (s < 1 && inverse == Math.Floor(inverse)) return "1/" + inverse + "x";
            return s.ToString(CultureInfo.InvariantCulture) + "x";
        }

        // A new world: follow yourself again (entity ids don't carry over)
        public static void ResetFollow()
        {
            if (Follow == FollowSelf) return;
            Follow = FollowSelf;
            Changed(false);
        }

        // Loads saved settings and sends them to the page
        public static void Load()
        {
            loaded = true;
            try
            {
                string path = File.Exists(FilePath) ? FilePath : OldFilePath;
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path))
                    {
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        string key = line.Substring(0, eq).Trim();
                        string value = line.Substring(eq + 1).Trim();
                        int n;
                        double scale;
                        if (key == "hidden")
                        {
                            foreach (string c in value.Split(','))
                            {
                                if (Array.IndexOf(Categories, c) >= 0) hidden.Add(c);
                            }
                        }
                        else if (key == "pois") ShowPois = value != "false";
                        else if (key == "workstations") ShowWorkstations = value != "false";
                        else if (key == "workstationsActiveOnly") WorkstationsActiveOnly = value == "true";
                        else if (key == "workstationsHidden")
                        {
                            foreach (string t in value.Split(','))
                            {
                                if (t == OtherWorkstation || Array.IndexOf(WorkstationTracker.Types, t) >= 0) hiddenWorkstations.Add(t);
                            }
                        }
                        else if (key == "poiMinTier" && int.TryParse(value, out n)) PoiMinTier = Math.Max(0, Math.Min(MaxTier, n));
                        else if (key == "zoomScale" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) && scale > 0) Zoom = NearestZoom(scale);
                        // Older settings saved zoom as a power of two
                        else if (key == "zoom" && int.TryParse(value, out n)) Zoom = NearestZoom(Math.Pow(2, n));
                        else if (key.StartsWith("size_") && int.TryParse(value, out n) && Array.IndexOf(SizeSteps, n) >= 0)
                        {
                            int i = Array.IndexOf(SizeKeys, key.Substring(5));
                            if (i >= 0) sizes[i] = n;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[WebMap] Could not read " + FilePath + ": " + ex.Message);
            }
            Version++;
            WebHost.PostView(ToJson());
        }

        private static void Changed(bool _save)
        {
            Version++;
            WebHost.PostView(ToJson());
            if (_save && loaded) Save();
        }

        private static void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < SizeKeys.Length; i++) sb.Append("size_").Append(SizeKeys[i]).Append('=').Append(sizes[i]).Append('\n');
                File.WriteAllText(FilePath,
                    "hidden=" + string.Join(",", hidden) + "\n" +
                    "pois=" + (ShowPois ? "true" : "false") + "\n" +
                    "poiMinTier=" + PoiMinTier + "\n" +
                    "workstations=" + (ShowWorkstations ? "true" : "false") + "\n" +
                    "workstationsActiveOnly=" + (WorkstationsActiveOnly ? "true" : "false") + "\n" +
                    "workstationsHidden=" + string.Join(",", hiddenWorkstations) + "\n" +
                    "zoomScale=" + ZoomSteps[Zoom].ToString(CultureInfo.InvariantCulture) + "\n" + sb);
            }
            catch (Exception ex)
            {
                Log.Warning("[WebMap] Could not save " + FilePath + ": " + ex.Message);
            }
        }

        // {"follow":"self"|"free"|entityId,"hide":[...],"pois":bool,"poiMinTier":n,"zoom":scale,"sizes":{"poi":100,...},
        //  "ws":{"show":bool,"active":bool,"hide":[type,...]}}
        private static string ToJson()
        {
            StringBuilder sb = new StringBuilder("{\"follow\":");
            sb.Append(Follow == FollowSelf || Follow == FollowFree ? "\"" + Follow + "\"" : Follow);
            sb.Append(",\"hide\":[");
            bool first = true;
            foreach (string c in hidden)
            {
                if (!first) sb.Append(',');
                sb.Append('"').Append(c).Append('"');
                first = false;
            }
            sb.Append("],\"pois\":").Append(ShowPois ? "true" : "false");
            sb.Append(",\"poiMinTier\":").Append(PoiMinTier);
            sb.Append(",\"zoom\":").Append(ZoomSteps[Zoom].ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"sizes\":{");
            for (int i = 0; i < SizeKeys.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(SizeKeys[i]).Append("\":").Append(sizes[i]);
            }
            sb.Append("},\"ws\":{\"show\":").Append(ShowWorkstations ? "true" : "false");
            sb.Append(",\"active\":").Append(WorkstationsActiveOnly ? "true" : "false").Append(",\"hide\":[");
            first = true;
            foreach (string t in hiddenWorkstations)
            {
                if (!first) sb.Append(',');
                sb.Append('"').Append(t).Append('"');
                first = false;
            }
            sb.Append("]}}");
            return sb.ToString();
        }
    }
}
