using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using SpaceWizards.HttpListener;

namespace WebMap
{
    // Local web server for the companion page. Bound to 127.0.0.1 only.
    // The main thread only enqueues messages (PostChunk etc.); the pusher thread keeps a mirror of the map,
    // and the listener's threads answer requests from that mirror.
    public static class WebHost
    {
        private const byte KindChunk = 1;
        private const byte KindPosition = 2;
        private const byte KindWorld = 3;
        private const byte KindReset = 4;
        private const byte KindState = 5;
        private const byte KindNav = 6;
        private const byte KindNavRemove = 7;
        private const byte KindView = 8;
        private const byte KindPois = 9;
        private const byte KindWorkstations = 10;

        private struct Msg
        {
            public byte Kind;
            public int X, Z;
            public float A, B, C, D;
            public ushort[] Data;
            public string Text, Text2, Text3;
            public int Color, Layer, Entity;
            public bool Flag;
            public List<PoiInfo> Pois;
            public List<string> Set, Del;
            public long Stamp;
            public long GameMs;
        }

        // One POI for the page's labels, copied on the main thread
        public struct PoiInfo
        {
            public string Name;
            public int X, Z, SizeX, SizeZ, Tier;
            public bool Trader;
        }

        private class MirrorChunk
        {
            public int X, Z;
            public int Seq;
            public readonly ushort[] Colors = new ushort[256];
        }

        private class SseClient
        {
            public HttpListenerResponse Response;
            public Stream Stream;
        }

        private static readonly ConcurrentQueue<Msg> inbox = new ConcurrentQueue<Msg>();
        private static readonly ConcurrentBag<ushort[]> bufferPool = new ConcurrentBag<ushort[]>();
        private static readonly AutoResetEvent wake = new AutoResetEvent(false);

        private static HttpListener listener;
        private static Thread pusher;
        private static volatile bool running;
        private static string webRoot;
        private static int port;

        // Map mirror: written by the pusher thread, read by request threads
        private static readonly object mirrorLock = new object();
        private static readonly Dictionary<long, MirrorChunk> mirror = new Dictionary<long, MirrorChunk>();
        private static int seq;
        private static int epoch = 1;
        private static string worldJson;
        private static string posJson;
        private static string stateJson = StateJson(Companion.StateMenu);

        // Map markers by nav object key, as JSON: written by the pusher thread, read when a browser connects
        private static readonly object navLock = new object();
        private static readonly Dictionary<int, string> navs = new Dictionary<int, string>();
        private static readonly List<string> navSet = new List<string>();
        private static readonly List<int> navDel = new List<int>();

        // Workstations by id, as JSON (WorkstationTracker.Snapshot): written by the pusher thread, read when a browser
        // connects. wsNote says why there are none, when it's not just that there aren't any.
        private static readonly Dictionary<string, string> workstations = new Dictionary<string, string>();
        private static string wsNote = "";

        // Folders the page's item icons come from: the game's, then each mod's (set on the main thread at startup)
        private static readonly List<string> itemIconDirs = new List<string>();

        // Page settings (CompanionSettings) and the world's POIs, as JSON
        private static string viewJson;
        private static volatile string poisJson;
        private static int poisVersion;

        // View changes from the page, applied by the main thread: "follow=<self|free|entity id>" or "zoom=<scale>"
        public static readonly ConcurrentQueue<string> ViewRequests = new ConcurrentQueue<string>();

        private static readonly object clientsLock = new object();
        private static readonly List<SseClient> clients = new List<SseClient>();
        private static volatile int clientCount;

        public static int ClientCount => clientCount;

        public static string Url => "http://127.0.0.1:" + port + "/";

        public static string Describe()
        {
            int chunks;
            lock (mirrorLock)
            {
                chunks = mirror.Count;
            }
            return (running ? "listening on " + Url : "not running") +
                   ", browsers=" + clientCount + ", mirrored chunks=" + chunks + ", seq=" + seq + ", epoch=" + epoch;
        }

        public static void Start(int _port, string _webRoot)
        {
            port = _port;
            webRoot = _webRoot;
            if (!HttpListener.IsSupported)
            {
                Log.Error("[WebMap] HttpListener not supported on this platform, web map disabled");
                return;
            }
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add(Url);
                listener.Start();
            }
            catch (Exception ex)
            {
                Log.Error("[WebMap] Could not listen on " + Url + " (port in use?): " + ex.Message);
                listener = null;
                return;
            }

            running = true;
            pusher = new Thread(PushLoop) { IsBackground = true, Name = "WebMap pusher" };
            pusher.Start();
            listener.BeginGetContext(OnContext, null);
            Log.Out("[WebMap] Open " + Url + " in a browser");
        }

        public static void Stop()
        {
            if (!running) return;
            running = false;
            wake.Set();
            try
            {
                listener.Close();
            }
            catch (Exception)
            {
                // Shutting down anyway
            }
            lock (clientsLock)
            {
                foreach (SseClient c in clients) CloseQuietly(c);
                clients.Clear();
                clientCount = 0;
            }
        }

        // ---- Main-thread API: enqueue only ----

        public static ushort[] RentBuffer()
        {
            ushort[] buf;
            return bufferPool.TryTake(out buf) ? buf : new ushort[256];
        }

        public static void PostChunk(int _chunkX, int _chunkZ, ushort[] _colors)
        {
            inbox.Enqueue(new Msg { Kind = KindChunk, X = _chunkX, Z = _chunkZ, Data = _colors });
            wake.Set();
        }

        public static void PostPosition(float _x, float _y, float _z, float _yaw, long _gameMs)
        {
            // Unix ms when the game posted it, so the page can tell game pauses from delivery delays
            long stamp = (DateTime.UtcNow.Ticks - 621355968000000000L) / 10000L;
            inbox.Enqueue(new Msg { Kind = KindPosition, A = _x, B = _y, C = _z, D = _yaw, Stamp = stamp, GameMs = _gameMs });
            wake.Set();
        }

        public static void PostWorld(int _minX, int _minZ, int _maxX, int _maxZ, string _playerName, string _worldName)
        {
            inbox.Enqueue(new Msg { Kind = KindWorld, X = _minX, Z = _minZ, A = _maxX, B = _maxZ, Text = _playerName, Text2 = _worldName });
            wake.Set();
        }

        public static void PostState(int _state)
        {
            inbox.Enqueue(new Msg { Kind = KindState, X = _state });
            wake.Set();
        }

        public static void PostNav(int _key, float _x, float _z, float _rot, string _sprite, string _label, int _color,
            float _scaleX, float _scaleY, int _layer, bool _adjustCenter, string _category, int _entityId)
        {
            inbox.Enqueue(new Msg
            {
                Kind = KindNav, X = _key, A = _x, B = _z, C = _rot, D = _scaleX, Z = (int)(_scaleY * 1000f),
                Text = _sprite, Text2 = _label, Text3 = _category, Color = _color, Layer = _layer, Flag = _adjustCenter,
                Entity = _entityId
            });
            wake.Set();
        }

        public static void PostView(string _json)
        {
            inbox.Enqueue(new Msg { Kind = KindView, Text = _json });
            wake.Set();
        }

        // Takes ownership of the list
        public static void PostPois(List<PoiInfo> _pois)
        {
            inbox.Enqueue(new Msg { Kind = KindPois, Pois = _pois });
            wake.Set();
        }

        public static void PostNavRemove(int _key)
        {
            inbox.Enqueue(new Msg { Kind = KindNavRemove, X = _key });
            wake.Set();
        }

        // A batch from the server; full replaces everything. The lists are copied, so callers can reuse theirs.
        public static void PostWorkstations(bool _full, List<string> _set, List<string> _del, string _note = "")
        {
            inbox.Enqueue(new Msg { Kind = KindWorkstations, Flag = _full, Set = new List<string>(_set), Del = new List<string>(_del), Text = _note });
            wake.Set();
        }

        public static void SetItemIconDirs(List<string> _dirs)
        {
            lock (itemIconDirs)
            {
                itemIconDirs.Clear();
                itemIconDirs.AddRange(_dirs);
            }
        }

        public static void PostReset()
        {
            inbox.Enqueue(new Msg { Kind = KindReset });
            wake.Set();
        }

        // ---- Pusher thread ----

        private static void PushLoop()
        {
            DateTime nextKeepAlive = DateTime.UtcNow.AddSeconds(15);
            while (running)
            {
                wake.WaitOne(1000);
                try
                {
                    DrainInbox();
                    if (DateTime.UtcNow >= nextKeepAlive)
                    {
                        nextKeepAlive = DateTime.UtcNow.AddSeconds(15);
                        Broadcast(": keepalive\n\n");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[WebMap] Pusher error: " + ex);
                }
            }
        }

        private static void DrainInbox()
        {
            bool mapChanged = false;
            string pendingPos = null;
            Msg m;
            while (inbox.TryDequeue(out m))
            {
                switch (m.Kind)
                {
                    case KindChunk:
                        if (ApplyChunk(m.X, m.Z, m.Data)) mapChanged = true;
                        bufferPool.Add(m.Data);
                        break;
                    case KindPosition:
                        pendingPos = "{\"x\":" + F(m.A) + ",\"y\":" + F(m.B) + ",\"z\":" + F(m.C) + ",\"yaw\":" + F(m.D) + ",\"ts\":" + m.Stamp + ",\"gt\":" + m.GameMs + "}";
                        break;
                    case KindWorld:
                        worldJson = "{\"minX\":" + m.X + ",\"minZ\":" + m.Z + ",\"maxX\":" + (int)m.A + ",\"maxZ\":" + (int)m.B +
                                    ",\"player\":" + JsonString(m.Text) + ",\"name\":" + JsonString(m.Text2) + "}";
                        Broadcast(SseEvent("world", worldJson));
                        break;
                    case KindState:
                        stateJson = StateJson(m.X);
                        Broadcast(SseEvent("state", stateJson));
                        break;
                    case KindNav:
                        string json = NavJson(m);
                        lock (navLock)
                        {
                            navs[m.X] = json;
                        }
                        navSet.Add(json);
                        break;
                    case KindNavRemove:
                        lock (navLock)
                        {
                            navs.Remove(m.X);
                        }
                        navDel.Add(m.X);
                        break;
                    case KindView:
                        viewJson = m.Text;
                        Broadcast(SseEvent("view", viewJson));
                        break;
                    case KindPois:
                        poisJson = PoisJson(m.Pois);
                        poisVersion++;
                        Broadcast(SseEvent("pois", "{\"v\":" + poisVersion + "}"));
                        break;
                    case KindWorkstations:
                        Broadcast(SseEvent("ws", ApplyWorkstations(m)));
                        break;
                    case KindReset:
                        int e;
                        lock (mirrorLock)
                        {
                            mirror.Clear();
                            seq = 0;
                            e = ++epoch;
                        }
                        lock (navLock)
                        {
                            navs.Clear();
                            workstations.Clear();
                            wsNote = "";
                        }
                        navSet.Clear();
                        navDel.Clear();
                        posJson = null;
                        poisJson = null;
                        mapChanged = false;
                        Broadcast(SseEvent("reset", "{\"epoch\":" + e + "}"));
                        break;
                }
            }
            if (mapChanged)
            {
                Broadcast(SseEvent("map", MapStateJson()));
            }
            if (pendingPos != null)
            {
                posJson = pendingPos;
                Broadcast(SseEvent("pos", pendingPos));
            }
            if (navSet.Count > 0 || navDel.Count > 0)
            {
                Broadcast(SseEvent("navs", NavBatchJson(false, navSet, navDel)));
                navSet.Clear();
                navDel.Clear();
            }
        }

        // {"full":bool,"note":"...","set":[snapshot,...],"del":[id,...]}
        private static string ApplyWorkstations(Msg _m)
        {
            lock (navLock)
            {
                if (_m.Flag) workstations.Clear();
                foreach (string json in _m.Set)
                {
                    // "id" is always the first field: {"id":"x,y,z",...
                    int end = json.IndexOf('"', 7);
                    if (json.StartsWith("{\"id\":\"") && end > 7) workstations[json.Substring(7, end - 7)] = json;
                }
                foreach (string id in _m.Del) workstations.Remove(id);
                wsNote = _m.Text ?? "";
            }
            return WorkstationBatchJson(_m.Flag, _m.Set, _m.Del);
        }

        private static string WorkstationBatchJson(bool _full, ICollection<string> _set, List<string> _del)
        {
            StringBuilder sb = new StringBuilder("{\"full\":").Append(_full ? "true" : "false");
            sb.Append(",\"note\":").Append(JsonString(wsNote)).Append(",\"set\":[");
            bool first = true;
            foreach (string json in _set)
            {
                if (!first) sb.Append(',');
                sb.Append(json);
                first = false;
            }
            sb.Append("],\"del\":[");
            for (int i = 0; i < _del.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonString(_del[i]));
            }
            return sb.Append("]}").ToString();
        }

        // k key, x/z world position, r rotation, s sprite, l label, c color #rrggbbaa, sx/sy icon scale,
        // layer draw order, ac AdjustCenter, cat category, e entity id (players only)
        private static string NavJson(Msg _m)
        {
            return "{\"k\":" + _m.X + ",\"x\":" + F(_m.A) + ",\"z\":" + F(_m.B) + ",\"r\":" + F(_m.C) +
                   ",\"s\":" + JsonString(_m.Text) + ",\"l\":" + JsonString(_m.Text2) +
                   ",\"c\":\"#" + ((uint)_m.Color).ToString("x8") + "\"" +
                   ",\"sx\":" + F(_m.D) + ",\"sy\":" + F(_m.Z / 1000f) + ",\"layer\":" + _m.Layer +
                   ",\"ac\":" + (_m.Flag ? "true" : "false") + ",\"cat\":" + JsonString(_m.Text3) +
                   (_m.Entity >= 0 ? ",\"e\":" + _m.Entity : "") + "}";
        }

        // [[name, centerX, centerZ, sizeX, sizeZ, tier, trader 0/1], ...]
        private static string PoisJson(List<PoiInfo> _pois)
        {
            StringBuilder sb = new StringBuilder(_pois.Count * 48).Append('[');
            for (int i = 0; i < _pois.Count; i++)
            {
                PoiInfo p = _pois[i];
                if (i > 0) sb.Append(',');
                sb.Append('[').Append(JsonString(p.Name)).Append(',').Append(p.X).Append(',').Append(p.Z)
                    .Append(',').Append(p.SizeX).Append(',').Append(p.SizeZ).Append(',').Append(p.Tier)
                    .Append(',').Append(p.Trader ? 1 : 0).Append(']');
            }
            return sb.Append(']').ToString();
        }

        private static string NavBatchJson(bool _full, ICollection<string> _set, List<int> _del)
        {
            StringBuilder sb = new StringBuilder("{\"full\":").Append(_full ? "true" : "false").Append(",\"set\":[");
            bool first = true;
            foreach (string json in _set)
            {
                if (!first) sb.Append(',');
                sb.Append(json);
                first = false;
            }
            sb.Append("],\"del\":[");
            for (int i = 0; i < _del.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_del[i]);
            }
            return sb.Append("]}").ToString();
        }

        // Returns true if the chunk is new or its colors changed. The player re-adds a 9x9 area on every
        // chunk crossing, so most incoming chunks are unchanged and get dropped here.
        private static bool ApplyChunk(int _x, int _z, ushort[] _colors)
        {
            long key = WorldChunkCache.MakeChunkKey(_x, _z);
            lock (mirrorLock)
            {
                MirrorChunk mc;
                if (!mirror.TryGetValue(key, out mc))
                {
                    mc = new MirrorChunk { X = _x, Z = _z };
                    mirror.Add(key, mc);
                }
                else if (SameColors(mc.Colors, _colors))
                {
                    return false;
                }
                Array.Copy(_colors, mc.Colors, 256);
                mc.Seq = ++seq;
                return true;
            }
        }

        private static bool SameColors(ushort[] _a, ushort[] _b)
        {
            for (int i = 0; i < 256; i++)
            {
                if (_a[i] != _b[i]) return false;
            }
            return true;
        }

        private static string MapStateJson()
        {
            lock (mirrorLock)
            {
                return "{\"epoch\":" + epoch + ",\"seq\":" + seq + "}";
            }
        }

        private static string StateJson(int _state)
        {
            string name = _state == Companion.StateInGame ? "ingame" : _state == Companion.StateLoading ? "loading" : "menu";
            return "{\"state\":\"" + name + "\"}";
        }

        private static string JsonString(string _s)
        {
            if (_s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in _s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static string F(float _v)
        {
            return _v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string SseEvent(string _name, string _json)
        {
            return "event: " + _name + "\ndata: " + _json + "\n\n";
        }

        private static void Broadcast(string _text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(_text);
            lock (clientsLock)
            {
                for (int i = clients.Count - 1; i >= 0; i--)
                {
                    if (Send(clients[i], bytes)) continue;
                    CloseQuietly(clients[i]);
                    clients.RemoveAt(i);
                }
                clientCount = clients.Count;
            }
        }

        private static bool Send(SseClient _client, byte[] _bytes)
        {
            try
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                _client.Stream.Write(_bytes, 0, _bytes.Length);
                _client.Stream.Flush();
                if (sw.ElapsedMilliseconds > 100)
                {
                    // A tab that stops reading (asleep, frozen) blocks every other tab's updates here
                    Log.Warning("[WebMap] Slow write to a browser tab: " + sw.ElapsedMilliseconds + " ms, " +
                                clients.Count + " tabs open");
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void CloseQuietly(SseClient _client)
        {
            try
            {
                _client.Response.Close();
            }
            catch (Exception)
            {
                // Browser already gone
            }
        }

        // ---- Request handling (listener threads) ----

        private static void OnContext(IAsyncResult _result)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = listener.EndGetContext(_result);
            }
            catch (Exception)
            {
                return; // listener closed
            }
            if (running)
            {
                try
                {
                    listener.BeginGetContext(OnContext, null);
                }
                catch (Exception ex)
                {
                    Log.Error("[WebMap] Listener stopped accepting requests: " + ex.Message);
                }
            }

            try
            {
                Handle(ctx);
            }
            catch (Exception ex)
            {
                Log.Warning("[WebMap] Request " + ctx.Request.RawUrl + " failed: " + ex.Message);
                try
                {
                    ctx.Response.Abort();
                }
                catch (Exception)
                {
                    // Already closed
                }
            }
        }

        private static void Handle(HttpListenerContext _ctx)
        {
            HttpListenerRequest req = _ctx.Request;
            HttpListenerResponse res = _ctx.Response;

            // Only answer pages loaded from this address. Stops other websites from reading the map
            // through DNS rebinding.
            string host = req.Headers["Host"];
            if (host != "127.0.0.1:" + port && host != "localhost:" + port)
            {
                Finish(res, 403, "text/plain", Encoding.UTF8.GetBytes("Forbidden"));
                return;
            }

            string path = req.Url.AbsolutePath;
            if (path == "/events")
            {
                OpenEventStream(res);
                return;
            }
            if (path == "/map")
            {
                ServeMap(req, res);
                return;
            }
            if (path == "/pois")
            {
                string pois = poisJson;
                Finish(res, 200, "application/json", Encoding.UTF8.GetBytes(pois ?? "[]"));
                return;
            }
            if (path == "/view")
            {
                // Custom header: other websites can't send it without a CORS preflight, which this server never allows
                if (req.HttpMethod != "POST" || req.Headers["X-WebMap"] != "1")
                {
                    Finish(res, 400, "text/plain", Encoding.UTF8.GetBytes("Bad request"));
                    return;
                }
                string follow = req.QueryString["follow"];
                if (!string.IsNullOrEmpty(follow) && follow.Length <= 16) ViewRequests.Enqueue("follow=" + follow);
                string zoom = req.QueryString["zoom"];
                if (!string.IsNullOrEmpty(zoom) && zoom.Length <= 8) ViewRequests.Enqueue("zoom=" + zoom);
                Finish(res, 204, "text/plain", new byte[0]);
                return;
            }
            if (path.StartsWith("/itemicons/"))
            {
                ServeItemIcon(path.Substring(11), res);
                return;
            }
            if (path == "/diag")
            {
                // The page reports position stalls here so they land in the game log
                if (req.HttpMethod != "POST" || req.Headers["X-WebMap"] != "1")
                {
                    Finish(res, 400, "text/plain", Encoding.UTF8.GetBytes("Bad request"));
                    return;
                }
                string msg = req.QueryString["m"];
                if (!string.IsNullOrEmpty(msg) && msg.Length <= 300) Log.Out("[WebMap] Page: " + msg);
                Finish(res, 204, "text/plain", new byte[0]);
                return;
            }
            ServeStatic(path, res);
        }

        private static void OpenEventStream(HttpListenerResponse _res)
        {
            _res.StatusCode = 200;
            _res.ContentType = "text/event-stream";
            _res.Headers["Cache-Control"] = "no-cache";
            _res.SendChunked = true;
            SseClient client = new SseClient { Response = _res, Stream = _res.OutputStream };

            StringBuilder hello = new StringBuilder("retry: 2000\n\n");
            hello.Append(SseEvent("state", stateJson));
            hello.Append(SseEvent("map", MapStateJson()));
            string world = worldJson;
            if (world != null) hello.Append(SseEvent("world", world));
            string pos = posJson;
            if (pos != null) hello.Append(SseEvent("pos", pos));
            lock (navLock)
            {
                hello.Append(SseEvent("navs", NavBatchJson(true, navs.Values, new List<int>())));
                hello.Append(SseEvent("ws", WorkstationBatchJson(true, workstations.Values, new List<string>())));
            }
            string view = viewJson;
            if (view != null) hello.Append(SseEvent("view", view));
            if (poisJson != null) hello.Append(SseEvent("pois", "{\"v\":" + poisVersion + "}"));

            lock (clientsLock)
            {
                if (!Send(client, Encoding.UTF8.GetBytes(hello.ToString())))
                {
                    CloseQuietly(client);
                    return;
                }
                clients.Add(client);
                clientCount = clients.Count;
            }
            Log.Out("[WebMap] Browser connected (" + clientCount + " open)");
        }

        // Binary: int32 epoch, int32 seq, int32 count, then count x (int32 chunkX, int32 chunkZ, 256 x uint16 RGB555).
        // Little-endian. Returns chunks changed after ?since=N; everything if ?epoch doesn't match.
        private static void ServeMap(HttpListenerRequest _req, HttpListenerResponse _res)
        {
            int since, reqEpoch;
            int.TryParse(_req.QueryString["since"], out since);
            int.TryParse(_req.QueryString["epoch"], out reqEpoch);

            byte[] body;
            lock (mirrorLock)
            {
                if (reqEpoch != epoch) since = 0;
                int count = 0;
                foreach (MirrorChunk mc in mirror.Values)
                {
                    if (mc.Seq > since) count++;
                }
                body = new byte[12 + count * 520];
                WriteInt(body, 0, epoch);
                WriteInt(body, 4, seq);
                WriteInt(body, 8, count);
                int off = 12;
                foreach (MirrorChunk mc in mirror.Values)
                {
                    if (mc.Seq <= since) continue;
                    WriteInt(body, off, mc.X);
                    WriteInt(body, off + 4, mc.Z);
                    Buffer.BlockCopy(mc.Colors, 0, body, off + 8, 512);
                    off += 520;
                }
            }
            Finish(_res, 200, "application/octet-stream", body);
        }

        private static void WriteInt(byte[] _buf, int _off, int _v)
        {
            _buf[_off] = (byte)_v;
            _buf[_off + 1] = (byte)(_v >> 8);
            _buf[_off + 2] = (byte)(_v >> 16);
            _buf[_off + 3] = (byte)(_v >> 24);
        }

        private static void ServeStatic(string _path, HttpListenerResponse _res)
        {
            string name = _path == "/" ? "index.html" : _path.TrimStart('/');
            // Simple names only, plus the icons/ folder: no directory traversal
            if (name.StartsWith("icons/")) name = "icons" + Path.DirectorySeparatorChar + name.Substring(6);
            foreach (char c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == Path.DirectorySeparatorChar) || name.Contains(".."))
                {
                    Finish(_res, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
                    return;
                }
            }
            string file = Path.Combine(webRoot, name);
            if (!File.Exists(file))
            {
                Finish(_res, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
                return;
            }
            Finish(_res, 200, ContentType(name), File.ReadAllBytes(file));
        }

        // An item's icon from the game's (or a mod's) ItemIcons folder, by icon name
        private static void ServeItemIcon(string _name, HttpListenerResponse _res)
        {
            if (_name.EndsWith(".png")) _name = _name.Substring(0, _name.Length - 4);
            foreach (char c in _name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                {
                    Finish(_res, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
                    return;
                }
            }
            string file = null;
            lock (itemIconDirs)
            {
                foreach (string dir in itemIconDirs)
                {
                    string f = Path.Combine(dir, _name + ".png");
                    if (!File.Exists(f)) continue;
                    file = f;
                    break;
                }
            }
            if (file == null || _name.Length == 0)
            {
                Finish(_res, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
                return;
            }
            _res.Headers["Cache-Control"] = "max-age=3600";
            Finish(_res, 200, "image/png", File.ReadAllBytes(file));
        }

        private static string ContentType(string _name)
        {
            string ext = Path.GetExtension(_name).ToLowerInvariant();
            switch (ext)
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "text/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".png": return "image/png";
                case ".svg": return "image/svg+xml";
                default: return "application/octet-stream";
            }
        }

        private static void Finish(HttpListenerResponse _res, int _status, string _contentType, byte[] _body)
        {
            _res.StatusCode = _status;
            _res.ContentType = _contentType;
            if (_res.Headers["Cache-Control"] == null) _res.Headers["Cache-Control"] = "no-cache";
            _res.ContentLength64 = _body.Length;
            _res.OutputStream.Write(_body, 0, _body.Length);
            _res.Close();
        }
    }
}
