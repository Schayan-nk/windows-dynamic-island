using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace Island;

/// <summary>
/// Island.exe --selftest LOG [--online]: checks the phone server (loopback only), the transcript reader,
/// lyrics parsing and the start-with-Windows entry. --online also asks LRCLIB for a song.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string logPath, bool online = false)
    {
        var log = new StringBuilder();
        int fails = 0;
        void Check(bool ok, string what)
        {
            log.AppendLine((ok ? "PASS  " : "FAIL  ") + what);
            if (!ok) fails++;
        }

        try { await PhoneTests(Check); }
        catch (Exception ex) { Check(false, "phone tests threw: " + ex); }
        try { ClaudeTests(Check); }
        catch (Exception ex) { Check(false, "claude tests threw: " + ex); }
        try { LyricsTests(Check); }
        catch (Exception ex) { Check(false, "lyrics tests threw: " + ex); }
        try { StartupTests(Check); }
        catch (Exception ex) { Check(false, "startup tests threw: " + ex); }
        if (online)
        {
            try { await LyricsOnline(Check, log); }
            catch (Exception ex) { Check(false, "online lyrics test threw: " + ex.Message); }
        }

        log.AppendLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllText(logPath, log.ToString());
        return fails;
    }

    static async Task PhoneTests(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "island-selftest");
        Directory.CreateDirectory(dir);
        var text = "hello from the island " + new string('x', 70_000);
        var f1 = Path.Combine(dir, "hello world.txt");
        File.WriteAllText(f1, text);
        var item = new TrayItem(f1, false);
        TrayItem[] items = { item };
        string? received = null;
        var share = new PhoneShare(() => items, () => 7, (p, _) => received = p);
        share.Start(IPAddress.Loopback, "loopback");
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X)");
            var root = share.Url;

            var page = await http.GetStringAsync(root);
            check(page.Contains($"\"/{share.Token}/\""), "page carries its base path");
            check(!page.Contains("{{"), "page placeholders are filled");
            check(share.Device == "iPhone", "device is recognised from the user agent");

            var wrong = await http.GetAsync($"http://127.0.0.1:{share.Port}/not-the-token/api/files");
            check(wrong.StatusCode == HttpStatusCode.NotFound, "a wrong token gets 404");

            var files = await http.GetStringAsync(root + "api/files");
            check(files.Contains("hello world.txt") && files.Contains(item.Id), "file list shows the tray");

            var whole = await http.GetByteArrayAsync(root + $"api/file/{item.Id}?dl=1");
            check(whole.Length == new FileInfo(f1).Length, "download returns the whole file");

            var req = new HttpRequestMessage(HttpMethod.Get, root + $"api/file/{item.Id}");
            req.Headers.Range = new RangeHeaderValue(6, 9);
            var part = await http.SendAsync(req);
            var bytes = await part.Content.ReadAsByteArrayAsync();
            check(part.StatusCode == HttpStatusCode.PartialContent && Encoding.ASCII.GetString(bytes) == text.Substring(6, 4), "byte range request");

            var missing = await http.GetAsync(root + "api/file/..%2F..%2Fwindows");
            check(missing.StatusCode == HttpStatusCode.NotFound, "unknown or crafted ids get 404");

            var body = Encoding.UTF8.GetBytes("sent from the phone");
            var up = await http.PutAsync(root + "api/upload?name=" + Uri.EscapeDataString(@"..\..\evil:na*me?.txt"), new ByteArrayContent(body));
            check(up.IsSuccessStatusCode, "upload is accepted");
            check(received != null && string.Equals(Path.GetDirectoryName(received), PhoneShare.SaveDir, StringComparison.OrdinalIgnoreCase),
                "upload lands in Downloads\\Island, not outside it");
            check(received != null && File.ReadAllText(received) == "sent from the phone", "uploaded bytes are intact");
            check(received != null && !Path.GetFileName(received).Contains(':') && !Path.GetFileName(received).Contains('*'), "unsafe characters are removed from the name");
            if (received != null) File.Delete(received);

            var ping = await http.GetStringAsync(root + "api/ping");
            check(ping.Contains("\"v\":7"), "ping reports the tray version");
        }
        finally
        {
            share.Stop(false);
        }

        bool refused;
        try
        {
            using var http2 = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
            await http2.GetAsync(share.Url);
            refused = false;
        }
        catch { refused = true; }
        check(refused, "nothing answers after Stop");
        try { Directory.Delete(dir, true); } catch { }
    }

    static void ClaudeTests(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "island-selftest-claude");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var proj = Path.Combine(root, "C--work-webshop");
        Directory.CreateDirectory(proj);
        var file = Path.Combine(proj, "s1.jsonl");
        var now = DateTime.UtcNow;
        string T(int sec) => now.AddSeconds(sec).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var cwd = Esc(@"C:\work\webshop");

        File.WriteAllText(file, string.Join("\n", new[]
        {
            $"{{\"type\":\"user\",\"cwd\":\"{cwd}\",\"timestamp\":\"{T(-60)}\",\"message\":{{\"role\":\"user\",\"content\":\"fix the VAT in checkout\"}}}}",
            $"{{\"type\":\"assistant\",\"cwd\":\"{cwd}\",\"timestamp\":\"{T(-55)}\",\"message\":{{\"id\":\"m1\",\"stop_reason\":\"tool_use\",\"usage\":{{\"input_tokens\":10,\"cache_read_input_tokens\":119990}},\"content\":[{{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{{\"file_path\":\"{Esc(@"C:\work\webshop\checkout.ts")}\"}}}}]}}}}",
            $"{{\"type\":\"user\",\"timestamp\":\"{T(-54)}\",\"message\":{{\"content\":[{{\"type\":\"tool_result\",\"content\":\"...\"}}]}}}}",
            $"{{\"type\":\"assistant\",\"timestamp\":\"{T(-50)}\",\"message\":{{\"id\":\"m2\",\"stop_reason\":\"tool_use\",\"content\":[{{\"type\":\"tool_use\",\"name\":\"Edit\",\"input\":{{\"file_path\":\"{Esc(@"C:\work\webshop\checkout.ts")}\",\"old_string\":\"const total = 0\",\"new_string\":\"const total = sum(items)\"}}}}]}}}}",
            "",
        }));
        var jobs = Path.Combine(root, "jobs.json");
        var w = new ClaudeWatcher(root, jobs);
        ClaudeUpdate? last = null;
        w.Updated += u => last = u;

        w.PollOnce();
        var p = last?.Primary;
        check(p != null && p.Working, "a fresh turn shows as working");
        check(p?.Project == "webshop", "project name comes from the working folder");
        check(p?.Stage == ClaudeStage.Edit && p.Activity == "Making changes", "stage follows the tools (read, then edit)");
        check(p?.File?.EndsWith("checkout.ts") == true && p.DiffOld == "const total = 0" && p.DiffNew == "const total = sum(items)", "current file and diff");
        check(p != null && Math.Abs(p.Context - 0.6) < 0.01, "context use is measured");

        File.AppendAllText(file, string.Join("\n", new[]
        {
            $"{{\"type\":\"assistant\",\"timestamp\":\"{T(-40)}\",\"message\":{{\"id\":\"m3\",\"stop_reason\":\"tool_use\",\"content\":[{{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{{\"command\":\"npm test\"}}}}]}}}}",
            $"{{\"type\":\"assistant\",\"timestamp\":\"{T(-5)}\",\"message\":{{\"id\":\"m4\",\"stop_reason\":\"end_turn\",\"content\":[{{\"type\":\"text\",\"text\":\"**Done!** The total now adds VAT the right way. All 48 tests pass.\"}}]}}}}",
            "",
        }));
        w.PollOnce();
        p = last?.Primary;
        check(last?.Finished != null, "the end of a turn is reported once, live");
        check(p != null && !p.Working && p.Stage == ClaudeStage.Done, "finished turn is done");
        check(p?.Summary == "Done! The total now adds VAT the right way. All 48 tests pass.", "summary is the last answer without markdown");
        check(last?.JobsToday == 1, "jobs today counts the finished turn");

        w.PollOnce();
        check(last?.Finished == null, "a finished turn is not reported twice");

        File.AppendAllText(file, $"{{\"type\":\"user\",\"timestamp\":\"{T(0)}\",\"message\":{{\"content\":\"next thing\"}}}}\n");
        w.PollOnce();
        check(last?.Primary?.Working == true && last.Primary.Stage == ClaudeStage.None, "a new prompt starts a new turn");

        File.AppendAllText(file, $"{{\"type\":\"user\",\"timestamp\":\"{T(1)}\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"[Request interrupted by user]\"}}]}}}}\n");
        w.PollOnce();
        check(last?.Primary?.Working == false, "an interrupt stops the turn");
        w.Dispose();
        try { Directory.Delete(root, true); } catch { }
    }

    // Made-up lines only: the tests never need a real song.
    static void LyricsTests(Action<bool, string> check)
    {
        var lines = Lrc.Parse("[ar:Nobody]\n[offset:+500]\n[00:01.00]first line\n[00:03.50][00:10.00]again and again\n[00:05.25]<00:05.25>word <00:05.80>timed\n[00:07]\n");
        check(lines.Count == 5, "LRC: one line per time stamp, tags skipped");
        check(lines.Count == 5 && lines[0].At == TimeSpan.FromSeconds(0.5) && lines[0].Text == "first line", "LRC: [offset] moves the words sooner");
        check(lines.Count == 5 && lines[2].Text == "word timed", "LRC: word time stamps are removed");
        check(lines.Count == 5 && lines[3].Text == "" && lines[4].Text == "again and again" && lines[4].At == TimeSpan.FromSeconds(9.5), "LRC: repeats and pauses keep their times, in order");
        check(Lrc.IndexAt(lines, TimeSpan.FromSeconds(0.2)) == -1 && Lrc.IndexAt(lines, TimeSpan.FromSeconds(3.2)) == 1 && Lrc.IndexAt(lines, TimeSpan.FromMinutes(5)) == 4,
            "LRC: the line being sung at a given time");

        var list = System.Text.Json.JsonDocument.Parse("""
            [
              {"trackName":"Paper Boats","artistName":"Nova Lane","duration":231,"instrumental":false,"plainLyrics":"x","syncedLyrics":"[00:01.00]x"},
              {"trackName":"Paper Boats","artistName":"Nova Lane","duration":200,"instrumental":false,"plainLyrics":"y","syncedLyrics":null},
              {"trackName":"Paper Boats (Live)","artistName":"Nova Lane","duration":201,"instrumental":false,"plainLyrics":"z","syncedLyrics":"[00:01.00]z"},
              {"trackName":"Other Song","artistName":"Nova Lane","duration":200,"instrumental":false,"plainLyrics":"w","syncedLyrics":"[00:01.00]w"}
            ]
            """).RootElement;
        var pick = LyricsService.Pick(list, "Nova Lane", "Paper Boats", TimeSpan.FromSeconds(200));
        check(pick?.GetProperty("syncedLyrics").GetString() == "[00:01.00]z", "lyrics search keeps the synced record of the same length and title");
        check(LyricsService.Pick(list, "Nova Lane", "Paper Boats", TimeSpan.FromSeconds(320)) == null, "a record of another length is not used");
        check(LyricsService.CleanTitle("Paper Boats - Remastered 2011") == "Paper Boats" && LyricsService.CleanTitle("Paper Boats (feat. Someone)") == "Paper Boats"
            && LyricsService.CleanTitle("Paper Boats [Official Video]") == "Paper Boats", "titles lose remaster, feat. and video tags");
        var fa = LyricsService.Build("[00:01.00]این یک خط نمونه است\n[00:02.00]و این خط دوم است", null, false);
        check(fa is { Synced: true, Rtl: true } && fa.Lines.Count == 2, "Persian lyrics read right to left");
        var plain = LyricsService.Build(null, "\n\nline one\n\nline two\n\n", false);
        check(plain is { Synced: false } && plain.Lines.Count == 3, "plain lyrics keep verse gaps, not the blank edges");
        check(LyricsService.Build(null, null, true)?.Instrumental == true && LyricsService.Build("", "", false) == null, "instrumental and empty records");
    }

    // Uses its own value name, so the user's real "Start with Windows" choice is never touched.
    static void StartupTests(Action<bool, string> check)
    {
        const string name = "Island-selftest";
        try
        {
            Startup.SetFor(name, true);
            check(Startup.IsEnabledFor(name), "start with Windows: on");
            using (var a = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"))
                a.SetValue(name, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Microsoft.Win32.RegistryValueKind.Binary);
            check(!Startup.IsEnabledFor(name), "start with Windows: off when Task Manager disabled it");
            Startup.SetFor(name, true);
            check(Startup.IsEnabledFor(name), "start with Windows: turning it on again clears the Task Manager switch");
            Startup.SetFor(name, false);
            check(!Startup.IsEnabledFor(name), "start with Windows: off");
        }
        finally { Startup.SetFor(name, false); }
    }

    // Reports only what was found, never the words.
    static async Task LyricsOnline(Action<bool, string> check, StringBuilder log)
    {
        var svc = new LyricsService();
        var l = await svc.GetAsync("", "Amazing Grace", "", TimeSpan.Zero, default);
        log.AppendLine($"INFO  LRCLIB: {(l == null ? "nothing" : l.Instrumental ? "instrumental" : $"{l.Lines.Count} lines, synced={l.Synced}")}");
        check(l is { Lines.Count: > 0 }, "LRCLIB answers a search (a public-domain hymn)");
    }
}
