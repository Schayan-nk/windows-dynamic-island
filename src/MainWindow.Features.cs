using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Island;

public enum Tab { Home, Claude, Music, Chat, Timer, Tray, Clip, Notes }

public partial class MainWindow
{
    static readonly Color Black = Color.FromRgb(5, 5, 6);
    static readonly Color Orange = Color.FromRgb(0xFF, 0x8A, 0x4C);
    static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    static readonly Color Amber = Color.FromRgb(0xFF, 0xC8, 0x3D);
    static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
    static readonly Color Purple = Color.FromRgb(0x9D, 0x5B, 0xD2);

    Tab _tab = Tab.Home;
    readonly ClaudeWatcher _claude = new();
    readonly MediaService _media = new();
    readonly AudioService _audio = new();
    TrayStore _tray = null!;
    ClaudeUpdate? _cu;
    MediaInfo _mi = new();
    double _phase, _peak;
    DateTime _nextBlink = DateTime.Now.AddSeconds(4), _nextGlance = DateTime.Now.AddSeconds(14), _noteUntil;
    readonly Random _rng = new();
    bool _glowPulsing;
    double _glowTarget = -1;
    Color _glowColor = Black, _popGlow = Black;
    string? _popSession;
    Dictionary<Tab, (Button Btn, TextBlock Label, Color Bg, Color Fg)> _tabs = null!;

    static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    static Visibility Vis(bool v) => v ? Visibility.Visible : Visibility.Collapsed;

    static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    void InitFeatures()
    {
        _tabs = new()
        {
            [Tab.Home] = (TabHome, LblHome, C(0x2C, 0x2C, 0x31), Colors.White),
            [Tab.Claude] = (TabClaude, LblClaude, Color.FromArgb(0x40, 0xE2, 0x88, 0x5F), C(0xF4, 0xA6, 0x82)),
            [Tab.Music] = (TabMusic, LblMusic, Color.FromArgb(0x40, 0xFF, 0x5A, 0x6E), C(0xFF, 0x93, 0xA2)),
            [Tab.Chat] = (TabChat, LblChat, Color.FromArgb(0x40, 0xE2, 0x88, 0x5F), C(0xF4, 0xA6, 0x82)),
            [Tab.Timer] = (TabTimer, LblTimer, Color.FromArgb(0x40, 0xB7, 0x84, 0xF2), C(0xD3, 0xB3, 0xFF)),
            [Tab.Tray] = (TabTray, LblTray, Color.FromArgb(0x45, 0xFF, 0xC8, 0x3D), C(0xFF, 0xD7, 0x6A)),
            [Tab.Clip] = (TabClip, LblClip, Color.FromArgb(0x40, 0x64, 0xB5, 0xF6), C(0x9C, 0xCF, 0xFF)),
            [Tab.Notes] = (TabNotes, LblNotes, Color.FromArgb(0x40, 0xFF, 0xD5, 0x4F), C(0xFF, 0xE0, 0x8A)),
        };

        _tray = new TrayStore(Dispatcher);
        TrayList.ItemsSource = _tray.Items;
        _tray.Changed += OnTrayChanged;
        foreach (var p in _settings.Tray) _tray.Add(p);
        ClipList.ItemsSource = _clips;
        LoadNotes();
        InitFocus();
        HookTiles();
        _musicArtDefault = MusicArt.Background;

        _claude.Updated += u => Dispatcher.InvokeAsync(() => OnClaude(u));
        _claude.Start();
        InitLyrics();
        _media.Changed += m => Dispatcher.InvokeAsync(() => OnMedia(m));
        if (!ShotsMode) _ = _media.StartAsync();

        SelectTab(Tab.Home);
        UpdateHeaderClock();
        UpdateIndicators();
        _clock.Start();
        ShowPop("Island is ready", "Touch the top of the screen, or press Ctrl+Alt+T.", "\uE80F", C(0x3A, 0x3A, 0x40), null, Tab.Home, 4.5);
    }

    void StopFeatures()
    {
        _claude.Dispose();
        _media.Dispose();
        _share?.Stop(false);
        SaveNotesNow();
        _settings.Save();
    }

    void OnClock()
    {
        UpdateHeaderClock();
        TickFocus();
        if (_mode is Mode.Pop or Mode.Progress && DateTime.Now > _popUntil && !_hoverInside) SetMode(RestingMode());
        if (_mode == Mode.Expanded) RefreshExpanded();
        if (HeaderNote.Visibility == Visibility.Visible && DateTime.Now > _noteUntil) HeaderNote.Visibility = Visibility.Collapsed;
        UpdateIndicators();
        Blink();
        if (++_tickCount % 2 == 0) CheckFullscreen();
        if (_tickCount % 5 == 0 && !_hidden)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        TickShare();
    }

    int _tickCount;

    void OnFast()
    {
        if (!MusicPlaying) { _fast.Stop(); return; }
        double p = _audio.Peak;
        _peak = Math.Max(p, _peak * 0.8);
        _phase += 0.36;
        double B(int i) => Math.Clamp(0.16 + _peak * (0.8 + 0.5 * Math.Sin(_phase * (1.25 + i * 0.41) + i * 1.9)), 0.14, 1);
        if (LiveBars.IsVisible) LiveBars.SetLevels(B(0), B(1), B(2), B(3));
        if (HeaderBars.IsVisible) HeaderBars.SetLevels(B(2), B(0), B(3), B(1));
        if (TileBars.IsVisible) TileBars.SetLevels(B(3), B(1), B(2), B(0));
    }

    void UpdateFastTimer()
    {
        bool need = MusicPlaying && _mode is Mode.Live or Mode.Expanded;
        if (need && !_fast.IsEnabled) _fast.Start();
        else if (!need && _fast.IsEnabled) _fast.Stop();
    }

    // ---------- state ----------

    bool ClaudeWorking => _cu?.Primary?.Working == true;
    bool ClaudeJustDone => _cu?.Primary is { Working: false, FinishedAt: DateTime f } && (DateTime.Now - f).TotalSeconds < 25;
    double DoneFade => _cu?.Primary?.FinishedAt is DateTime f ? Math.Clamp(1 - (DateTime.Now - f).TotalSeconds / 25.0, 0, 1) : 0;
    bool MusicPlaying => _mi.Playing;
    bool HasLiveActivity() => ClaudeWorking || FocusRunning || MusicPlaying;

    void UpdateIndicators()
    {
        (EyesMode m, Color c) eyes =
            ClaudeWorking ? (EyesMode.Dots, C(0xFF, 0x9A, 0x62)) :
            ClaudeJustDone ? (EyesMode.Happy, C(0x63, 0xE6, 0x8F)) :
            FocusRunning ? (EyesMode.Dots, C(0xC9, 0xA4, 0xFF)) :
            MusicPlaying ? (EyesMode.Hearts, C(0xFF, 0x5C, 0x7A)) :
            _share?.Connected == true ? (EyesMode.Dots, C(0x5A, 0xAE, 0xFF)) :
            (EyesMode.Happy, C(0xDC, 0xF2, 0xE2));
        SetEyes(eyes.m, eyes.c);

        bool claude = ClaudeWorking, focus = !claude && FocusRunning, music = !claude && !focus && MusicPlaying;
        LiveSpark.Visibility = Vis(claude);
        LiveRing.Visibility = Vis(focus);
        LiveArt.Visibility = Vis(music);
        LiveBars.Visibility = Vis(music);
        LiveText.Visibility = Vis(claude || focus);
        if (claude && _cu?.Primary is { } p)
        {
            LiveText.Text = Fmt.Clock(DateTime.Now - p.TurnStart);
            LiveText.Foreground = Paint.Brush(C(0xFF, 0xAE, 0x82));
        }
        else if (focus)
        {
            LiveText.Text = Fmt.Clock(FocusRemaining);
            LiveText.Foreground = Paint.Brush(C(0xDC, 0xC2, 0xFF));
            LiveRing.Progress = FocusFraction;
        }
        if (music)
            LiveArt.Background = _mi.Art != null ? new ImageBrush(_mi.Art) { Stretch = Stretch.UniformToFill } : Paint.Brush(0x40, 255, 255, 255);
        UpdateIdleCompact(claude || focus || music);
        UpdateTileGlow();

        if (_mode is Mode.Collapsed or Mode.Live)
        {
            var want = RestingMode();
            if (want != _mode) SetMode(want);
        }
        UpdateGlow();
        UpdateFastTimer();
        SyncLoops();
        if (_mode == Mode.Expanded) UpdateTint(1.0);
    }

    void SetEyes(EyesMode m, Color c)
    {
        if (CompactEyes.Mode != m) { CompactEyes.Mode = m; HeaderEyes.Mode = m; }
        if (CompactEyes.Color != c)
        {
            var a = new ColorAnimation(c, TimeSpan.FromMilliseconds(450));
            CompactEyes.BeginAnimation(EyesView.ColorProperty, a);
            HeaderEyes.BeginAnimation(EyesView.ColorProperty, a);
        }
    }

    // ---------- looping effects ----------

    // Every repaint of the see-through window costs CPU, so the effects that loop for as long as Claude
    // works (spinners, breathing eyes, the glow) all run off ONE timer: one tick, one repaint. WPF
    // animations with a frame-rate cap drift apart and repaint far more often (measured: two clocks
    // capped at 12 fps drew 28 frames a second and doubled the CPU).
    readonly DispatcherTimer _loopTimer = new(DispatcherPriority.Render);
    readonly Stopwatch _loopClock = Stopwatch.StartNew();
    EyesMode? _pulseCompact, _pulseHeader;
    bool _sparkSpinning, _ringSpinning, _dashMarching;
    double _pulseStart, _glowStart, _glowTop;

    double LoopTime => _loopClock.Elapsed.TotalSeconds;

    /// <summary>Loops run only on what can be seen: a spinner on a hidden tile costs as much as a visible one.</summary>
    void SyncLoops()
    {
        bool compact = _mode is Mode.Collapsed or Mode.Live, expanded = _mode == Mode.Expanded;
        EyesMode? pulse = CompactEyes.Mode is EyesMode.Dots or EyesMode.Hearts ? CompactEyes.Mode : null;
        EyesMode? pc = compact ? pulse : null, ph = expanded ? pulse : null;
        if (pc != _pulseCompact || ph != _pulseHeader) _pulseStart = LoopTime;
        _pulseCompact = pc;
        _pulseHeader = ph;
        if (pc == null) CompactEyes.Pulse = 1;
        if (ph == null) HeaderEyes.Pulse = 1;

        _sparkSpinning = compact && ClaudeWorking;
        _ringSpinning = expanded && _tab == Tab.Home && ClaudeWorking;
        if (!_ringSpinning) ClaudeRingSpin.Angle = 0;
        _dashMarching = _mode == Mode.Drop;

        bool any = pc != null || ph != null || _sparkSpinning || _ringSpinning || _dashMarching || _glowPulsing;
        // These loops run for hours while Claude works, and 12 frames a second is plenty for a spinner
        // and a breathing glow. Only the drop zone's marching outline, part of a drag, gets 50.
        bool smooth = any && _mode == Mode.Drop;
        var interval = TimeSpan.FromMilliseconds(1000.0 / (smooth ? Fps : 12));
        if (_loopTimer.Interval != interval) _loopTimer.Interval = interval;
        // Windows' default 15.6 ms timer tick cannot do 20 ms steps; ask for 1 ms ticks only meanwhile.
        if (smooth != _fineTimer)
        {
            _fineTimer = smooth;
            if (smooth) Native.timeBeginPeriod(1); else Native.timeEndPeriod(1);
        }
        if (any && !_loopTimer.IsEnabled)
        {
            _loopTimer.Start();
            OnLoopTick();
        }
        else if (!any && _loopTimer.IsEnabled) _loopTimer.Stop();
    }

    bool _fineTimer;

    void OnLoopTick()
    {
        double t = LoopTime;
        if (_pulseCompact is { } pc) CompactEyes.Pulse = PulseAt(pc, t - _pulseStart);
        if (_pulseHeader is { } ph) HeaderEyes.Pulse = PulseAt(ph, t - _pulseStart);
        if (_sparkSpinning) LiveSpark.Angle = t * 150 % 360;
        if (_ringSpinning) ClaudeRingSpin.Angle = t * 150 % 360;
        if (_dashMarching) DropDash.StrokeDashOffset = -(t / 0.9 % 1) * 18;
        if (_glowPulsing) Glow.Opacity = 0.3 + (_glowTop - 0.3) * Wave(t - _glowStart, 1.3);
    }

    /// <summary>Eases from 0 to 1 over <paramref name="half"/> seconds and back, forever.</summary>
    static double Wave(double t, double half) => (1 - Math.Cos(Math.PI * t / half)) / 2;

    static double PulseAt(EyesMode m, double t)
    {
        if (m == EyesMode.Dots) return 0.1 + 0.9 * Wave(t, 0.85);
        // heartbeat: two quick beats, then rest
        double x = t % 1.25;
        return x < 0.11 ? 1
            : x < 0.23 ? 1 - 0.65 * (x - 0.11) / 0.12
            : x < 0.34 ? 0.35 + 0.55 * (x - 0.23) / 0.11
            : x < 0.70 ? 0.9 - 0.75 * (x - 0.34) / 0.36
            : 0.15;
    }

    /// <summary>Eyes blink every few seconds, sometimes twice, and now and then glance sideways.</summary>
    void Blink()
    {
        if (_mode is not (Mode.Collapsed or Mode.Live or Mode.Expanded)) return;
        var now = DateTime.Now;
        if (now >= _nextBlink && CompactEyes.Mode is EyesMode.Happy or EyesMode.Dots)
        {
            bool twice = _rng.NextDouble() < 0.22;
            var k = new DoubleAnimationUsingKeyFrames();
            k.KeyFrames.Add(new EasingDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170))));
            if (twice)
            {
                k.KeyFrames.Add(new EasingDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))));
                k.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360))));
            }
            Timeline.SetDesiredFrameRate(k, 30);
            (_mode == Mode.Expanded ? HeaderEyes : CompactEyes).BeginAnimation(EyesView.OpenProperty, k);
            _nextBlink = now.AddSeconds(3.2 + _rng.NextDouble() * 4.5);
        }
        if (now >= _nextGlance && CompactEyes.Mode == EyesMode.Happy && !ClaudeWorking)
        {
            double dir = _rng.NextDouble() < 0.5 ? -1 : 1;
            var k = new DoubleAnimationUsingKeyFrames();
            k.KeyFrames.Add(new EasingDoubleKeyFrame(dir, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(dir, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000))));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
            Timeline.SetDesiredFrameRate(k, 30);
            (_mode == Mode.Expanded ? HeaderEyes : CompactEyes).BeginAnimation(EyesView.LookProperty, k);
            _nextGlance = now.AddSeconds(12 + _rng.NextDouble() * 14);
        }
    }

    void UpdateGlow()
    {
        Color c; double target; bool pulse = false;
        if (_mode == Mode.Progress) return;
        if (_mode == Mode.Drop) { c = Amber; target = 0.95; }
        else if (ClaudeWorking) { c = Orange; target = 0.8; pulse = true; }
        else if (ClaudeJustDone) { c = Green; target = 0.9 * DoneFade; }
        else if (_mode == Mode.Pop) { c = _popGlow; target = 0.75; }
        else if (FocusRunning && _mode is Mode.Live) { c = Purple; target = 0.45; }
        else if (_share?.Running == true && _mode == Mode.Expanded && _tab == Tab.Tray) { c = Blue; target = 0.7; }
        else { c = _glowColor; target = 0; }

        GlowFx.BlurRadius = _mode is Mode.Collapsed or Mode.Live ? 18 : 36;
        if (c != _glowColor)
        {
            _glowColor = c;
            GlowFx.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(500)));
        }
        if (pulse)
        {
            if (_glowPulsing) return;
            _glowPulsing = true;
            _glowTarget = -1;
            _glowTop = target;
            _glowStart = LoopTime;
            Glow.BeginAnimation(OpacityProperty, null);
            Glow.Opacity = 0.3;
            SyncLoops();
            return;
        }
        if (_glowPulsing || Math.Abs(target - _glowTarget) > 0.02)
        {
            _glowPulsing = false;
            _glowTarget = target;
            Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(target > 0 ? 450 : 900)));
        }
    }

    void UpdateTint(double seconds)
    {
        var c = DesiredTint();
        if (c == _tint) return;
        _tint = c;
        _tintStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(c, TimeSpan.FromSeconds(seconds)) { EasingFunction = new SineEase() });
    }

    Color DesiredTint()
    {
        if (_mode == Mode.Drop) return C(0x55, 0x3E, 0x06);
        if (_mode == Mode.Progress) return C(0x16, 0x4A, 0x2A);
        if (_mode == Mode.Pop) return Mix(Black, _popGlow, 0.32);
        if (_mode != Mode.Expanded) return Black;
        return _tab switch
        {
            Tab.Claude => ClaudeWorking ? C(0x82, 0x46, 0x27) : ClaudeJustDone ? Mix(Black, C(0x1E, 0x74, 0x3A), DoneFade) : Black,
            Tab.Music => MusicPlaying ? C(0x52, 0x13, 0x1F) : C(0x26, 0x0B, 0x10),
            Tab.Timer => C(0x2E, 0x19, 0x4F),
            Tab.Tray => _share?.Running == true ? C(0x0B, 0x27, 0x4F) : C(0x42, 0x30, 0x05),
            Tab.Chat => C(0x2B, 0x17, 0x0D),
            Tab.Clip => C(0x0C, 0x1E, 0x33),
            Tab.Notes => C(0x2D, 0x26, 0x08),
            _ => Black,
        };
    }

    // ---------- tabs ----------

    void Tab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string s && Enum.TryParse<Tab>(s, out var t)) SelectTab(t);
    }

    void SelectTab(Tab t)
    {
        bool changed = t != _tab;
        _tab = t;
        HomePanel.Visibility = Vis(t == Tab.Home);
        ClaudePanel.Visibility = Vis(t == Tab.Claude);
        MusicPanel.Visibility = Vis(t == Tab.Music);
        ChatPanel.Visibility = Vis(t == Tab.Chat);
        TimerPanel.Visibility = Vis(t == Tab.Timer);
        TrayPanel.Visibility = Vis(t == Tab.Tray);
        ClipPanel.Visibility = Vis(t == Tab.Clip);
        NotesPanel.Visibility = Vis(t == Tab.Notes);
        SyncExpandedSize();
        var dim = Paint.Brush(0x8C, 255, 255, 255);
        foreach (var (k, s) in _tabs)
        {
            bool sel = k == t;
            s.Btn.Background = sel ? Paint.Brush(s.Bg) : Brushes.Transparent;
            s.Btn.Foreground = sel ? Paint.Brush(s.Fg) : dim;
            s.Label.Visibility = Vis(sel);
        }
        IcoClaude.Fill = t == Tab.Claude ? Paint.Brush(_tabs[Tab.Claude].Fg) : dim;
        IcoTray.Stroke = t == Tab.Tray ? Paint.Brush(_tabs[Tab.Tray].Fg) : dim;
        CloseVolume();
        RefreshExpanded();
        UpdateTint(0.4);
        UpdateGlow();
        SyncLoops();
        if (changed && _mode == Mode.Expanded) Reveal(PanelFor(t), false);
        if (t == Tab.Notes && _mode == Mode.Expanded) Dispatcher.InvokeAsync(() => { if (IsActive) NotesBox.Focus(); });
    }

    UIElement PanelFor(Tab t) => t switch
    {
        Tab.Claude => ClaudePanel,
        Tab.Music => MusicPanel,
        Tab.Chat => ChatPanel,
        Tab.Timer => TimerPanel,
        Tab.Tray => TrayPanel,
        Tab.Clip => ClipPanel,
        Tab.Notes => NotesPanel,
        _ => HomePanel,
    };

    void RefreshExpanded()
    {
        UpdateHeader();
        switch (_tab)
        {
            case Tab.Home: RefreshClaudeTile(); RefreshMusicTile(); RefreshFocusTile(); break;
            case Tab.Claude: RefreshClaude(); break;
            case Tab.Music: RefreshMusic(); break;
            case Tab.Chat: RefreshChat(); break;
            case Tab.Timer: RefreshFocus(); break;
            case Tab.Tray: RefreshTray(); break;
            case Tab.Clip: RefreshClips(); break;
        }
    }

    void UpdateHeader()
    {
        (string a, string b, Color bc) = _tab switch
        {
            Tab.Claude => ("Claude", "Code", C(0xE2, 0x88, 0x5F)),
            Tab.Tray => ("File", "Tray", Amber),
            Tab.Music => ("Music", "", Colors.White),
            Tab.Chat => ("Ask", "Claude", C(0xE2, 0x88, 0x5F)),
            Tab.Timer => ("Focus", "", Colors.White),
            Tab.Clip => ("Clipboard", "", Colors.White),
            Tab.Notes => ("Notes", "", Colors.White),
            _ => ("", "", Colors.White),
        };
        HeaderA.Text = a;
        HeaderB.Text = b;
        HeaderB.Foreground = Paint.Brush(bc);
        FolderBtn.Visibility = Vis(_tab == Tab.Claude && !string.IsNullOrEmpty(_cu?.Primary?.Cwd));
        PhoneBtn.Visibility = Vis(_tab == Tab.Tray);
        bool sharing = _share?.Running == true;
        PhoneBtn.Background = sharing ? Paint.Brush(Blue) : Brushes.Transparent;
        ClearBtn.Visibility = Vis((_tab == Tab.Tray && _tray.Items.Count > 0) || (_tab == Tab.Clip && _clips.Count > 0));
        ClearBtn.Content = _tab == Tab.Clip ? "Clear" : "Clear all";
        HeaderBars.Visibility = Vis(MusicPlaying);
    }

    void UpdateHeaderClock()
    {
        var (d, t) = Fmt.Now(_settings.PersianDate);
        HeaderDate.Text = d;
        HeaderTime.Text = t;
        HeaderDate.FlowDirection = _settings.PersianDate ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    // ---------- notifications ----------

    void ShowPop(string title, string text, string glyph, Color badge, ImageSource? image, Tab? tab, double seconds = 5.5)
    {
        PopTitle.Text = title;
        PopText.Text = text;
        PopText.Visibility = Vis(!string.IsNullOrEmpty(text));
        PopGlyph.Text = glyph;
        PopGlyph.Visibility = Vis(image == null);
        PopBadge.Background = image != null ? new ImageBrush(image) { Stretch = Stretch.UniformToFill } : Paint.Brush(badge);
        PopBadge.CornerRadius = new CornerRadius(image != null ? 12 : 22);
        _popGlow = badge;
        _popTab = tab;
        _popSession = null;
        _popUntil = DateTime.Now.AddSeconds(seconds);
        if (_mode is Mode.Expanded or Mode.Drop) { FlashNote(title); return; }
        SetMode(Mode.Pop);
        Bounce();
    }

    void FlashNote(string text)
    {
        HeaderNote.Text = text;
        HeaderNote.Visibility = Visibility.Visible;
        _noteUntil = DateTime.Now.AddSeconds(2.8);
        if (_mode == Mode.Expanded) Reveal(HeaderNote, false);
    }

    // ---------- home tiles ----------

    void HookTiles()
    {
        foreach (var t in new[] { TileClaude, TileMusic, TileFocus })
        {
            t.MouseEnter += (_, _) => ScaleTile(t, 1.035);
            t.MouseLeave += (_, _) => ScaleTile(t, 1.0);
            t.PreviewMouseLeftButtonDown += (_, _) => ScaleTile(t, 0.97);
            t.PreviewMouseLeftButtonUp += (_, _) => ScaleTile(t, 1.035);
        }
    }

    static void ScaleTile(Border t, double s)
    {
        if (t.RenderTransform is not ScaleTransform st) return;
        var a = new DoubleAnimation(s, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // ---------- settings menu ----------

    void MoreBtn_Click(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu { PlacementTarget = MoreBtn, Placement = PlacementMode.Bottom };
        m.Items.Add(Check("Open when the pointer touches it", _settings.HoverToOpen, v => _settings.HoverToOpen = v));
        m.Items.Add(Check("Hide when an app is full screen", _settings.HideInFullscreen, v => _settings.HideInFullscreen = v));
        m.Items.Add(Check("Persian calendar", _settings.PersianDate, v => { _settings.PersianDate = v; UpdateHeaderClock(); }));
        m.Items.Add(Check("Lyrics in the Music tab", _settings.Lyrics, SetLyricsOn));
        m.Items.Add(Check("Start with Windows", Startup.IsEnabled, Startup.Set));
        m.Items.Add(new Separator());
        m.Items.Add(new MenuItem { Header = "Open or close:  Ctrl+Alt+T", IsEnabled = false });
        m.Items.Add(Item("Files from your phone", () => OpenPath(PhoneShare.SaveDir, true)));
        m.Items.Add(Item("Quit Island", () => Application.Current.Shutdown()));
        OpenMenu(m);
    }

    MenuItem Check(string header, bool on, Action<bool> set)
    {
        var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = on, StaysOpenOnClick = true };
        mi.Click += (_, _) => { set(mi.IsChecked); _settings.Save(); };
        return mi;
    }

    static MenuItem Item(string header, Action act)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => act();
        return mi;
    }

    void OpenMenu(ContextMenu m)
    {
        _menuOpen = true;
        m.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (!_hoverInside && _mode == Mode.Expanded) _collapseTimer.Start();
        };
        m.IsOpen = true;
    }

    static void OpenPath(string path, bool folder = false)
    {
        try
        {
            if (folder) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Settings.Log(ex); }
    }

    static void ShowInFolder(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { Settings.Log(ex); }
    }

    void CheckFullscreen()
    {
        bool fs = _settings.HideInFullscreen && Native.ForegroundIsFullscreen(_hwnd);
        if (fs && !_hidden)
        {
            _hidden = true;
            Collapse();
            Hide();
        }
        else if (!fs && _hidden)
        {
            _hidden = false;
            Show();
        }
    }
}
