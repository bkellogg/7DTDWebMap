using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace WebCompanion
{
    // Everything the mod does on the game's main thread. It only copies data and hands it to WebHost;
    // building responses, comparing and sending all happen on WebHost's threads.
    public static class Companion
    {
        private const float TickInterval = 0.2f;   // map updates at 5 Hz
        private const float PositionInterval = 1f / 60f; // up to 60 Hz: an enqueue each, and the page eases over ~1 interval, so faster means less trailing
        private const float NavInterval = 0.1f;       // map markers at 10 Hz
        private const int ChunkBudget = 512;       // changed chunks copied per tick, at most
        private const int SyncSlotBudget = 2048;   // map slots scanned per frame during the initial copy

        private enum SyncState { None, Running, Done }

        public const int StateMenu = 0;
        public const int StateLoading = 1;
        public const int StateInGame = 2;
        private static int postedState = -1;

        private static float nextTick;
        private static float nextPosTick;
        private static float nextNavTick;
        private static SyncState sync;
        private static bool poisPosted;

        // Initial copy of a region-based map (the game's default)
        private static List<Vector2i> syncRegionPos;
        private static List<MapChunkDatabaseByRegion.RegionData> syncRegions;
        private static int syncRegionIndex;
        private static int syncOffset;

        // Initial copy of a fixed-size map database
        private static MapChunkDatabase syncFixed;
        private static int[] syncKeys;
        private static int syncKeyIndex;

        private static int syncCopied;
        private static readonly List<long> unprocessed = new List<long>();

        private static float lastX = float.NaN, lastY, lastZ, lastYaw;

        // Main-thread cost, measured only on frames where the mod did any work
        private static long statFrames, statTicks, statMaxTicks;

        public static string SyncStatus
        {
            get
            {
                if (sync == SyncState.Running) return "copying (" + syncCopied + " chunks so far)";
                if (sync == SyncState.Done) return "done (" + syncCopied + " chunks)";
                return "not started";
            }
        }

        public static void Tick()
        {
            // Idle: no browser connected, nothing to do
            if (WebHost.ClientCount == 0) return;

            long start = Stopwatch.GetTimestamp();
            TickInner();
            long elapsed = Stopwatch.GetTimestamp() - start;
            statFrames++;
            statTicks += elapsed;
            if (elapsed > statMaxTicks) statMaxTicks = elapsed;
        }

        public static string TakeStats()
        {
            double toMs = 1000.0 / Stopwatch.Frequency;
            string s = statFrames == 0
                ? "no work frames"
                : "work frames=" + statFrames +
                  ", avg=" + (statTicks * toMs / statFrames).ToString("0.000") + " ms" +
                  ", max=" + (statMaxTicks * toMs).ToString("0.000") + " ms";
            statFrames = 0;
            statTicks = 0;
            statMaxTicks = 0;
            return s;
        }

        // World is shutting down: drop the mirror so the next world starts clean
        public static void Reset()
        {
            MapPatches.Watched = null;
            MapPatches.ClearDirty();
            NavMirror.Clear();
            CompanionSettings.ResetFollow();
            sync = SyncState.None;
            poisPosted = false;
            syncRegionPos = null;
            syncRegions = null;
            syncFixed = null;
            syncKeys = null;
            lastX = float.NaN;
            WebHost.PostReset();
        }

        private static void TickInner()
        {
            string request;
            while (WebHost.ViewRequests.TryDequeue(out request))
            {
                int zoom;
                if (request.StartsWith("follow=")) CompanionSettings.SetFollow(request.Substring(7));
                else if (request.StartsWith("zoom=") && int.TryParse(request.Substring(5), out zoom)) CompanionSettings.SetZoom(zoom);
            }

            GameManager gm = GameManager.Instance;
            World world = gm != null && gm.gameStateManager != null && gm.gameStateManager.IsGameStarted() ? gm.World : null;
            if (world == null)
            {
                PostState(StateMenu);
                return;
            }
            EntityPlayerLocal player = world.GetPrimaryPlayer();
            IMapChunkDatabase db = player != null && player.IsSpawned() && player.ChunkObserver != null
                ? player.ChunkObserver.mapDatabase
                : null;
            if (db == null)
            {
                PostState(StateLoading);
                return;
            }

            if (db != MapPatches.Watched || MapPatches.ClearedSinceSync)
            {
                StartSync(db, world, player);
            }
            PostState(StateInGame);
            if (sync == SyncState.Running)
            {
                SyncSlice();
            }

            float now = Time.unscaledTime;
            if (now >= nextPosTick)
            {
                // Fixed schedule, so frame timing jitter doesn't skip every other frame at 60 fps
                nextPosTick += PositionInterval;
                if (nextPosTick < now) nextPosTick = now;
                PostPosition(player);
            }
            if (sync == SyncState.Done && !poisPosted)
            {
                PostPois();
            }
            if (now >= nextNavTick && sync == SyncState.Done)
            {
                nextNavTick = now + NavInterval;
                NavMirror.Update(player);
            }
            if (now >= nextTick)
            {
                nextTick = now + TickInterval;
                if (sync == SyncState.Done)
                {
                    DrainDirty(db);
                }
            }
        }

        private static void PostState(int _state)
        {
            if (_state == postedState) return;
            postedState = _state;
            WebHost.PostState(_state);
        }

        private static void StartSync(IMapChunkDatabase _db, World _world, EntityPlayerLocal _player)
        {
            MapPatches.Watched = _db;
            MapPatches.ClearedSinceSync = false;
            MapPatches.ClearDirty();
            NavMirror.Clear();
            poisPosted = false;
            WebHost.PostReset();

            Vector3i min, max;
            if (_world.GetWorldExtent(out min, out max))
            {
                WebHost.PostWorld(min.x, min.z, max.x, max.z, _player.PlayerDisplayName, GamePrefs.GetString(EnumGamePrefs.GameWorld));
            }

            syncRegionPos = null;
            syncRegions = null;
            syncFixed = null;
            syncKeys = null;
            syncRegionIndex = 0;
            syncOffset = 0;
            syncKeyIndex = 0;
            syncCopied = 0;

            MapChunkDatabaseByRegion byRegion = _db as MapChunkDatabaseByRegion;
            MapChunkDatabase fixedDb = _db as MapChunkDatabase;
            if (byRegion != null)
            {
                syncRegionPos = new List<Vector2i>();
                syncRegions = new List<MapChunkDatabaseByRegion.RegionData>();
                lock (byRegion.m_regionsLock)
                {
                    foreach (KeyValuePair<Vector2i, MapChunkDatabaseByRegion.RegionData> kv in byRegion.m_regions)
                    {
                        syncRegionPos.Add(kv.Key);
                        syncRegions.Add(kv.Value);
                    }
                }
            }
            else if (fixedDb != null)
            {
                syncFixed = fixedDb;
                lock (fixedDb.catalog)
                {
                    syncKeys = fixedDb.catalog.list.ToArray();
                }
            }
            else
            {
                Log.Warning("[WebCompanion] Unknown map database type " + _db.GetType().Name + ", only new chunks will show");
                sync = SyncState.Done;
                return;
            }
            sync = SyncState.Running;
        }

        // Copies one budgeted slice of the existing map per frame, so the initial copy never stalls a frame
        private static void SyncSlice()
        {
            int scanned = 0;
            if (syncRegions != null)
            {
                while (scanned < SyncSlotBudget && syncRegionIndex < syncRegions.Count)
                {
                    MapChunkDatabaseByRegion.RegionData region = syncRegions[syncRegionIndex];
                    Vector2i rpos = syncRegionPos[syncRegionIndex];
                    for (; syncOffset < 1024 && scanned < SyncSlotBudget; syncOffset++, scanned++)
                    {
                        int ox = syncOffset & 31;
                        int oz = syncOffset >> 5;
                        ushort[] colors = region.m_regionData[oz][ox];
                        if (colors != null)
                        {
                            PostChunk(rpos.x * 32 + ox, rpos.y * 32 + oz, colors);
                        }
                    }
                    if (syncOffset >= 1024)
                    {
                        syncRegionIndex++;
                        syncOffset = 0;
                    }
                }
                if (syncRegionIndex < syncRegions.Count) return;
            }
            else
            {
                for (; syncKeyIndex < syncKeys.Length && scanned < SyncSlotBudget; syncKeyIndex++, scanned++)
                {
                    int key = syncKeys[syncKeyIndex];
                    ushort[] colors = syncFixed.GetDS(key);
                    if (colors != null)
                    {
                        PostChunk((short)(key & 0xFFFF), (short)(key >> 16), colors);
                    }
                }
                if (syncKeyIndex < syncKeys.Length) return;
            }

            sync = SyncState.Done;
            syncRegionPos = null;
            syncRegions = null;
            syncFixed = null;
            syncKeys = null;
            Log.Out("[WebCompanion] Initial map copy done: " + syncCopied + " chunks");
        }

        private static void DrainDirty(IMapChunkDatabase _db)
        {
            HashSet<long> dirty = MapPatches.TakeDirty();
            if (dirty == null) return;

            int copied = 0;
            foreach (long key in dirty)
            {
                if (copied >= ChunkBudget)
                {
                    unprocessed.Add(key);
                    continue;
                }
                ushort[] colors = _db.GetMapColors(key);
                if (colors == null) continue;
                PostChunk(WorldChunkCache.extractX(key), WorldChunkCache.extractZ(key), colors);
                copied++;
            }
            MapPatches.ReturnDirty(dirty, unprocessed);
            unprocessed.Clear();
        }

        // Once per world: the POI list for labels. Multiplayer clients have it too, the game downloads it while
        // joining (NetPackagePOIMetadataResponse). The page only labels POIs in explored areas.
        private static void PostPois()
        {
            poisPosted = true;
            DynamicPrefabDecorator deco = GameManager.Instance.GetDynamicPrefabDecorator();
            if (deco == null) return;
            List<PrefabInstance> list = new List<PrefabInstance>();
            deco.GetWorldPrefabs(list);
            List<WebHost.PoiInfo> pois = new List<WebHost.PoiInfo>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                PrefabInstance pi = list[i];
                if (pi == null || pi.prefab == null) continue;
                Vector3i pos = pi.boundingBoxPosition;
                Vector3i size = pi.boundingBoxSize;
                pois.Add(new WebHost.PoiInfo
                {
                    Name = pi.prefab.LocalizedName,
                    X = pos.x + size.x / 2,
                    Z = pos.z + size.z / 2,
                    SizeX = size.x,
                    SizeZ = size.z,
                    Tier = pi.prefab.DifficultyTier,
                    Trader = pi.prefab.bTraderArea
                });
            }
            WebHost.PostPois(pois);
        }

        private static void PostChunk(int _chunkX, int _chunkZ, ushort[] _colors)
        {
            if (_colors.Length != 256) return;
            ushort[] copy = WebHost.RentBuffer();
            Array.Copy(_colors, copy, 256);
            WebHost.PostChunk(_chunkX, _chunkZ, copy);
            syncCopied++;
        }

        private static void PostPosition(EntityPlayerLocal _player)
        {
            Vector3 pos = _player.position;
            float yaw = _player.rotation.y;
            if (Mathf.Abs(pos.x - lastX) < 0.02f && Mathf.Abs(pos.z - lastZ) < 0.02f &&
                Mathf.Abs(pos.y - lastY) < 0.05f && Mathf.Abs(yaw - lastYaw) < 0.25f)
            {
                return;
            }
            lastX = pos.x;
            lastY = pos.y;
            lastZ = pos.z;
            lastYaw = yaw;
            WebHost.PostPosition(pos.x, pos.y, pos.z, yaw);
        }
    }
}
