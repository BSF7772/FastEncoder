using Microsoft.UI.Xaml;

namespace FastEncoder;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs();
        var st = Array.IndexOf(argv, "--selftest");
        if (st >= 0)
        {
            var report = argv.Length > st + 1 ? argv[st + 1] : Path.Combine(Path.GetTempPath(), "FastEncoder-selftest.txt");
            int code;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                code = await Task.Run(() => Core.SelfTest.RunAsync(report));
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(report, Environment.NewLine + "CRASH " + ex); } catch { }
                code = 2;
            }
            Environment.Exit(code);
        }

        // FastEncoder.exe --syncdetect <video> <report.txt>: writes the measured audio offset (for diagnosing desync)
        var sd = Array.IndexOf(argv, "--syncdetect");
        if (sd >= 0 && argv.Length > sd + 2)
        {
            int code = 0;
            try
            {
                Core.Ff.Locate();
                var info = await Core.Ff.ProbeAsync(argv[sd + 1]) ?? throw new InvalidOperationException("can't read " + argv[sd + 1]);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = await Task.Run(() => Core.AudioSync.DetectAsync(info.Path, 0, info.Duration, null, CancellationToken.None));
                File.WriteAllText(argv[sd + 2], $"{r.AudioLate * 1000:+0;-0} ms audio late  {r.Confidence}  agree {r.Agreeing}/{r.Consistent}  {sw.Elapsed.TotalSeconds:0.0}s" + Environment.NewLine);
            }
            catch (Exception ex) { File.WriteAllText(argv[sd + 2], "ERROR " + ex); code = 2; }
            Environment.Exit(code);
        }

        _window = new MainWindow();
        _window.Activate();
    }
}
