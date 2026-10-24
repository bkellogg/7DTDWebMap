# WebMap

A second-screen map for **7 Days to Die** (V 3.30). Open `http://127.0.0.1:26910/` in a browser on your other monitor and it shows your full explored map, live, with your position and the same markers as the in-game map. A sidebar lists the workstations in your and your party's land claims, with what they're crafting and how long is left.

Everything is controlled from inside the game, in a panel on the map screen (**M**), so you never have to alt-tab.

## Features

- **Live map.** Your explored map as the game draws it: fog of war, the concrete background, roads and biomes. New areas appear as you explore.
- **Smooth movement.** Your arrow and heading update up to 60 times a second and are played back on a steady clock, so the map glides instead of stuttering.
- **Markers.** Waypoints, bedroll, land claims, backpack, vehicles, quests, traders, party members and creatures. Each kind can be shown or hidden.
- **POI labels.** Names and red difficulty skulls for points of interest, only in areas you've explored, so there are no spoilers. Filter by minimum tier.
- **Workstations sidebar.** Forges, workbenches, campfires, chemistry stations and the rest inside your and your party's land claims, each showing:
  - the current craft with a progress bar and countdown, and the rest of the queue;
  - when everything will be done, fuel left, and items waiting to collect;
  - whether it's working, idle, done, out of fire or in use.

  Filter by type, or show only the ones that are working. Click a card to move the map to it. Far-away workstations keep counting down as estimates, the same way the game catches them up when you return.
- **Follow or pan.** Follow yourself or a party member, or pan freely with the mouse.
- **Zoom.** From 1/16× (whole map) to 8×, with in-between steps (0.75×, 1.25×, 1.5×, 1.75×, 2.5×...).
- **Text and icon sizes.** Adjustable for POI labels, marker labels, your name and icons.
- **Fast.** The map is drawn with WebGL by default (press **G** on the page to switch to a 2D canvas fallback).

## Install

1. Turn off EasyAntiCheat (DLL mods need it off).
2. Copy the `WebMap` folder into the game's `Mods` folder, so you have `7 Days To Die\Mods\WebMap\WebMap.dll`.
3. Start the game and load a world.
4. Open `http://127.0.0.1:26910/` in a browser.

**Multiplayer:** each player who wants the map installs the mod. Nobody else needs it:

- **Players without the mod can join a server that runs it**, with no errors and nothing extra on their screen.
- **Players with the mod can join a server without it.** The map, markers and panel all work. Only the workstations sidebar needs the mod on the server, and it says so when the server lacks it.

The web server only listens on `127.0.0.1` and only answers pages loaded from that address, so nothing is exposed to your network or the internet.

## Using it

Open the map screen (**M**). The **Web Map** panel on the right has three pages:

| Page | Settings |
|------|----------|
| Main | Follow (you, a party member, or free), zoom, which markers to show, POI labels and minimum tier |
| Workstations | Show the sidebar, only working ones, which types to show |
| Text & icon sizes | Size of POI labels, marker labels, your name and icons |

Settings are saved in `WebMap.txt` in the game's user data folder (`%AppData%\7DaysToDie`).

On the page itself you can also drag to pan, use the mouse wheel or **+**/**-** to zoom, and press:

| Key | Action |
|-----|--------|
| F | Follow on/off |
| G | Switch renderer (WebGL / cached canvas) |
| D | Diagnostics readout (frame rate, update delays) |

Console commands (F1):

| Command | Shows |
|---------|-------|
| `wm` | Page address, connected browsers, map copy status |
| `wm stats` | Main-thread time the mod used since the last `wm stats` |

## How it works

The overriding rule is **near-zero cost on the game's main thread**.

- **Client (your game):** a small web server (`SpaceWizards.HttpListener`, which ships with the game) runs on its own thread. The main thread only copies small pieces of state into a queue: changed map chunks, your position, marker changes. A background thread builds the responses and pushes updates to the browser with Server-Sent Events. With no browser open, the mod does nothing per frame.
- **Server:** for each player with the map open, every 2 seconds it looks at the loaded chunks inside their and their party's land claims, snapshots the workstations there, and sends only what changed in a custom `NetPackage`. Nothing runs while nobody has the map open.
- **Mixed installs:** normally a client that doesn't know one of the server's package types refuses to connect. The server lists ours under a name that vanilla clients skip, and modded clients map it back. The panel isn't in `Config/` (the server would send it to everyone); each modded client adds it to its own map screen when the XUi loads.
- **Browser:** does all the drawing and all the timer countdowns, so the game never sends per-second updates.

| File | Role |
|------|------|
| `WebMapMod.cs` | Mod entry point |
| `Companion.cs` | Main-thread tick: map chunks, position, markers, settings |
| `MapPatches.cs` | Harmony patches that spot newly explored map chunks |
| `NavMirror.cs` | Copies the in-game map's markers |
| `WebHost.cs` | Web server, background pusher, map mirror |
| `CompanionSettings.cs` | Settings, saved and pushed to the page |
| `XUiC_CompanionPanel.cs`, `XUi/windows.xml`, `XUiInject.cs` | The panel on the map screen, added on the client only |
| `WorkstationTracker.cs` | Server side of the workstations sidebar |
| `WorkstationClient.cs`, `NetPackages.cs` | Client subscription and the network package |
| `NetCompat.cs` | Lets players without the mod join a server that has it |
| `web/` | The page: `app.js` (map), `gl.js` (WebGL renderer), `ws.js` (workstations sidebar) |
| `tools/` | Scripts that extract the map's textures from the game files |

## Building

Requires the game installed, and MSBuild (Visual Studio, Rider or the .NET SDK) targeting .NET Framework 4.8.

1. If the game isn't at `C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die`, change `GamePath` in `WebMap.csproj`.
2. Build:
   ```
   msbuild WebMap.csproj /p:Configuration=Debug
   ```
   The build copies the DLL, `ModInfo.xml`, `XUi/` and `web/` into `Mods\WebMap` in the game folder.

The code is C# 7.3 (the game's .NET Framework 4.8 runtime), so no newer language features.

## Limitations

- Workstations are only known to the server after their area has been loaded at least once since the server started.
- Land claims of party members who are offline aren't included.
- Projected timers for far-away workstations don't account for the output slots filling up or forge smelting.

## Credits

Map icons and textures in `web/` come from 7 Days to Die and belong to The Fun Pimps.
