using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace WebMap
{
    // Records which chunks of the local player's map changed. Only a set insert happens here;
    // Companion.Tick copies the colors later, throttled and budgeted.
    public static class MapPatches
    {
        // The map database being mirrored. Null until a browser connects, so the hooks cost nothing before that.
        public static volatile IMapChunkDatabase Watched;

        // Set when the watched map is cleared (map reset), so the mirror starts over
        public static volatile bool ClearedSinceSync;

        private static readonly object dirtyLock = new object();
        private static HashSet<long> dirty = new HashSet<long>();
        private static HashSet<long> spare = new HashSet<long>();

        public static void MarkDirty(int _chunkX, int _chunkZ)
        {
            lock (dirtyLock)
            {
                dirty.Add(WorldChunkCache.MakeChunkKey(_chunkX, _chunkZ));
            }
        }

        // Hands the caller every dirty key and starts a fresh set. The caller passes the set back via ReturnDirty.
        public static HashSet<long> TakeDirty()
        {
            lock (dirtyLock)
            {
                if (dirty.Count == 0) return null;
                HashSet<long> taken = dirty;
                dirty = spare;
                spare = null;
                return taken;
            }
        }

        // Puts back the keys the caller didn't get to (they stay dirty) and recycles the set
        public static void ReturnDirty(HashSet<long> _set, List<long> _unprocessed)
        {
            lock (dirtyLock)
            {
                for (int i = 0; i < _unprocessed.Count; i++)
                {
                    dirty.Add(_unprocessed[i]);
                }
                _set.Clear();
                spare = _set;
            }
        }

        public static void ClearDirty()
        {
            lock (dirtyLock)
            {
                dirty.Clear();
            }
        }

        // MapChunkDatabaseByRegion: both public Add overloads go through this private one
        [HarmonyPatch]
        public static class RegionAdd
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(MapChunkDatabaseByRegion), "Add",
                    new[] { typeof(int), typeof(int), typeof(ushort[]), typeof(bool) });
            }

            public static void Postfix(MapChunkDatabaseByRegion __instance, int chunkX, int chunkZ)
            {
                if (__instance != Watched) return;
                MarkDirty(chunkX, chunkZ);
            }
        }

        [HarmonyPatch(typeof(MapChunkDatabase), nameof(MapChunkDatabase.Add), typeof(int), typeof(int), typeof(ushort[]))]
        public static class FixedAdd
        {
            public static void Postfix(MapChunkDatabase __instance, int _chunkX, int _chunkZ)
            {
                if (__instance != Watched) return;
                MarkDirty(_chunkX, _chunkZ);
            }
        }

        [HarmonyPatch(typeof(MapChunkDatabase), nameof(MapChunkDatabase.Add), typeof(List<int>), typeof(List<ushort[]>))]
        public static class FixedAddList
        {
            public static void Postfix(MapChunkDatabase __instance, List<int> _chunks)
            {
                if (__instance != Watched) return;
                for (int i = 0; i < _chunks.Count; i++)
                {
                    // Same layout as IMapChunkDatabase.ToChunkDBKey: z in the high 16 bits, x in the low 16, both signed
                    int key = _chunks[i];
                    MarkDirty((short)(key & 0xFFFF), (short)(key >> 16));
                }
            }
        }

        [HarmonyPatch(typeof(MapChunkDatabaseByRegion), nameof(MapChunkDatabaseByRegion.Clear))]
        public static class RegionClear
        {
            public static void Postfix(MapChunkDatabaseByRegion __instance)
            {
                if (__instance == Watched) ClearedSinceSync = true;
            }
        }

        [HarmonyPatch(typeof(MapChunkDatabase), nameof(MapChunkDatabase.Clear))]
        public static class FixedClear
        {
            public static void Postfix(MapChunkDatabase __instance)
            {
                if (__instance == Watched) ClearedSinceSync = true;
            }
        }
    }
}
