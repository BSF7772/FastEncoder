using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace FastEncoder.Core;

/// <summary>
/// Measures how far the soundtrack is shifted against the picture: sound onsets (shots, hits, beats)
/// are lined up with sudden picture changes (muzzle flashes, impacts, cuts). The answer is cross-checked
/// by repeating the match with the audio deliberately shifted — a real offset moves by exactly that
/// amount, a coincidental match (e.g. automatic fire repeating every 150 ms) does not.
/// </summary>
public static class AudioSync
{
    public enum Confidence { None, Low, High }

    /// <param name="AudioLate">Seconds the sound arrives after the picture (negative = early). Fix = delay audio by −AudioLate.</param>
    public sealed record Result(double AudioLate, Confidence Confidence, int Agreeing, int Consistent);

    /// <summary>
    /// Offsets people don't notice (ITU-R BT.1359: sound early is spotted from ~45 ms, sound late only from ~100 ms;
    /// games also play sounds a few frames late on purpose), so they are reported as "in sync".
    /// </summary>
    public static bool InSync(double audioLate) => audioLate > -0.045 && audioLate < 0.080;

    private const int R = 200;          // envelope rate (Hz) → 5 ms resolution
    private const int Sr = 16000;
    private const int Win = 512;
    private const int W = 192, H = 108, G = 8;
    private const double MaxLag = 1.5;  // seconds searched either way
    private static readonly double[] Shifts = [0, 0.2, -0.3, 0.5];

    /// <summary>Analyses up to <paramref name="maxSpan"/> seconds from the middle of [start, end].</summary>
    public static async Task<Result> DetectAsync(string path, double start, double end, Action<double>? progress,
                                                 CancellationToken ct, double maxSpan = 75)
    {
        var len = Math.Max(1, end - start);
        if (len > maxSpan) { start += (len - maxSpan) / 2; len = maxSpan; }

        var audioTask = Task.Run(() => DecodeAudioAsync(path, start, len, ct), ct);
        var video = await Task.Run(() => DecodeVideoAsync(path, start, len, progress, ct), ct);
        var audio = await audioTask;
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() => Analyse(audio, video), ct);
    }

    // ------------------------------------------------------------------ analysis

    private sealed record VideoFeatures(double[] Pts, double[][] Values);   // Values[feature][frame]

    private static Result Analyse(float[] audio, VideoFeatures video)
    {
        var afeat = AudioFeatures(audio);
        int n = afeat[0].Length;
        if (n < R * 3 || video.Pts.Length < 30) return new(0, Confidence.None, 0, 0);

        var vgrid = video.Values.Select(v => Prep(ToGrid(video.Pts, v, n))).ToArray();
        int maxLag = (int)(MaxLag * R);

        // per (audio feature × video feature): lag at every shift, corrected back by the shift
        var items = new List<(double Lag, double Ratio)>();
        foreach (var af in afeat)
            foreach (var vg in vgrid)
            {
                var lags = new double[Shifts.Length];
                double ratioSum = 0;
                for (int k = 0; k < Shifts.Length; k++)
                {
                    var (lag, ratio) = XCorr(Prep(Shift(af, (int)Math.Round(Shifts[k] * R))), vg, maxLag);
                    lags[k] = lag / (double)R - Shifts[k];
                    ratioSum += ratio;
                }
                if (lags.Max() - lags.Min() <= 0.020)
                    items.Add((Median(lags), ratioSum / Shifts.Length));
            }

        if (items.Count == 0) return new(0, Confidence.None, 0, 0);

        // largest group of consistent pairs agreeing within ±40 ms, weighted by peak dominance
        List<(double Lag, double Ratio)> best = [];
        double bestScore = double.MinValue;
        foreach (var it in items)
        {
            var grp = items.Where(o => Math.Abs(o.Lag - it.Lag) <= 0.040).ToList();
            var score = grp.Sum(g => g.Ratio - 1);
            if (score > bestScore) { bestScore = score; best = grp; }
        }
        var wsum = best.Sum(g => g.Ratio - 0.99);
        var late = best.Sum(g => g.Lag * (g.Ratio - 0.99)) / wsum;
        var conf = best.Count >= 3 && best.Count * 2 > items.Count && best.Max(g => g.Ratio) >= 1.3 ? Confidence.High : Confidence.Low;
        return new(late, conf, best.Count, items.Count);
    }

    /// <summary>Spectral flux, high-band flux (&gt;2 kHz) and loudness rise, at R Hz.</summary>
    private static double[][] AudioFeatures(float[] samples)
    {
        // Frame f is centred on sample f·hop (half a window of silence in front); stamping a window with its
        // start instead reads every sound up to one window (32 ms) early.
        var a = new float[samples.Length + Win / 2];
        Array.Copy(samples, 0, a, Win / 2, samples.Length);
        int hop = Sr / R;
        int n = Math.Max(0, (a.Length - Win) / hop);
        var flux = new double[n];
        var hf = new double[n];
        var energy = new double[n];
        var window = new double[Win];
        for (int i = 0; i < Win; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Win - 1));

        int bins = Win / 2 + 1, hfBin = (int)(2000 / ((double)Sr / Win));
        var prev = new double[bins];
        var buf = new Complex[Win];
        double prevLog = 0;
        for (int f = 0; f < n; f++)
        {
            int o = f * hop;
            double sq = 0;
            for (int i = 0; i < Win; i++) { var x = a[o + i]; sq += x * x * window[i]; buf[i] = new Complex(x * window[i], 0); }
            Fft(buf);
            double fl = 0, fh = 0;
            for (int b = 0; b < bins; b++)
            {
                var m = Math.Log(1 + 100 * buf[b].Magnitude);
                var d = f == 0 ? 0 : Math.Max(0, m - prev[b]);
                fl += d;
                if (b >= hfBin) fh += d;
                prev[b] = m;
            }
            var lg = Math.Log(Math.Sqrt(sq / (Win / 2.0) + 1e-10));   // window-weighted, so centred like the flux
            energy[f] = f == 0 ? 0 : Math.Max(0, lg - prevLog);
            prevLog = lg;
            flux[f] = fl;
            hf[f] = fh;
        }
        return [flux, hf, energy];
    }

    /// <summary>Places per-frame values on the R Hz timeline using the real (variable) frame timestamps.</summary>
    private static double[] ToGrid(double[] pts, double[] vals, int n)
    {
        var g = new double[n];
        for (int i = 0; i < vals.Length; i++)
        {
            int k = (int)Math.Round(pts[i] * R);
            if (k >= 0 && k < n) g[k] = Math.Max(g[k], vals[i]);
        }
        return g;
    }

    private static double[] Shift(double[] x, int s)
    {
        if (s == 0) return x;
        var y = new double[x.Length];
        if (s > 0) Array.Copy(x, 0, y, s, Math.Max(0, x.Length - s));
        else Array.Copy(x, -s, y, 0, Math.Max(0, x.Length + s));
        return y;
    }

    /// <summary>Log-compress, ~20 ms blur, remove the slow trend (0.5 s), z-score.</summary>
    private static double[] Prep(double[] x)
    {
        int n = x.Length;
        var pos = x.Where(v => v > 0).ToArray();
        var med = pos.Length > 0 ? Median(pos) : 1;
        var y = new double[n];
        for (int i = 0; i < n; i++) y[i] = Math.Log(1 + x[i] / (med + 1e-9));

        var k = new double[25];
        for (int i = 0; i < 25; i++) k[i] = Math.Exp(-0.5 * Math.Pow((i - 12) / 4.0, 2));
        var ks = k.Sum();
        var b = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = 0;
            for (int j = 0; j < 25; j++) { int t = i + j - 12; if (t >= 0 && t < n) s += y[t] * k[j]; }
            b[i] = s / ks;
        }

        int w = R / 2;
        var cum = new double[n + 1];
        for (int i = 0; i < n; i++) cum[i + 1] = cum[i] + b[i];
        double mean = 0;
        for (int i = 0; i < n; i++)
        {
            int lo = i - w / 2, hi = lo + w;
            double s = cum[Math.Clamp(hi, 0, n)] - cum[Math.Clamp(lo, 0, n)];
            y[i] = b[i] - s / w;
            mean += y[i];
        }
        mean /= n;
        double var = 0;
        for (int i = 0; i < n; i++) var += (y[i] - mean) * (y[i] - mean);
        var sd = Math.Sqrt(var / n) + 1e-9;
        for (int i = 0; i < n; i++) y[i] = (y[i] - mean) / sd;
        return y;
    }

    /// <summary>Best lag (positive = audio after picture) and how much it beats the next peak ≥ 60 ms away.</summary>
    private static (int Lag, double Ratio) XCorr(double[] a, double[] v, int maxLag)
    {
        int m = Math.Min(a.Length, v.Length);
        var cc = new double[2 * maxLag + 1];
        Parallel.For(-maxLag, maxLag + 1, l =>
        {
            int i0 = Math.Max(0, -l), i1 = m - Math.Max(0, l);
            double s = 0;
            for (int i = i0; i < i1; i++) s += a[i + l] * v[i];
            cc[l + maxLag] = s / Math.Max(1, m - Math.Abs(l));
        });
        int best = 0;
        for (int i = 1; i < cc.Length; i++) if (cc[i] > cc[best]) best = i;
        double second = double.MinValue;
        for (int i = 0; i < cc.Length; i++)
            if (Math.Abs(i - best) > 0.06 * R && cc[i] > second) second = cc[i];
        return (best - maxLag, cc[best] / Math.Max(second, 1e-9));
    }

    private static double Median(double[] x)
    {
        var s = x.OrderBy(v => v).ToArray();
        return s.Length == 0 ? 0 : s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    private static void Fft(Complex[] buf)
    {
        int n = buf.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (buf[i], buf[j]) = (buf[j], buf[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            var w = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (int i = 0; i < n; i += len)
            {
                var wn = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = buf[i + k];
                    var t = buf[i + k + len / 2] * wn;
                    buf[i + k] = u + t;
                    buf[i + k + len / 2] = u - t;
                    wn *= w;
                }
            }
        }
    }

    // ------------------------------------------------------------------ decoding

    private static async Task<float[]> DecodeAudioAsync(string path, double start, double len, CancellationToken ct)
    {
        // first_pts=0 pads a late-starting track with silence so sample 0 is the same instant as video time 0
        using var p = Start(["-hide_banner", "-nostdin", "-loglevel", "error", "-ss", Ff.Sec(start), "-t", Ff.Sec(len), "-i", path,
                             "-map", "0:a:0", "-vn", "-af", "aresample=async=1:first_pts=0", "-ac", "1", "-ar", Sr.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-"]);
        using var reg = ct.Register(() => Kill(p));
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, CancellationToken.None);
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        var err = await errTask;
        if (p.ExitCode != 0) throw new InvalidOperationException("Couldn't read the audio: " + err.Trim());
        var bytes = ms.GetBuffer();
        var a = new float[ms.Length / 4];
        Buffer.BlockCopy(bytes, 0, a, 0, a.Length * 4);
        return a;
    }

    /// <summary>Decodes a small grey copy and keeps only per-frame change features + real timestamps.</summary>
    private static async Task<VideoFeatures> DecodeVideoAsync(string path, double start, double len, Action<double>? progress, CancellationToken ct)
    {
        using var p = Start(["-hide_banner", "-nostdin", "-loglevel", "info", "-hwaccel", "auto",
                             "-ss", Ff.Sec(start), "-t", Ff.Sec(len), "-i", path, "-map", "0:v:0", "-an",
                             "-vf", $"scale={W}:{H}:flags=area,format=gray,showinfo", "-fps_mode", "passthrough",
                             "-f", "rawvideo", "-"]);
        using var reg = ct.Register(() => Kill(p));

        var pts = new List<double>();
        var errTail = new List<string>();
        var errTask = Task.Run(async () =>
        {
            string? l;
            while ((l = await p.StandardError.ReadLineAsync()) != null)
            {
                var i = l.IndexOf("pts_time:", StringComparison.Ordinal);
                if (i >= 0)
                {
                    var s = l[(i + 9)..];
                    var e = s.IndexOf(' ');
                    if (double.TryParse(e > 0 ? s[..e] : s, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                        lock (pts) pts.Add(t);
                }
                else if (errTail.Count < 20 && !l.StartsWith(' ')) errTail.Add(l);
            }
        });

        const int cells = G * G;
        int ch = H / G, cw = W / G;
        var frame = new byte[W * H];
        var prev = new byte[W * H];
        var prevCells = new double[cells];
        var curCells = new double[cells];
        var motion = new List<double>();
        var flash = new List<double>();
        var cellChg = new List<double>();
        var lumUp = new List<double>();
        double prevLum = 0;
        int count = 0;
        var stream = p.StandardOutput.BaseStream;
        var diffs = new double[cells];
        var absd = new double[cells];

        while (true)
        {
            int got = 0;
            while (got < frame.Length)
            {
                int r = await stream.ReadAsync(frame.AsMemory(got), CancellationToken.None);
                if (r == 0) break;
                got += r;
            }
            if (got < frame.Length) break;

            long lumSum = 0;
            Array.Clear(curCells);
            for (int y = 0; y < H; y++)
            {
                int cy = Math.Min(G - 1, y / ch);
                for (int x = 0; x < W; x++) { int v = frame[y * W + x]; lumSum += v; curCells[cy * G + Math.Min(G - 1, x / cw)] += v; }
            }
            for (int c = 0; c < cells; c++) curCells[c] /= (double)ch * cw;
            double lum = lumSum / (double)(W * H);

            if (count > 0)
            {
                long md = 0;
                for (int i = 0; i < frame.Length; i++) md += Math.Abs(frame[i] - prev[i]);
                for (int c = 0; c < cells; c++) { diffs[c] = Math.Max(0, curCells[c] - prevCells[c]); absd[c] = Math.Abs(curCells[c] - prevCells[c]); }
                Array.Sort(diffs);
                Array.Sort(absd);
                motion.Add(md / (double)frame.Length);
                flash.Add((diffs[^1] + diffs[^2] + diffs[^3] + diffs[^4]) / 4);
                cellChg.Add(absd.Skip(cells - 6).Average());
                lumUp.Add(Math.Max(0, lum - prevLum));
            }
            (prev, frame) = (frame, prev);
            Array.Copy(curCells, prevCells, cells);
            prevLum = lum;
            count++;
            if (count % 30 == 0) progress?.Invoke(Math.Min(0.99, count / (len * 60)));
        }
        await p.WaitForExitAsync(CancellationToken.None);
        await errTask;
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0 && count == 0) throw new InvalidOperationException("Couldn't read the video: " + string.Join(" ", errTail).Trim());
        progress?.Invoke(1);

        double[] t;
        lock (pts) t = pts.Skip(1).Take(motion.Count).ToArray();   // feature i compares frame i+1 with frame i
        int k = Math.Min(t.Length, motion.Count);
        return new(t[..k], [motion.Take(k).ToArray(), flash.Take(k).ToArray(), cellChg.Take(k).ToArray(), lumUp.Take(k).ToArray()]);
    }

    private static Process Start(List<string> args)
    {
        var psi = Ff.StartInfo(Ff.Ffmpeg, args);
        psi.StandardOutputEncoding = null;
        var p = new Process { StartInfo = psi };
        p.Start();
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        return p;
    }

    private static void Kill(Process p) { try { p.Kill(true); } catch { } }
}
