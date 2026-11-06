namespace FastEncoder.Core;

/// <summary>How a video is compressed (shared by every file in the queue).</summary>
public sealed class EncodeSettings
{
    public string Encoder { get; set; } = "svtav1";
    public int Quality { get; set; } = 32;      // CRF / CQ — lower = better quality, bigger file
    public int Speed { get; set; } = 4;         // index into EncoderInfo.Speeds (0 = slowest/smallest)
    public int MaxShortSide { get; set; }       // 0 = keep resolution, else cap e.g. 720
    public int FpsCap { get; set; }             // 0 = keep frame rate
    public string Audio { get; set; } = "opus"; // opus | aac | copy | none
    public int AudioKbps { get; set; } = 96;
    public string Container { get; set; } = "mp4";
    public bool TargetSize { get; set; }
    public double TargetMB { get; set; } = 25;
    /// <summary>How much extra analysis NVENC encoders do on top of <see cref="Speed"/> (ignored by other encoders).</summary>
    public GpuProfile GpuProfile { get; set; } = GpuProfile.Quality;

    public EncodeSettings Clone() => (EncodeSettings)MemberwiseClone();

    public bool SameAs(EncodeSettings o) =>
        Encoder == o.Encoder && Quality == o.Quality && Speed == o.Speed && MaxShortSide == o.MaxShortSide
        && FpsCap == o.FpsCap && Audio == o.Audio && AudioKbps == o.AudioKbps && Container == o.Container
        && GpuProfile == o.GpuProfile
        && TargetSize == o.TargetSize && (!TargetSize || Math.Abs(TargetMB - o.TargetMB) < 0.01);
}

/// <summary>
/// NVENC rate-control effort. Quality = two-pass full-resolution analysis with a 32-frame lookahead (the original
/// GPU Turbo behaviour); Balanced = single pass with a short 8-frame lookahead; Fast = single pass, no lookahead, no AQ.
/// </summary>
public enum GpuProfile { Quality, Balanced, Fast }

/// <summary>Edits applied on top of the compression (also shared by the queue; trim is per file).</summary>
public sealed class EditSettings
{
    public double Speed { get; set; } = 1.0;
    public double Volume { get; set; } = 1.0;
    public bool Mute { get; set; }
    /// <summary>Constant frame rate output + audio re-timed from its timestamps (game captures drift otherwise).</summary>
    public bool FixSync { get; set; } = true;
}

public sealed class Preset
{
    public required string Id { get; init; }
    public required string Emoji { get; init; }
    public required string Name { get; init; }
    public required string Tagline { get; init; }
    public required string Savings { get; init; }
    public required Func<EncodeSettings> Build { get; init; }
    /// <summary>True when <see cref="Build"/> depends on the detected GPU encoders, so it must be rebuilt after detection.</summary>
    public bool HardwareDependent { get; init; }
}

/// <summary>
/// Storage-first presets. AV1 at 10-bit is the modern sweet spot: ~30–50 % smaller than HEVC and
/// ~50–70 % smaller than H.264 at the same visual quality. Opus beats AAC at every bitrate.
/// </summary>
public static class Presets
{
    private static EncodeSettings Svt(int crf, string preset, int audioKbps, int maxShort = 0, int fps = 0)
    {
        var e = Encoders.Get("svtav1");
        return new EncodeSettings
        {
            Encoder = "svtav1", Quality = crf, Speed = Array.IndexOf(e.Speeds, preset),
            Audio = "opus", AudioKbps = audioKbps, MaxShortSide = maxShort, FpsCap = fps,
        };
    }

    private static EncodeSettings Gpu(int audioKbps)
    {
        var hw = Encoders.BestGpu;
        if (hw == null) return Svt(33, "10", audioKbps); // no GPU encoder: fastest sane CPU AV1
        var q = hw.Family switch { Family.AV1 => 36, Family.HEVC => 28, _ => 27 };
        return new EncodeSettings { Encoder = hw.Id, Quality = q, Speed = 0, Audio = "opus", AudioKbps = audioKbps };
    }

    /// <summary>
    /// Lighter GPU presets: same quality target as <see cref="Gpu"/>, less encoder analysis. The speed is given per
    /// vendor by name (NVENC p-levels, QuickSync presets, AMF quality modes); the NVENC analysis depth comes from
    /// <see cref="GpuProfile"/>. No GPU encoder: a faster SVT-AV1 preset.
    /// </summary>
    private static EncodeSettings GpuLight(GpuProfile profile, string nvenc, string qsv, string amf, string svt, int audioKbps)
    {
        var hw = Encoders.BestGpu;
        if (hw == null) return Svt(33, svt, audioKbps);
        var name = hw.Id.EndsWith("_nvenc") ? nvenc : hw.Id.EndsWith("_qsv") ? qsv : amf;
        var q = hw.Family switch { Family.AV1 => 36, Family.HEVC => 28, _ => 27 };
        return new EncodeSettings
        {
            Encoder = hw.Id, Quality = q, Speed = Math.Max(0, Array.IndexOf(hw.Speeds, name)),
            Audio = "opus", AudioKbps = audioKbps, GpuProfile = profile,
        };
    }

    public static readonly List<Preset> All =
    [
        new() { Id = "balanced", Emoji = "⚖️", Name = "Balanced", Savings = "−70–85 %",
                Tagline = "Recommended. Looks the same, a fraction of the size.",
                Build = () => Svt(32, "6", 96) },
        new() { Id = "archive", Emoji = "📦", Name = "Archive", Savings = "−85–95 %",
                Tagline = "Smallest file. Slow, meant for long-term storage.",
                Build = () => Svt(40, "4", 64) },
        new() { Id = "pristine", Emoji = "💎", Name = "Pristine", Savings = "−40–65 %",
                Tagline = "Visually lossless for footage you care about.",
                Build = () => Svt(24, "5", 160) },
        new() { Id = "turbo", Emoji = "⚡", Name = "GPU Turbo", Savings = "−60–80 %",
                Tagline = "Uses your graphics card. Many times faster.",
                Build = () => Gpu(96), HardwareDependent = true },
        new() { Id = "gpu-balanced", Emoji = "🏎️", Name = "GPU Balanced", Savings = "−60–80 %",
                Tagline = "About 2× faster than Turbo, nearly the same look.",
                Build = () => GpuLight(GpuProfile.Balanced, "p4", "medium", "balanced", "8", 96), HardwareDependent = true },
        new() { Id = "gpu-fast", Emoji = "🚀", Name = "GPU Fast", Savings = "−60–80 %",
                Tagline = "Quickest GPU settings, for long clips.",
                Build = () => GpuLight(GpuProfile.Fast, "p2", "veryfast", "speed", "12", 96), HardwareDependent = true },
        new() { Id = "compact", Emoji = "📱", Name = "Compact 720p", Savings = "−90–97 %",
                Tagline = "Screen recordings, lectures and phone clips.",
                Build = () => Svt(34, "6", 64, maxShort: 720, fps: 30) },
        new() { Id = "universal", Emoji = "🌐", Name = "Universal", Savings = "−30–60 %",
                Tagline = "H.264 + AAC. Plays on anything, anywhere.",
                Build = () => new EncodeSettings { Encoder = "x264", Quality = 24, Speed = 2, Audio = "aac", AudioKbps = 128 } },
        new() { Id = "target", Emoji = "🎯", Name = "Fit to size", Savings = "exact MB",
                Tagline = "Hit a file size limit, e.g. 10 MB for Discord.",
                Build = () =>
                {
                    var hw = Encoders.BestHardware(Family.AV1) ?? Encoders.BestHardware(Family.HEVC);
                    return new EncodeSettings
                    {
                        Encoder = hw?.Id ?? "x264", Speed = hw != null ? 0 : 2, Quality = 30,
                        Audio = "opus", AudioKbps = 64, TargetSize = true, TargetMB = 10,
                    };
                }, HardwareDependent = true },
        new() { Id = "trim", Emoji = "✂️", Name = "Trim only", Savings = "lossless",
                Tagline = "Cut without re-encoding. Instant, no quality loss.",
                Build = () => new EncodeSettings { Encoder = "copy", Audio = "copy" } },
    ];

    public static Preset? Get(string id) => All.FirstOrDefault(p => p.Id == id);
}
