using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Island;

public sealed class Settings
{
    public bool HoverToOpen { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool PersianDate { get; set; }
    public bool Lyrics { get; set; } = true;
    public int FocusMinutes { get; set; } = 25;
    public string? LastFolder { get; set; }
    public List<string> Tray { get; set; } = new();
    public string FocusDate { get; set; } = "";
    public int FocusDoneToday { get; set; }

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "island.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\r\n\r\n");
        }
        catch { }
    }
}

/// <summary>"Start with Windows": the per-user Run entry, nothing that needs an administrator.</summary>
internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Task Manager and Settings > Apps > Startup record "Disabled" here, and Windows then skips the Run entry.
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string Name = "Island";

    public static bool IsEnabled => IsEnabledFor(Name);
    public static void Set(bool on) => SetFor(Name, on);

    /// <summary>On only when Windows will really start this copy: the entry points here and was not turned off in Task Manager.</summary>
    internal static bool IsEnabledFor(string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            if (k?.GetValue(name) is not string cmd || !PointsHere(cmd)) return false;
            using var a = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return a?.GetValue(name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
        }
        catch { return false; }
    }

    internal static void SetFor(string name, bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on)
            {
                k.SetValue(name, $"\"{Environment.ProcessPath}\"");
                // Clears a "Disabled" left by Task Manager; otherwise Windows would still skip the entry.
                using var a = Registry.CurrentUser.CreateSubKey(ApprovedKey);
                a.SetValue(name, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            }
            else
            {
                k.DeleteValue(name, false);
                using var a = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
                a?.DeleteValue(name, false);
            }
        }
        catch (Exception ex) { Settings.Log(ex); }
    }

    /// <summary>After Island.exe was moved, points an existing entry at the new place. A turned-off entry stays off.</summary>
    public static void Repair()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k?.GetValue(Name) is string cmd && !PointsHere(cmd)) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        }
        catch (Exception ex) { Settings.Log(ex); }
    }

    static bool PointsHere(string cmd)
    {
        var p = cmd.Trim();
        if (p.StartsWith('"'))
        {
            int end = p.IndexOf('"', 1);
            p = end > 0 ? p[1..end] : p.Trim('"');
        }
        return string.Equals(p, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class Fmt
{
    public static string Size(long b) =>
        b < 1024 ? $"{b} B" :
        b < 1024 * 1024 ? $"{b / 1024.0:0} kB" :
        b < 1024L * 1024 * 1024 ? $"{b / 1048576.0:0.0} MB" :
        $"{b / 1073741824.0:0.00} GB";

    public static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        return t.ToString("d MMM", CultureInfo.InvariantCulture);
    }

    public static bool IsRtl(string s)
    {
        foreach (var ch in s)
        {
            if ((ch >= 0x0590 && ch <= 0x08FF) || (ch >= 0xFB1D && ch <= 0xFEFF)) return true;
            if (char.IsLetter(ch)) return false;
        }
        return false;
    }

    static readonly string[] FaDays = { "یکشنبه", "دوشنبه", "سه‌شنبه", "چهارشنبه", "پنجشنبه", "جمعه", "شنبه" };
    static readonly string[] FaMonths = { "فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور", "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند" };

    public static string FaDigits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(ch >= '0' && ch <= '9' ? (char)('۰' + (ch - '0')) : ch);
        return sb.ToString();
    }

    public static (string date, string time) Now(bool persian)
    {
        var n = DateTime.Now;
        if (!persian) return (n.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture), n.ToString("HH:mm", CultureInfo.InvariantCulture));
        var pc = new PersianCalendar();
        string date = $"{FaDays[(int)n.DayOfWeek]} {pc.GetDayOfMonth(n)} {FaMonths[pc.GetMonth(n) - 1]} {pc.GetYear(n)}";
        return (FaDigits(date), FaDigits(n.ToString("HH:mm", CultureInfo.InvariantCulture)));
    }

    public static string OneLine(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        return t.Length > max ? t[..(max - 1)] + "…" : t;
    }
}
