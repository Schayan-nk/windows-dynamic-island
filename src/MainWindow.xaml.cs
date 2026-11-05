using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Island;

public partial class MainWindow : Window
{
    enum Mode { Collapsed, Live, Pop, Expanded, Drop, Progress }

    // Island geometry per mode (width, height, bottom corner radius).
    static readonly (double W, double H, double R) Collapsed = (204, 32, 16), Live = (300, 32, 16),
        Pop = (440, 76, 28), Expanded = (720, 292, 32), DropSize = (640, 220, 32), ProgressSize = (460, 92, 30);

    // Spring that drives every size change. Slightly under-damped, so the island overshoots a hair and settles.
    const double Stiffness = 430, Damping = 31;

    // Motion runs at a steady 50 frames a second: smooth, and every frame of the see-through window costs
    // CPU. Looping effects on the small island run slower still (SyncLoops).
    public const int Fps = 50;
    static readonly long FrameTicks = Stopwatch.Frequency / Fps, SlackTicks = Stopwatch.Frequency / 200;
    long _nextFrame;

    /// <summary>
    /// True when the next 50 fps frame is due. Rendering events come at the screen's rate or faster,
    /// so on a 60 Hz screen this lets 5 of every 6 through. Motion is time-based, so speed never changes.
    /// </summary>
    static bool FrameDue(ref long next)
    {
        long now = Stopwatch.GetTimestamp();
        if (now < next - SlackTicks) return false;
        next = Math.Max(next + FrameTicks, now - SlackTicks);
        return true;
    }

    readonly Settings _settings = Settings.Load();
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(130) };
    readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    readonly DispatcherTimer _dropLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    readonly LinearGradientBrush _islandBrush;
    readonly GradientStop _tintStop;
    Mode _mode = Mode.Collapsed;
    Mode _beforeDrop = Mode.Collapsed;
    bool _hoverInside, _menuOpen, _dialogOpen, _internalDrag, _hidden;
    DateTime _popUntil;
    Tab? _popTab;
    IntPtr _hwnd;
    Color _tint = Color.FromRgb(5, 5, 6);

    // spring state
    double _w = Collapsed.W, _h = Collapsed.H, _r = Collapsed.R, _vw, _vh, _vr, _tw = Collapsed.W, _th = Collapsed.H, _tr = Collapsed.R;
    bool _animating;
    long _lastFrame;

    public MainWindow()
    {
        InitializeComponent();
        _islandBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        _islandBrush.GradientStops.Add(new GradientStop(Color.FromRgb(5, 5, 6), 0));
        _islandBrush.GradientStops.Add(new GradientStop(Color.FromRgb(5, 5, 6), 0.32));
        _tintStop = new GradientStop(Color.FromRgb(5, 5, 6), 1);
        _islandBrush.GradientStops.Add(_tintStop);
        Pill.Background = _islandBrush;
        ApplyGeometry();

        _clock.Tick += (_, _) => OnClock();
        _fast.Tick += (_, _) => OnFast();
        _loopTimer.Tick += (_, _) => OnLoopTick();
        _hoverTimer.Tick += (_, _) => OnHoverTimer();
        _collapseTimer.Tick += (_, _) => OnCollapseTimer();
        _dropLeaveTimer.Tick += (_, _) => { _dropLeaveTimer.Stop(); if (_mode == Mode.Drop) SetMode(_beforeDrop == Mode.Expanded ? Mode.Expanded : RestingMode()); };

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => { if (ShotsMode) { Left = -3000; Top = 0; } else { PlaceWindow(); FitWindow(true); } InitFeatures(); };
        Deactivated += (_, _) => { if (_mode == Mode.Expanded && !_hoverInside && !_menuOpen && !_dialogOpen) Collapse(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Collapse(); e.Handled = true; } };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(PlaceWindow);
    }

    // ---------- window plumbing ----------

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        // Software drawing: for this small see-through window the GPU path cost more CPU (every frame is
        // copied back from the GPU) and kept ~150 MB of driver memory.
        if (HwndSource.FromHwnd(_hwnd) is { } src)
            src.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        if (!Native.RegisterHotKey(_hwnd, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x54 /* T */))
            Settings.Log(new Exception("Ctrl+Alt+T is already taken by another app."));
        Native.AddClipboardFormatListener(_hwnd);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_HOTKEY when wParam.ToInt32() == 1:
                if (_mode == Mode.Expanded) Collapse(); else Expand(true);
                handled = true;
                break;
            case Native.WM_CLIPBOARDUPDATE:
                OnClipboardChanged();
                break;
            case Native.WM_DISPLAYCHANGE:
                Dispatcher.InvokeAsync(PlaceWindow);
                break;
        }
        return IntPtr.Zero;
    }

    void PlaceWindow()
    {
        if (ShotsMode) return;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    /// <summary>
    /// A see-through window repaints its whole surface on every animation frame, so while the island is
    /// small the window gets short (a quarter of the pixels) and grows back before it opens. Only the
    /// height changes: the island hangs from the top edge, so nothing moves on screen while it resizes
    /// (moving and resizing a layered window shows one frame of the old picture in the wrong place).
    /// </summary>
    void FitWindow(bool small)
    {
        if (ShotsMode) return;
        double h = small ? 100 : 420;
        if (Height != h) Height = h;
    }

    public void OpenFromTray()
    {
        if (_hidden) { _hidden = false; Show(); }
        Expand(true);
    }

    public void Teardown()
    {
        try
        {
            Native.UnregisterHotKey(_hwnd, 1);
            Native.RemoveClipboardFormatListener(_hwnd);
        }
        catch { }
        StopFeatures();
    }

    // ---------- modes ----------

    Mode RestingMode() => HasLiveActivity() ? Mode.Live : Mode.Collapsed;

    void Expand(bool activate = false)
    {
        _popUntil = default;
        SetMode(Mode.Expanded);
        if (activate) { Activate(); Focus(); }
    }

    void Collapse()
    {
        if (_mode is Mode.Collapsed or Mode.Live) return;
        _collapseTimer.Stop();
        CloseVolume();
        Keyboard.ClearFocus();
        SetMode(RestingMode());
    }

    void SetMode(Mode m)
    {
        if (m == _mode && m != Mode.Pop) return;
        var prev = _mode;
        _mode = m;
        if (m is not (Mode.Collapsed or Mode.Live)) FitWindow(false);
        if (!ShotsMode && (prev is not (Mode.Collapsed or Mode.Live) || m is not (Mode.Collapsed or Mode.Live))) HideGlowForSpring();

        CompactContent.Visibility = m is Mode.Collapsed or Mode.Live ? Visibility.Visible : Visibility.Collapsed;
        PopContent.Visibility = m == Mode.Pop ? Visibility.Visible : Visibility.Collapsed;
        // Hidden, not Collapsed: the open island stays laid out, so its first frame does not stall on layout.
        ExpandedContent.Visibility = m == Mode.Expanded ? Visibility.Visible : Visibility.Hidden;
        DropContent.Visibility = m == Mode.Drop ? Visibility.Visible : Visibility.Collapsed;
        ProgressContent.Visibility = m == Mode.Progress ? Visibility.Visible : Visibility.Collapsed;

        var g = m switch
        {
            Mode.Live => Live,
            Mode.Pop => Pop,
            Mode.Expanded => Expanded,
            Mode.Drop => DropSize,
            Mode.Progress => ProgressSize,
            _ => Collapsed,
        };
        AnimateTo(g.W, g.H, g.R);

        UIElement content = m switch
        {
            Mode.Pop => PopContent,
            Mode.Expanded => ExpandedContent,
            Mode.Drop => DropContent,
            Mode.Progress => ProgressContent,
            _ => CompactContent,
        };
        bool growing = g.H > (prev is Mode.Collapsed or Mode.Live ? Collapsed.H : 0);
        Reveal(content, growing && m != Mode.Live && m != Mode.Collapsed);

        if (m == Mode.Expanded) RefreshExpanded();
        UpdateTint(0.35);
        UpdateGlow();
        UpdateFastTimer();
        SyncLoops();
    }

    /// <summary>Content fades in and rises a few pixels while the island springs open; a soft blur clears as it lands.</summary>
    static void Reveal(UIElement el, bool fromSmall)
    {
        var delay = TimeSpan.FromMilliseconds(fromSmall ? 110 : 40);
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(260)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        el.BeginAnimation(OpacityProperty, fade);

        if (el.RenderTransform is not TransformGroup tg)
        {
            tg = new TransformGroup();
            tg.Children.Add(new ScaleTransform());
            tg.Children.Add(new TranslateTransform());
            el.RenderTransform = tg;
            el.RenderTransformOrigin = new Point(0.5, 0);
        }
        var sc = (ScaleTransform)tg.Children[0];
        var tt = (TranslateTransform)tg.Children[1];
        var grow = new DoubleAnimationUsingKeyFrames();
        grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(fromSmall ? 0.9 : 0.985, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(fromSmall ? 0.9 : 0.985, KeyTime.FromTimeSpan(delay)));
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(460)), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }));
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        var rise = new DoubleAnimationUsingKeyFrames();
        rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(fromSmall ? -10 : -4, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(fromSmall ? -10 : -4, KeyTime.FromTimeSpan(delay)));
        rise.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(420)), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }));
        tt.BeginAnimation(TranslateTransform.YProperty, rise);

        if (!fromSmall) { el.Effect = null; return; }
        var blur = new System.Windows.Media.Effects.BlurEffect { Radius = 10, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        el.Effect = blur;
        var unblur = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(300)) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        unblur.Completed += (_, _) => { if (el.Effect == blur) el.Effect = null; };
        blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, unblur);
    }

    // ---------- spring animation ----------

    void AnimateTo(double w, double h, double r)
    {
        _tw = w; _th = h; _tr = r;
        if (_animating) return;
        _animating = true;
        _lastFrame = _nextFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        if (!FrameDue(ref _nextFrame)) return;
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Clamp((now - _lastFrame) / (double)Stopwatch.Frequency, 0.001, 0.034);
        _lastFrame = now;
        Step(ref _w, ref _vw, _tw, dt);
        Step(ref _h, ref _vh, _th, dt);
        Step(ref _r, ref _vr, _tr, dt);
        bool settled = Math.Abs(_w - _tw) < 0.3 && Math.Abs(_h - _th) < 0.3 && Math.Abs(_vw) < 4 && Math.Abs(_vh) < 4;
        if (settled)
        {
            _w = _tw; _h = _th; _r = _tr; _vw = _vh = _vr = 0;
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
            ApplyGeometry();
            if (_mode is Mode.Collapsed or Mode.Live && !_hoverInside) FitWindow(true);
            ShowGlowAfterSpring();
            return;
        }
        ApplyGeometry();
    }

    /// <summary>
    /// While the island grows or shrinks between sizes the glow is hidden: it is a blur, and redrawing a
    /// blur of a changing shape on every frame is what made opening stutter. It fades back in on landing.
    /// </summary>
    void HideGlowForSpring()
    {
        GlowFade.BeginAnimation(OpacityProperty, null);
        GlowFade.Visibility = Visibility.Hidden;
    }

    void ShowGlowAfterSpring()
    {
        if (GlowFade.Visibility == Visibility.Visible) return;
        GlowFade.Visibility = Visibility.Visible;
        GlowFade.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
    }

    static void Step(ref double x, ref double v, double target, double dt)
    {
        const int n = 4;
        double h = dt / n;
        for (int i = 0; i < n; i++)
        {
            double a = -Stiffness * (x - target) - Damping * v;
            v += a * h;
            x += v * h;
        }
    }

    void ApplyGeometry()
    {
        double w = Math.Max(20, _w), h = Math.Max(12, _h);
        double r = Math.Clamp(_r, 0, h / 2);
        Host.Width = w;
        Host.Height = h;
        var cr = new CornerRadius(0, 0, r, r);
        Pill.CornerRadius = cr;
        GlowShape.CornerRadius = cr;
        Pill.Clip = new RectangleGeometry(new Rect(0, -r, w, h + r), r, r);
    }

    // ---------- hover, click, keyboard ----------

    void Host_MouseEnter(object sender, MouseEventArgs e)
    {
        _hoverInside = true;
        _collapseTimer.Stop();
        if (_mode is Mode.Collapsed or Mode.Live or Mode.Pop or Mode.Progress && _settings.HoverToOpen) _hoverTimer.Start();
        SquishOnHover(true);
    }

    void Host_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverInside = false;
        _hoverTimer.Stop();
        SquishOnHover(false);
        if (_mode == Mode.Expanded) _collapseTimer.Start();
    }

    /// <summary>The collapsed island swells a little under the pointer, like a button being touched.</summary>
    void SquishOnHover(bool on)
    {
        if (_mode is not (Mode.Collapsed or Mode.Live)) return;
        var g = _mode == Mode.Live ? Live : Collapsed;
        AnimateTo(on ? g.W + 14 : g.W, on ? g.H + 4 : g.H, g.R + (on ? 2 : 0));
    }

    void Host_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode is not (Mode.Collapsed or Mode.Live or Mode.Pop or Mode.Progress)) return;
        _hoverTimer.Stop();
        if (_mode is Mode.Pop or Mode.Progress && _popTab != null) SelectTab(_popTab.Value);
        Expand(true);
        e.Handled = true;
    }

    void OnHoverTimer()
    {
        _hoverTimer.Stop();
        if (!_hoverInside || _mode is not (Mode.Collapsed or Mode.Live or Mode.Pop or Mode.Progress)) return;
        if (_mode is Mode.Pop or Mode.Progress && _popTab != null) SelectTab(_popTab.Value);
        else if (ClaudeWorking && _tab == Tab.Home) SelectTab(Tab.Claude);
        Expand();
    }

    void OnCollapseTimer()
    {
        _collapseTimer.Stop();
        if (_hoverInside || _mode != Mode.Expanded || KeepOpen()) return;
        Collapse();
    }

    bool KeepOpen()
    {
        if (_menuOpen || _dialogOpen || _internalDrag || Mouse.LeftButton == MouseButtonState.Pressed) return true;
        return IsActive && Keyboard.FocusedElement is TextBox tb && tb.IsKeyboardFocusWithin;
    }

    // ---------- drag and drop ----------

    void Host_DragEnter(object sender, DragEventArgs e)
    {
        if (_internalDrag || !e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        _dropLeaveTimer.Stop();
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        DragImage.Enter(_hwnd, e.Data, e.Effects);
        if (_mode == Mode.Drop) { MochiFollow(e.GetPosition(DropContent)); return; }
        _beforeDrop = _mode;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        DropSub.Text = files is { Length: > 1 } ? $"{files.Length} files go into your tray" : "The file goes into your tray";
        SetMode(Mode.Drop);
        MochiEnter();
    }

    void Host_DragOver(object sender, DragEventArgs e)
    {
        if (_internalDrag || !e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        _dropLeaveTimer.Stop();
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        DragImage.Over(e.Effects);
        if (_mode == Mode.Drop) MochiFollow(e.GetPosition(DropContent));
    }

    void Host_DragLeave(object sender, DragEventArgs e)
    {
        DragImage.Leave();
        if (_mode == Mode.Drop && !_eating) _dropLeaveTimer.Start();
    }

    void Host_Drop(object sender, DragEventArgs e)
    {
        _dropLeaveTimer.Stop();
        e.Handled = true;
        DragImage.Drop(e.Data, DragDropEffects.Copy);
        if (_internalDrag || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        if (_mode != Mode.Drop) { SetMode(Mode.Drop); MochiEnter(); }
        EatAndCollect(files, e.GetPosition(DropContent));
    }

    /// <summary>A quick nudge of the island, used when it catches something.</summary>
    void Bounce()
    {
        _vh += 260;
        _vw += 180;
        if (!_animating) AnimateTo(_tw, _th, _tr);
    }
}
