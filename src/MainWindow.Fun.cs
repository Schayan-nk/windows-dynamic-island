using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Island;

/// <summary>Mascot, sparkles, glowing tiles and the phone progress bar.</summary>
public partial class MainWindow
{
    double _mochiX = 290, _mochiV, _mochiTarget = 290;
    bool _mochiRunning, _eating, _claudeSpin;
    long _mochiLast;
    string _lastTrack = "";
    readonly DropShadowEffect _artGlow = new() { Color = Color.FromRgb(0xFF, 0x3B, 0x5C), BlurRadius = 26, ShadowDepth = 0, Opacity = 0.8 };

    static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    static void Anim(IAnimatable target, DependencyProperty p, double to, double ms, IEasingFunction? ease = null) =>
        target.BeginAnimation(p, new DoubleAnimation(to, Ms(ms)) { EasingFunction = ease });

    // ---------- drop zone ----------

    /// <summary>The mascot pops up from the bottom of the drop zone, curious, mouth a little open.</summary>
    void MochiEnter()
    {
        _eating = false;
        _mochiX = _mochiTarget = (DropContent.Width - Mochi.Width) / 2;
        _mochiV = 0;
        Canvas.SetLeft(Mochi, _mochiX);
        Mochi.BeginAnimation(Mascot.HappyProperty, null);
        Mochi.Happy = 0;
        Mochi.Look = 0;
        Anim(Mochi, Mascot.MouthProperty, 0.25, 200);
        MochiScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        MochiScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        MochiScale.ScaleX = MochiScale.ScaleY = 1;
        MochiTilt.Angle = 0;
        MochiLift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(70, 0, Ms(520)) { BeginTime = Ms(120), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 } });
        DropFile.Visibility = Visibility.Collapsed;
        DropSparkles.Children.Clear();
        DropTitle.Text = "Let go, I'll catch it!";
    }

    /// <summary>The mascot runs under the pointer, looks at it, and opens wide when the file is right above it.</summary>
    void MochiFollow(Point p)
    {
        if (_eating) return;
        _mochiTarget = Math.Clamp(p.X - Mochi.Width / 2, 34, DropContent.Width - 34 - Mochi.Width);
        double cx = _mochiX + Mochi.Width / 2;
        Mochi.Look = Math.Clamp((p.X - cx) / 140, -1, 1);
        bool above = Math.Abs(p.X - cx) < 80 && p.Y > 80;
        if (Math.Abs(Mochi.Mouth - (above ? 1 : 0.25)) > 0.05) Anim(Mochi, Mascot.MouthProperty, above ? 1 : 0.25, 160);
        StartMochi();
    }

    void StartMochi()
    {
        if (_mochiRunning) return;
        _mochiRunning = true;
        _mochiLast = _mochiNext = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += MochiFrame;
    }

    long _mochiNext;

    void MochiFrame(object? sender, EventArgs e)
    {
        if (!FrameDue(ref _mochiNext)) return;
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Clamp((now - _mochiLast) / (double)Stopwatch.Frequency, 0.001, 0.034);
        _mochiLast = now;
        for (int i = 0; i < 4; i++)
        {
            double h = dt / 4, a = -240 * (_mochiX - _mochiTarget) - 22 * _mochiV;
            _mochiV += a * h;
            _mochiX += _mochiV * h;
        }
        Canvas.SetLeft(Mochi, _mochiX);
        MochiTilt.Angle = Math.Clamp(_mochiV * 0.035, -15, 15);
        bool done = Math.Abs(_mochiX - _mochiTarget) < 0.4 && Math.Abs(_mochiV) < 3;
        if (done || _mode != Mode.Drop)
        {
            CompositionTarget.Rendering -= MochiFrame;
            _mochiRunning = false;
            MochiTilt.Angle = 0;
        }
    }

    /// <summary>Catch: the file drops into the mouth, the mascot gulps, chews, smiles; sparkles; then the tray opens.</summary>
    async void EatAndCollect(string[] files, Point drop)
    {
        _eating = true;
        int added = 0;
        foreach (var f in files) if (_tray.Add(f) != null) added++;
        SaveTray();

        var thumb = files.Length > 0 ? ShellThumb.Get(files[0], 96) : null;
        DropFile.Background = thumb != null ? new ImageBrush(thumb) { Stretch = Stretch.UniformToFill } : Paint.Brush(0x66, 255, 255, 255);
        double fx = Math.Clamp(drop.X - 22, 24, DropContent.Width - 68), fy = Math.Clamp(drop.Y - 30, 12, 96);
        DropFile.BeginAnimation(Canvas.LeftProperty, null);
        DropFile.BeginAnimation(Canvas.TopProperty, null);
        DropFile.BeginAnimation(OpacityProperty, null);
        Canvas.SetLeft(DropFile, fx);
        Canvas.SetTop(DropFile, fy);
        DropFile.Opacity = 1;
        DropFileScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DropFileScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        DropFileScale.ScaleX = DropFileScale.ScaleY = 1;
        DropFile.Visibility = Visibility.Visible;

        _mochiTarget = Math.Clamp(fx + 22 - Mochi.Width / 2, 34, DropContent.Width - 34 - Mochi.Width);
        StartMochi();
        Mochi.Look = 0;
        Anim(Mochi, Mascot.MouthProperty, 1, 140);
        await Task.Delay(240);

        double mx = _mochiTarget + Mochi.Width / 2 - 22, my = Canvas.GetTop(Mochi) - 6;
        var fall = new CubicEase { EasingMode = EasingMode.EaseIn };
        DropFile.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(fx, mx, Ms(300)) { EasingFunction = fall });
        DropFile.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(fy, my, Ms(300)) { EasingFunction = fall });
        DropFileScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.3, Ms(300)) { EasingFunction = fall });
        DropFileScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.3, Ms(300)) { EasingFunction = fall });
        DropFile.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Ms(110)) { BeginTime = Ms(220) });
        await Task.Delay(300);

        Anim(Mochi, Mascot.MouthProperty, 0, 110);
        Chew();
        await Task.Delay(300);
        Anim(Mochi, Mascot.HappyProperty, 1, 220);
        Burst(DropSparkles, new Point(_mochiX + Mochi.Width / 2, Canvas.GetTop(Mochi) + 8), Amber, 14);
        DropTitle.Text = added > 1 ? $"Got {added} files!" : added == 1 ? "Got it!" : "Already in your tray";
        DropSub.Text = added > 0 ? "It's in your tray now" : "Nothing new to add";
        await Task.Delay(950);

        _eating = false;
        DropFile.Visibility = Visibility.Collapsed;
        if (_mode == Mode.Drop)
        {
            SelectTab(Tab.Tray);
            Expand();
        }
    }

    void Chew()
    {
        var y = new DoubleAnimationUsingKeyFrames();
        var x = new DoubleAnimationUsingKeyFrames();
        void K(double ms, double sy, double sx)
        {
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            y.KeyFrames.Add(new EasingDoubleKeyFrame(sy, KeyTime.FromTimeSpan(Ms(ms)), ease));
            x.KeyFrames.Add(new EasingDoubleKeyFrame(sx, KeyTime.FromTimeSpan(Ms(ms)), ease));
        }
        K(100, 0.8, 1.14);
        K(220, 1.1, 0.93);
        K(330, 0.88, 1.08);
        K(440, 1.05, 0.97);
        K(560, 1, 1);
        MochiScale.BeginAnimation(ScaleTransform.ScaleYProperty, y);
        MochiScale.BeginAnimation(ScaleTransform.ScaleXProperty, x);
    }

    /// <summary>A little burst of dots flying out of a point and fading.</summary>
    void Burst(Canvas layer, Point c, Color color, int n)
    {
        for (int i = 0; i < n; i++)
        {
            double a = i * 2 * Math.PI / n + _rng.NextDouble() * 0.6;
            double dist = 24 + _rng.NextDouble() * 34, size = 3 + _rng.NextDouble() * 3.5;
            var dot = new Ellipse { Width = size, Height = size, Fill = Paint.Brush(i % 3 == 0 ? Colors.White : color), IsHitTestVisible = false };
            var tt = new TranslateTransform();
            dot.RenderTransform = tt;
            Canvas.SetLeft(dot, c.X - size / 2);
            Canvas.SetTop(dot, c.Y - size / 2);
            layer.Children.Add(dot);
            var dur = Ms(560 + _rng.Next(260));
            var outEase = new CubicEase { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Math.Cos(a) * dist, dur) { EasingFunction = outEase });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Math.Sin(a) * dist - 8, dur) { EasingFunction = outEase });
            var fade = new DoubleAnimation(1, 0, dur) { BeginTime = Ms(140) };
            fade.Completed += (_, _) => layer.Children.Remove(dot);
            dot.BeginAnimation(OpacityProperty, fade);
        }
    }

    // ---------- compact island when idle: time, eyes, battery ----------

    void UpdateIdleCompact(bool live)
    {
        IdleTime.Visibility = Vis(!live);
        IdleBattery.Visibility = Vis(!live);
        if (live) return;
        var now = DateTime.Now;
        IdleTime.Text = _settings.PersianDate ? Fmt.FaDigits(now.ToString("H:mm")) : now.ToString("H:mm");
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        bool battery = (ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0
                       && ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.Unknown;
        ((UIElement)IdleBattery.Children[0]).Visibility = Vis(battery);
        if (battery)
        {
            double pct = Math.Clamp(ps.BatteryLifePercent, 0, 1);
            IdleBatteryText.Text = $"{Math.Round(pct * 100)}%";
            IdleBatteryFill.Width = Math.Max(1.5, 13.6 * pct);
            IdleBatteryFill.Background = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online ? Paint.Brush(Green)
                : pct < 0.2 ? Paint.Brush(C(0xFF, 0x45, 0x3A)) : Brushes.White;
        }
        else
        {
            IdleBatteryText.Text = _settings.PersianDate ? Fmt.Now(true).date.Split(' ')[1] + " " + Fmt.Now(true).date.Split(' ')[2]
                : now.ToString("ddd d", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // ---------- home tiles light up ----------

    void UpdateTileGlow()
    {
        bool working = ClaudeWorking;
        if (working != _claudeSpin)
        {
            _claudeSpin = working;
            ClaudeRingText.Visibility = Vis(!working);
            ClaudeRingMark.Visibility = Vis(working);
            if (working) ClaudeRing.Progress = 0.28;
            FlashTile(TileClaudeFlash);
        }
        bool playing = MusicPlaying;
        TileBars.Visibility = Vis(playing);
        TileMusicArt.Effect = playing && _mi.Art != null ? _artGlow : null;
        var track = _mi.Title + "|" + _mi.Artist;
        if (track != _lastTrack)
        {
            if (_lastTrack.Length > 1 && _mi.HasSession) FlashTile(TileMusicFlash);
            _lastTrack = track;
        }
    }

    static void FlashTile(Border flash)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(Ms(90))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(Ms(800)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        flash.BeginAnimation(OpacityProperty, a);
    }

    // ---------- files arriving from the phone ----------

    void OnPhoneProgress(string name, double f)
    {
        if (_mode == Mode.Expanded && _tab == Tab.Tray)
        {
            if (f < 1) ShareStatus.Text = $"Receiving {name} · {Math.Round(f * 100)}%";
            return;
        }
        if (_mode == Mode.Drop) return;
        if (_mode != Mode.Progress)
        {
            if (_mode == Mode.Expanded) return;
            ProgressMochi.Happy = 0;
            _popTab = Tab.Tray;
            SetMode(Mode.Progress);
        }
        _popUntil = DateTime.Now.AddSeconds(4);
        ProgressTitle.Text = $"Receiving {name}";
        ProgressPct.Text = $"{Math.Round(f * 100)} %";
        Anim(ProgressFill, ScaleTransform.ScaleXProperty, f, 160);
        double width = Math.Max(1, ProgressTrack.ActualWidth > 0 ? ProgressTrack.ActualWidth : 408);
        Anim(ProgressMochi, Canvas.LeftProperty, width * f - 12, 160);
        _tintStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Mix(Black, C(0x1E, 0x74, 0x3A), 0.25 + 0.6 * f), Ms(200)));
        _tint = Color.FromRgb(1, 2, 3); // force the next UpdateTint to repaint
        GlowFx.Color = Green;
        _glowPulsing = false;
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25 + 0.65 * f, Ms(200)));
        _glowTarget = -1;
    }
}

/// <summary>Lets Windows draw the dragged file's picture over the island, as Explorer does.</summary>
internal static class DragImage
{
    [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
    class DragDropHelperCom { }

    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTargetHelper
    {
        [PreserveSig] int DragEnter(IntPtr hwnd, System.Runtime.InteropServices.ComTypes.IDataObject data, ref NPOINT pt, int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int DragOver(ref NPOINT pt, int effect);
        [PreserveSig] int Drop(System.Runtime.InteropServices.ComTypes.IDataObject data, ref NPOINT pt, int effect);
        [PreserveSig] int Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }

    [StructLayout(LayoutKind.Sequential)] struct NPOINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NPOINT p);

    static IDropTargetHelper? _helper;
    static IDropTargetHelper? Helper
    {
        get
        {
            try { return _helper ??= (IDropTargetHelper)new DragDropHelperCom(); }
            catch { return null; }
        }
    }

    public static void Enter(IntPtr hwnd, IDataObject data, DragDropEffects fx)
    {
        try
        {
            if (data is System.Runtime.InteropServices.ComTypes.IDataObject com && GetCursorPos(out var p))
                Helper?.DragEnter(hwnd, com, ref p, (int)fx);
        }
        catch { }
    }

    public static void Over(DragDropEffects fx)
    {
        try { if (GetCursorPos(out var p)) Helper?.DragOver(ref p, (int)fx); } catch { }
    }

    public static void Leave()
    {
        try { Helper?.DragLeave(); } catch { }
    }

    public static void Drop(IDataObject data, DragDropEffects fx)
    {
        try
        {
            if (data is System.Runtime.InteropServices.ComTypes.IDataObject com && GetCursorPos(out var p))
                Helper?.Drop(com, ref p, (int)fx);
        }
        catch { }
    }
}
