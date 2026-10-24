using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WebCompanion
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

        // Page zoom as a power of two: -4 is 1/16x (whole map), 3 is 8x
        public const int MinZoom = -4;
        public const int MaxZoom = 3;

        // "self", "free" or a player's entity id
        public static string Follow { get; private set; } = FollowSelf;
        public static bool ShowPois { get; private set; } = true;
        public static int PoiMinTier { get; private set; } = 1;
        public static int Zoom { get; private set; }

        // Bumped on every change, so the panel knows to refresh
        public static int Version { get; private set; }

        private static readonly HashSet<string> hidden = new HashSet<string>();
        private static bool loaded;

        private static string FilePath => Path.Combine(GameIO.GetUserGameDataDir(), "WebCompanion.txt");

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
            _zoom = Math.Max(MinZoom, Math.Min(MaxZoom, _zoom));
            if (_zoom == Zoom) return;
            Zoom = _zoom;
            Changed(true);
        }

        public static string ZoomName(int _zoom)
        {
            return _zoom >= 0 ? (1 << _zoom) + "x" : "1/" + (1 << -_zoom) + "x";
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
                if (File.Exists(FilePath))
                {
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        string key = line.Substring(0, eq).Trim();
                        string value = line.Substring(eq + 1).Trim();
                        int n;
                        if (key == "hidden")
                        {
                            foreach (string c in value.Split(','))
                            {
                                if (Array.IndexOf(Categories, c) >= 0) hidden.Add(c);
                            }
                        }
                        else if (key == "pois") ShowPois = value != "false";
                        else if (key == "poiMinTier" && int.TryParse(value, out n)) PoiMinTier = Math.Max(0, Math.Min(MaxTier, n));
                        else if (key == "zoom" && int.TryParse(value, out n)) Zoom = Math.Max(MinZoom, Math.Min(MaxZoom, n));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[WebCompanion] Could not read " + FilePath + ": " + ex.Message);
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
                File.WriteAllText(FilePath,
                    "hidden=" + string.Join(",", hidden) + "\n" +
                    "pois=" + (ShowPois ? "true" : "false") + "\n" +
                    "poiMinTier=" + PoiMinTier + "\n" +
                    "zoom=" + Zoom + "\n");
            }
            catch (Exception ex)
            {
                Log.Warning("[WebCompanion] Could not save " + FilePath + ": " + ex.Message);
            }
        }

        // {"follow":"self"|"free"|entityId,"hide":[...],"pois":bool,"poiMinTier":n,"zoom":n}
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
            sb.Append(",\"zoom\":").Append(Zoom).Append('}');
            return sb.ToString();
        }
    }
}
