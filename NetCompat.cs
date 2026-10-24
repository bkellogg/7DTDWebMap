using System;
using HarmonyLib;

namespace WebMap
{
    // Lets players without the mod join a server that runs it.
    //
    // When a client connects, the server sends the name of every package type it knows, and a client that
    // doesn't know one disconnects ("Unknown package type"). Clients skip entries named NetPackagePackageIds
    // without complaint, so the server lists ours under that name, in the last slot (a vanilla client's tables
    // are one shorter, so no vanilla package may sit after ours). A client with the mod maps that slot back to
    // NetPackageWebMap, which is also how it knows the server runs the mod. The server only sends our package
    // to players who subscribed, and only modded clients subscribe.
    public static class NetCompat
    {
        // Patched on every instance: servers relabel, clients map back
        public static readonly Type[] Patches = { typeof(StartServerPatch), typeof(IdMappingsPatch) };

        private static readonly Type packageIdsType = typeof(NetPackagePackageIds);

        [HarmonyPatch(typeof(NetPackageManager), nameof(NetPackageManager.StartServer))]
        public static class StartServerPatch
        {
            public static void Postfix()
            {
                try
                {
                    Type[] ids = NetPackageManager.packageIdToClass;
                    int ours = Array.IndexOf(ids, typeof(NetPackageWebMap));
                    if (ours < 1) return;
                    int last = ids.Length - 1;
                    if (ours != last)
                    {
                        // Swap so ours is last. AddPackageMapping fills all three tables.
                        Type other = ids[last];
                        NetPackageManager.AddPackageMapping(ours, other);
                        NetPackageManager.AddPackageMapping(last, typeof(NetPackageWebMap));
                    }
                    // Only the name list sent to clients (and a debug name lookup) reads this table; the server
                    // still finds our package by type and by id through the other two
                    ids[last] = packageIdsType;
                }
                catch (Exception e)
                {
                    Log.Error("[WebMap] Couldn't hide the WebMap package from clients without the mod: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(NetPackageManager), nameof(NetPackageManager.IdMappingsReceived))]
        public static class IdMappingsPatch
        {
            public static void Postfix(string[] _mappings)
            {
                Type[] ids = NetPackageManager.packageIdToClass;
                if (ids == null) return;
                for (int i = _mappings.Length - 1; i > 0; i--)
                {
                    if (!string.Equals(_mappings[i], packageIdsType.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (i < ids.Length) NetPackageManager.AddPackageMapping(i, typeof(NetPackageWebMap));
                    return;
                }
            }
        }

        public static bool ServerHasMod()
        {
            Type[] ids = NetPackageManager.packageIdToClass;
            return ids != null && Array.IndexOf(ids, typeof(NetPackageWebMap)) >= 0;
        }
    }
}
