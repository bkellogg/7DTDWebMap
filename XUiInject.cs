using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using HarmonyLib;

namespace WebMap
{
    // Adds the Web Map panel to this client's map screen. It isn't in Config/, because the server sends its XUi
    // to every client: players without the mod would get a window whose controller they don't have. Instead each
    // client with the mod adds it to whatever XUi it ends up with (its own, or the server's), just before parsing.
    // On a host the copy sent to clients is cached before this runs, so it stays out of theirs too.
    public static class XUiInject
    {
        public const string WindowName = "webMapPanel";

        private static string windowsPath;

        public static void Init(string _modPath)
        {
            windowsPath = Path.Combine(_modPath, "XUi/windows.xml");
        }

        [HarmonyPatch(typeof(XUiFromXml), nameof(XUiFromXml.Load))]
        public static class LoadPatch
        {
            public static void Prefix(XmlFile _xmlFile)
            {
                try
                {
                    XElement root = _xmlFile != null && _xmlFile.XmlDoc != null ? _xmlFile.XmlDoc.Root : null;
                    if (root == null) return;
                    if (root.Name.LocalName == "windows") AddWindows(root);
                    else if (root.Name.LocalName == "xui") AddToMapGroup(root);
                }
                catch (Exception e)
                {
                    Log.Error("[WebMap] Couldn't add the Web Map panel to the map screen: " + e);
                }
            }
        }

        // The in-game windows file is the one with the map; the main menu's has no mapArea
        private static void AddWindows(XElement _root)
        {
            if (!_root.Elements("window").Any(w => (string)w.Attribute("name") == "mapArea")) return;
            if (_root.Elements("window").Any(w => (string)w.Attribute("name") == WindowName)) return;
            foreach (XElement window in XDocument.Load(windowsPath).Root.Elements("window"))
            {
                _root.Add(new XElement(window));
            }
        }

        private static void AddToMapGroup(XElement _root)
        {
            XElement group = _root.Elements("window_group").FirstOrDefault(g => (string)g.Attribute("name") == "map");
            if (group == null) return;
            if (group.Elements("window").Any(w => (string)w.Attribute("name") == WindowName)) return;
            group.Add(new XElement("window", new XAttribute("name", WindowName)));
        }
    }
}
