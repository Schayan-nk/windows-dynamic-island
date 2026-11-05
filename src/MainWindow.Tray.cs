using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Island;

public partial class MainWindow
{
    PhoneShare? _share;
    List<(IPAddress Ip, string Name)> _nets = new();
    int _netIndex;
    bool _phoneSeen;
    Point _dragStart;
    TrayItem? _dragItem;

    void OnTrayChanged()
    {
        SaveTray();
        if (_mode == Mode.Expanded && _tab == Tab.Tray) RefreshTray();
        UpdateHeader();
    }

    void SaveTray()
    {
        if (ShotsMode) return;
        _settings.Tray = _tray.Items.Select(i => i.Path).ToList();
        _settings.Save();
    }

    bool _previewShare;

    void RefreshTray()
    {
        bool sharing = _share?.Running == true || _previewShare;
        TrayScroll.Visibility = Vis(!sharing);
        SharePanel.Visibility = Vis(sharing);
        if (_share?.Running == true) RefreshShare();
    }

    // ---------- tray items ----------

    void TrayItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = (sender as FrameworkElement)?.Tag as TrayItem;
        _dragStart = e.GetPosition(this);
        if (e.ClickCount == 2 && _dragItem != null)
        {
            OpenPath(_dragItem.Path);
            _dragItem = null;
        }
    }

    void TrayItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < 6 && Math.Abs(p.Y - _dragStart.Y) < 6) return;
        var item = _dragItem;
        _dragItem = null;
        _internalDrag = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex) { Settings.Log(ex); }
        finally
        {
            _internalDrag = false;
            if (!_hoverInside && _mode == Mode.Expanded) _collapseTimer.Start();
        }
    }

    void TrayItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TrayItem item) return;
        var m = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.MousePoint };
        m.Items.Add(Item("Open", () => OpenPath(item.Path)));
        m.Items.Add(Item("Show in folder", () => ShowInFolder(item.Path)));
        m.Items.Add(Item("Copy path", () =>
        {
            _ignoreClipUntil = DateTime.Now.AddMilliseconds(700);
            try { Clipboard.SetText(item.Path); } catch { }
        }));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Remove from the tray", () => _tray.Remove(item)));
        OpenMenu(m);
        e.Handled = true;
    }

    void TrayRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TrayItem item) _tray.Remove(item);
        e.Handled = true;
    }

    void TrayDropTile_Click(object sender, MouseButtonEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Add files to the tray" };
        _dialogOpen = true;
        try
        {
            if (dlg.ShowDialog(this) == true)
                foreach (var f in dlg.FileNames) _tray.Add(f);
        }
        finally { _dialogOpen = false; }
    }

    void TrayPhoneTile_Click(object sender, MouseButtonEventArgs e) => StartShare(0);

    void PhoneBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_share?.Running == true) StopShare();
        else StartShare(0);
    }

    void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_tab == Tab.Clip)
        {
            _clips.Clear();
            RefreshClips();
        }
        else _tray.Clear();
        UpdateHeader();
    }

    // ---------- phone ----------

    void StartShare(int netIndex)
    {
        _nets = PhoneShare.Candidates();
        if (_nets.Count == 0) { FlashNote("No network. Connect to Wi-Fi first."); return; }
        _netIndex = Math.Clamp(netIndex, 0, _nets.Count - 1);
        if (_share == null)
        {
            _share = new PhoneShare(() => _tray.Snapshot, () => _tray.Version,
                (path, device) => Dispatcher.InvokeAsync(() => OnPhoneFile(path, device)));
            _share.Changed += () => Dispatcher.InvokeAsync(OnShareChanged);
            _share.Progress += (name, f) => Dispatcher.InvokeAsync(() => OnPhoneProgress(name, f));
        }
        try { _share.Start(_nets[_netIndex].Ip, _nets[_netIndex].Name); }
        catch (Exception ex)
        {
            Settings.Log(ex);
            FlashNote("Could not open a network port");
            return;
        }
        QrImage.Source = Qr.Render(_share.Url);
        _phoneSeen = false;
        SelectTab(Tab.Tray);
        RefreshTray();
        UpdateHeader();
        UpdateTint(0.5);
        UpdateGlow();
    }

    void StopShare()
    {
        _share?.Stop();
        RefreshTray();
        UpdateHeader();
        UpdateTint(0.5);
        UpdateGlow();
        UpdateIndicators();
    }

    void OnShareChanged()
    {
        if (_share?.Device != null && !_phoneSeen)
        {
            _phoneSeen = true;
            if (_mode == Mode.Expanded && _tab == Tab.Tray) Bounce();
            else ShowPop($"{_share.Device} connected", "Send or save files on your phone.", "\uE8EA", Blue, null, Tab.Tray);
        }
        if (_mode == Mode.Expanded && _tab == Tab.Tray) RefreshTray();
        UpdateIndicators();
    }

    void OnPhoneFile(string path, string device)
    {
        _tray.Add(path, true);
        if (_mode == Mode.Progress) { ProgressMochi.BeginAnimation(Mascot.HappyProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(200))); }
        ShowPop($"Got {Path.GetFileName(path)}", $"From your {device}, saved in Downloads\\Island.", "\uE896", Blue, null, Tab.Tray);
    }

    void RefreshShare()
    {
        var s = _share;
        if (s == null) return;
        bool seen = s.Device != null;
        QrCheck.Visibility = Vis(seen);
        QrGlow.Color = seen ? Green : C(0x3B, 0x82, 0xF6);
        ShareTitle.Text = seen ? $"{s.Device} is connected" : "Scan with your phone";
        ShareText.Text = seen
            ? "Tap “Send to PC” on your phone. What is in the tray is on your phone already."
            : "Point your phone's camera at the code and tap the link. Same Wi-Fi only. If Windows asks, allow Island on private networks.";
        ShareDot.Fill = Paint.Brush(seen && s.Connected ? Green : seen ? Amber : C(0x8E, 0x8E, 0x93));
        ShareStatus.Text = !seen ? "Waiting for your phone…" : s.Connected ? "Connected" : "Phone is asleep or away";

        var files = _tray.Snapshot.Where(i => !i.IsDir).ToArray();
        ShareFilesRow.Visibility = Vis(files.Length > 0);
        if (files.Length > 0)
        {
            var first = files[0];
            ShareThumb.Background = first.Thumb != null
                ? new ImageBrush(first.Thumb) { Stretch = first.IsPhoto ? Stretch.UniformToFill : Stretch.Uniform }
                : Paint.Brush(0x26, 255, 255, 255);
            ShareFilesText.Text = files.Length == 1
                ? $"{first.Name} {(seen ? "is on your phone" : "is ready")}"
                : $"{files.Length} files {(seen ? "are on your phone" : "are ready")}";
        }
        var left = s.ExpiresAt - DateTime.Now;
        ShareFoot.Text = $"Same Wi-Fi only · ends in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min";
        ShareUrl.Text = $"{s.Address}:{s.Port} · {s.NetworkName}" + (_nets.Count > 1 ? "  ·  click to switch network" : "");
    }

    void TickShare()
    {
        if (_share?.Running != true) return;
        if (DateTime.Now > _share.ExpiresAt)
        {
            StopShare();
            FlashNote("The phone link has ended");
            return;
        }
        if (_mode == Mode.Expanded && _tab == Tab.Tray) RefreshShare();
    }

    void ShareStop_Click(object sender, RoutedEventArgs e) => StopShare();

    void ShareUrl_Click(object sender, MouseButtonEventArgs e)
    {
        if (_nets.Count > 1) StartShare((_netIndex + 1) % _nets.Count);
    }
}
