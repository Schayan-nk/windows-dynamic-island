using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Island;

public sealed record LyricLine(TimeSpan At, string Text);

public sealed class Lyrics
{
    public List<LyricLine> Lines { get; init; } = new();
    /// <summary>Every line has a time, so the island can follow the song.</summary>
    public bool Synced { get; init; }
    public bool Instrumental { get; init; }
    public bool Rtl { get; init; }
}

/// <summary>LRC text ("[01:02.50] words") to timed lines.</summary>
public static class Lrc
{
    static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    static readonly Regex Offset = new(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    static readonly Regex WordStamp = new(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

    public static List<LyricLine> Parse(string? lrc)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return lines;
        // [offset:+500] shows every line half a second sooner
        var off = Offset.Match(lrc);
        double shift = off.Success && int.TryParse(off.Groups[1].Value, out var ms) ? -ms / 1000.0 : 0;
        foreach (var raw in lrc.Split('\n'))
        {
            var row = raw.TrimEnd('\r');
            var times = new List<double>();
            int pos = 0;
            for (var m = Stamp.Match(row); m.Success && m.Index == pos; m = Stamp.Match(row, pos))
            {
                double frac = 0;
                if (m.Groups[3].Success) frac = int.Parse(m.Groups[3].Value) / Math.Pow(10, m.Groups[3].Value.Length);
                times.Add(int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) + frac);
                pos = m.Index + m.Length;
            }
            if (times.Count == 0) continue; // [ar:...], [ti:...] and other tags
            var text = WordStamp.Replace(row[pos..], "").Trim();
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            foreach (var t in times) lines.Add(new LyricLine(TimeSpan.FromSeconds(Math.Max(0, t + shift)), text));
        }
        return lines.OrderBy(l => l.At).ToList();
    }

    /// <summary>The line being sung at <paramref name="t"/>, or -1 before the first one.</summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, TimeSpan t)
    {
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].At <= t) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }
}

/// <summary>
/// Looks songs up on LRCLIB (lrclib.net), a free and open lyrics database: synced lyrics when it has
/// them, plain text otherwise. Only the song's title, artist, album and length are sent.
/// </summary>
public sealed class LyricsService
{
    static readonly HttpClient Http = CreateClient();
    readonly Dictionary<string, Lyrics?> _cache = new();
    readonly Queue<string> _order = new();

    static HttpClient CreateClient()
    {
        var h = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Island/1.1 (+https://github.com/Schayan-nk/windows-dynamic-island)");
        return h;
    }

    public static string Key(string artist, string title, TimeSpan duration) =>
        $"{Norm(artist)}|{Norm(title)}|{(int)Math.Round(duration.TotalSeconds)}";

    /// <summary>Null when LRCLIB has nothing for the song. Throws when the site cannot be reached.</summary>
    public async Task<Lyrics?> GetAsync(string artist, string title, string album, TimeSpan duration, CancellationToken ct)
    {
        var key = Key(artist, title, duration);
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var found = await FetchAsync(artist, title, album, duration, ct);
        if (_cache.TryAdd(key, found))
        {
            _order.Enqueue(key);
            while (_order.Count > 200) _cache.Remove(_order.Dequeue());
        }
        return found;
    }

    static async Task<Lyrics?> FetchAsync(string artist, string title, string album, TimeSpan duration, CancellationToken ct)
    {
        string t = CleanTitle(title), a = MainArtist(artist);
        if (t.Length == 0) return null;
        int secs = (int)Math.Round(duration.TotalSeconds);

        // 1. the exact song (needs the album)  2. title and artist  3. free text
        if (a.Length > 0 && album.Length > 0 && secs > 0
            && await GetJsonAsync($"api/get?artist_name={E(a)}&track_name={E(t)}&album_name={E(album)}&duration={secs}", ct) is { ValueKind: JsonValueKind.Object } exact
            && FromRecord(exact) is { } l)
            return l;
        var list = await GetJsonAsync(a.Length > 0 ? $"api/search?track_name={E(t)}&artist_name={E(a)}" : $"api/search?q={E(t)}", ct);
        var best = Pick(list, a, t, duration);
        if (best == null && a.Length > 0) best = Pick(await GetJsonAsync($"api/search?q={E(a + " " + t)}", ct), a, t, duration);
        return best is { } b ? FromRecord(b) : null;
    }

    static string E(string s) => Uri.EscapeDataString(s);

    static async Task<JsonElement?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var r = await Http.GetAsync(path, ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        await using var s = await r.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// The best record for the song: the same title, synced if possible, and the same length (Spotify and
    /// LRCLIB agree within a second or two; a live or radio version that is longer would be out of step).
    /// </summary>
    public static JsonElement? Pick(JsonElement? list, string artist, string title, TimeSpan duration)
    {
        if (list is not { ValueKind: JsonValueKind.Array } arr) return null;
        string nt = Norm(CleanTitle(title)), na = Norm(MainArtist(artist));
        if (nt.Length == 0) return null;
        JsonElement? best = null;
        int bestScore = int.MinValue;
        foreach (var r in arr.EnumerateArray())
        {
            bool synced = Str(r, "syncedLyrics").Length > 0, plain = Str(r, "plainLyrics").Length > 0, instrumental = IsTrue(r, "instrumental");
            if (!synced && !plain && !instrumental) continue;
            string rt = Norm(CleanTitle(Str(r, "trackName"))), ra = Norm(Str(r, "artistName"));
            if (rt.Length == 0 || (rt != nt && !rt.Contains(nt) && !nt.Contains(rt))) continue;
            int score = synced ? 4 : instrumental ? 2 : 1;
            score += rt == nt ? 3 : 1;
            if (na.Length > 0) score += ra == na || ra.Contains(na) || na.Contains(ra) ? 3 : -5;
            if (duration > TimeSpan.Zero && r.TryGetProperty("duration", out var dv) && dv.ValueKind == JsonValueKind.Number)
            {
                double diff = Math.Abs(dv.GetDouble() - duration.TotalSeconds);
                if (diff > 10) continue;
                score += diff <= 2 ? 4 : 1;
            }
            if (score > bestScore) { bestScore = score; best = r; }
        }
        return bestScore >= 5 ? best : null;
    }

    public static Lyrics? FromRecord(JsonElement r) => Build(Str(r, "syncedLyrics"), Str(r, "plainLyrics"), IsTrue(r, "instrumental"));

    public static Lyrics? Build(string? synced, string? plain, bool instrumental)
    {
        if (instrumental) return new Lyrics { Instrumental = true };
        var timed = Lrc.Parse(synced);
        if (timed.Count > 0) return new Lyrics { Lines = timed, Synced = true, Rtl = MostlyRtl(timed) };
        var rows = (plain ?? "").Replace("\r", "").Split('\n').Select(s => s.Trim()).ToList();
        while (rows.Count > 0 && rows[0].Length == 0) rows.RemoveAt(0);
        while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
        if (rows.Count == 0) return null;
        var lines = rows.Select(s => new LyricLine(TimeSpan.Zero, s)).ToList();
        return new Lyrics { Lines = lines, Synced = false, Rtl = MostlyRtl(lines) };
    }

    static bool MostlyRtl(List<LyricLine> lines)
    {
        int words = 0, rtl = 0;
        foreach (var l in lines)
        {
            if (l.Text.Length == 0) continue;
            words++;
            if (Fmt.IsRtl(l.Text)) rtl++;
        }
        return words > 0 && rtl * 2 > words;
    }

    static readonly Regex Feat = new(@"\s*[\(\[](feat\.?|ft\.?|with)\s[^\)\]]*[\)\]]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Tagged = new(@"\s*[\(\[][^\)\]]*\b(remaster(ed)?|version|edit|live|mix|mono|stereo|official|video|audio|lyrics?|visualizer)\b[^\)\]]*[\)\]]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex DashTail = new(@"\s+-\s+[^-]*\b(remaster(ed)?|version|edit|live|mix|mono|stereo|single|radio)\b.*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex FeatArtist = new(@"\s+(feat\.?|ft\.?)\s.*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"Song - Remastered 2011", "Song (feat. Someone)", "Song [Official Video]": all just "Song".</summary>
    public static string CleanTitle(string title) => DashTail.Replace(Tagged.Replace(Feat.Replace(title, ""), ""), "").Trim();

    static string MainArtist(string artist) => FeatArtist.Replace(artist.Split(',', ';')[0], "").Trim();

    /// <summary>Lower case, letters and digits only; Arabic letter forms as Persian ones.</summary>
    static string Norm(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s.StartsWith("the ")) s = s[4..];
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormKD))
        {
            if (!char.IsLetterOrDigit(ch)) continue;
            sb.Append(ch switch { 'ي' => 'ی', 'ى' => 'ی', 'ك' => 'ک', 'ة' => 'ه', _ => ch });
        }
        return sb.ToString();
    }

    static string Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";

    static bool IsTrue(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
