using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Island;

public partial class MainWindow
{
    readonly double[] _segTargets = { -1, -1, -1, -1 };

    void OnClaude(ClaudeUpdate u)
    {
        bool wasWorking = ClaudeWorking;
        _cu = u;
        if (u.Finished is { } f)
        {
            ShowPop($"Done · {f.Project}", string.IsNullOrEmpty(f.Summary) ? "Claude finished the task." : f.Summary,
                "\uE73E", Green, null, Tab.Claude, 7);
            _popSession = f.SessionId;
        }
        else if (_mode == Mode.Pop && _popSession != null && u.Primary is { } p && p.SessionId == _popSession && !string.IsNullOrEmpty(p.Summary))
        {
            PopText.Text = p.Summary;
        }
        if (!wasWorking && ClaudeWorking && _mode is Mode.Collapsed or Mode.Live) Bounce();
        RefreshClaudeTile();
        if (_mode == Mode.Expanded && _tab == Tab.Claude) RefreshClaude();
        UpdateIndicators();
    }

    void RefreshClaudeTile()
    {
        var p = _cu?.Primary;
        int jobs = _cu?.JobsToday ?? 0;
        if (p is { Working: true })
        {
            ClaudeTileBig.Text = Fmt.Clock(DateTime.Now - p.TurnStart);
            ClaudeTileSub.Text = p.Activity;
        }
        else
        {
            ClaudeTileBig.Text = jobs.ToString();
            ClaudeTileSub.Text = jobs == 1 ? "job today" : "jobs today";
        }
        double ctx = p?.Context ?? 0;
        if (p is not { Working: true }) ClaudeRing.Progress = ctx;
        ClaudeRingText.Text = $"{Math.Round(ctx * 100)}%";
        TileClaudeStop.Color = p is { Working: true } ? C(0xC4, 0x6C, 0x3B)
            : ClaudeJustDone ? Mix(C(0xB5, 0x69, 0x3F), C(0x2E, 0x9B, 0x55), DoneFade)
            : C(0xB5, 0x69, 0x3F);
    }

    void RefreshClaude()
    {
        var u = _cu;
        var p = u?.Primary;
        if (p == null)
        {
            ClaudeProject.Text = "No session yet";
            ClaudeProject.ToolTip = null;
            ClaudeElapsed.Text = "0:00";
            ClaudeStatus.Text = "Start Claude Code in a terminal and it shows up here.";
            CardText("Island follows Claude Code's own session files on this PC. There is nothing to set up.", false);
            SetSegments(ClaudeStage.None, false);
            ClaudeOthers.Visibility = Visibility.Collapsed;
            return;
        }

        ClaudeProject.Text = p.Project;
        ClaudeProject.ToolTip = string.IsNullOrEmpty(p.Title) ? p.Cwd : $"{p.Title}\n{p.Cwd}";
        var end = p.Working ? DateTime.Now : p.TurnEnd ?? p.LastEvent;
        ClaudeElapsed.Text = p.TurnStart == default ? "0:00" : Fmt.Clock(end - p.TurnStart);

        if (p.Working)
            ClaudeStatus.Text = p.Stage == ClaudeStage.None ? p.Activity : $"{p.Activity} · step {Math.Clamp((int)p.Stage, 1, 3)} of 4";
        else if (p.Stage == ClaudeStage.Done)
            ClaudeStatus.Text = ClaudeJustDone ? "Done · every step" : $"Done · {Fmt.Ago(p.TurnEnd ?? p.LastEvent)}";
        else
            ClaudeStatus.Text = p.Activity == "Stopped" ? "Stopped" : $"Idle · {Fmt.Ago(p.LastEvent)}";

        if (p.Working && p.File != null && (p.DiffNew != null || p.DiffOld != null || p.FileVerb == "being read"))
        {
            ClaudeFileRow.Visibility = Visibility.Visible;
            ClaudeCardText.Visibility = Visibility.Collapsed;
            var ext = Path.GetExtension(p.File).TrimStart('.').ToUpperInvariant();
            ClaudeExt.Text = ext.Length == 0 ? "FILE" : ext.Length > 4 ? ext[..4] : ext;
            ClaudeFile.Text = Path.GetFileName(p.File);
            ClaudeFile.ToolTip = p.File;
            ClaudeFileVerb.Text = p.FileVerb ?? "";
            DiffOldRow.Visibility = Vis(p.DiffOld != null);
            DiffNewRow.Visibility = Vis(p.DiffNew != null);
            DiffOld.Text = "−  " + p.DiffOld;
            DiffNew.Text = "+  " + p.DiffNew;
        }
        else if (p.Working && !string.IsNullOrEmpty(p.Command))
        {
            CardText("$ " + p.Command, true);
        }
        else if (!p.Working && !string.IsNullOrEmpty(p.Summary))
        {
            CardText(p.Summary, false);
        }
        else if (!string.IsNullOrEmpty(p.Prompt))
        {
            CardText("“" + p.Prompt + "”", false);
        }
        else
        {
            CardText(p.Working ? "Thinking it through…" : "Waiting for the next task.", false);
        }

        SetSegments(p.Stage, p.Working);
        var others = u!.Active.Where(a => a.SessionId != p.SessionId).ToList();
        ClaudeOthers.Visibility = Vis(others.Count > 0);
        if (others.Count == 1) ClaudeOthers.Text = $"Also running: {others[0].Project} · {others[0].Activity}";
        else if (others.Count > 1) ClaudeOthers.Text = $"{others.Count} more sessions running";
    }

    void CardText(string text, bool mono)
    {
        ClaudeFileRow.Visibility = Visibility.Collapsed;
        DiffOldRow.Visibility = Visibility.Collapsed;
        DiffNewRow.Visibility = Visibility.Collapsed;
        ClaudeCardText.Visibility = Visibility.Visible;
        ClaudeCardText.Text = text;
        ClaudeCardText.FontFamily = (FontFamily)FindResource(mono ? "MonoFont" : "UiFont");
        ClaudeCardText.FontSize = mono ? 12 : 13;
        ClaudeCardText.FlowDirection = Fmt.IsRtl(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    void SetSegments(ClaudeStage stage, bool working)
    {
        var segs = new[] { Seg0, Seg1, Seg2, Seg3 };
        var labels = new[] { SegLbl0, SegLbl1, SegLbl2, SegLbl3 };
        int s = (int)stage;
        for (int i = 0; i < 4; i++)
        {
            double target = stage == ClaudeStage.Done || s >= i + 2 ? 1 : s == i + 1 ? (working ? 0.55 : 1) : 0;
            labels[i].Foreground = target > 0 ? Brushes.White : Paint.Brush(0x80, 255, 255, 255);
            if (Math.Abs(_segTargets[i] - target) < 0.01) continue;
            _segTargets[i] = target;
            segs[i].BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    void TileClaude_Click(object sender, MouseButtonEventArgs e) => SelectTab(Tab.Claude);

    void FolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var cwd = _cu?.Primary?.Cwd;
        if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) OpenPath(cwd);
    }
}
