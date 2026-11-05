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

internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Island";

    public static bool IsEnabled
    {
        get
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(Name) is string; }
            catch { return false; }
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(Name, false);
        }
        catch (Exception ex) { Settings.Log(ex); }
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
