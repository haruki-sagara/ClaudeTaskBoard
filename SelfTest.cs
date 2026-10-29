using System.IO;
using System.Text;
using System.Text.Json;

namespace ClaudeTaskBoard;

// Framework-free checks for the data layer: ClaudeTaskBoard.exe --selftest (exit code 0 = pass).
static class SelfTest
{
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

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Check(SessionStore.IsLive(false, now, now), "dead pid + fresh ts => live");
        Check(!SessionStore.IsLive(false, now - 5 * 3600, now), "dead pid + stale ts => dropped");

        Console.WriteLine(failures.Count == 0 ? "selftest OK" : $"selftest FAILED ({failures.Count})");
        return failures.Count == 0 ? 0 : 1;
    }
}
