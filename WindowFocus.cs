using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeTaskBoard;

public enum FocusResult { ChildWindow, MainWindow, Terminal, NotFound }

// Brings a session's window to the front.
// Claude desktop: a session popped out into its own window has that window titled with the session
// title; everything else lives in the main window titled "Claude". Prefer the session's own window.
// CLI: the session's process owns no window, so walk up its parents to the terminal (best-effort).
public static class WindowFocus
{
    const string DesktopMainTitle = "Claude";

    public static FocusResult Focus(string title, string entrypoint, int pid)
    {
        if (entrypoint != "cli")
        {
            var windows = DesktopWindows();
            var child = windows.FirstOrDefault(w => w.Title.Length > 0 && w.Title == title && w.Title != DesktopMainTitle);
            if (child.Handle != IntPtr.Zero) { Raise(child.Handle); return FocusResult.ChildWindow; }
            var main = windows.FirstOrDefault(w => w.Title == DesktopMainTitle);
            if (main.Handle != IntPtr.Zero) { Raise(main.Handle); return FocusResult.MainWindow; }
            if (entrypoint.Length > 0) return FocusResult.NotFound;
        }
        return FocusTerminal(pid) ? FocusResult.Terminal : FocusResult.NotFound;
    }

    // The window that belongs to this session alone, to move it around: its own desktop-app window or its
    // terminal. Zero for a session living in the desktop app's main window, which is shared by all of them.
    public static IntPtr OwnWindow(string title, string entrypoint, int pid)
    {
        if (entrypoint != "cli")
        {
            var child = DesktopWindows().FirstOrDefault(w => w.Title.Length > 0 && w.Title == title && w.Title != DesktopMainTitle);
            if (child.Handle != IntPtr.Zero) return child.Handle;
            if (entrypoint.Length > 0) return IntPtr.Zero;
        }
        return TerminalWindow(pid);
    }

    // A window of the Claude desktop app (as opposed to a terminal).
    public static bool IsDesktopAppWindow(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // Titles of sessions popped out into their own desktop-app window (= their session titles).
    public static HashSet<string> ChildWindowTitles() =>
        DesktopWindows().Select(w => w.Title).Where(t => t != DesktopMainTitle).ToHashSet(StringComparer.Ordinal);

    readonly record struct Win(IntPtr Handle, string Title);

    // Visible, unowned, titled top-level windows of the Claude desktop app (Electron).
    static List<Win> DesktopWindows()
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("claude")) { pids.Add((uint)p.Id); p.Dispose(); }

        var list = new List<Win>();
        if (pids.Count == 0) return list;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid) || !IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() != "Chrome_WidgetWin_1") return true;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.Length > 0) list.Add(new Win(h, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static bool FocusTerminal(int pid)
    {
        var h = TerminalWindow(pid);
        if (h == IntPtr.Zero) return false;
        Raise(h);
        return true;
    }

    static IntPtr TerminalWindow(int pid)
    {
        try
        {
            for (int i = 0; i < 8 && pid > 0; i++)
            {
                using var p = Process.GetProcessById(pid);
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
                pid = ParentPid(p);
            }
        }
        catch { /* process gone: on Windows the recorded pid is often a transient shell */ }
        return IntPtr.Zero;
    }

    public static void Raise(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        SetForegroundWindow(h);
    }

    static int ParentPid(Process p)
    {
        var pbi = new ProcessBasicInformation();
        return NtQueryInformationProcess(p.Handle, 0, ref pbi, Marshal.SizeOf(pbi), out _) == 0
            ? pbi.InheritedFromUniqueProcessId.ToInt32() : 0;
    }

    const uint GW_OWNER = 4;
    const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr Reserved1, PebBaseAddress, Reserved2_0, Reserved2_1, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, ref ProcessBasicInformation pbi, int len, out int ret);
}
