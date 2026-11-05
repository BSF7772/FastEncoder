using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FastEncoder.Core;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr);

public sealed class MediaInfo
{
    public string Path { get; init; } = "";
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public string VideoCodec { get; set; } = "";
    public string AudioCodec { get; set; } = "";
    public int AudioStreams { get; set; }
    public long SizeBytes { get; set; }
    public bool HasVideo => Width > 0 && Height > 0;
    public bool HasAudio => AudioStreams > 0;
}

/// <summary>Locates and drives ffmpeg / ffprobe. Everything here is UI-free.</summary>
public static class Ff
{
    public static string Ffmpeg { get; private set; } = "";
    public static string Ffprobe { get; private set; } = "";
    public static bool Available => Ffmpeg.Length > 0 && Ffprobe.Length > 0;

    public static string TempRoot { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FastEncoder");

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".ts", ".mts", ".m2ts",
        ".mpg", ".mpeg", ".3gp", ".ogv", ".vob", ".mxf", ".f4v", ".divx", ".asf",
    };

    public static void Locate()
    {
        Ffmpeg = Find("ffmpeg.exe");
        Ffprobe = Find("ffprobe.exe");
        Directory.CreateDirectory(TempRoot);
    }

    private static string Find(string exe)
    {
        var local = System.IO.Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(local)) return local;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var p = System.IO.Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch { /* malformed PATH entry */ }
        }

        // winget installs (Gyan.FFmpeg / BtbN) that are not on PATH for GUI launches
        var winget = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet");
        var link = System.IO.Path.Combine(winget, "Links", exe);
        if (File.Exists(link)) return link;
        try
        {
            var pkgs = System.IO.Path.Combine(winget, "Packages");
            if (Directory.Exists(pkgs))
                foreach (var d in Directory.EnumerateDirectories(pkgs, "*FFmpeg*"))
                {
                    var hit = Directory.EnumerateFiles(d, exe, SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) return hit;
                }
        }
        catch { }
        return "";
    }

    public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static async Task<ProcResult> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = StartInfo(exe, args) };
        p.Start();
        var so = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var se = p.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw;
        }
        return new ProcResult(p.ExitCode, await so, await se);
    }

    // ------------------------------------------------------------------ probe

    public static async Task<MediaInfo?> ProbeAsync(string path, CancellationToken ct = default)
    {
        var r = await RunAsync(Ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], ct);
        if (r.ExitCode != 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            var root = doc.RootElement;
            var info = new MediaInfo { Path = path };
            try { info.SizeBytes = new FileInfo(path).Length; } catch { }

            if (root.TryGetProperty("format", out var fmt))
                info.Duration = Num(fmt, "duration");

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = Str(s, "codec_type");
                    if (type == "video" && !info.HasVideo && !IsAttachedPic(s))
                    {
                        info.VideoCodec = Str(s, "codec_name");
                        info.Width = (int)Num(s, "width");
                        info.Height = (int)Num(s, "height");
                        info.Fps = Rate(Str(s, "avg_frame_rate"));
                        if (info.Fps <= 0 || info.Fps > 1000) info.Fps = Rate(Str(s, "r_frame_rate"));
                        if (Math.Abs(Rotation(s)) == 90) (info.Width, info.Height) = (info.Height, info.Width);
                        if (info.Duration <= 0) info.Duration = Num(s, "duration");
                    }
                    else if (type == "audio")
                    {
                        if (info.AudioStreams == 0) info.AudioCodec = Str(s, "codec_name");
                        info.AudioStreams++;
                    }
                }
            }
            return info;
        }
        catch { return null; }
    }

    private static bool IsAttachedPic(JsonElement s) =>
        s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1;

    private static int Rotation(JsonElement s)
    {
        if (s.TryGetProperty("side_data_list", out var sdl))
            foreach (var sd in sdl.EnumerateArray())
                if (sd.TryGetProperty("rotation", out var rot)) return (int)Math.Round(rot.GetDouble());
        if (s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rt)
            && int.TryParse(rt.GetString(), out var r)) return r;
        return 0;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private static double Rate(string r)
    {
        var parts = r.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0)
            return a / b;
        return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    }

    // ------------------------------------------------------------ thumbnails

    public static async Task<string?> FrameAsync(string input, double seconds, int height, string outPath, CancellationToken ct = default)
    {
        var r = await RunAsync(Ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", Sec(seconds), "-i", input, "-frames:v", "1",
            "-vf", $"scale=-2:{height}:flags=fast_bilinear", "-q:v", "5", "-y", outPath,
        ], ct);
        return r.ExitCode == 0 && File.Exists(outPath) ? outPath : null;
    }

    /// <summary>Grab <paramref name="count"/> evenly spaced frames in parallel (fast keyframe seeks).</summary>
    public static async Task<string?[]> FilmstripAsync(string input, double duration, int count, int height, string dir, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        using var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var t = duration * (i + 0.5) / count;
                return await FrameAsync(input, t, height, System.IO.Path.Combine(dir, $"strip_{i:00}.jpg"), ct);
            }
            finally { gate.Release(); }
        });
        return await Task.WhenAll(tasks);
    }

    // ------------------------------------------------------------- helpers

    public static string Sec(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);

    public static string TempDirFor(string input)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(input.ToLowerInvariant())))[..12];
        var dir = System.IO.Path.Combine(TempRoot, hash);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void CleanupTemp()
    {
        try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, true); } catch { }
    }
}
