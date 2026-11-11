using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace ClaudeTaskBoard;

// Framework-free checks for the data layer: ClaudeTaskBoard.exe --selftest (exit code 0 = pass).
static class SelfTest
{
    static Window TrialWindow()
    {
        var w = new Window { Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false, Left = 100, Top = 100, Title = "ClaudeTaskBoard selftest" };
        w.Show();
        return w;
    }

    public static int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string msg) { Console.WriteLine((ok ? "ok   " : "FAIL ") + msg); if (!ok) failures.Add(msg); }

        static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
        const string Up = "mcp__ccd_turn__update_plan";
        string Statuses(List<PlanStep> p) => string.Concat(p.Select(s => s.Status switch
        {
            StepStatus.Completed => 'x', StepStatus.InProgress => '>', _ => '.',
        }));

        var plan = PlanReducer.Apply(new(), Up, J("""{"items":[{"content":"a","status":"in_progress"},{"content":"b","status":"pending"},{"content":"c","status":"pending"}],"scope":"first_task"}"""));
        Check(plan.Count == 3 && Statuses(plan) == ">..", "items replaces the list");

        plan = PlanReducer.Apply(plan, Up, J("""{"advance":true}"""));
        Check(Statuses(plan) == "x>.", "advance completes current and starts next");

        plan = PlanReducer.Apply(plan, Up, J("""{"done":[2],"start":3}"""));
        Check(Statuses(plan) == "xx>", "done + start out of order");

        plan = PlanReducer.Apply(plan, Up, J("""{"start":1}"""));
        Check(Statuses(plan) == ">x.", "start sets aside the running step (pending, not completed)");

        plan = PlanReducer.Apply(plan, Up, J("""{"advance":true}"""));
        Check(Statuses(plan) == "xx>", "advance skips already-completed steps");

        plan = PlanReducer.Apply(plan, Up, J("""{"advance":true}"""));
        Check(Statuses(plan) == "xxx", "advance on the last step finishes the list");

        plan = PlanReducer.Apply(PlanReducer.Apply(new(), Up, J("""{"items":[{"content":"a","status":"in_progress"},{"content":"b","status":"pending"}]}""")),
            "mcp__ccd_turn__wrap_up", J("""{"recap":"r"}"""));
        Check(Statuses(plan) == "xx", "wrap_up checks everything off");

        plan = PlanReducer.Apply(new(), Up, J("""{"done":[9],"start":0,"advance":true}"""));
        Check(plan.Count == 0, "deltas on an empty list are ignored, no crash");

        plan = PlanReducer.Apply(new(), "TodoWrite", J("""{"todos":[{"content":"t1","status":"completed","activeForm":"T1"},{"content":"t2","status":"in_progress","activeForm":"T2"}]}"""));
        Check(Statuses(plan) == "x>" && plan[1].Content == "t2", "TodoWrite replaces the list");

        Check(!PlanReducer.IsPlanTool("Bash") && PlanReducer.IsPlanTool("TodoWrite"), "only task-list tools count");

        // Transcript tailing: incremental reads, half-written last line, sidechain lines ignored.
        string tmp = Path.Combine(Path.GetTempPath(), $"taskboard-selftest-{Environment.ProcessId}.jsonl");
        try
        {
            static string Use(string name, string input, bool side = false) =>
                "{\"type\":\"assistant\",\"isSidechain\":" + (side ? "true" : "false") +
                ",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"" + name + "\",\"input\":" + input + "}]}}\n";

            File.WriteAllText(tmp,
                """{"type":"custom-title","customTitle":"テスト題名","sessionId":"s"}""" + "\n" +
                Use(Up, """{"items":[{"content":"一","status":"in_progress"},{"content":"二","status":"pending"}]}"""),
                new UTF8Encoding(false));
            var tr = new TranscriptTracker(tmp);
            Check(tr.Refresh() && tr.Title == "テスト題名" && Statuses(tr.Plan) == ">.", "reads title and plan (UTF-8)");

            File.AppendAllText(tmp, Use(Up, """{"advance":true}""", side: true));
            tr.Refresh();
            Check(Statuses(tr.Plan) == ">.", "sidechain (subagent) task calls are ignored");

            string adv = Use(Up, """{"advance":true}""");
            File.AppendAllText(tmp, adv[..20]);
            tr.Refresh();
            Check(Statuses(tr.Plan) == ">.", "half-written line waits");
            File.AppendAllText(tmp, adv[20..]);
            tr.Refresh();
            Check(Statuses(tr.Plan) == "x>", "completed line is picked up next read");

            Check(!tr.Refresh(), "no new bytes => no change");

            File.WriteAllText(tmp, Use(Up, """{"items":[{"content":"new","status":"pending"}]}"""));
            tr.Refresh();
            Check(tr.Plan.Count == 1 && tr.Plan[0].Content == "new", "shrunk (rewritten) file restarts from the top");

            // End-of-turn decision (wrap_up "ask"): pending until the user's next real prompt.
            File.AppendAllText(tmp, Use("mcp__ccd_turn__wrap_up", """{"recap":"r","ask":"コミットしますか？"}"""));
            tr.Refresh();
            Check(tr.PendingAsk == "コミットしますか？", "wrap_up ask becomes a pending decision");
            File.AppendAllText(tmp, """{"type":"user","message":{"content":[{"type":"tool_result","content":"x"}]}}""" + "\n");
            tr.Refresh();
            Check(tr.PendingAsk.Length > 0, "a tool result is not the user's answer");
            File.AppendAllText(tmp, """{"type":"user","message":{"content":"はい"}}""" + "\n");
            tr.Refresh();
            Check(tr.PendingAsk.Length == 0, "the user's next prompt answers it");
            File.AppendAllText(tmp, Use("mcp__ccd_turn__wrap_up", """{"recap":"r"}"""));
            tr.Refresh();
            Check(tr.PendingAsk.Length == 0, "wrap_up without ask leaves nothing pending");
        }
        finally { try { File.Delete(tmp); } catch { } }

        // Sessions open in their own window are always listed: stale ones, and ones with no state file.
        string root = Path.Combine(Path.GetTempPath(), $"taskboard-selftest-store-{Environment.ProcessId}");
        try
        {
            string sd = Path.Combine(root, "state.d"), pd = Path.Combine(root, "projects", "p");
            Directory.CreateDirectory(sd);
            Directory.CreateDirectory(Path.Combine(pd, "x", "subagents"));
            string T(string name, string title)
            {
                string path = Path.Combine(pd, name + ".jsonl");
                File.WriteAllText(path, $"{{\"type\":\"custom-title\",\"customTitle\":\"{title}\"}}\n", new UTF8Encoding(false));
                return path;
            }
            long old = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10 * 3600; // stale: past the 4h liveness window
            void State(string id, string transcript, long ts) => File.WriteAllText(Path.Combine(sd, id + ".json"),
                JsonSerializer.Serialize(new StateFile { SessionId = id, Transcript = transcript, State = "done", Started = true, Ts = ts }));
            State("s1", T("s1", "窓あり"), old);
            State("s2", T("s2", "窓なし"), old);
            T("s3", "フックなし窓");
            File.WriteAllText(Path.Combine(pd, "x", "subagents", "agent.jsonl"), "{\"type\":\"custom-title\",\"customTitle\":\"サブ\"}\n");

            var store = new SessionStore(sd, Path.Combine(root, "projects"),
                () => new HashSet<string> { "窓あり", "フックなし窓", "サブ" });
            var got = store.Poll().ToDictionary(x => x.State.SessionId);
            Check(got.TryGetValue("s1", out var s1) && s1.InWindow, "stale session open in a window is still listed");
            Check(!got.ContainsKey("s2"), "stale session without a window is dropped");
            Check(got.TryGetValue("s3", out var s3) && s3.InWindow && s3.Title == "フックなし窓",
                "window with no state file is found through its transcript title");
            Check(!got.Values.Any(x => x.Title == "サブ"), "subagent transcripts are never matched");
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        // Weekly usage: response parsing and the token's validity (fake values only).
        var week = UsageClient.Parse("""{"five_hour":{"utilization":3.0,"resets_at":null},"seven_day":{"utilization":25.9,"resets_at":"2026-11-08T14:00:00.044+00:00"}}""");
        Check(week is { PercentUsed: 25, Error: "" } && week.ResetsAt == DateTimeOffset.Parse("2026-11-08T14:00:00.044Z"),
            "seven_day is read, floored like the official display");
        Check(UsageClient.Parse("""{"seven_day":{"utilization":140,"resets_at":null}}""") is { PercentUsed: 100, ResetsAt: null }, "percent clamped; reset optional");
        Check(UsageClient.Parse("""{"seven_day":{"utilization":null}}""") is null, "null utilization => unreadable, not 0%");
        Check(UsageClient.Parse("""{"seven_day":null}""") is null && UsageClient.Parse("[]") is null && UsageClient.Parse("<html>") is null, "unexpected bodies => unreadable");

        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000);
        string Cred(long exp) => "{\"claudeAiOauth\":{\"accessToken\":\"fake\",\"expiresAt\":" + exp + "}}";
        const long Margin = 5 * 60 * 1000;
        Check(UsageClient.ReadToken(Cred(1_000_001 + Margin), at).token == "fake", "valid token is used");
        Check(UsageClient.ReadToken(Cred(1_000_000 + Margin), at) is (null, UsageClient.ErrExpired), "a token about to expire counts as expired");
        Check(UsageClient.ReadToken(Cred(1_000), at) is (null, UsageClient.ErrExpired), "expired token is not sent");
        Check(UsageClient.ReadToken("{}", at) is (null, UsageClient.ErrNoLogin) && UsageClient.ReadToken("oops", at) is (null, UsageClient.ErrNoLogin),
            "missing or broken credentials => no login");

        // Token refresh: only the login's own fields change; nothing else in the file is lost.
        const string Creds = """{"other":{"keep":1},"claudeAiOauth":{"accessToken":"a1","refreshToken":"r1","expiresAt":1,"scopes":["s1","s2"],"subscriptionType":"pro","rateLimitTier":"t"}}""";
        Check(UsageClient.ReadRefreshToken(Creds) == ("r1", "s1 s2"), "refresh token and scopes are read");
        Check(UsageClient.ReadRefreshToken("{}") is null && UsageClient.ReadRefreshToken("oops") is null, "no refresh token => none");
        var saved = UsageClient.ApplyRefresh(Creds, """{"access_token":"a2","refresh_token":"r2","expires_in":3600,"scope":"s1 s3"}""", at);
        var savedLogin = saved is null ? default : J(saved).GetProperty("claudeAiOauth");
        Check(saved is not null && savedLogin.GetProperty("accessToken").GetString() == "a2" && savedLogin.GetProperty("refreshToken").GetString() == "r2"
            && savedLogin.GetProperty("expiresAt").GetInt64() == 1_000_000 + 3_600_000
            && savedLogin.GetProperty("scopes").GetArrayLength() == 2 && savedLogin.GetProperty("scopes")[1].GetString() == "s3", "refresh response is folded in");
        Check(saved is not null && savedLogin.GetProperty("subscriptionType").GetString() == "pro" && J(saved).GetProperty("other").GetProperty("keep").GetInt32() == 1,
            "everything else in the file is kept");
        Check(UsageClient.ReadToken(saved ?? "", at).token == "a2", "the saved file reads back as valid");
        var keepRefresh = UsageClient.ApplyRefresh(Creds, """{"access_token":"a2","expires_in":3600}""", at);
        Check(keepRefresh is not null && UsageClient.ReadRefreshToken(keepRefresh)?.refresh == "r1", "no rotation in the response => the refresh token stays");
        Check(UsageClient.ApplyRefresh(Creds, """{"error":"invalid_grant"}""", at) is null && UsageClient.ApplyRefresh(Creds, "<html>", at) is null
            && UsageClient.ApplyRefresh("{}", """{"access_token":"a","expires_in":1}""", at) is null, "a bad response never overwrites the login");

        // The re-login button: only when logging in is the fix, and gone once usage reads again.
        var board = new BoardVM();
        board.ApplyUsage(new UsageResult(null, null, UsageClient.ErrRelogin, TimeSpan.Zero));
        Check(board.ShowLogin && !board.HasUsage, "invalid login => the re-login button shows");
        board.ApplyUsage(new UsageResult(null, null, "取得できませんでした", TimeSpan.Zero));
        Check(!board.ShowLogin, "a passing failure => no re-login button");
        board.ApplyUsage(new UsageResult(null, null, UsageClient.ErrExpired, TimeSpan.Zero));
        board.ApplyUsage(new UsageResult(25, null, "", TimeSpan.Zero));
        Check(!board.ShowLogin && board.HasUsage, "usage reads again => the button is gone");

        board.ApplyUsage(new UsageResult(25, DateTimeOffset.Parse("2026-11-08T13:59:59.900Z"), "", TimeSpan.Zero));
        string justBefore = board.UsageDetail;
        board.ApplyUsage(new UsageResult(25, DateTimeOffset.Parse("2026-11-08T14:00:00.044Z"), "", TimeSpan.Zero));
        Check(board.UsageDetail == justBefore, "the reset time does not flip with the server's jitter around the hour");

        // Right-click menu: the path has to survive as one URL-encoded query value.
        string Decoded(string p) => Uri.UnescapeDataString(FolderLink.Build(p)["claude://code/new?folder=".Length..]);
        Check(FolderLink.Build(@"C:\a b\日本語&x=1#y%z+.").Count(c => c is '&' or '#' or ' ' or '+' or '\\') == 0, "special characters are encoded");
        Check(Decoded(@"C:\a b\日本語&x=1#y%z+") == @"C:\a b\日本語&x=1#y%z+", "encoded path decodes back unchanged");
        Check(Decoded(@"C:\a b\.") == @"C:\a b" && Decoded(@"C:\.") == @"C:\", "the menu's trailing '.' is normalized away");

        // Window arrangement: the layout math, then real (throwaway) windows of this process.
        var area = new Box(100, 50, 1000, 601);
        bool Fills(List<Box> b) => b.Sum(x => x.W * x.H) == area.W * area.H && b.All(x => x.X >= area.X && x.Right <= area.Right && x.Y >= area.Y && x.Bottom <= area.Bottom);
        var col3 = ArrangeCalc.Layout(ArrangeLayout.Columns, 3, area);
        Check(col3.Count == 3 && col3.All(b => b.H == area.H && b.Y == area.Y) && col3[0].Right == col3[1].X && Fills(col3), "columns: full height, side by side, no gaps");
        var tile4 = ArrangeCalc.Layout(ArrangeLayout.Tile, 4, area);
        Check(tile4.Count == 4 && tile4.Select(b => b.X).Distinct().Count() == 2 && tile4.Select(b => b.Y).Distinct().Count() == 2 && Fills(tile4), "tile: 4 windows make a 2x2 grid");
        var tile3 = ArrangeCalc.Layout(ArrangeLayout.Tile, 3, area);
        Check(tile3.Count == 3 && tile3[2].W == area.W && Fills(tile3), "tile: the last row stretches to fill");
        Check(Enumerable.Range(1, 12).All(n => ArrangeCalc.Layout(ArrangeLayout.Tile, n, area).Count == n && Fills(ArrangeCalc.Layout(ArrangeLayout.Tile, n, area))), "tile: any count fills the area exactly");
        Check(ArrangeCalc.Layout(ArrangeLayout.Tile, 0, area).Count == 0, "no windows => nothing to place");

        // Pressing the same arrange button again.
        Check(tile4[0].X == tile4[2].X && tile4[1].X == tile4[3].X && tile4[0].Y == tile4[1].Y && tile4[2].Y == tile4[3].Y && tile4[0].X < tile4[1].X && tile4[0].Y < tile4[2].Y,
            "tile fills in a Z: top-left, top-right, bottom-left, bottom-right");
        Check(string.Concat(ArrangeCalc.NextOrder("abcd".ToList(), "abcd".ToList())) == "bcda", "again: the first goes last, the rest move up");
        Check(string.Concat(ArrangeCalc.NextOrder(ArrangeCalc.NextOrder("abc".ToList(), "abc".ToList()), "abc".ToList())) == "cab", "again and again keeps rotating");
        Check(string.Concat(ArrangeCalc.NextOrder("bcda".ToList(), "abcd".ToList())) == "cdab", "the rotated order wins over the board's order");
        Check(string.Concat(ArrangeCalc.NextOrder("abc".ToList(), "acd".ToList())) == "cda", "closed windows drop out, new ones join at the end");
        Check(string.Concat(ArrangeCalc.NextOrder("a".ToList(), "a".ToList())) == "a" && ArrangeCalc.NextOrder(new List<char>(), new List<char>()).Count == 0, "one or none: nothing to rotate");

        var work = new Box(0, 0, 1000, 800);
        Check(ArrangeCalc.Exclude(work, new Box(800, 60, 200, 300)) == new Box(0, 0, 800, 800), "board on the right: use the left side");
        Check(ArrangeCalc.Exclude(work, new Box(0, 60, 200, 300)) == new Box(200, 0, 800, 800), "board on the left: use the right side");
        Check(ArrangeCalc.Exclude(work, new Box(2000, 0, 200, 300)) == work, "board on another monitor: whole area");

        // Width limits: a window gets no wider than its text can use; the rest goes to the others.
        var wide = new Box(100, 0, 3000, 800);
        var capped = ArrangeCalc.Layout(ArrangeLayout.Columns, 2, wide, new[] { 1000, 1000 });
        Check(capped.Select(b => (b.X, b.W)).SequenceEqual(new[] { (100, 1000), (1100, 1000) }) && capped.All(b => b.H == 800),
            "columns: capped windows keep to their width, packed from the left");
        var mixed = ArrangeCalc.Layout(ArrangeLayout.Columns, 3, wide, new[] { 600, int.MaxValue, 700 });
        Check(mixed.Select(b => b.W).SequenceEqual(new[] { 600, 1700, 700 }) && mixed[1].X == 700 && mixed[2].Right == wide.Right,
            "columns: a terminal (no limit) takes what the capped ones leave");
        var roomy = ArrangeCalc.Layout(ArrangeLayout.Columns, 3, area, new[] { 5000, 5000, 5000 });
        Check(roomy.SequenceEqual(ArrangeCalc.Layout(ArrangeLayout.Columns, 3, area)), "limits wider than the share change nothing");
        var tileCap = ArrangeCalc.Layout(ArrangeLayout.Tile, 3, wide, new[] { 1000, 1000, 1200 });
        Check(tileCap.Select(b => (b.X, b.W)).SequenceEqual(new[] { (100, 1000), (1100, 1000), (100, 1200) }) && tileCap[2].Y == 400,
            "tile: each cell keeps to its window's limit, Z order kept");
        var right = ArrangeCalc.Layout(ArrangeLayout.Columns, 2, wide, new[] { 1000, 1000 }, alignRight: true);
        Check(right.Select(b => (b.X, b.W)).SequenceEqual(new[] { (1100, 1000), (2100, 1000) }) && right[1].Right == wide.Right,
            "align right: capped windows packed to the right edge, order kept");
        var tileRight = ArrangeCalc.Layout(ArrangeLayout.Tile, 3, wide, new[] { 1000, 1000, 1200 }, alignRight: true);
        Check(tileRight.Select(b => (b.X, b.W)).SequenceEqual(new[] { (1100, 1000), (2100, 1000), (1900, 1200) }),
            "align right: each tile row ends at the right edge");
        Check(ArrangeCalc.Layout(ArrangeLayout.Columns, 3, area, alignRight: true).SequenceEqual(ArrangeCalc.Layout(ArrangeLayout.Columns, 3, area)),
            "align right: no effect when the windows fill the width");

        // Session cards: columns by board width.
        Check(TilePanel.ColumnsFor(314, 280, 6) == 1 && TilePanel.ColumnsFor(566, 280, 6) == 2 && TilePanel.ColumnsFor(565, 280, 6) == 1
            && TilePanel.ColumnsFor(1200, 280, 6) == 4, "cards: as many columns as fit");
        Check(TilePanel.ColumnsFor(0, 280, 6) == 1 && TilePanel.ColumnsFor(double.PositiveInfinity, 280, 6) == 1 && TilePanel.ColumnsFor(100, 280, 6) == 1,
            "cards: at least one column, whatever the width");

        // ✕ on a card: hidden until the session does something new.
        var dismissed = new DismissList();
        dismissed.Add("a", 100);
        Check(dismissed.Hides("a", 100) && dismissed.Hides("a", 99) && !dismissed.Hides("b", 100), "a put-away card stays hidden while nothing happens");
        Check(!dismissed.Hides("a", 101) && !dismissed.Hides("a", 100), "new activity brings it back, for good");

        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; // closing the test windows must not end the run
        var trial = new[] { TrialWindow(), TrialWindow() };
        try
        {
            var handles = trial.Select(w => new System.Windows.Interop.WindowInteropHelper(w).Handle).ToList();
            var spots = new[] { new Box(200, 200, 500, 400), new Box(700, 200, 500, 400) };
            trial[1].WindowState = WindowState.Maximized;
            for (int i = 0; i < 2; i++) WindowArranger.Place(handles[i], spots[i]);
            for (int i = 0; i < 2; i++)
            {
                var got = WindowArranger.BoundsOf(handles[i], visibleOnly: true);
                Check(Math.Abs(got.X - spots[i].X) <= 2 && Math.Abs(got.Y - spots[i].Y) <= 2 && Math.Abs(got.W - spots[i].W) <= 2 && Math.Abs(got.H - spots[i].H) <= 2,
                    $"a real window lands where asked, without border gaps ({i})");
            }
            Check(trial[1].WindowState == WindowState.Normal, "a maximized window is restored first");
            Check(WindowArranger.IsUsable(handles[0]) && !WindowArranger.IsUsable(IntPtr.Zero), "closed windows are recognized");
        }
        finally { foreach (var w in trial) w.Close(); }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Check(SessionStore.IsLive(false, now, now), "dead pid + fresh ts => live");
        Check(!SessionStore.IsLive(false, now - 5 * 3600, now), "dead pid + stale ts => dropped");

        Console.WriteLine(failures.Count == 0 ? "selftest OK" : $"selftest FAILED ({failures.Count})");
        return failures.Count == 0 ? 0 : 1;
    }
}
