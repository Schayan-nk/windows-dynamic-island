using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Island;

/// <summary>
/// Lyrics under the player in the Music tab, from LRCLIB. The island grows by one band to show them:
/// the line being sung is bright, the last one fades above and the next one waits below. A one-shot
/// timer fires when the next line starts, so nothing polls while a line is sung.
/// </summary>
public partial class MainWindow
{
    const double LyricsBand = 100, LyricsBoxH = 94, LyricLineH = 30, DimLine = 0.36, SmallLine = 0.84;
    static readonly TimeSpan LyricLead = TimeSpan.FromMilliseconds(200);

    enum LyricsState { None, Loading, Found, NotFound, Instrumental, Offline }

    readonly LyricsService _lyricsService = new();
    readonly DispatcherTimer _lyricTimer = new(DispatcherPriority.Render);
    CancellationTokenSource? _lyricsCts;
    string? _lyricsKey;
    Lyrics? _lyrics;
    LyricsState _lyricsState;
    DateTime _lyricsRetryAt;
    int _lyricIndex = int.MinValue;

    /// <summary>The island is taller while the Music tab shows lyrics.</summary>
    bool LyricsShown => _tab == Tab.Music && _settings.Lyrics && _mi.HasSession && _lyrics != null;

    double ExpandedHeight => Expanded.H + (LyricsShown ? LyricsBand : 0);

    void InitLyrics() => _lyricTimer.Tick += (_, _) => ScheduleLyric(true);

    /// <summary>Called on every media update: looks up a new song, otherwise keeps the lyrics in step.</summary>
    void UpdateLyrics()
    {
        if (!_settings.Lyrics || !_mi.HasSession || string.IsNullOrWhiteSpace(_mi.Title))
        {
            if (_lyricsKey != null || _lyricsState != LyricsState.None)
            {
                _lyricsCts?.Cancel();
                _lyricsKey = null;
                SetLyrics(null, LyricsState.None);
            }
            return;
        }
        var key = LyricsService.Key(_mi.Artist, _mi.Title, _mi.Duration);
        bool retry = _lyricsState == LyricsState.Offline && DateTime.Now >= _lyricsRetryAt;
        if (key == _lyricsKey && !retry)
        {
            ScheduleLyric(true);
            return;
        }
        _lyricsKey = key;
        _lyricsCts?.Cancel();
        if (ShotsMode)
        {
            SetLyrics(null, LyricsState.None);
            return;
        }
        var cts = _lyricsCts = new CancellationTokenSource();
        SetLyrics(null, LyricsState.Loading);
        _ = LoadLyricsAsync(key, _mi.Artist, _mi.Title, _mi.Album, _mi.Duration, cts.Token);
    }

    async Task LoadLyricsAsync(string key, string artist, string title, string album, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var l = await _lyricsService.GetAsync(artist, title, album, duration, ct);
            if (ct.IsCancellationRequested || key != _lyricsKey) return;
            SetLyrics(l, l == null ? LyricsState.NotFound : l.Instrumental ? LyricsState.Instrumental : LyricsState.Found);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (key != _lyricsKey) return;
            Settings.Log(ex);
            _lyricsRetryAt = DateTime.Now.AddMinutes(1);
            SetLyrics(null, LyricsState.Offline);
        }
    }

    void SetLyrics(Lyrics? l, LyricsState state)
    {
        _lyrics = l is { Instrumental: false, Lines.Count: > 0 } ? l : null;
        _lyricsState = state;
        _lyricIndex = int.MinValue;
        BuildLyricLines();
        RefreshLyricsUi();
        SyncExpandedSize();
        ScheduleLyric(false);
    }

    void BuildLyricLines()
    {
        LyricsStack.Children.Clear();
        LyricsShift.BeginAnimation(TranslateTransform.YProperty, null);
        LyricsShift.Y = 0;
        if (_lyrics == null) return;
        var font = (FontFamily)FindResource("DisplayFont");
        foreach (var line in _lyrics.Lines)
        {
            bool rtl = line.Text.Length == 0 ? _lyrics.Rtl : Fmt.IsRtl(line.Text);
            LyricsStack.Children.Add(new TextBlock
            {
                Text = line.Text.Length == 0 ? "♪" : line.Text,
                FontFamily = font,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                LineHeight = LyricLineH,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                Opacity = _lyrics.Synced ? DimLine : 0.85,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(_lyrics.Synced ? SmallLine : 1, _lyrics.Synced ? SmallLine : 1),
            });
        }
    }

    bool? _lyricsBtnOn;

    void RefreshLyricsUi()
    {
        bool on = _settings.Lyrics;
        LyricsBtn.Visibility = Vis(_mi.HasSession);
        if (_lyricsBtnOn != on) // this runs every second while the tab is open: repaint the button only on change
        {
            _lyricsBtnOn = on;
            LyricsBtn.ToolTip = on ? "Hide lyrics" : "Show lyrics";
            if (on) { LyricsBtn.Background = Paint.Brush(0x2E, 255, 255, 255); LyricsBtn.Foreground = Brushes.White; }
            else { LyricsBtn.ClearValue(BackgroundProperty); LyricsBtn.ClearValue(ForegroundProperty); }
        }
        LyricsStatus.Text = !on || !_mi.HasSession ? "" : _lyricsState switch
        {
            LyricsState.Loading => "Finding lyrics…",
            LyricsState.NotFound => "No lyrics for this song",
            LyricsState.Instrumental => "Instrumental",
            LyricsState.Offline => "Lyrics are offline",
            LyricsState.Found when _lyrics is { Synced: false } => "Not synced: scroll to read",
            _ => "",
        };
        LyricsBox.Visibility = Vis(LyricsShown);
    }

    /// <summary>Grows or shrinks the open island when the lyrics band comes or goes.</summary>
    void SyncExpandedSize()
    {
        double h = ExpandedHeight;
        if (ExpandedContent.Height != h) ExpandedContent.Height = h;
        if (_mode != Mode.Expanded) return;
        FitWindow(false);
        AnimateTo(Expanded.W, h, Expanded.R);
    }

    /// <summary>
    /// While the island springs between its short and tall size, the tab bar rides its bottom edge
    /// (a transform, so no layout pass on each frame).
    /// </summary>
    void PlaceTabBar(double h)
    {
        double y = _mode == Mode.Expanded ? Math.Round(Math.Clamp(h, Expanded.H, Expanded.H + LyricsBand) - ExpandedContent.Height) : 0;
        if (TabBarShift.Y != y) TabBarShift.Y = y;
    }

    void ScheduleLyric(bool animate)
    {
        _lyricTimer.Stop();
        if (_lyrics == null || !LyricsShown || _mode != Mode.Expanded) return;
        if (!_lyrics.Synced)
        {
            if (_lyricIndex == int.MinValue) { _lyricIndex = 0; LyricsShift.Y = 0; }
            return;
        }
        var pos = _mi.LivePosition + LyricLead;
        int i = Lrc.IndexAt(_lyrics.Lines, pos);
        ShowLyric(i, animate && _lyricIndex != int.MinValue);
        if (!_mi.Playing || i + 1 >= _lyrics.Lines.Count) return;
        var wait = _lyrics.Lines[i + 1].At - pos;
        // wake for the next line; also every 10 s at most, in case the player's clock drifted
        _lyricTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(wait.TotalMilliseconds, 15, 10_000));
        _lyricTimer.Start();
    }

    void ShowLyric(int i, bool animate)
    {
        if (i == _lyricIndex) return;
        int old = _lyricIndex;
        _lyricIndex = i;
        Emphasize(old, false, animate);
        Emphasize(i, true, animate);
        // The sung line sits in the middle of the band; before the first line, it waits just below.
        double y = LyricsBoxH / 2 - (i * LyricLineH + LyricLineH / 2);
        if (animate)
            LyricsShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(y, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            LyricsShift.BeginAnimation(TranslateTransform.YProperty, null);
            LyricsShift.Y = y;
        }
    }

    void Emphasize(int i, bool on, bool animate)
    {
        if (i < 0 || i >= LyricsStack.Children.Count) return;
        var tb = (TextBlock)LyricsStack.Children[i];
        var st = (ScaleTransform)tb.RenderTransform;
        double o = on ? 1 : DimLine, s = on ? 1 : SmallLine;
        if (!animate)
        {
            tb.BeginAnimation(OpacityProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            tb.Opacity = o;
            st.ScaleX = st.ScaleY = s;
            return;
        }
        var d = TimeSpan.FromMilliseconds(360);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        tb.BeginAnimation(OpacityProperty, new DoubleAnimation(o, d) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(s, d) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(s, d) { EasingFunction = ease });
    }

    void LyricsBtn_Click(object sender, RoutedEventArgs e) => SetLyricsOn(!_settings.Lyrics);

    void SetLyricsOn(bool on)
    {
        _settings.Lyrics = on;
        _settings.Save();
        if (!on) _lyricsKey = null;
        UpdateLyrics();
        RefreshLyricsUi();
        SyncExpandedSize();
    }

    /// <summary>Lyrics without times cannot follow the song, so the wheel scrolls them instead.</summary>
    void LyricsBox_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_lyrics is not { Synced: false }) return;
        double min = Math.Min(0, LyricsBoxH - _lyrics.Lines.Count * LyricLineH);
        LyricsShift.BeginAnimation(TranslateTransform.YProperty, null);
        LyricsShift.Y = Math.Clamp(LyricsShift.Y + (e.Delta > 0 ? 2 : -2) * LyricLineH, min, 0);
        e.Handled = true;
    }

    /// <summary>For --shots: made-up lines that show the feature, not a real song.</summary>
    void ShowDemoLyrics()
    {
        var lines = new[]
        {
            (20.0, "Lyrics follow the song, line by line"),
            (24.0, "The line you hear is the bright one"),
            (28.0, "and the next one waits just below"),
            (32.0, "Pause the music and the words wait too"),
        }.Select(x => new LyricLine(TimeSpan.FromSeconds(x.Item1), x.Item2)).ToList();
        _lyricsKey = "demo";
        SetLyrics(new Lyrics { Lines = lines, Synced = true }, LyricsState.Found);
    }
}
