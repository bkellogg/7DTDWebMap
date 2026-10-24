using System.Collections.Generic;

namespace WebMap
{
    // Client side of the workstations sidebar: asks the server for workstation updates while a browser has the
    // web map open and the sidebar is on, and passes what arrives to the page. Main thread.
    public static class WorkstationClient
    {
        private static bool subscribed;

        // False when the server doesn't run the mod (NetCompat found no slot for our package), so the page can say so
        public static bool ServerHasMod { get; private set; }

        public static void Tick(bool _inGame)
        {
            bool want = _inGame && WebHost.ClientCount > 0 && CompanionSettings.ShowWorkstations;
            if (want == subscribed) return;
            ConnectionManager cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            EntityPlayerLocal player = world != null ? world.GetPrimaryPlayer() : null;
            if (cm == null || (want && player == null)) return;
            subscribed = want;

            if (cm.IsServer)
            {
                // Single player or hosting: the tracker is in this process
                ServerHasMod = true;
                if (player != null) WorkstationTracker.SetSubscribed(player.entityId, want);
                return;
            }
            ServerHasMod = NetCompat.ServerHasMod();
            if (!ServerHasMod)
            {
                if (!want) return;
                Log.Out("[WebMap] The server doesn't run WebMap, so workstations can't be shown");
                WebHost.PostWorkstations(true, new List<string>(), new List<string>(), "The server doesn't run WebMap, so workstations can't be shown.");
                return;
            }
            cm.SendToServer(NetPackageManager.GetPackage<NetPackageWebMap>().SetupSubscribe(want));
        }

        // World left: the server forgets us anyway
        public static void Reset()
        {
            subscribed = false;
        }

        public static void Receive(bool _full, List<string> _set, List<string> _del)
        {
            WebHost.PostWorkstations(_full, _set, _del);
        }
    }
}
