using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;

namespace ClaudeTaskBoard;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class StepVM
{
    public required string Content { get; init; }
    public required StepStatus Status { get; init; }
    public bool IsDone => Status == StepStatus.Completed;
    public bool IsActive => Status == StepStatus.InProgress;
    public bool IsPending => Status == StepStatus.Pending;
}

public sealed class SessionVM : Observable
{
    static readonly Brush PermissionBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xB9, 0x31)));
    static readonly Brush WorkingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x3C)));
    static readonly Brush DoneBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0xB2, 0x7F)));
    static readonly Brush IdleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98)));

    static readonly Dictionary<string, string> ToolLabels = new()
    {
        ["Running command"] = "コマンド実行中", ["Editing"] = "編集中", ["Writing"] = "書き込み中",
        ["Reading"] = "読み込み中", ["Searching"] = "検索中", ["Browsing web"] = "Web 閲覧中",
        ["Searching web"] = "Web 検索中", ["Delegating"] = "サブエージェント実行中", ["Planning"] = "計画中",
    };

    public string Id { get; }
    public long FirstSeen { get; }

    public SessionVM(string id, long firstSeen) { Id = id; FirstSeen = firstSeen; }

    // false while the card plays its exit animation; removed from the list afterwards.
    bool _isPresent = true;
    public bool IsPresent { get => _isPresent; set => Set(ref _isPresent, value); }

    string _title = "", _project = "", _stateText = "", _timeText = "", _currentStep = "", _progressText = "";
    string _state = "idle";
    Brush _stateBrush = IdleBrush;
    long _startedAt, _ts;
    int _done, _total;
    bool _isExpanded, _compact;
    IReadOnlyList<StepVM> _steps = Array.Empty<StepVM>();
    string _planSig = "";

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Project { get => _project; private set { if (Set(ref _project, value)) Raise(nameof(ShowProject)); } }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string TimeText { get => _timeText; private set => Set(ref _timeText, value); }
    public Brush StateBrush { get => _stateBrush; private set => Set(ref _stateBrush, value); }
    public string CurrentStep { get => _currentStep; private set { if (Set(ref _currentStep, value)) Raise(nameof(ShowCurrentStep)); } }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public IReadOnlyList<StepVM> Steps { get => _steps; private set => Set(ref _steps, value); }
    public int Done { get => _done; private set { if (Set(ref _done, value)) { Raise(nameof(Progress)); Raise(nameof(ProgressBrush)); } } }
    public Brush ProgressBrush => Total > 0 && Done == Total ? DoneBrush : WorkingBrush;
    public int Total { get => _total; private set { if (Set(ref _total, value)) { Raise(nameof(Progress)); Raise(nameof(HasPlan)); Raise(nameof(ShowPlan)); Raise(nameof(ShowChevron)); Raise(nameof(ShowCompactProgress)); Raise(nameof(ShowCurrentStep)); Raise(nameof(ShowSteps)); } } }
    public double Progress => Total == 0 ? 0 : (double)Done / Total;

    public string State
    {
        get => _state;
        private set { if (Set(ref _state, value)) { Raise(nameof(IsWorking)); Raise(nameof(Priority)); } }
    }
    public bool IsWorking => State is "thinking" or "tool";
    // A finished turn that still asks the user something is not "resting": it must not be dimmed or hidden.
    public bool IsResting => State is "done" or "idle" && !NoticeNeedsAction;

    // Open in its own desktop-app window — always listed (and marked with a window icon).
    bool _inWindow;
    public bool InWindow { get => _inWindow; private set => Set(ref _inWindow, value); }

    // waiting on the user > working > resting — the order sessions are listed in.
    public int Priority => NoticeNeedsAction || State is "permission" or "question" ? 2 : IsWorking ? 1 : 0;

    // ---- what Claude Code is asking / telling the user ----
    static readonly Brush InfoBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF)));
    string _noticeTitle = "", _noticeText = "", _noticeKind = "", _noticeKey = "";
    public string NoticeTitle { get => _noticeTitle; private set => Set(ref _noticeTitle, value); }
    public string NoticeText { get => _noticeText; private set => Set(ref _noticeText, value); }
    public string NoticeKind { get => _noticeKind; private set => Set(ref _noticeKind, value); }
    public bool HasNotice => NoticeKind.Length > 0;
    public bool NoticeNeedsAction => NoticeKind is "permission" or "question" or "plan" or "ask";
    public Brush NoticeBrush => NoticeNeedsAction ? PermissionBrush : InfoBrush;
    // Changes whenever a different notice arrives — the board toasts on that.
    public string NoticeKey => _noticeKey;

    void SetNotice(Notice? n)
    {
        string key = n is null ? "" : $"{n.Kind}|{n.Ts}|{n.Text}";
        if (key == _noticeKey) return;
        _noticeKey = key;
        NoticeKind = n?.Kind ?? "";
        NoticeTitle = n?.Title ?? "";
        NoticeText = n?.Text ?? "";
        foreach (var p in new[] { nameof(HasNotice), nameof(NoticeNeedsAction), nameof(NoticeBrush), nameof(IsResting), nameof(Priority) })
            Raise(p);
    }

    public bool HasPlan => Total > 0;
    public bool ShowProject => !Compact && Project.Length > 0 && Project != Title;
    public bool ShowPlan => !Compact && HasPlan;
    public bool ShowCurrentStep => !Compact && !IsExpanded && CurrentStep.Length > 0;
    public bool ShowSteps => !Compact && IsExpanded && HasPlan;
    public bool ShowCompactProgress => Compact && HasPlan;
    public bool ShowChevron => !Compact && HasPlan;

    // Where the session runs, for click-to-focus.
    public string Entrypoint { get; private set; } = "";
    public int Pid { get; private set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value)) { Raise(nameof(ShowCurrentStep)); Raise(nameof(ShowSteps)); } }
    }

    public bool Compact
    {
        get => _compact;
        set
        {
            if (!Set(ref _compact, value)) return;
            foreach (var p in new[] { nameof(ShowProject), nameof(ShowPlan), nameof(ShowCurrentStep), nameof(ShowSteps), nameof(ShowCompactProgress), nameof(ShowChevron) })
                Raise(p);
        }
    }

    public void Update(SessionSnapshot snap)
    {
        var s = snap.State;
        State = s.State;
        _startedAt = s.StartedAt;
        _ts = s.Ts;
        Entrypoint = s.Entrypoint;
        Pid = s.Pid;
        Project = s.Project;
        Title = snap.Title.Length > 0 ? snap.Title : (s.Project.Length > 0 ? s.Project : "(無題のセッション)");
        SetNotice(snap.Notice);
        InWindow = snap.InWindow;
        Raise(nameof(IsResting));

        (StateText, StateBrush) = s.State switch
        {
            "permission" => ("許可待ち", PermissionBrush),
            "question" => (NoticeKind == "plan" ? "承認待ち" : "回答待ち", PermissionBrush),
            "done" when NoticeKind == "ask" => ("判断待ち", PermissionBrush),
            "thinking" => ("思考中", WorkingBrush),
            "tool" => (ToolText(s), WorkingBrush),
            "done" => ("完了", DoneBrush),
            _ => ("待機中", IdleBrush),
        };

        // Only rebuild the step list when it actually changed, so expanded cards don't flicker.
        string sig = string.Join("\u001f", snap.Plan.Select(p => $"{(int)p.Status}{p.Content}"));
        if (sig != _planSig)
        {
            _planSig = sig;
            Steps = snap.Plan.Select(p => new StepVM { Content = p.Content, Status = p.Status }).ToList();
            Done = snap.Plan.Count(p => p.Status == StepStatus.Completed);
            Total = snap.Plan.Count;
            ProgressText = $"{Done}/{Total}";
            CurrentStep = snap.Plan.FirstOrDefault(p => p.Status == StepStatus.InProgress)?.Content ?? "";
        }
        Tick(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public void Tick(long now)
    {
        TimeText = State switch
        {
            "thinking" or "tool" when _startedAt > 0 => Duration(now - _startedAt),
            "permission" or "question" => _ts > 0 ? Ago(now - _ts) + "から" : "",
            _ => _ts > 0 ? Ago(now - _ts) : "",
        };
    }

    static string ToolText(StateFile s)
    {
        if (ToolLabels.TryGetValue(s.Label, out var jp)) return jp;
        string tool = s.Tool;
        int i = tool.LastIndexOf("__", StringComparison.Ordinal); // mcp__server__tool -> tool
        if (i >= 0) tool = tool[(i + 2)..];
        return tool.Length > 0 ? $"{tool} 実行中" : "ツール実行中";
    }

    static string Duration(long sec)
    {
        if (sec < 0) sec = 0;
        return sec >= 3600 ? $"{sec / 3600}時間{sec % 3600 / 60}分" : sec >= 60 ? $"{sec / 60}分{sec % 60}秒" : $"{sec}秒";
    }

    static string Ago(long sec)
    {
        if (sec < 60) return "たった今";
        if (sec < 3600) return $"{sec / 60}分前";
        return $"{sec / 3600}時間前";
    }

    static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}

public sealed class Settings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Opacity { get; set; } = 0.95;
    public bool Compact { get; set; }
    public bool Topmost { get; set; } = true;
    public bool HideResting { get; set; }

    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "taskboard", "settings.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path), Options) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
        }
        catch { /* best-effort */ }
    }
}

public sealed class BoardVM : Observable
{
    public ObservableCollection<SessionVM> Sessions { get; } = new();

    string _summary = "";
    bool _isEmpty, _showSettings; // not "empty" until the first poll says so — avoids a startup flash

    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public bool IsEmpty { get => _isEmpty; set => Set(ref _isEmpty, value); }
    public bool ShowSettings { get => _showSettings; set => Set(ref _showSettings, value); }

    bool _confirmExit;
    public bool ConfirmExit { get => _confirmExit; set => Set(ref _confirmExit, value); }

    string _toastText = "";
    bool _showToast;
    public string ToastText { get => _toastText; set => Set(ref _toastText, value); }
    public bool ShowToast { get => _showToast; set => Set(ref _showToast, value); }
}
