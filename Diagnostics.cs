using System.IO;
using System.Text.Json;

namespace ClaudeTaskBoard;

// %USERPROFILE%\.claude\taskboard (or %TASKBOARD_HOME%) — shared with hooks/hook.js.
public static class AppHome
{
    public static readonly string Dir = Environment.GetEnvironmentVariable("TASKBOARD_HOME") is { Length: > 0 } h ? h
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "taskboard");
}

// Append-only diagnostics: every start, every exit with its reason, every unhandled exception.
// An exit with no "exit" line after its "start" means the process was killed or died natively.
public static class Log
{
    static readonly string FilePath = Path.Combine(AppHome.Dir, "logs", "board.log");
    const long MaxBytes = 1024 * 1024;
    static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string where, Exception? ex) =>
        Write("ERROR", $"{where}: {ex?.GetType().FullName}: {ex?.Message}\n{ex}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] pid={Environment.ProcessId} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never take the board down */ }
    }
}

// "I am alive" file read by hooks/hook.js: when it goes stale (crash, kill) the next Claude Code
// prompt relaunches the board — unless the user closed it on purpose (closedByUser).
public static class Heartbeat
{
    static readonly string FilePath = Path.Combine(AppHome.Dir, "board.json");

    public static void Beat() => Write(false);
    public static void ClosedByUser() => Write(true);

    static void Write(bool closedByUser)
    {
        try
        {
            Directory.CreateDirectory(AppHome.Dir);
            var json = JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                exe = Environment.ProcessPath ?? "",
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                closedByUser,
            });
            string tmp = FilePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* best-effort */ }
    }
}
