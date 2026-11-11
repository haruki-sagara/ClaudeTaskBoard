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
        CollapseBtn.IsChecked = _vm.HeaderOnly = _cfg.HeaderOnly;
        ArrangeRightBox.IsChecked = _cfg.ArrangeRight;
        SetBoardWidth(_cfg.Width);
        SetBoardHeight(_cfg.Height);
        AutostartBox.IsChecked = AutostartEnabled();
        OpacitySlider.Value = Math.Clamp(_cfg.Opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); _cfg.Save(); };

        Loaded += (_, _) => PlaceWindow();
        LocationChanged += (_, _) => { if (IsLoaded && Left > -10000) { _cfg.Left = Left; _cfg.Top = Top; } };
        Closing += OnClosing;

        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();

        _usageTimer.Tick += async (_, _) => await PollUsageAsync();
        _usageTimer.Start();
        _ = PollUsageAsync();
    }

    // ---- size ----

    // The panel is 24 narrower than the window (shadow margin). One column of cards by default; the narrowest
    // is a third of that panel. Wider boards lay the cards out in more columns (TilePanel), up to the screen.
    public const double DefaultBoardWidth = 360;
    const double ShadowMargin = 12, MinBoardWidth = (DefaultBoardWidth - 2 * ShadowMargin) / 3 + 2 * ShadowMargin;
    const double MinBoardHeight = 160;
    const double SlimBelow = 280, TinyBelow = 190;

    public void SetBoardWidth(double width)
    {
        double max = Math.Max(DefaultBoardWidth, SystemParameters.WorkArea.Width);
        double w = double.IsFinite(width) ? Math.Clamp(width, MinBoardWidth, max) : DefaultBoardWidth;
        Width = _cfg.Width = w;
        _vm.Slim = w < SlimBelow;
        _vm.Tiny = w < TinyBelow;
        foreach (var s in _vm.Sessions) ApplyCardMode(s);
    }

    // NaN: back to fitting the content.
    public void SetBoardHeight(double height)
    {
        _cfg.Height = double.IsFinite(height)
            ? Math.Clamp(height, MinBoardHeight, Math.Max(MinBoardHeight, SystemParameters.WorkArea.Height))
            : double.NaN;
        ApplyHeight();
    }

    // A dragged height is kept while the body shows; the header-only view always shrinks to the header.
    void ApplyHeight()
    {
        if (!_vm.HeaderOnly && double.IsFinite(_cfg.Height))
        {
            SizeToContent = SizeToContent.Manual;
            Height = _cfg.Height;
            Scroller.MaxHeight = double.PositiveInfinity; // the list takes whatever the other rows leave
        }
        else
        {
            SizeToContent = SizeToContent.Height;
            Scroller.MaxHeight = SystemParameters.WorkArea.Height * 0.7;
        }
    }

    void ApplyCardMode(SessionVM s)
    {
        s.Compact = _cfg.Compact || _vm.Tiny;
        s.Tiny = _vm.Tiny;
    }

    // Resizing from any edge or corner: the grip's Tag names the sides it moves (L, R, T, B).
    // The cursor is read on screen (not relative to the window): dragging the left/top edge moves the
    // window, and a window-relative position read mid-move drifts by however far it has moved.
    Border? _grip;
    string _gripSides = "";
    Point _gripFrom;
    Rect _gripStart;

    void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border grip) return;
        e.Handled = true;
        string sides = grip.Tag as string ?? "";
        if (e.ClickCount == 2 && sides is "T" or "B")
        {
            SetBoardHeight(double.NaN);
            _cfg.Save();
            Toast("高さを内容に合わせる表示に戻しました");
            return;
        }
        _grip = grip;
        _gripSides = sides;
        _gripFrom = ScreenPoint(e);
        _gripStart = new Rect(Left, Top, ActualWidth, ActualHeight);
        grip.CaptureMouse();
    }

    void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_grip is null || !_grip.IsMouseCaptured) return;
        var p = ScreenPoint(e);
        double dx = p.X - _gripFrom.X, dy = p.Y - _gripFrom.Y;

        if (_gripSides.Contains('R') || _gripSides.Contains('L'))
        {
            SetBoardWidth(_gripStart.Width + (_gripSides.Contains('L') ? -dx : dx));
            if (_gripSides.Contains('L')) Left = _gripStart.Right - Width; // the right edge stays put
        }
        if (_gripSides.Contains('T') || _gripSides.Contains('B'))
        {
            // Dragging the height while header-only would be undone at once: open the body first.
            if (_vm.HeaderOnly) { CollapseBtn.IsChecked = _cfg.HeaderOnly = _vm.HeaderOnly = false; }
            SetBoardHeight(_gripStart.Height + (_gripSides.Contains('T') ? -dy : dy));
            if (_gripSides.Contains('T')) Top = _gripStart.Bottom - Height; // the bottom edge stays put
        }
    }

    void Grip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_grip is null || !_grip.IsMouseCaptured) return;
        _grip.ReleaseMouseCapture();
        _grip = null;
        _cfg.Save();
    }

    Point ScreenPoint(MouseEventArgs e)
    {
        var device = GetCursorPos(out var pt) ? new Point(pt.X, pt.Y) : PointToScreen(e.GetPosition(this));
        return PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.Transform(device) ?? device;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct CursorPoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out CursorPoint p);

    // ---- header-only view ----

    void Collapse_Click(object sender, RoutedEventArgs e)
    {
        _cfg.HeaderOnly = _vm.HeaderOnly = CollapseBtn.IsChecked == true;
        ApplyHeight();
        _cfg.Save();
    }

    public void SetHeaderOnly() { _vm.HeaderOnly = true; ApplyHeight(); } // --snapshot only: not saved

    // ---- arranging session windows ----

    // What the last arrange button did: pressing the same one again sends the first window to the end.
    (int priority, ArrangeLayout layout)? _lastArrange;
    List<IntPtr> _lastOrder = new();

    // Priority is what the list is sorted by: 2 = waiting on the user, 1 = working.
    void ArrangeWorkingColumns_Click(object sender, RoutedEventArgs e) => ArrangeSessions(1, ArrangeLayout.Columns, "進行中");
    void ArrangeWorkingTile_Click(object sender, RoutedEventArgs e) => ArrangeSessions(1, ArrangeLayout.Tile, "進行中");
    void ArrangeWaitingColumns_Click(object sender, RoutedEventArgs e) => ArrangeSessions(2, ArrangeLayout.Columns, "判断が必要");
    void ArrangeWaitingTile_Click(object sender, RoutedEventArgs e) => ArrangeSessions(2, ArrangeLayout.Tile, "判断が必要");

    void ArrangeSessions(int priority, ArrangeLayout layout, string what)
    {
        var targets = _vm.Sessions.Where(s => s.IsPresent && s.Priority == priority).ToList();
        if (targets.Count == 0) { Toast($"{what}のセッションはありません"); return; }

        var found = targets.Select(s => WindowFocus.OwnWindow(s.Title, s.Entrypoint, s.Pid)).ToList();
        var windows = found.Where(h => h != IntPtr.Zero).Distinct().ToList();
        if (windows.Count == 0) { Toast($"{what}のセッションに別ウィンドウがありません"); return; }

        bool again = _lastArrange == (priority, layout);
        var order = again ? ArrangeCalc.NextOrder(_lastOrder, windows) : windows;
        bool exact = WindowArranger.Arrange(order, layout, new System.Windows.Interop.WindowInteropHelper(this).Handle, _cfg.ArrangeRight);
        _lastArrange = (priority, layout);
        _lastOrder = order;
        int noWindow = found.Count(h => h == IntPtr.Zero);
        Toast((again && order.Count > 1 ? $"{order.Count} 個のウィンドウを順送りしました" : $"{order.Count} 個のウィンドウを整列しました")
            + (noWindow > 0 ? $"（{noWindow} 件は別ウィンドウなし）" : "")
            + (exact ? "" : "（最小サイズのため一部が重なっています）"));
    }

    void ArrangeRight_Click(object sender, RoutedEventArgs e)
    {
        _cfg.ArrangeRight = ArrangeRightBox.IsChecked == true;
        _lastArrange = null; // the next press lays out afresh on the new side instead of rotating
        _cfg.Save();
        Toast(_cfg.ArrangeRight ? "整列時は右寄せで並べます" : "整列時は左寄せで並べます");
    }

    // ---- weekly usage ----

    readonly DispatcherTimer _usageTimer = new() { Interval = TimeSpan.FromSeconds(5) }; // each tick only checks _usageNextAt
    DateTime _usageNextAt = DateTime.MinValue;
    bool _usagePolling, _usageFixed;

    async Task PollUsageAsync()
    {
        if (_usagePolling || _usageFixed || DateTime.UtcNow < _usageNextAt) return;
        _usagePolling = true;
        try
        {
            var r = await UsageClient.FetchAsync();
            if (_usageFixed) return;
            _vm.ApplyUsage(r);
            _usageNextAt = DateTime.UtcNow + r.RetryIn;
        }
        catch (Exception ex) { Log.Error("usage", ex); _usageNextAt = DateTime.UtcNow.AddMinutes(5); }
        finally { _usagePolling = false; }
    }

    DateTime _loginStartedAt = DateTime.MinValue;

    void CliLogin_Click(object sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow - _loginStartedAt < TimeSpan.FromSeconds(5)) return; // a double click opens one terminal
        _loginStartedAt = DateTime.UtcNow;
        Toast(CliLogin.Start() ? "ターミナルとブラウザでログインを完了してください" : "ターミナルを起動できませんでした");
    }

    // A fixed value for --snapshot renders.
    public void ShowUsage(UsageResult r)
    {
        _usageFixed = true;
        _vm.ApplyUsage(r);
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
        _usageNextAt = DateTime.MinValue;
        _ = PollUsageAsync();
    }

    public void EnsureOnScreen()
    {
        if (_snapshot || Left < -10000) return;
        SetBoardWidth(_cfg.Width);   // the screen may have shrunk
        SetBoardHeight(_cfg.Height);
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
            if (id.Length == 0 || _dismissed.Hides(id, snap.State.Ts)) continue;
            bool waiting = snap.Notice is { NeedsAction: true };
            // Sessions open in their own window are always shown, even with "hide finished" on.
            if (_cfg.HideResting && snap.State.State is "done" or "idle" && !waiting && !snap.InWindow) continue;
            if (!byId.TryGetValue(id, out var vm))
            {
                vm = new SessionVM(id, now);
                ApplyCardMode(vm);
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

        foreach (var s in _vm.Sessions.Where(s => !keep.Contains(s.Id) && s.IsPresent).ToList()) Retire(s);

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

    // Gone sessions play their exit animation first, then leave the list.
    void Retire(SessionVM s)
    {
        s.IsPresent = false;
        var t = new DispatcherTimer { Interval = Anim.OutDuration + TimeSpan.FromMilliseconds(50) };
        t.Tick += (_, _) => { t.Stop(); if (!s.IsPresent) _vm.Sessions.Remove(s); };
        t.Start();
    }

    readonly DismissList _dismissed = new();

    void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionVM vm) return;
        _dismissed.Add(vm.Id, vm.Ts);
        Retire(vm);
        Toast("非表示にしました（新しい動きがあれば再表示されます）");
        _ = PollAsync(); // the header's counts
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
        foreach (var s in _vm.Sessions) ApplyCardMode(s);
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
