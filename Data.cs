using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeTaskBoard;

// Something Claude Code is asking the user, or telling them (written by hooks/hook.js).
public sealed class Notice
{
    // permission | question | plan | ask | idle | info
    [JsonPropertyName("kind")] public string Kind { get; set; } = "info";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("ts")] public long Ts { get; set; }

    // Waiting on the user, as opposed to a plain heads-up.
    public bool NeedsAction => Kind is "permission" or "question" or "plan" or "ask";
}

// One session's state file written by this app's own hook (hooks/hook.js):
// %USERPROFILE%\.claude\taskboard\state.d\<session_id>.json
public sealed class StateFile
{
    [JsonPropertyName("notice")] public Notice? Notice { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "idle";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    [JsonPropertyName("project")] public string Project { get; set; } = "";
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("transcript")] public string Transcript { get; set; } = "";
    [JsonPropertyName("entrypoint")] public string Entrypoint { get; set; } = ""; // "cli", "claude-desktop", …
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("started")] public bool Started { get; set; }
    [JsonPropertyName("startedAt")] public long StartedAt { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
}

public enum StepStatus { Pending, InProgress, Completed }

public sealed record PlanStep(string Content, StepStatus Status);

// Rebuilds a session's task list from the task-list tool calls recorded in its transcript.
// Desktop app sessions use mcp__ccd_turn__update_plan (full list or deltas) and wrap_up (checks
// everything off); CLI sessions use TodoWrite (always the full list).
public static class PlanReducer
{
    public static bool IsPlanTool(string name) =>
        name.EndsWith("ccd_turn__update_plan", StringComparison.Ordinal) ||
        name.EndsWith("ccd_turn__wrap_up", StringComparison.Ordinal) ||
        name == "TodoWrite";

    public static List<PlanStep> Apply(List<PlanStep> plan, string tool, JsonElement input)
    {
        if (tool.EndsWith("ccd_turn__wrap_up", StringComparison.Ordinal))
            return plan.Select(s => s with { Status = StepStatus.Completed }).ToList();

        if (tool == "TodoWrite")
            return ParseList(input, "todos") ?? plan;

        if (!tool.EndsWith("ccd_turn__update_plan", StringComparison.Ordinal)) return plan;

        var next = ParseList(input, "items") ?? plan.ToList();

        if (input.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.Array)
            foreach (var d in done.EnumerateArray())
                if (d.TryGetInt32(out int i) && i >= 1 && i <= next.Count)
                    next[i - 1] = next[i - 1] with { Status = StepStatus.Completed };

        if (input.TryGetProperty("start", out var start) && start.TryGetInt32(out int st) && st >= 1 && st <= next.Count)
        {
            // Exactly one step runs at a time; one left running without a "done" was set aside, not finished.
            for (int i = 0; i < next.Count; i++)
                if (next[i].Status == StepStatus.InProgress) next[i] = next[i] with { Status = StepStatus.Pending };
            next[st - 1] = next[st - 1] with { Status = StepStatus.InProgress };
        }

        if (input.TryGetProperty("advance", out var adv) && adv.ValueKind == JsonValueKind.True)
        {
            int cur = next.FindIndex(s => s.Status == StepStatus.InProgress);
            if (cur >= 0) next[cur] = next[cur] with { Status = StepStatus.Completed };
            int nxt = next.FindIndex(Math.Max(cur, 0), s => s.Status == StepStatus.Pending);
            if (nxt < 0) nxt = next.FindIndex(s => s.Status == StepStatus.Pending);
            if (nxt >= 0) next[nxt] = next[nxt] with { Status = StepStatus.InProgress };
        }
        return next;
    }

    static List<PlanStep>? ParseList(JsonElement input, string prop)
    {
        if (!input.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<PlanStep>();
        foreach (var it in arr.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue;
            string content = it.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
            string status = it.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            list.Add(new PlanStep(content, status switch
            {
                "completed" => StepStatus.Completed,
                "in_progress" => StepStatus.InProgress,
                _ => StepStatus.Pending,
            }));
        }
        return list;
    }
}

// Incrementally tails one transcript (.jsonl). Only bytes appended since the last read are parsed,
// and only lines that can matter (task-list tool calls, session title) are JSON-decoded.
public sealed class TranscriptTracker
{
    readonly string _path;
    long _offset;
    public List<PlanStep> Plan { get; private set; } = new();
    public string Title { get; private set; } = "";

    // The question a desktop-app turn ended on (wrap_up "ask"), until the user's next prompt answers it.
    public string PendingAsk { get; private set; } = "";
    public long PendingAskTs { get; private set; }

    public TranscriptTracker(string path) => _path = path;

    public bool Refresh()
    {
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _offset) { _offset = 0; Plan = new(); Title = ""; PendingAsk = ""; } // rewritten — start over
            if (fs.Length == _offset) return false;

            fs.Seek(_offset, SeekOrigin.Begin);
            var buf = new byte[fs.Length - _offset];
            int read = 0;
            while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }

            // Consume whole lines only; a half-written last line is picked up next time.
            int end = Array.LastIndexOf(buf, (byte)'\n', read - 1);
            if (end < 0) return false;
            _offset += end + 1;

            bool changed = false;
            int lineStart = 0;
            for (int i = 0; i <= end; i++)
            {
                if (buf[i] != (byte)'\n') continue;
                if (i > lineStart) changed |= ProcessLine(Encoding.UTF8.GetString(buf, lineStart, i - lineStart));
                lineStart = i + 1;
            }
            return changed;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    bool ProcessLine(string line)
    {
        bool title = line.Contains("\"custom-title\"", StringComparison.Ordinal);
        bool plan = line.Contains("update_plan", StringComparison.Ordinal) ||
                    line.Contains("ccd_turn__wrap_up", StringComparison.Ordinal) ||
                    line.Contains("\"TodoWrite\"", StringComparison.Ordinal);
        bool reply = PendingAsk.Length > 0 && line.Contains("\"type\":\"user\"", StringComparison.Ordinal) &&
                     !line.Contains("tool_result", StringComparison.Ordinal);
        if (!title && !plan && !reply) return false;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

            if (type == "user")
            {
                if (!reply || (root.TryGetProperty("isSidechain", out var us) && us.ValueKind == JsonValueKind.True)) return false;
                PendingAsk = ""; // the user wrote back: the question is answered
                return true;
            }

            if (type == "custom-title")
            {
                string v = root.TryGetProperty("customTitle", out var ct) ? ct.GetString() ?? "" : "";
                if (v.Length == 0 || v == Title) return false;
                Title = v;
                return true;
            }

            if (type != "assistant") return false;
            if (root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) return false;
            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array) return false;

            bool changed = false;
            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var bt) || bt.GetString() != "tool_use") continue;
                string name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!PlanReducer.IsPlanTool(name) || !block.TryGetProperty("input", out var input)) continue;
                Plan = PlanReducer.Apply(Plan, name, input);
                if (name.EndsWith("ccd_turn__wrap_up", StringComparison.Ordinal))
                {
                    PendingAsk = input.TryGetProperty("ask", out var ask) && ask.ValueKind == JsonValueKind.String
                        ? ask.GetString() ?? "" : "";
                    PendingAskTs = root.TryGetProperty("timestamp", out var tsv) && tsv.TryGetDateTimeOffset(out var at)
                        ? at.ToUnixTimeSeconds() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
                changed = true;
            }
            return changed;
        }
        catch (JsonException) { return false; }
    }
}

public sealed class SessionSnapshot
{
    public required StateFile State { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<PlanStep> Plan { get; init; }

    // What to surface for this session: the hook's notice wins; otherwise a turn that ended on a question.
    public Notice? Notice { get; init; }

    // Open in its own desktop-app window: always listed, whatever its state or age.
    public bool InWindow { get; init; }
}

// Session title per transcript, read from the file's tail: Claude Code re-writes the "custom-title"
// line throughout a session, so the latest one is near the end. Falls back to one full scan.
public sealed class TitleIndex
{
    const int TailBytes = 256 * 1024;
    readonly Dictionary<string, (DateTime mtime, string title)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string TitleOf(string path)
    {
        try
        {
            var mtime = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(path, out var hit) && hit.mtime == mtime) return hit.title;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string title = LastTitle(fs, Math.Max(0, fs.Length - TailBytes));
            if (title.Length == 0) title = hit.title ?? "";                      // renamed long ago: keep what we knew
            if (title.Length == 0 && fs.Length > TailBytes) title = LastTitle(fs, 0); // first sight: scan it all once
            _cache[path] = (mtime, title);
            return title;
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    static string LastTitle(FileStream fs, long from)
    {
        fs.Seek(from, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
        string title = "";
        for (string? line; (line = sr.ReadLine()) is not null;)
        {
            if (!line.Contains("\"custom-title\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("customTitle", out var ct) && ct.GetString() is { Length: > 0 } t) title = t;
            }
            catch (JsonException) { /* a cut-off first line of the tail */ }
        }
        return title;
    }
}

// Polls state.d and keeps one transcript tracker per live session. Sessions open in their own
// desktop-app window are always included — even stale, finished, or never seen by the hook.
public sealed class SessionStore
{
    // TASKBOARD_HOME overrides the location (the hook honors it too) — used for isolated testing and demos.
    // It isolates transcript lookup as well (<home>\projects), so no real session leaks into a test run.
    static readonly string? HomeOverride = Environment.GetEnvironmentVariable("TASKBOARD_HOME") is { Length: > 0 } h ? h : null;
    public static readonly string DefaultStateDir = Path.Combine(
        HomeOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "taskboard"),
        "state.d");
    public static readonly string DefaultProjectsDir = HomeOverride is not null
        ? Path.Combine(HomeOverride, "projects")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    readonly string _stateDir, _projectsDir;
    readonly Func<IReadOnlySet<string>> _windowTitles;

    public SessionStore() : this(DefaultStateDir, DefaultProjectsDir, WindowFocus.ChildWindowTitles) { }

    public SessionStore(string stateDir, string projectsDir, Func<IReadOnlySet<string>> windowTitles)
    {
        _stateDir = stateDir;
        _projectsDir = projectsDir;
        _windowTitles = windowTitles;
    }

    readonly TitleIndex _titles = new();
    // Transcripts scanned to find windows the hook never reported (refreshed at most every few seconds).
    const int RecentDays = 14;
    static readonly TimeSpan RecentRefresh = TimeSpan.FromSeconds(15);
    DateTime _recentAt = DateTime.MinValue;
    List<(string path, DateTime mtime)> _recent = new();

    // Hooks run through a transient shell on Windows, so the recorded pid is usually already dead;
    // a fresh ts is the real liveness signal.
    const long StaleSeconds = 4 * 3600;
    // Files of sessions that ended without SessionEnd (crash, killed app) are deleted after this long.
    const long DeleteSeconds = 24 * 3600;

    readonly Dictionary<string, (DateTime mtime, StateFile data)> _files = new();
    readonly Dictionary<string, TranscriptTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);

    public List<SessionSnapshot> Poll()
    {
        var result = new List<SessionSnapshot>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var seen = new HashSet<string>();
        var liveTranscripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> windows;
        try { windows = _windowTitles(); } catch { windows = new HashSet<string>(); }
        var shown = new HashSet<string>(StringComparer.Ordinal);       // window titles already covered
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var files = Directory.Exists(_stateDir) ? Directory.EnumerateFiles(_stateDir, "*.json") : Enumerable.Empty<string>();
        foreach (var f in files)
        {
            seen.Add(f);
            StateFile? s;
            try
            {
                var mtime = File.GetLastWriteTimeUtc(f);
                if (_files.TryGetValue(f, out var hit) && hit.mtime == mtime) s = hit.data;
                else
                {
                    s = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(f));
                    if (s is not null) _files[f] = (mtime, s);
                }
            }
            catch { continue; } // locked or mid-rename — next tick
            if (s is null) continue;

            // The title is cheap to get (file tail) and decides whether this session has its own window.
            string title = string.IsNullOrEmpty(s.Transcript) ? "" : _titles.TitleOf(s.Transcript);
            bool inWindow = title.Length > 0 && windows.Contains(title);

            if (!inWindow && now - s.Ts > DeleteSeconds && !Alive(s.Pid)) { TryDelete(f); continue; }
            if (!inWindow && (!s.Started || !IsLive(Alive(s.Pid), s.Ts, now))) continue;

            if (inWindow) shown.Add(title);
            ids.Add(s.SessionId);
            result.Add(Snapshot(s, inWindow, liveTranscripts));
        }

        // Windows the hook knows nothing about (opened before the hook was installed, or long idle):
        // find their transcript by title and list them from that alone.
        var missing = windows.Where(t => !shown.Contains(t)).ToList();
        if (missing.Count > 0)
        {
            foreach (var (path, mtime) in RecentTranscripts())
            {
                if (missing.Count == 0) break;
                string title = _titles.TitleOf(path);
                if (title.Length == 0 || !missing.Remove(title)) continue; // newest first: first match wins
                string id = Path.GetFileNameWithoutExtension(path);
                if (!ids.Add(id)) continue;
                var s = new StateFile
                {
                    SessionId = id, Transcript = path, Entrypoint = "claude-desktop", State = "idle",
                    Started = true, Ts = new DateTimeOffset(mtime).ToUnixTimeSeconds(),
                };
                result.Add(Snapshot(s, true, liveTranscripts));
            }
        }

        // Stray temp files from a hook killed mid-write.
        if (Directory.Exists(_stateDir))
            foreach (var tmp in Directory.EnumerateFiles(_stateDir, "*.tmp"))
                try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(tmp) > TimeSpan.FromHours(1)) File.Delete(tmp); } catch { }

        foreach (var gone in _files.Keys.Where(k => !seen.Contains(k)).ToList()) _files.Remove(gone);
        foreach (var gone in _trackers.Keys.Where(k => !liveTranscripts.Contains(k)).ToList()) _trackers.Remove(gone);
        return result;
    }

    SessionSnapshot Snapshot(StateFile s, bool inWindow, HashSet<string> liveTranscripts)
    {
        string title = "";
        IReadOnlyList<PlanStep> plan = Array.Empty<PlanStep>();
        Notice? notice = s.Notice;
        if (!string.IsNullOrEmpty(s.Transcript))
        {
            liveTranscripts.Add(s.Transcript);
            if (!_trackers.TryGetValue(s.Transcript, out var tr))
                _trackers[s.Transcript] = tr = new TranscriptTracker(s.Transcript);
            tr.Refresh();
            title = tr.Title;
            plan = tr.Plan;
            // Only once the turn has stopped: mid-turn the question isn't the latest word yet.
            if ((notice is null || !notice.NeedsAction) && s.State is "done" or "idle" && tr.PendingAsk.Length > 0)
                notice = new Notice { Kind = "ask", Title = "判断待ち", Text = tr.PendingAsk, Ts = tr.PendingAskTs };
        }
        return new SessionSnapshot { State = s, Title = title, Plan = plan, Notice = notice, InWindow = inWindow };
    }

    // Main-session transcripts touched in the last RecentDays, newest first (subagent logs excluded).
    List<(string path, DateTime mtime)> RecentTranscripts()
    {
        if (DateTime.UtcNow - _recentAt < RecentRefresh) return _recent;
        _recentAt = DateTime.UtcNow;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RecentDays);
            _recent = Directory.Exists(_projectsDir)
                ? Directory.EnumerateFiles(_projectsDir, "*.jsonl", SearchOption.AllDirectories)
                    .Where(p => !p.Contains(Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .Select(p => (path: p, mtime: File.GetLastWriteTimeUtc(p)))
                    .Where(x => x.mtime >= cutoff)
                    .OrderByDescending(x => x.mtime)
                    .ToList()
                : new();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return _recent;
    }

    public static bool IsLive(bool pidAlive, long ts, long now) => pidAlive || now - ts <= StaleSeconds;

    static void TryDelete(string f)
    {
        try { File.Delete(f); } catch { /* the hook may be rewriting it; next poll */ }
    }

    static bool Alive(int pid)
    {
        if (pid <= 0) return false;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}
