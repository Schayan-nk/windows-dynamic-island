using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace Island;

public sealed class MediaInfo
{
    public bool HasSession;
    public bool Playing;
    public string Title = "";
    public string Artist = "";
    public string Album = "";
    public string App = "";
    public ImageSource? Art;
    public TimeSpan Position;
    public TimeSpan Duration;
    public DateTime PositionAt;

    public TimeSpan LivePosition
    {
        get
        {
            var p = Playing ? Position + (DateTime.Now - PositionAt) : Position;
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }
}

/// <summary>Now playing, through Windows' media controls (Spotify, browsers, Media Player...).</summary>
public sealed class MediaService : IDisposable
{
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    string? _sessionApp;
    string? _artKey;
    ImageSource? _art;
    Timer? _poll;
    readonly SemaphoreSlim _gate = new(1, 1);

    public event Action<MediaInfo>? Changed;
    public MediaInfo Current { get; private set; } = new();

    public async Task StartAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => _ = RefreshAsync();
            _mgr.SessionsChanged += (_, _) => _ = RefreshAsync();
            await RefreshAsync();
            _poll = new Timer(_ => _ = RefreshAsync(), null, 3000, 3000);
        }
        catch (Exception ex) { Settings.Log(ex); }
    }

    public void Dispose() => _poll?.Dispose();

    async Task RefreshAsync()
    {
        if (_mgr == null) return;
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            var sessions = _mgr.GetSessions();
            var pick = sessions.FirstOrDefault(IsPlaying) ?? _mgr.GetCurrentSession() ?? sessions.FirstOrDefault();
            var app = pick?.SourceAppUserModelId;
            if (app != _sessionApp)
            {
                if (_session != null)
                {
                    _session.MediaPropertiesChanged -= OnSessionChanged;
                    _session.PlaybackInfoChanged -= OnSessionChanged;
                    _session.TimelinePropertiesChanged -= OnSessionChanged;
                }
                _session = pick;
                _sessionApp = app;
                if (_session != null)
                {
                    _session.MediaPropertiesChanged += OnSessionChanged;
                    _session.PlaybackInfoChanged += OnSessionChanged;
                    _session.TimelinePropertiesChanged += OnSessionChanged;
                }
            }
            await UpdateAsync();
        }
        catch (Exception ex) { Settings.Log(ex); }
        finally { _gate.Release(); }
    }

    void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshAsync();

    static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    async Task UpdateAsync()
    {
        var s = _session;
        var info = new MediaInfo();
        if (s != null)
        {
            info.HasSession = true;
            info.App = PrettyApp(s.SourceAppUserModelId);
            info.Playing = IsPlaying(s);
            try
            {
                var p = await s.TryGetMediaPropertiesAsync();
                info.Title = p?.Title ?? "";
                info.Artist = p?.Artist ?? "";
                info.Album = p?.AlbumTitle ?? "";
                var key = info.App + "|" + info.Title + "|" + info.Artist;
                if (key != _artKey)
                {
                    _artKey = key;
                    _art = p?.Thumbnail != null ? await LoadArtAsync(p.Thumbnail) : null;
                }
                info.Art = _art;
            }
            catch { }
            try
            {
                var tl = s.GetTimelineProperties();
                info.Position = tl.Position;
                info.Duration = tl.EndTime - tl.StartTime;
                info.PositionAt = tl.LastUpdatedTime.LocalDateTime;
                if (info.PositionAt.Year < 2000 || info.PositionAt > DateTime.Now) info.PositionAt = DateTime.Now;
            }
            catch { info.PositionAt = DateTime.Now; }
            if (string.IsNullOrEmpty(info.Title)) info.HasSession = info.Playing;
        }
        Current = info;
        Changed?.Invoke(info);
    }

    static async Task<ImageSource?> LoadArtAsync(Windows.Storage.Streams.IRandomAccessStreamReference r)
    {
        try
        {
            using var ras = await r.OpenReadAsync();
            using var st = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await st.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 240;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    static string PrettyApp(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var l = id.ToLowerInvariant();
        if (l.Contains("spotify")) return "Spotify";
        if (l.Contains("zunemusic") || l.Contains("media player")) return "Media Player";
        if (l.Contains("msedge")) return "Edge";
        if (l.Contains("chrome")) return "Chrome";
        if (l.Contains("firefox")) return "Firefox";
        if (l.Contains("vlc")) return "VLC";
        if (l.Contains("youtube")) return "YouTube";
        var n = id.Split('!')[0].Split('\\').Last();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        return n.Length > 18 ? n[..18] : n;
    }

    public async void TogglePlayPause()
    {
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async void Next()
    {
        try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
    }

    public async void Previous()
    {
        try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
    }
}
