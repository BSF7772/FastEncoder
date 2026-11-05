using System.ComponentModel;
using System.Runtime.CompilerServices;
using FastEncoder.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FastEncoder;

public enum JobState { Probing, Ready, Queued, Encoding, Done, Failed, Cancelled, Unsupported }

/// <summary>One file in the queue.</summary>
public sealed class JobItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public JobItem(string path) => Path = path;

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public MediaInfo? Info { get; set; }

    // per-file trim (seconds)
    public double TrimStart { get; set; }
    public double? TrimEnd { get; set; }

    /// <summary>Seconds the soundtrack is delayed against the picture (negative = earlier). Per file, like trim.</summary>
    public double AudioDelay { get; set; }
    public string? AudioTrackPath { get; set; }   // extracted soundtrack for the live offset preview

    public string? OutputPath { get; set; }
    public string? ProxyPath { get; set; }
    public string?[]? Filmstrip { get; set; }
    public long OutputBytes { get; set; }
    public CancellationTokenSource? Cts { get; set; }

    private JobState _state = JobState.Probing;
    public JobState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(IsBusy));
                Raise(nameof(ShowProgress));
                Raise(nameof(StatusBrush));
                Raise(nameof(StatusGlyph));
            }
        }
    }

    public bool IsBusy => State is JobState.Encoding or JobState.Probing;
    public bool ShowProgress => State == JobState.Encoding;
    public bool IsFinished => State is JobState.Done or JobState.Failed or JobState.Cancelled;

    private ImageSource? _thumb;
    public ImageSource? Thumb { get => _thumb; set => Set(ref _thumb, value); }

    private string _meta = "Reading…";
    public string Meta { get => _meta; set => Set(ref _meta, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    private string _trimBadge = "";
    public string TrimBadge { get => _trimBadge; set { Set(ref _trimBadge, value); Raise(nameof(HasTrim)); } }
    public bool HasTrim => TrimBadge.Length > 0;

    public string StatusGlyph => State switch
    {
        JobState.Done => "",
        JobState.Failed or JobState.Unsupported => "",
        JobState.Cancelled => "",
        JobState.Encoding => "",
        _ => "",
    };

    public Brush StatusBrush => State switch
    {
        JobState.Done => Res("SystemFillColorSuccessBrush"),
        JobState.Failed or JobState.Unsupported => Res("SystemFillColorCriticalBrush"),
        JobState.Cancelled => Res("SystemFillColorCautionBrush"),
        JobState.Encoding => Res("AccentTextFillColorPrimaryBrush"),
        _ => Res("TextFillColorSecondaryBrush"),
    };

    private static Brush Res(string key) =>
        Application.Current.Resources.TryGetValue(key, out var b) && b is Brush br ? br : new SolidColorBrush(Colors.Gray);

    public void UpdateTrimBadge()
    {
        var parts = new List<string>();
        if (TrimStart > 0.05 || TrimEnd.HasValue) parts.Add($"✂ {Pipeline.Time(TrimStart)} – {Pipeline.Time(TrimEnd ?? Info?.Duration ?? 0)}");
        if (Math.Abs(AudioDelay) > 0.0005) parts.Add($"♪ {AudioDelay * 1000:+0;−0} ms");
        TrimBadge = string.Join("  ·  ", parts);
    }

    public void LoadThumb(string? file)
    {
        if (file == null) return;
        Thumb = new BitmapImage(new Uri(file)) { DecodePixelHeight = 96 };
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
