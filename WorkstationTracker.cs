using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace WebMap
{
    // Server side of the workstations sidebar. Every 2 s, for each player whose web map is open (subscribed),
    // looks at the loaded chunks inside their own and their party's land claims, snapshots the workstations
    // there, and sends that player only what changed. Workstations in unloaded chunks keep their last snapshot:
    // the game doesn't run them while unloaded either (it catches up on load), so the page projects timers
    // forward from it. Nothing runs while nobody is subscribed.
    public static class WorkstationTracker
    {
        private const float Interval = 2f;
        private const float RefreshAfter = 30f;  // resend unchanged ones this often, so drift in projected timers is corrected

        // Vanilla workstation blocks; anything else is grouped as "other" on the page's filters
        public static readonly string[] Types = { "campfire", "forge", "workbench", "combineStation", "cementMixer", "chemistryStation", "tableSaw" };

        private class Snap
        {
            public Vector3i Pos;
            public long ChunkKey;
            public string Body;     // JSON without the time-dependent "age"
            public string Shape;    // what counts as a change: queue items and counts, burning, fuel and output counts
            public ulong LastTick;  // the workstation's lastTickTime: how far the game has run it
        }

        private class Subscriber
        {
            public readonly Dictionary<Vector3i, string> Sent = new Dictionary<Vector3i, string>();  // pos -> Shape sent
            public float LastFull = -999f;
        }

        private static readonly Dictionary<Vector3i, Snap> snaps = new Dictionary<Vector3i, Snap>();
        private static readonly Dictionary<int, Subscriber> subscribers = new Dictionary<int, Subscriber>();
        private static float nextTick;

        // Scratch, reused every tick
        private static readonly List<Vector3i> claims = new List<Vector3i>();
        private static readonly HashSet<long> chunkKeys = new HashSet<long>();
        private static readonly HashSet<Vector3i> found = new HashSet<Vector3i>();
        private static readonly HashSet<Vector3i> visible = new HashSet<Vector3i>();
        private static readonly List<Vector3i> scratch = new List<Vector3i>();
        private static readonly List<string> setJson = new List<string>();
        private static readonly List<string> delIds = new List<string>();
        private static readonly List<int> scratchIds = new List<int>();
        private static readonly StringBuilder sb = new StringBuilder();

        public static void SetSubscribed(int _entityId, bool _on)
        {
            if (_on)
            {
                if (!subscribers.ContainsKey(_entityId)) subscribers.Add(_entityId, new Subscriber());
                nextTick = 0f;  // send the first list right away
            }
            else
            {
                subscribers.Remove(_entityId);
            }
        }

        public static void Reset()
        {
            snaps.Clear();
            subscribers.Clear();
        }

        // Main thread, every frame on the server (and in single player / when hosting)
        public static void Tick()
        {
            if (subscribers.Count == 0) return;
            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + Interval;

            GameManager gm = GameManager.Instance;
            World world = gm != null ? gm.World : null;
            if (world == null || gm.persistentPlayers == null || GameTimer.Instance == null) return;

            scratchIds.Clear();
            foreach (int id in subscribers.Keys) scratchIds.Add(id);
            for (int i = 0; i < scratchIds.Count; i++)
            {
                EntityPlayer player = world.GetEntity(scratchIds[i]) as EntityPlayer;
                if (player == null)
                {
                    // Left the game
                    subscribers.Remove(scratchIds[i]);
                    continue;
                }
                UpdateFor(world, gm.persistentPlayers, player, subscribers[scratchIds[i]]);
            }
        }

        private static void UpdateFor(World _world, PersistentPlayerList _players, EntityPlayer _player, Subscriber _sub)
        {
            // Claims of this player and everyone in their party
            claims.Clear();
            AddClaims(_players, _player.entityId);
            if (_player.Party != null)
            {
                List<EntityPlayer> members = _player.Party.MemberList;
                for (int i = 0; i < members.Count; i++)
                {
                    if (members[i] != null && members[i].entityId != _player.entityId) AddClaims(_players, members[i].entityId);
                }
            }
            int half = (GameStats.GetInt(EnumGameStats.LandClaimSize) - 1) / 2;

            chunkKeys.Clear();
            for (int i = 0; i < claims.Count; i++)
            {
                Vector3i c = claims[i];
                for (int cx = (c.x - half) >> 4; cx <= (c.x + half) >> 4; cx++)
                {
                    for (int cz = (c.z - half) >> 4; cz <= (c.z + half) >> 4; cz++) chunkKeys.Add(WorldChunkCache.MakeChunkKey(cx, cz));
                }
            }

            // Snapshot every workstation in the loaded ones; forget cached ones that are gone from a loaded chunk
            ulong now = GameTimer.Instance.ticks;
            visible.Clear();
            foreach (long key in chunkKeys)
            {
                Chunk chunk = _world.GetChunkSync(key) as Chunk;
                if (chunk == null)
                {
                    // Not loaded: whatever we last saw there still stands
                    foreach (Snap s in snaps.Values)
                    {
                        if (s.ChunkKey == key && InClaims(s.Pos, half)) visible.Add(s.Pos);
                    }
                    continue;
                }
                found.Clear();
                List<TileEntity> list = chunk.GetTileEntities().list;
                for (int i = 0; i < list.Count; i++)
                {
                    TileEntityWorkstation ws = list[i] as TileEntityWorkstation;
                    if (ws == null || !ws.IsPlayerPlaced) continue;
                    Vector3i pos = ws.ToWorldPos();
                    found.Add(pos);
                    if (!InClaims(pos, half)) continue;
                    Snapshot(_world, ws, pos, key);
                    visible.Add(pos);
                }
                scratch.Clear();
                foreach (Snap s in snaps.Values)
                {
                    if (s.ChunkKey == key && !found.Contains(s.Pos)) scratch.Add(s.Pos);
                }
                for (int i = 0; i < scratch.Count; i++) snaps.Remove(scratch[i]);
            }

            // Diff against what this player has
            bool full = Time.unscaledTime - _sub.LastFull >= RefreshAfter;
            if (full) _sub.LastFull = Time.unscaledTime;
            setJson.Clear();
            delIds.Clear();
            foreach (Vector3i pos in visible)
            {
                Snap s = snaps[pos];
                string sent;
                if (!full && _sub.Sent.TryGetValue(pos, out sent) && sent == s.Shape) continue;
                _sub.Sent[pos] = s.Shape;
                float age = s.LastTick == 0 || now < s.LastTick ? 0f : (now - s.LastTick) / 20f;
                setJson.Add(s.Body + ",\"age\":" + age.ToString("0.##", CultureInfo.InvariantCulture) +
                            ",\"live\":" + (_world.GetChunkSync(s.ChunkKey) != null ? "true" : "false") + "}");
            }
            scratch.Clear();
            foreach (Vector3i pos in _sub.Sent.Keys)
            {
                if (!visible.Contains(pos)) scratch.Add(pos);
            }
            for (int i = 0; i < scratch.Count; i++)
            {
                _sub.Sent.Remove(scratch[i]);
                delIds.Add(Id(scratch[i]));
            }
            if (!full && setJson.Count == 0 && delIds.Count == 0) return;
            Send(_world, _player.entityId, full, setJson, delIds);
        }

        private static void AddClaims(PersistentPlayerList _players, int _entityId)
        {
            PersistentPlayerData data = _players.GetPlayerDataFromEntityID(_entityId);
            if (data != null && data.LPBlocks != null) claims.AddRange(data.LPBlocks);
        }

        private static bool InClaims(Vector3i _pos, int _half)
        {
            for (int i = 0; i < claims.Count; i++)
            {
                if (Mathf.Abs(_pos.x - claims[i].x) <= _half && Mathf.Abs(_pos.z - claims[i].z) <= _half) return true;
            }
            return false;
        }

        private static string Id(Vector3i _pos)
        {
            return _pos.x + "," + _pos.y + "," + _pos.z;
        }

        private static void Snapshot(World _world, TileEntityWorkstation _ws, Vector3i _pos, long _chunkKey)
        {
            Snap s;
            if (!snaps.TryGetValue(_pos, out s))
            {
                s = new Snap { Pos = _pos, ChunkKey = _chunkKey };
                snaps.Add(_pos, s);
            }
            s.LastTick = _ws.lastTickTime;

            Block block = _world.GetBlock(_pos).Block;
            string type = block.GetBlockName();
            bool usesFuel = _ws.isModuleUsed != null && _ws.isModuleUsed.Length > 3 && _ws.isModuleUsed[3];

            // Shape: the parts whose change is worth sending; timers are projected on the page between sends
            StringBuilder shape = new StringBuilder();
            shape.Append(_ws.isBurning ? 'B' : '-').Append(_ws.bUserAccessing ? 'U' : '-');

            sb.Length = 0;
            sb.Append("{\"id\":\"").Append(Id(_pos)).Append("\",\"x\":").Append(_pos.x).Append(",\"z\":").Append(_pos.z);
            sb.Append(",\"type\":").Append(Json(type));
            sb.Append(",\"name\":").Append(Json(block.GetLocalizedBlockName()));
            sb.Append(",\"icon\":").Append(Json(block.GetIconName()));
            sb.Append(",\"usesFuel\":").Append(usesFuel ? "true" : "false");
            sb.Append(",\"burning\":").Append(_ws.isBurning ? "true" : "false");
            sb.Append(",\"paused\":").Append(_ws.bUserAccessing ? "true" : "false");
            if (usesFuel)
            {
                float fuel = _ws.getTotalFuelSeconds();
                sb.Append(",\"burnLeft\":").Append(F(_ws.currentBurnTimeLeft)).Append(",\"fuel\":").Append(F(fuel));
                shape.Append('F').Append((int)fuel);
            }

            // Queue, the item being crafted first (the game crafts from the end of the array)
            sb.Append(",\"queue\":[");
            bool first = true;
            RecipeQueueItem[] queue = _ws.queue;
            for (int i = queue != null ? queue.Length - 1 : -1; i >= 0; i--)
            {
                RecipeQueueItem q = queue[i];
                if (q == null || q.Recipe == null || q.Multiplier <= 0) continue;
                ItemClass item = ItemClass.GetForId(q.Recipe.itemValueType);
                string itemName = item != null ? item.GetLocalizedItemName() : q.Recipe.GetName();
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(Json(itemName));
                sb.Append(",\"icon\":").Append(Json(item != null ? item.GetIconName() : q.Recipe.GetName()));
                sb.Append(",\"count\":").Append(q.Multiplier);
                sb.Append(",\"per\":").Append(q.Recipe.count);
                sb.Append(",\"left\":").Append(F(q.CraftingTimeLeft));
                sb.Append(",\"each\":").Append(F(q.OneItemCraftTime)).Append('}');
                shape.Append('Q').Append(q.Recipe.itemValueType).Append('x').Append(q.Multiplier);
            }
            sb.Append(']');

            // Output waiting to be taken
            int outItems = 0, outSlots = 0;
            ItemStack[] output = _ws.output;
            if (output != null)
            {
                for (int i = 0; i < output.Length; i++)
                {
                    if (output[i] == null || output[i].IsEmpty()) continue;
                    outSlots++;
                    outItems += output[i].count;
                }
            }
            sb.Append(",\"out\":").Append(outItems).Append(",\"outSlots\":").Append(outSlots)
              .Append(",\"outMax\":").Append(output != null ? output.Length : 0);
            shape.Append('O').Append(outItems);

            s.Body = sb.ToString();
            s.Shape = shape.ToString();
        }

        private static void Send(World _world, int _entityId, bool _full, List<string> _set, List<string> _del)
        {
            // The local player (single player or hosting) gets it directly
            EntityPlayerLocal local = _world.GetPrimaryPlayer();
            if (local != null && local.entityId == _entityId)
            {
                WorkstationClient.Receive(_full, _set, _del);
                return;
            }
            NetPackageWebMap pkg = NetPackageManager.GetPackage<NetPackageWebMap>().SetupWorkstations(_full, _set, _del);
            SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(pkg, false, _entityId);
        }

        private static string F(float _v)
        {
            return _v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Json(string _s)
        {
            if (_s == null) return "\"\"";
            StringBuilder o = new StringBuilder("\"");
            foreach (char c in _s)
            {
                if (c == '"' || c == '\\') o.Append('\\').Append(c);
                else if (c < ' ') o.Append(' ');
                else o.Append(c);
            }
            return o.Append('"').ToString();
        }
    }
}
