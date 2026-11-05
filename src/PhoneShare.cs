using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Island;

/// <summary>
/// AirDrop-style sharing with a phone on the same Wi-Fi. The phone scans a QR code and gets a web page:
/// it can save what is in the tray and send files to the PC. No app and no cloud: a small HTTP server on
/// the local network, reachable only through a random link that stops working after an hour.
/// </summary>
public sealed class PhoneShare
{
    readonly Func<TrayItem[]> _items;
    readonly Func<int> _version;
    readonly Action<string, string> _received; // path, device
    TcpListener? _listener;
    CancellationTokenSource? _cts;

    public PhoneShare(Func<TrayItem[]> items, Func<int> version, Action<string, string> received)
    {
        _items = items;
        _version = version;
        _received = received;
    }

    public string Token { get; private set; } = "";
    public IPAddress? Address { get; private set; }
    public string NetworkName { get; private set; } = "";
    public int Port { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public string? Device { get; private set; }
    public DateTime LastSeen { get; private set; }
    public bool Running => _listener != null;
    public string Url => $"http://{Address}:{Port}/{Token}/";
    public bool Connected => Device != null && DateTime.Now - LastSeen < TimeSpan.FromSeconds(12);
    public static string SaveDir => Path.Combine(Native.DownloadsFolder(), "Island");

    public event Action? Changed;
    /// <summary>File name and 0..1 while a file arrives from the phone.</summary>
    public event Action<string, double>? Progress;

    public static List<(IPAddress Ip, string Name)> Candidates()
    {
        var list = new List<(IPAddress Ip, string Name, int Score)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var d = (ni.Description + " " + ni.Name).ToLowerInvariant();
                bool virt = d.Contains("virtual") || d.Contains("vmware") || d.Contains("hyper-v") || d.Contains("vethernet")
                    || d.Contains("virtualbox") || d.Contains("wsl") || d.Contains("bluetooth") || d.Contains("tap-")
                    || d.Contains("wireguard") || d.Contains("vpn") || d.Contains("loopback");
                var props = ni.GetIPProperties();
                bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;
                    bool priv = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
                    int score = (gw ? 4 : 0) + (virt ? -8 : 0) + (priv ? 2 : 0)
                        + (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 2 : 0)
                        + (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 1 : 0);
                    var kind = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : ni.Name;
                    list.Add((ua.Address, kind, score));
                }
            }
        }
        catch { }
        return list.OrderByDescending(x => x.Score).Select(x => (x.Ip, x.Name)).ToList();
    }

    public void Start(IPAddress ip, string networkName)
    {
        Stop(false);
        Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        foreach (var port in new[] { 52800, 52801, 52802, 52803, 0 })
        {
            try
            {
                var l = new TcpListener(ip, port);
                l.Start();
                _listener = l;
                Port = ((IPEndPoint)l.LocalEndpoint).Port;
                break;
            }
            catch (SocketException) { }
        }
        if (_listener == null) throw new InvalidOperationException("No free network port.");
        Address = ip;
        NetworkName = networkName;
        ExpiresAt = DateTime.Now.AddHours(1);
        Device = null;
        LastSeen = default;
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(_listener, _cts.Token);
        Changed?.Invoke();
    }

    public void Stop(bool notify = true)
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
        Device = null;
        if (notify) Changed?.Invoke();
    }

    async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client, ct));
        }
    }

    sealed record Request(string Method, string Path, string Query, Dictionary<string, string> Headers, byte[] Extra);

    async Task HandleAsync(TcpClient client, CancellationToken appCt)
    {
        using var _ = client;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        cts.CancelAfter(TimeSpan.FromMinutes(45));
        var ct = cts.Token;
        try
        {
            client.NoDelay = true;
            var ns = client.GetStream();
            var req = await ReadHeadAsync(ns, ct);
            if (req == null) return;
            if (DateTime.Now > ExpiresAt) { await TextAsync(ns, 410, "Gone", ct); return; }

            var segs = req.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0 || !SameToken(segs[0])) { await TextAsync(ns, 404, "Not found", ct); return; }
            Seen(req.Headers.GetValueOrDefault("user-agent") ?? "");

            if (segs.Length == 1) { await PageAsync(ns, ct); return; }
            if (segs[1] != "api" || segs.Length < 3) { await TextAsync(ns, 404, "Not found", ct); return; }
            switch (segs[2])
            {
                case "ping":
                    await JsonAsync(ns, new { v = _version(), pc = Environment.MachineName }, ct);
                    break;
                case "files":
                    var files = _items().Where(i => !i.IsDir && File.Exists(i.Path))
                        .Select(i => new { id = i.Id, name = i.Name, size = i.Size, ext = i.Ext, thumb = i.PhoneThumb != null }).ToArray();
                    await JsonAsync(ns, new { v = _version(), files }, ct);
                    break;
                case "thumb" when segs.Length > 3:
                    var t = _items().FirstOrDefault(i => i.Id == segs[3]);
                    if (t?.PhoneThumb == null) await TextAsync(ns, 404, "Not found", ct);
                    else await BytesAsync(ns, 200, "image/jpeg", t.PhoneThumb, ct);
                    break;
                case "file" when segs.Length > 3:
                    var f = _items().FirstOrDefault(i => i.Id == segs[3]);
                    if (f == null || f.IsDir || !File.Exists(f.Path)) await TextAsync(ns, 404, "Not found", ct);
                    else await FileAsync(ns, req, f, ct);
                    break;
                case "upload" when req.Method is "PUT" or "POST":
                    await UploadAsync(ns, req, ct);
                    break;
                default:
                    await TextAsync(ns, 404, "Not found", ct);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (Exception ex) { Settings.Log(ex); }
    }

    bool SameToken(string s)
    {
        var a = Encoding.ASCII.GetBytes(s);
        var b = Encoding.ASCII.GetBytes(Token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    void Seen(string ua)
    {
        LastSeen = DateTime.Now;
        if (Device != null) return;
        Device = ua.Contains("iPhone") ? "iPhone" : ua.Contains("iPad") ? "iPad" : ua.Contains("Android") ? "Android phone" : "Phone";
        Changed?.Invoke();
    }

    static async Task<Request?> ReadHeadAsync(NetworkStream ns, CancellationToken ct)
    {
        var buf = new byte[32 * 1024];
        int len = 0;
        using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headCts.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            if (len == buf.Length) return null;
            int n = await ns.ReadAsync(buf.AsMemory(len), headCts.Token);
            if (n <= 0) return null;
            len += n;
            int end = IndexOf(buf, len, "\r\n\r\n"u8);
            if (end < 0) continue;
            var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int c = line.IndexOf(':');
                if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim();
            }
            var target = first[1];
            int q = target.IndexOf('?');
            var path = q >= 0 ? target[..q] : target;
            var query = q >= 0 ? target[(q + 1)..] : "";
            return new Request(first[0].ToUpperInvariant(), Uri.UnescapeDataString(path), query, headers, buf[(end + 4)..len]);
        }
    }

    static int IndexOf(byte[] buf, int len, ReadOnlySpan<byte> pat)
    {
        int i = buf.AsSpan(0, len).IndexOf(pat);
        return i;
    }

    static string? QueryValue(string query, string key)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int e = part.IndexOf('=');
            var k = e >= 0 ? part[..e] : part;
            if (k == key) return Uri.UnescapeDataString((e >= 0 ? part[(e + 1)..] : "").Replace('+', ' '));
        }
        return null;
    }

    async Task PageAsync(NetworkStream ns, CancellationToken ct)
    {
        string html;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("phone.html"))
        using (var r = new StreamReader(s!))
            html = await r.ReadToEndAsync(ct);
        html = html.Replace("{{BASE}}", "/" + Token + "/").Replace("{{PC}}", WebUtility.HtmlEncode(Environment.MachineName));
        await BytesAsync(ns, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), ct);
    }

    async Task FileAsync(NetworkStream ns, Request req, TrayItem item, CancellationToken ct)
    {
        var fi = new FileInfo(item.Path);
        long total = fi.Length, start = 0, end = total - 1;
        bool partial = false;
        if (req.Headers.TryGetValue("range", out var range) && TryRange(range, total, out var s, out var e)) { start = s; end = e; partial = true; }
        long count = Math.Max(0, end - start + 1);
        bool download = QueryValue(req.Query, "dl") == "1";
        var ascii = new string(item.Name.Select(ch => ch < 32 || ch > 126 || ch == '"' || ch == '\\' ? '_' : ch).ToArray());
        var sb = new StringBuilder();
        sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        sb.Append($"Content-Type: {Mime(fi.Extension)}\r\nContent-Length: {count}\r\nAccept-Ranges: bytes\r\n");
        if (partial) sb.Append($"Content-Range: bytes {start}-{end}/{total}\r\n");
        sb.Append($"Content-Disposition: {(download ? "attachment" : "inline")}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(item.Name)}\r\n");
        sb.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        await ns.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        if (req.Method == "HEAD" || count == 0) return;
        await using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true);
        fs.Seek(start, SeekOrigin.Begin);
        var b = new byte[1 << 16];
        long left = count;
        while (left > 0)
        {
            int n = await fs.ReadAsync(b.AsMemory(0, (int)Math.Min(b.Length, left)), ct);
            if (n <= 0) break;
            await ns.WriteAsync(b.AsMemory(0, n), ct);
            left -= n;
        }
    }

    static bool TryRange(string h, long total, out long start, out long end)
    {
        start = 0; end = total - 1;
        if (!h.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || total <= 0) return false;
        var spec = h[6..].Split(',')[0].Trim();
        int dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var a = spec[..dash]; var z = spec[(dash + 1)..];
        if (a.Length == 0)
        {
            if (!long.TryParse(z, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, total - suffix);
            return true;
        }
        if (!long.TryParse(a, out start) || start >= total) return false;
        if (z.Length > 0 && long.TryParse(z, out var e)) end = Math.Min(e, total - 1);
        return end >= start;
    }

    async Task UploadAsync(NetworkStream ns, Request req, CancellationToken ct)
    {
        if (!req.Headers.TryGetValue("content-length", out var cl) || !long.TryParse(cl, out long length) || length < 0)
        {
            await TextAsync(ns, 411, "Length required", ct);
            return;
        }
        if (length > 16L * 1024 * 1024 * 1024) { await TextAsync(ns, 413, "Too large", ct); return; }
        Directory.CreateDirectory(SaveDir);
        var dest = UniquePath(Path.Combine(SaveDir, SafeName(QueryValue(req.Query, "name") ?? "file")));
        try
        {
            var root = Path.GetPathRoot(dest);
            if (root != null && new DriveInfo(root).AvailableFreeSpace < length + 200L * 1024 * 1024)
            {
                await TextAsync(ns, 507, "Disk is full", ct);
                return;
            }
        }
        catch { }
        if (req.Headers.TryGetValue("expect", out var expect) && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
            await ns.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct);

        long got = 0;
        var shown = Path.GetFileName(dest);
        Progress?.Invoke(shown, 0);
        var lastReport = DateTime.Now;
        try
        {
            await using (var fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                if (req.Extra.Length > 0)
                {
                    int take = (int)Math.Min(req.Extra.Length, length);
                    await fs.WriteAsync(req.Extra.AsMemory(0, take), ct);
                    got += take;
                }
                var b = new byte[1 << 16];
                while (got < length)
                {
                    int n = await ns.ReadAsync(b.AsMemory(0, (int)Math.Min(b.Length, length - got)), ct);
                    if (n <= 0) throw new IOException("The phone closed the connection.");
                    await fs.WriteAsync(b.AsMemory(0, n), ct);
                    got += n;
                    if ((DateTime.Now - lastReport).TotalMilliseconds > 120 && length > 0)
                    {
                        lastReport = DateTime.Now;
                        Progress?.Invoke(shown, (double)got / length);
                    }
                }
            }
        }
        catch
        {
            try { File.Delete(dest); } catch { }
            throw;
        }
        Progress?.Invoke(shown, 1);
        _received(dest, Device ?? "Phone");
        await JsonAsync(ns, new { ok = true, name = Path.GetFileName(dest) }, ct);
    }

    static string SafeName(string name)
    {
        name = Path.GetFileName(name.Replace('\\', '/').Split('/').Last());
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().Trim('.');
        if (name.Length == 0) name = "file";
        if (name.Length > 120)
        {
            var ext = Path.GetExtension(name);
            name = name[..(120 - ext.Length)] + ext;
        }
        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            name = "_" + name;
        return name;
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    static string Mime(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        ".txt" or ".log" or ".md" or ".csv" => "text/plain; charset=utf-8",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    static Task TextAsync(NetworkStream ns, int status, string text, CancellationToken ct) =>
        BytesAsync(ns, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), ct);

    static Task JsonAsync(NetworkStream ns, object value, CancellationToken ct) =>
        BytesAsync(ns, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value), ct);

    static async Task BytesAsync(NetworkStream ns, int status, string type, byte[] body, CancellationToken ct)
    {
        var reason = status switch
        {
            200 => "OK", 404 => "Not Found", 410 => "Gone", 411 => "Length Required", 413 => "Payload Too Large", 507 => "Insufficient Storage", _ => "Error",
        };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                   "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await ns.WriteAsync(body, ct);
    }
}
