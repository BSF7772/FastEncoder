using System.Diagnostics;
using System.Globalization;

namespace FastEncoder.Core;

/// <summary>Everything needed to build one ffmpeg run for one file.</summary>
/// <param name="AudioDelay">Seconds to delay the soundtrack against the picture (negative = play it earlier).</param>
public sealed record EncodeRequest(
    MediaInfo Info, double TrimStart, double? TrimEnd,
    EncodeSettings S, EditSettings Edit, string Output, double BitrateScale = 1.0, double AudioDelay = 0)
{
    public double InputSpan => Math.Max(0.05, (TrimEnd ?? Info.Duration) - TrimStart);
    public double OutputDuration => InputSpan / (IsCopy ? 1 : Edit.Speed);
    public bool IsCopy => S.Encoder == "copy";
}

public static class Pipeline
{
    private static readonly HashSet<string> Mp4Audio = ["aac", "mp3", "opus", "ac3", "eac3", "alac", "flac"];
    private static string I(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static string OutputExtension(MediaInfo info, EncodeSettings s) =>
        s.Encoder == "copy" ? Path.GetExtension(info.Path).ToLowerInvariant() : "." + s.Container;

    /// <summary>Returns one command per pass (two for x264/x265 target-size, otherwise one).</summary>
    public static List<List<string>> Build(EncodeRequest r)
    {
        var s = r.S;
        var enc = Encoders.Get(s.Encoder);
        bool twoPass = s.TargetSize && !r.IsCopy && enc.Id is "x264" or "x265";
        if (!twoPass) return [BuildPass(r, enc, 0, null)];
        var log = Path.Combine(Ff.TempRoot, "pass_" + Guid.NewGuid().ToString("N")[..8]);
        return [BuildPass(r, enc, 1, log), BuildPass(r, enc, 2, log)];
    }

    private static List<string> BuildPass(EncodeRequest r, EncoderInfo enc, int pass, string? passLog)
    {
        var (info, s, ed) = (r.Info, r.S, r.Edit);
        var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };

        if (r.TrimStart > 0.001) a.AddRange(["-ss", I(r.TrimStart)]);
        if (r.TrimEnd.HasValue) a.AddRange(["-t", I(r.InputSpan)]);
        a.AddRange(["-i", info.Path]);

        // ------------------------------------------------------------ audio plan
        bool audioWanted = info.HasAudio && (r.IsCopy || (s.Audio != "none" && !ed.Mute));
        bool audioOn = audioWanted && pass != 1;                       // pass 1 of two-pass analyses video only
        var af = new List<string>();
        string codec = "copy";
        if (audioOn && !r.IsCopy)
        {
            if (Math.Abs(ed.Volume - 1) > 0.001) af.Add($"volume={I(ed.Volume)}");
            if (Math.Abs(ed.Speed - 1) > 0.001) af.AddRange(Atempo(ed.Speed).Select(t => $"atempo={I(t)}"));
            codec = s.Audio;
            if (codec == "copy" && (af.Count > 0 || (s.Container == "mp4" && !Mp4Audio.Contains(info.AudioCodec))))
                codec = "opus";
            if (codec == "opus" && !Encoders.HasOpus) codec = "aac";
        }

        // A sync offset reads the soundtrack through a second, shifted input of the same file:
        // output audio at time t = source audio at TrimStart + t − delay.
        int ai = 0;
        double lead = 0;
        if (audioOn && Math.Abs(r.AudioDelay) > 0.0005)
        {
            ai = 1;
            var from = r.TrimStart - r.AudioDelay;
            lead = Math.Max(0, -from);                                   // silence before the first sample
            if (from > 0.001) a.AddRange(["-ss", I(from)]);
            a.AddRange(["-t", I(Math.Max(0.05, r.InputSpan - lead))]);
            if (codec == "copy" && lead > 0) a.AddRange(["-itsoffset", I(lead)]);
            a.AddRange(["-i", info.Path]);
        }
        if (codec != "copy")
        {
            var pre = new List<string>();
            // Follow the audio timestamps: fills capture gaps with silence instead of letting the sound run ahead.
            if (ed.FixSync) pre.Add("aresample=async=1:min_hard_comp=0.02:first_pts=0");
            if (lead > 0) pre.Add($"adelay=delays={(int)Math.Round(lead * 1000)}:all=1");
            af.InsertRange(0, pre);
            if (codec == "opus") af.Add("aformat=channel_layouts=7.1|5.1|stereo|mono");
        }

        // ---------------------------------------------------------- lossless copy
        if (r.IsCopy)
        {
            if (info.HasVideo) a.AddRange(["-map", "0:v:0"]);
            a.AddRange(["-map", $"{ai}:a?", "-c", "copy", "-map_metadata", "0", "-avoid_negative_ts", "make_zero"]);
            if (r.Output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || r.Output.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
                a.AddRange(["-movflags", "+faststart"]);
            a.Add(r.Output);
            return a;
        }

        // ------------------------------------------------------------------ video
        double kbpsTotal = 0, videoKbps = 0;
        int audioKbps = s.AudioKbps;
        if (s.TargetSize)
        {
            kbpsTotal = s.TargetMB * 1024 * 1024 * 8 / 1000 / r.OutputDuration * 0.94 * r.BitrateScale; // container headroom
            if (audioWanted) audioKbps = (int)Math.Clamp(Math.Min(audioKbps, kbpsTotal * 0.2), 24, 512);
            videoKbps = Math.Max(40, kbpsTotal - (audioWanted ? audioKbps : 0));
        }

        if (info.HasVideo)
        {
            a.AddRange(["-map", "0:v:0"]);
            var vf = new List<string>();
            if (Math.Abs(ed.Speed - 1) > 0.001) vf.Add($"setpts=PTS/{I(ed.Speed)}");
            if (s.MaxShortSide > 0 && Math.Min(info.Width, info.Height) > s.MaxShortSide)
            {
                int w, h;
                if (info.Width >= info.Height) { h = s.MaxShortSide; w = Even(info.Width * (double)h / info.Height); }
                else { w = s.MaxShortSide; h = Even(info.Height * (double)w / info.Width); }
                vf.Add($"scale={w}:{h}:flags=lanczos");
            }
            bool capped = s.FpsCap > 0 && info.Fps > s.FpsCap + 0.5;
            if (capped) vf.Add($"fps={s.FpsCap}");
            if (vf.Count > 0) a.AddRange(["-vf", string.Join(",", vf)]);
            // Game captures stamp frames at uneven times; editors and many players assume an even rate and drift.
            if (ed.FixSync && !capped && CfrRate(info) is { } rate) a.AddRange(["-fps_mode", "cfr", "-r", rate]);

            var speed = enc.Speeds[Math.Clamp(s.Speed, 0, enc.Speeds.Length - 1)];
            var q = s.Quality.ToString(CultureInfo.InvariantCulture);
            var fps = info.Fps > 1 ? info.Fps : 30;
            var gop = ((int)Math.Clamp(Math.Round(fps * 10), 60, 600)).ToString(CultureInfo.InvariantCulture);
            var vb = $"{(int)videoKbps}k";
            bool tenBit = enc.Family is Family.AV1 or Family.HEVC;

            a.AddRange(["-c:v", enc.Ff]);
            switch (enc.Id)
            {
                case "svtav1":
                    a.AddRange(["-preset", speed, "-g", gop, "-svtav1-params", "tune=0", "-pix_fmt", "yuv420p10le"]);
                    a.AddRange(s.TargetSize ? ["-b:v", vb] : ["-crf", q]);
                    break;
                case "x265":
                    a.AddRange(["-preset", speed, "-pix_fmt", "yuv420p10le"]);
                    var xp = "log-level=error";
                    if (s.TargetSize) { a.AddRange(["-b:v", vb]); if (pass > 0) xp += $":pass={pass}:stats={passLog!.Replace("\\", "/").Replace(":", "\\:")}.x265"; }
                    else a.AddRange(["-crf", q]);
                    a.AddRange(["-x265-params", xp]);
                    break;
                case "x264":
                    a.AddRange(["-preset", speed, "-pix_fmt", "yuv420p"]);
                    if (s.TargetSize) { a.AddRange(["-b:v", vb]); if (pass > 0) a.AddRange(["-pass", pass.ToString(), "-passlogfile", passLog!]); }
                    else a.AddRange(["-crf", q]);
                    break;
                default:
                    if (enc.Id.EndsWith("_nvenc"))
                    {
                        a.AddRange(["-preset", speed, "-tune", "hq", "-multipass", "fullres", "-rc-lookahead", "32",
                                    "-spatial-aq", "1", "-temporal-aq", "1", "-pix_fmt", tenBit ? "p010le" : "yuv420p", "-rc", "vbr"]);
                        a.AddRange(s.TargetSize
                            ? ["-b:v", vb, "-maxrate", $"{(int)(videoKbps * 1.2)}k", "-bufsize", $"{(int)(videoKbps * 1.5)}k"]
                            : ["-cq", q, "-b:v", "0"]);
                    }
                    else if (enc.Id.EndsWith("_qsv"))
                    {
                        a.AddRange(["-preset", speed, "-pix_fmt", tenBit ? "p010le" : "nv12"]);
                        a.AddRange(s.TargetSize
                            ? ["-b:v", vb, "-maxrate", $"{(int)(videoKbps * 1.2)}k", "-bufsize", $"{(int)(videoKbps * 1.5)}k"]
                            : ["-global_quality", q]);
                    }
                    else // amf
                    {
                        a.AddRange(["-quality", speed, "-pix_fmt", "nv12"]);
                        a.AddRange(s.TargetSize
                            ? ["-rc", "vbr_peak", "-b:v", vb, "-maxrate", $"{(int)(videoKbps * 1.2)}k"]
                            : ["-rc", "cqp", "-qp_i", q, "-qp_p", q]);
                    }
                    break;
            }
            if (enc.Family == Family.HEVC && s.Container == "mp4") a.AddRange(["-tag:v", "hvc1"]);
        }

        // ------------------------------------------------------------------ audio
        if (!audioOn)
        {
            a.Add("-an");
        }
        else
        {
            a.AddRange(["-map", $"{ai}:a?"]);
            if (af.Count > 0) a.AddRange(["-af", string.Join(",", af)]);
            a.AddRange(codec switch
            {
                "copy" => ["-c:a", "copy"],
                "aac" => ["-c:a", "aac", "-b:a", $"{audioKbps}k"],
                _ => ["-c:a", "libopus", "-b:a", $"{audioKbps}k"],
            });
        }

        a.AddRange(["-sn", "-dn", "-map_metadata", "0", "-map_chapters", "-1", "-max_muxing_queue_size", "4096"]);
        if (pass == 1) { a.AddRange(["-f", "null", "NUL"]); return a; }
        if (s.Container == "mp4") a.AddRange(["-movflags", "+faststart"]);
        a.Add(r.Output);
        return a;
    }

    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    private static readonly (double Fps, string Rate)[] StandardRates =
    [
        (23.976, "24000/1001"), (24, "24"), (25, "25"), (29.97, "30000/1001"), (30, "30"), (48, "48"), (50, "50"),
        (59.94, "60000/1001"), (60, "60"), (90, "90"), (100, "100"), (119.88, "120000/1001"), (120, "120"),
        (144, "144"), (165, "165"), (240, "240"),
    ];

    /// <summary>
    /// The even frame rate a capture was aiming for (59.93 fps on average → 60), or null when the source is
    /// genuinely variable (screen recordings that only emit frames on change) and must keep its own timing.
    /// </summary>
    public static string? CfrRate(MediaInfo info)
    {
        if (info.Fps <= 1) return null;
        var (fps, rate) = StandardRates.MinBy(x => Math.Abs(x.Fps - info.Fps));
        return Math.Abs(fps - info.Fps) / fps <= 0.03 ? rate : null;
    }

    private static List<double> Atempo(double speed)
    {
        var parts = new List<double>();
        var x = speed;
        while (x > 2.0) { parts.Add(2.0); x /= 2.0; }
        while (x < 0.5) { parts.Add(0.5); x /= 0.5; }
        parts.Add(x);
        return parts;
    }

    // ------------------------------------------------------------------ encode

    /// <summary>
    /// Full encode of one request: runs every pass and, in target-size mode, re-encodes with a lower
    /// bitrate if the first result overshoots. Progress: (fraction 0..1, speed ×realtime, stage label).
    /// </summary>
    public static async Task EncodeAsync(EncodeRequest r, Action<double, double, string> progress, CancellationToken ct)
    {
        var req = r;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var cmds = Build(req);
            for (int pass = 0; pass < cmds.Count; pass++)
            {
                int p = pass;
                var stage = (attempt > 0 ? "refit · " : "") + (cmds.Count > 1 ? $"pass {p + 1}/{cmds.Count} · " : "");
                await RunAsync(cmds[p], req.OutputDuration, (f, sp) => progress((p + f) / cmds.Count, sp, stage), ct);
            }
            if (!req.S.TargetSize) return;
            var limit = req.S.TargetMB * 1024 * 1024;
            var actual = new FileInfo(req.Output).Length;
            if (actual <= limit) return;
            req = req with { BitrateScale = req.BitrateScale * limit / actual * 0.95 };
        }
    }

    // ------------------------------------------------------------------ runner

    /// <summary>Runs ffmpeg reporting (fraction 0..1, speed ×realtime). Throws OperationCanceledException on cancel.</summary>
    public static async Task RunAsync(List<string> args, double outDuration, Action<double, double> progress, CancellationToken ct)
    {
        using var p = new Process { StartInfo = Ff.StartInfo(Ff.Ffmpeg, args) };
        p.Start();
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        var errTail = new LinkedList<string>();
        var errTask = Task.Run(async () =>
        {
            string? l;
            while ((l = await p.StandardError.ReadLineAsync()) != null)
                lock (errTail) { errTail.AddLast(l); if (errTail.Count > 12) errTail.RemoveFirst(); }
        });

        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        double speed = 0;
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync()) != null)
        {
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq];
            var val = line[(eq + 1)..].Trim();
            if (key == "out_time_us" && long.TryParse(val, out var us) && us > 0)
                progress(Math.Clamp(us / 1e6 / outDuration, 0, 1), speed);
            else if (key == "speed" && double.TryParse(val.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var sp))
                speed = sp;
            else if (key == "progress" && val == "end")
                progress(1, speed);
        }
        await p.WaitForExitAsync(CancellationToken.None);
        await errTask;
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            string msg;
            lock (errTail) msg = string.Join("\n", errTail).Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(msg) ? $"ffmpeg exited with code {p.ExitCode}" : msg);
        }
    }

    // --------------------------------------------------------------- estimate

    public sealed record Estimate(long Bytes, string? SamplePath);

    /// <summary>Encodes a few short samples across the clip and extrapolates the final size.</summary>
    public static async Task<Estimate> EstimateAsync(EncodeRequest r, Action<double> progress, CancellationToken ct)
    {
        if (r.S.TargetSize) return new((long)(r.S.TargetMB * 1024 * 1024 * 0.97), null);
        if (r.IsCopy) return new((long)(r.Info.SizeBytes * Math.Min(1, r.InputSpan / Math.Max(0.1, r.Info.Duration))), null);

        var span = r.InputSpan;
        var start = r.TrimStart;
        const double len = 4;
        double[] positions = span <= len * 4 ? [start] : [start + span * 0.18, start + span * 0.5 - len / 2, start + span * 0.82 - len];
        double sampleLen = Math.Min(len, span);

        var dir = Ff.TempDirFor(r.Info.Path);
        var run = Guid.NewGuid().ToString("N")[..6];
        long bytes = 0;
        double secs = 0;
        string? middle = null;
        for (int i = 0; i < positions.Length; i++)
        {
            var outFile = Path.Combine(dir, $"sample_{run}_{i}{Path.GetExtension(r.Output)}");
            var req = r with { TrimStart = positions[i], TrimEnd = positions[i] + sampleLen, Output = outFile };
            var cmd = Build(req)[0];
            int idx = i;
            await RunAsync(cmd, req.OutputDuration, (f, _) => progress((idx + f) / positions.Length), ct);
            bytes += new FileInfo(outFile).Length;
            secs += sampleLen;
            if (i == positions.Length / 2) middle = outFile;
        }
        return new((long)(bytes / secs * span), middle);
    }

    // ----------------------------------------------------------- preview proxy

    /// <summary>Fast low-res H.264 copy for sources Windows can't decode (HEVC/AV1/VP9 without extensions…).</summary>
    public static async Task<string> MakeProxyAsync(MediaInfo info, Action<double> progress, CancellationToken ct)
    {
        var outFile = Path.Combine(Ff.TempDirFor(info.Path), "proxy.mp4");
        if (File.Exists(outFile) && new FileInfo(outFile).Length > 0) return outFile;
        var hw = Encoders.IsAvailable("h264_nvenc") ? "h264_nvenc" : Encoders.IsAvailable("h264_qsv") ? "h264_qsv" : null;
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats", "-i", info.Path,
                                      "-map", "0:v:0", "-map", "0:a:0?", "-vf", "scale=-2:'min(540,ih)',format=yuv420p" };
        args.AddRange(hw != null ? ["-c:v", hw, "-preset", "p1", "-b:v", "3M"] : ["-c:v", "libx264", "-preset", "ultrafast", "-crf", "30"]);
        args.AddRange(["-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", outFile + ".part.mp4"]);
        await RunAsync(args, Math.Max(0.1, info.Duration), (f, _) => progress(f), ct);
        File.Move(outFile + ".part.mp4", outFile, true);
        return outFile;
    }

    /// <summary>
    /// Soundtrack alone (AAC) for the live audio-offset preview. Re-timed from its timestamps so position 0
    /// matches the video's position 0 even when the capture started the audio late.
    /// </summary>
    public static async Task<string> ExtractAudioAsync(MediaInfo info, CancellationToken ct)
    {
        var outFile = Path.Combine(Ff.TempDirFor(info.Path), "audio.m4a");
        if (File.Exists(outFile) && new FileInfo(outFile).Length > 0) return outFile;
        await RunAsync(["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats", "-i", info.Path,
                        "-map", "0:a:0", "-vn", "-af", "aresample=async=1:first_pts=0", "-c:a", "aac", "-b:a", "160k",
                        "-movflags", "+faststart", outFile + ".part.m4a"],
                       Math.Max(0.1, info.Duration), (_, _) => { }, ct);
        File.Move(outFile + ".part.m4a", outFile, true);
        return outFile;
    }

    // ---------------------------------------------------------------- naming

    public static string UniqueOutput(string dir, string input, string suffix, string ext)
    {
        var stem = Path.GetFileNameWithoutExtension(input);
        var p = Path.Combine(dir, stem + suffix + ext);
        int i = 2;
        while (File.Exists(p) || string.Equals(Path.GetFullPath(p), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
            p = Path.Combine(dir, $"{stem}{suffix} ({i++}){ext}");
        return p;
    }

    public static string Size(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int k = 0;
        while (v >= 1024 && k < u.Length - 1) { v /= 1024; k++; }
        return k == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " " + u[k];
    }

    public static string Time(double seconds, bool fraction = false)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        var core = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
        return fraction ? core + "." + (t.Milliseconds / 100) : core;
    }
}
