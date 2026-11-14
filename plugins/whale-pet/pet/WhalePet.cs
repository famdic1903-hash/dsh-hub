// WhalePet.cs - DSH desktop pet: transparent, topmost, draggable, heartbeat-managed.
// Target: .NET Framework 4.x / C# 5 (compile with csc.exe only, no extra assemblies).
//
// Usage: WhalePet.exe "<absolute path to config.json>"
// Config fields: imagePath, posPath, apiBase, size, x, y, idleBob
//
// Protocol:
//   GET  {apiBase}/api/whale-pet/ping   every 3000 ms; 4 consecutive failures -> exit(0)
//   POST {apiBase}/api/whale-pet/pos    after drag / resize / reset (physical geometry)
//   GET  {apiBase}/api/whale-pet/poke   on left click: bubble lines + press/release sounds
//   POST {apiBase}/api/whale-pet/recall on "back to DSH window" / "close"; exit(0) either way
//
// Interaction:
//   * left click (no drag) = "poke": squeeze/bounce animation, press + release sound
//     (winmm MCI on a dedicated STA COM thread - the mpegvideo/DirectShow driver
//     refuses to load on an MTA thread with rc=266) and a balance bubble above the
//     character's head. The bubble
//     is its own click-through layered window (class WhalePetBubbleWindow), so the pet
//     window geometry / drag / hit-testing / persistence semantics stay unchanged.
//   * right click = native popup menu (back to DSH window / size / reset / close).
//
// Notes:
//   * The source file is saved as UTF-8 WITH BOM so that the legacy (non-Roslyn)
//     csc.exe reads Chinese menu strings correctly.
//   * Window class name "WhalePetWindow" is stable and used by automated tests.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WhalePet
{
    internal static class Program
    {
        public const string AppVersion = "1.1.1";
        public const string WindowClassName = "WhalePetWindow";
        // The balance bubble lives in its own layered window: the pet window keeps its
        // stable size x size geometry (drag/hit-test/persistence semantics untouched).
        public const string BubbleClassName = "WhalePetBubbleWindow";
        // NOTE: intentionally no single-instance mutex. The DSH Host already de-dupes
        // pet launches (isPetAlive); a silent exit(0) from a stale invisible instance
        // looked like "clicking does nothing" to the user. A stale instance dies by
        // itself within ~12 s (heartbeat), so duplicate instances are the lesser evil.

        [STAThread]
        internal static int Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); }
            catch (Exception ex) { Logger.Error("SetProcessDPIAware", ex); }

            string cfgPath = (args != null && args.Length > 0) ? args[0] : null;
            Config cfg;
            int autoDpr = 1;
            try
            {
                if (string.IsNullOrEmpty(cfgPath)) throw new ArgumentException("missing config path; usage: WhalePet.exe <config.json>");
                cfgPath = Path.GetFullPath(cfgPath);
                autoDpr = DpiUtil.GetDefaultScale();
                cfg = Config.Load(cfgPath, autoDpr);
                Logger.Setup(cfg.PosPath);
            }
            catch (Exception ex)
            {
                Logger.SetupFallback();
                Logger.Write("FATAL config error: " + Describe(ex));
                return 2;
            }

            Logger.Write("WhalePet " + AppVersion + " starting; config=" + cfgPath);
            Logger.Write("imagePath=" + cfg.ImagePath);
            Logger.Write("posPath=" + cfg.PosPath);
            Logger.Write("apiBase=" + cfg.ApiBase + " size=" + cfg.Size + " x=" + (cfg.HasX ? cfg.X.ToString(CultureInfo.InvariantCulture) : "none") + " y=" + (cfg.HasY ? cfg.Y.ToString(CultureInfo.InvariantCulture) : "none") + " idleBob=" + cfg.IdleBob);
            Logger.Write("dpr=" + cfg.Dpr.ToString(CultureInfo.InvariantCulture) + " (" + cfg.DprSource + ")" + " autoDpr=" + autoDpr.ToString(CultureInfo.InvariantCulture) + " systemDpi=" + DpiUtil.GetSystemDpi().ToString(CultureInfo.InvariantCulture));
            if (cfg.HasPhysical)
                Logger.Write("config.physical= x=" + (cfg.HasPhysicalX ? cfg.PhysicalX.ToString(CultureInfo.InvariantCulture) : "none") + " y=" + (cfg.HasPhysicalY ? cfg.PhysicalY.ToString(CultureInfo.InvariantCulture) : "none") + " size=" + (cfg.HasPhysicalSize ? cfg.PhysicalSize.ToString(CultureInfo.InvariantCulture) : "none"));

            // No single-instance check: every launch gets its own window.
            PetApp app = new PetApp(cfg);
            int code = app.Run();
            Logger.Write("main loop finished; exit code=" + code);
            return code;
        }

        internal static string Describe(Exception ex)
        {
            if (ex == null) return "null";
            StringBuilder sb = new StringBuilder();
            Exception cur = ex;
            int depth = 0;
            while (cur != null && depth < 4)
            {
                if (depth > 0) sb.Append(" <- ");
                sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
                cur = cur.InnerException;
                depth++;
            }
            return sb.ToString();
        }
    }

    internal static class Logger
    {
        private static readonly object Sync = new object();
        private static string _path;
        private static bool _writeFailed;

        // Tries the preferred location (next to pos.json, i.e. the DSH state dir)
        // and then several fallbacks, probing real writability by opening the file.
        // This matters when the pet is spawned by the DSH Host with a token that
        // cannot write some directories.
        public static void Setup(string posPath)
        {
            List<string> candidates = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(posPath))
                {
                    string full = Path.GetFullPath(posPath);
                    string dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir)) candidates.Add(dir);
                }
            }
            catch { }
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData)) candidates.Add(Path.Combine(localAppData, "WhalePet"));
            }
            catch { }
            try
            {
                candidates.Add(Path.Combine(Path.GetTempPath(), "WhalePet"));
            }
            catch { }
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(exeDir)) candidates.Add(exeDir);
            }
            catch { }

            string preferred = candidates.Count > 0 ? candidates[0] : null;
            for (int i = 0; i < candidates.Count; i++)
            {
                string dir = candidates[i];
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    Directory.CreateDirectory(dir);
                    string p = Path.Combine(dir, "pet.log");
                    using (FileStream fs = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                    _path = p;
                    if (i > 0)
                    {
                        Write("logger fallback: could not write " + (preferred == null ? "(none)" : preferred) + ", logging to " + p);
                    }
                    return;
                }
                catch { }
            }
            _path = null;
        }

        public static void SetupFallback()
        {
            Setup(null);
        }

        public static void Write(string message)
        {
            if (_path == null) return;
            try
            {
                lock (Sync)
                {
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message + "\r\n";
                    File.AppendAllText(_path, line, new UTF8Encoding(false));
                    _writeFailed = false;
                }
            }
            catch
            {
                // Logging must never crash the pet.
                if (!_writeFailed)
                {
                    _writeFailed = true;
                    // Try the alternative fallback locations once.
                    try { Setup(null); } catch { }
                }
            }
        }

        public static void Error(string where, Exception ex)
        {
            Write("ERROR " + where + ": " + Program.Describe(ex));
        }
    }

    internal sealed class Config
    {
        public string ImagePath = "";
        public string PosPath = "";
        public string ApiBase = "";
        public int Size = 230;          // physical pixels after dpr scaling
        public bool HasX = false;
        public int X = 0;               // physical pixels after dpr scaling
        public bool HasY = false;
        public int Y = 0;
        public bool IdleBob = false;

        // Effective scale factor that converts the "size"/"x"/"y" values from DIP
        // (the units the DSH side uses, e.g. Electron screen API / CSS pixels) into
        // physical pixels.  Explicit "dpr" wins; otherwise it is auto-detected from
        // the system DPI (192 -> 2 on this machine).
        public double Dpr = 1.0;
        public bool HasDpr = false;
        public string DprSource = "default";

        // Optional "physical" object written back by the DSH Host from a previous
        // POST /api/whale-pet/pos.  Values are already physical pixels: use them
        // verbatim, never multiply again.
        public bool HasPhysical = false;
        public bool HasPhysicalSize = false;
        public int PhysicalSize = 0;
        public bool HasPhysicalX = false;
        public int PhysicalX = 0;
        public bool HasPhysicalY = false;
        public int PhysicalY = 0;

        public static Config Load(string path, int autoScale)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            JsonFlat j = JsonFlat.Parse(text);
            Config c = new Config();
            c.ImagePath = j.GetString("imagePath", "");
            c.PosPath = j.GetString("posPath", "");
            c.ApiBase = j.GetString("apiBase", "");
            c.IdleBob = j.GetBool("idleBob", false);

            // 1) effective scale: explicit dpr > auto-detected system scale.
            int v;
            double dv;
            if (j.TryGetDouble("dpr", out dv) && dv >= 0.5 && dv <= 4.0)
            {
                c.Dpr = dv;
                c.HasDpr = true;
                c.DprSource = "config";
            }
            else
            {
                int scale = autoScale;
                if (scale < 1) scale = 1;
                if (scale > 4) scale = 4;
                c.Dpr = scale;
                c.DprSource = "auto";
            }

            // 2) "physical" object (from the DSH Host) - physical pixels, no scaling.
            string raw;
            if (j.TryGetRaw("physical", out raw) && raw != null && raw.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    JsonFlat pj = JsonFlat.Parse(raw);
                    c.HasPhysical = true;
                    int pv;
                    if (pj.TryGetInt("x", out pv)) { c.PhysicalX = pv; c.HasPhysicalX = true; }
                    if (pj.TryGetInt("y", out pv)) { c.PhysicalY = pv; c.HasPhysicalY = true; }
                    if (pj.TryGetInt("size", out pv)) { c.PhysicalSize = ClampSize(pv); c.HasPhysicalSize = true; }
                }
                catch (Exception ex)
                {
                    c.HasPhysical = false;
                    c.HasPhysicalX = false;
                    c.HasPhysicalY = false;
                    c.HasPhysicalSize = false;
                    Logger.Write("WARN config.physical could not be parsed: " + Program.Describe(ex));
                }
            }

            // 3) size/x/y from config are DIP -> multiply by the effective scale.
            if (j.TryGetInt("size", out v)) c.Size = v;
            if (j.TryGetInt("x", out v)) { c.X = v; c.HasX = true; }
            if (j.TryGetInt("y", out v)) { c.Y = v; c.HasY = true; }
            if (c.Dpr != 1.0)
            {
                c.Size = (int)Math.Round(c.Size * c.Dpr);
                if (c.HasX) c.X = (int)Math.Round(c.X * c.Dpr);
                if (c.HasY) c.Y = (int)Math.Round(c.Y * c.Dpr);
            }
            c.Size = ClampSize(c.Size);

            while (c.ApiBase.EndsWith("/", StringComparison.Ordinal)) c.ApiBase = c.ApiBase.Substring(0, c.ApiBase.Length - 1);
            if (c.ImagePath.Length == 0) throw new FormatException("config: imagePath missing");
            if (c.ApiBase.Length == 0) throw new FormatException("config: apiBase missing");
            return c;
        }

        public static int ClampSize(int value)
        {
            if (value < 48) return 48;
            if (value > 1024) return 1024;
            return value;
        }
    }

    // Minimal flat JSON reader (enough for config.json and pos.json).
    internal sealed class JsonFlat
    {
        private readonly Dictionary<string, string> _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static JsonFlat Parse(string text)
        {
            if (text == null) throw new FormatException("json: empty input");
            JsonFlat o = new JsonFlat();
            int i = 0;
            SkipWs(text, ref i);
            if (i >= text.Length || text[i] != '{') throw new FormatException("json: expected '{'");
            i++;
            while (true)
            {
                SkipWs(text, ref i);
                if (i >= text.Length) throw new FormatException("json: unexpected end");
                if (text[i] == '}') { i++; break; }
                string key = ReadString(text, ref i);
                SkipWs(text, ref i);
                if (i >= text.Length || text[i] != ':') throw new FormatException("json: expected ':'");
                i++;
                SkipWs(text, ref i);
                if (i < text.Length && text[i] == '"')
                {
                    o._map[key] = ReadString(text, ref i);
                }
                else
                {
                    int start = i;
                    SkipValue(text, ref i);
                    o._map[key] = text.Substring(start, i - start).Trim();
                }
                SkipWs(text, ref i);
                if (i < text.Length && text[i] == ',') { i++; continue; }
                if (i < text.Length && text[i] == '}') { i++; break; }
                throw new FormatException("json: expected ',' or '}'");
            }
            return o;
        }

        public string GetString(string key, string fallback)
        {
            string v;
            if (_map.TryGetValue(key, out v)) return v;
            return fallback;
        }

        public bool TryGetInt(string key, out int value)
        {
            value = 0;
            string raw;
            if (!_map.TryGetValue(key, out raw) || raw == null) return false;
            return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        public bool TryGetDouble(string key, out double value)
        {
            value = 0;
            string raw;
            if (!_map.TryGetValue(key, out raw) || raw == null) return false;
            return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // Returns the raw JSON text of a nested object/array value, so it can be
        // parsed with a second JsonFlat.Parse() call (used for config.physical).
        public bool TryGetRaw(string key, out string rawValue)
        {
            rawValue = null;
            string v;
            if (!_map.TryGetValue(key, out v) || v == null) return false;
            rawValue = v;
            return true;
        }

        public bool GetBool(string key, bool fallback)
        {
            string raw;
            if (!_map.TryGetValue(key, out raw)) return fallback;
            if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static string ReadString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("json: expected string");
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) throw new FormatException("json: bad escape");
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("json: bad \\u escape");
                            int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default:
                            throw new FormatException("json: bad escape '\\" + e + "'");
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            throw new FormatException("json: unterminated string");
        }

        private static void SkipValue(string s, ref int i)
        {
            if (i >= s.Length) throw new FormatException("json: expected value");
            char c = s[i];
            if (c == '"') { ReadString(s, ref i); return; }
            if (c == '{' || c == '[')
            {
                char open = (c == '{') ? '{' : '[';
                char close = (c == '{') ? '}' : ']';
                int depth = 0;
                while (i < s.Length)
                {
                    char d = s[i];
                    if (d == '"') { ReadString(s, ref i); continue; }
                    if (d == open) { depth++; i++; continue; }
                    if (d == close)
                    {
                        depth--;
                        i++;
                        if (depth == 0) return;
                        continue;
                    }
                    i++;
                }
                throw new FormatException("json: unbalanced value");
            }
            int start = i;
            while (i < s.Length && ",}] \t\r\n".IndexOf(s[i]) < 0) i++;
            if (i == start) throw new FormatException("json: empty value");
        }
    }

    internal sealed class PosData
    {
        public bool HasX = false;
        public int X = 0;
        public bool HasY = false;
        public int Y = 0;
        public bool HasSize = false;
        public int Size = 0;
    }

    internal static class PosStore
    {
        // When the pet runs inside the DSH file sandbox, writing pos.json is denied
        // every time; log that once per process instead of spamming pet.log.
        private static bool _writeErrorLogged;

        public static PosData Read(string path)
        {
            PosData d = new PosData();
            if (string.IsNullOrEmpty(path)) return d;
            try
            {
                if (!File.Exists(path)) return d;
                string text = File.ReadAllText(path, Encoding.UTF8);
                JsonFlat j = JsonFlat.Parse(text);
                int v;
                if (j.TryGetInt("x", out v)) { d.HasX = true; d.X = v; }
                if (j.TryGetInt("y", out v)) { d.HasY = true; d.Y = v; }
                if (j.TryGetInt("size", out v)) { d.HasSize = true; d.Size = Config.ClampSize(v); }
            }
            catch (Exception ex)
            {
                Logger.Error("read pos file " + path, ex);
            }
            return d;
        }

        public static void Write(string path, int x, int y, int size)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string full = Path.GetFullPath(path);
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = full + ".tmp";
                string text = "{\"x\":" + x.ToString(CultureInfo.InvariantCulture)
                            + ",\"y\":" + y.ToString(CultureInfo.InvariantCulture)
                            + ",\"size\":" + size.ToString(CultureInfo.InvariantCulture) + "}";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                File.Copy(tmp, full, true);
                File.Delete(tmp);
                Logger.Write("pos saved x=" + x + " y=" + y + " size=" + size + " -> " + full);
            }
            catch (Exception ex)
            {
                if (!_writeErrorLogged)
                {
                    _writeErrorLogged = true;
                    Logger.Error("write pos file " + path + " (this is expected when the pet runs inside the DSH file sandbox; further pos.json write errors are silenced for this process)", ex);
                }
            }
        }
    }

    internal static class HttpUtil
    {
        static HttpUtil()
        {
            try { ServicePointManager.Expect100Continue = false; }
            catch { }
        }

        public static void Get(string url, int timeoutMs)
        {
            HttpWebRequest req = Create(url, "GET", timeoutMs);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                Validate(resp, "GET " + url);
            }
        }

        // GET that returns the response body as UTF-8 text (used by the poke API).
        public static string GetString(string url, int timeoutMs)
        {
            HttpWebRequest req = Create(url, "GET", timeoutMs);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                Validate(resp, "GET " + url);
                using (Stream s = resp.GetResponseStream())
                {
                    if (s == null) return "";
                    using (StreamReader reader = new StreamReader(s, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
        }

        public static void Post(string url, string body, int timeoutMs)
        {
            HttpWebRequest req = Create(url, "POST", timeoutMs);
            byte[] data = Encoding.UTF8.GetBytes(body == null ? "" : body);
            req.ContentType = "application/json; charset=utf-8";
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream())
            {
                if (data.Length > 0) s.Write(data, 0, data.Length);
            }
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                Validate(resp, "POST " + url);
            }
        }

        private static HttpWebRequest Create(string url, string method, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.KeepAlive = false;
            req.AllowAutoRedirect = false;
            req.Proxy = null;
            req.UserAgent = "WhalePet/" + Program.AppVersion;
            return req;
        }

        private static void Validate(HttpWebResponse resp, string what)
        {
            int code = (int)resp.StatusCode;
            if (code < 200 || code >= 300) throw new WebException(what + " -> HTTP " + code);
        }
    }

    // ----------------------------------------------------------------------
    // Poke API response: GET {apiBase}/api/whale-pet/poke
    // Every field is optional; parsing is defensive so a broken/partial payload
    // still yields a usable bubble (or the "余额 —" fallback on the caller side).
    // ----------------------------------------------------------------------
    internal sealed class PokeResult
    {
        public string[] Lines = new string[0];
        public string PressSound = "";
        public string ReleaseSound = "";
        public double Volume = 1.0;
        public int BubbleMs = 5000;

        public static PokeResult Parse(string json)
        {
            PokeResult r = new PokeResult();
            if (string.IsNullOrEmpty(json)) return r;
            JsonFlat j;
            try { j = JsonFlat.Parse(json); }
            catch (Exception ex)
            {
                Logger.Write("WARN poke response is not valid JSON: " + Program.Describe(ex));
                return r;
            }
            try
            {
                string raw;
                if (j.TryGetRaw("lines", out raw))
                {
                    string[] parsed = JsonUtil.ParseStringArray(raw);
                    if (parsed != null && parsed.Length > 0) r.Lines = parsed;
                }
            }
            catch (Exception ex) { Logger.Write("WARN poke lines unparsable: " + Program.Describe(ex)); }
            try { r.PressSound = j.GetString("pressSound", ""); } catch { }
            try { r.ReleaseSound = j.GetString("releaseSound", ""); } catch { }
            double dv;
            try
            {
                if (j.TryGetDouble("volume", out dv))
                {
                    if (dv < 0) dv = 0;
                    if (dv > 1) dv = 1;
                    r.Volume = dv;
                }
            }
            catch { }
            int iv;
            try
            {
                if (j.TryGetInt("bubbleMs", out iv))
                {
                    if (iv < 500) iv = 500;
                    if (iv > 30000) iv = 30000;
                    r.BubbleMs = iv;
                }
            }
            catch { }
            // Host may send absolute Windows paths with escaped backslashes: JsonFlat
            // already unescapes "\\" into a single backslash. Nothing else to do.
            return r;
        }
    }

    // Parses a JSON array of strings (the "lines" field). Returns an empty array on
    // any structural problem instead of throwing, so a bad payload cannot kill the pet.
    internal static class JsonUtil
    {
        public static string[] ParseStringArray(string raw)
        {
            List<string> items = new List<string>();
            if (string.IsNullOrEmpty(raw)) return items.ToArray();
            int i = 0;
            SkipWs(raw, ref i);
            if (i >= raw.Length || raw[i] != '[') return items.ToArray();
            i++;
            while (i < raw.Length)
            {
                SkipWs(raw, ref i);
                if (i >= raw.Length) break;
                if (raw[i] == ']') break;
                if (raw[i] == ',') { i++; continue; }
                if (raw[i] == '"')
                {
                    string s = ReadString(raw, ref i);
                    if (s != null && s.Length > 0) items.Add(s);
                    continue;
                }
                i++; // skip stray tokens defensively
            }
            return items.ToArray();
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static string ReadString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                    sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }

    // ----------------------------------------------------------------------
    // Sound playback through winmm MCI (mp3 via mpegvideo, wav via waveaudio).
    // Everything runs on a throwaway background thread; failure only logs.
    // ----------------------------------------------------------------------
    // ----------------------------------------------------------------------
    // Sound playback through winmm MCI.
    //
    // IMPORTANT (v1.1.1): the MCI "mpegvideo" driver used for .mp3 is backed by
    // DirectShow and only loads on a thread inside a Single-Threaded Apartment.
    // A plain worker thread (MTA) makes mciSendString("open ...") fail with
    // rc=266 (MCIERR_CANNOT_LOAD_DRIVER). Every playback therefore runs on its
    // own thread that calls CoInitializeEx(COINIT_APARTMENTTHREADED) first.
    // ----------------------------------------------------------------------
    internal static class SoundUtil
    {
        private static int _counter;

        // Plays now (fire and forget).
        public static void PlayAsync(string path, double volume)
        {
            PlayAfterAsync(path, volume, 0);
        }

        // Plays after `delayMs` on a dedicated STA thread. Used for the release
        // sound so it starts ~120 ms after the press sound and never queues behind
        // the press clip's blocking "play ... wait".
        public static void PlayAfterAsync(string path, double volume, int delayMs)
        {
            if (string.IsNullOrEmpty(path)) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) { Logger.Write("sound skipped (bad path): " + Program.Describe(ex)); return; }
            if (!File.Exists(full))
            {
                Logger.Write("sound skipped (missing file): " + full);
                return;
            }
            string alias = "whalepet" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture);
            try
            {
                Thread t = new Thread(delegate() { PlayOnOwnThread(full, volume, alias, delayMs); });
                t.IsBackground = true;
                t.Name = "WhalePetSound";
                // Managed threads start in the MTA; the mpegvideo MCI driver must run
                // in an STA. Setting the apartment before Start() makes the CLR
                // initialize COM as STA for this thread.
                try { t.SetApartmentState(ApartmentState.STA); }
                catch (Exception ex) { Logger.Write("sound thread: SetApartmentState(STA) failed: " + Program.Describe(ex)); }
                t.Start();
            }
            catch (Exception ex)
            {
                Logger.Error("SoundUtil.PlayAfterAsync", ex);
            }
        }

        private static void PlayOnOwnThread(string full, double volume, string alias, int delayMs)
        {
            bool comInitialized = false;
            bool opened = false;
            try
            {
                if (delayMs > 0) Thread.Sleep(delayMs);

                // STA is required by the DirectShow-backed mpegvideo MCI driver.
                int hr = -1;
                try { hr = Native.CoInitializeEx(IntPtr.Zero, Native.COINIT_APARTMENTTHREADED); }
                catch (Exception ex) { Logger.Error("CoInitializeEx", ex); }
                if (hr == 0 || hr == 1)
                {
                    comInitialized = true; // S_OK or S_FALSE (thread was already STA)
                    Logger.Write("mci thread: STA COM ready (hr=" + hr.ToString(CultureInfo.InvariantCulture) + ")");
                }
                else if (hr == Native.RPC_E_CHANGED_MODE)
                {
                    // Already in another apartment: MCI may still work, so try anyway.
                    Logger.Write("mci thread: CoInitializeEx RPC_E_CHANGED_MODE, continuing");
                }
                else
                {
                    Logger.Write("mci thread: CoInitializeEx failed hr=0x" + hr.ToString("X8", CultureInfo.InvariantCulture) + ", continuing");
                }

                string name = Path.GetFileName(full);
                string primary = full.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? "waveaudio" : "mpegvideo";
                string alternate = primary == "waveaudio" ? "mpegvideo" : "waveaudio";

                int rc = Mci("open \"" + full + "\" type " + primary + " alias " + alias);
                if (rc == 0)
                {
                    opened = true;
                    Logger.Write("mci open ok " + name + " type=" + primary + " rc=0");
                }
                else
                {
                    Logger.Write("mci open failed " + name + " type=" + primary + " rc=" + rc.ToString(CultureInfo.InvariantCulture));
                    rc = Mci("open \"" + full + "\" type " + alternate + " alias " + alias);
                    if (rc == 0)
                    {
                        opened = true;
                        Logger.Write("mci open ok " + name + " type=" + alternate + " rc=0 (after fallback)");
                    }
                    else
                    {
                        Logger.Write("mci open failed " + name + " type=" + alternate + " rc=" + rc.ToString(CultureInfo.InvariantCulture));
                    }
                }

                if (!opened)
                {
                    // Last resort for .wav only: SoundPlayer does not use MCI at all.
                    if (primary == "waveaudio")
                    {
                        try
                        {
                            using (System.Media.SoundPlayer player = new System.Media.SoundPlayer(full))
                            {
                                player.PlaySync();
                                Logger.Write("soundplayer fallback played " + name);
                            }
                        }
                        catch (Exception ex) { Logger.Error("SoundPlayer fallback", ex); }
                    }
                    return;
                }

                int vol = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, volume)) * 1000);
                int vrc = Mci("setaudio " + alias + " volume to " + vol.ToString(CultureInfo.InvariantCulture));
                if (vrc != 0)
                {
                    // Some devices do not support MCI volume: keep playing unmodified.
                    Logger.Write("mci setaudio failed rc=" + vrc.ToString(CultureInfo.InvariantCulture) + " (ignored, volume unchanged)");
                }
                Logger.Write("sound play " + name + " volume=" + vol.ToString(CultureInfo.InvariantCulture));
                int prc = Mci("play " + alias + " wait");
                if (prc != 0) Logger.Write("mci play failed " + name + " rc=" + prc.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Logger.Error("SoundUtil.PlayOnOwnThread", ex);
            }
            finally
            {
                if (opened) Mci("close " + alias);
                if (comInitialized)
                {
                    try { Native.CoUninitialize(); }
                    catch (Exception ex) { Logger.Error("CoUninitialize", ex); }
                }
            }
        }

        // Fires one MCI string command; returns its raw return code (-1 on exception).
        private static int Mci(string command)
        {
            try { return Native.mciSendString(command, null, 0, IntPtr.Zero); }
            catch (Exception ex)
            {
                Logger.Error("mci command", ex);
                return -1;
            }
        }
    }


    // Detects the system DPI *after* SetProcessDPIAware(): 192 -> scale 2.
    internal static class DpiUtil
    {
        public static int GetSystemDpi()
        {
            try
            {
                uint dpi = Native.GetDpiForSystem();
                if (dpi >= 96 && dpi <= 960) return (int)dpi;
            }
            catch
            {
                // GetDpiForSystem is Win10 1607+; fall through to GDI.
            }
            try
            {
                IntPtr dc = Native.GetDC(IntPtr.Zero);
                if (dc != IntPtr.Zero)
                {
                    try
                    {
                        int logPixels = Native.GetDeviceCaps(dc, Native.LOGPIXELSX);
                        if (logPixels >= 96 && logPixels <= 960) return logPixels;
                    }
                    finally
                    {
                        Native.ReleaseDC(IntPtr.Zero, dc);
                    }
                }
            }
            catch { }
            return 96;
        }

        public static int GetDefaultScale()
        {
            int dpi = GetSystemDpi();
            int scale = (int)Math.Round(dpi / 96.0);
            if (scale < 1) scale = 1;
            if (scale > 4) scale = 4;
            return scale;
        }
    }

    internal sealed class PetApp
    {
        private const uint TimedIdBob = 1;
        private const uint TimedIdMenuWatchdog = 2;
        private const uint TimedIdAnim = 3;
        private const uint TimedIdBubble = 4;
        private const int BobIntervalMs = 50;
        private const int BobPeriodMs = 3000;
        private const int BobAmplitude = 3;
        private const int AlphaThreshold = 16;
        private const int CornerMargin = 24;
        private const int MenuWatchdogMs = 80;

        // "Poke" (left click): squeeze/bounce animation + click sounds + balance bubble.
        private const int AnimIntervalMs = 60;
        private const int AnimStepCount = 5;       // last step is the settled (1.0, 1.0) frame
        private const int ReleaseSoundDelayMs = 120;
        private const int DefaultBubbleMs = 5000;
        private const int MaxBubbleLines = 4;
        private const int MaxBubbleLineChars = 64;
        private static readonly double[] AnimScaleX = new double[] { 1.06, 0.96, 1.03, 0.99, 1.0 };
        private static readonly double[] AnimScaleY = new double[] { 0.91, 1.05, 0.97, 1.01, 1.0 };

        private const int IdRecall = 1001;
        private const int IdClose = 1002;
        private const int IdSizeSmall = 1101;
        private const int IdSizeMedium = 1102;
        private const int IdSizeLarge = 1103;
        private const int IdSizeXLarge = 1104;
        private const int IdReset = 1201;

        private readonly Config _cfg;
        private readonly Native.WndProcDelegate _wndProc; // keep delegate alive for the native class

        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _hDib = IntPtr.Zero;
        private byte[] _alpha;
        private int _size;

        private int _baseX;
        private int _baseY;
        private int _bobOffset;
        private int _bobStartTick;
        private bool _dragging;
        private int _dragOffsetX;
        private int _dragOffsetY;
        private int _dragStartBaseX;
        private int _dragStartBaseY;
        private int _dragStartTick;
        private bool _dragMoved;
        private IntPtr _arrowCursor = IntPtr.Zero;
        private bool _cursorHandlerLogged;
        private bool _menuOpen;
        private bool _removedNoActivate;
        private bool _menuMouseWasDown;
        private readonly Native.EnumThreadProc _enumThreadProc; // keep delegate alive
        private volatile bool _shuttingDown;
        private int _pingInFlight;
        private int _pingFailCount;
        private Timer _heartbeatTimer;

        // Character image decoded once (animation frames reuse it).
        private Bitmap _source;
        private int _sourceW;
        private int _sourceH;

        // Balance bubble: its own layered window above the pet window. It never
        // overlaps the pet, is fully click-through and never takes focus, so drag /
        // hit-test / geometry semantics of the pet window stay exactly as before.
        private IntPtr _bubbleHwnd = IntPtr.Zero;
        private IntPtr _bubbleDib = IntPtr.Zero;
        private int _bubbleW;
        private int _bubbleH;
        private int _bubbleGap;
        private bool _bubbleVisible;

        // Poke/click state. The HTTP fetch runs on a worker thread and reports back
        // through WM_POKE_DONE; only the UI thread touches windows and rendering.
        private readonly object _pokeLock = new object();
        private PokeResult _lastPoke;
        private string _cachedPressSound = "";
        private string _cachedReleaseSound = "";
        private double _cachedVolume = 1.0;
        private int _pokeSeq;
        private bool _pressPlayedForSeq;
        private int _animStep = -1; // -1 = not animating
        private readonly int _uiScale;

        // A press/release shorter than ClickMaxMs and moving less than ClickMaxMovePx
        // is treated as a click (opens the same popup menu) instead of a drag.
        private const int ClickMaxMovePx = 4;
        private const int ClickMaxMs = 400;

        public PetApp(Config cfg)
        {
            _cfg = cfg;
            _size = Config.ClampSize(cfg.Size);
            _uiScale = DpiUtil.GetDefaultScale();
            if (_uiScale < 1) _uiScale = 1;
            if (_uiScale > 4) _uiScale = 4;
            _wndProc = new Native.WndProcDelegate(WndProcProc);
            _enumThreadProc = new Native.EnumThreadProc(FindThreadMenuWindowCallback);
            _arrowCursor = Native.LoadCursor(IntPtr.Zero, (IntPtr)Native.IDC_ARROW);
            if (_arrowCursor == IntPtr.Zero)
                Logger.Write("WARN LoadCursor(IDC_ARROW) failed err=" + Marshal.GetLastWin32Error());
            LoadSourceImage();
            ResolveStartupGeometry();
            RenderPetFrame(1.0, 1.0, true);
        }

        private void LoadSourceImage()
        {
            try
            {
                _source = new Bitmap(_cfg.ImagePath);
                _sourceW = _source.Width;
                _sourceH = _source.Height;
                Logger.Write("image loaded " + _sourceW + "x" + _sourceH + " uiScale=" + _uiScale);
            }
            catch (Exception ex)
            {
                Logger.Error("LoadSourceImage", ex);
                _source = null;
                _sourceW = 0;
                _sourceH = 0;
            }
        }

        private void ResolveStartupGeometry()
        {
            // Priority (DSH Host round-trip is authoritative):
            //   1) config.physical  (physical px returned by the Host from a previous
            //      POST /api/whale-pet/pos) - used verbatim, never scaled again;
            //   2) pos.json on disk (legacy, also physical px, never scaled);
            //   3) config x/y/size (DIP, already scaled by dpr in Config.Load);
            //   4) primary work area bottom-right with a 24 px margin.
            if (_cfg.HasPhysical && _cfg.HasPhysicalX && _cfg.HasPhysicalY)
            {
                _baseX = _cfg.PhysicalX;
                _baseY = _cfg.PhysicalY;
                if (_cfg.HasPhysicalSize) _size = Config.ClampSize(_cfg.PhysicalSize);
                Logger.Write("startup pos from config.physical: " + _baseX + "," + _baseY + " size=" + _size);
                return;
            }
            PosData pos = PosStore.Read(_cfg.PosPath);
            if (pos.HasX && pos.HasY)
            {
                _baseX = pos.X;
                _baseY = pos.Y;
                if (pos.HasSize) _size = Config.ClampSize(pos.Size);
                Logger.Write("startup pos from pos.json: " + _baseX + "," + _baseY + " size=" + _size);
                return;
            }
            if (pos.HasSize) _size = Config.ClampSize(pos.Size);
            if (_cfg.HasX && _cfg.HasY)
            {
                _baseX = _cfg.X;
                _baseY = _cfg.Y;
                Logger.Write("startup pos from config (dpr scaled): " + _baseX + "," + _baseY + " size=" + _size);
                return;
            }
            Native.RECT wa;
            int right = 1920;
            int bottom = 1080;
            try
            {
                if (Native.SystemParametersInfo(Native.SPI_GETWORKAREA, 0, out wa, 0))
                {
                    right = wa.Right;
                    bottom = wa.Bottom;
                }
            }
            catch (Exception ex) { Logger.Error("SystemParametersInfo", ex); }
            _baseX = right - _size - CornerMargin;
            _baseY = bottom - _size - CornerMargin;
            Logger.Write("startup pos default bottom-right: " + _baseX + "," + _baseY + " size=" + _size);
        }

        public int Run()
        {
            IntPtr hInstance = Native.GetModuleHandle(null);
            Native.WNDCLASSEX wc = new Native.WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(Native.WNDCLASSEX));
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc);
            wc.hInstance = hInstance;
            wc.hCursor = _arrowCursor; // do not inherit the previous window's cursor (e.g. APPSTARTING)
            wc.lpszClassName = Program.WindowClassName;
            ushort atom = Native.RegisterClassEx(ref wc);
            if (atom == 0) Logger.Write("WARN RegisterClassEx failed err=" + Marshal.GetLastWin32Error());

            // Same WndProc, separate class: the bubble window (fully click-through).
            Native.WNDCLASSEX bwc = wc;
            bwc.lpszClassName = Program.BubbleClassName;
            ushort bubbleAtom = Native.RegisterClassEx(ref bwc);
            if (bubbleAtom == 0) Logger.Write("WARN RegisterClassEx(bubble) failed err=" + Marshal.GetLastWin32Error());

            int exStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            _hwnd = Native.CreateWindowEx(exStyle, Program.WindowClassName, "WhalePet", Native.WS_POPUP,
                _baseX, _baseY, _size, _size, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Logger.Write("FATAL CreateWindowEx failed err=" + Marshal.GetLastWin32Error());
                return 1;
            }

            // Created up front (never shown until a poke); WS_EX_TRANSPARENT makes it
            // click-through even before WM_NCHITTEST answers.
            int bubbleStyle = exStyle | Native.WS_EX_TRANSPARENT;
            _bubbleHwnd = Native.CreateWindowEx(bubbleStyle, Program.BubbleClassName, "WhalePetBubble", Native.WS_POPUP,
                0, 0, 8, 8, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_bubbleHwnd == IntPtr.Zero)
                Logger.Write("WARN bubble window creation failed err=" + Marshal.GetLastWin32Error());

            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            if (_arrowCursor != IntPtr.Zero) Native.SetCursor(_arrowCursor);
            bool layeredOk = UpdateLayeredWindowNow();
            Native.SetTimer(_hwnd, (IntPtr)TimedIdBob, BobIntervalMs, IntPtr.Zero);
            _bobStartTick = Environment.TickCount;
            StartHeartbeat();

            Logger.Write("window created hwnd=0x" + _hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture)
                + " pos=" + _baseX + "," + _baseY + " size=" + _size + " layeredOk=" + layeredOk);

            Native.MSG msg;
            while (true)
            {
                int r = Native.GetMessage(out msg, IntPtr.Zero, 0, 0);
                if (r <= 0) break;
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            Cleanup();
            return 0;
        }

        private void Cleanup()
        {
            try { if (_heartbeatTimer != null) _heartbeatTimer.Dispose(); }
            catch (Exception ex) { Logger.Error("dispose heartbeat", ex); }
            try
            {
                if (_bubbleHwnd != IntPtr.Zero)
                {
                    Native.DestroyWindow(_bubbleHwnd);
                    _bubbleHwnd = IntPtr.Zero;
                }
            }
            catch (Exception ex) { Logger.Error("cleanup bubble window", ex); }
            try
            {
                if (_bubbleDib != IntPtr.Zero)
                {
                    Native.DeleteObject(_bubbleDib);
                    _bubbleDib = IntPtr.Zero;
                }
            }
            catch (Exception ex) { Logger.Error("cleanup bubble gdi", ex); }
            try
            {
                if (_hDib != IntPtr.Zero)
                {
                    Native.DeleteObject(_hDib);
                    _hDib = IntPtr.Zero;
                }
            }
            catch (Exception ex) { Logger.Error("cleanup gdi", ex); }
            try
            {
                if (_source != null)
                {
                    _source.Dispose();
                    _source = null;
                }
            }
            catch (Exception ex) { Logger.Error("cleanup source", ex); }
        }

        // ------------------------------------------------------------------
        // Rendering
        // ------------------------------------------------------------------

        // Converts a GDI+ canvas into the premultiplied BGRA buffer UpdateLayeredWindow
        // expects; optionally records the source alpha table used by WM_NCHITTEST.
        private static void ConvertToPremultiplied(Bitmap canvas, int w, int h, byte[] premul, byte[] alpha)
        {
            BitmapData bd = canvas.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                IntPtr scan0 = bd.Scan0;
                byte[] row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(scan0, y * stride), row, 0, w * 4);
                    int rowOut = y * w * 4;
                    int rowAlpha = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int p = x * 4;
                        int q = rowOut + p;
                        byte a = row[p + 3];
                        if (alpha != null) alpha[rowAlpha + x] = a;
                        if (a == 0)
                        {
                            premul[q] = 0; premul[q + 1] = 0; premul[q + 2] = 0; premul[q + 3] = 0;
                        }
                        else
                        {
                            premul[q] = (byte)((row[p] * a + 127) / 255);
                            premul[q + 1] = (byte)((row[p + 1] * a + 127) / 255);
                            premul[q + 2] = (byte)((row[p + 2] * a + 127) / 255);
                            premul[q + 3] = a;
                        }
                    }
                }
            }
            finally
            {
                canvas.UnlockBits(bd);
            }
        }

        // Creates a top-down 32bpp BGRA DIB section holding the premultiplied pixels.
        // NOTE: the DIB is never left selected into a DC here, otherwise
        // UpdateLayeredWindow fails with error 31 (ERROR_GEN_FAILURE).
        private static IntPtr CreateDib(byte[] premul, int w, int h)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC(0) failed");
            try
            {
                Native.BITMAPINFO bmi = new Native.BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = w;
                bmi.bmiHeader.biHeight = -h; // top-down
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0; // BI_RGB
                bmi.bmiHeader.biSizeImage = (uint)(w * h * 4);
                IntPtr bits;
                IntPtr hDib = Native.CreateDIBSection(screenDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (hDib == IntPtr.Zero || bits == IntPtr.Zero)
                    throw new InvalidOperationException("CreateDIBSection failed err=" + Marshal.GetLastWin32Error());
                Marshal.Copy(premul, 0, bits, premul.Length);
                return hDib;
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private static bool UpdateLayeredWindowAt(IntPtr hwnd, IntPtr hDib, int w, int h, int x, int y)
        {
            if (hwnd == IntPtr.Zero || hDib == IntPtr.Zero) return false;
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;
            try
            {
                IntPtr memDc = Native.CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero)
                {
                    Logger.Write("ERROR CreateCompatibleDC failed");
                    return false;
                }
                IntPtr old = Native.SelectObject(memDc, hDib);
                Native.POINT dst;
                dst.X = x;
                dst.Y = y;
                Native.SIZE sz;
                sz.cx = w;
                sz.cy = h;
                Native.POINT src;
                src.X = 0;
                src.Y = 0;
                Native.BLENDFUNCTION blend;
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;
                bool ok = Native.UpdateLayeredWindow(hwnd, screenDc, ref dst, ref sz, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
                Native.SelectObject(memDc, old);
                Native.DeleteDC(memDc);
                if (!ok)
                {
                    Logger.Write("ERROR UpdateLayeredWindow failed err=" + Marshal.GetLastWin32Error());
                    return false;
                }
                return true;
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private bool UpdateLayeredWindowNow()
        {
            return UpdateLayeredWindowAt(_hwnd, _hDib, _size, _size, _baseX, _baseY + _bobOffset);
        }

        // Draws the character with an extra (sx, sy) scale around the bottom-center
        // anchor (1.0, 1.0 = resting pose) and uploads it to the pet window.
        // When refreshHitAlpha is set the per-pixel alpha table for WM_NCHITTEST is
        // rebuilt from this frame.
        private void RenderPetFrame(double sx, double sy, bool refreshHitAlpha)
        {
            int size = _size;
            byte[] premul = new byte[size * size * 4];
            byte[] alpha = refreshHitAlpha ? new byte[size * size] : null;
            try
            {
                if (_source != null && _sourceW > 0 && _sourceH > 0)
                {
                    using (Bitmap canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(canvas))
                        {
                            g.Clear(Color.Transparent);
                            g.CompositingMode = CompositingMode.SourceOver;
                            g.CompositingQuality = CompositingQuality.HighQuality;
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            double baseScale = Math.Min((double)size / _sourceW, (double)size / _sourceH);
                            if (baseScale <= 0 || double.IsNaN(baseScale) || double.IsInfinity(baseScale)) baseScale = 1.0;
                            int dw = (int)Math.Round(_sourceW * baseScale * sx);
                            int dh = (int)Math.Round(_sourceH * baseScale * sy);
                            if (dw < 1) dw = 1;
                            if (dh < 1) dh = 1;
                            int ox = (size - dw) / 2;
                            int oy = size - dh; // anchored to the bottom edge
                            using (ImageAttributes ia = new ImageAttributes())
                            {
                                ia.SetWrapMode(WrapMode.TileFlipXY);
                                g.DrawImage(_source, new Rectangle(ox, oy, dw, dh), 0, 0, _sourceW, _sourceH, GraphicsUnit.Pixel, ia);
                            }
                        }
                        ConvertToPremultiplied(canvas, size, size, premul, alpha);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("RenderPetFrame", ex);
            }

            try
            {
                if (_hDib != IntPtr.Zero)
                {
                    Native.DeleteObject(_hDib);
                    _hDib = IntPtr.Zero;
                }
                _hDib = CreateDib(premul, size, size);
            }
            catch (Exception ex)
            {
                Logger.Error("RenderPetFrame/dib", ex);
                return;
            }
            if (alpha != null) _alpha = alpha;
            UpdateLayeredWindowNow();
        }

        // ------------------------------------------------------------------
        // Poke: click -> bounce animation + sounds + balance bubble
        // ------------------------------------------------------------------

        // Left click without movement: hide an open bubble, play the press sound,
        // bounce once, and ask the Host for the bubble lines.
        private void OnPokeClick()
        {
            try
            {
                HideBubble("re-poke");
                _pokeSeq++;
                _pressPlayedForSeq = false;

                // Immediate feedback with the sounds cached from the previous poke.
                if (!string.IsNullOrEmpty(_cachedPressSound))
                {
                    SoundUtil.PlayAsync(_cachedPressSound, _cachedVolume);
                    _pressPlayedForSeq = true;
                    ScheduleReleaseSound(_cachedReleaseSound, _cachedVolume);
                }

                Logger.Write("poke click seq=" + _pokeSeq + " (animation + fetch)");
                StartBounceAnimation();
                StartPokeFetch(_pokeSeq);
            }
            catch (Exception ex)
            {
                Logger.Error("OnPokeClick", ex);
            }
        }

        private void ScheduleReleaseSound(string path, double volume)
        {
            if (string.IsNullOrEmpty(path)) return;
            // Dedicated STA thread with the delay inside it: the release clip runs in
            // its own apartment and never queues behind the press clip's "play wait".
            SoundUtil.PlayAfterAsync(path, volume, ReleaseSoundDelayMs);
        }

        private void StartBounceAnimation()
        {
            if (_hwnd == IntPtr.Zero) return;
            _animStep = 0;
            if (_bobOffset != 0)
            {
                _bobOffset = 0;
                ApplyWindowPosition();
            }
            ApplyAnimationFrame();
            Native.SetTimer(_hwnd, (IntPtr)TimedIdAnim, AnimIntervalMs, IntPtr.Zero);
        }

        private void OnAnimTick()
        {
            if (_animStep < 0) return;
            _animStep++;
            if (_animStep >= AnimStepCount)
            {
                _animStep = -1;
                Native.KillTimer(_hwnd, (IntPtr)TimedIdAnim);
                _bobStartTick = Environment.TickCount;
                return;
            }
            ApplyAnimationFrame();
        }

        private void ApplyAnimationFrame()
        {
            int step = _animStep;
            if (step < 0) step = AnimStepCount - 1;
            if (step >= AnimStepCount) step = AnimStepCount - 1;
            RenderPetFrame(AnimScaleX[step], AnimScaleY[step], false);
        }

        // Fire-and-forget GET /api/whale-pet/poke on a worker thread. The result is
        // handed back to the UI thread through WM_POKE_DONE (never touch the UI here).
        private void StartPokeFetch(int seq)
        {
            if (string.IsNullOrEmpty(_cfg.ApiBase)) return;
            string url = _cfg.ApiBase + "/api/whale-pet/poke";
            try
            {
                Thread t = new Thread(delegate()
                {
                    PokeResult result = null;
                    try
                    {
                        string body = HttpUtil.GetString(url, 1500);
                        result = PokeResult.Parse(body);
                        Logger.Write("poke ok lines=" + result.Lines.Length + " bubbleMs=" + result.BubbleMs
                            + " press=" + (string.IsNullOrEmpty(result.PressSound) ? "-" : Path.GetFileName(result.PressSound))
                            + " release=" + (string.IsNullOrEmpty(result.ReleaseSound) ? "-" : Path.GetFileName(result.ReleaseSound))
                            + " volume=" + result.Volume.ToString("0.##", CultureInfo.InvariantCulture));
                    }
                    catch (Exception ex)
                    {
                        Logger.Write("poke fetch failed (bubble falls back to 余额 -): " + Program.Describe(ex));
                    }
                    lock (_pokeLock) { _lastPoke = result; }
                    Native.PostMessage(_hwnd, Native.WM_POKE_DONE, (IntPtr)(result != null ? 1 : 0), (IntPtr)seq);
                });
                t.IsBackground = true;
                t.Name = "WhalePetPoke";
                t.Start();
            }
            catch (Exception ex)
            {
                Logger.Error("StartPokeFetch", ex);
            }
        }

        private void OnPokeDone(IntPtr wParam, IntPtr lParam)
        {
            try
            {
                int seq = (int)lParam.ToInt64();
                PokeResult result;
                lock (_pokeLock) { result = _lastPoke; }
                if (result == null) result = new PokeResult();

                _cachedPressSound = result.PressSound ?? "";
                _cachedReleaseSound = result.ReleaseSound ?? "";
                _cachedVolume = result.Volume;
                if (!_pressPlayedForSeq)
                {
                    _pressPlayedForSeq = true;
                    if (!string.IsNullOrEmpty(_cachedPressSound)) SoundUtil.PlayAsync(_cachedPressSound, _cachedVolume);
                    ScheduleReleaseSound(_cachedReleaseSound, _cachedVolume);
                }
                // Only the newest click may show its bubble (rapid re-clicks).
                if (seq != _pokeSeq) return;
                ShowBubble(result);
            }
            catch (Exception ex)
            {
                Logger.Error("OnPokeDone", ex);
            }
        }

        private void ShowBubble(PokeResult result)
        {
            try
            {
                if (_bubbleHwnd == IntPtr.Zero) return;
                string[] lines = NormalizeBubbleLines(result);
                int bubbleMs = result.BubbleMs > 0 ? result.BubbleMs : DefaultBubbleMs;
                RenderBubbleBitmap(lines);
                _bubbleVisible = true;
                RepositionBubble();
                Native.ShowWindow(_bubbleHwnd, Native.SW_SHOWNOACTIVATE);
                Native.SetTimer(_hwnd, (IntPtr)TimedIdBubble, (uint)bubbleMs, IntPtr.Zero);
                Logger.Write("bubble shown lines=" + lines.Length + " size=" + _bubbleW + "x" + _bubbleH + " ms=" + bubbleMs + " text=[" + string.Join(" | ", lines) + "]");
            }
            catch (Exception ex)
            {
                Logger.Error("ShowBubble", ex);
            }
        }

        private string[] NormalizeBubbleLines(PokeResult result)
        {
            List<string> lines = new List<string>();
            if (result != null && result.Lines != null)
            {
                for (int i = 0; i < result.Lines.Length && lines.Count < MaxBubbleLines; i++)
                {
                    string s = result.Lines[i];
                    if (s == null) continue;
                    s = s.Trim();
                    if (s.Length == 0) continue;
                    if (s.Length > MaxBubbleLineChars) s = s.Substring(0, MaxBubbleLineChars - 1) + "...";
                    lines.Add(s);
                }
            }
            if (lines.Count == 0) lines.Add("余额 —");
            return lines.ToArray();
        }

        // Renders the rounded-rectangle bubble into its own layered window bitmap.
        private void RenderBubbleBitmap(string[] lines)
        {
            int scale = _uiScale;
            int fontPx = 14 * scale;
            int lineHeight = (int)Math.Round(fontPx * 1.45);
            int padX = 14 * scale;
            int padY = 9 * scale;
            int radius = 12 * scale;
            int border = Math.Max(1, 2 * scale);
            int gap = 8 * scale;

            using (Font font = CreateBubbleFont(fontPx))
            {
                float maxText = 1;
                using (Bitmap probe = new Bitmap(1, 1))
                {
                    using (Graphics g = Graphics.FromImage(probe))
                    {
                        for (int i = 0; i < lines.Length; i++)
                        {
                            SizeF m = g.MeasureString(lines[i], font);
                            if (m.Width > maxText) maxText = m.Width;
                        }
                    }
                }
                int maxBubbleW = Math.Max((int)Math.Round(_size * 1.6), 220 * scale);
                int minBubbleW = Math.Min(120 * scale, maxBubbleW);
                int w = (int)Math.Round(maxText) + padX * 2 + border * 2;
                if (w < minBubbleW) w = minBubbleW;
                if (w > maxBubbleW) w = maxBubbleW;
                int h = padY * 2 + lineHeight * lines.Length + border * 2;

                byte[] premul = new byte[w * h * 4];
                using (Bitmap canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(canvas))
                    {
                        g.Clear(Color.Transparent);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                        Rectangle shadowRect = new Rectangle(border, border + 2 * scale, w - border * 2, h - border * 2);
                        using (GraphicsPath shadow = RoundedRect(shadowRect, radius))
                        using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(28, 10, 20, 50)))
                        {
                            g.FillPath(shadowBrush, shadow);
                        }
                        Rectangle cardRect = new Rectangle(border, border, w - border * 2, h - border * 2);
                        using (GraphicsPath card = RoundedRect(cardRect, radius))
                        using (SolidBrush fill = new SolidBrush(Color.FromArgb(246, 249, 252, 255)))
                        using (Pen pen = new Pen(Color.FromArgb(235, 29, 43, 100), border))
                        {
                            g.FillPath(fill, card);
                            g.DrawPath(pen, card);
                        }
                        using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(255, 24, 34, 74)))
                        {
                            for (int i = 0; i < lines.Length; i++)
                            {
                                g.DrawString(lines[i], font, textBrush, padX + border, border + padY + i * lineHeight);
                            }
                        }
                    }
                    ConvertToPremultiplied(canvas, w, h, premul, null);
                }

                if (_bubbleDib != IntPtr.Zero)
                {
                    Native.DeleteObject(_bubbleDib);
                    _bubbleDib = IntPtr.Zero;
                }
                _bubbleDib = CreateDib(premul, w, h);
                _bubbleW = w;
                _bubbleH = h;
                _bubbleGap = gap;
            }
        }

        private static Font CreateBubbleFont(int fontPx)
        {
            string[] families = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" };
            for (int i = 0; i < families.Length; i++)
            {
                try
                {
                    Font f = new Font(families[i], fontPx, FontStyle.Regular, GraphicsUnit.Pixel);
                    if (f != null) return f;
                }
                catch { }
            }
            return new Font(FontFamily.GenericSansSerif, fontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Moves the bubble window above the character (following idle bob / drags).
        private void RepositionBubble()
        {
            if (!_bubbleVisible || _bubbleHwnd == IntPtr.Zero || _bubbleDib == IntPtr.Zero) return;
            int x = _baseX + (_size - _bubbleW) / 2;
            int y = _baseY + _bobOffset - _bubbleGap - _bubbleH;
            if (y < 0) y = 0;
            UpdateLayeredWindowAt(_bubbleHwnd, _bubbleDib, _bubbleW, _bubbleH, x, y);
            Native.SetWindowPos(_bubbleHwnd, Native.HWND_TOPMOST, x, y, _bubbleW, _bubbleH,
                Native.SWP_NOACTIVATE | Native.SWP_NOSENDCHANGING);
        }

        private void HideBubble(string reason)
        {
            Native.KillTimer(_hwnd, (IntPtr)TimedIdBubble);
            if (!_bubbleVisible)
            {
                return;
            }
            _bubbleVisible = false;
            try
            {
                Native.ShowWindow(_bubbleHwnd, Native.SW_HIDE);
            }
            catch (Exception ex)
            {
                Logger.Error("HideBubble", ex);
            }
            Logger.Write("bubble hidden (" + reason + ")");
        }


        private void ApplyWindowPosition()
        {
            if (_hwnd == IntPtr.Zero) return;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _baseX, _baseY + _bobOffset, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOSENDCHANGING);
            // The bubble lives in its own window: keep it glued above the character.
            RepositionBubble();
        }

        private void PersistPosition()
        {
            // Disk is only a best-effort legacy cache: when the pet is spawned by the
            // DSH Host it may not be allowed to write files at all.  The Host is the
            // authoritative store and accepts the same values via HTTP.
            PosStore.Write(_cfg.PosPath, _baseX, _baseY, _size);
            ReportPositionToHost(_baseX, _baseY, _size);
        }

        // Fire-and-forget POST /api/whale-pet/pos on a background thread.
        // Units are the pet's own coordinate space (whatever GetWindowRect returns,
        // i.e. physical pixels on this DPI-aware window).  Failure is logged only.
        private void ReportPositionToHost(int x, int y, int size)
        {
            if (_shuttingDown) return;
            if (string.IsNullOrEmpty(_cfg.ApiBase)) return;
            string url = _cfg.ApiBase + "/api/whale-pet/pos";
            string body = "{\"x\":" + x.ToString(CultureInfo.InvariantCulture)
                        + ",\"y\":" + y.ToString(CultureInfo.InvariantCulture)
                        + ",\"size\":" + size.ToString(CultureInfo.InvariantCulture) + "}";
            try
            {
                Thread t = new Thread(delegate()
                {
                    try
                    {
                        HttpUtil.Post(url, body, 1500);
                        Logger.Write("pos reported to DSH: " + body);
                    }
                    catch (Exception ex)
                    {
                        Logger.Write("pos report failed (ignored; only affects next-start memory): " + Program.Describe(ex));
                    }
                });
                t.IsBackground = true;
                t.Name = "WhalePetPosReport";
                t.Start();
            }
            catch (Exception ex)
            {
                Logger.Error("ReportPositionToHost", ex);
            }
        }

        private void OnBobTick()
        {
            if (!_cfg.IdleBob) return;
            if (_dragging || _menuOpen) return;
            if (_animStep >= 0) return;
            if (_shuttingDown) return;
            long elapsed = (uint)(Environment.TickCount - _bobStartTick);
            double phase = 2.0 * Math.PI * (elapsed % BobPeriodMs) / BobPeriodMs;
            int off = (int)Math.Round(BobAmplitude * Math.Sin(phase));
            if (off != _bobOffset)
            {
                _bobOffset = off;
                ApplyWindowPosition();
            }
        }

        // ------------------------------------------------------------------
        // Window procedure
        // ------------------------------------------------------------------

        private IntPtr WndProcProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case Native.WM_SETCURSOR:
                        // Always show the standard arrow over the pet. Without this the
                        // window inherits whatever global cursor state the previous
                        // window left behind (e.g. an APPSTARTING/working-in-background
                        // cursor from another app), which looks like the pet is busy.
                        if (_arrowCursor != IntPtr.Zero)
                        {
                            Native.SetCursor(_arrowCursor);
                            if (!_cursorHandlerLogged)
                            {
                                _cursorHandlerLogged = true;
                                Logger.Write("WM_SETCURSOR -> SetCursor(IDC_ARROW)");
                            }
                        }
                        return (IntPtr)1;
                    case Native.WM_NCHITTEST:
                        if (hwnd == _bubbleHwnd) return (IntPtr)Native.HTTRANSPARENT;
                        return OnNcHitTest(lParam);
                    case Native.WM_MOUSEACTIVATE:
                        return (IntPtr)Native.MA_NOACTIVATE;
                    case Native.WM_LBUTTONDOWN:
                        if (hwnd != _hwnd) return IntPtr.Zero;
                        OnLeftDown(lParam);
                        return IntPtr.Zero;
                    case Native.WM_MOUSEMOVE:
                        if (hwnd == _hwnd && _dragging) OnLeftMove(lParam);
                        return IntPtr.Zero;
                    case Native.WM_LBUTTONUP:
                        if (hwnd == _hwnd && _dragging) OnLeftUp();
                        return IntPtr.Zero;
                    case Native.WM_CAPTURECHANGED:
                        if (_dragging)
                        {
                            _dragging = false;
                            PersistPosition();
                        }
                        return IntPtr.Zero;
                    case Native.WM_RBUTTONUP:
                        if (hwnd != _hwnd) return IntPtr.Zero;
                        OnRightUp(lParam);
                        return IntPtr.Zero;
                    case Native.WM_COMMAND:
                        OnCommand(wParam);
                        return IntPtr.Zero;
                    case Native.WM_POKE_DONE:
                        OnPokeDone(wParam, lParam);
                        return IntPtr.Zero;
                    case Native.WM_TIMER:
                        if (wParam.ToInt64() == TimedIdBob) OnBobTick();
                        else if (wParam.ToInt64() == TimedIdMenuWatchdog) OnMenuWatchdog();
                        else if (wParam.ToInt64() == TimedIdAnim) OnAnimTick();
                        else if (wParam.ToInt64() == TimedIdBubble) HideBubble("bubble-timeout");
                        return IntPtr.Zero;
                    case Native.WM_CANCELMODE:
                        // The system (or an automation harness) can cancel our popup menu mode.
                        if (_menuOpen)
                        {
                            Logger.Write("WM_CANCELMODE received while menu open -> EndMenu");
                            Native.EndMenu();
                        }
                        return IntPtr.Zero;
                    case Native.WM_CLOSE:
                        if (hwnd == _bubbleHwnd)
                        {
                            HideBubble("bubble-wm-close");
                            Native.DestroyWindow(hwnd);
                            return IntPtr.Zero;
                        }
                        Logger.Write("WM_CLOSE received");
                        HideBubble("pet-wm-close");
                        if (_bubbleHwnd != IntPtr.Zero)
                        {
                            Native.DestroyWindow(_bubbleHwnd);
                            _bubbleHwnd = IntPtr.Zero;
                        }
                        Native.DestroyWindow(hwnd);
                        return IntPtr.Zero;
                    case Native.WM_DESTROY:
                        if (hwnd == _bubbleHwnd)
                        {
                            _bubbleHwnd = IntPtr.Zero;
                            return IntPtr.Zero;
                        }
                        Native.KillTimer(hwnd, (IntPtr)TimedIdBob);
                        Native.KillTimer(hwnd, (IntPtr)TimedIdMenuWatchdog);
                        Native.KillTimer(hwnd, (IntPtr)TimedIdAnim);
                        Native.KillTimer(hwnd, (IntPtr)TimedIdBubble);
                        Native.PostQuitMessage(0);
                        return IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("WndProc msg=0x" + msg.ToString("X", CultureInfo.InvariantCulture), ex);
            }
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private IntPtr OnNcHitTest(IntPtr lParam)
        {
            int sx = Native.LoWord(lParam);
            int sy = Native.HiWord(lParam);
            Native.POINT pt;
            pt.X = sx;
            pt.Y = sy;
            if (!Native.ScreenToClient(_hwnd, ref pt)) return (IntPtr)Native.HTTRANSPARENT;
            if (pt.X < 0 || pt.Y < 0 || pt.X >= _size || pt.Y >= _size) return (IntPtr)Native.HTTRANSPARENT;
            byte a = 0;
            int idx = pt.Y * _size + pt.X;
            if (_alpha != null && idx >= 0 && idx < _alpha.Length) a = _alpha[idx];
            return a < AlphaThreshold ? (IntPtr)Native.HTTRANSPARENT : (IntPtr)Native.HTCLIENT;
        }

        private void OnLeftDown(IntPtr lParam)
        {
            _dragOffsetX = Native.LoWord(lParam);
            _dragOffsetY = Native.HiWord(lParam);
            _dragging = true;
            _dragMoved = false;
            _dragStartBaseX = _baseX;
            _dragStartBaseY = _baseY;
            _dragStartTick = Environment.TickCount;
            if (_bobOffset != 0)
            {
                _bobOffset = 0;
                ApplyWindowPosition();
            }
            Native.SetCapture(_hwnd);
            Logger.Write("drag start offset=" + _dragOffsetX + "," + _dragOffsetY + " base=" + _baseX + "," + _baseY);
        }

        private void OnLeftMove(IntPtr lParam)
        {
            int cx = Native.LoWord(lParam);
            int cy = Native.HiWord(lParam);
            int nx = _baseX + cx - _dragOffsetX;
            int ny = _baseY + cy - _dragOffsetY;
            if (nx != _baseX || ny != _baseY)
            {
                _baseX = nx;
                _baseY = ny;
                if (Math.Abs(_baseX - _dragStartBaseX) >= ClickMaxMovePx ||
                    Math.Abs(_baseY - _dragStartBaseY) >= ClickMaxMovePx)
                {
                    _dragMoved = true;
                }
                ApplyWindowPosition();
            }
        }

        private void OnLeftUp()
        {
            _dragging = false;
            Native.ReleaseCapture();
            _bobStartTick = Environment.TickCount;

            int elapsedMs = Environment.TickCount - _dragStartTick;
            bool moved = _dragMoved ||
                         Math.Abs(_baseX - _dragStartBaseX) >= ClickMaxMovePx ||
                         Math.Abs(_baseY - _dragStartBaseY) >= ClickMaxMovePx;
            // A short press with no movement is a click: poke the pet (bounce animation
            // + press/release sounds + balance bubble). A real drag keeps its old path.
            if (!moved && elapsedMs >= 0 && elapsedMs < ClickMaxMs)
            {
                Logger.Write("left click (no move, " + elapsedMs + "ms) -> poke");
                OnPokeClick();
                return;
            }

            PersistPosition();
            Logger.Write("drag end base=" + _baseX + "," + _baseY + " size=" + _size + " moved=" + moved + " elapsed=" + elapsedMs + "ms");
        }

        private void OnRightUp(IntPtr lParam)
        {
            int sx = Native.LoWord(lParam);
            int sy = Native.HiWord(lParam);
            Logger.Write("right-click at screen " + sx + "," + sy);
            ShowMenuAt(sx, sy, "right-click");
        }

        // Shared by right-click and left-click: shows the native popup menu at the
        // given screen position, with foreground attempt + watchdog fallback.
        private void ShowMenuAt(int sx, int sy, string source)
        {
            if (_shuttingDown) return;
            _menuOpen = true;
            if (_bobOffset != 0)
            {
                _bobOffset = 0;
                ApplyWindowPosition();
            }
            IntPtr prevFg = Native.GetForegroundWindow();
            bool fgOk = EnsureForeground();
            Logger.Write("menu open (" + source + ") at " + sx + "," + sy + " foreground=" + fgOk + " prevFg=0x" + prevFg.ToInt64().ToString("X", CultureInfo.InvariantCulture));

            // Foreground wins normally let the menu dismiss itself on an outside click.
            // If another app refused to release the foreground (observed on some hosts),
            // the watchdog below still closes the menu on an outside click or ESC.
            _menuMouseWasDown = IsAnyMenuButtonDown();
            Native.SetTimer(_hwnd, (IntPtr)TimedIdMenuWatchdog, MenuWatchdogMs, IntPtr.Zero);

            IntPtr hMenu = BuildMenu();
            Native.TrackPopupMenu(hMenu, Native.TPM_RIGHTBUTTON | Native.TPM_LEFTALIGN | Native.TPM_TOPALIGN, sx, sy, 0, _hwnd, IntPtr.Zero);
            Native.KillTimer(_hwnd, (IntPtr)TimedIdMenuWatchdog);
            Native.PostMessage(_hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            Native.DestroyMenu(hMenu);
            _menuOpen = false;
            RestoreAfterMenu(prevFg);
            _bobStartTick = Environment.TickCount;
            Logger.Write("menu closed (" + source + ")");
        }

        private static bool IsAnyMenuButtonDown()
        {
            try
            {
                bool left = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0;
                bool right = (Native.GetAsyncKeyState(Native.VK_RBUTTON) & 0x8000) != 0;
                return left || right;
            }
            catch
            {
                return false;
            }
        }

        // Runs from the popup-menu modal loop (WM_TIMER is dispatched to us while
        // TrackPopupMenu is active).  This is the safety net for the case where the
        // foreground could not be taken and the menu therefore does not see the
        // outside click by itself.
        private void OnMenuWatchdog()
        {
            if (!_menuOpen) return;
            try
            {
                if ((Native.GetAsyncKeyState(Native.VK_ESCAPE) & 0x8000) != 0)
                {
                    Logger.Write("menu watchdog: ESC -> EndMenu");
                    Native.EndMenu();
                    return;
                }
                bool down = IsAnyMenuButtonDown();
                bool wasDown = _menuMouseWasDown;
                _menuMouseWasDown = down;
                if (!down || wasDown) return;
                Native.POINT pt;
                if (!Native.GetCursorPos(out pt)) return;
                if (IsPointInsideMenuOrPet(pt)) return;
                Logger.Write("menu watchdog: outside click at " + pt.X + "," + pt.Y + " -> EndMenu");
                Native.EndMenu();
            }
            catch (Exception ex)
            {
                Logger.Error("OnMenuWatchdog", ex);
            }
        }

        private bool IsPointInsideMenuOrPet(Native.POINT pt)
        {
            Native.RECT r;
            if (Native.GetWindowRect(_hwnd, out r))
            {
                if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom) return true;
            }
            IntPtr menu = FindThreadMenuWindow();
            if (menu != IntPtr.Zero && Native.GetWindowRect(menu, out r))
            {
                if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom) return true;
            }
            return false;
        }

        private IntPtr FindThreadMenuWindow()
        {
            _foundMenuStatic = IntPtr.Zero;
            try
            {
                Native.EnumThreadWindows(Native.GetCurrentThreadId(), _enumThreadProc, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Logger.Error("FindThreadMenuWindow", ex);
            }
            return _foundMenuStatic;
        }

        private static IntPtr _foundMenuStatic = IntPtr.Zero;

        private static bool FindThreadMenuWindowCallback(IntPtr hwnd, IntPtr lParam)
        {
            try
            {
                StringBuilder sb = new StringBuilder(32);
                if (Native.GetClassName(hwnd, sb, sb.Capacity) > 0 && sb.ToString() == "#32768")
                {
                    _foundMenuStatic = hwnd;
                    return false;
                }
            }
            catch { }
            return true;
        }

        private bool EnsureForeground()
        {
            try
            {
                if (Native.GetForegroundWindow() == _hwnd) return true;
                Native.SetForegroundWindow(_hwnd);
                if (Native.GetForegroundWindow() == _hwnd) return true;

                // WS_EX_NOACTIVATE may block programmatic foreground: drop it temporarily.
                int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_NOACTIVATE) != 0)
                {
                    Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex & ~Native.WS_EX_NOACTIVATE);
                    _removedNoActivate = true;
                    Native.SetForegroundWindow(_hwnd);
                    if (Native.GetForegroundWindow() == _hwnd) return true;
                }

                // Last resort: attach to the current foreground thread.
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    uint pid;
                    uint fgThread = Native.GetWindowThreadProcessId(fg, out pid);
                    uint myThread = Native.GetCurrentThreadId();
                    if (fgThread != 0 && fgThread != myThread)
                    {
                        Native.AttachThreadInput(myThread, fgThread, true);
                        Native.SetForegroundWindow(_hwnd);
                        Native.AttachThreadInput(myThread, fgThread, false);
                    }
                }

                // Undocumented last resort; harmless if the OS does not expose it.
                try
                {
                    Native.SwitchToThisWindow(_hwnd, false);
                }
                catch (Exception switchEx)
                {
                    Logger.Write("SwitchToThisWindow unavailable: " + Program.Describe(switchEx));
                }
                return Native.GetForegroundWindow() == _hwnd;
            }
            catch (Exception ex)
            {
                Logger.Error("EnsureForeground", ex);
                return false;
            }
        }

        private void RestoreAfterMenu(IntPtr prevFg)
        {
            try
            {
                if (_removedNoActivate)
                {
                    int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                    Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE);
                    _removedNoActivate = false;
                }
                if (prevFg != IntPtr.Zero && prevFg != _hwnd && Native.IsWindow(prevFg) && Native.GetForegroundWindow() == _hwnd)
                {
                    Native.SetForegroundWindow(prevFg);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("RestoreAfterMenu", ex);
            }
        }

        private IntPtr BuildMenu()
        {
            IntPtr hMenu = Native.CreatePopupMenu();
            Native.AppendMenu(hMenu, Native.MF_STRING, (IntPtr)IdRecall, "回到 DSH 窗口");
            Native.AppendMenu(hMenu, Native.MF_SEPARATOR, IntPtr.Zero, null);

            IntPtr sub = Native.CreatePopupMenu();
            // Menu presets are DIP values (like the config "size" field); the window
            // edge itself is physical pixels, so scale by the effective dpr
            // (170/230/300/380 DIP -> 340/460/600/760 physical on a 200% system).
            int[] dipValues = new int[] { 170, 230, 300, 380 };
            string[] labels = new string[] { "小（170）", "中（230）", "大（300）", "特大（380）" };
            int[] ids = new int[] { IdSizeSmall, IdSizeMedium, IdSizeLarge, IdSizeXLarge };
            for (int i = 0; i < dipValues.Length; i++)
            {
                int physical = SizeDipToPhysical(dipValues[i]);
                uint flags = Native.MF_STRING;
                if (physical == _size) flags |= Native.MF_CHECKED;
                Native.AppendMenu(sub, flags, (IntPtr)ids[i], labels[i]);
            }
            Native.AppendMenu(hMenu, Native.MF_POPUP, sub, "大小");

            Native.AppendMenu(hMenu, Native.MF_STRING, (IntPtr)IdReset, "复位到右下角");
            Native.AppendMenu(hMenu, Native.MF_SEPARATOR, IntPtr.Zero, null);
            Native.AppendMenu(hMenu, Native.MF_STRING, (IntPtr)IdClose, "关闭桌面宠物");
            return hMenu;
        }

        // Converts a DIP size (config "size", menu presets) to physical pixels.
        private int SizeDipToPhysical(int dipSize)
        {
            return Config.ClampSize((int)Math.Round(dipSize * _cfg.Dpr));
        }

        private void OnCommand(IntPtr wParam)
        {
            int id = (int)(wParam.ToInt64() & 0xFFFF);
            Logger.Write("WM_COMMAND id=" + id);
            switch (id)
            {
                case IdRecall:
                    ShutdownViaRecall("menu-recall");
                    break;
                case IdClose:
                    ShutdownViaRecall("menu-close");
                    break;
                case IdSizeSmall:
                    ChangeSize(SizeDipToPhysical(170));
                    break;
                case IdSizeMedium:
                    ChangeSize(SizeDipToPhysical(230));
                    break;
                case IdSizeLarge:
                    ChangeSize(SizeDipToPhysical(300));
                    break;
                case IdSizeXLarge:
                    ChangeSize(SizeDipToPhysical(380));
                    break;
                case IdReset:
                    ResetToBottomRight();
                    break;
            }
        }

        private void ChangeSize(int newSize)
        {
            newSize = Config.ClampSize(newSize);
            if (newSize == _size) return;
            try
            {
                int old = _size;
                int centerX = _baseX + old / 2;
                int centerY = _baseY + old / 2;
                _size = newSize;
                HideBubble("resize");
                RenderPetFrame(1.0, 1.0, true);
                _baseX = centerX - newSize / 2;
                _baseY = centerY - newSize / 2;
                _bobOffset = 0;
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _baseX, _baseY, _size, _size, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                UpdateLayeredWindowNow();
                PersistPosition();
                Logger.Write("size changed to " + newSize + " base=" + _baseX + "," + _baseY);
            }
            catch (Exception ex)
            {
                Logger.Error("ChangeSize", ex);
            }
        }

        private void ResetToBottomRight()
        {
            try
            {
                Native.RECT wa;
                int right = 1920;
                int bottom = 1080;
                if (Native.SystemParametersInfo(Native.SPI_GETWORKAREA, 0, out wa, 0))
                {
                    right = wa.Right;
                    bottom = wa.Bottom;
                }
                _baseX = right - _size - CornerMargin;
                _baseY = bottom - _size - CornerMargin;
                _bobOffset = 0;
                _bobStartTick = Environment.TickCount;
                ApplyWindowPosition();
                PersistPosition();
                Logger.Write("reset to bottom-right base=" + _baseX + "," + _baseY);
            }
            catch (Exception ex)
            {
                Logger.Error("ResetToBottomRight", ex);
            }
        }

        // ------------------------------------------------------------------
        // Heartbeat / recall
        // ------------------------------------------------------------------

        private void StartHeartbeat()
        {
            _heartbeatTimer = new Timer(HeartbeatTick, null, 3000, 3000);
        }

        private void HeartbeatTick(object state)
        {
            if (_shuttingDown) return;
            if (Interlocked.CompareExchange(ref _pingInFlight, 1, 0) != 0) return;
            try
            {
                HttpUtil.Get(_cfg.ApiBase + "/api/whale-pet/ping", 1500);
                if (_pingFailCount > 0) Logger.Write("ping recovered after " + _pingFailCount + " failures");
                _pingFailCount = 0;
            }
            catch (Exception ex)
            {
                _pingFailCount++;
                Logger.Write("ping failed " + _pingFailCount + "/4: " + Program.Describe(ex));
                if (_pingFailCount >= 4)
                {
                    Logger.Write("heartbeat lost (4 consecutive failures); DSH probably gone -> exit 0");
                    ExitSoon(0);
                }
            }
            finally
            {
                _pingInFlight = 0;
            }
        }

        private void ShutdownViaRecall(string reason)
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            Logger.Write("recall requested (" + reason + ") -> POST " + _cfg.ApiBase + "/api/whale-pet/recall");
            Thread t = new Thread(delegate()
            {
                try
                {
                    HttpUtil.Post(_cfg.ApiBase + "/api/whale-pet/recall", "", 1500);
                    Logger.Write("recall POST ok");
                }
                catch (Exception ex)
                {
                    Logger.Write("recall POST failed: " + Program.Describe(ex));
                }
                Environment.Exit(0);
            });
            t.IsBackground = true;
            t.Name = "WhalePetRecall";
            t.Start();
        }

        private void ExitSoon(int code)
        {
            _shuttingDown = true;
            Logger.Write("process exit requested code=" + code);
            try
            {
                Environment.Exit(code);
            }
            catch (Exception ex)
            {
                Logger.Error("ExitSoon", ex);
            }
        }
    }

    // ----------------------------------------------------------------------
    // Win32 interop (only what the pet needs)
    // ----------------------------------------------------------------------
    internal static class Native
    {
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int GWL_EXSTYLE = -20;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_OVER = 0;
        public const byte AC_SRC_ALPHA = 1;

        public const int WM_NULL = 0x0000;
        public const int WM_DESTROY = 0x0002;
        public const int WM_CLOSE = 0x0010;
        public const int WM_SETCURSOR = 0x0020;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_TIMER = 0x0113;
        public const int WM_COMMAND = 0x0111;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_CAPTURECHANGED = 0x0215;
        public const int WM_CANCELMODE = 0x001F;
        // Custom message: posted by the background poke-fetch thread when the host
        // answered; wParam = 1 on success. Never touch UI state from that thread.
        public const int WM_POKE_DONE = 0x8001; // WM_APP + 1

        public const int HTTRANSPARENT = -1;
        public const int HTCLIENT = 1;
        public const int MA_NOACTIVATE = 3;
        public const int IDC_ARROW = 32512;
        public const int GCLP_HCURSOR = -12;
        public const int GCLP_HICON = -14;

        public const int VK_LBUTTON = 0x01;
        public const int VK_RBUTTON = 0x02;
        public const int VK_ESCAPE = 0x1B;

        public const uint TPM_LEFTALIGN = 0x0000;
        public const uint TPM_TOPALIGN = 0x0000;
        public const uint TPM_RIGHTBUTTON = 0x0002;

        public const uint MF_STRING = 0x0000;
        public const uint MF_SEPARATOR = 0x0800;
        public const uint MF_POPUP = 0x0010;
        public const uint MF_CHECKED = 0x0008;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_HIDEWINDOW = 0x0080;
        public const uint SWP_NOSENDCHANGING = 0x0400;

        public const uint SPI_GETWORKAREA = 0x0030;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors0;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate bool EnumThreadProc(IntPtr hWnd, IntPtr lParam);

        public static int LoWord(IntPtr value)
        {
            long raw = value.ToInt64();
            return (int)(short)(raw & 0xFFFF);
        }

        public static int HiWord(IntPtr value)
        {
            long raw = value.ToInt64();
            return (int)(short)((raw >> 16) & 0xFFFF);
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetCapture();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll")]
        public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out RECT pvParam, uint fWinIni);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCursor(IntPtr hCursor);

        [DllImport("user32.dll")]
        public static extern IntPtr GetCursor();

        [DllImport("user32.dll")]
        public static extern bool EndMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool EnumThreadWindows(uint dwThreadId, EnumThreadProc lpfn, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetDpiForSystem();

        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        public const int LOGPIXELSX = 88;

        // winmm MCI: used to play the perceived click sounds (mp3/wav).
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        public static extern int mciSendString(string lpszCommand, StringBuilder lpszReturnString, int cchReturn, IntPtr hwndCallback);

        // ole32: the MCI mpegvideo (DirectShow) driver only loads on an STA thread,
        // otherwise "open" fails with rc=266 MCIERR_CANNOT_LOAD_DRIVER.
        [DllImport("ole32.dll")]
        public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        public static extern void CoUninitialize();

        public const uint COINIT_APARTMENTTHREADED = 0x2;
        public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    }
}
