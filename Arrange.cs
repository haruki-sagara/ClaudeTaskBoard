using System.Runtime.InteropServices;

namespace ClaudeTaskBoard;

public enum ArrangeLayout { Columns, Tile }

public readonly record struct Box(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
}

// Where each window goes. Pure, so it can be tested without moving anything.
public static class ArrangeCalc
{
    // maxWidths[i]: the widest window i is worth (past it, its text no longer gets wider); null = no limit.
    // Capped windows keep to that width and pack from the left (or the right: alignRight); what they leave
    // goes to the others. The order stays left to right either way.
    public static List<Box> Layout(ArrangeLayout layout, int count, Box area, IReadOnlyList<int>? maxWidths = null, bool alignRight = false)
    {
        var boxes = new List<Box>();
        if (count <= 0) return boxes;

        // Full-height columns side by side = one row; a tile grid = about as many rows as columns.
        int cols = layout == ArrangeLayout.Columns ? count : (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (int)Math.Ceiling(count / (double)cols);
        for (int r = 0; r < rows; r++)
        {
            int inRow = r == rows - 1 ? count - cols * (rows - 1) : cols; // the last row stretches to fill
            int top = area.Y + area.H * r / rows, bottom = area.Y + area.H * (r + 1) / rows;
            var caps = Enumerable.Range(r * cols, inRow).Select(i => maxWidths is not null && i < maxWidths.Count ? maxWidths[i] : int.MaxValue).ToList();
            var widths = Widths(area.W, caps);
            int left = alignRight ? area.Right - widths.Sum() : area.X;
            foreach (int w in widths)
            {
                boxes.Add(new Box(left, top, w, bottom - top));
                left += w;
            }
        }
        return boxes;
    }

    // Shares a row's width out evenly, except that no window gets more than its cap: a capped window's
    // unused share is split among the rest, again and again until every share fits.
    static int[] Widths(int total, IReadOnlyList<int> caps)
    {
        var widths = new int[caps.Count];
        var open = Enumerable.Range(0, caps.Count).ToList();
        int left = total;
        while (open.Count > 0)
        {
            int share = left / open.Count;
            var capped = open.Where(i => caps[i] <= share).ToList();
            if (capped.Count == 0) break;
            foreach (int i in capped) { widths[i] = caps[i]; left -= caps[i]; open.Remove(i); }
        }
        for (int k = 0; k < open.Count; k++)
            widths[open[k]] = left * (k + 1) / open.Count - left * k / open.Count; // exact, no pixel lost
        return widths;
    }

    // The order for pressing the same arrange button again: the first (left, or top-left in a tile) goes
    // last (right, or bottom-right) and the rest move up. Layout fills Z-wise, so this holds for both.
    // Windows closed since are dropped; ones that have joined line up at the end before the move.
    public static List<T> NextOrder<T>(IReadOnlyList<T> previous, IReadOnlyList<T> current)
    {
        var order = previous.Where(current.Contains).Concat(current.Where(x => !previous.Contains(x))).ToList();
        if (order.Count > 1)
        {
            order.Add(order[0]);
            order.RemoveAt(0);
        }
        return order;
    }

    // The work area without the board: whichever side of it is wider.
    public static Box Exclude(Box work, Box board)
    {
        if (board.Right <= work.X || board.X >= work.Right || board.Bottom <= work.Y || board.Y >= work.Bottom) return work;
        int leftW = Math.Max(0, board.X - work.X), rightW = Math.Max(0, work.Right - board.Right);
        return leftW >= rightW ? work with { W = leftW } : work with { X = work.Right - rightW, W = rightW };
    }
}

// Moves real windows. Coordinates are this process's Win32 ones (not WPF device-independent units).
public static class WindowArranger
{
    // The Claude desktop app centers its conversation at a fixed maximum width; measured (2026-11, at 125%
    // scaling) the text stops widening once the window is about 1021 px = 817 DIP wide. A little over that,
    // so the conversation is never squeezed. Terminals reflow to any width and get no limit.
    public const int DesktopAppUsefulWidthDip = 820;

    // Returns false when some window would not take its size (e.g. it has a minimum width), so they may overlap.
    public static bool Arrange(IReadOnlyList<IntPtr> windows, ArrangeLayout layout, IntPtr board, bool alignRight)
    {
        var caps = windows.Select(UsefulWidth).ToList();
        var boxes = ArrangeCalc.Layout(layout, windows.Count, ArrangeCalc.Exclude(WorkAreaOf(board), BoundsOf(board)), caps, alignRight);
        bool exact = true;
        for (int i = 0; i < windows.Count; i++) exact &= Place(windows[i], boxes[i]);
        return exact;
    }

    public static bool IsUsable(IntPtr h) => IsWindow(h) && IsWindowVisible(h);

    // In this process's pixels at the window's own scaling.
    static int UsefulWidth(IntPtr h)
    {
        if (!WindowFocus.IsDesktopAppWindow(h)) return int.MaxValue;
        uint dpi = GetDpiForWindow(h);
        return (int)Math.Round(DesktopAppUsefulWidthDip * (dpi > 0 ? dpi : 96) / 96.0);
    }

    public static bool Place(IntPtr h, Box box)
    {
        if (IsIconic(h) || IsZoomed(h)) ShowWindow(h, SW_RESTORE);

        // Windows have invisible resize borders: place what is visible, or gaps appear between neighbors.
        GetWindowRect(h, out var outer);
        var visible = VisibleFrame(h, outer);
        int l = visible.Left - outer.Left, t = visible.Top - outer.Top;
        int r = outer.Right - visible.Right, b = outer.Bottom - visible.Bottom;
        SetWindowPos(h, IntPtr.Zero, box.X - l, box.Y - t, box.W + l + r, box.H + t + b, SWP_NOACTIVATE | SWP_NOOWNERZORDER);

        var placed = BoundsOf(h, visibleOnly: true);
        return Math.Abs(placed.W - box.W) <= 2 && Math.Abs(placed.H - box.H) <= 2;
    }

    public static Box BoundsOf(IntPtr h, bool visibleOnly = false)
    {
        GetWindowRect(h, out var rc);
        if (visibleOnly) rc = VisibleFrame(h, rc);
        return new Box(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
    }

    static Rc VisibleFrame(IntPtr h, Rc fallback) =>
        DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out Rc rc, Marshal.SizeOf<Rc>()) == 0 ? rc : fallback;

    static Box WorkAreaOf(IntPtr h)
    {
        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), ref mi);
        return new Box(mi.Work.Left, mi.Work.Top, mi.Work.Right - mi.Work.Left, mi.Work.Bottom - mi.Work.Top);
    }

    const int SW_RESTORE = 9;
    const uint SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200, MONITOR_DEFAULTTONEAREST = 2;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)] struct Rc { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rc Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rc rc);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr m, ref MonitorInfo mi);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Rc value, int size);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
}
