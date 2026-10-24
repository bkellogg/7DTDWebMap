using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace WebMap
{
    [SuppressMessage("ReSharper", "UnusedType.Global")]
    public class ConsoleCmdWebMap : ConsoleCmdAbstract
    {
        public override string[] getCommands()
        {
            return new[] { "webmap", "wm" };
        }

        public override string getDescription()
        {
            return "WebMap: show the web map address and status";
        }

        public override string getHelp()
        {
            return "Usage:\n" +
                   "  wm         - page address, connected browsers, map copy status\n" +
                   "  wm stats   - main-thread time the mod used since the last 'wm stats'";
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            if (_params.Count > 0 && _params[0] == "stats")
            {
                SdtdConsole.Instance.Output("[WebMap] Main thread: " + Companion.TakeStats());
                return;
            }
            SdtdConsole.Instance.Output("[WebMap] " + WebHost.Describe() + ", map copy " + Companion.SyncStatus);
        }
    }
}
