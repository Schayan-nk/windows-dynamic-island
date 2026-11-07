using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Island;

public partial class App : Application
{
    Mutex? _mutex;
    bool _ownsMutex;
    System.Windows.Forms.NotifyIcon? _tray;
    MainWindow? _win;

    static App()
    {
        // Every WPF animation runs at 50 frames a second at most: steady motion, and each frame of the
        // see-through window costs CPU. Must run before any animation exists.
        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(System.Windows.Media.Animation.Timeline), new FrameworkPropertyMetadata { DefaultValue = Island.MainWindow.Fps });
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        int test = Array.IndexOf(e.Args, "--selftest");
        if (test >= 0 && test + 1 < e.Args.Length)
        {
            var log = e.Args[test + 1];
            bool online = Array.IndexOf(e.Args, "--online") >= 0;
            Task.Run(() => SelfTest.RunAsync(log, online)).ContinueWith(t => Dispatcher.Invoke(() => Shutdown(t.IsCompletedSuccessfully ? t.Result : 99)));
            return;
        }

        int shots = Array.IndexOf(e.Args, "--shots");
        if (shots >= 0 && shots + 1 < e.Args.Length)
        {
            var w = new MainWindow { ShotsMode = true };
            w.Show();
            _ = w.RunShotsAsync(e.Args[shots + 1]);
            return;
        }

        _mutex = new Mutex(true, @"Local\Island.DynamicIsland", out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, a) => { Settings.Log(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Settings.Log(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Settings.Log(a.Exception); a.SetObserved(); };

        Island.Startup.Repair();
        _win = new MainWindow();
        _win.Show();

        _tray = new System.Windows.Forms.NotifyIcon { Text = "Island", Visible = true, Icon = LoadIcon() };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var autoStart = new System.Windows.Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        autoStart.Click += (_, _) => Island.Startup.Set(autoStart.Checked);
        menu.Opening += (_, _) => autoStart.Checked = Island.Startup.IsEnabled;
        menu.Items.Add("Open the island", null, (_, _) => _win.OpenFromTray());
        menu.Items.Add(autoStart);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, a) =>
        {
            if (a.Button == System.Windows.Forms.MouseButtons.Left) _win.OpenFromTray();
        };
    }

    static System.Drawing.Icon? LoadIcon()
    {
        try
        {
            var s = GetResourceStream(new Uri("pack://application:,,,/Assets/island.ico"))?.Stream;
            return s == null ? null : new System.Drawing.Icon(s);
        }
        catch { return null; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _win?.Teardown(); } catch { }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        if (_ownsMutex) { try { _mutex?.ReleaseMutex(); } catch { } }
        base.OnExit(e);
    }
}
