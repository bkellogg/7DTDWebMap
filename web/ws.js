'use strict';
// Workstations sidebar: forges, workbenches, campfires... in your and your party's land claims, sent by the server
// (WorkstationTracker.cs) when they change. Timers run here: each snapshot says how far the game had run the
// workstation ("age" seconds ago), and the page projects crafting and fuel forward from that, the way the game
// catches up when it next runs it. Shown, filtered by type and "only working ones" from the in-game panel.
// Loaded before app.js; uses its globals (settings, player, view, follow...) only from inside functions.

const workstations = new Map();  // id -> { d: snapshot, at: performance.now() when it arrived }
let wsNote = '';
const WS_TYPES = ['campfire', 'forge', 'workbench', 'combineStation', 'cementMixer', 'chemistryStation', 'tableSaw'];
const wsEl = document.getElementById('ws');
const wsList = document.getElementById('ws');
let wsShown = null;

function wsApply(batch) {
  const now = performance.now();
  if (batch.full) workstations.clear();
  for (const d of batch.set) workstations.set(d.id, { d, at: now });
  for (const id of batch.del) workstations.delete(id);
  wsNote = batch.note || '';
  wsRender();
}

function wsClear() {
  workstations.clear();
  wsNote = '';
  wsRender();
}

// The cards float over the map along the edge chosen in the panel (ws-left, ws-right, ws-top or ws-bottom on the body)
let wsSide = '';
function wsUpdateVisibility() {
  const show = !!(settings.ws && settings.ws.show);
  const side = show ? (settings.ws.side || 'left') : '';
  if (side === wsSide) return;
  if (wsSide) document.body.classList.remove('ws-' + wsSide);
  if (side) document.body.classList.add('ws-' + side);
  wsShown = show;
  wsSide = side;
  document.body.classList.toggle('ws-on', show);
  wsRender();
}

// Where the workstation is now, projected from its snapshot
function wsProject(w, now) {
  const d = w.d;
  const elapsed = d.paused ? 0 : d.age + (now - w.at) / 1000;
  let burning = d.burning, fuelLeft = 0, craftTime = elapsed;
  if (d.usesFuel) {
    const total = Math.max(0, d.burnLeft) + d.fuel;
    if (burning) {
      craftTime = Math.min(elapsed, total);
      fuelLeft = total - craftTime;
      if (fuelLeft <= 0) burning = false;
    } else {
      craftTime = 0;
      fuelLeft = total;
    }
  }
  let t = craftTime, made = 0;
  const queue = [];
  for (const it of d.queue) {
    let count = it.count, left = it.left > 0 ? it.left : it.each;
    if (t > 0) {
      if (t < left) {
        left -= t;
        t = 0;
      } else {
        t -= left;
        count--;
        made += it.per;
        const more = it.each > 0 ? Math.min(count, Math.floor(t / it.each)) : count;
        count -= more;
        made += more * it.per;
        t -= more * it.each;
        if (count > 0) { left = it.each - t; t = 0; }
      }
    }
    if (count > 0) queue.push({ name: it.name, icon: it.icon, count, left, each: it.each });
  }
  let total = 0;
  for (const it of queue) total += it.left + (it.count - 1) * it.each;
  const working = queue.length > 0 && (!d.usesFuel || burning);
  return { burning, fuelLeft, queue, total, out: d.out + made, working, active: working || burning };
}

function wsTime(s) {
  s = Math.max(0, Math.ceil(s));
  const h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, sec = s % 60;
  return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(sec).padStart(2, '0');
}

function wsWhere(d) {
  if (!player) return '';
  const dx = d.x - player.x, dz = d.z - player.z, dist = Math.hypot(dx, dz);
  if (dist < 8) return 'here';
  const dirs = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'];
  const dir = dirs[Math.round(Math.atan2(dx, dz) / (Math.PI / 4) + 8) % 8];
  return (dist >= 1000 ? (dist / 1000).toFixed(1) + ' km ' : Math.round(dist) + ' m ') + dir;
}

function wsIcon(name, cls) {
  const img = document.createElement('img');
  img.className = cls;
  img.alt = '';
  img.loading = 'lazy';
  img.src = '/itemicons/' + encodeURIComponent(name) + '.png';
  img.onerror = () => { img.style.visibility = 'hidden'; };
  return img;
}

function wsEl2(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

function wsRender() {
  if (!wsShown) return;
  const now = performance.now();
  const f = settings.ws || {};
  const hiddenTypes = new Set(f.hide || []);
  const rows = [];
  for (const w of workstations.values()) {
    const type = WS_TYPES.includes(w.d.type) ? w.d.type : 'other';
    if (hiddenTypes.has(type)) continue;
    const p = wsProject(w, now);
    if (f.active && !p.active) continue;
    rows.push({ w, p });
  }
  // Working ones first, then by name, then a fixed order so cards don't swap places
  rows.sort((a, b) => (b.p.active - a.p.active) || a.w.d.name.localeCompare(b.w.d.name) || a.w.d.id.localeCompare(b.w.d.id));

  wsList.textContent = '';
  if (!rows.length) {
    wsList.appendChild(wsEl2('p', 'ws-empty', wsNote ||
      (workstations.size ? 'None match the filters in the game\'s map panel.' :
        'No workstations in your or your party\'s land claims yet.')));
    return;
  }
  for (const { w, p } of rows) wsList.appendChild(wsCard(w.d, p));
}

// Two lines at most: station, distance and status; then the current craft (or fuel / what's waiting) with its
// time. The rest (queue, when it's all done, estimates) is in the tooltip.
function wsCard(d, p) {
  const card = wsEl2('div', 'ws-card' + (p.active ? ' active' : ''));
  card.addEventListener('click', () => wsShowOnMap(d));

  const head = wsEl2('div', 'ws-head');
  const iconBox = wsEl2('div', 'ws-iconbox');
  iconBox.appendChild(wsIcon(d.icon, 'ws-icon'));
  if (p.out > 0) iconBox.appendChild(wsEl2('span', 'ws-out', p.out > 999 ? '999+' : String(p.out)));
  head.appendChild(iconBox);
  const name = wsEl2('div', 'ws-name', d.name);
  name.appendChild(wsEl2('span', 'ws-where', wsWhere(d)));
  head.appendChild(name);
  let status, cls;
  if (d.paused && p.queue.length) { status = 'In use'; cls = 'paused'; }
  else if (p.working) { status = 'Working'; cls = 'working'; }
  else if (p.queue.length) { status = d.usesFuel ? 'No fire' : 'Waiting'; cls = 'stopped'; }
  else if (p.burning) { status = 'Burning'; cls = 'working'; }
  else if (p.out > 0) { status = 'Done'; cls = 'done'; }
  else { status = 'Idle'; cls = 'idle'; }
  head.appendChild(wsEl2('span', 'ws-status ' + cls, (d.live ? '' : '~') + status));
  card.appendChild(head);

  const tip = [d.name + (wsWhere(d) ? ' (' + wsWhere(d) + ')' : '') + ': ' + status];
  if (p.queue.length) {
    const cur = p.queue[0];
    const line = wsEl2('div', 'ws-line');
    line.appendChild(wsIcon(cur.icon, 'ws-item'));
    line.appendChild(wsEl2('span', 'ws-craft-name', cur.name + (cur.count > 1 ? ' ×' + cur.count : '')));
    if (p.queue.length > 1) line.appendChild(wsEl2('span', 'ws-more', '+' + (p.queue.length - 1)));
    line.appendChild(wsEl2('span', 'ws-time', wsTime(cur.left)));
    card.appendChild(line);
    const bar = wsEl2('div', 'ws-bar');
    const fill = wsEl2('div', 'ws-fill');
    fill.style.width = (cur.each > 0 ? Math.max(0, Math.min(1, 1 - cur.left / cur.each)) * 100 : 0) + '%';
    bar.appendChild(fill);
    card.appendChild(bar);
    for (const it of p.queue) tip.push('  ' + it.name + ' ×' + it.count);
    tip.push('All done in ' + wsTime(p.total));
  } else {
    const facts = [];
    if (d.usesFuel && (p.burning || p.fuelLeft > 0)) facts.push('Fuel ' + wsTime(p.fuelLeft));
    if (p.out > 0) facts.push(p.out + ' to collect');
    if (facts.length) card.appendChild(wsEl2('div', 'ws-line', facts.join(' · ')));
  }
  if (p.queue.length && d.usesFuel && (p.burning || p.fuelLeft > 0)) tip.push('Fuel ' + wsTime(p.fuelLeft));
  if (p.out > 0) tip.push(p.out + ' to collect');
  if (!d.live) tip.push('~ Estimated: too far away for the game to update it');
  tip.push('Click to show on the map');
  card.title = tip.join('\n');
  return card;
}

// Pans the map to the workstation (stops following)
function wsShowOnMap(d) {
  setFollowTarget('free');
  view.x = d.x;
  view.z = d.z;
  requestDraw();
}

// Timers tick every second while the sidebar is shown
setInterval(() => { if (wsShown && workstations.size) wsRender(); }, 1000);
