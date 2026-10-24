'use strict';

// WebGL renderer for the static part of the map: background, explored map and POI outlines/labels.
// Map tiles live on the GPU as textures, so newly explored chunks are a small texture update and nothing
// is redrawn from scratch. POI labels are drawn once each into an atlas texture and stamped as quads.
// Markers and the player arrow stay on the 2D canvas on top. G on the page switches renderers.
// Uses app.js globals (tiles, view, layer, worldToScreen, layoutPois...) at call time.

const GL_VS = `#version 300 es
in vec2 a_pos;
in vec2 a_uv;
uniform vec2 u_res;
out vec2 v_uv;
void main() {
  v_uv = a_uv;
  gl_Position = vec4(a_pos.x / u_res.x * 2.0 - 1.0, 1.0 - a_pos.y / u_res.y * 2.0, 0.0, 1.0);
}`;

const GL_FS = `#version 300 es
precision highp float;
in vec2 v_uv;
uniform sampler2D u_tex;
uniform vec4 u_color;
out vec4 o_color;
void main() {
  o_color = texture(u_tex, v_uv) * u_color;
}`;

function createGlRenderer(cv) {
  const gl = cv.getContext('webgl2', { alpha: false, antialias: false, depth: false, stencil: false });
  if (!gl) return null;
  const ATLAS_W = 4096, ATLAS_H = 2048;
  const r = { lost: false, layoutScale: 0, layoutDpr: 0, entries: [] };
  let uRes, uColor, buf, atlasTex, bgTex, atlas = { gen: 0 }, atlasFull = false;
  let verts = new Float32Array(24 * 512);
  let n = 0; // floats used
  const scratch = document.createElement('canvas');
  const sctx = scratch.getContext('2d');

  function compile(type, src) {
    const sh = gl.createShader(type);
    gl.shaderSource(sh, src);
    gl.compileShader(sh);
    if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(sh));
    return sh;
  }

  function texture(filter, wrap) {
    const tex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, filter === 'mip' ? gl.LINEAR_MIPMAP_LINEAR : filter);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, filter === gl.NEAREST ? gl.NEAREST : gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, wrap);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, wrap);
    return tex;
  }

  function init() {
    const prog = gl.createProgram();
    gl.attachShader(prog, compile(gl.VERTEX_SHADER, GL_VS));
    gl.attachShader(prog, compile(gl.FRAGMENT_SHADER, GL_FS));
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(prog));
    gl.useProgram(prog);
    uRes = gl.getUniformLocation(prog, 'u_res');
    uColor = gl.getUniformLocation(prog, 'u_color');
    gl.bindVertexArray(gl.createVertexArray());
    buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    const aPos = gl.getAttribLocation(prog, 'a_pos'), aUv = gl.getAttribLocation(prog, 'a_uv');
    gl.enableVertexAttribArray(aPos);
    gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, 16, 0);
    gl.enableVertexAttribArray(aUv);
    gl.vertexAttribPointer(aUv, 2, gl.FLOAT, false, 16, 8);
    // Everything is premultiplied: tiles carry the fog alpha, labels are antialiased text
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);

    // Labels are stamped 1:1 at whole pixels, so no filtering
    // Smooth sampling, so labels can slide by fractions of a pixel with the map; on whole pixels it's as crisp as nearest
    atlasTex = texture(gl.LINEAR, gl.CLAMP_TO_EDGE);
    gl.texStorage2D(gl.TEXTURE_2D, 1, gl.RGBA8, ATLAS_W, ATLAS_H);
    resetAtlas();
    bgTex = null;
    for (const t of tiles.values()) { t.tex = null; t.glDirty = true; }
    r.layoutScale = 0;
  }

  // Shelf packing; a 4x4 white block at the origin is used for solid quads (POI outlines)
  function resetAtlas() {
    atlas = { gen: atlas.gen + 1, x: 6, y: 0, rowH: 6 };
    atlasFull = false;
    gl.bindTexture(gl.TEXTURE_2D, atlasTex);
    gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, 4, 4, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(64).fill(255));
  }

  function alloc(w, h) {
    if (atlas.x + w > ATLAS_W) { atlas.y += atlas.rowH; atlas.x = 0; atlas.rowH = 0; }
    if (atlas.y + h > ATLAS_H) return null;
    const s = { x: atlas.x, y: atlas.y };
    atlas.x += w + 1;
    atlas.rowH = Math.max(atlas.rowH, h + 1);
    return s;
  }

  // The POI's label (outlined name + skulls) in the atlas, drawn the first time it is needed
  function labelSlot(p, dpr) {
    const skullIcon = iconFor(SKULL, SKULL_COLOR);
    const slot = p.slot;
    const k = sizeOf('poi');
    const fontPx = 12 * dpr * k, skull = Math.round(11 * dpr * k), pad = Math.ceil(2 * dpr);
    if (slot && slot.gen === atlas.gen && slot.fontPx === fontPx && slot.skull === !!skullIcon) return slot;
    const w = Math.ceil(p.tw + (p.tier ? 3 * dpr + p.tier * skull : 0)) + pad * 2;
    const h = Math.ceil(fontPx * 1.4) + pad * 2;
    const s = alloc(w, h);
    if (!s) { atlasFull = true; return null; }
    scratch.width = w;
    scratch.height = h;
    setPoiFont(sctx, dpr);
    sctx.lineWidth = 3 * dpr;
    sctx.strokeStyle = 'rgba(0,0,0,.75)';
    sctx.strokeText(p.name, pad, h / 2);
    sctx.fillStyle = '#f3ead2';
    sctx.fillText(p.name, pad, h / 2);
    if (p.tier && skullIcon) {
      for (let i = 0; i < p.tier; i++) sctx.drawImage(skullIcon, pad + p.tw + 3 * dpr + i * skull, h / 2 - skull / 2, skull, skull);
    }
    gl.bindTexture(gl.TEXTURE_2D, atlasTex);
    gl.texSubImage2D(gl.TEXTURE_2D, 0, s.x, s.y, gl.RGBA, gl.UNSIGNED_BYTE, scratch);
    p.slot = { x: s.x, y: s.y, w, h, pad, gen: atlas.gen, fontPx, skull: !!skullIcon };
    return p.slot;
  }

  function quad(x0, y0, x1, y1, u0, v0, u1, v1) {
    if (n + 24 > verts.length) {
      const bigger = new Float32Array(verts.length * 2);
      bigger.set(verts);
      verts = bigger;
    }
    const v = verts;
    v[n++] = x0; v[n++] = y0; v[n++] = u0; v[n++] = v0;
    v[n++] = x1; v[n++] = y0; v[n++] = u1; v[n++] = v0;
    v[n++] = x0; v[n++] = y1; v[n++] = u0; v[n++] = v1;
    v[n++] = x0; v[n++] = y1; v[n++] = u0; v[n++] = v1;
    v[n++] = x1; v[n++] = y0; v[n++] = u1; v[n++] = v0;
    v[n++] = x1; v[n++] = y1; v[n++] = u1; v[n++] = v1;
  }

  function uploadTile(t) {
    if (!t.tex) {
      t.tex = texture('mip', gl.CLAMP_TO_EDGE);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, t.img);
    } else {
      gl.bindTexture(gl.TEXTURE_2D, t.tex);
      gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, gl.RGBA, gl.UNSIGNED_BYTE, t.img);
    }
    gl.generateMipmap(gl.TEXTURE_2D);
    t.glDirty = false;
  }

  // POI layout in zoomed world pixels (origin at world 0,0), redone only when zoom, settings, markers or
  // explored POIs change. Each frame just offsets it.
  function relayout(dpr) {
    if (atlasFull || r.layoutDpr !== dpr) resetAtlas();
    paint = { x: 0, z: 0, w: 0, h: 0 };
    r.entries = layoutPois(sctx, -Infinity, -Infinity, Infinity, Infinity, dpr);
    paint = null;
    r.layoutScale = view.scale;
    r.layoutDpr = dpr;
    layer.valid = true;
  }

  r.draw = function (W, H, dpr) {
    if (!layer.valid || r.layoutScale !== view.scale || r.layoutDpr !== dpr || atlasFull) relayout(dpr);
    pendingPatch.clear();
    gl.viewport(0, 0, W, H);
    gl.clearColor(0x7d / 255, 0x7d / 255, 0x7d / 255, 1);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.uniform2f(uRes, W, H);
    n = 0;
    const s = view.scale * dpr;
    const [ox, oy] = worldToScreen(0, 0);

    // Background: the concrete texture repeats every 2048 blocks
    let bgQuads = 0;
    if (bgImage) {
      if (!bgTex) {
        bgTex = texture('mip', gl.REPEAT);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, bgImage);
        gl.generateMipmap(gl.TEXTURE_2D);
      }
      const k = 2048 * s;
      quad(0, 0, W, H, -ox / k, -oy / k, (W - ox) / k, (H - oy) / k);
      bgQuads = 1;
    }

    const size = TILE_PX * s;
    const drawn = [];
    for (const t of tiles.values()) {
      const [x, y] = worldToScreen(t.rx * TILE_PX, (t.rz + 1) * TILE_PX);
      if (x > W || y > H || x + size < 0 || y + size < 0) continue;
      if (t.glDirty || !t.tex) uploadTile(t);
      quad(x, y, x + size, y + size, 0, 0, 1, 1);
      drawn.push(t.tex);
    }

    // POI outlines (solid white quads), then labels
    const outlineStart = n / 4;
    const wu = 2 / ATLAS_W, wv = 2 / ATLAS_H, lw = 1 * dpr, hl = lw / 2;
    const visible = [];
    for (const e of r.entries) {
      if (e.bx1 + ox < 0 || e.by1 + oy < 0 || e.bx0 + ox > W || e.by0 + oy > H) continue;
      visible.push(e);
      if (!e.outline) continue;
      const x0 = e.x - e.hw + ox, x1 = e.x + e.hw + ox, y0 = e.y - e.hh + oy, y1 = e.y + e.hh + oy;
      quad(x0 - hl, y0 - hl, x1 + hl, y0 + hl, wu, wv, wu, wv);
      quad(x0 - hl, y1 - hl, x1 + hl, y1 + hl, wu, wv, wu, wv);
      quad(x0 - hl, y0 + hl, x0 + hl, y1 - hl, wu, wv, wu, wv);
      quad(x1 - hl, y0 + hl, x1 + hl, y1 - hl, wu, wv, wu, wv);
    }
    // Labels keep their whole-pixel layout relative to the map and share its fraction-of-a-pixel offset
    const labelStart = n / 4, fx = ox - Math.round(ox), fy = oy - Math.round(oy);
    for (const e of visible) {
      if (e.ly === null) continue;
      const slot = labelSlot(e.p, dpr);
      if (!slot) continue;
      const x = Math.round(e.x0 + ox - fx) + fx - slot.pad, y = Math.round(e.ly + oy - fy - slot.h / 2) + fy;
      quad(x, y, x + slot.w, y + slot.h, slot.x / ATLAS_W, slot.y / ATLAS_H,
        (slot.x + slot.w) / ATLAS_W, (slot.y + slot.h) / ATLAS_H);
    }
    const end = n / 4;

    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, verts.subarray(0, n), gl.STREAM_DRAW);
    gl.uniform4f(uColor, 1, 1, 1, 1);
    if (bgQuads) {
      gl.bindTexture(gl.TEXTURE_2D, bgTex);
      gl.drawArrays(gl.TRIANGLES, 0, 6);
    }
    for (let i = 0; i < drawn.length; i++) {
      gl.bindTexture(gl.TEXTURE_2D, drawn[i]);
      gl.drawArrays(gl.TRIANGLES, (bgQuads + i) * 6, 6);
    }
    gl.bindTexture(gl.TEXTURE_2D, atlasTex);
    if (labelStart > outlineStart) {
      gl.uniform4f(uColor, 0.22, 0.22, 0.22, 0.22); // white at 22%, premultiplied
      gl.drawArrays(gl.TRIANGLES, outlineStart, labelStart - outlineStart);
    }
    if (end > labelStart) {
      gl.uniform4f(uColor, 1, 1, 1, 1);
      gl.drawArrays(gl.TRIANGLES, labelStart, end - labelStart);
    }
  };

  r.forgetTiles = function () {
    for (const t of tiles.values()) if (t.tex) gl.deleteTexture(t.tex);
  };

  cv.addEventListener('webglcontextlost', (e) => { e.preventDefault(); r.lost = true; });
  cv.addEventListener('webglcontextrestored', () => { init(); r.lost = false; requestDraw(); });

  try {
    init();
  } catch (err) {
    console.warn('WebGL renderer failed to start', err);
    return null;
  }
  return r;
}
