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
            // The companion page shows the local player's map, so a dedicated server has nothing to serve
            if (GameManager.IsDedicatedServer)
            {
                Log.Out("[WebMap] Dedicated server, web map not started");
                return;
            }

            new HarmonyLib.Harmony("com.webcompanion.patches").PatchAll(Assembly.GetExecutingAssembly());
            WebHost.Start(Port, Path.Combine(_modInstance.Path, "web"));
            CompanionSettings.Load();

            // UnityUpdate also fires in the main menu (GameUpdate only once a game has started), so the page can show "not in a game"
            ModEvents.UnityUpdate.RegisterHandler(OnUnityUpdate);
            ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown);
            ModEvents.GameShutdown.RegisterHandler(OnGameShutdown);
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
