using System.Text;

namespace FastEncoder.Core;

/// <summary>Headless check: FastEncoder.exe --selftest [report.txt]. Encodes a generated clip with every preset.</summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string reportPath)
    {
        var log = new StringBuilder();
        int failures = 0;
        void Log(string s) { log.AppendLine(s); File.WriteAllText(reportPath, log.ToString()); }

        Ff.Locate();
        Log($"ffmpeg:  {Ff.Ffmpeg}\nffprobe: {Ff.Ffprobe}");
        if (!Ff.Available) { Log("FAIL ffmpeg not found"); return 1; }
        await Encoders.DetectAsync();
        Log("encoders: " + string.Join(", ", Encoders.Available.Select(e => e.Id)));

        var dir = Path.Combine(Ff.TempRoot, "selftest");
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "source.mp4");
        var gen = await Ff.RunAsync(Ff.Ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=8",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:d=8",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12", "-g", "30", "-c:a", "pcm_s16le", "-shortest",
            Path.ChangeExtension(src, ".mov"),
        ]);
        src = Path.ChangeExtension(src, ".mov");
        if (gen.ExitCode != 0) { Log("FAIL generating source: " + gen.StdErr); return 1; }

        var info = await Ff.ProbeAsync(src);
        Log($"probe: {info?.Width}x{info?.Height} {info?.Fps:0.##}fps {info?.Duration:0.00}s {info?.VideoCodec}/{info?.AudioCodec} {info?.SizeBytes} B");
        if (info == null) return 1;

        var strip = await Ff.FilmstripAsync(src, info.Duration, 6, 64, Path.Combine(dir, "strip"), CancellationToken.None);
        Log($"filmstrip: {strip.Count(s => s != null)}/6 frames");
        if (strip.Any(s => s == null)) failures++;

        var edits = new EditSettings { Speed = 1.5, Volume = 1.3 };
        var cases = Presets.All.Select(p => (p.Id, p.Build())).ToList();
        cases.Add(("x264-2pass", new EncodeSettings { Encoder = "x264", Speed = 6, TargetSize = true, Audio = "opus", AudioKbps = 64 }));
        cases.Add(("x265-2pass", new EncodeSettings { Encoder = "x265", Speed = 6, TargetSize = true, Audio = "aac", AudioKbps = 96 }));
        cases.Add(("scale480", new EncodeSettings { Encoder = "svtav1", Quality = 35, Speed = 8, MaxShortSide = 480, FpsCap = 24, Container = "mkv" }));
        if (Encoders.IsAvailable("hevc_nvenc")) cases.Add(("hevc_nvenc", new EncodeSettings { Encoder = "hevc_nvenc", Quality = 28 }));
        foreach (var (id, s) in cases)
        {
            var p = new { Id = id };
            if (s.TargetSize) s.TargetMB = 1;
            var outFile = Path.Combine(dir, $"out_{p.Id}{Pipeline.OutputExtension(info, s)}");
            var req = new EncodeRequest(info, 1, 7, s, p.Id == "trim" ? new EditSettings() : edits, outFile);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double lastFrac = 0;
            try
            {
                await Pipeline.EncodeAsync(req, (f, _, _) => lastFrac = f, CancellationToken.None);
                var o = await Ff.ProbeAsync(outFile);
                var ok = o != null && o.HasVideo && Math.Abs(o.Duration - req.OutputDuration) < 0.5
                         && (!s.TargetSize || new FileInfo(outFile).Length <= s.TargetMB * 1024 * 1024);
                if (!ok) failures++;
                Log($"{(ok ? "PASS" : "FAIL")} {p.Id,-10} {s.Encoder,-11} {new FileInfo(outFile).Length,9} B  {o?.Width}x{o?.Height} {o?.VideoCodec}/{o?.AudioCodec} dur {o?.Duration:0.00}s (want {req.OutputDuration:0.00})  progress {lastFrac:0.00}  {sw.Elapsed.TotalSeconds:0.0}s");
            }
            catch (Exception ex)
            {
                failures++;
                Log($"FAIL {p.Id,-10} {s.Encoder}: {ex.Message}\n     cmd: {string.Join(" ", Pipeline.Build(req)[0])}");
            }
        }

        failures += await SyncTestsAsync(dir, Log);

        try
        {
            var bal = Presets.Get("balanced")!.Build();
            var est = await Pipeline.EstimateAsync(new EncodeRequest(info, 0, null, bal, new EditSettings(), Path.Combine(dir, "e.mp4")), _ => { }, CancellationToken.None);
            Log($"PASS estimate  {est.Bytes} B, sample {(est.SamplePath != null ? "ok" : "none")}");
        }
        catch (Exception ex) { failures++; Log("FAIL estimate: " + ex.Message); }

        try
        {
            var proxy = await Pipeline.MakeProxyAsync(info, _ => { }, CancellationToken.None);
            Log($"PASS proxy     {new FileInfo(proxy).Length} B");
        }
        catch (Exception ex) { failures++; Log("FAIL proxy: " + ex.Message); }

        Log(failures == 0 ? "ALL PASSED" : $"{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Audio sync: a clip whose clicks land 300 ms after its flashes must be measured as +300 ms, come out of
    /// an encode with −300 ms applied in sync (encoded and lossless paths), and a jittery capture must come out
    /// at an even frame rate.
    /// </summary>
    private static async Task<int> SyncTestsAsync(string dir, Action<string> log)
    {
        int failures = 0;
        const double late = 0.3;
        // Events from two trains with unrelated periods (so no wrong lag lines up all of them). Each event flips
        // the picture between dark and bright — one clean edge per event — and plays a 10 ms click `late` after.
        const string flips = "mod(floor(T*1.3)+floor(T*0.77+0.2),2)";
        var clicks = $"lt(mod((t-{late})*1.3,1),0.013)+lt(mod((t-{late})*0.77+0.2,1),0.008)";
        var src = Path.Combine(dir, "sync_late300.mp4");
        var gen = await Ff.RunAsync(Ff.Ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "color=c=black:s=320x180:r=60:d=14",
            "-f", "lavfi", "-i", $"aevalsrc='0.8*min(1,{clicks})*sin(2*PI*2500*t)+0.01*(random(0)-0.5)':s=48000:d=14",
            "-vf", $"geq=lum='16+219*{flips}':cb=128:cr=128",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-g", "30", "-c:a", "aac", "-b:a", "128k", "-shortest", src,
        ]);
        if (gen.ExitCode != 0) { log("FAIL sync source: " + gen.StdErr); return 1; }
        var info = (await Ff.ProbeAsync(src))!;

        async Task<AudioSync.Result> Measure(string file)
        {
            var i = (await Ff.ProbeAsync(file))!;
            return await AudioSync.DetectAsync(file, 0, i.Duration, null, CancellationToken.None);
        }

        try
        {
            var r = await Measure(src);
            var ok = r.Confidence == AudioSync.Confidence.High && Math.Abs(r.AudioLate - late) < 0.035;
            if (!ok) failures++;
            log($"{(ok ? "PASS" : "FAIL")} sync detect   {r.AudioLate * 1000:+0;-0} ms (want +{late * 1000:0})  {r.Confidence} {r.Agreeing}/{r.Consistent}");

            foreach (var (name, s) in new[]
            {
                ("sync fix enc", new EncodeSettings { Encoder = "x264", Quality = 23, Speed = 8, Audio = "aac", AudioKbps = 128 }),
                ("sync fix copy", new EncodeSettings { Encoder = "copy", Audio = "copy" }),
            })
            {
                var outFile = Path.Combine(dir, $"sync_{s.Encoder}{Pipeline.OutputExtension(info, s)}");
                var req = new EncodeRequest(info, 1, 13, s, new EditSettings(), outFile, AudioDelay: -late);
                await Pipeline.EncodeAsync(req, (_, _, _) => { }, CancellationToken.None);
                var o = await Measure(outFile);
                var oi = await Ff.ProbeAsync(outFile);
                var good = o.Confidence == AudioSync.Confidence.High && Math.Abs(o.AudioLate) < 0.035 && Math.Abs(oi!.Duration - 12) < 0.5;
                if (!good) failures++;
                log($"{(good ? "PASS" : "FAIL")} {name,-13} after −{late * 1000:0} ms: {o.AudioLate * 1000:+0;-0} ms {o.Confidence}  dur {oi?.Duration:0.00}s");
            }
        }
        catch (Exception ex) { failures++; log("FAIL sync: " + ex.Message); }

        // jittery capture (frames stamped ±6 ms off the 30 fps grid) → even frame rate when FixSync is on
        try
        {
            var vfr = Path.Combine(dir, "vfr.mkv");
            var g = await Ff.RunAsync(Ff.Ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=6", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:d=6",
                "-vf", "setpts='(N/30+0.006*sin(N*1.7))/TB'", "-fps_mode", "passthrough",
                "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", vfr,
            ]);
            if (g.ExitCode != 0) throw new InvalidOperationException(g.StdErr);
            var vi = (await Ff.ProbeAsync(vfr))!;
            var outFile = Path.Combine(dir, "vfr_fixed.mp4");
            var s = new EncodeSettings { Encoder = "x264", Quality = 28, Speed = 8, Audio = "aac", AudioKbps = 96 };
            await Pipeline.EncodeAsync(new EncodeRequest(vi, 0, null, s, new EditSettings(), outFile), (_, _, _) => { }, CancellationToken.None);
            var p = await Ff.RunAsync(Ff.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", outFile]);
            var pts = p.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).Order().ToArray();
            var worst = pts.Select((t, i) => Math.Abs(t - pts[0] - i / 30.0)).Max();
            var ok = Pipeline.CfrRate(vi) == "30" && worst < 0.002;
            if (!ok) failures++;
            log($"{(ok ? "PASS" : "FAIL")} cfr           source {vi.Fps:0.###} fps → rate {Pipeline.CfrRate(vi)}, {pts.Length} frames, worst grid error {worst * 1000:0.0} ms");
        }
        catch (Exception ex) { failures++; log("FAIL cfr: " + ex.Message); }

        return failures;
    }
}
