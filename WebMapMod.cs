using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;

namespace WebMap
{
    [SuppressMessage("ReSharper", "UnusedType.Global")]
    public class WebMapMod : IModApi
    {
        public const int Port = 26910;

        public void InitMod(Mod _modInstance)
        {
            // Every server (dedicated, hosting or single player) tracks workstations for players with the map open
            ModEvents.GameUpdate.RegisterHandler(OnGameUpdate);
            ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDownServer);

            // The page shows the local player's map, so a dedicated server has nothing to serve
            if (GameManager.IsDedicatedServer)
            {
                Log.Out("[WebMap] Dedicated server, web map not started");
                return;
            }

            new HarmonyLib.Harmony("com.webcompanion.patches").PatchAll(Assembly.GetExecutingAssembly());
            WebHost.Start(Port, Path.Combine(_modInstance.Path, "web"));
            List<string> iconDirs = new List<string> { GameIO.GetGameDir("Data/ItemIcons") };
            foreach (Mod mod in ModManager.GetLoadedMods()) iconDirs.Add(Path.Combine(mod.Path, "ItemIcons"));
            WebHost.SetItemIconDirs(iconDirs);
            CompanionSettings.Load();

            // UnityUpdate also fires in the main menu (GameUpdate only once a game has started), so the page can show "not in a game"
            ModEvents.UnityUpdate.RegisterHandler(OnUnityUpdate);
            ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown);
            ModEvents.GameShutdown.RegisterHandler(OnGameShutdown);
        }

        private static void OnGameUpdate(ref ModEvents.SGameUpdateData _data)
        {
            WorkstationTracker.Tick();
        }

        private static void OnWorldShuttingDownServer(ref ModEvents.SWorldShuttingDownData _data)
        {
            WorkstationTracker.Reset();
        }

        private static void OnUnityUpdate(ref ModEvents.SUnityUpdateData _data)
        {
            Companion.Tick();
        }

        private static void OnWorldShuttingDown(ref ModEvents.SWorldShuttingDownData _data)
        {
            Companion.Reset();
        }

        private static void OnGameShutdown(ref ModEvents.SGameShutdownData _data)
        {
            WebHost.Stop();
        }
    }
}
