using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Island;

public partial class MainWindow
{
    Brush? _musicArtDefault;
    bool _volumeOpen, _volumeSync;

    void OnMedia(MediaInfo m)
    {
        _mi = m;
        UpdateLyrics();
        RefreshMusicTile();
        if (_mode == Mode.Expanded && _tab == Tab.Music) RefreshMusic();
        UpdateHeader();
        UpdateIndicators();
    }

    void RefreshMusicTile()
    {
        if (!_mi.HasSession)
        {
            MusicTileBig.Text = "Spotify";
            MusicTileSub.Text = "Connect your music";
            TileMusicArt.Background = Paint.Brush(0x40, 0, 0, 0);
            TileMusicGlyph.Visibility = Visibility.Visible;
            TilePlay.Visibility = Visibility.Collapsed;
            return;
        }
        MusicTileBig.Text = string.IsNullOrEmpty(_mi.Title) ? _mi.App : _mi.Title;
        MusicTileSub.Text = string.IsNullOrEmpty(_mi.Artist) ? _mi.App : _mi.Artist;
        if (_mi.Art != null)
        {
            TileMusicArt.Background = new ImageBrush(_mi.Art) { Stretch = Stretch.UniformToFill };
            TileMusicGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            TileMusicArt.Background = Paint.Brush(0x40, 0, 0, 0);
            TileMusicGlyph.Visibility = Visibility.Visible;
        }
        TilePlay.Visibility = Visibility.Visible;
        TilePlayGlyph.Text = _mi.Playing ? "\uE769" : "\uE768";
    }

    void RefreshMusic()
    {
        MusicApp.Text = _mi.App.ToUpperInvariant();
        RefreshLyricsUi();
        bool has = _mi.HasSession;
        MusicOpen.Visibility = Vis(!has);
        MusicPrev.IsEnabled = MusicNext.IsEnabled = MusicPlay.IsEnabled = has;
        if (!has)
        {
            MusicTitle.Text = "Nothing playing";
            MusicArtist.Text = "Play something in Spotify, a browser or any player.";
            MusicArt.Background = _musicArtDefault;
            MusicArtGlyph.Visibility = Visibility.Visible;
            MusicProgressRow.Visibility = MusicTimeRow.Visibility = Visibility.Collapsed;
            MusicPlayGlyph.Text = "\uE768";
            return;
        }
        MusicTitle.Text = string.IsNullOrEmpty(_mi.Title) ? _mi.App : _mi.Title;
        MusicArtist.Text = _mi.Artist;
        if (_mi.Art != null)
        {
            MusicArt.Background = new ImageBrush(_mi.Art) { Stretch = Stretch.UniformToFill };
            MusicArtGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            MusicArt.Background = _musicArtDefault;
            MusicArtGlyph.Visibility = Visibility.Visible;
        }
        MusicPlayGlyph.Text = _mi.Playing ? "\uE769" : "\uE768";
        bool timeline = _mi.Duration > TimeSpan.FromSeconds(1);
        MusicProgressRow.Visibility = MusicTimeRow.Visibility = Vis(timeline);
        if (timeline)
        {
            var pos = _mi.LivePosition;
            MusicProgress.ScaleX = Math.Clamp(pos.TotalSeconds / _mi.Duration.TotalSeconds, 0, 1);
            MusicPos.Text = Fmt.Clock(pos);
            MusicDur.Text = Fmt.Clock(_mi.Duration);
        }
        ScheduleLyric(true);
    }

    void MusicPlay_Click(object sender, RoutedEventArgs e)
    {
        _media.TogglePlayPause();
        MusicPlayGlyph.Text = MusicPlayGlyph.Text == "\uE769" ? "\uE768" : "\uE769";
    }

    void TilePlay_Click(object sender, RoutedEventArgs e)
    {
        _media.TogglePlayPause();
        TilePlayGlyph.Text = TilePlayGlyph.Text == "\uE769" ? "\uE768" : "\uE769";
        e.Handled = true;
    }

    void MusicPrev_Click(object sender, RoutedEventArgs e) => _media.Previous();
    void MusicNext_Click(object sender, RoutedEventArgs e) => _media.Next();
    void MusicOpen_Click(object sender, RoutedEventArgs e) => OpenSpotify();

    void TileMusic_Click(object sender, MouseButtonEventArgs e)
    {
        if (_mi.HasSession) SelectTab(Tab.Music);
        else OpenSpotify();
    }

    static void OpenSpotify()
    {
        try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo("https://open.spotify.com") { UseShellExecute = true }); } catch { }
        }
    }

    // ---------- volume ----------

    void VolumeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_volumeOpen) CloseVolume();
        else OpenVolume();
    }

    void OpenVolume()
    {
        _volumeOpen = true;
        _volumeSync = true;
        VolumeSlider.Value = Math.Round(_audio.Volume * 100);
        _volumeSync = false;
        RefreshVolumeUi();
        Tabs.Visibility = Visibility.Collapsed;
        VolumeRow.Visibility = Visibility.Visible;
        Reveal(VolumeRow, false);
    }

    void CloseVolume()
    {
        if (!_volumeOpen) return;
        _volumeOpen = false;
        VolumeRow.Visibility = Visibility.Collapsed;
        Tabs.Visibility = Visibility.Visible;
        Reveal(Tabs, false);
    }

    void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeSync || !_volumeOpen) return;
        _audio.Volume = (float)(e.NewValue / 100);
        if (e.NewValue > 0 && _audio.Muted) _audio.Muted = false;
        RefreshVolumeUi();
    }

    void MuteBtn_Click(object sender, RoutedEventArgs e)
    {
        _audio.Muted = !_audio.Muted;
        RefreshVolumeUi();
    }

    void VolumeClose_Click(object sender, RoutedEventArgs e) => CloseVolume();

    void VolumeBtn_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var v = Math.Clamp(_audio.Volume + (e.Delta > 0 ? 0.02f : -0.02f), 0f, 1f);
        _audio.Volume = v;
        if (v > 0 && _audio.Muted) _audio.Muted = false;
        if (_volumeOpen)
        {
            _volumeSync = true;
            VolumeSlider.Value = v * 100;
            _volumeSync = false;
        }
        FlashNote($"Volume {Math.Round(v * 100)}%");
        RefreshVolumeUi();
        e.Handled = true;
    }

    void RefreshVolumeUi()
    {
        var v = _audio.Volume;
        var glyph = _audio.Muted || v < 0.01 ? "\uE74F" : "\uE767";
        MuteBtn.Content = glyph;
        VolumeBtn.Content = glyph;
        VolumeText.Text = $"{Math.Round(v * 100)}%";
    }
}
