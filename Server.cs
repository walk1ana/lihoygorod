// Сервер игры «Альфредо» на C#. Не требует прав администратора и установки чего-либо:
// работает на обычных TCP-сокетах (без HttpListener), собирается встроенным в Windows csc.exe.
// Раздаёт index.html и пересылает сообщения между игроками (WebSocket).
// Запуск: RomchikServer.exe [файл.html] [--port=1220] [--no-browser]   (порт также из переменной PORT)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Romchik
{
    // ---------- минимальный JSON ----------
    static class Json
    {
        public sealed class Raw
        {
            public readonly string S;
            public Raw(string s) { S = s; }
        }

        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i, 0);
            SkipWs(s, ref i);
            if (i != s.Length) throw new FormatException("trailing");
            return v;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        static object Value(string s, ref int i, int depth)
        {
            if (depth > 100) throw new FormatException("deep");
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("eof");
            char ch = s[i];
            if (ch == '{')
            {
                i++;
                Dictionary<string, object> d = new Dictionary<string, object>();
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    SkipWs(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException("key");
                    string k = Str(s, ref i);
                    SkipWs(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("colon");
                    i++;
                    d[k] = Value(s, ref i, depth + 1);
                    SkipWs(s, ref i);
                    if (i >= s.Length) throw new FormatException("eof");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("obj");
                }
            }
            if (ch == '[')
            {
                i++;
                List<object> l = new List<object>();
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i, depth + 1));
                    SkipWs(s, ref i);
                    if (i >= s.Length) throw new FormatException("eof");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException("arr");
                }
            }
            if (ch == '"') return Str(s, ref i);
            int st = i;
            while (i < s.Length)
            {
                char x = s[i];
                if (x == ',' || x == '}' || x == ']' || x == ' ' || x == '\t' || x == '\r' || x == '\n') break;
                i++;
            }
            string lit = s.Substring(st, i - st);
            if (lit == "true" || lit == "false" || lit == "null" || IsNumber(lit)) return new Raw(lit);
            throw new FormatException("literal");
        }

        static bool IsNumber(string lit)
        {
            if (lit.Length == 0) return false;
            for (int k = 0; k < lit.Length; k++)
            {
                if ("0123456789+-.eE".IndexOf(lit[k]) < 0) return false;
            }
            double dv;
            return double.TryParse(lit, NumberStyles.Float, CultureInfo.InvariantCulture, out dv);
        }

        static string Str(string s, ref int i)
        {
            i++; // открывающая кавычка
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("str");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) throw new FormatException("esc");
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
                            if (i + 4 > s.Length) throw new FormatException("u");
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                            break;
                        default: throw new FormatException("esc");
                    }
                }
                else sb.Append(c);
            }
        }

        public static string Write(object o)
        {
            StringBuilder sb = new StringBuilder();
            W(sb, o);
            return sb.ToString();
        }

        static void W(StringBuilder sb, object o)
        {
            if (o == null) { sb.Append("null"); return; }
            string str = o as string;
            if (str != null) { WStr(sb, str); return; }
            Raw r = o as Raw;
            if (r != null) { sb.Append(r.S); return; }
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in d)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WStr(sb, kv.Key);
                    sb.Append(':');
                    W(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            List<object> l = o as List<object>;
            if (l != null)
            {
                sb.Append('[');
                for (int k = 0; k < l.Count; k++)
                {
                    if (k > 0) sb.Append(',');
                    W(sb, l[k]);
                }
                sb.Append(']');
                return;
            }
            sb.Append("null");
        }

        static void WStr(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int k = 0; k < s.Length; k++)
            {
                char c = s[k];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    // ---------- буферизованное чтение из сокета ----------
    sealed class Reader
    {
        readonly Stream s;
        readonly byte[] buf = new byte[16384];
        int pos, len;

        public Reader(Stream s) { this.s = s; }

        public int ReadByte()
        {
            if (pos >= len)
            {
                len = s.Read(buf, 0, buf.Length);
                pos = 0;
                if (len <= 0) { len = 0; return -1; }
            }
            return buf[pos++];
        }

        public bool ReadExact(byte[] d, int off, int n)
        {
            while (n > 0)
            {
                if (pos >= len)
                {
                    len = s.Read(buf, 0, buf.Length);
                    pos = 0;
                    if (len <= 0) { len = 0; return false; }
                }
                int k = Math.Min(n, len - pos);
                Buffer.BlockCopy(buf, pos, d, off, k);
                pos += k;
                off += k;
                n -= k;
            }
            return true;
        }
    }

    sealed class Pair
    {
        public string A, B;
        public Pair(string a, string b) { A = a; B = b; }
    }

    sealed class Client
    {
        public TcpClient Tc;
        public NetworkStream Ns;
        public string Id;
        public volatile bool Dead;
        readonly object wl = new object();

        public void SendRaw(byte[] f)
        {
            if (Dead) return;
            lock (wl)
            {
                try { Ns.Write(f, 0, f.Length); }
                catch (Exception)
                {
                    Dead = true;
                    try { Tc.Close(); } catch (Exception) { }
                }
            }
        }

        public void Send(object o)
        {
            if (Dead) return;
            byte[] p = Encoding.UTF8.GetBytes(Json.Write(o));
            SendRaw(Program.Frame(1, p));
        }
    }

    static class Program
    {
        static int port = 1220;
        static string file;
        static bool lan = false;
        static readonly object L = new object();
        static readonly Dictionary<string, Client> peers = new Dictionary<string, Client>();
        static readonly Dictionary<string, Pair> pairs = new Dictionary<string, Pair>();
        static readonly RandomNumberGenerator rng = RandomNumberGenerator.Create();
        const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        static void Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }
            string env = Environment.GetEnvironmentVariable("PORT");
            int ep;
            if (env != null && int.TryParse(env, out ep)) port = ep;
            bool openBrowser = true;
            string f = null;
            if (Environment.GetEnvironmentVariable("LAN") == "1") lan = true;
            foreach (string a in args)
            {
                if (a == "--no-browser") openBrowser = false;
                else if (a == "--lan") lan = true;
                else if (a.StartsWith("--port="))
                {
                    int q;
                    if (int.TryParse(a.Substring(7), out q)) port = q;
                }
                else f = a;
            }
            file = ResolveFile(f);

            TcpListener listener = null;
            int first = port;
            for (int p = first; p < first + 10; p++)
            {
                try
                {
                    TcpListener l = new TcpListener(lan ? IPAddress.Any : IPAddress.Loopback, p);
                    l.Start();
                    listener = l;
                    port = p;
                    break;
                }
                catch (SocketException) { }
            }
            if (listener == null)
            {
                Console.WriteLine("Не удалось занять ни один порт " + first + "-" + (first + 9) + ". Закрой другую копию сервера или укажи --port=ЧИСЛО");
                return;
            }

            Console.WriteLine("Игра запущена: http://localhost:" + port);
            if (!lan)
            {
                Console.WriteLine("Режим без брандмауэра: сервер доступен только на этом ПК, мультиплеер идёт напрямую между игроками через интернет (WebRTC).");
                Console.WriteLine("Для игры по локальной сети через этот сервер запусти start-lan.bat.");
            }
            else Console.WriteLine("Открой на ПК и на телефоне (в одной Wi-Fi сети):");
            try
            {
                if (lan)
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                            Console.WriteLine("  http://" + ua.Address + ":" + port);
                    }
                }
            }
            catch (Exception) { }
            if (!File.Exists(file)) Console.WriteLine("ВНИМАНИЕ: не найден файл игры " + file);
            Console.WriteLine("Чтобы остановить сервер, закрой это окно.");

            if (openBrowser)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("http://localhost:" + port);
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
                catch (Exception) { }
            }

            while (true)
            {
                TcpClient tc;
                try { tc = listener.AcceptTcpClient(); }
                catch (Exception) { break; }
                TcpClient cc = tc;
                Thread th = new Thread(delegate () { Handle(cc); });
                th.IsBackground = true;
                th.Start();
            }
        }

        static string ResolveFile(string f)
        {
            if (f != null) return Path.GetFullPath(f);
            string a = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.html");
            if (File.Exists(a)) return a;
            string b = Path.Combine(Directory.GetCurrentDirectory(), "index.html");
            if (File.Exists(b)) return b;
            return a;
        }

        // ---------- HTTP / WebSocket ----------
        static void Handle(TcpClient tc)
        {
            Client c = null;
            try
            {
                tc.NoDelay = true;
                tc.ReceiveTimeout = 15000;
                tc.SendTimeout = 15000;
                NetworkStream ns = tc.GetStream();
                Reader r = new Reader(ns);

                StringBuilder head = new StringBuilder();
                while (true)
                {
                    int b = r.ReadByte();
                    if (b < 0) return;
                    head.Append((char)b);
                    if (head.Length > 16384) return;
                    int n = head.Length;
                    if (n >= 4 && head[n - 1] == '\n' && head[n - 2] == '\r' && head[n - 3] == '\n' && head[n - 4] == '\r') break;
                }
                string[] lines = head.ToString().Split(new string[] { "\r\n" }, StringSplitOptions.None);
                string[] first = lines[0].Split(' ');
                if (first.Length < 2) return;
                string path = first[1];
                int qi = path.IndexOf('?');
                if (qi >= 0) path = path.Substring(0, qi);
                Dictionary<string, string> hd = new Dictionary<string, string>();
                for (int i = 1; i < lines.Length; i++)
                {
                    int ci = lines[i].IndexOf(':');
                    if (ci <= 0) continue;
                    hd[lines[i].Substring(0, ci).Trim().ToLowerInvariant()] = lines[i].Substring(ci + 1).Trim();
                }

                if (path == "/ws")
                {
                    string key;
                    if (!hd.TryGetValue("sec-websocket-key", out key)) return;
                    string acc = Convert.ToBase64String(SHA1.Create().ComputeHash(Encoding.ASCII.GetBytes(key + WsGuid)));
                    byte[] resp = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + acc + "\r\n\r\n");
                    ns.Write(resp, 0, resp.Length);
                    tc.ReceiveTimeout = 0;
                    c = new Client();
                    c.Tc = tc;
                    c.Ns = ns;
                    WsLoop(c, r);
                    return;
                }

                if (path == "/health")
                {
                    int cnt;
                    lock (L) { cnt = peers.Count; }
                    Respond(ns, 200, "OK", "text/plain;charset=utf-8", Encoding.UTF8.GetBytes("ok " + cnt));
                    return;
                }
                if (path == "/shakhed.mp3")
                {
                    string audio = Path.Combine(Path.GetDirectoryName(file) ?? AppDomain.CurrentDomain.BaseDirectory, "shakhed.mp3");
                    if (File.Exists(audio)) Respond(ns, 200, "OK", "audio/mpeg", File.ReadAllBytes(audio));
                    else Respond(ns, 404, "Not Found", "text/plain;charset=utf-8", new byte[0]);
                    return;
                }
                if (path == "/favicon.ico")
                {
                    Respond(ns, 404, "Not Found", "text/plain;charset=utf-8", new byte[0]);
                    return;
                }

                string html;
                try { html = File.ReadAllText(file, Encoding.UTF8); }
                catch (Exception)
                {
                    Respond(ns, 500, "Internal Server Error", "text/plain;charset=utf-8", Encoding.UTF8.GetBytes("Не найден " + file));
                    return;
                }
                string inj = !lan ? "" : "<script>window.LG_RELAY=(location.protocol===\"https:\"?\"wss://\":\"ws://\")+location.host+\"/ws\";</script>";
                int hi = html.IndexOf("<head>", StringComparison.Ordinal);
                if (hi >= 0) html = html.Substring(0, hi + 6) + inj + html.Substring(hi + 6);
                Respond(ns, 200, "OK", "text/html;charset=utf-8", Encoding.UTF8.GetBytes(html));
            }
            catch (Exception) { }
            finally
            {
                if (c != null) Gone(c);
                try { tc.Close(); } catch (Exception) { }
            }
        }

        static void Respond(NetworkStream ns, int code, string status, string type, byte[] body)
        {
            string h = "HTTP/1.1 " + code + " " + status + "\r\nContent-Type: " + type + "\r\nCache-Control: no-store\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(h);
            ns.Write(hb, 0, hb.Length);
            if (body.Length > 0) ns.Write(body, 0, body.Length);
            ns.Flush();
        }

        public static byte[] Frame(int op, byte[] payload)
        {
            int n = payload.Length;
            byte[] h;
            if (n < 126) h = new byte[] { (byte)(0x80 | op), (byte)n };
            else if (n < 65536) h = new byte[] { (byte)(0x80 | op), 126, (byte)(n >> 8), (byte)(n & 255) };
            else h = new byte[] { (byte)(0x80 | op), 127, 0, 0, 0, 0, (byte)(n >> 24), (byte)((n >> 16) & 255), (byte)((n >> 8) & 255), (byte)(n & 255) };
            byte[] f = new byte[h.Length + n];
            Buffer.BlockCopy(h, 0, f, 0, h.Length);
            Buffer.BlockCopy(payload, 0, f, h.Length, n);
            return f;
        }

        static void WsLoop(Client c, Reader r)
        {
            MemoryStream frag = new MemoryStream();
            byte[] two = new byte[2];
            while (true)
            {
                if (!r.ReadExact(two, 0, 2)) return;
                int op = two[0] & 15;
                bool fin = (two[0] & 128) != 0;
                bool mask = (two[1] & 128) != 0;
                long n = two[1] & 127;
                if (n == 126)
                {
                    byte[] e = new byte[2];
                    if (!r.ReadExact(e, 0, 2)) return;
                    n = (e[0] << 8) | e[1];
                }
                else if (n == 127)
                {
                    byte[] e = new byte[8];
                    if (!r.ReadExact(e, 0, 8)) return;
                    n = 0;
                    for (int i = 0; i < 8; i++) n = (n << 8) | e[i];
                }
                if (!mask) return;
                if (n < 0 || n > 4000000) return;
                byte[] key = new byte[4];
                if (!r.ReadExact(key, 0, 4)) return;
                byte[] p = new byte[(int)n];
                if (n > 0 && !r.ReadExact(p, 0, (int)n)) return;
                for (int i = 0; i < p.Length; i++) p[i] ^= key[i & 3];
                if (op == 8)
                {
                    c.SendRaw(Frame(8, new byte[0]));
                    return;
                }
                if (op == 9) { c.SendRaw(Frame(10, p)); continue; }
                if (op == 10) continue;
                if (op == 1 || op == 2 || op == 0)
                {
                    frag.Write(p, 0, p.Length);
                    if (frag.Length > 4000000) return;
                    if (fin)
                    {
                        string text = Encoding.UTF8.GetString(frag.ToArray());
                        frag.SetLength(0);
                        Msg(c, text);
                    }
                }
            }
        }

        // ---------- ретранслятор: то же, что делал server.js ----------
        static string S(Dictionary<string, object> m, string k)
        {
            object v;
            if (!m.TryGetValue(k, out v)) return null;
            return v as string;
        }

        static Dictionary<string, object> Obj(params object[] kv)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        static bool ValidId(string id)
        {
            if (id.Length == 0) return false;
            for (int i = 0; i < id.Length; i++)
            {
                char ch = id[i];
                bool ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-';
                if (!ok) return false;
            }
            return true;
        }

        static string RandId()
        {
            byte[] b = new byte[5];
            rng.GetBytes(b);
            StringBuilder sb = new StringBuilder("p");
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        static void Msg(Client c, string text)
        {
            Dictionary<string, object> m;
            try { m = Json.Parse(text) as Dictionary<string, object>; }
            catch (Exception) { return; }
            if (m == null) return;
            string t = S(m, "t");

            if (t == "reg")
            {
                if (c.Id != null) return;
                string id = S(m, "id");
                if (id != null)
                {
                    if (id.Length > 64) id = id.Substring(0, 64);
                    if (!ValidId(id)) id = null;
                }
                bool taken = false;
                lock (L)
                {
                    if (id != null && peers.ContainsKey(id)) taken = true;
                    else
                    {
                        if (id == null)
                        {
                            do { id = RandId(); } while (peers.ContainsKey(id));
                        }
                        c.Id = id;
                        peers[id] = c;
                    }
                }
                if (taken) { c.Send(Obj("t", "err", "type", "unavailable-id")); return; }
                c.Send(Obj("t", "ok", "id", c.Id));
                return;
            }

            if (c.Id == null) return;

            if (t == "conn")
            {
                string to = S(m, "to");
                string cid = S(m, "cid");
                if (to == null || cid == null) return;
                Client o = null;
                lock (L)
                {
                    peers.TryGetValue(to, out o);
                    if (o != null && o != c) pairs[cid] = new Pair(c.Id, to);
                    else o = null;
                }
                if (o == null) { c.Send(Obj("t", "nopeer")); return; }
                o.Send(Obj("t", "conn", "from", c.Id, "cid", cid));
                c.Send(Obj("t", "open", "cid", cid));
                return;
            }

            string cid2 = S(m, "cid");
            if (cid2 == null) return;
            Client other = null;
            lock (L)
            {
                Pair pr;
                if (!pairs.TryGetValue(cid2, out pr)) return;
                if (pr.A != c.Id && pr.B != c.Id) return;
                string oid = pr.A == c.Id ? pr.B : pr.A;
                peers.TryGetValue(oid, out other);
                if (t == "close") pairs.Remove(cid2);
            }
            if (t == "data")
            {
                if (other != null)
                {
                    object d;
                    m.TryGetValue("d", out d);
                    other.Send(Obj("t", "data", "cid", cid2, "d", d));
                }
            }
            else if (t == "close")
            {
                if (other != null) other.Send(Obj("t", "close", "cid", cid2));
            }
        }

        static void Gone(Client c)
        {
            c.Dead = true;
            List<KeyValuePair<Client, string>> notify = new List<KeyValuePair<Client, string>>();
            lock (L)
            {
                if (c.Id != null)
                {
                    Client cur;
                    if (peers.TryGetValue(c.Id, out cur) && cur == c) peers.Remove(c.Id);
                    List<string> keys = new List<string>(pairs.Keys);
                    foreach (string k in keys)
                    {
                        Pair pr = pairs[k];
                        if (pr.A == c.Id || pr.B == c.Id)
                        {
                            string oid = pr.A == c.Id ? pr.B : pr.A;
                            Client o;
                            peers.TryGetValue(oid, out o);
                            pairs.Remove(k);
                            if (o != null) notify.Add(new KeyValuePair<Client, string>(o, k));
                        }
                    }
                }
            }
            foreach (KeyValuePair<Client, string> kv in notify) kv.Key.Send(Obj("t", "close", "cid", kv.Value));
        }
    }
}
