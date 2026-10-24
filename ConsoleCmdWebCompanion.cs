using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace WebCompanion
{
    [SuppressMessage("ReSharper", "UnusedType.Global")]
    public class ConsoleCmdWebCompanion : ConsoleCmdAbstract
    {
        public override string[] getCommands()
        {
            return new[] { "webcompanion", "wc" };
        }

        public override string getDescription()
        {
            return "WebCompanion: show the companion page address and status";
        }

        public override string getHelp()
        {
            return "Usage:\n" +
                   "  wc         - page address, connected browsers, map copy status\n" +
                   "  wc stats   - main-thread time the mod used since the last 'wc stats'";
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            if (_params.Count > 0 && _params[0] == "stats")
            {
                SdtdConsole.Instance.Output("[WebCompanion] Main thread: " + Companion.TakeStats());
                return;
            }
            SdtdConsole.Instance.Output("[WebCompanion] " + WebHost.Describe() + ", map copy " + Companion.SyncStatus);
        }
    }
}
