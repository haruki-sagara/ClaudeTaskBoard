using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClaudeTaskBoard;

public partial class MainWindow : Window
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "ClaudeTaskBoard";

    readonly BoardVM _vm = new();
    readonly SessionStore _store = new();
    readonly Settings _cfg = Settings.Load();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    bool _polling;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        Opacity = _cfg.Opacity;
        Topmost = _cfg.Topmost;
        PinBtn.IsChecked = _cfg.Topmost;
        CompactBtn.IsChecked = _cfg.Compact;
        HideRestingBox.IsChecked = _cfg.HideResting;
        AutostartBox.IsChecked = AutostartEnabled();
        OpacitySlider.Value = Math.Clamp(_cfg.Opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); _cfg.Save(); };

        Scroller.MaxHeight = SystemParameters.WorkArea.Height * 0.7;
        Loaded += (_, _) => PlaceWindow();
        LocationChanged += (_, _) => { if (IsLoaded && Left > -10000) { _cfg.Left = Left; _cfg.Top = Top; } };
        Closing += OnClosing;

        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
    }

    // ---- staying alive ----

    // Set by App for the real board (not for --snapshot renders): only it reports a heartbeat.
    public bool IsPrimary { get; init; }
    readonly DispatcherTimer _beat = new() { Interval = TimeSpan.FromSeconds(5) };
    bool _allowClose;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!IsPrimary || _beat.IsEnabled) return;
        _beat.Tick += (_, _) => Task.Run(Heartbeat.Beat);
        _beat.Start();
        Task.Run(Heartbeat.Beat);
    }

    // Windows is logging off / shutting down: close without asking.
    public void AllowClose() => _allowClose = true;

    // ✕ and Alt+F4 both ask first (in the board, no system dialog); an accidental close is the
    // most likely way to "lose" the board, and it does not come back until relaunched.
    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_snapshot || _allowClose || !IsPrimary)
        {
            if (!_snapshot) _cfg.Save();
            return;
        }
        e.Cancel = true;
        _vm.ShowSettings = false;
        _vm.ConfirmExit = true;
        BringToFront();
    }

    void Close_Click(object sender, RoutedEventArgs e) => _vm.ConfirmExit = true;

    void ConfirmExit_Click(object sender, RoutedEventArgs e)
    {
        App.ExitReason = "closed by user";
        _beat.Stop();
        Heartbeat.ClosedByUser(); // tells the hook not to relaunch it
        _allowClose = true;
        Close();
    }

    void CancelExit_Click(object sender, RoutedEventArgs e) => _vm.ConfirmExit = false;

    // After sleep or a monitor/DPI change the saved spot may be off screen; bring it back and refresh.
    public void OnResume()
    {
        EnsureOnScreen();
        _ = PollAsync();
    }

    public void EnsureOnScreen()
    {
        if (_snapshot || Left < -10000) return;
        Scroller.MaxHeight = SystemParameters.WorkArea.Height * 0.7;
        if (OnScreen(Left, Top)) return;
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 8;
        Top = wa.Top + 60;
        Log.Info("window was off screen; moved back");
    }

    static bool OnScreen(double left, double top)
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
        return left >= vl - 50 && left + 100 <= vr && top >= vt - 20 && top + 60 <= vb;
    }

    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        var top = Topmost;
        Topmost = true; // flash to the top even when not pinned
        Topmost = top;
        Activate();
    }

    public void SaveSnapshot(string path)
    {
        _snapshot = true;
        // Render the whole window (not its content) so the shadow margin doesn't shift and crop the image.
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(this);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }

    bool _snapshot; // snapshot runs must not overwrite the saved window position
    bool _applied;  // first poll done (notices seen at startup are not toasted)

    public void ExpandAll()
    {
        foreach (var s in _vm.Sessions) s.IsExpanded = true;
    }

    public void OpenSettings() => _vm.ShowSettings = true;

    void PlaceWindow()
    {
        if (Left < -10000) return; // off-screen snapshot render
        var wa = SystemParameters.WorkArea;

        // Restore the saved spot only if it is still on screen (monitors change).
        if (!double.IsNaN(_cfg.Left) && !double.IsNaN(_cfg.Top) && OnScreen(_cfg.Left, _cfg.Top))
        {
            Left = _cfg.Left;
            Top = _cfg.Top;
        }
        else
        {
            Left = wa.Right - ActualWidth - 8;
            Top = wa.Top + 60;
        }
    }

    async Task PollAsync()
    {
        if (_polling) return; // a large first transcript read can outlast one tick
        _polling = true;
        try
        {
            var snaps = await Task.Run(_store.Poll);
            Apply(snaps);
        }
        catch (Exception ex) { Log.Error("poll", ex); /* keep the board alive; the next tick retries */ }
        finally { _polling = false; }
    }

    void Apply(List<SessionSnapshot> snaps)
    {
        var byId = _vm.Sessions.ToDictionary(s => s.Id);
        var keep = new HashSet<string>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var snap in snaps)
        {
            string id = snap.State.SessionId;
            if (id.Length == 0) continue;
            bool waiting = snap.Notice is { NeedsAction: true };
            // Sessions open in their own window are always shown, even with "hide finished" on.
            if (_cfg.HideResting && snap.State.State is "done" or "idle" && !waiting && !snap.InWindow) continue;
            if (!byId.TryGetValue(id, out var vm))
            {
                vm = new SessionVM(id, now) { Compact = _cfg.Compact };
                byId[id] = vm;
                _vm.Sessions.Add(vm);
            }
            string before = vm.NoticeKey;
            vm.Update(snap);
            vm.IsPresent = true; // also cancels an exit that was still animating
            keep.Add(id);

            // A new request from Claude Code: say so in the board (not on the very first poll, which
            // would replay everything that was already pending when the board started).
            if (_applied && vm.NoticeNeedsAction && vm.NoticeKey != before)
                Toast($"{vm.NoticeTitle}: {vm.Title}");
        }
        _applied = true;

        // Gone sessions play their exit animation first, then leave the list.
        foreach (var s in _vm.Sessions.Where(s => !keep.Contains(s.Id) && s.IsPresent).ToList())
        {
            s.IsPresent = false;
            var t = new DispatcherTimer { Interval = Anim.OutDuration + TimeSpan.FromMilliseconds(50) };
            t.Tick += (_, _) => { t.Stop(); if (!s.IsPresent) _vm.Sessions.Remove(s); };
            t.Start();
        }

        // Most urgent first; otherwise keep arrival order so cards don't jump around.
        var all = _vm.Sessions.OrderByDescending(s => s.Priority).ThenBy(s => s.FirstSeen).ToList();
        for (int i = 0; i < all.Count; i++)
        {
            int cur = _vm.Sessions.IndexOf(all[i]);
            if (cur != i) _vm.Sessions.Move(cur, i);
        }
        var order = all.Where(s => s.IsPresent).ToList();

        int waitingCount = order.Count(s => s.NoticeNeedsAction || s.State is "permission" or "question");
        int working = order.Count(s => s.IsWorking);
        var parts = new List<string>();
        if (waitingCount > 0) parts.Add($"{waitingCount} 件確認待ち");
        if (working > 0) parts.Add($"{working} 件実行中");
        if (parts.Count == 0) parts.Add(order.Count > 0 ? $"{order.Count} 件のセッション" : "待機中");
        _vm.Summary = string.Join(" · ", parts);
        _vm.IsEmpty = order.Count == 0;
    }

    void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) is not null) return;
        DragMove();
        _cfg.Save();
    }

    void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) is not null) return; // chevron
        if ((sender as FrameworkElement)?.DataContext is not SessionVM vm) return;

        switch (WindowFocus.Focus(vm.Title, vm.Entrypoint, vm.Pid))
        {
            case FocusResult.MainWindow:
                Toast("別ウィンドウが見つからないため、Claude のメインウィンドウを表示しました");
                break;
            case FocusResult.NotFound:
                Toast("このセッションのウィンドウが見つかりませんでした");
                break;
        }
    }

    void Chevron_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionVM vm) vm.IsExpanded = !vm.IsExpanded;
    }

    DispatcherTimer? _toastTimer;

    void Toast(string text)
    {
        _vm.ToastText = text;
        _vm.ShowToast = true;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) => { _toastTimer!.Stop(); _vm.ShowToast = false; };
        _toastTimer.Start();
    }

    void Compact_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Compact = CompactBtn.IsChecked == true;
        foreach (var s in _vm.Sessions) s.Compact = _cfg.Compact;
        _cfg.Save();
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Topmost = PinBtn.IsChecked == true;
        Topmost = _cfg.Topmost;
        _cfg.Save();
    }

    void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return; // XAML coercion while loading must not overwrite the saved value
        _cfg.Opacity = Math.Round(e.NewValue, 2);
        Opacity = _cfg.Opacity; // live while dragging
        _saveDebounce.Stop();   // write once the drag settles, not on every step
        _saveDebounce.Start();
    }

    void HideResting_Click(object sender, RoutedEventArgs e)
    {
        _cfg.HideResting = HideRestingBox.IsChecked == true;
        _cfg.Save();
        _ = PollAsync();
    }

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (AutostartBox.IsChecked == true && Environment.ProcessPath is string exe)
                k?.SetValue(RunName, $"\"{exe}\"");
            else
                k?.DeleteValue(RunName, throwOnMissingValue: false);
        }
        catch { }
        AutostartBox.IsChecked = AutostartEnabled(); // reflect what actually happened
    }

    static bool AutostartEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string;
        }
        catch { return false; }
    }


    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }
}
