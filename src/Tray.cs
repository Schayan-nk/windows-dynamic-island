using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Island;

public sealed class TrayItem : INotifyPropertyChanged
{
    static readonly HashSet<string> PhotoExts = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm" };

    public event PropertyChangedEventHandler? PropertyChanged;

    public TrayItem(string path, bool fromPhone)
    {
        Path = path;
        FromPhone = fromPhone;
        IsDir = Directory.Exists(path);
        try { Size = IsDir ? 0 : new FileInfo(path).Length; } catch { }
        IsPhoto = !IsDir && PhotoExts.Contains(System.IO.Path.GetExtension(path));
    }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public string Path { get; }
    public bool FromPhone { get; }
    public bool IsDir { get; }
    public long Size { get; }
    public bool IsPhoto { get; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
    public string DisplayName
    {
        get
        {
            var n = IsDir ? Name : System.IO.Path.GetFileNameWithoutExtension(Name);
            return string.IsNullOrEmpty(n) ? Name : n;
        }
    }
    public string Ext => IsDir ? "" : System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    public string Meta => IsDir ? "Folder" : $"{(Ext.Length > 0 ? Ext : "File")} · {Fmt.Size(Size)}";
    public Visibility PhotoVisibility => IsPhoto ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconVisibility => IsPhoto ? Visibility.Collapsed : Visibility.Visible;

    ImageSource? _thumb;
    public ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb))); }
    }

    /// <summary>Small JPEG for the phone page; null for non-photos.</summary>
    public byte[]? PhoneThumb { get; set; }
}

/// <summary>Files dropped on the island. Items reference the original files; nothing is copied.</summary>
public sealed class TrayStore
{
    public ObservableCollection<TrayItem> Items { get; } = new();
    volatile TrayItem[] _snapshot = Array.Empty<TrayItem>();
    int _version;
    readonly ThumbWorker _thumbs;

    public event Action? Changed;

    public TrayStore(Dispatcher ui) { _thumbs = new ThumbWorker(ui); }

    public TrayItem[] Snapshot => _snapshot;
    public int Version => Volatile.Read(ref _version);

    public TrayItem? Add(string path, bool fromPhone = false)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return null;
        var existing = Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        var item = new TrayItem(path, fromPhone);
        Items.Add(item);
        _thumbs.Request(item);
        Publish();
        return item;
    }

    public void Remove(TrayItem item)
    {
        if (Items.Remove(item)) Publish();
    }

    public void Clear()
    {
        if (Items.Count == 0) return;
        Items.Clear();
        Publish();
    }

    public TrayItem? Find(string id) => _snapshot.FirstOrDefault(i => i.Id == id);

    void Publish()
    {
        _snapshot = Items.ToArray();
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }
}

/// <summary>Makes thumbnails on its own STA thread so the island never stutters.</summary>
sealed class ThumbWorker
{
    readonly BlockingCollection<TrayItem> _queue = new();
    readonly Dispatcher _ui;

    public ThumbWorker(Dispatcher ui)
    {
        _ui = ui;
        var t = new Thread(Run) { IsBackground = true, Name = "Island thumbnails" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public void Request(TrayItem item) => _queue.Add(item);

    void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                var bmp = ShellThumb.Get(item.Path, item.IsPhoto ? 256 : 96);
                if (bmp == null) continue;
                if (item.IsPhoto) item.PhoneThumb = Jpeg(bmp, 240);
                _ui.InvokeAsync(() => item.Thumb = bmp);
            }
            catch (Exception ex) { Settings.Log(ex); }
        }
    }

    static byte[]? Jpeg(BitmapSource src, int max)
    {
        try
        {
            double k = Math.Min(1.0, max / (double)Math.Max(src.PixelWidth, src.PixelHeight));
            BitmapSource s = k < 1 ? new TransformedBitmap(src, new ScaleTransform(k, k)) : src;
            var flat = new FormatConvertedBitmap(s, PixelFormats.Bgr24, null, 0);
            var enc = new JpegBitmapEncoder { QualityLevel = 82 };
            enc.Frames.Add(BitmapFrame.Create(flat));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}

/// <summary>Explorer's own thumbnails (photos with the right rotation, video frames, PDF pages, icons).</summary>
static class ShellThumb
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, ref BITMAP bm);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    public static BitmapSource? Get(string path, int size)
    {
        IShellItemImageFactory? f = null;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
            if (f.GetImage(new SIZE { cx = size, cy = size }, 0, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
            try { return FromHBitmap(hbm); }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
        finally { if (f != null) Marshal.ReleaseComObject(f); }
    }

    static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        var bm = new BITMAP();
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        if (w <= 0 || h <= 0) return null;
        var bi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var buf = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, hbm, 0, (uint)h, buf, ref bi, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        bool anyAlpha = false;
        for (int i = 3; i < buf.Length; i += 4) if (buf[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buf, w * 4);
        src.Freeze();
        return src;
    }
}
