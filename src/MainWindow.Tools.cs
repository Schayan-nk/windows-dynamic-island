using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Island;

public sealed class ClipItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string? Text { get; init; }
    public BitmapSource? Image { get; init; }
    public DateTime At { get; set; } = DateTime.Now;
    public string Preview => Image != null ? $"Image · {Image.PixelWidth} × {Image.PixelHeight}" : Fmt.OneLine(Text, 180);
    public string Meta => (Image != null ? "Image" : $"{Text?.Length ?? 0} characters") + " · " + Fmt.Ago(At);
    public Visibility ImageVisibility => Image != null ? Visibility.Visible : Visibility.Collapsed;
    public FlowDirection Flow => Text != null && Fmt.IsRtl(Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public void Touch() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Meta)));
}

public partial class MainWindow
{
    // ---------- focus timer ----------

    enum FocusState { Ready, Running, Paused, Done }
    FocusState _focus = FocusState.Ready;
    TimeSpan _focusTotal = TimeSpan.FromMinutes(25), _focusLeft = TimeSpan.FromMinutes(25);
    DateTime _focusEnd;
    bool _isBreak;

    bool FocusRunning => _focus == FocusState.Running;

    TimeSpan FocusRemaining
    {
        get
        {
            var t = _focus == FocusState.Running ? _focusEnd - DateTime.Now : _focusLeft;
            return t < TimeSpan.Zero ? TimeSpan.Zero : t;
        }
    }

    double FocusFraction => _focusTotal.TotalSeconds > 0 ? FocusRemaining.TotalSeconds / _focusTotal.TotalSeconds : 0;

    void InitFocus()
    {
        _focusTotal = _focusLeft = TimeSpan.FromMinutes(Math.Clamp(_settings.FocusMinutes, 1, 180));
        RollFocusDay();
        RefreshFocus();
    }

    void RollFocusDay()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (_settings.FocusDate == today) return;
        _settings.FocusDate = today;
        _settings.FocusDoneToday = 0;
    }

    void StartFocus()
    {
        if (_focus is FocusState.Ready or FocusState.Done) _focusLeft = _focusTotal;
        _focusEnd = DateTime.Now + _focusLeft;
        _focus = FocusState.Running;
        RefreshFocus();
        UpdateIndicators();
    }

    void PauseFocus()
    {
        _focusLeft = FocusRemaining;
        _focus = FocusState.Paused;
        RefreshFocus();
        UpdateIndicators();
    }

    void ResetFocus()
    {
        _focus = FocusState.Ready;
        _focusLeft = _focusTotal;
        RefreshFocus();
        UpdateIndicators();
    }

    void TickFocus()
    {
        if (_focus != FocusState.Running) return;
        if (DateTime.Now >= _focusEnd)
        {
            _focus = FocusState.Done;
            _focusLeft = TimeSpan.Zero;
            if (!_isBreak)
            {
                RollFocusDay();
                _settings.FocusDoneToday++;
                _settings.Save();
            }
            System.Media.SystemSounds.Asterisk.Play();
            FlashTile(TileFocusFlash);
            ShowPop(_isBreak ? "Break is over" : "Focus session done",
                _isBreak ? "Ready for the next one?" : "Nice work. Take a short break.",
                "\uE916", Purple, null, Tab.Timer, 8);
            UpdateIndicators();
        }
        RefreshFocusTile();
        if (_mode == Mode.Expanded && _tab == Tab.Timer) RefreshFocus();
    }

    void RefreshFocus()
    {
        RefreshFocusTile();
        var rem = FocusRemaining;
        TimerBig.Text = Fmt.Clock(rem);
        TimerSmall.Text = _isBreak ? "Break" : "Focus";
        TimerRing.Progress = _focus == FocusState.Ready ? 1 : FocusFraction;
        TimerRing.Fill = Paint.Brush(_isBreak ? C(0x5E, 0xD3, 0xA1) : C(0xB7, 0x84, 0xF2));
        TimerState.Text = _focus switch
        {
            FocusState.Running => _isBreak ? "On a break" : "In focus",
            FocusState.Paused => "Paused",
            FocusState.Done => _isBreak ? "Break is over" : "Done, nice work",
            _ => "Ready to start",
        };
        TimerStart.Content = _focus switch { FocusState.Running => "Pause", FocusState.Paused => "Resume", _ => "Start" };
        int n = _settings.FocusDoneToday;
        TimerToday.Text = n == 1 ? "1 session today" : $"{n} sessions today";
    }

    void RefreshFocusTile()
    {
        var rem = FocusRemaining;
        bool active = _focus is FocusState.Running or FocusState.Paused;
        FocusTileBig.Text = Fmt.Clock(_focus == FocusState.Ready ? _focusTotal : rem);
        FocusTileSub.Text = _focus switch
        {
            FocusState.Running => $"{Math.Max(1, (int)Math.Ceiling(rem.TotalMinutes))} min left",
            FocusState.Paused => "Paused",
            FocusState.Done => _isBreak ? "Break is over" : "Done, nice work",
            _ => "Ready to start",
        };
        FocusTileRing.Visibility = Vis(active);
        FocusTileRing.Progress = FocusFraction;
        FocusTileBtn.Background = active ? Brushes.Transparent : Brushes.White;
        FocusTileGlyph.Foreground = active ? Brushes.White : Paint.Brush(C(0x8E, 0x4B, 0xD6));
        FocusTileGlyph.Text = _focus == FocusState.Running ? "\uE769" : "\uE768";
    }

    void ToggleFocus()
    {
        if (_focus == FocusState.Running) PauseFocus();
        else StartFocus();
    }

    void FocusTileBtn_Click(object sender, RoutedEventArgs e) { ToggleFocus(); e.Handled = true; }
    void TileFocus_Click(object sender, MouseButtonEventArgs e) => SelectTab(Tab.Timer);
    void TimerStart_Click(object sender, RoutedEventArgs e) => ToggleFocus();
    void TimerReset_Click(object sender, RoutedEventArgs e) => ResetFocus();

    void Preset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string s || !int.TryParse(s, out int minutes)) return;
        _isBreak = minutes < 0;
        _focusTotal = TimeSpan.FromMinutes(Math.Abs(minutes));
        if (!_isBreak) { _settings.FocusMinutes = minutes; _settings.Save(); }
        _focusLeft = _focusTotal;
        if (_focus == FocusState.Running) _focusEnd = DateTime.Now + _focusTotal;
        else _focus = FocusState.Ready;
        RefreshFocus();
        UpdateIndicators();
    }

    // ---------- clipboard ----------

    readonly ObservableCollection<ClipItem> _clips = new();
    DateTime _ignoreClipUntil;

    void OnClipboardChanged()
    {
        if (DateTime.Now < _ignoreClipUntil) return;
        Dispatcher.InvokeAsync(ReadClipboard, DispatcherPriority.Background);
    }

    void ReadClipboard()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return;
                if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore")) return;
                if (data.GetDataPresent("CanIncludeInClipboardHistory") && data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
                {
                    var b = new byte[4];
                    ms.Read(b, 0, 4);
                    if (BitConverter.ToInt32(b, 0) == 0) return;
                }
                if (data.GetDataPresent(DataFormats.UnicodeText))
                {
                    if (data.GetData(DataFormats.UnicodeText) is string t && !string.IsNullOrWhiteSpace(t)) AddClip(new ClipItem { Text = t });
                }
                else if (data.GetDataPresent(DataFormats.Bitmap))
                {
                    var img = Clipboard.GetImage();
                    if (img != null)
                    {
                        img.Freeze();
                        AddClip(new ClipItem { Image = img });
                    }
                }
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                Settings.Log(ex);
                return;
            }
        }
    }

    void AddClip(ClipItem c)
    {
        if (c.Text != null)
        {
            var dup = _clips.FirstOrDefault(x => x.Text == c.Text);
            if (dup != null) _clips.Remove(dup);
        }
        _clips.Insert(0, c);
        while (_clips.Count > 30) _clips.RemoveAt(_clips.Count - 1);
        int images = 0;
        for (int i = 0; i < _clips.Count; i++)
            if (_clips[i].Image != null && ++images > 5) { _clips.RemoveAt(i); i--; }
        if (_mode == Mode.Expanded && _tab == Tab.Clip) RefreshClips();
    }

    void RefreshClips()
    {
        ClipEmpty.Visibility = Vis(_clips.Count == 0);
        foreach (var c in _clips) c.Touch();
    }

    void ClipItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ClipItem c) return;
        try
        {
            _ignoreClipUntil = DateTime.Now.AddMilliseconds(700);
            if (c.Image != null) Clipboard.SetImage(c.Image);
            else Clipboard.SetText(c.Text ?? "");
            c.At = DateTime.Now;
            int i = _clips.IndexOf(c);
            if (i > 0) _clips.Move(i, 0);
            c.Touch();
            FlashNote("Copied");
        }
        catch { FlashNote("The clipboard is busy, try again"); }
    }

    void ClipRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ClipItem c) _clips.Remove(c);
        RefreshClips();
        UpdateHeader();
        e.Handled = true;
    }

    // ---------- notes ----------

    readonly DispatcherTimer _notesSave = new() { Interval = TimeSpan.FromMilliseconds(800) };
    bool _notesLoading;
    static string NotesPath => Path.Combine(Settings.Dir, "notes.txt");

    void LoadNotes()
    {
        _notesSave.Tick += (_, _) => { _notesSave.Stop(); SaveNotesNow(); FlashNote("Saved"); };
        _notesLoading = true;
        try { NotesBox.Text = File.Exists(NotesPath) ? File.ReadAllText(NotesPath) : ""; }
        catch { }
        finally { _notesLoading = false; }
        NotesBox.FlowDirection = Fmt.IsRtl(NotesBox.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    void NotesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_notesLoading) return;
        _notesSave.Stop();
        _notesSave.Start();
        NotesBox.FlowDirection = Fmt.IsRtl(NotesBox.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    void SaveNotesNow()
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.WriteAllText(NotesPath, NotesBox.Text);
        }
        catch (Exception ex) { Settings.Log(ex); }
    }

    // ---------- ask Claude ----------

    string? _chatFolder;

    void RefreshChat()
    {
        _chatFolder ??= _settings.LastFolder ?? _cu?.Folders.FirstOrDefault() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ChatFolderText.Text = FolderLabel(_chatFolder);
        ChatFolderBtn.ToolTip = _chatFolder;
        ChatPlaceholder.Visibility = Vis(string.IsNullOrEmpty(ChatBox.Text));
    }

    static string FolderLabel(string p)
    {
        var n = Path.GetFileName(p.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(n) ? p : n;
    }

    void ChatFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu { PlacementTarget = ChatFolderBtn, Placement = PlacementMode.Bottom };
        var folders = (_cu?.Folders ?? new()).ToList();
        if (_settings.LastFolder != null && !folders.Contains(_settings.LastFolder, StringComparer.OrdinalIgnoreCase)) folders.Insert(0, _settings.LastFolder);
        foreach (var f in folders.Take(8))
        {
            var mi = new MenuItem { Header = $"{FolderLabel(f)}    {f}", IsChecked = string.Equals(f, _chatFolder, StringComparison.OrdinalIgnoreCase) };
            mi.Click += (_, _) => { _chatFolder = f; RefreshChat(); };
            m.Items.Add(mi);
        }
        if (folders.Count > 0) m.Items.Add(new Separator());
        m.Items.Add(Item("Choose another folder…", BrowseFolder));
        OpenMenu(m);
    }

    void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should Claude work?" };
        if (_chatFolder != null && Directory.Exists(_chatFolder)) dlg.InitialDirectory = _chatFolder;
        _dialogOpen = true;
        try
        {
            if (dlg.ShowDialog(this) == true)
            {
                _chatFolder = dlg.FolderName;
                RefreshChat();
            }
        }
        finally { _dialogOpen = false; }
    }

    void ChatBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            RunClaude();
            e.Handled = true;
        }
    }

    void ChatBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ChatPlaceholder.Visibility = Vis(string.IsNullOrEmpty(ChatBox.Text));
        ChatBox.FlowDirection = Fmt.IsRtl(ChatBox.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    void ChatRun_Click(object sender, RoutedEventArgs e) => RunClaude();

    void RunClaude()
    {
        var folder = _chatFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { FlashNote("Pick a folder first"); return; }
        var exe = FindClaude();
        if (exe == null)
        {
            ShowPop("Claude Code not found", "Install it, or check that 'claude' works in a terminal.", "\uE7BA", C(0xE2, 0x88, 0x5F), null, Tab.Chat, 6);
            return;
        }
        // One line, no double quotes: the prompt passes through PowerShell and, for npm installs, cmd.
        var prompt = Regex.Replace(ChatBox.Text.Trim(), @"\s*\r?\n\s*", " ").Replace('"', '\'');
        try
        {
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, WorkingDirectory = folder };
            psi.ArgumentList.Add("-NoExit");
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-Command");
            var quoted = "'" + exe.Replace("'", "''") + "'";
            psi.ArgumentList.Add(prompt.Length > 0 ? $"& {quoted} $env:ISLAND_PROMPT" : $"& {quoted}");
            if (prompt.Length > 0) psi.Environment["ISLAND_PROMPT"] = prompt;
            Process.Start(psi);
            _settings.LastFolder = folder;
            _settings.Save();
            ChatBox.Clear();
            Collapse();
        }
        catch (Exception ex)
        {
            Settings.Log(ex);
            FlashNote("Could not start Claude");
        }
    }

    static string? FindClaude()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                try
                {
                    var p = Path.Combine(dir.Trim().Trim('"'), name);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var p in new[]
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"),
        })
            if (File.Exists(p)) return p;
        return null;
    }
}
