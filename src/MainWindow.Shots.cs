using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Island;

/// <summary>Island.exe --shots DIR renders every state off screen to PNG files (for checking the design).</summary>
public partial class MainWindow
{
    public bool ShotsMode { get; set; }

    public async Task RunShotsAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        await Task.Delay(3000);

        await Shot(dir, "01-collapsed", () => { _cu = null; _mi = new MediaInfo(); SetMode(Mode.Collapsed); });
        await Shot(dir, "02-live-claude", () => SetMode(Mode.Collapsed), 2500);
        await Shot(dir, "03-pop", () => ShowPop("Done · webshop", "Done! The total now adds VAT the right way. All 48 tests pass.", "\uE73E", Green, null, Tab.Claude, 60));
        await Shot(dir, "04-home", () => { SelectTab(Tab.Home); SetMode(Mode.Expanded); }, 1300);
        await Shot(dir, "05-claude", () => SelectTab(Tab.Claude));

        var art = FakeArt();
        _mi = new MediaInfo { HasSession = true, Playing = true, Title = "Golden Hour", Artist = "Nova Lane", App = "Spotify", Art = art,
            Duration = TimeSpan.FromSeconds(200), Position = TimeSpan.FromSeconds(72), PositionAt = DateTime.Now };
        OnMedia(_mi);
        await Shot(dir, "06-home-music", () => SelectTab(Tab.Home));
        await Shot(dir, "07-music", () => SelectTab(Tab.Music));
        await Shot(dir, "08-focus", () => { SelectTab(Tab.Timer); StartFocus(); });
        await Shot(dir, "09-chat", () => SelectTab(Tab.Chat));

        var sample = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Claude", "karlancer-portfolio", "mototaj", "mototaj-00-cover.jpg");
        if (File.Exists(sample)) _tray.Add(sample);
        _tray.Add(Path.Combine(AppContext.BaseDirectory, "Island.dll"));
        await Shot(dir, "10-tray", () => SelectTab(Tab.Tray), 2500);
        await Shot(dir, "11-share", () =>
        {
            _previewShare = true;
            QrImage.Source = Qr.Render("http://192.168.1.20:52800/Zm9vYmFyYmF6cXV4/");
            TrayScroll.Visibility = Visibility.Collapsed;
            SharePanel.Visibility = Visibility.Visible;
            ShareTitle.Text = "Scan with your phone";
            ShareFoot.Text = "Same Wi-Fi only · ends in 60 min";
            ShareUrl.Text = "192.168.1.20:52800 · Wi-Fi";
            ShareFilesText.Text = "2 files are ready";
            _tintStop.BeginAnimation(GradientStop.ColorProperty, null);
            _tintStop.Color = C(0x0B, 0x27, 0x4F);
        });
        _previewShare = false;
        _clips.Clear();
        AddClip(new ClipItem { Text = "const total = sum(items) * (1 + VAT)" });
        AddClip(new ClipItem { Text = "سلام، فایل نهایی را برایتان فرستادم." });
        AddClip(new ClipItem { Text = "https://example.com/invoice/2026-11" });
        await Shot(dir, "12-clipboard", () => SelectTab(Tab.Clip));
        await Shot(dir, "13-notes", () => SelectTab(Tab.Notes));
        await Shot(dir, "14-drop", () => { DropSub.Text = "The file goes into your tray"; SetMode(Mode.Drop); MochiEnter(); }, 900);
        await Shot(dir, "15-drop-follow", () => MochiFollow(new Point(420, 150)), 700);
        await Shot(dir, "16-eat", () => EatAndCollect(new[] { sample }, new Point(430, 70)), 620);
        await Shot(dir, "17-eaten", () => { }, 520);
        await Task.Delay(1500);
        await Shot(dir, "18-progress", () => { SetMode(Mode.Collapsed); OnPhoneProgress("beach.jpg", 0.2); }, 400);
        await Shot(dir, "19-progress-61", () => OnPhoneProgress("beach.jpg", 0.61), 500);
        await Shot(dir, "20-home-claude", () => { SelectTab(Tab.Home); SetMode(Mode.Expanded); }, 1300);
        await Shot(dir, "21-music-lyrics", () =>
        {
            _mi = new MediaInfo { HasSession = true, Playing = true, Title = "Golden Hour", Artist = "Nova Lane", App = "Spotify", Art = art,
                Duration = TimeSpan.FromSeconds(200), Position = TimeSpan.FromSeconds(24.5), PositionAt = DateTime.Now };
            OnMedia(_mi);
            SelectTab(Tab.Music);
            ShowDemoLyrics();
        }, 1300);
        Application.Current.Shutdown();
    }

    async Task Shot(string dir, string name, Action setup, int wait = 1100)
    {
        setup();
        await Task.Delay(wait);
        Root.UpdateLayout();
        int w = (int)Root.ActualWidth, h = (int)Root.ActualHeight;
        var island = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
        island.Render(Root);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var wall = new LinearGradientBrush(Color.FromRgb(0x10, 0x2A, 0x8C), Color.FromRgb(0x2B, 0x6C, 0xF0), 65);
            dc.DrawRectangle(wall, null, new Rect(0, 0, w, h));
            dc.DrawImage(island, new Rect(0, 0, w, h));
        }
        var outBmp = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
        outBmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(outBmp));
        await using var fs = File.Create(Path.Combine(dir, name + ".png"));
        enc.Save(fs);
    }

    static ImageSource FakeArt()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xFF, 0x7A, 0x59), Color.FromRgb(0xC2, 0x1E, 0x56), 45), null, new Rect(0, 0, 200, 200));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xE0, 0xB0)), null, new Point(140, 70), 34, 34);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x66, 0x3A, 0x0B, 0x2A)), null, new Rect(0, 130, 200, 70));
        }
        var bmp = new RenderTargetBitmap(200, 200, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }
}
