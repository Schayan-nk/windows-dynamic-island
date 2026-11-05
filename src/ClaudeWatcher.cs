using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Island;

public enum ClaudeStage { None = 0, Read = 1, Edit = 2, Test = 3, Done = 4 }

public sealed class ClaudeSnapshot
{
    public string SessionId = "";
    public string Project = "";
    public string Cwd = "";
    public string? Title;
    public bool Working;
    public DateTime TurnStart;
    public DateTime? TurnEnd;
    public ClaudeStage Stage;
    public string Activity = "";
    public string? File;
    public string? FileVerb;
    public string? DiffOld;
    public string? DiffNew;
    public string? Command;
    public string? Summary;
    public string? Prompt;
    public double Context;
    public DateTime LastEvent;
    public DateTime? FinishedAt;
}

public sealed class ClaudeUpdate
{
    public ClaudeSnapshot? Primary;
    public List<ClaudeSnapshot> Active = new();
    public int JobsToday;
    public List<string> Folders = new();
    public ClaudeSnapshot? Finished;
}

/// <summary>
/// Follows Claude Code's own transcripts (~/.claude/projects/*/*.jsonl). Only the newest bytes are read:
/// the files can be hundreds of MB. Nothing is written into Claude Code's folders.
/// </summary>
public sealed class ClaudeWatcher : IDisposable
{
    const int TailBytes = 768 * 1024;
    const long MaxStep = 48L * 1024 * 1024;
    static readonly TimeSpan TrackWindow = TimeSpan.FromHours(12);
    static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(20);
    static readonly Regex TestRx = new(
        @"\b(test|tests|pytest|jest|vitest|mocha|phpunit|rspec|go\s+(test|vet|build)|cargo\s+(test|build|check|clippy)|dotnet\s+(test|build)|(npm|pnpm|yarn|bun)\s+(run\s+)?(test|build|lint|check|typecheck)|tsc|eslint|ruff|mypy|make)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly string _root;
    readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _jobs = new();
    readonly string _jobsFile;
    string _jobsDate = "";
    bool _jobsDirty;
    Timer? _timer;
    int _busy;

    public event Action<ClaudeUpdate>? Updated;

    sealed class Session
    {
        public Session(string path) { Path = path; Id = System.IO.Path.GetFileNameWithoutExtension(path); }
        public readonly string Path, Id;
        public long Offset;
        public byte[] Pending = Array.Empty<byte>();
        public string Cwd = "";
        public string? Title;
        public bool Working;
        public DateTime TurnStart, LastEvent, LastWrite;
        public DateTime? TurnEnd, FinishedAt;
        public ClaudeStage Stage;
        public string Activity = "Idle";
        public string? File, FileVerb, DiffOld, DiffNew, Command, Summary, Prompt, LastText;
        public long Ctx;
        public long Window = 200_000;
    }

    public ClaudeWatcher(string? root = null, string? jobsFile = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        _jobsFile = jobsFile ?? Path.Combine(Settings.Dir, "jobs.json");
    }

    public void Start()
    {
        LoadJobs();
        _timer = new Timer(_ => Poll(), null, 400, 1000);
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>One pass, for tests.</summary>
    internal void PollOnce() { LoadJobs(); Poll(); }

    void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            RollDay();
            var finished = new List<Session>();
            if (Directory.Exists(_root))
            {
                var cutoff = DateTime.UtcNow - TrackWindow;
                foreach (var dir in SafeDirs(_root))
                {
                    foreach (var fi in SafeFiles(dir))
                    {
                        if (!_sessions.TryGetValue(fi.FullName, out var s))
                        {
                            if (fi.LastWriteTimeUtc < cutoff) continue;
                            s = new Session(fi.FullName);
                            _sessions[fi.FullName] = s;
                            Pump(s, fi, true, finished);
                        }
                        else if (fi.Length != s.Offset)
                        {
                            Pump(s, fi, false, finished);
                        }
                    }
                }
            }
            if (_jobsDirty) SaveJobs();
            Updated?.Invoke(Build(finished.LastOrDefault()));
        }
        catch (Exception ex) { Settings.Log(ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.GetDirectories(root); } catch { return Array.Empty<string>(); }
    }

    static IEnumerable<FileInfo> SafeFiles(string dir)
    {
        try { return new DirectoryInfo(dir).GetFiles("*.jsonl", SearchOption.TopDirectoryOnly); } catch { return Array.Empty<FileInfo>(); }
    }

    void Pump(Session s, FileInfo fi, bool initial, List<Session> finished)
    {
        long len = fi.Length;
        s.LastWrite = fi.LastWriteTime;
        if (len < s.Offset) { s.Offset = 0; s.Pending = Array.Empty<byte>(); }
        long start = s.Offset;
        bool skipFirst = false;
        if ((initial && len > TailBytes) || (!initial && len - start > MaxStep))
        {
            start = Math.Max(0, len - TailBytes);
            skipFirst = start > 0;
            s.Pending = Array.Empty<byte>();
        }
        int want = (int)(len - start);
        if (want <= 0) { s.Offset = len; return; }

        byte[] buf;
        int pre = s.Pending.Length, read = 0;
        try
        {
            using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(start, SeekOrigin.Begin);
            buf = new byte[pre + want];
            if (pre > 0) Buffer.BlockCopy(s.Pending, 0, buf, 0, pre);
            while (read < want)
            {
                int n = fs.Read(buf, pre + read, want - read);
                if (n <= 0) break;
                read += n;
            }
        }
        catch { return; }

        s.Offset = start + read;
        int total = pre + read, lineStart = 0;
        bool first = skipFirst;
        for (int i = 0; i < total; i++)
        {
            if (buf[i] != (byte)'\n') continue;
            int l = i - lineStart;
            if (l > 0 && buf[i - 1] == (byte)'\r') l--;
            if (first) first = false;
            else if (l > 1) ParseLine(s, new ReadOnlyMemory<byte>(buf, lineStart, l), !initial, finished);
            lineStart = i + 1;
        }
        s.Pending = !first && lineStart < total ? buf[lineStart..total] : Array.Empty<byte>();
    }

    void ParseLine(Session s, ReadOnlyMemory<byte> line, bool live, List<Session> finished)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (Bool(root, "isSidechain")) return;
            var cwd = Str(root, "cwd");
            if (!string.IsNullOrEmpty(cwd)) s.Cwd = cwd;
            var ts = Time(root) ?? DateTime.Now;
            switch (Str(root, "type"))
            {
                case "ai-title":
                    s.Title = Str(root, "aiTitle") ?? s.Title;
                    break;
                case "summary":
                    s.Title ??= Str(root, "summary");
                    break;
                case "user":
                    OnUser(s, root, ts);
                    break;
                case "assistant":
                    OnAssistant(s, root, ts, live, finished);
                    break;
                case "system":
                    if (Str(root, "subtype") == "turn_duration" && s.Working) EndTurn(s, ts, live, finished, null);
                    break;
            }
        }
    }

    void OnUser(Session s, JsonElement root, DateTime ts)
    {
        if (Bool(root, "isMeta")) return;
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return;
        if (!msg.TryGetProperty("content", out var content)) return;
        string? text = null;
        if (content.ValueKind == JsonValueKind.String) text = content.GetString();
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in content.EnumerateArray())
            {
                var bt = Str(b, "type");
                if (bt == "tool_result") { s.LastEvent = ts; return; }
                if (bt == "text" && text == null) text = Str(b, "text");
                else if (bt == "image" && text == null) text = "[image]";
            }
        }
        if (text == null) return;
        var t = text.TrimStart();
        if (t.StartsWith("[Request interrupted", StringComparison.Ordinal))
        {
            s.Working = false;
            s.Activity = "Stopped";
            s.LastEvent = ts;
            return;
        }
        if (t.StartsWith("<local-command", StringComparison.Ordinal)) return;
        if (s.Working) { s.LastEvent = ts; return; } // a message queued while Claude was busy
        s.Working = true;
        s.TurnStart = ts;
        s.TurnEnd = null;
        s.Stage = ClaudeStage.None;
        s.Activity = "Thinking";
        s.File = s.FileVerb = s.DiffOld = s.DiffNew = s.Command = s.Summary = s.LastText = null;
        s.Prompt = Fmt.OneLine(StripTags(t), 140);
        s.LastEvent = ts;
    }

    void OnAssistant(Session s, JsonElement root, DateTime ts, bool live, List<Session> finished)
    {
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return;
        if (msg.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            long ctx = Long(u, "input_tokens") + Long(u, "cache_read_input_tokens") + Long(u, "cache_creation_input_tokens");
            if (ctx > 0)
            {
                s.Ctx = ctx;
                if (ctx > 200_000) s.Window = 1_000_000;
            }
        }
        var stop = Str(msg, "stop_reason");
        bool ends = stop is "end_turn" or "stop_sequence";
        if (!s.Working && !ends && s.Stage != ClaudeStage.Done)
        {
            // We started reading in the middle of a turn.
            s.Working = true;
            if (s.TurnStart == default) s.TurnStart = ts;
            s.Activity = "Thinking";
        }
        s.LastEvent = ts;
        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in content.EnumerateArray())
            {
                switch (Str(b, "type"))
                {
                    case "tool_use":
                        OnTool(s, Str(b, "name") ?? "", b.TryGetProperty("input", out var inp) ? inp : default);
                        break;
                    case "text":
                        var tx = Str(b, "text");
                        if (!string.IsNullOrWhiteSpace(tx)) s.LastText = tx;
                        break;
                    case "thinking":
                        if (s.Working && s.Stage == ClaudeStage.None) s.Activity = "Thinking";
                        break;
                }
            }
        }
        if (ends) EndTurn(s, ts, live, finished, Str(msg, "id"));
    }

    void OnTool(Session s, string name, JsonElement input)
    {
        void Bump(ClaudeStage st) { if (st > s.Stage) s.Stage = st; }
        switch (name)
        {
            case "Read":
            case "NotebookRead":
                Bump(ClaudeStage.Read);
                s.Activity = "Reading code";
                s.File = Str(input, "file_path") ?? Str(input, "notebook_path") ?? s.File;
                s.FileVerb = "being read";
                s.DiffOld = s.DiffNew = null;
                s.Command = null;
                break;
            case "Grep":
            case "Glob":
            case "LS":
                Bump(ClaudeStage.Read);
                s.Activity = "Searching the code";
                s.Command = Str(input, "pattern");
                break;
            case "WebFetch":
            case "WebSearch":
                s.Activity = "Looking things up";
                break;
            case "Edit":
                Bump(ClaudeStage.Edit);
                s.Activity = "Making changes";
                s.File = Str(input, "file_path") ?? s.File;
                s.FileVerb = "being edited";
                SetDiff(s, Str(input, "old_string"), Str(input, "new_string"));
                break;
            case "MultiEdit":
                Bump(ClaudeStage.Edit);
                s.Activity = "Making changes";
                s.File = Str(input, "file_path") ?? s.File;
                s.FileVerb = "being edited";
                if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array && edits.GetArrayLength() > 0)
                    SetDiff(s, Str(edits[0], "old_string"), Str(edits[0], "new_string"));
                break;
            case "Write":
                Bump(ClaudeStage.Edit);
                s.Activity = "Writing a file";
                s.File = Str(input, "file_path") ?? s.File;
                s.FileVerb = "being written";
                SetDiff(s, null, Str(input, "content"));
                break;
            case "NotebookEdit":
                Bump(ClaudeStage.Edit);
                s.Activity = "Editing a notebook";
                s.File = Str(input, "notebook_path") ?? s.File;
                s.FileVerb = "being edited";
                SetDiff(s, null, Str(input, "new_source"));
                break;
            case "Bash":
            case "PowerShell":
                var cmd = Str(input, "command") ?? "";
                s.Command = Fmt.OneLine(cmd, 90);
                if (TestRx.IsMatch(cmd)) { Bump(ClaudeStage.Test); s.Activity = "Running tests"; }
                else s.Activity = "Running a command";
                break;
            case "Task":
            case "Agent":
                s.Activity = "Working with helpers";
                break;
            case "TodoWrite":
            case "TaskCreate":
            case "TaskUpdate":
                s.Activity = "Planning";
                break;
            default:
                var n = name.StartsWith("mcp__", StringComparison.Ordinal) ? name.Split("__").Last() : name;
                s.Activity = "Using " + n.Replace('_', ' ');
                break;
        }
    }

    static void SetDiff(Session s, string? oldText, string? newText)
    {
        s.DiffOld = FirstLine(oldText);
        s.DiffNew = FirstLine(newText);
        s.Command = null;
    }

    static string? FirstLine(string? t)
    {
        if (string.IsNullOrEmpty(t)) return null;
        foreach (var line in t.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length > 0) return l.Length > 70 ? l[..69] + "…" : l;
        }
        return null;
    }

    void EndTurn(Session s, DateTime ts, bool live, List<Session> finished, string? msgId)
    {
        bool was = s.Working;
        s.Working = false;
        s.Stage = ClaudeStage.Done;
        s.Activity = "Done";
        s.TurnEnd = ts;
        if (s.LastText != null) s.Summary = CleanText(s.LastText);
        if (msgId != null && ts.Date == DateTime.Today && _jobs.Add(msgId)) _jobsDirty = true;
        if (was && live)
        {
            s.FinishedAt = DateTime.Now;
            if (!finished.Contains(s)) finished.Add(s);
        }
    }

    ClaudeUpdate Build(Session? fin)
    {
        var now = DateTime.Now;
        var snaps = _sessions.Values.Where(x => x.LastEvent != default).Select(x => Snap(x, now)).ToList();
        var active = snaps.Where(x => x.Working).OrderByDescending(x => x.LastEvent).ToList();
        var primary = active.FirstOrDefault()
            ?? snaps.Where(x => (now - x.LastEvent) < TrackWindow).OrderByDescending(x => x.LastEvent).FirstOrDefault();
        return new ClaudeUpdate
        {
            Primary = primary,
            Active = active,
            JobsToday = _jobs.Count,
            Folders = snaps.OrderByDescending(x => x.LastEvent).Select(x => x.Cwd)
                .Where(x => !string.IsNullOrEmpty(x) && Directory.Exists(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList(),
            Finished = fin == null ? null : Snap(fin, now),
        };
    }

    static ClaudeSnapshot Snap(Session s, DateTime now) => new()
    {
        SessionId = s.Id,
        Cwd = s.Cwd,
        Project = ProjectName(s),
        Title = s.Title,
        Working = s.Working && now - s.LastWrite < StaleAfter,
        TurnStart = s.TurnStart,
        TurnEnd = s.TurnEnd,
        Stage = s.Stage,
        Activity = s.Working && now - s.LastWrite >= StaleAfter ? "Idle" : s.Activity,
        File = s.File,
        FileVerb = s.FileVerb,
        DiffOld = s.DiffOld,
        DiffNew = s.DiffNew,
        Command = s.Command,
        Summary = s.Summary,
        Prompt = s.Prompt,
        Context = s.Window > 0 ? Math.Clamp((double)s.Ctx / s.Window, 0, 1) : 0,
        LastEvent = s.LastEvent,
        FinishedAt = s.FinishedAt,
    };

    static string ProjectName(Session s)
    {
        if (!string.IsNullOrEmpty(s.Cwd))
        {
            var n = Path.GetFileName(s.Cwd.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(n)) return n;
        }
        var d = Path.GetFileName(Path.GetDirectoryName(s.Path) ?? "");
        return string.IsNullOrEmpty(d) ? "Claude Code" : d.Split('-').Last();
    }

    static string StripTags(string t) => Regex.Replace(t, "<[^>]{1,80}>", " ");

    static string CleanText(string t)
    {
        var x = Regex.Replace(t, @"```[\s\S]*?```", " ");
        x = Regex.Replace(x, @"\[([^\]]+)\]\([^)]+\)", "$1");
        x = Regex.Replace(x, @"[*_`#>|]+", "");
        x = Regex.Replace(x, @"^\s*[-•]\s+", "", RegexOptions.Multiline);
        return Fmt.OneLine(x, 220);
    }

    void RollDay()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (_jobsDate == today) return;
        _jobsDate = today;
        _jobs.Clear();
        _jobsDirty = true;
    }

    void LoadJobs()
    {
        try
        {
            if (!System.IO.File.Exists(_jobsFile)) return;
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(_jobsFile));
            var date = Str(doc.RootElement, "date");
            if (date != DateTime.Today.ToString("yyyy-MM-dd")) return;
            _jobsDate = date!;
            if (doc.RootElement.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                foreach (var id in ids.EnumerateArray()) if (id.GetString() is string v) _jobs.Add(v);
        }
        catch { }
    }

    void SaveJobs()
    {
        _jobsDirty = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_jobsFile)!);
            System.IO.File.WriteAllText(_jobsFile, JsonSerializer.Serialize(new { date = _jobsDate, ids = _jobs.ToArray() }));
        }
        catch { }
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    static DateTime? Time(JsonElement e)
    {
        var s = Str(e, "timestamp");
        if (s != null && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)) return d.ToLocalTime();
        return null;
    }
}
