using System;
using System.Collections.Generic;
using UnityEngine;

namespace WebMap
{
    // Mirrors the markers the in-game map draws (waypoints, bedroll, land claims, backpack, quests, vehicles,
    // party members, traders...). They all come from NavObjectManager.NavObjectList, filtered and styled the
    // same way as XUiC_MapArea.updateNavObjectList. Main thread; only changes are posted to WebHost.
    public static class NavMirror
    {
        private class Snap
        {
            public float X, Z, Rot, ScaleX, ScaleY;
            public string Sprite, Label;
            public int Color, Layer;
            public bool AdjustCenter;
            public bool Seen;
        }

        private static readonly Dictionary<int, Snap> snaps = new Dictionary<int, Snap>();
        private static readonly List<int> gone = new List<int>();
        private static bool loggedError;

        // Forget what was sent, so everything is posted again (after WebHost cleared its copy)
        public static void Clear()
        {
            snaps.Clear();
        }

        public static void Update(EntityPlayerLocal _player)
        {
            NavObjectManager mgr = NavObjectManager.Instance;
            if (mgr == null) return;

            foreach (Snap s in snaps.Values) s.Seen = false;

            List<NavObject> list = mgr.NavObjectList;
            for (int i = 0; i < list.Count; i++)
            {
                NavObject nav = list[i];
                try
                {
                    UpdateOne(nav, _player);
                }
                catch (Exception ex)
                {
                    // A marker whose tracked object just got destroyed; skip it this round
                    if (loggedError) continue;
                    loggedError = true;
                    Log.Warning("[WebMap] Skipping map marker " + (nav != null ? nav.Key.ToString() : "null") + ": " + ex.Message);
                }
            }

            foreach (KeyValuePair<int, Snap> kv in snaps)
            {
                if (!kv.Value.Seen) gone.Add(kv.Key);
            }
            for (int i = 0; i < gone.Count; i++)
            {
                snaps.Remove(gone[i]);
                WebHost.PostNavRemove(gone[i]);
            }
            gone.Clear();
        }

        private static void UpdateOne(NavObject _nav, EntityPlayerLocal _player)
        {
            if (_nav == null || _nav.hiddenOnMap) return;
            NavObjectMapSettings settings = _nav.CurrentMapSettings;
            if (settings == null) return;
            // The page draws the local player itself, with smooth movement
            if (_nav.TrackType == NavObject.TrackTypes.Entity &&
                (_nav.TrackedEntity == null || _nav.TrackedEntity.entityId == _player.entityId)) return;
            if (_nav.TrackType == NavObject.TrackTypes.Transform && _nav.TrackedTransform == null) return;

            Vector3 pos = _nav.GetPosition();
            if (pos == NavObject.InvalidPos) return;
            pos += Origin.position;

            string sprite = _nav.GetSpriteName(settings);
            if (string.IsNullOrEmpty(sprite)) return;
            EntityPlayer trackedPlayer = _nav.TrackedEntity as EntityPlayer;
            string label = trackedPlayer != null ? trackedPlayer.PlayerDisplayName : _nav.DisplayName;
            int entityId = trackedPlayer != null ? trackedPlayer.entityId : -1;
            Color32 c = _nav.hiddenOnCompass ? (Color32)Color.grey : (Color32)(_nav.UseOverrideColor ? _nav.OverrideColor : settings.Color);
            int color = (c.r << 24) | (c.g << 16) | (c.b << 8) | c.a;
            float rot = _nav.Rotation.y;

            Snap s;
            bool changed = false;
            if (!snaps.TryGetValue(_nav.Key, out s))
            {
                s = new Snap();
                snaps.Add(_nav.Key, s);
                changed = true;
            }
            else if (Mathf.Abs(s.X - pos.x) > 0.05f || Mathf.Abs(s.Z - pos.z) > 0.05f || Mathf.Abs(s.Rot - rot) > 0.5f ||
                     !string.Equals(s.Sprite, sprite) || !string.Equals(s.Label, label) || s.Color != color ||
                     s.ScaleX != settings.IconScaleVector.x || s.ScaleY != settings.IconScaleVector.y ||
                     s.Layer != settings.Layer || s.AdjustCenter != settings.AdjustCenter)
            {
                changed = true;
            }
            s.Seen = true;
            if (!changed) return;

            s.X = pos.x;
            s.Z = pos.z;
            s.Rot = rot;
            s.Sprite = sprite;
            s.Label = label;
            s.Color = color;
            s.ScaleX = settings.IconScaleVector.x;
            s.ScaleY = settings.IconScaleVector.y;
            s.Layer = settings.Layer;
            s.AdjustCenter = settings.AdjustCenter;
            WebHost.PostNav(_nav.Key, pos.x, pos.z, rot, sprite, label, color, s.ScaleX, s.ScaleY, s.Layer, s.AdjustCenter,
                CategoryOf(_nav), entityId);
        }

        // Groups the game's nav object classes (Data/Config/nav_objects.xml) for the show/hide toggles
        public static string CategoryOf(NavObject _nav)
        {
            if (_nav.TrackedEntity is EntityPlayer) return "players";
            string name = _nav.NavObjectClass != null ? _nav.NavObjectClass.NavObjectClassName : "";
            switch (name)
            {
                case "player":
                case "ally":
                case "party":
                    return "players";
                case "waypoint":
                case "quick_waypoint":
                case "waypoint_invite":
                    return "waypoints";
                case "land_claim":
                case "sleeping_bag":
                    return "base";
                case "backpack":
                case "backpack_distant":
                case "supply_drop":
                case "spear":
                case "treasure":
                case "treasure_direct":
                    return "loot";
                case "bicycle":
                case "minibike":
                case "motorcycle":
                case "4x4":
                case "gyrocopter":
                case "raft":
                case "NOJunkDrone":
                case "junk_turret":
                case "junk_sledge":
                    return "vehicles";
                case "trader":
                case "vending_machine":
                case "rally":
                case "fetch_container":
                case "restore_power":
                case "clear_sleeper":
                case "sleeper_volume":
                    return "quests";
                case "zombie":
                    return "creatures";
            }
            if (name.StartsWith("quest") || name.EndsWith("_trader")) return "quests";
            if (name.StartsWith("animaltracking")) return "creatures";
            return "other";
        }
    }
}
