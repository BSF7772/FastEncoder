namespace FastEncoder.Core;

public enum Family { AV1, HEVC, H264, Copy }

/// <summary>One ffmpeg video encoder. Speeds are ordered slowest (smallest file) → fastest.</summary>
public sealed record EncoderInfo(
    string Id, string Label, string Ff, Family Family, bool Hardware,
    int QMin, int QMax, string[] Speeds, string[] SpeedLabels)
{
    public override string ToString() => Label;
}

public static class Encoders
{
    private static readonly string[] X26x = ["veryslow", "slower", "slow", "medium", "fast", "faster", "veryfast", "ultrafast"];
    private static readonly string[] Svt = ["2", "3", "4", "5", "6", "7", "8", "10", "12"];
    private static readonly string[] Nv = ["p7", "p6", "p5", "p4", "p3", "p2", "p1"];
    private static readonly string[] Qsv = ["veryslow", "slower", "slow", "medium", "fast", "faster", "veryfast"];
    private static readonly string[] Amf = ["quality", "balanced", "speed"];

    public static readonly EncoderInfo[] All =
    [
        new("svtav1",     "AV1 · SVT-AV1 (CPU)",          "libsvtav1",  Family.AV1,  false, 10, 55, Svt, Svt.Select(s => "preset " + s).ToArray()),
        new("av1_nvenc",  "AV1 · NVIDIA NVENC (GPU)",     "av1_nvenc",  Family.AV1,  true,  10, 55, Nv, Nv),
        new("av1_qsv",    "AV1 · Intel QuickSync (GPU)",  "av1_qsv",    Family.AV1,  true,  10, 51, Qsv, Qsv),
        new("x265",       "HEVC · x265 (CPU)",            "libx265",    Family.HEVC, false, 14, 40, X26x, X26x),
        new("hevc_nvenc", "HEVC · NVIDIA NVENC (GPU)",    "hevc_nvenc", Family.HEVC, true,  14, 45, Nv, Nv),
        new("hevc_qsv",   "HEVC · Intel QuickSync (GPU)", "hevc_qsv",   Family.HEVC, true,  14, 45, Qsv, Qsv),
        new("hevc_amf",   "HEVC · AMD AMF (GPU)",         "hevc_amf",   Family.HEVC, true,  14, 45, Amf, Amf),
        new("x264",       "H.264 · x264 (CPU)",           "libx264",    Family.H264, false, 14, 40, X26x, X26x),
        new("h264_nvenc", "H.264 · NVIDIA NVENC (GPU)",   "h264_nvenc", Family.H264, true,  14, 45, Nv, Nv),
        new("h264_qsv",   "H.264 · Intel QuickSync (GPU)","h264_qsv",   Family.H264, true,  14, 45, Qsv, Qsv),
        new("h264_amf",   "H.264 · AMD AMF (GPU)",        "h264_amf",   Family.H264, true,  14, 45, Amf, Amf),
        new("copy",       "Stream copy (no re-encode)",   "copy",       Family.Copy, false, 0, 0, ["copy"], ["lossless"]),
    ];

    /// <summary>Encoders proven to work on this machine (filled by <see cref="DetectAsync"/>).</summary>
    public static List<EncoderInfo> Available { get; private set; } =
        All.Where(e => !e.Hardware).ToList();

    public static EncoderInfo Get(string id) => All.FirstOrDefault(e => e.Id == id) ?? All[0];
    public static bool IsAvailable(string id) => Available.Any(e => e.Id == id);

    /// <summary>Best hardware encoder of a family, or null.</summary>
    public static EncoderInfo? BestHardware(Family f) => Available.FirstOrDefault(e => e.Hardware && e.Family == f);

    public static EncoderInfo? BestGpu => BestHardware(Family.AV1) ?? BestHardware(Family.HEVC) ?? BestHardware(Family.H264);

    public static async Task DetectAsync()
    {
        var listing = await Ff.RunAsync(Ff.Ffmpeg, ["-hide_banner", "-encoders"]);
        var names = new HashSet<string>(listing.StdOut.Split('\n')
            .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.Length >= 2).Select(t => t[1]));

        // Hardware encoders are listed even when no GPU exists → prove each with a tiny test encode.
        var tests = All.Where(e => e.Hardware && names.Contains(e.Ff)).Select(async e =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var r = await Ff.RunAsync(Ff.Ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                    "-i", "color=black:s=256x256:r=30:d=0.2", "-c:v", e.Ff, "-f", "null", "-",
                ], cts.Token);
                return r.ExitCode == 0 ? e : null;
            }
            catch { return null; }
        });
        var hw = (await Task.WhenAll(tests)).Where(e => e != null).Cast<EncoderInfo>().ToHashSet();

        Available = All.Where(e => e.Id == "copy" || (e.Hardware ? hw.Contains(e) : names.Contains(e.Ff))).ToList();
        HasOpus = names.Contains("libopus");
    }

    public static bool HasOpus { get; private set; } = true;
}
