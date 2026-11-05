using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using FastEncoder.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace FastEncoder;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly ObservableCollection<JobItem> _jobs = [];
    private readonly AppSettings _cfg = AppSettings.Load();
    private EncodeSettings _enc;
    private EditSettings _edit;
    private string _presetId;

    private readonly IntPtr _hwnd;
    private readonly Taskbar _taskbar;
    private readonly MediaPlayer _mp = new() { AutoPlay = false };
    private readonly MediaPlayer _audio = new() { AutoPlay = false };   // soundtrack played shifted for the live sync preview
    private string? _audioSource;
    private DateTime _audioResync;
    private CancellationTokenSource? _detectCts;
    private readonly DispatcherQueueTimer _tick;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly HashSet<DispatcherQueueTimer> _tweens = [];
    private readonly SemaphoreSlim _probeGate = new(3);

    private JobItem? _current;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _estimateCts;
    private bool _ready, _encoding, _suppressUi, _sampleMode, _ffmpegOk, _closingConfirmed;
    private string? _samplePath;
    private string? _lastOutput;
    private bool _wasPlaying;

    private enum StripDrag { None, Seek, Start, End }
    private StripDrag _drag;
    private DateTime _lastSeek;

    private static readonly (string Label, int Value)[] Resolutions =
        [("Original", 0), ("2160p (4K)", 2160), ("1440p", 1440), ("1080p", 1080), ("720p", 720), ("480p", 480)];
    private static readonly (string Label, int Value)[] FpsCaps = [("Original", 0), ("60 fps", 60), ("30 fps", 30), ("24 fps", 24)];
    private static readonly (string Label, string Codec, int Kbps)[] Audios =
    [
        ("Opus 48 kbps", "opus", 48), ("Opus 64 kbps", "opus", 64), ("Opus 96 kbps", "opus", 96),
        ("Opus 128 kbps", "opus", 128), ("Opus 160 kbps", "opus", 160), ("AAC 128 kbps", "aac", 128),
        ("AAC 192 kbps", "aac", 192), ("Keep original", "copy", 0), ("No audio", "none", 0),
    ];
    private static readonly (string Label, string Value)[] Containers = [("MP4", "mp4"), ("MKV", "mkv")];

    public MainWindow()
    {
        _enc = _cfg.Encode;
        _edit = _cfg.Edit;
        _presetId = _cfg.PresetId;

        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _taskbar = new Taskbar(_hwnd);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "FastEncoder.ico"));
        PlaceWindow();

        QueueList.ItemsSource = _jobs;
        PresetGrid.ItemsSource = Presets.All;
        Player.SetMediaPlayer(_mp);
        _mp.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(OnMediaOpened);
        _mp.MediaFailed += (_, e) => DispatcherQueue.TryEnqueue(() => OnMediaFailed(e.ErrorMessage));
        _mp.PlaybackSession.PlaybackStateChanged += (_, _) => DispatcherQueue.TryEnqueue(() => { UpdatePlayGlyph(); SyncAudio(hard: true); });
        _mp.PlaybackSession.SeekCompleted += (_, _) => DispatcherQueue.TryEnqueue(() => SyncAudio(hard: true));
        _audio.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(() => { ApplyAudioRouting(); SyncAudio(hard: true); });

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(33);
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(700);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveNow();

        _jobs.CollectionChanged += (_, _) => UpdateQueueUi();
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) => Shutdown();
        Root.Loaded += Root_Loaded;

        RestoreUi();
        _ready = true;
    }

    // =====================================================================================
    //  startup / shutdown
    // =====================================================================================

    private void PlaceWindow()
    {
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min((int)(1500 * scale), area.Width - 32);
        int h = Math.Min((int)(940 * scale), area.Height - 32);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        if (AppWindow.Presenter is OverlappedPresenter op)
        {
            op.PreferredMinimumWidth = (int)(1180 * scale);
            op.PreferredMinimumHeight = (int)(720 * scale);
        }
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        TitleChips.Margin = new Thickness(0, 0, AppWindow.TitleBar.RightInset / scale + 8, 0);
        StartIdleAnimations();
        Root.Focus(FocusState.Programmatic);

        AddChip("Starting…", "SystemFillColorNeutralBrush", out var startChip);
        await Task.Run(Ff.Locate);
        _ffmpegOk = Ff.Available;
        TitleChips.Children.Remove(startChip);

        if (!_ffmpegOk)
        {
            AddChip("ffmpeg not found", "SystemFillColorCriticalBrush", out _);
            Toast("ffmpeg is missing", "FastEncoder needs ffmpeg. Install it with:  winget install Gyan.FFmpeg  — then restart.",
                  InfoBarSeverity.Error, 0);
            UpdateQueueUi();
            return;
        }

        await Task.Run(Encoders.DetectAsync);
        var gpu = Encoders.BestGpu;
        AddChip(gpu != null ? $"GPU · {gpu.Label.Split('·')[1].Replace("(GPU)", "").Trim()}" : "CPU encoding",
                gpu != null ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush", out _);
        if (Encoders.IsAvailable("svtav1")) AddChip("AV1 ready", "AccentFillColorDefaultBrush", out _);

        // Presets that depend on hardware (GPU Turbo, Fit to size) are rebuilt now that we know what's available.
        if (!Encoders.IsAvailable(_enc.Encoder) || _presetId is "turbo" or "target")
        {
            var keepMb = _enc.TargetMB;
            _enc = (Presets.Get(_presetId) ?? Presets.All[0]).Build();
            if (_enc.TargetSize) _enc.TargetMB = keepMb;
        }
        LoadFineTune();
        UpdateQueueUi();

        var args = Environment.GetCommandLineArgs().Skip(1).Where(a => File.Exists(a) || Directory.Exists(a)).ToList();
        if (args.Count > 0) AddPaths(args);
    }

    private void AddChip(string text, string brushKey, out Border chip)
    {
        var dot = new Ellipse { Width = 7, Height = 7, Fill = Brush(brushKey), VerticalAlignment = VerticalAlignment.Center };
        chip = new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 3, 10, 4),
            Background = Brush("ControlFillColorSecondaryBrush"),
            BorderBrush = Brush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { dot, new TextBlock { Text = text, FontSize = 12 } },
            },
        };
        TitleChips.Children.Add(chip);
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_encoding || _closingConfirmed) return;
        args.Cancel = true;
        var dlg = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Stop compressing?",
            Content = "A video is still being compressed. Quitting now discards the unfinished file.",
            PrimaryButtonText = "Stop and quit",
            CloseButtonText = "Keep going",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            _closingConfirmed = true;
            _runCts?.Cancel();
            await Task.Delay(300);
            Close();
        }
    }

    private void Shutdown()
    {
        SaveNow();
        _runCts?.Cancel();
        _estimateCts?.Cancel();
        _previewCts?.Cancel();
        _detectCts?.Cancel();
        foreach (var j in _jobs) j.Cts?.Cancel();
        _tick.Stop();
        _mp.Source = null;
        _mp.Dispose();
        _audio.Source = null;
        _audio.Dispose();
        _taskbar.Set(Taskbar.State.None);
        Task.Delay(400).ContinueWith(_ => Ff.CleanupTemp());
    }

    // =====================================================================================
    //  settings <-> UI
    // =====================================================================================

    private void RestoreUi()
    {
        _suppressUi = true;
        SpeedSlider.Value = _edit.Speed;
        VolumeSlider.Value = _edit.Volume * 100;
        MuteToggle.IsChecked = _edit.Mute;
        FixSyncToggle.IsChecked = _edit.FixSync;
        NextToSourceSwitch.IsOn = _cfg.SaveNextToSource;
        FolderBox.Text = _cfg.OutputFolder;
        SuffixBox.Text = _cfg.Suffix;
        DeleteBiggerCheck.IsChecked = _cfg.DeleteBiggerResults;
        ResolutionBox.ItemsSource = Resolutions.Select(r => r.Label).ToList();
        FpsBox.ItemsSource = FpsCaps.Select(r => r.Label).ToList();
        AudioBox.ItemsSource = Audios.Select(r => r.Label).ToList();
        ContainerBox.ItemsSource = Containers.Select(r => r.Label).ToList();
        PresetGrid.SelectedItem = Presets.Get(_presetId);
        _suppressUi = false;
        UpdateEditTexts();
        UpdateOutputUi();
        LoadFineTune();
    }

    private void LoadFineTune()
    {
        _suppressUi = true;
        var enc = Encoders.Get(_enc.Encoder);
        var list = Encoders.Available.ToList();
        if (!list.Contains(enc)) list.Insert(0, enc);
        EncoderBox.ItemsSource = list;
        EncoderBox.SelectedItem = enc;

        bool copy = enc.Family == Family.Copy;
        QualitySlider.Minimum = enc.QMin;
        QualitySlider.Maximum = Math.Max(enc.QMax, enc.QMin + 1);
        QualitySlider.Value = Math.Clamp(_enc.Quality, enc.QMin, enc.QMax);
        EffortSlider.Maximum = Math.Max(1, enc.Speeds.Length - 1);
        EffortSlider.Value = Math.Clamp(_enc.Speed, 0, enc.Speeds.Length - 1);

        ResolutionBox.SelectedIndex = Math.Max(0, Array.FindIndex(Resolutions, r => r.Value == _enc.MaxShortSide));
        FpsBox.SelectedIndex = Math.Max(0, Array.FindIndex(FpsCaps, r => r.Value == _enc.FpsCap));
        var ai = Array.FindIndex(Audios, a => a.Codec == _enc.Audio && (a.Kbps == _enc.AudioKbps || a.Kbps == 0));
        AudioBox.SelectedIndex = ai >= 0 ? ai : 2;
        ContainerBox.SelectedIndex = Math.Max(0, Array.FindIndex(Containers, c => c.Value == _enc.Container));
        TargetBox.Value = _enc.TargetMB;
        _suppressUi = false;

        QualityPanel.Visibility = copy || _enc.TargetSize ? Visibility.Collapsed : Visibility.Visible;
        SpeedPanel.Visibility = copy ? Visibility.Collapsed : Visibility.Visible;
        ResolutionBox.IsEnabled = FpsBox.IsEnabled = AudioBox.IsEnabled = ContainerBox.IsEnabled = !copy;
        SpeedSlider.IsEnabled = VolumeSlider.IsEnabled = MuteToggle.IsEnabled = !copy;
        TargetCard.Visibility = _enc.TargetSize ? Visibility.Visible : Visibility.Collapsed;
        UpdateFineTexts();
        UpdateSyncUi();
    }

    private void UpdateFineTexts()
    {
        var enc = Encoders.Get(_enc.Encoder);
        var qName = enc.Hardware ? "CQ" : "CRF";
        QualityText.Text = $"{qName} {_enc.Quality}";
        EffortText.Text = enc.SpeedLabels[Math.Clamp(_enc.Speed, 0, enc.SpeedLabels.Length - 1)];

        var parts = new List<string> { enc.Label.Replace(" (CPU)", "").Replace(" (GPU)", "") };
        if (enc.Family != Family.Copy)
        {
            parts.Add(_enc.TargetSize ? $"{_enc.TargetMB:0.#} MB target" : $"{qName} {_enc.Quality}");
            if (_enc.MaxShortSide > 0) parts.Add($"{_enc.MaxShortSide}p");
            if (_enc.FpsCap > 0) parts.Add($"{_enc.FpsCap} fps");
            parts.Add(Audios[Math.Max(0, AudioBox.SelectedIndex)].Label);
        }
        FineTuneSummary.Text = string.Join(" · ", parts);

        var preset = Presets.Get(_presetId);
        var custom = preset == null || !_enc.SameAs(PresetWithTarget(preset));
        CustomPill.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private EncodeSettings PresetWithTarget(Preset p)
    {
        var s = p.Build();
        if (s.TargetSize) s.TargetMB = _enc.TargetMB;
        return s;
    }

    private void PresetGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || PresetGrid.SelectedItem is not Preset p) return;
        var keepMb = _enc.TargetMB;
        _presetId = p.Id;
        _enc = p.Build();
        if (_enc.TargetSize) _enc.TargetMB = keepMb;
        LoadFineTune();
        InvalidateEstimate();
        SaveSoon();
    }

    private void Fine_Changed(object sender, object e)
    {
        if (!_ready || _suppressUi) return;
        if (ReferenceEquals(sender, EncoderBox) && EncoderBox.SelectedItem is EncoderInfo enc && enc.Id != _enc.Encoder)
        {
            _enc.Encoder = enc.Id;
            _enc.Quality = enc.Family switch
            {
                Family.AV1 => enc.Hardware ? 36 : 32,
                Family.HEVC => 26,
                _ => 24,
            };
            _enc.Speed = enc.Speeds.Length / 2;
            if (enc.Family == Family.Copy) _enc.Audio = "copy";
            else if (_enc.Audio == "copy") (_enc.Audio, _enc.AudioKbps) = ("opus", 96);
            LoadFineTune();
        }
        else
        {
            _enc.Quality = (int)QualitySlider.Value;
            _enc.Speed = (int)EffortSlider.Value;
            if (ResolutionBox.SelectedIndex >= 0) _enc.MaxShortSide = Resolutions[ResolutionBox.SelectedIndex].Value;
            if (FpsBox.SelectedIndex >= 0) _enc.FpsCap = FpsCaps[FpsBox.SelectedIndex].Value;
            if (AudioBox.SelectedIndex >= 0) (_, _enc.Audio, _enc.AudioKbps) = Audios[AudioBox.SelectedIndex];
            if (_enc.AudioKbps == 0) _enc.AudioKbps = 96;
            if (ContainerBox.SelectedIndex >= 0) _enc.Container = Containers[ContainerBox.SelectedIndex].Value;
            UpdateFineTexts();
        }
        InvalidateEstimate();
        SaveSoon();
    }

    private void TargetBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_ready || _suppressUi || double.IsNaN(args.NewValue)) return;
        _enc.TargetMB = Math.Max(1, args.NewValue);
        UpdateFineTexts();
        InvalidateEstimate();
        SaveSoon();
    }

    private void TargetQuick_Click(object sender, RoutedEventArgs e) =>
        TargetBox.Value = double.Parse((string)((Button)sender).Tag);

    private void Edit_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready || _suppressUi) return;
        _edit.Speed = SpeedSlider.Value;
        _edit.Volume = VolumeSlider.Value / 100.0;
        UpdateEditTexts();
        InvalidateEstimate();
        SaveSoon();
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _edit.Mute = MuteToggle.IsChecked == true;
        UpdateEditTexts();
        UpdateSyncUi();
        InvalidateEstimate();
        SaveSoon();
    }

    private void UpdateEditTexts()
    {
        SpeedText.Text = $"{_edit.Speed:0.##}×";
        VolumeText.Text = _edit.Mute ? "muted" : $"{_edit.Volume * 100:0} %";
        VolumeSlider.Opacity = _edit.Mute ? 0.4 : 1;
        if (!_sampleMode)
        {
            try { _mp.PlaybackSession.PlaybackRate = _edit.Speed; } catch { }
        }
        _mp.Volume = _audio.Volume = Math.Min(1, _edit.Volume);
        ApplyAudioRouting();
        UpdateTrimText();
    }

    private void Output_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppressUi) return;
        _cfg.SaveNextToSource = NextToSourceSwitch.IsOn;
        _cfg.DeleteBiggerResults = DeleteBiggerCheck.IsChecked == true;
        UpdateOutputUi();
        SaveSoon();
    }

    private void Suffix_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _suppressUi) return;
        var clean = string.Concat(SuffixBox.Text.Where(c => !System.IO.Path.GetInvalidFileNameChars().Contains(c)));
        _cfg.Suffix = clean;
        SaveSoon();
    }

    private void UpdateOutputUi()
    {
        FolderRow.Opacity = NextToSourceSwitch.IsOn ? 0.45 : 1;
        FolderRow.IsHitTestVisible = !NextToSourceSwitch.IsOn;
    }

    private void SaveSoon() { _saveTimer.Stop(); _saveTimer.Start(); }

    private void SaveNow()
    {
        _cfg.PresetId = _presetId;
        _cfg.Encode = _enc;
        _cfg.Edit = _edit;
        _cfg.Save();
    }

    // =====================================================================================
    //  queue
    // =====================================================================================

    private async void AddFiles_Click(object sender, RoutedEventArgs e) => await PickFilesAsync();

    private async Task PickFilesAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary, ViewMode = PickerViewMode.Thumbnail };
        InitializeWithWindow.Initialize(picker, _hwnd);
        foreach (var ext in Ff.VideoExtensions) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 }) AddPaths(files.Select(f => f.Path));
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) AddPaths([folder.Path]);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                try
                {
                    files.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
                        .Where(f => Ff.VideoExtensions.Contains(System.IO.Path.GetExtension(f))
                                    && !System.IO.Path.GetFileNameWithoutExtension(f).Contains(".fe-partial"))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                }
                catch { }
            }
            else if (File.Exists(p) && Ff.VideoExtensions.Contains(System.IO.Path.GetExtension(p)))
            {
                files.Add(p);
            }
        }

        var existing = new HashSet<string>(_jobs.Select(j => j.Path), StringComparer.OrdinalIgnoreCase);
        var added = new List<JobItem>();
        foreach (var f in files.Where(f => existing.Add(f)))
        {
            var job = new JobItem(f);
            _jobs.Add(job);
            added.Add(job);
            _ = ProbeAsync(job);
        }

        if (added.Count == 0)
        {
            Toast("Nothing new to add", files.Count > 0 ? "Those videos are already in the queue." : "No video files found there.",
                  InfoBarSeverity.Informational);
            return;
        }
        if (QueueList.SelectedItem == null) QueueList.SelectedItem = added[0];
        if (added.Count > 1) Toast($"Added {added.Count} videos", null, InfoBarSeverity.Success, 3);
    }

    private async Task ProbeAsync(JobItem job)
    {
        await _probeGate.WaitAsync();
        try
        {
            var info = _ffmpegOk ? await Task.Run(() => Ff.ProbeAsync(job.Path)) : null;
            if (info == null || !info.HasVideo || info.Duration <= 0)
            {
                job.State = JobState.Unsupported;
                job.Meta = "Not a readable video";
                job.Status = _ffmpegOk ? "Can't read this file" : "ffmpeg missing";
                return;
            }
            job.Info = info;
            var res = info.Height >= 2160 || info.Width >= 3840 ? "4K" : $"{Math.Min(info.Width, info.Height)}p";
            job.Meta = $"{res} · {info.VideoCodec.ToUpperInvariant()} · {Pipeline.Time(info.Duration)} · {Pipeline.Size(info.SizeBytes)}";
            job.State = JobState.Ready;
            job.Status = "Ready";
            UpdateQueueUi();

            if (_current == job) LoadPreview(job);
            var thumb = await Task.Run(() => Ff.FrameAsync(job.Path, Math.Min(info.Duration * 0.1, 8), 96,
                System.IO.Path.Combine(Ff.TempDirFor(job.Path), "thumb.jpg")));
            job.LoadThumb(thumb);
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private void UpdateQueueUi()
    {
        var any = _jobs.Count > 0;
        QueueEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        QueueBadge.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        QueueBadge.Value = _jobs.Count;

        var todo = _jobs.Where(IsEncodable).ToList();
        EncodeButton.IsEnabled = _ffmpegOk && !_encoding && todo.Count > 0;
        EncodeButtonText.Text = todo.Count > 1 ? $"Compress {todo.Count} videos" : "Compress";

        if (_encoding) return;
        var total = todo.Sum(j => j.Info?.SizeBytes ?? 0);
        StatusMain.Text = !_ffmpegOk ? "ffmpeg required" : todo.Count > 0 ? "Ready to compress" : any ? "All done" : "Ready";
        StatusSub.Text = todo.Count > 0 ? $"{todo.Count} video{(todo.Count == 1 ? "" : "s")} · {Pipeline.Size(total)} total"
                        : any ? "Add more videos or re-queue from the right-click menu" : "Add some videos to begin";
    }

    private static bool IsEncodable(JobItem j) =>
        j.Info != null && j.State is JobState.Ready or JobState.Failed or JobState.Cancelled;

    private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QueueList.SelectedItem is JobItem job) LoadPreview(job);
        else if (_jobs.Count == 0) ClearPreview();
    }

    private void RemoveJob_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is JobItem job) RemoveJob(job);
    }

    private void RemoveJob(JobItem job)
    {
        job.Cts?.Cancel();
        var idx = _jobs.IndexOf(job);
        _jobs.Remove(job);
        if (_current == job)
        {
            if (_jobs.Count > 0) QueueList.SelectedIndex = Math.Clamp(idx, 0, _jobs.Count - 1);
            else ClearPreview();
        }
    }

    private void RequeueJob_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is JobItem { Info: not null } job && !job.IsBusy)
        {
            job.State = JobState.Ready;
            job.Status = "Ready";
            job.Progress = 0;
            UpdateQueueUi();
        }
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        foreach (var j in _jobs.Where(j => j.State is JobState.Done or JobState.Unsupported).ToList()) RemoveJob(j);
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var j in _jobs.ToList()) RemoveJob(j);
    }

    private void RevealSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is JobItem job) Reveal(job.Path);
    }

    private void RevealOutput_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is JobItem job)
        {
            if (job.OutputPath != null && File.Exists(job.OutputPath)) Reveal(job.OutputPath);
            else Toast("Not compressed yet", "This video has no result file yet.", InfoBarSeverity.Informational, 3);
        }
    }

    private static void Reveal(string file) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });

    // ---------------------------------------------------------------------------- drag & drop

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to queue";
        DropOverlay.Opacity = 1;
    }

    private void Root_DragLeave(object sender, DragEventArgs e) => DropOverlay.Opacity = 0;

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Opacity = 0;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var def = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            AddPaths(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)));
        }
        finally { def.Complete(); }
    }

    // =====================================================================================
    //  preview player
    // =====================================================================================

    private void ClearPreview()
    {
        _previewCts?.Cancel();
        _current = null;
        ExitSampleMode(reload: false);
        _mp.Source = null;
        LoadShiftedAudio();
        LoadSyncUi();
        PlayerEmpty.Visibility = Visibility.Visible;
        PlayerLoading.Visibility = Visibility.Collapsed;
        StripFrames.Children.Clear();
        StripFrames.ColumnDefinitions.Clear();
        StripOverlay.Visibility = Visibility.Collapsed;
        TimeText.Text = "00:00.0 / 00:00";
        TrimText.Text = "Full length";
        EstimateFor.Text = "";
        ResetEstimateUi();
    }

    private void LoadPreview(JobItem job)
    {
        var switching = _current != job;
        _current = job;
        if (switching)
        {
            _previewCts?.Cancel();
            _previewCts = new CancellationTokenSource();
            ExitSampleMode(reload: false);
            _estimateCts?.Cancel();
            ResetEstimateUi();
            EstimateFor.Text = job.Name;
            StripFrames.Children.Clear();
            StripFrames.ColumnDefinitions.Clear();
        }
        PlayerEmpty.Visibility = Visibility.Collapsed;
        LoadSyncUi();
        if (switching) LoadShiftedAudio();

        if (job.Info == null)
        {
            ShowLoading(job.State == JobState.Unsupported ? "This file can't be read" : "Reading video…", null);
            _mp.Source = null;
            StripOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        ShowLoading("Opening…", null);
        if (switching) _mp.Pause();
        _mp.Source = MediaSource.CreateFromUri(new Uri(job.ProxyPath ?? job.Path));
        StripOverlay.Visibility = Visibility.Visible;
        LayoutStrip();
        UpdateTrimText();
        _ = LoadFilmstripAsync(job, _previewCts!.Token);
    }

    private void ShowLoading(string text, double? progress)
    {
        PlayerLoading.Visibility = Visibility.Visible;
        PlayerLoadingText.Text = text;
        PlayerLoadingRing.IsIndeterminate = progress == null;
        if (progress != null) PlayerLoadingRing.Value = progress.Value;
    }

    private void OnMediaOpened()
    {
        if (_current?.Info == null) return;
        if (_sampleMode)
        {
            PlayerLoading.Visibility = Visibility.Collapsed;
            _mp.PlaybackSession.PlaybackRate = 1;
            _mp.Play();
            return;
        }
        // Windows opened the container but has no decoder for the video track → black picture.
        if (_mp.PlaybackSession.NaturalVideoWidth == 0 && _current.ProxyPath == null)
        {
            _ = MakeProxyAsync(_current);
            return;
        }
        PlayerLoading.Visibility = Visibility.Collapsed;
        _mp.PlaybackSession.PlaybackRate = _edit.Speed;
        _mp.PlaybackSession.Position = TimeSpan.FromSeconds(_current.TrimStart);
    }

    private void OnMediaFailed(string message)
    {
        if (_current?.Info == null || _sampleMode) return;
        if (_current.ProxyPath == null) _ = MakeProxyAsync(_current);
        else ShowLoading("Preview unavailable for this file (it can still be compressed)", null);
    }

    private async Task MakeProxyAsync(JobItem job)
    {
        var ct = _previewCts?.Token ?? CancellationToken.None;
        var codec = job.Info!.VideoCodec.ToUpperInvariant();
        ShowLoading($"Windows can't play {codec} directly — building a preview…", 0);
        try
        {
            var path = await Task.Run(() => Pipeline.MakeProxyAsync(job.Info, f =>
                DispatcherQueue.TryEnqueue(() => { if (_current == job) PlayerLoadingRing.Value = f; }), ct), ct);
            job.ProxyPath = path;
            if (_current == job && !_sampleMode) _mp.Source = MediaSource.CreateFromUri(new Uri(path));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_current == job) ShowLoading("Preview unavailable: " + FirstLine(ex.Message), null);
        }
    }

    private async Task LoadFilmstripAsync(JobItem job, CancellationToken ct)
    {
        try
        {
            job.Filmstrip ??= await Task.Run(() => Ff.FilmstripAsync(job.Path, job.Info!.Duration, 12, 96,
                System.IO.Path.Combine(Ff.TempDirFor(job.Path), "strip"), ct), ct);
        }
        catch (OperationCanceledException) { return; }
        if (_current != job || ct.IsCancellationRequested) return;

        StripFrames.Children.Clear();
        StripFrames.ColumnDefinitions.Clear();
        for (int i = 0; i < job.Filmstrip.Length; i++)
        {
            StripFrames.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (job.Filmstrip[i] is not { } file) continue;
            var img = new Image
            {
                Source = new BitmapImage(new Uri(file)) { DecodePixelHeight = 96 },
                Stretch = Stretch.UniformToFill,
            };
            Grid.SetColumn(img, i);
            StripFrames.Children.Add(img);
        }
    }

    private void OnTick()
    {
        if (_current?.Info == null || _mp.Source == null) return;
        SyncAudio(hard: false);
        var session = _mp.PlaybackSession;
        double pos = session.Position.TotalSeconds;

        if (_sampleMode)
        {
            var len = session.NaturalDuration.TotalSeconds;
            TimeText.Text = $"{Pipeline.Time(pos, true)} / {Pipeline.Time(len)}";
            return;
        }

        var dur = _current.Info.Duration;
        var end = _current.TrimEnd ?? dur;
        bool playing = session.PlaybackState == MediaPlaybackState.Playing;
        if (playing && _drag == StripDrag.None && pos >= end - 0.03)
        {
            if (LoopToggle.IsChecked == true) session.Position = TimeSpan.FromSeconds(_current.TrimStart);
            else { _mp.Pause(); session.Position = TimeSpan.FromSeconds(end); }
        }
        TimeText.Text = $"{Pipeline.Time(pos, true)} / {Pipeline.Time(dur)}";
        var w = Strip.ActualWidth;
        if (w > 0 && dur > 0) Canvas.SetLeft(Playhead, Math.Clamp(pos / dur * w, 0, w) - 1.5);
    }

    private void UpdatePlayGlyph()
    {
        var playing = _mp.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        PlayGlyph.Glyph = playing ? "" : "";
    }

    private void TogglePlay()
    {
        if (_mp.Source == null) return;
        var session = _mp.PlaybackSession;
        bool playing = session.PlaybackState == MediaPlaybackState.Playing;
        if (playing) _mp.Pause();
        else
        {
            if (!_sampleMode && _current?.Info != null)
            {
                var pos = session.Position.TotalSeconds;
                var end = _current.TrimEnd ?? _current.Info.Duration;
                if (pos < _current.TrimStart - 0.05 || pos >= end - 0.05) session.Position = TimeSpan.FromSeconds(_current.TrimStart);
            }
            _mp.Play();
        }
        PulsePlay(!playing);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private void Player_Tapped(object sender, TappedRoutedEventArgs e)
    {
        Root.Focus(FocusState.Pointer);
        TogglePlay();
    }

    private void Seek(double seconds)
    {
        if (_current?.Info == null) return;
        var dur = _current.Info.Duration;
        _mp.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, dur));
    }

    // ------------------------------------------------------------------------ trim

    private double CurrentPos => _mp.PlaybackSession.Position.TotalSeconds;

    private void SetIn_Click(object sender, RoutedEventArgs e) => SetTrimStart(CurrentPos);
    private void SetOut_Click(object sender, RoutedEventArgs e) => SetTrimEnd(CurrentPos);

    private void ResetTrim_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        _current.TrimStart = 0;
        _current.TrimEnd = null;
        TrimChanged();
    }

    private void SetTrimStart(double t)
    {
        if (_current?.Info == null || _sampleMode) return;
        var end = _current.TrimEnd ?? _current.Info.Duration;
        _current.TrimStart = Math.Clamp(t, 0, Math.Max(0, end - 0.5));
        if (_current.TrimStart < 0.25) _current.TrimStart = 0;
        TrimChanged();
    }

    private void SetTrimEnd(double t)
    {
        if (_current?.Info == null || _sampleMode) return;
        var dur = _current.Info.Duration;
        var v = Math.Clamp(t, _current.TrimStart + 0.5, dur);
        _current.TrimEnd = v >= dur - 0.05 ? null : v;
        TrimChanged();
    }

    private void TrimChanged()
    {
        _current?.UpdateTrimBadge();
        LayoutStrip();
        UpdateTrimText();
        InvalidateEstimate();
    }

    private void UpdateTrimText()
    {
        if (_current?.Info == null) { TrimText.Text = "Full length"; return; }
        var dur = _current.Info.Duration;
        var end = _current.TrimEnd ?? dur;
        var span = end - _current.TrimStart;
        var outLen = span / (_enc.Encoder == "copy" ? 1 : _edit.Speed);
        var trimmed = _current.TrimStart > 0 || _current.TrimEnd.HasValue;
        TrimText.Text = (trimmed ? $"Keeping {Pipeline.Time(_current.TrimStart, true)} → {Pipeline.Time(end, true)}" : "Full length")
                        + $"  ·  result {Pipeline.Time(outLen)} long";
    }

    private void LayoutStrip()
    {
        var w = Strip.ActualWidth;
        var h = Strip.ActualHeight;
        if (_current?.Info == null || w <= 0) return;
        var dur = _current.Info.Duration;
        double xs = _current.TrimStart / dur * w;
        double xe = (_current.TrimEnd ?? dur) / dur * w;

        DimLeft.Width = Math.Max(0, xs);
        Canvas.SetLeft(DimRight, xe);
        DimRight.Width = Math.Max(0, w - xe);
        Canvas.SetLeft(TrimFrame, xs);
        TrimFrame.Width = Math.Max(0, xe - xs);
        Canvas.SetLeft(HandleL, xs);
        Canvas.SetLeft(HandleR, Math.Max(xs + 12, xe - 12));
        DimLeft.Height = DimRight.Height = TrimFrame.Height = HandleL.Height = HandleR.Height = h;
    }

    private void Strip_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutStrip();

    private double StripTime(PointerRoutedEventArgs e)
    {
        var x = e.GetCurrentPoint(Strip).Position.X;
        return Math.Clamp(x / Math.Max(1, Strip.ActualWidth), 0, 1) * (_current?.Info?.Duration ?? 0);
    }

    private void Strip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Root.Focus(FocusState.Pointer);
        if (_current?.Info == null || _sampleMode) return;
        var x = e.GetCurrentPoint(Strip).Position.X;
        var w = Strip.ActualWidth;
        var dur = _current.Info.Duration;
        double xs = _current.TrimStart / dur * w, xe = (_current.TrimEnd ?? dur) / dur * w;

        _drag = Math.Abs(x - xs) <= 14 && x <= xs + 14 ? StripDrag.Start
              : Math.Abs(x - xe) <= 14 ? StripDrag.End
              : StripDrag.Seek;
        _wasPlaying = _mp.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        if (_drag != StripDrag.Seek) _mp.Pause();
        Strip.CapturePointer(e.Pointer);
        ApplyDrag(StripTime(e), force: true);
        e.Handled = true;
    }

    private void Strip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == StripDrag.None) return;
        ApplyDrag(StripTime(e), force: false);
    }

    private void Strip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == StripDrag.None) return;
        ApplyDrag(StripTime(e), force: true);
        Strip.ReleasePointerCapture(e.Pointer);
        EndDrag();
    }

    private void Strip_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (_drag is StripDrag.Start or StripDrag.End && _wasPlaying) _mp.Play();
        _drag = StripDrag.None;
    }

    private void ApplyDrag(double t, bool force)
    {
        switch (_drag)
        {
            case StripDrag.Start: SetTrimStart(t); t = _current!.TrimStart; break;
            case StripDrag.End: SetTrimEnd(t); t = _current!.TrimEnd ?? _current.Info!.Duration; break;
        }
        var w = Strip.ActualWidth;
        if (_current?.Info != null && w > 0) Canvas.SetLeft(Playhead, t / _current.Info.Duration * w - 1.5);
        if (force || (DateTime.UtcNow - _lastSeek).TotalMilliseconds > 60)
        {
            _lastSeek = DateTime.UtcNow;
            Seek(t);
        }
    }

    // ------------------------------------------------------------------------ keyboard

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                   .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.O) { _ = PickFilesAsync(); e.Handled = true; return; }

        var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        if (focused is TextBox or NumberBox or ComboBox or AutoSuggestBox) return;
        if (focused is Slider && e.Key != VirtualKey.Space) return;   // arrows adjust the slider; Space still plays (to check the sync)
        if (e.Key == VirtualKey.Space && focused is ButtonBase) return;

        switch (e.Key)
        {
            case VirtualKey.Space: TogglePlay(); break;
            case VirtualKey.I: SetTrimStart(CurrentPos); break;
            case VirtualKey.O: SetTrimEnd(CurrentPos); break;
            case VirtualKey.R: ResetTrim_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.Left: Seek(CurrentPos - 5); break;
            case VirtualKey.Right: Seek(CurrentPos + 5); break;
            case (VirtualKey)188: Seek(CurrentPos - 1 / Math.Max(1, _current?.Info?.Fps ?? 30)); break; // ,
            case (VirtualKey)190: Seek(CurrentPos + 1 / Math.Max(1, _current?.Info?.Fps ?? 30)); break; // .
            case VirtualKey.Delete when focused is ListViewItem && _current != null: RemoveJob(_current); break;
            default: return;
        }
        e.Handled = true;
    }

    // =====================================================================================
    //  estimate + A/B sample
    // =====================================================================================

    private async void Estimate_Click(object sender, RoutedEventArgs e)
    {
        if (_estimateCts != null) { _estimateCts.Cancel(); return; }
        if (_current?.Info == null) { Toast("Pick a video first", "Select a video in the queue to estimate its size.", InfoBarSeverity.Informational, 3); return; }
        if (!_ffmpegOk) return;

        var job = _current;
        ExitSampleMode(reload: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_previewCts?.Token ?? CancellationToken.None);
        _estimateCts = cts;
        EstimateButtonText.Text = "Cancel";
        EstimateProgress.Visibility = Visibility.Visible;
        EstimateProgress.Value = 0;
        EstimateSmall.Text = "Compressing sample clips…";
        EstimateBig.Opacity = 0.4;

        var req = new EncodeRequest(job.Info, job.TrimStart, job.TrimEnd, _enc.Clone(), Clone(_edit),
            System.IO.Path.Combine(Ff.TempRoot, "est" + Pipeline.OutputExtension(job.Info, _enc)), AudioDelay: job.AudioDelay);
        var sw = Stopwatch.StartNew();
        try
        {
            var est = await Task.Run(() => Pipeline.EstimateAsync(req, f =>
                DispatcherQueue.TryEnqueue(() => EstimateProgress.Value = f), cts.Token), cts.Token);
            if (_current != job) return;

            var srcSpan = (long)(job.Info.SizeBytes * Math.Min(1, req.InputSpan / job.Info.Duration));
            var ratio = srcSpan > 0 ? 1 - (double)est.Bytes / srcSpan : 0;
            EstimateBig.Opacity = 1;
            Tween(0, est.Bytes, 900, v => EstimateBig.Text = "≈ " + Pipeline.Size((long)v));
            Tween(0, Math.Max(0, ratio) * 100, 1100, v => { SavingsRing.Value = v; SavingsText.Text = $"−{v:0}%"; });
            if (ratio < 0) SavingsText.Text = "+" + (-ratio * 100).ToString("0") + "%";
            EstimateSmall.Text = ratio >= 0
                ? $"from {Pipeline.Size(srcSpan)} — saves {Pipeline.Size(srcSpan - est.Bytes)}"
                : $"bigger than the original {Pipeline.Size(srcSpan)}. Try a stronger preset.";
            if (est.SamplePath != null)
            {
                _samplePath = est.SamplePath;
                CompareToggle.IsEnabled = true;
            }
            _ = sw;
        }
        catch (OperationCanceledException)
        {
            ResetEstimateUi();
        }
        catch (Exception ex)
        {
            ResetEstimateUi();
            EstimateSmall.Text = "Estimate failed";
            Toast("Couldn't estimate", FirstLine(ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _estimateCts = null;
            EstimateButtonText.Text = "Estimate";
            EstimateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void InvalidateEstimate()
    {
        if (!_ready) return;
        _estimateCts?.Cancel();
        if (_sampleMode) ExitSampleMode(reload: true);
        _samplePath = null;
        CompareToggle.IsEnabled = false;
        if (EstimateBig.Text != "—")
        {
            EstimateBig.Opacity = 0.4;
            EstimateSmall.Text = "Settings changed — estimate again";
        }
        UpdateFineTexts();
        UpdateTrimText();
    }

    private void ResetEstimateUi()
    {
        EstimateBig.Text = "—";
        EstimateBig.Opacity = 1;
        EstimateSmall.Text = "Compress a few seconds to see the real size";
        SavingsRing.Value = 0;
        SavingsText.Text = "";
        _samplePath = null;
        CompareToggle.IsEnabled = false;
        CompareToggle.IsChecked = false;
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (CompareToggle.IsChecked == true && _samplePath != null && File.Exists(_samplePath))
        {
            _sampleMode = true;
            SampleBadge.Visibility = Visibility.Visible;
            Strip.Opacity = 0.35;
            ApplyAudioRouting();   // the sample already carries the offset → play its own sound
            _mp.IsLoopingEnabled = true;
            ShowLoading("Loading sample…", null);
            _mp.Source = MediaSource.CreateFromUri(new Uri(_samplePath));
        }
        else ExitSampleMode(reload: true);
    }

    private void ExitSampleMode(bool reload)
    {
        if (!_sampleMode) { CompareToggle.IsChecked = false; return; }
        _sampleMode = false;
        CompareToggle.IsChecked = false;
        SampleBadge.Visibility = Visibility.Collapsed;
        Strip.Opacity = 1;
        _mp.IsLoopingEnabled = false;
        ApplyAudioRouting();
        if (reload && _current?.Info != null)
            _mp.Source = MediaSource.CreateFromUri(new Uri(_current.ProxyPath ?? _current.Path));
    }

    // =====================================================================================
    //  audio sync: per-video offset with live preview + detection
    // =====================================================================================

    private bool SyncAllowed => _current?.Info is { HasAudio: true } && (_enc.Encoder == "copy" || (!_edit.Mute && _enc.Audio != "none"));

    /// <summary>True while the video plays muted and the extracted soundtrack plays shifted beside it.</summary>
    private bool ShiftedAudio => !_sampleMode && _current is { } j && Math.Abs(j.AudioDelay) > 0.0005
                                 && j.AudioTrackPath != null && _audioSource == j.AudioTrackPath;

    private void LoadSyncUi()
    {
        _suppressUi = true;
        SyncSlider.Value = Math.Clamp((_current?.AudioDelay ?? 0) * 1000, SyncSlider.Minimum, SyncSlider.Maximum);
        _suppressUi = false;
        UpdateSyncUi();
    }

    private void UpdateSyncUi()
    {
        var ok = SyncAllowed;
        SyncSlider.IsEnabled = SyncEarlierButton.IsEnabled = SyncLaterButton.IsEnabled = ok;
        DetectButton.IsEnabled = ok || _detectCts != null;
        SyncRow.Opacity = ok ? 1 : 0.55;
        var d = (_current?.AudioDelay ?? 0) * 1000;
        SyncText.Text = Math.Abs(d) < 0.5 ? "in sync" : d > 0 ? $"sound {d:0} ms later" : $"sound {-d:0} ms earlier";
        SyncHint.Text = _current?.Info == null ? "select a video"
                      : _current.Info.HasAudio ? "this video · drag while it plays to hear the change" : "this video has no sound";
    }

    private void Sync_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready || _suppressUi || _current?.Info == null) return;
        SetAudioDelay(_current, SyncSlider.Value / 1000.0);
    }

    private void SyncNudge_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.Info == null) return;
        SetAudioDelay(_current, _current.AudioDelay + double.Parse((string)((Button)sender).Tag) / 1000.0, moveSlider: true);
    }

    private void SyncReset_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.Info != null) SetAudioDelay(_current, 0, moveSlider: true);
    }

    private void SyncApplyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.Info == null) return;
        var d = _current.AudioDelay;
        var n = 0;
        foreach (var j in _jobs.Where(j => j.Info is { HasAudio: true } && j != _current)) { SetAudioDelay(j, d); n++; }
        Toast("Offset copied", $"{n} other video{(n == 1 ? "" : "s")} now use {d * 1000:+0;−0;0} ms.", InfoBarSeverity.Success, 4);
    }

    private void FixSync_Click(object sender, RoutedEventArgs e)
    {
        _edit.FixSync = FixSyncToggle.IsChecked;
        InvalidateEstimate();
        SaveSoon();
    }

    private void SetAudioDelay(JobItem job, double seconds, bool moveSlider = false)
    {
        seconds = Math.Clamp(Math.Round(seconds, 3), -5, 5);
        job.AudioDelay = Math.Abs(seconds) < 0.0005 ? 0 : seconds;
        job.UpdateTrimBadge();
        if (job != _current) return;
        if (moveSlider) LoadSyncUi(); else UpdateSyncUi();
        InvalidateEstimate();
        if (job.AudioDelay != 0 && job.AudioTrackPath == null) _ = PrepareShiftedAudioAsync(job);
        ApplyAudioRouting();
        SyncAudio(hard: true);
    }

    private bool _extracting;

    private async Task PrepareShiftedAudioAsync(JobItem job)
    {
        if (_extracting || job.Info is not { HasAudio: true } info || job.AudioTrackPath != null) return;
        var ct = _previewCts?.Token ?? CancellationToken.None;
        _extracting = true;
        SyncHint.Text = "preparing the sound for preview…";
        try
        {
            job.AudioTrackPath = await Task.Run(() => Pipeline.ExtractAudioAsync(info, ct), ct);
            if (_current == job) LoadShiftedAudio();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_current == job) { SyncHint.Text = "preview unavailable: " + FirstLine(ex.Message); return; }
        }
        finally { _extracting = false; }
        if (_current == job) UpdateSyncUi();
        else if (_current is { AudioDelay: not 0, AudioTrackPath: null } cur) _ = PrepareShiftedAudioAsync(cur);
    }

    private void LoadShiftedAudio()
    {
        var want = _current?.AudioTrackPath;
        if (want != _audioSource)
        {
            _audio.Pause();
            _audio.Source = want != null ? MediaSource.CreateFromUri(new Uri(want)) : null;
            _audioSource = want;
        }
        if (want == null && _current is { AudioDelay: not 0 }) _ = PrepareShiftedAudioAsync(_current);
        ApplyAudioRouting();
    }

    private void ApplyAudioRouting()
    {
        _mp.IsMuted = (_edit.Mute && !_sampleMode) || ShiftedAudio;
        _audio.IsMuted = _edit.Mute;
        if (!ShiftedAudio) _audio.Pause();
    }

    /// <summary>Keeps the soundtrack player at (video position − offset). Hard = reposition now.</summary>
    private void SyncAudio(bool hard)
    {
        if (!ShiftedAudio)
        {
            if (_audio.PlaybackSession.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Buffering) _audio.Pause();
            return;
        }
        var vs = _mp.PlaybackSession;
        var au = _audio.PlaybackSession;
        var target = vs.Position.TotalSeconds - _current!.AudioDelay;
        var len = au.NaturalDuration.TotalSeconds;
        bool videoPlaying = vs.PlaybackState == MediaPlaybackState.Playing;
        bool audioActive = au.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Buffering or MediaPlaybackState.Opening;
        bool inside = target >= 0 && (len <= 0 || target < len - 0.05);

        if (!videoPlaying || !inside)
        {
            if (audioActive) _audio.Pause();
            if (hard && au.PlaybackState != MediaPlaybackState.Opening) au.Position = TimeSpan.FromSeconds(Math.Max(0, target));
            return;
        }
        if (Math.Abs(au.PlaybackRate - vs.PlaybackRate) > 0.001) au.PlaybackRate = vs.PlaybackRate;
        if (!audioActive)
        {
            au.Position = TimeSpan.FromSeconds(target);
            _audio.Play();
            _audioResync = DateTime.UtcNow;
        }
        else if (hard || ((DateTime.UtcNow - _audioResync).TotalMilliseconds > 500 && Math.Abs(au.Position.TotalSeconds - target) > 0.04))
        {
            au.Position = TimeSpan.FromSeconds(target);
            _audioResync = DateTime.UtcNow;
        }
    }

    private Task<AudioSync.Result> MeasureAsync(JobItem job, Action<double>? progress, CancellationToken ct) =>
        Task.Run(() => AudioSync.DetectAsync(job.Path, job.TrimStart, job.TrimEnd ?? job.Info!.Duration, progress, ct), ct);

    /// <summary>The delay that cancels a measured offset (0 when it is too small to notice), in 5 ms steps.</summary>
    private static double Fix(AudioSync.Result r) =>
        AudioSync.InSync(r.AudioLate) ? 0 : Math.Round(-r.AudioLate * 200) / 200;

    private static string Describe(double late) => $"{Math.Abs(late) * 1000:0} ms {(late > 0 ? "late" : "early")}";

    private async void Detect_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (_detectCts != null) { _detectCts.Cancel(); return; }
        if (_current?.Info == null || !_ffmpegOk || !SyncAllowed) return;
        var job = _current;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_previewCts?.Token ?? CancellationToken.None);
        _detectCts = cts;
        DetectText.Text = "Cancel";
        try
        {
            var r = await MeasureAsync(job, f => DispatcherQueue.TryEnqueue(() =>
            {
                if (_detectCts == cts) DetectText.Text = $"Cancel · {f * 100:0}%";
            }), cts.Token);
            var fix = Fix(r);
            switch (r.Confidence)
            {
                case AudioSync.Confidence.High when fix == 0:
                    SetAudioDelay(job, 0, moveSlider: true);
                    Toast("Sound is in sync", $"Measured {r.AudioLate * 1000:+0;−0;0} ms — too small to notice.", InfoBarSeverity.Success, 5);
                    break;
                case AudioSync.Confidence.High:
                    SetAudioDelay(job, fix, moveSlider: true);
                    Toast("Audio sync fixed", $"The sound was {Describe(r.AudioLate)}. Press play to check — nudge with the slider if needed.",
                          InfoBarSeverity.Success, 7);
                    break;
                case AudioSync.Confidence.Low:
                    Toast("Not sure about this one",
                          $"Best guess: the sound is {Describe(r.AudioLate)}, but the clip has few clear sound + flash moments. Play it and judge by ear.",
                          InfoBarSeverity.Warning, 12, action: ("Use guess", () => SetAudioDelay(job, fix, moveSlider: true)));
                    break;
                default:
                    Toast("Couldn't measure",
                          "No clear moments where a sound and a picture change happen together. Play the clip and drag Audio sync until it looks right.",
                          InfoBarSeverity.Warning, 10);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Toast("Couldn't measure the sync", FirstLine(ex.Message), InfoBarSeverity.Error); }
        finally
        {
            _detectCts = null;
            DetectText.Text = "Detect";
            UpdateSyncUi();
        }
    }

    private async void DetectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_detectCts != null || !_ffmpegOk) return;
        var todo = _jobs.Where(j => j.Info is { HasAudio: true } && !j.IsBusy).ToList();
        if (todo.Count == 0) { Toast("Nothing to check", "No videos with sound in the queue.", InfoBarSeverity.Informational, 3); return; }
        using var cts = new CancellationTokenSource();
        _detectCts = cts;
        DetectButton.IsEnabled = true;
        int fixedN = 0, fine = 0;
        var unsure = new List<string>();
        try
        {
            for (int i = 0; i < todo.Count; i++)
            {
                var job = todo[i];
                var step = $"Cancel · {i + 1}/{todo.Count}";
                DetectText.Text = step;
                var r = await MeasureAsync(job, f => DispatcherQueue.TryEnqueue(() =>
                {
                    if (_detectCts == cts) DetectText.Text = $"{step} · {f * 100:0}%";
                }), cts.Token);
                if (r.Confidence != AudioSync.Confidence.High) { unsure.Add(job.Name); continue; }
                SetAudioDelay(job, Fix(r), moveSlider: true);
                if (Fix(r) == 0) fine++; else fixedN++;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Toast("Couldn't measure the sync", FirstLine(ex.Message), InfoBarSeverity.Error); }
        finally
        {
            _detectCts = null;
            DetectText.Text = "Detect";
            UpdateSyncUi();
        }
        Toast("Sync check done",
              $"{fixedN} fixed · {fine} already in sync"
              + (unsure.Count > 0 ? $" · {unsure.Count} unsure, left as they were: {string.Join(", ", unsure.Take(3))}{(unsure.Count > 3 ? "…" : "")}" : ""),
              unsure.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success, 12);
    }

    // =====================================================================================
    //  output folder
    // =====================================================================================

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;
        _cfg.OutputFolder = folder.Path;
        FolderBox.Text = folder.Path;
        SaveSoon();
    }

    private string OutputDirFor(string input) =>
        _cfg.SaveNextToSource ? System.IO.Path.GetDirectoryName(input)! : _cfg.OutputFolder;

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutput != null && File.Exists(_lastOutput)
            && string.Equals(System.IO.Path.GetDirectoryName(_lastOutput), OutputDirFor(_lastOutput), StringComparison.OrdinalIgnoreCase)
            || (_lastOutput != null && File.Exists(_lastOutput) && _cfg.SaveNextToSource))
        {
            Reveal(_lastOutput!);
            return;
        }
        string? dir = _cfg.SaveNextToSource
            ? (_current != null ? System.IO.Path.GetDirectoryName(_current.Path) : null)
            : _cfg.OutputFolder;
        if (string.IsNullOrEmpty(dir))
        {
            Toast("No folder yet", "Results are saved next to each original video. Add a video first.", InfoBarSeverity.Informational, 4);
            return;
        }
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Toast("Couldn't open folder", ex.Message, InfoBarSeverity.Error); }
    }

    // =====================================================================================
    //  encoding
    // =====================================================================================

    private async void Encode_Click(object sender, RoutedEventArgs e)
    {
        var todo = _jobs.Where(IsEncodable).ToList();
        if (todo.Count == 0 || _encoding) return;

        if (!_cfg.SaveNextToSource)
        {
            try { Directory.CreateDirectory(_cfg.OutputFolder); }
            catch (Exception ex) { Toast("Output folder unavailable", ex.Message, InfoBarSeverity.Error); return; }
        }

        _estimateCts?.Cancel();
        SetBusy(true);
        _runCts = new CancellationTokenSource();
        foreach (var j in todo) { j.State = JobState.Queued; j.Status = "Waiting…"; j.Progress = 0; }

        long srcTotal = 0, outTotal = 0;
        int ok = 0, failed = 0, bigger = 0;
        var clock = Stopwatch.StartNew();

        for (int i = 0; i < todo.Count; i++)
        {
            var job = todo[i];
            if (_runCts.IsCancellationRequested) { job.State = JobState.Cancelled; job.Status = "Stopped"; continue; }
            if (!_jobs.Contains(job)) continue;

            var s = _enc.Clone();
            var ed = Clone(_edit);
            var info = job.Info!;
            var dir = OutputDirFor(job.Path);
            var ext = Pipeline.OutputExtension(info, s);
            var final = Pipeline.UniqueOutput(dir, job.Path, _cfg.Suffix, ext);
            var partial = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(final) + ".fe-partial" + ext);
            var req = new EncodeRequest(info, job.TrimStart, job.TrimEnd, s, ed, partial, AudioDelay: job.AudioDelay);

            job.Cts = CancellationTokenSource.CreateLinkedTokenSource(_runCts.Token);
            job.State = JobState.Encoding;
            job.Status = "Starting…";
            QueueList.ScrollIntoView(job);
            StatusMain.Text = todo.Count > 1 ? $"Compressing {i + 1} of {todo.Count}" : "Compressing";
            StatusSub.Text = job.Name;

            try
            {
                Directory.CreateDirectory(dir);
                int idx = i;
                await Task.Run(() => Pipeline.EncodeAsync(req, (f, sp, stage) =>
                    DispatcherQueue.TryEnqueue(() => ReportProgress(job, f, sp, stage, idx, todo.Count, req.OutputDuration)),
                    job.Cts.Token));

                File.Move(partial, final, overwrite: false);
                var outBytes = new FileInfo(final).Length;
                job.OutputBytes = outBytes;
                job.Progress = 1;

                if (!req.IsCopy && outBytes >= info.SizeBytes && _cfg.DeleteBiggerResults)
                {
                    TryDelete(final);
                    bigger++;
                    job.State = JobState.Cancelled;
                    job.Status = $"Result was bigger ({Pipeline.Size(outBytes)}) — discarded";
                }
                else
                {
                    ok++;
                    srcTotal += info.SizeBytes;
                    outTotal += outBytes;
                    job.OutputPath = final;
                    _lastOutput = final;
                    var pct = info.SizeBytes > 0 ? (1 - (double)outBytes / info.SizeBytes) * 100 : 0;
                    job.State = JobState.Done;
                    job.Status = $"{Pipeline.Size(info.SizeBytes)} → {Pipeline.Size(outBytes)}  ·  {(pct >= 0 ? "−" : "+")}{Math.Abs(pct):0}%";
                }
            }
            catch (OperationCanceledException)
            {
                TryDelete(partial);
                job.State = JobState.Cancelled;
                job.Status = "Stopped";
                job.Progress = 0;
            }
            catch (Exception ex)
            {
                TryDelete(partial);
                failed++;
                job.State = JobState.Failed;
                job.Status = "Failed: " + FirstLine(ex.Message);
                job.Progress = 0;
                Toast($"Couldn't compress {job.Name}", ex.Message, InfoBarSeverity.Error, 12);
            }
            finally
            {
                job.Cts?.Dispose();
                job.Cts = null;
            }
        }

        var cancelled = _runCts.IsCancellationRequested;
        _runCts.Dispose();
        _runCts = null;
        SetBusy(false);

        if (ok > 0)
        {
            var saved = srcTotal - outTotal;
            var pct = srcTotal > 0 ? saved * 100.0 / srcTotal : 0;
            Toast(cancelled ? "Stopped" : "🎉  All done!",
                  $"{ok} video{(ok == 1 ? "" : "s")} · {Pipeline.Size(srcTotal)} → {Pipeline.Size(outTotal)} · saved {Pipeline.Size(Math.Max(0, saved))} ({pct:0}%) in {Pipeline.Time(clock.Elapsed.TotalSeconds)}"
                  + (bigger > 0 ? $" · {bigger} discarded (would have grown)" : ""),
                  InfoBarSeverity.Success, 10, action: ("Show files", () => { if (_lastOutput != null) Reveal(_lastOutput); }));
            StatusMain.Text = "Done";
            StatusSub.Text = $"Saved {Pipeline.Size(Math.Max(0, saved))} ({pct:0}%)";
        }
        else if (cancelled) Toast("Stopped", "Nothing was finished.", InfoBarSeverity.Warning, 4);
        else if (bigger > 0) Toast("Nothing saved", "The results would have been bigger than the originals, so they were discarded. Try a stronger preset.", InfoBarSeverity.Warning, 8);

        if (!cancelled) _taskbar.Flash();
    }

    private void ReportProgress(JobItem job, double frac, double speed, string passText, int idx, int count, double outDur)
    {
        if (!_encoding || job.State != JobState.Encoding) return;
        job.Progress = frac;
        if (speed > 0.01)
        {
            var remaining = (1 - frac) * outDur / speed;
            job.Status = $"{passText}{frac * 100:0}% · {speed:0.0}× · {Pipeline.Time(remaining)} left";
            StatusLeft.Text = $"{speed:0.00}× realtime";
            StatusRight.Text = $"≈ {Pipeline.Time(remaining)} left for this video";
        }
        else job.Status = $"{passText}{frac * 100:0}%";
        var overall = (idx + frac) / count;
        OverallProgress.Value = overall;
        _taskbar.Set(Taskbar.State.Normal, overall);
    }

    private void SetBusy(bool busy)
    {
        _encoding = busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PresetGrid.IsEnabled = true;
        if (busy)
        {
            OverallProgress.Value = 0;
            StartShimmer();
            _taskbar.Set(Taskbar.State.Normal, 0);
        }
        else
        {
            StopShimmer();
            StatusLeft.Text = StatusRight.Text = "";
            OverallProgress.Value = _jobs.Any(j => j.State == JobState.Done) ? 1 : 0;
            _taskbar.Set(Taskbar.State.None);
        }
        UpdateQueueUi();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _runCts?.Cancel();
        StatusMain.Text = "Stopping…";
    }

    // =====================================================================================
    //  toasts
    // =====================================================================================

    private void Toast(string title, string? message, InfoBarSeverity severity, int seconds = 6, (string Text, Action Run)? action = null)
    {
        var bar = new InfoBar
        {
            Title = title,
            Message = message ?? "",
            Severity = severity,
            IsOpen = true,
            IsClosable = true,
            Background = Brush("SolidBackgroundFillColorTertiaryBrush"),
            Shadow = new ThemeShadow(),
            Translation = new Vector3(0, 0, 24),
        };
        if (action is { } a)
        {
            var btn = new Button { Content = a.Text };
            btn.Click += (_, _) => { a.Run(); bar.IsOpen = false; };
            bar.ActionButton = btn;
        }
        bar.Closed += (_, _) => ToastHost.Children.Remove(bar);
        ToastHost.Children.Add(bar);
        while (ToastHost.Children.Count > 3) ToastHost.Children.RemoveAt(0);
        if (seconds > 0)
        {
            var t = DispatcherQueue.CreateTimer();
            t.Interval = TimeSpan.FromSeconds(seconds);
            t.IsRepeating = false;
            t.Tick += (_, _) => { bar.IsOpen = false; _tweens.Remove(t); };
            _tweens.Add(t);
            t.Start();
        }
    }

    // =====================================================================================
    //  animation helpers
    // =====================================================================================

    private Storyboard? _shimmer;

    private void StartIdleAnimations()
    {
        var bob = new DoubleAnimation
        {
            From = 0, To = -10, Duration = TimeSpan.FromSeconds(1.6), AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(bob, EmptyEmojiT);
        Storyboard.SetTargetProperty(bob, "TranslateY");
        var sb = new Storyboard();
        sb.Children.Add(bob);
        sb.Begin();
    }

    private void StartShimmer()
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, 124, 58, 237), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 124, 58, 237), Offset = 0.35 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 6, 182, 212), Offset = 0.5 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, 6, 182, 212), Offset = 0.65 });
        var shift = new TranslateTransform();
        brush.RelativeTransform = shift;
        BusyLine.Fill = brush;
        BusyLine.Opacity = 1;

        var anim = new DoubleAnimation
        {
            From = -0.7, To = 0.7, Duration = TimeSpan.FromSeconds(1.4), RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(anim, shift);
        Storyboard.SetTargetProperty(anim, "X");
        _shimmer = new Storyboard();
        _shimmer.Children.Add(anim);
        _shimmer.Begin();
    }

    private void StopShimmer()
    {
        _shimmer?.Stop();
        _shimmer = null;
        BusyLine.Opacity = 0;
    }

    private void PulsePlay(bool playing)
    {
        PlayPulseIcon.Glyph = playing ? "" : "";
        var scale = new ScaleTransform { CenterX = 36, CenterY = 36 };
        PlayPulse.RenderTransform = scale;
        var sb = new Storyboard();
        void Add(DependencyObject target, string prop, double from, double to)
        {
            var a = new DoubleAnimation
            {
                From = from, To = to, Duration = TimeSpan.FromMilliseconds(520),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(a, target);
            Storyboard.SetTargetProperty(a, prop);
            sb.Children.Add(a);
        }
        Add(PlayPulse, "Opacity", 1, 0);
        Add(scale, "ScaleX", 0.8, 1.35);
        Add(scale, "ScaleY", 0.8, 1.35);
        sb.Begin();
    }

    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) => Pop((UIElement)sender, 1.045f);
    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) => Pop((UIElement)sender, 1f);

    private static void Pop(UIElement el, float scale)
    {
        el.ScaleTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
        el.CenterPoint = new Vector3(el.ActualSize.X / 2, el.ActualSize.Y / 2, 0);
        el.Scale = new Vector3(scale, scale, 1);
    }

    private void Tween(double from, double to, int ms, Action<double> step)
    {
        var start = DateTime.UtcNow;
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(16);
        t.Tick += (_, _) =>
        {
            var p = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / ms);
            step(from + (to - from) * (1 - Math.Pow(1 - p, 3)));
            if (p >= 1) { t.Stop(); _tweens.Remove(t); }
        };
        _tweens.Add(t);
        t.Start();
    }

    // =====================================================================================
    //  utils
    // =====================================================================================

    private static Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var b) && b is Brush br ? br : new SolidColorBrush(Colors.Gray);

    private static EditSettings Clone(EditSettings e) => new() { Speed = e.Speed, Volume = e.Volume, Mute = e.Mute, FixSync = e.FixSync };

    private static string FirstLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? s;

    private static void TryDelete(string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try { if (File.Exists(path)) File.Delete(path); return; }
            catch { Thread.Sleep(150); }
        }
    }
}
