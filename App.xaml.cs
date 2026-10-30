using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClaudeTaskBoard;

public partial class App : Application
{
    const string MutexName = "ClaudeTaskBoard.Singleton";
    const string ShowEventName = "ClaudeTaskBoard.Show";

    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    bool _primary; // this is the running board (not a second launch handing over, not a test mode)

    // UI-thread exceptions are logged and survived; a burst of them means something is truly broken,
    // so after that many in a minute the board is let go (and the hook relaunches it on the next prompt).
    const int ErrorBurst = 20;
    readonly Queue<DateTime> _uiErrors = new();

    // Why the process is ending, for the exit log line.
    public static string ExitReason { get; set; } = "unknown";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, a) =>
        {
            Log.Error("ui", a.Exception);
            var now = DateTime.UtcNow;
            _uiErrors.Enqueue(now);
            while (_uiErrors.Count > 0 && now - _uiErrors.Peek() > TimeSpan.FromMinutes(1)) _uiErrors.Dequeue();
            a.Handled = _uiErrors.Count < ErrorBurst;
            if (!a.Handled) ExitReason = "crash: repeated UI errors";
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            ExitReason = "crash: unhandled exception";
            Log.Error(a.IsTerminating ? "fatal" : "background", a.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("task", a.Exception); a.SetObserved(); };

        if (e.Args.Contains("--selftest"))
        {
            Shutdown(SelfTest.Run());
            return;
        }

        // --snapshot <png>: render the board off-screen from live data and exit (visual check / bug reports).
        int snapAt = Array.IndexOf(e.Args, "--snapshot");
        if (snapAt >= 0 && snapAt + 1 < e.Args.Length)
        {
            ApplyTheme(e.Args.Contains("--dark") ? false : e.Args.Contains("--light") ? true : null);
            var shot = new MainWindow { Left = -20000, Top = -20000 };
            shot.Show();
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            bool expand = e.Args.Contains("--expanded");
            t.Tick += async (_, _) =>
            {
                t.Stop();
                if (expand) shot.ExpandAll();
                if (e.Args.Contains("--settings")) shot.OpenSettings();
                await Task.Delay(400); // let enter animations finish
                shot.SaveSnapshot(e.Args[snapAt + 1]);
                Shutdown();
            };
            t.Start();
            return;
        }

        // Single instance: a second launch just brings the existing board forward.
        _mutex = new Mutex(true, MutexName, out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        _primary = true;
        Log.Info($"start v{typeof(App).Assembly.GetName().Version} exe={Environment.ProcessPath}");
        SessionEnding += (_, a) =>
        {
            ExitReason = $"windows {a.ReasonSessionEnding}";
            (MainWindow as MainWindow)?.AllowClose();
        };
        SystemEvents.PowerModeChanged += (_, a) =>
        {
            Log.Info($"power {a.Mode}");
            if (a.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.OnResume());
        };
        SystemEvents.DisplaySettingsChanged += (_, _) =>
            Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.EnsureOnScreen());

        ApplyTheme();
        SystemEvents.UserPreferenceChanged += (_, a) =>
        {
            if (a.Category == UserPreferenceCategory.General) Dispatcher.Invoke(() => ApplyTheme());
        };

        var win = new MainWindow { IsPrimary = true };
        MainWindow = win;
        win.Show();

        var waiter = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                // An exception on this background thread would kill the process (e.g. during shutdown).
                try { Dispatcher.Invoke(win.BringToFront); }
                catch (Exception ex) { Log.Error("show-event", ex); }
            }
        }) { IsBackground = true };
        waiter.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_primary) Log.Info($"exit code={e.ApplicationExitCode} reason={ExitReason}");
        _mutex?.Dispose();
        base.OnExit(e);
    }

    static bool LightTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    void ApplyTheme(bool? forceLight = null)
    {
        bool light = forceLight ?? LightTheme();
        void Brush(string key, uint argb) =>
            Resources[key] = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

        Brush("Bg", light ? 0xFFFAFAFBu : 0xFF1C1D21u); // opaque; the slider alone controls see-through
        Brush("Border", light ? 0x1F000000u : 0x33FFFFFFu);
        Brush("Card", light ? 0x0D000000u : 0x14FFFFFFu);
        Brush("CardHover", light ? 0x1A000000u : 0x24FFFFFFu);
        Brush("Fg", light ? 0xFF1F2328u : 0xFFF2F3F5u);
        Brush("FgSub", light ? 0xFF6A717Bu : 0xFF9AA0A8u);
        Brush("Track", light ? 0x24000000u : 0x2EFFFFFFu);
        // Notice heading text: the bar/icon keep the bright hue; text needs a darker one on light backgrounds.
        Brush("WarnText", light ? 0xFF9A6200u : 0xFFE8B931u);
        Brush("InfoText", light ? 0xFF2F64C8u : 0xFF7FA6F2u);
    }
}
