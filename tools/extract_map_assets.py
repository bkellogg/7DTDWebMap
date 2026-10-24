"""Copies the in-game map's textures out of the game files into ../web/.

The companion page uses them so it looks like the in-game map:
  map_background.png  - concrete texture behind the map (material MaskableMainmap, _Background)
  fow_chunkMask.png   - 64x64 sheet of 16x16 fog-of-war edge masks (see XUiC_MapArea.initFOWChunkMaskColors)

Re-run after a game update if the map looks off. Needs UnityPy:
  python -m venv venv && venv\Scripts\pip install UnityPy && venv\Scripts\python extract_map_assets.py
"""
import os
import UnityPy

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "web")
SOURCES = {
    "map_background": os.path.join(GAME, "7DaysToDie_Data", "data.unity3d"),
    "fow_chunkMask": os.path.join(GAME, "Data", "Addressables", "Standalone", "textures_assets_textures", "ui.bundle"),
}

for name, path in SOURCES.items():
    found = False
    for obj in UnityPy.load(path).objects:
        if obj.type.name != "Texture2D":
            continue
        tex = obj.read()
        if tex.m_Name == name:
            tex.image.save(os.path.join(OUT, name + ".png"))
            print("saved", name, tex.m_Width, "x", tex.m_Height)
            found = True
            break
    if not found:
        print("NOT FOUND:", name, "in", path)
