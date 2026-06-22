using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fragment.Models;
using Fragment.Services;
using Microsoft.Win32;

namespace Fragment.Views;

/// <summary>
/// Basic non-linear editor: an ordered list of <see cref="EditorSegment"/> shown as a filmstrip
/// timeline. Cut by splitting/deleting segments, merge by adding clips, reorder, then export (MP4 or
/// GIF) with resize / target-size / audio options via <see cref="VideoEditorService"/>. The preview
/// shows the currently selected segment's source.
/// </summary>
public partial class EditorWindow : Window
{
    private const int ThumbHeight = 60;
    private const double ThumbSpacingPx = 56; // target ~one filmstrip frame per this many pixels of width
    private const int MinThumbs = 2;
    private const int MaxThumbs = 60;
    private const double SplitEdgeMargin = 0.1; // seconds; keep splits clear of a segment's very edges

    private readonly VideoEditorService _editor;
    private readonly DispatcherTimer _tick;

    private readonly List<EditorSegment> _segments = new();
    private readonly HashSet<EditorSegment> _thumbsInFlight = new();
    private readonly List<(double left, double width, int index)> _blocks = new();
    private readonly HashSet<MediaPlayer> _probes = new(); // root probe players until they open/fail

    private int _selected = -1;
    private double _localSec;        // playhead time within the selected segment's SOURCE
    private string? _loadedSource;   // file currently in the preview MediaElement
    private double _pendingSeekSec;
    private double _primaryW, _primaryH;

    private bool _mediaReady, _isPlaying, _busy;
    private System.Windows.Shapes.Rectangle? _playLine;

    // This window's private thumbnail folder, so closing it never deletes another window's thumbs.
    private readonly string _thumbRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Fragment", "thumbs", Guid.NewGuid().ToString("N"));

    public EditorWindow(VideoEditorService editor, string? initialInputPath = null)
    {
        InitializeComponent();
        Fragment.Services.NativeTheme.ApplyDarkTitleBar(this);
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _tick.Tick += OnTick;
        _tick.Start();

        Loaded += (_, _) => { RebuildTimeline(); UpdateExportEnabled(); };

        if (!string.IsNullOrWhiteSpace(initialInputPath) && File.Exists(initialInputPath))
        {
            OpenPrimary(initialInputPath!);
        }
    }

    // ------------------------------------------------------------- load / add
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select a video",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.flv|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true) OpenPrimary(dlg.FileName);
    }

    private void OnAddClipClick(object sender, RoutedEventArgs e)
    {
        if (_segments.Count == 0) { OnBrowseClick(sender, e); return; }
        var dlg = new OpenFileDialog
        {
            Title = "Add a clip to the end",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.flv|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true) ProbeAndAdd(dlg.FileName, makePrimary: false);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasDroppableFile(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var file = Array.Find(files, File.Exists);
            if (file is not null)
            {
                if (_segments.Count == 0) OpenPrimary(file);
                else ProbeAndAdd(file, makePrimary: false);
            }
        }
    }

    private static bool HasDroppableFile(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop)
           && e.Data.GetData(DataFormats.FileDrop) is string[] files && Array.Exists(files, File.Exists);

    private void OpenPrimary(string path)
    {
        _segments.Clear();
        _thumbsInFlight.Clear();
        _selected = -1;
        _primaryW = _primaryH = 0;
        _loadedSource = null;
        try { Media.Stop(); Media.Close(); } catch { }
        // Start a fresh clip un-rotated: a manual rotation left over from a previous clip would otherwise
        // silently rotate this one on export (the bug behind "export came out sideways but preview looked
        // right"). Setting the box fires OnRotateChanged → resets the preview transform too.
        if (RotateBox != null) RotateBox.SelectedIndex = 0;
        InputBox.Text = path;
        StatusText.Text = "Loading…";
        ProbeAndAdd(path, makePrimary: true);
    }

    /// <summary>Reads duration/size with a lightweight off-screen MediaPlayer, then appends a segment.</summary>
    private async void ProbeAndAdd(string path, bool makePrimary)
    {
        // Probe via FFMPEG (not WPF MediaPlayer): ffmpeg reads any format it supports (.mov/.mkv/.webm/HEVC/…),
        // gives the rotation-correct display size for the canvas, and the duration — so the clip loads even when
        // Windows lacks a codec to PREVIEW it. The preview (loaded by SelectSegment below) is best-effort; if it
        // can't play the format the clip is still added and exports fine.
        StatusText.Text = "Reading video…";
        var info = await System.Threading.Tasks.Task.Run(() => _editor.ProbeInfo(path));
        if (info is not { } x || x.DurationSec <= 0.01)
        {
            StatusText.Text = "Couldn't read that video file (unsupported or corrupt).";
            return;
        }

        if (makePrimary && _primaryW <= 0 && x.Width > 0) { _primaryW = x.Width; _primaryH = x.Height; }

        _segments.Add(new EditorSegment(path, 0, x.DurationSec));
        UpdateTotals();
        SelectSegment(_segments.Count - 1); // loads the preview (WPF, best-effort)
        StatusText.Text = makePrimary
            ? "Loaded — cut with Split/Delete, add clips to merge, then Export."
            : "Clip added.";
    }

    // ------------------------------------------------------------- preview
    private void SelectSegment(int index, double? localSec = null)
    {
        if (index < 0 || index >= _segments.Count)
        {
            _selected = -1;
            RebuildTimeline();
            UpdateSegInfo();
            return;
        }

        _selected = index;
        var seg = _segments[index];
        _localSec = Clamp(localSec ?? seg.InSec, seg.InSec, seg.OutSec);
        LoadPreviewSource(seg.SourceFile, _localSec);
        RebuildTimeline();
        UpdateSegInfo();
    }

    private void LoadPreviewSource(string file, double seekSec)
    {
        if (_loadedSource == file && _mediaReady)
        {
            try { Media.Position = ToTs(seekSec); } catch { }
            UpdatePlayhead();
            return;
        }

        _loadedSource = file;
        _mediaReady = false;
        _isPlaying = false;
        _pendingSeekSec = seekSec;
        PlayButton.IsEnabled = false;
        PlayButton.Content = "▶ Play";
        PreviewHint.Text = "Loading…";
        PreviewHint.Visibility = Visibility.Visible;
        try { Media.Stop(); Media.Source = new Uri(file); Media.Play(); }
        catch (Exception ex) { StatusText.Text = $"Could not preview: {ex.Message}"; }
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        Media.Pause();
        _isPlaying = false;
        _mediaReady = true;
        if (_primaryW <= 0 && Media.NaturalVideoWidth > 0) { _primaryW = Media.NaturalVideoWidth; _primaryH = Media.NaturalVideoHeight; }
        try { Media.Position = ToTs(_pendingSeekSec); } catch { }
        _localSec = _pendingSeekSec;
        PreviewHint.Visibility = Visibility.Collapsed;
        PlayButton.IsEnabled = true;
        PlayButton.Content = "▶ Play";
        // Re-apply the manual-rotation transform to the freshly opened clip so the preview always shows the
        // exact orientation that will be exported (otherwise a non-None Rotate could go unreflected here).
        UpdatePreviewRotation();
        UpdatePlayhead();
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _mediaReady = false;
        PreviewHint.Text = "Preview unavailable for this file (codec not supported) — editing/export still work.";
        PreviewHint.Visibility = Visibility.Visible;
        PlayButton.IsEnabled = false;
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        Media.Pause();
        _isPlaying = false;
        PlayButton.Content = "▶ Play";
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady || _selected < 0) return;
        var seg = _segments[_selected];

        if (_isPlaying)
        {
            Media.Pause();
            _isPlaying = false;
            PlayButton.Content = "▶ Play";
        }
        else
        {
            if (_localSec < seg.InSec || _localSec >= seg.OutSec - 0.05)
            {
                _localSec = seg.InSec;
                try { Media.Position = ToTs(seg.InSec); } catch { }
            }
            Media.Play();
            _isPlaying = true;
            PlayButton.Content = "⏸ Pause";
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_isPlaying || !_mediaReady || _selected < 0) return;
        var seg = _segments[_selected];
        _localSec = Media.Position.TotalSeconds;
        if (_localSec >= seg.OutSec)
        {
            Media.Pause();
            _isPlaying = false;
            _localSec = seg.OutSec;
            try { Media.Position = ToTs(seg.OutSec); } catch { }
            PlayButton.Content = "▶ Play";
        }
        UpdatePlayhead();
    }

    // ------------------------------------------------------------- timeline
    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e) => RebuildTimeline();

    private void OnTimelineMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_segments.Count == 0) return;
        double x = e.GetPosition(SequenceCanvas).X;
        foreach (var b in _blocks)
        {
            if (x >= b.left && x <= b.left + b.width)
            {
                var seg = _segments[b.index];
                double frac = b.width > 0 ? (x - b.left) / b.width : 0;
                // Cap just below 1 so a right-edge click lands inside the segment (not exactly at Out,
                // which would make playback reset immediately).
                SelectSegment(b.index, seg.InSec + Clamp(frac, 0, 0.999) * seg.Duration);
                return;
            }
        }
    }

    private void RebuildTimeline()
    {
        SequenceCanvas.Children.Clear();
        _blocks.Clear();
        _playLine = null;

        double w = SequenceCanvas.ActualWidth, h = SequenceCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double total = TotalDuration();
        if (_segments.Count == 0 || total <= 0) return;

        const double gap = 2;
        double usable = Math.Max(1, w - gap * (_segments.Count - 1));
        double x = 0;

        for (int i = 0; i < _segments.Count; i++)
        {
            var seg = _segments[i];
            // Strictly proportional (min 1px) so the blocks always sum to the canvas width — a larger
            // per-block minimum would overflow the canvas and desync hit-testing/playhead from _blocks.
            double bw = Math.Max(1, seg.Duration / total * usable);

            var border = new Border
            {
                Width = bw,
                Height = h,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(i == _selected ? 2 : 1),
                BorderBrush = Brush(i == _selected ? "AccentBrush" : "BorderBrush"),
                Background = Brush("ControlBackgroundBrush"),
                ClipToBounds = true,
                IsHitTestVisible = false, // clicks handled at the canvas level
            };

            // Tile whatever thumbnails we already have across the block (so it's not blank while more
            // are generated), then request (re)generation if this block is wide enough to want more.
            if (seg.Thumbnails.Count > 0)
            {
                var strip = new StackPanel { Orientation = Orientation.Horizontal };
                double iw = bw / seg.Thumbnails.Count;
                if (iw > 0)
                {
                    foreach (var tp in seg.Thumbnails)
                    {
                        var src = TryLoadBitmap(tp);
                        if (src != null)
                            strip.Children.Add(new Image { Width = iw, Height = h, Stretch = Stretch.UniformToFill, Source = src });
                    }
                }
                border.Child = strip;
            }

            int want = Math.Clamp((int)Math.Round(bw / ThumbSpacingPx), MinThumbs, MaxThumbs);
            if (seg.Thumbnails.Count < want)
            {
                RequestThumbnails(seg, want);
            }

            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, 0);
            SequenceCanvas.Children.Add(border);
            _blocks.Add((x, bw, i));
            x += bw + gap;
        }

        _playLine = new System.Windows.Shapes.Rectangle { Width = 2, Height = h, Fill = Brush("ForegroundBrush"), IsHitTestVisible = false };
        SequenceCanvas.Children.Add(_playLine);
        UpdatePlayhead();
    }

    private async void RequestThumbnails(EditorSegment seg, int count)
    {
        if (_thumbsInFlight.Contains(seg)) return;
        _thumbsInFlight.Add(seg);
        try
        {
            var thumbs = await _editor.GenerateFilmstripAsync(seg.SourceFile, seg.InSec, seg.Duration, count, ThumbHeight, _thumbRoot);
            if (thumbs.Count > 0)
            {
                seg.Thumbnails.Clear();
                seg.Thumbnails.AddRange(thumbs);
                if (_segments.Contains(seg)) RebuildTimeline();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Filmstrip generation failed for {seg.SourceFile}: {ex.Message}");
        }
        finally { _thumbsInFlight.Remove(seg); }
    }

    private void UpdatePlayhead()
    {
        PositionRun.Text = Fmt(_localSec);
        if (_playLine != null)
        {
            Canvas.SetLeft(_playLine, PlayheadX() - 1);
            Canvas.SetTop(_playLine, 0);
        }
    }

    private double PlayheadX()
    {
        if (_selected < 0) return -10;
        foreach (var b in _blocks)
        {
            if (b.index != _selected) continue;
            var seg = _segments[_selected];
            double frac = seg.Duration > 0 ? (_localSec - seg.InSec) / seg.Duration : 0;
            return b.left + Clamp(frac, 0, 1) * b.width;
        }
        return -10;
    }

    // ------------------------------------------------------------- segment ops
    private void OnSplitClick(object sender, RoutedEventArgs e)
    {
        if (_selected < 0) { StatusText.Text = "Select a segment first (click it on the timeline)."; return; }
        var seg = _segments[_selected];
        double cut = _localSec;
        double margin = Math.Min(SplitEdgeMargin, seg.Duration * 0.25); // shrink for very short segments
        if (cut < seg.InSec + margin || cut > seg.OutSec - margin)
        {
            StatusText.Text = "Move the playhead further inside the segment, then Split.";
            return;
        }

        int at = _selected;
        _segments.RemoveAt(at);
        _segments.Insert(at, new EditorSegment(seg.SourceFile, cut, seg.OutSec));
        _segments.Insert(at, new EditorSegment(seg.SourceFile, seg.InSec, cut));
        // Re-select the left half through the normal path so the playhead is re-clamped and the preview
        // resyncs (a bare _localSec assignment would leave playback state stale at the cut point).
        SelectSegment(at, cut);
        StatusText.Text = "Split.";
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_selected < 0) { StatusText.Text = "Select a segment to delete."; return; }
        if (_segments.Count == 1)
        {
            StatusText.Text = "Can't delete the only segment — open a different video instead.";
            return;
        }

        _segments.RemoveAt(_selected);
        int next = Math.Min(_selected, _segments.Count - 1);
        UpdateTotals();
        SelectSegment(next);
        StatusText.Text = "Segment deleted.";
    }

    private void OnMoveLeftClick(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveRightClick(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int dir)
    {
        if (_selected < 0) return;
        int target = _selected + dir;
        if (target < 0 || target >= _segments.Count) return;
        (_segments[_selected], _segments[target]) = (_segments[target], _segments[_selected]);
        // Re-select so the playhead is re-clamped and the preview reloads the (possibly different) source.
        SelectSegment(target);
    }

    // ------------------------------------------------------------- export options UI
    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var fmt = SelectedFormat();
        bool gif = fmt == EditorOutputFormat.Gif;
        AudioPanel.IsEnabled = !gif;            // every video container keeps audio; GIF can't
        SizePanel.IsEnabled = !gif;
        FastCopyCheck.IsEnabled = fmt == EditorOutputFormat.Mp4;   // stream-copy cut is MP4-only
    }

    private EditorOutputFormat SelectedFormat() => FormatBox.SelectedIndex switch
    {
        1 => EditorOutputFormat.Mov,
        2 => EditorOutputFormat.Mkv,
        3 => EditorOutputFormat.Webm,
        4 => EditorOutputFormat.Gif,
        _ => EditorOutputFormat.Mp4,
    };

    private static string ExtFor(EditorOutputFormat f) => f switch
    {
        EditorOutputFormat.Mov => ".mov",
        EditorOutputFormat.Mkv => ".mkv",
        EditorOutputFormat.Webm => ".webm",
        EditorOutputFormat.Gif => ".gif",
        _ => ".mp4",
    };

    private void OnAudioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        VolumeSlider.Visibility = AudioBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ExportOptions BuildOptions()
    {
        var o = new ExportOptions
        {
            Format = SelectedFormat(),
            OutFps = 60,
            GifFps = 15,
            FastCopy = FastCopyCheck.IsChecked == true,
            AudioMode = AudioBox.SelectedIndex switch { 1 => EditorAudioMode.Mute, 2 => EditorAudioMode.Volume, _ => EditorAudioMode.Keep },
            Volume = VolumeSlider.Value,
            TargetSizeMb = SizeBox.SelectedIndex switch { 1 => 10.0, 2 => 50.0, 3 => 25.0, _ => (double?)null },
        };

        int outH = ResolutionBox.SelectedIndex switch { 1 => 1080, 2 => 720, 3 => 480, _ => 0 };
        if (outH == 0)
        {
            o.OutWidth = _primaryW > 0 ? (int)_primaryW : 1920;
            o.OutHeight = _primaryH > 0 ? (int)_primaryH : 1080;
        }
        else
        {
            double aspect = (_primaryW > 0 && _primaryH > 0) ? _primaryW / _primaryH : 16.0 / 9.0;
            o.OutHeight = outH;
            o.OutWidth = (int)Math.Round(outH * aspect);
        }

        // Manual rotation (fallback if a clip comes out the wrong way). 90/270 also swaps the canvas so the
        // rotated frame fills it without bars.
        o.RotateDegrees = RotateBox.SelectedIndex switch { 1 => 90, 2 => 180, 3 => 270, _ => 0 };
        if (o.RotateDegrees is 90 or 270)
            (o.OutWidth, o.OutHeight) = (o.OutHeight, o.OutWidth);
        return o;
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_segments.Count == 0) { StatusText.Text = "Nothing to export — open a video first."; return; }

        // Claim the busy flag immediately (before any await) so a rapid second click can't queue a
        // duplicate export.
        SetBusy(true);
        try
        {
            Media.Pause();
            _isPlaying = false;
            PlayButton.Content = "▶ Play";

            var opts = BuildOptions();
            string outPath = BuildOutputPath(ExtFor(opts.Format));
            var snapshot = _segments.Select(s => s.Clone()).ToList();

            string phase = "Rendering…";
            var progress = new Progress<string>(m => { phase = m; StatusText.Text = m; });
            var pct = new Progress<double>(f =>
            {
                ExportProgress.IsIndeterminate = false;       // a real % arrived → switch from the spinner
                ExportProgress.Value = f;
                StatusText.Text = $"{phase} {f * 100:0}%";
            });
            await _editor.ExportAsync(snapshot, opts, outPath, progress, pct);
            StatusText.Text = $"Saved: {outPath}";
            NotificationService.ShowClipSaved(outPath);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Export failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        // Show the export progress bar while busy: indeterminate (spinner) during the analysis/fast-copy
        // phase, then it switches to a real 0..100% fill once ffmpeg starts reporting render progress.
        ExportProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ExportProgress.IsIndeterminate = busy;
        ExportProgress.Value = 0;
        UpdateExportEnabled();
    }

    private void UpdateExportEnabled() => ExportButton.IsEnabled = !_busy && _segments.Count > 0;

    // Rotate the preview to match the selected manual rotation, so the user sees what they'll get on export.
    private void OnRotateChanged(object sender, SelectionChangedEventArgs e) => UpdatePreviewRotation();
    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewRotation();

    private void UpdatePreviewRotation()
    {
        if (Media is null || RotateBox is null) return;
        int angle = RotateBox.SelectedIndex switch { 1 => 90, 2 => 180, 3 => 270, _ => 0 };
        if (angle == 0) { Media.RenderTransform = Transform.Identity; return; }

        // For a quarter-turn the element becomes H×W; scale it down to fit its original W×H slot (no clipping).
        double w = Media.ActualWidth, h = Media.ActualHeight;
        double scale = (angle == 90 || angle == 270) && w > 0 && h > 0 ? Math.Min(w, h) / Math.Max(w, h) : 1.0;
        var g = new TransformGroup();
        g.Children.Add(new ScaleTransform(scale, scale));
        g.Children.Add(new RotateTransform(angle));
        Media.RenderTransform = g;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        try { _tick.Tick -= OnTick; _tick.Stop(); } catch { }
        try { Media.Stop(); Media.Close(); } catch { }
        // Close any probe players still awaiting MediaOpened/MediaFailed so they don't leak native media
        // resources when the window is closed mid-probe.
        foreach (var p in _probes.ToArray()) { try { p.Close(); } catch { } }
        _probes.Clear();
        // Delete only THIS window's thumbnail folder so other editor windows keep theirs.
        try { if (Directory.Exists(_thumbRoot)) Directory.Delete(_thumbRoot, recursive: true); } catch { }
        base.OnClosed(e);
    }

    // ------------------------------------------------------------- helpers
    private double TotalDuration()
    {
        double t = 0;
        foreach (var s in _segments) t += s.Duration;
        return t;
    }

    private void UpdateTotals() => TotalRun.Text = Fmt(TotalDuration());

    private void UpdateSegInfo()
    {
        if (_selected >= 0 && _selected < _segments.Count)
        {
            var seg = _segments[_selected];
            SegInfo.Text = $"Segment {_selected + 1} of {_segments.Count}  •  {Fmt(seg.Duration)}";
        }
        else
        {
            SegInfo.Text = _segments.Count > 0 ? $"{_segments.Count} segment(s)" : "";
        }
        UpdateExportEnabled();
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    private static BitmapImage? TryLoadBitmap(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // load fully so the temp file isn't locked
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private string BuildOutputPath(string ext)
    {
        string baseFile = _segments.Count > 0 ? _segments[0].SourceFile : "";
        string dir = Path.GetDirectoryName(baseFile) ?? "";
        if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string name = string.IsNullOrEmpty(baseFile) ? "Fragment_edit" : Path.GetFileNameWithoutExtension(baseFile) + "_edit";

        string candidate = Path.Combine(dir, name + ext);
        int i = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            i++;
        }
        return candidate;
    }

    private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

    private static TimeSpan ToTs(double seconds) => TimeSpan.FromSeconds(seconds < 0 ? 0 : seconds);

    private static string Fmt(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }
}
