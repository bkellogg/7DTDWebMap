'use strict';

// Map tiles: 32x32 chunks each (512x512 blocks, one pixel per block)
const TILE_CHUNKS = 32;
const TILE_PX = TILE_CHUNKS * 16;
const CHUNK_BYTES = 8 + 512;

const canvas = document.getElementById('map');
const glCanvas = document.getElementById('mapgl');  // under the 2D canvas, used by the WebGL renderer
const ctx = canvas.getContext('2d');
const statusEl = document.getElementById('status');
const readoutEl = document.getElementById('readout');
const followBtn = document.getElementById('follow');
const overlayEl = document.getElementById('overlay');
const overlayTitle = document.getElementById('overlay-title');
const overlayText = document.getElementById('overlay-text');

const tiles = new Map();  // "rx,rz" -> { rx, rz, canvas, ctx, img, dirty }

// Static layer: background, explored map and POI labels, drawn once into a canvas twice the screen size and
// slid around each frame. Redrawing all of that every frame cost Chrome half its frame rate. Rebuilt on zoom,
// resize, settings changes or when the view nears its edge; newly explored chunks only patch their area.
const layer = { canvas: document.createElement('canvas'), x: 0, z: 0, scale: 0, dpr: 0, valid: false, labels: [] };
layer.ctx = layer.canvas.getContext('2d');
const pendingPatch = new Set(); // chunk keys redrawn since the layer was last updated
let paint = null;               // while drawing the layer: { x, z, w, h } stands in for the view center and canvas size
const chunks = new Map(); // "cx,cz" -> Uint16Array(256) RGB555 colors, kept so edges can be redrawn when neighbors arrive
let epoch = 0, seq = 0;          // what we have
let wantEpoch = 0, wantSeq = 0;  // what the game says exists
let fetching = false;

let player = null;  // drawn position { x, y, z, yaw }, glides between updates from the game
let world = null;   // { minX, minZ, maxX, maxZ, player }
let connected = false;
let gameState = 'menu'; // menu | loading | ingame

// Smooth movement: the game sends position about 20 times a second; the page eases from where the
// arrow is drawn to each new position over roughly one update interval instead of jumping.
let posFrom = null, posTo = null, posStart = 0, posDuration = 50, lastPosAt = 0;

// Map markers (waypoints, bedroll, land claims, backpack, quests, vehicles, party members...), by key.
// Each has the game's values (x, z, r, s sprite, l label, c color, sx/sy scale, layer, ac) plus the drawn
// position (dx, dz, dr), which eases toward x/z/r when a marker moves.
const navs = new Map();
let navOrder = [];        // markers sorted by layer, lowest first
const NAV_EASE_MS = 100;  // markers update 10 times a second
const view = { x: 0, z: 0, scale: 1 }; // world point at screen center, screen px per block

// What to show, set from the in-game panel (map screen) or here. follow: 'self', 'free' or a player's entity id.
let settings = { follow: 'self', hide: [], pois: true, poiMinTier: 1 };
let hidden = new Set();
let follow = true;  // settings.follow !== 'free'

// POIs for labels: { name, x, z, w, h, tier, key (center chunk) }; drawn only where the map is explored.
// Traders are left out, their map marker already shows the name.
let pois = [];
const SKULL = 'ui_game_symbol_skull', SKULL_COLOR = '#ff0000';  // the game's [red] difficulty skulls
let poiKeys = new Set(); // center chunks of all POIs, to spot a newly explored one
let centeredOnce = false;
let renderer = 'canvas';  // 'canvas' or 'webgl' (G switches)
let glr = null;           // WebGL renderer, see gl.js
let mouse = null;   // last mouse position over the canvas, for the readout

// ---- In-game map look ----

// Fog-of-war edge masks, same as XUiC_MapArea.initFOWChunkMaskColors: a 64x64 sheet of 16x16 alpha masks.
// The game reads it bottom-up (Unity texture rows), the PNG is top-down, hence the 63 - k.
let fowMasks = null;
let bgPattern = null;
let bgImage = null;

function loadImage(src) {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = reject;
    img.src = src;
  });
}

async function loadLook() {
  iconFor(SKULL, SKULL_COLOR); // preload the POI difficulty icon
  try {
    const sheet = await loadImage('fow_chunkMask.png');
    const c = document.createElement('canvas');
    c.width = c.height = 64;
    const cctx = c.getContext('2d');
    cctx.drawImage(sheet, 0, 0);
    const px = cctx.getImageData(0, 0, 64, 64).data;
    const red = (k, l) => px[((63 - k) * 64 + l) * 4];
    const masks = new Array(13);
    for (let i = 0; i < 3; i++) {
      for (let j = 0; j < 3; j++) {
        const a = new Uint8Array(256);
        let n = 0;
        for (let k = i * 16; k < (i + 1) * 16; k++) for (let l = j * 16; l < (j + 1) * 16; l++) a[n++] = red(k, l);
        masks[i * 3 + j] = a;
      }
    }
    for (let m = 0; m < 4; m++) {
      const a = new Uint8Array(256);
      let n = 0;
      for (let k = 48; k < 64; k++) for (let l = m * 16; l < (m + 1) * 16; l++) a[n++] = red(k, l);
      masks[9 + m] = a;
    }
    fowMasks = masks;
    for (const key of chunks.keys()) renderChunkKey(key);
    layer.valid = false;
  } catch (err) {
    console.warn('fog mask not loaded, using hard edges', err);
  }
  try {
    bgImage = await loadImage('map_background.png');
    bgPattern = layer.ctx.createPattern(bgImage, 'repeat');
    layer.valid = false;
  } catch (err) {
    console.warn('map background not loaded', err);
  }
  requestDraw();
}

// Which fog mask a chunk gets, from which neighbors are explored. Same decision as XUiC_MapArea.updateMapSection.
function maskIndex(cx, cz) {
  const has = (x, z) => chunks.has(x + ',' + z);
  const n = has(cx, cz + 1), s = has(cx, cz - 1), w = has(cx - 1, cz), e = has(cx + 1, cz);
  if (n && s && w && e) {
    if (!has(cx - 1, cz + 1)) return 9;
    if (!has(cx + 1, cz + 1)) return 10;
    if (!has(cx + 1, cz - 1)) return 11;
    return has(cx - 1, cz - 1) ? 4 : 12;
  }
  let idx = 0;
  if (s && !n) idx += 6;
  else if (s && n) idx += 3;
  if (e && w) idx += 1;
  else if (w) idx += 2;
  return idx;
}

// ---- Markers ----

function applyNavs(d) {
  const now = performance.now();
  if (d.full) navs.clear();
  for (const n of d.set) {
    const old = navs.get(n.k);
    if (old) {
      n.fx = old.dx; n.fz = old.dz; n.fr = old.dr; n.start = now;
      n.dx = old.dx; n.dz = old.dz; n.dr = old.dr;
    } else {
      n.dx = n.x; n.dz = n.z; n.dr = n.r;
    }
    navs.set(n.k, n);
  }
  for (const k of d.del) navs.delete(k);
  // POI labels dodge markers; re-lay them out when markers come or go (not when they move)
  if (d.full || d.del.length || navOrder.length !== navs.size) layer.valid = false;
  navOrder = Array.from(navs.values()).sort((a, b) => a.layer - b.layer);
  requestDraw();
}

// Eases moving markers toward their latest position. Returns true while any is still moving.
function stepNavs(now) {
  let moving = false;
  for (const n of navOrder) {
    if (n.start === undefined) continue;
    const t = Math.max(0, Math.min(1, (now - n.start) / NAV_EASE_MS));
    n.dx = n.fx + (n.x - n.fx) * t;
    n.dz = n.fz + (n.z - n.fz) * t;
    n.dr = n.fr + ((((n.r - n.fr) % 360) + 540) % 360 - 180) * t;
    if (t >= 1) n.start = undefined; else moving = true;
  }
  return moving;
}

// The game's icons are white; it tints them with the marker color (NGUI multiplies sprite x color).
const icons = new Map();   // sprite -> Image (or null while loading / if missing)
const tinted = new Map();  // sprite|rgb -> canvas

function iconFor(sprite, color) {
  if (!icons.has(sprite)) {
    icons.set(sprite, null);
    const img = new Image();
    img.onload = () => { icons.set(sprite, img); if (sprite === SKULL) layer.valid = false; requestDraw(); };
    img.src = 'icons/' + encodeURIComponent(sprite) + '.png';
  }
  const img = icons.get(sprite);
  if (!img) return null;
  const rgb = color.slice(0, 7);
  const key = sprite + '|' + rgb;
  let c = tinted.get(key);
  if (!c) {
    c = document.createElement('canvas');
    c.width = img.width;
    c.height = img.height;
    const t = c.getContext('2d');
    t.drawImage(img, 0, 0);
    t.globalCompositeOperation = 'multiply';
    t.fillStyle = rgb;
    t.fillRect(0, 0, c.width, c.height);
    t.globalCompositeOperation = 'destination-in';
    t.drawImage(img, 0, 0);
    tinted.set(key, c);
  }
  return c;
}

function drawNavs(dpr) {
  // Icons grow a little when zoomed in, like the in-game map, within limits
  const zoom = Math.min(1.6, Math.max(0.6, Math.sqrt(view.scale)));
  ctx.font = '600 ' + (12 * dpr) + 'px system-ui, "Segoe UI", sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'bottom';
  const labels = [];  // text, x, y, color
  for (const n of navOrder) {
    if (hidden.has(n.cat)) continue;
    const icon = iconFor(n.s, n.c);
    if (!icon) continue;
    const w = Math.min(56, Math.max(10, 26 * n.sx * zoom)) * dpr;
    const h = Math.min(56, Math.max(10, 26 * n.sy * zoom)) * dpr;
    let [x, y] = worldToScreen(n.dx, n.dz);
    if (n.ac) { x += w / 2; y -= h / 2; }
    if (x < -w || y < -h || x > canvas.width + w || y > canvas.height + h) continue;
    const alpha = parseInt(n.c.slice(7, 9), 16) / 255;
    ctx.save();
    ctx.globalAlpha = isNaN(alpha) ? 1 : alpha;
    ctx.translate(x, y);
    if (n.dr) ctx.rotate(n.dr * Math.PI / 180);
    ctx.drawImage(icon, -w / 2, -h / 2, w, h);
    ctx.restore();
    if (n.l) labels.push(n.l, x, y - h / 2 - 2 * dpr, n.c.slice(0, 7));
  }
  // Labels after all icons, so a neighbouring icon never covers one
  ctx.lineWidth = 3 * dpr;
  ctx.strokeStyle = 'rgba(0,0,0,.7)';
  for (let i = 0; i < labels.length; i += 4) {
    ctx.strokeText(labels[i], labels[i + 1], labels[i + 2]);
    ctx.fillStyle = labels[i + 3];
    ctx.fillText(labels[i], labels[i + 1], labels[i + 2]);
  }
}

// ---- POI labels ----

// Names without a translation come through as prefab file names (e.g. "house_old_ranch_04")
function tidyName(n) {
  if (!n.includes('_')) return n;
  return n.replace(/_\d+$/, '').split('_').filter(Boolean).map((w) => w[0].toUpperCase() + w.slice(1)).join(' ');
}

async function loadPois() {
  try {
    const res = await fetch('/pois', { cache: 'no-store' });
    const list = await res.json();
    pois = list.filter((p) => !p[6]).map(([name, x, z, w, h, tier]) => ({
      name: tidyName(name), x, z, w, h, tier,
      key: Math.floor(x / 16) + ',' + Math.floor(z / 16), tw: 0,
    }));
    // Higher tiers win when labels collide
    pois.sort((a, b) => b.tier - a.tier);
    poiKeys = new Set(pois.map((q) => q.key));
    invalidateLayer();
  } catch (err) {
    console.warn('POI list not loaded', err);
  }
}

function overlaps(boxes, x0, y0, x1, y1) {
  for (let i = 0; i < boxes.length; i += 4) {
    if (x0 < boxes[i + 2] && x1 > boxes[i] && y0 < boxes[i + 3] && y1 > boxes[i + 1]) return true;
  }
  return false;
}

// Screen boxes of markers and their labels, which POI labels keep clear of. The player moves too much
// to be baked into the layer, so its arrow is drawn on top instead.
function markerBoxes(dpr) {
  const boxes = [];
  const zoom = Math.min(1.6, Math.max(0.6, Math.sqrt(view.scale)));
  for (const n of navOrder) {
    if (hidden.has(n.cat)) continue;
    const [x, y] = worldToScreen(n.dx, n.dz);
    const r = Math.min(56, Math.max(10, 26 * Math.max(n.sx, n.sy) * zoom)) * dpr / 2;
    boxes.push(x - r, y - r, x + r, y + r);
    if (n.l) {
      const lw = n.l.length * 3.5 * dpr;
      boxes.push(x - lw, y - r - 16 * dpr, x + lw, y - r);
    }
  }
  return boxes;
}

function setPoiFont(g, dpr) {
  g.font = '600 ' + (12 * dpr) + 'px system-ui, "Segoe UI", sans-serif';
  g.textAlign = 'left';
  g.textBaseline = 'middle';
}

// Lays out the POI outlines and labels in layer coordinates and draws them
function drawPois(g, W, H, dpr) {
  layer.labels = layoutPois(g, 0, 0, W, H, dpr);
  for (const e of layer.labels) drawPoi(g, e, dpr);
}

// Where each POI's outline and label go, for POIs inside the given box (in worldToScreen coordinates)
function layoutPois(g, bx0, by0, bx1, by1, dpr) {
  const entries = [];
  if (!settings.pois || !pois.length) return entries;
  const fontPx = 12 * dpr;
  const skull = Math.round(11 * dpr);
  setPoiFont(g, dpr);
  const placed = markerBoxes(dpr);
  const outlines = view.scale >= 0.5;
  for (const p of pois) {
    if (p.tier < settings.poiMinTier) continue;
    const [x, y] = worldToScreen(p.x, p.z);
    const hw = p.w / 2 * view.scale * dpr, hh = p.h / 2 * view.scale * dpr;
    if (x + hw < bx0 || y + hh < by0 || x - hw > bx1 || y - hh > by1) continue;
    if (!chunks.has(p.key)) continue;
    const e = { p, x, y, hw, hh, outline: outlines && hw > 8 * dpr, x0: 0, ly: null, skull };

    // Centered on the POI, or just below / above it when a marker is in the way
    if (p.twDpr !== dpr) { p.tw = g.measureText(p.name).width; p.twDpr = dpr; }
    const total = p.tw + (p.tier ? 3 * dpr + p.tier * skull : 0);
    const x0 = x - total / 2, x1 = x0 + total, half = fontPx * 0.7;
    for (const dy of [0, 2.2 * half, -2.2 * half]) {
      if (!overlaps(placed, x0, y + dy - half, x1, y + dy + half)) { e.ly = y + dy; break; }
    }
    if (e.ly !== null) {
      placed.push(x0, e.ly - half, x1, e.ly + half);
      e.x0 = x0;
    }
    if (!e.outline && e.ly === null) continue;
    // Bounds of everything this entry draws, for patching
    e.bx0 = Math.min(x - hw, e.ly === null ? Infinity : x0) - 4 * dpr;
    e.bx1 = Math.max(x + hw, e.ly === null ? -Infinity : x1) + 4 * dpr;
    e.by0 = Math.min(y - hh, e.ly === null ? Infinity : e.ly - half) - 4 * dpr;
    e.by1 = Math.max(y + hh, e.ly === null ? -Infinity : e.ly + half) + 4 * dpr;
    entries.push(e);
  }
  return entries;
}

function drawPoi(g, e, dpr) {
  if (e.outline) {
    g.strokeStyle = 'rgba(255,255,255,.22)';
    g.lineWidth = 1 * dpr;
    g.strokeRect(e.x - e.hw, e.y - e.hh, e.hw * 2, e.hh * 2);
  }
  if (e.ly === null) return;
  const p = e.p;
  g.lineWidth = 3 * dpr;
  g.strokeStyle = 'rgba(0,0,0,.75)';
  g.strokeText(p.name, e.x0, e.ly);
  g.fillStyle = '#f3ead2';
  g.fillText(p.name, e.x0, e.ly);
  const skullIcon = iconFor(SKULL, SKULL_COLOR);
  if (p.tier && skullIcon) {
    for (let i = 0; i < p.tier; i++) {
      g.drawImage(skullIcon, e.x0 + p.tw + 3 * dpr + i * e.skull, e.ly - e.skull / 2, e.skull, e.skull);
    }
  }
}

// ---- Follow target ----

function followedPoint() {
  if (settings.follow === 'self') return player;
  if (settings.follow === 'free') return null;
  for (const n of navOrder) {
    if (n.e === settings.follow) return { x: n.dx, z: n.dz };
  }
  return null;  // that player's marker isn't on the map right now
}

function followedName() {
  if (typeof settings.follow !== 'number') return null;
  for (const n of navOrder) {
    if (n.e === settings.follow) return n.l;
  }
  return null;
}

function updateFollowButton() {
  followBtn.classList.toggle('on', follow);
  const name = followedName();
  followBtn.textContent = name ? 'Following ' + name : 'Follow';
}

// Changes who the map follows, here and in the game's panel
function setFollowTarget(target) {
  settings.follow = target;
  follow = target !== 'free';
  updateFollowButton();
  const p = followedPoint();
  if (p) { view.x = p.x; view.z = p.z; }
  requestDraw();
  fetch('/view?follow=' + encodeURIComponent(target), { method: 'POST', headers: { 'X-Companion': '1' } })
    .catch((err) => console.warn('could not send follow change', err));
}

// ---- Map data ----

function tileFor(cx, cz) {
  const rx = Math.floor(cx / TILE_CHUNKS), rz = Math.floor(cz / TILE_CHUNKS);
  const key = rx + ',' + rz;
  let t = tiles.get(key);
  if (!t) {
    const c = document.createElement('canvas');
    c.width = c.height = TILE_PX;
    const tctx = c.getContext('2d');
    t = { rx, rz, canvas: c, ctx: tctx, img: tctx.createImageData(TILE_PX, TILE_PX), dirty: false };
    tiles.set(key, t);
  }
  return t;
}

// Stores one chunk from a /map response and marks it and its neighbors for redrawing (their edges may change)
function storeChunk(dv, off, touched, fresh) {
  const cx = dv.getInt32(off, true), cz = dv.getInt32(off + 4, true);
  const colors = new Uint16Array(256);
  for (let j = 0, p = off + 8; j < 256; j++, p += 2) colors[j] = dv.getUint16(p, true);
  const key = cx + ',' + cz;
  if (!chunks.has(key)) fresh.push(key);
  chunks.set(key, colors);
  for (let dz = -1; dz <= 1; dz++) for (let dx = -1; dx <= 1; dx++) touched.add((cx + dx) + ',' + (cz + dz));
}

function renderChunkKey(key) {
  const colors = chunks.get(key);
  if (!colors) return;
  const comma = key.indexOf(',');
  renderChunk(+key.slice(0, comma), +key.slice(comma + 1), colors);
}

// Colors are RGB555, 16x16, index = localZ * 16 + localX. North (+z) is up on screen.
// Same conversion as Utils.FromColor5To32, alpha from the chunk's fog mask.
function renderChunk(cx, cz, colors) {
  const t = tileFor(cx, cz);
  const px0 = (cx - t.rx * TILE_CHUNKS) * 16;
  const pz0 = (cz - t.rz * TILE_CHUNKS) * 16;
  const mask = fowMasks ? fowMasks[maskIndex(cx, cz)] : null;
  const data = t.img.data;
  for (let j = 0; j < 256; j++) {
    const c = colors[j];
    const row = TILE_PX - 1 - (pz0 + (j >> 4));
    const i = (row * TILE_PX + px0 + (j & 15)) * 4;
    data[i] = ((c >> 10) & 31) / 31 * 255;
    data[i + 1] = ((c >> 5) & 31) / 31 * 255;
    data[i + 2] = (c & 31) / 31 * 255;
    data[i + 3] = mask ? mask[j] : 255;
  }
  t.dirty = true;
  t.glDirty = true;
}

function clearMap() {
  if (glr) glr.forgetTiles();
  tiles.clear();
  chunks.clear();
  layer.valid = false;
  seq = 0;
}

async function pull() {
  if (fetching) return;
  fetching = true;
  try {
    while (wantEpoch !== epoch || wantSeq > seq) {
      const res = await fetch('/map?since=' + seq + '&epoch=' + epoch, { cache: 'no-store' });
      if (!res.ok) throw new Error('HTTP ' + res.status);
      const dv = new DataView(await res.arrayBuffer());
      const e = dv.getInt32(0, true), s = dv.getInt32(4, true), n = dv.getInt32(8, true);
      if (e !== epoch) { clearMap(); epoch = e; }
      const touched = new Set(), fresh = [];
      for (let k = 0, off = 12; k < n; k++, off += CHUNK_BYTES) storeChunk(dv, off, touched, fresh);
      for (const key of touched) { renderChunkKey(key); pendingPatch.add(key); }
      // A POI that just became explored gets a label, which needs a fresh layout
      if (fresh.some((key) => poiKeys.has(key))) layer.valid = false;
      seq = s;
      if (wantEpoch !== epoch) wantEpoch = epoch; // game reset again while we fetched; the next 'map' event catches up
      requestDraw();
    }
  } catch (err) {
    console.warn('map fetch failed', err);
    setTimeout(pull, 2000);
  } finally {
    fetching = false;
  }
}

// ---- Live events from the game ----

function connect() {
  const es = new EventSource('/events');
  es.onopen = () => { connected = true; updateOverlay(); };
  es.onerror = () => { connected = false; updateOverlay(); };
  es.addEventListener('state', (ev) => {
    gameState = JSON.parse(ev.data).state;
    if (gameState !== 'ingame') { player = posFrom = posTo = null; world = null; navs.clear(); navOrder = []; pois = []; layer.valid = false; }
    updateOverlay();
    requestDraw();
  });
  es.addEventListener('map', (ev) => {
    const d = JSON.parse(ev.data);
    wantEpoch = d.epoch;
    wantSeq = d.seq;
    pull();
  });
  es.addEventListener('reset', (ev) => {
    const d = JSON.parse(ev.data);
    clearMap();
    epoch = wantEpoch = d.epoch;
    wantSeq = 0;
    player = posFrom = posTo = null;
    navs.clear();
    navOrder = [];
    pois = [];
    layer.valid = false;
    centeredOnce = false;
    requestDraw();
  });
  es.addEventListener('navs', (ev) => { applyNavs(JSON.parse(ev.data)); updateFollowButton(); });
  es.addEventListener('view', (ev) => {
    settings = JSON.parse(ev.data);
    hidden = new Set(settings.hide);
    follow = settings.follow !== 'free';
    updateFollowButton();
    invalidateLayer();
  });
  es.addEventListener('pois', () => loadPois());
  es.addEventListener('world', (ev) => { world = JSON.parse(ev.data); updateOverlay(); requestDraw(); });
  es.addEventListener('pos', (ev) => {
    const p = JSON.parse(ev.data);
    const now = performance.now();
    diagPos(p, lastPosAt ? now - lastPosAt : 0);
    if (!player || now - lastPosAt > 1000) {
      // First position, or after a pause (teleport, alt-tab): jump straight there
      player = { x: p.x, y: p.y, z: p.z, yaw: p.yaw };
      posFrom = posTo = null;
    } else {
      // Ease over about one update interval, smoothed so jittery delivery doesn't stutter
      posDuration = Math.min(250, Math.max(16, posDuration * 0.8 + (now - lastPosAt) * 0.2));
      posFrom = { x: player.x, y: player.y, z: player.z, yaw: player.yaw };
      posTo = p;
      posStart = now;
    }
    lastPosAt = now;
    if (!centeredOnce) { view.x = player.x; view.z = player.z; centeredOnce = true; }
    requestDraw();
  });
}

const STATES = {
  offline: ['Game not running', 'Start 7 Days to Die with WebCompanion installed. This page reconnects by itself.', 'Not connected'],
  menu: ['Not in a game', 'Load a world or join a server and your map will show up here.', 'Main menu'],
  loading: ['Loading world…', 'Your map will show up once you spawn in.', 'Loading'],
};

function updateOverlay() {
  const key = !connected ? 'offline' : gameState === 'ingame' ? null : gameState;
  const info = key ? STATES[key] || STATES.menu : null;
  overlayEl.hidden = !info;
  if (info) {
    overlayTitle.textContent = info[0];
    overlayText.textContent = info[1];
  }
  statusEl.textContent = info ? info[2] : (world && world.player ? world.player : 'In game');
  // Tab title: "PlayerName - WorldName" while in a game
  const parts = info || !world ? [] : [world.player, world.name].filter(Boolean);
  document.title = parts.length ? parts.join(' - ') : '7DTD Companion';
  statusEl.classList.toggle('on', connected);
  statusEl.classList.toggle('off', !connected);
}

// Moves the drawn arrow toward the latest position. Returns true while still moving.
function stepPlayer(now) {
  if (!posTo) return false;
  const t = Math.max(0, Math.min(1, (now - posStart) / posDuration));
  player.x = posFrom.x + (posTo.x - posFrom.x) * t;
  player.y = posFrom.y + (posTo.y - posFrom.y) * t;
  player.z = posFrom.z + (posTo.z - posFrom.z) * t;
  let dYaw = ((posTo.yaw - posFrom.yaw) % 360 + 540) % 360 - 180; // shortest way round
  player.yaw = posFrom.yaw + dYaw * t;
  if (t >= 1) { posFrom = posTo = null; return false; }
  return true;
}

// ---- Diagnostics (D toggles the numbers; stalls are always reported to the game log) ----

const diag = {
  show: false,
  lag: 0,            // ms from the game posting the latest position to the page handling it
  worstLag: [],      // [time, ms] over the last 5 min
  busy: [],          // [time, ms] page main-thread blocks over the last 5 min
  reportedAt: 0,
  stalls: [],        // last few stall descriptions
  prevTs: 0, prevX: 0, prevZ: 0,
};

function diagKeep(list, now, v) {
  list.push([now, v]);
  while (list.length && now - list[0][0] > 300000) list.shift();
}

// Writes a line to the game log (rate-limited), so lag can be checked afterwards without watching the page
function diagReport(msg) {
  const now = performance.now();
  if (now - diag.reportedAt < 1000) return;
  diag.reportedAt = now;
  console.warn('[WebCompanion]', msg);
  fetch('/diag?m=' + encodeURIComponent(msg), { method: 'POST', headers: { 'X-Companion': '1' } }).catch(() => {});
}

function diagMax(list) {
  let m = 0;
  for (const e of list) if (e[1] > m) m = e[1];
  return Math.round(m);
}

// Called for every position; flags a gap while moving (a stall), and says whose fault it was
function diagPos(p, arrivedGap) {
  if (!p.ts) return;
  const now = performance.now();
  diag.lag = Date.now() - p.ts;
  diagKeep(diag.worstLag, now, diag.lag);
  if (diag.lag > 150) diagReport('late position: ' + diag.lag + ' ms from game to page, tab ' + document.visibilityState);
  const gameGap = diag.prevTs ? p.ts - diag.prevTs : 0;
  const dist = Math.hypot(p.x - diag.prevX, p.z - diag.prevZ);
  diag.prevTs = p.ts; diag.prevX = p.x; diag.prevZ = p.z;
  // Walking covers ~0.3 blocks per update; a long gap that ends far away means updates were held up
  if (arrivedGap < 300 || dist < 1.5) return;
  const msg = 'position stall: ' + Math.round(arrivedGap) + ' ms without updates, game gap ' + gameGap +
    ' ms, delivery lag ' + diag.lag + ' ms, moved ' + dist.toFixed(1) + ' blocks, page busy max ' +
    diagMax(diag.busy) + ' ms, tab ' + document.visibilityState;
  diag.stalls.push(new Date().toLocaleTimeString() + ' ' + Math.round(arrivedGap) + 'ms game ' + gameGap +
    ' lag ' + diag.lag);
  if (diag.stalls.length > 4) diag.stalls.shift();
  diag.reportedAt = 0;
  diagReport(msg);
}

// Frame rate while the map is animating. Chrome draws the canvas on the GPU, so a heavy frame shows up
// as fewer frames, not as a blocked page.
const fps = { last: 0, chained: false, frames: 0, gapSum: 0, gapMax: 0, costSum: 0, costMax: 0, since: 0 };

function diagFrame(start, end, moving) {
  if (fps.chained) {
    const gap = start - fps.last;
    fps.frames++; fps.gapSum += gap; fps.costSum += end - start;
    if (gap > fps.gapMax) fps.gapMax = gap;
    if (end - start > fps.costMax) fps.costMax = end - start;
  }
  fps.last = start;
  fps.chained = moving || end - lastPosAt < 100;  // still gliding, or positions still arriving
  if (!fps.since) fps.since = start;
  if (start - fps.since < 10000) return;
  if (fps.frames >= 30) {
    diag.fps = Math.round(1000 * fps.frames / fps.gapSum);
    diag.reportedAt = 0;
    diagReport('frames while moving: ' + diag.fps + ' fps, worst gap ' + Math.round(fps.gapMax) + ' ms, draw script ' +
      (fps.costSum / fps.frames).toFixed(1) + ' ms avg / ' + Math.round(fps.costMax) + ' ms max, POI labels ' +
      (settings.pois ? 'on' : 'off') + ', ' + renderer + ', zoom ' + view.scale + ', canvas ' + canvas.width + 'x' + canvas.height);
  }
  fps.frames = fps.gapSum = fps.gapMax = fps.costSum = fps.costMax = 0;
  fps.since = start;
}

// A 50 ms heartbeat: when it fires late, the page's main thread was blocked for that long
let beatAt = performance.now();
setInterval(() => {
  const now = performance.now();
  const late = now - beatAt - 50;
  beatAt = now;
  if (late > 30 && document.visibilityState === 'visible') {
    diagKeep(diag.busy, now, late);
    if (late > 150) diagReport('page blocked ' + Math.round(late) + ' ms (could not run or draw)');
  }
  if (diag.show) updateReadout();
}, 50);

// ---- Drawing ----

function setRenderer(name) {
  if (name === 'webgl' && !glr) glr = createGlRenderer(glCanvas);
  if (!glr) name = 'canvas';
  renderer = name;
  glCanvas.style.display = name === 'webgl' ? 'block' : 'none';
  layer.valid = false;
  for (const t of tiles.values()) t.dirty = true;  // the canvas copies of tiles may be stale
  try { localStorage.setItem('renderer', name); } catch (err) { /* private window */ }
  requestDraw();
}

function invalidateLayer() {
  layer.valid = false;
  requestDraw();
}

let drawQueued = false;
function requestDraw() {
  if (drawQueued) return;
  drawQueued = true;
  requestAnimationFrame(draw);
}

function resize() {
  const dpr = window.devicePixelRatio || 1;
  canvas.width = Math.round(canvas.clientWidth * dpr);
  canvas.height = Math.round(canvas.clientHeight * dpr);
  glCanvas.width = canvas.width;
  glCanvas.height = canvas.height;
  requestDraw();
}

function worldToScreen(wx, wz) {
  const dpr = window.devicePixelRatio || 1;
  const cx = paint ? paint.x : view.x, cz = paint ? paint.z : view.z;
  return [
    (wx - cx) * view.scale * dpr + (paint ? paint.w : canvas.width) / 2,
    (cz - wz) * view.scale * dpr + (paint ? paint.h : canvas.height) / 2,
  ];
}

function screenToWorld(sx, sy) {
  const dpr = window.devicePixelRatio || 1;
  return [
    view.x + (sx * dpr - canvas.width / 2) / (view.scale * dpr),
    view.z - (sy * dpr - canvas.height / 2) / (view.scale * dpr),
  ];
}

function draw() {
  drawQueued = false;
  // Same clock as the event handlers: the frame timestamp can be earlier than an event that
  // arrived during the frame, which would ease backwards
  const t = performance.now();
  const moving = stepPlayer(t) | stepNavs(t);
  const target = follow ? followedPoint() : null;
  if (target) { view.x = target.x; view.z = target.z; }
  const dpr = window.devicePixelRatio || 1;
  const W = canvas.width, H = canvas.height;
  if (!W || !H) return;
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  if (renderer === 'webgl' && !glr.lost) {
    glr.draw(W, H, dpr);
    ctx.clearRect(0, 0, W, H);
  } else {
    drawLayer(W, H, dpr);
  }
  drawNavs(dpr);
  if (player) drawPlayer(dpr);

  updateReadout();
  if (moving) requestDraw();
  diagFrame(t, performance.now(), moving);
}

// Puts the static layer on screen, rebuilding or patching it first when needed
function drawLayer(W, H, dpr) {
  const s = view.scale * dpr;
  let dx = (layer.x - view.x) * s, dy = (view.z - layer.z) * s;  // layer center relative to screen center
  if (!layer.valid || layer.scale !== view.scale || layer.dpr !== dpr || layer.canvas.width !== W * 2 ||
      layer.canvas.height !== H * 2 || Math.abs(dx) > W / 2 || Math.abs(dy) > H / 2) {
    buildLayer(W * 2, H * 2, dpr);
    dx = dy = 0;
  } else if (pendingPatch.size) {
    patchLayer(dpr);
  }
  pendingPatch.clear();
  // Whole pixels, so sliding the layer never resamples it
  ctx.drawImage(layer.canvas, Math.round(dx - W / 2), Math.round(dy - H / 2));
}

function buildLayer(w, h, dpr) {
  if (layer.canvas.width !== w || layer.canvas.height !== h) {
    layer.canvas.width = w;
    layer.canvas.height = h;
  }
  layer.x = view.x; layer.z = view.z; layer.scale = view.scale; layer.dpr = dpr; layer.valid = true;
  const g = layer.ctx;
  paint = { x: layer.x, z: layer.z, w, h };
  g.setTransform(1, 0, 0, 1, 0, 0);
  drawBackground(g, 0, 0, w, h);
  drawTiles(g, 0, 0, w, h, dpr);
  drawPois(g, w, h, dpr);
  paint = null;
}

// Redraws just the area of newly explored chunks (and their neighbours, whose fog edges changed)
function patchLayer(dpr) {
  let minX = Infinity, minZ = Infinity, maxX = -Infinity, maxZ = -Infinity;
  for (const key of pendingPatch) {
    const comma = key.indexOf(',');
    const cx = +key.slice(0, comma), cz = +key.slice(comma + 1);
    if (cx < minX) minX = cx; if (cx > maxX) maxX = cx;
    if (cz < minZ) minZ = cz; if (cz > maxZ) maxZ = cz;
  }
  const g = layer.ctx, w = layer.canvas.width, h = layer.canvas.height;
  paint = { x: layer.x, z: layer.z, w, h };
  const [ax, ay] = worldToScreen(minX * 16, (maxZ + 1) * 16);
  const [bx, by] = worldToScreen((maxX + 1) * 16, minZ * 16);
  const x0 = Math.max(0, Math.floor(ax) - 1), y0 = Math.max(0, Math.floor(ay) - 1);
  const x1 = Math.min(w, Math.ceil(bx) + 1), y1 = Math.min(h, Math.ceil(by) + 1);
  if (x1 > x0 && y1 > y0) {
    g.save();
    g.beginPath();
    g.rect(x0, y0, x1 - x0, y1 - y0);
    g.clip();
    drawBackground(g, x0, y0, x1 - x0, y1 - y0);
    drawTiles(g, x0, y0, x1, y1, dpr);
    setPoiFont(g, dpr);
    for (const e of layer.labels) {
      if (e.bx1 > x0 && e.bx0 < x1 && e.by1 > y0 && e.by0 < y1) drawPoi(g, e, dpr);
    }
    g.restore();
  }
  paint = null;
}

// The game's concrete texture moves and scales with the map; it repeats every 2048 blocks
function drawBackground(g, x, y, w, h) {
  if (!bgPattern) {
    g.fillStyle = '#7d7d7d';
    g.fillRect(x, y, w, h);
    return;
  }
  const dpr = window.devicePixelRatio || 1;
  const [ox, oy] = worldToScreen(0, 0);
  const k = 2048 / bgImage.width * view.scale * dpr;
  bgPattern.setTransform(new DOMMatrix([k, 0, 0, k, ox, oy]));
  g.fillStyle = bgPattern;
  g.fillRect(x, y, w, h);
}

function drawTiles(g, x0, y0, x1, y1, dpr) {
  // The game draws its map texture with bilinear filtering
  g.imageSmoothingEnabled = true;
  g.imageSmoothingQuality = 'high';
  const size = TILE_PX * view.scale * dpr;
  for (const t of tiles.values()) {
    const [x, y] = worldToScreen(t.rx * TILE_PX, (t.rz + 1) * TILE_PX);
    if (x > x1 || y > y1 || x + size < x0 || y + size < y0) continue;
    if (t.dirty) { t.ctx.putImageData(t.img, 0, 0); t.dirty = false; }
    g.drawImage(t.canvas, x, y, size, size);
  }
}

// White arrow with a dark outline and the player's name, like the in-game map marker
function drawPlayer(dpr) {
  const [px, py] = worldToScreen(player.x, player.z);
  ctx.save();
  ctx.translate(px, py);
  ctx.save();
  ctx.rotate(player.yaw * Math.PI / 180);
  const s = 7 * dpr;
  ctx.beginPath();
  ctx.moveTo(0, -s * 1.5);
  ctx.lineTo(s, s);
  ctx.lineTo(0, s * 0.4);
  ctx.lineTo(-s, s);
  ctx.closePath();
  ctx.lineJoin = 'round';
  ctx.strokeStyle = 'rgba(0,0,0,.75)';
  ctx.lineWidth = 2.5 * dpr;
  ctx.stroke();
  ctx.fillStyle = '#f2f2f2';
  ctx.fill();
  ctx.restore();
  if (world && world.player) {
    ctx.font = '600 ' + (13 * dpr) + 'px system-ui, "Segoe UI", sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'bottom';
    ctx.lineWidth = 3 * dpr;
    ctx.strokeStyle = 'rgba(0,0,0,.7)';
    ctx.strokeText(world.player, 0, -s * 1.9);
    ctx.fillStyle = '#f2f2f2';
    ctx.fillText(world.player, 0, -s * 1.9);
  }
  ctx.restore();
}

function fmt(v, pos, neg) {
  const n = Math.round(v);
  return Math.abs(n) + ' ' + (n >= 0 ? pos : neg);
}

function updateReadout() {
  const lines = [];
  if (player) lines.push('You     ' + fmt(player.z, 'N', 'S') + ', ' + fmt(player.x, 'E', 'W'));
  if (mouse) {
    const [wx, wz] = screenToWorld(mouse.x, mouse.y);
    lines.push('Cursor  ' + fmt(wz, 'N', 'S') + ', ' + fmt(wx, 'E', 'W'));
  }
  lines.push('Zoom    ' + (view.scale >= 1 ? view.scale + 'x' : '1/' + (1 / view.scale) + 'x'));
  if (diag.show) {
    lines.push('Lag     ' + diag.lag + ' ms (worst 5 min ' + diagMax(diag.worstLag) + ')');
    lines.push('Busy    worst 5 min ' + diagMax(diag.busy) + ' ms');
    lines.push('Render  ' + renderer + ' (G switches)');
    if (diag.fps) lines.push('FPS     ' + diag.fps + ' while moving');
    for (const st of diag.stalls) lines.push('Stall   ' + st);
  }
  readoutEl.textContent = lines.join('\n');
}

// ---- Input ----

function toggleFollow() {
  setFollowTarget(follow ? 'free' : 'self');
}

function zoomAt(factor, sx, sy) {
  const next = Math.min(8, Math.max(1 / 16, view.scale * factor));
  if (next === view.scale) return;
  if (sx === undefined || follow) {
    view.scale = next;
  } else {
    // Keep the world point under the cursor fixed
    const [wx, wz] = screenToWorld(sx, sy);
    view.scale = next;
    const [wx2, wz2] = screenToWorld(sx, sy);
    view.x += wx - wx2;
    view.z += wz - wz2;
  }
  requestDraw();
}

let drag = null;
canvas.addEventListener('pointerdown', (e) => {
  drag = { x: e.clientX, y: e.clientY, vx: view.x, vz: view.z };
  canvas.setPointerCapture(e.pointerId);
  canvas.classList.add('dragging');
});
canvas.addEventListener('pointermove', (e) => {
  const r = canvas.getBoundingClientRect();
  mouse = { x: e.clientX - r.left, y: e.clientY - r.top };
  if (drag) {
    const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    if (follow && Math.abs(dx) + Math.abs(dy) > 3) setFollowTarget('free');
    if (!follow) {
      view.x = drag.vx - dx / view.scale;
      view.z = drag.vz + dy / view.scale;
    }
  }
  requestDraw();
});
canvas.addEventListener('pointerup', () => { drag = null; canvas.classList.remove('dragging'); });
canvas.addEventListener('pointerleave', () => { mouse = null; requestDraw(); });
canvas.addEventListener('wheel', (e) => {
  e.preventDefault();
  const r = canvas.getBoundingClientRect();
  zoomAt(e.deltaY < 0 ? 2 : 0.5, e.clientX - r.left, e.clientY - r.top);
}, { passive: false });

followBtn.addEventListener('click', toggleFollow);
document.getElementById('zoomin').addEventListener('click', () => zoomAt(2));
document.getElementById('zoomout').addEventListener('click', () => zoomAt(0.5));
window.addEventListener('keydown', (e) => {
  if (e.key === 'f' || e.key === 'F') toggleFollow();
  else if (e.key === '+' || e.key === '=') zoomAt(2);
  else if (e.key === '-') zoomAt(0.5);
  else if (e.key === 'd' || e.key === 'D') { diag.show = !diag.show; updateReadout(); }
  else if (e.key === 'g' || e.key === 'G') setRenderer(renderer === 'webgl' ? 'canvas' : 'webgl');
});
window.addEventListener('resize', resize);

resize();
let savedRenderer = null;
try { savedRenderer = localStorage.getItem('renderer'); } catch (err) { /* private window */ }
setRenderer(new URLSearchParams(location.search).get('renderer') || savedRenderer || 'webgl');
updateOverlay();
loadLook();
connect();
