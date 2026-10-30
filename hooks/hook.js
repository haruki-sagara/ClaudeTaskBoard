#!/usr/bin/env node
// ClaudeTaskBoard hook: keeps one state file per Claude Code session for the board to read.
// Usage (from settings.json hooks): node hook.js <start|end|prompt|pre|post|permreq|notify|stop>
// Hook JSON arrives on stdin. Never blocks or fails the session: every error is swallowed, exit 0.
//
// State file: <home>\state.d\<session_id>.json, home = %TASKBOARD_HOME% or ~/.claude/taskboard
//   state:  idle | thinking | tool | question | permission | done
//   notice: null | { kind, title, text, ts }   — something Claude Code is asking the user / telling them
//     kind: permission (approve a tool) | question (AskUserQuestion) | plan (ExitPlanMode) | idle | info

const fs = require("fs");
const os = require("os");
const path = require("path");

const home = process.env.TASKBOARD_HOME || path.join(os.homedir(), ".claude", "taskboard");
const stateDir = path.join(home, "state.d");
const event = process.argv[2] || "";

const TOOL_LABELS = {
  Bash: "Running command", PowerShell: "Running command", Edit: "Editing", Write: "Writing",
  MultiEdit: "Editing", NotebookEdit: "Editing", Read: "Reading", Grep: "Searching", Glob: "Searching",
  WebFetch: "Browsing web", WebSearch: "Searching web", Task: "Delegating", Agent: "Delegating",
  TodoWrite: "Planning",
};

const safeId = (s) => String(s || "").replace(/[^A-Za-z0-9_.-]/g, "").slice(0, 64) || "unknown";
const clip = (s, n = 240) => { s = String(s || "").replace(/\s+/g, " ").trim(); return s.length > n ? s.slice(0, n - 1) + "…" : s; };
const shortTool = (t) => { const i = String(t || "").lastIndexOf("__"); return i >= 0 ? t.slice(i + 2) : String(t || ""); };

// What is being asked for, in one line: the command, the file, the URL, or the first string field.
function describe(tool, input) {
  input = input && typeof input === "object" ? input : {};
  const v = input.command || input.file_path || input.notebook_path || input.url || input.pattern || input.query ||
    Object.values(input).find((x) => typeof x === "string" && x.trim());
  const name = shortTool(tool) || "ツール";
  return v ? `${name}: ${clip(v, 200)}` : name;
}

let raw = "", done = false;
process.stdin.on("data", (d) => (raw += d));
process.stdin.on("end", run);
process.stdin.on("error", run);
setTimeout(run, 1500); // hooks always pipe stdin, but never hang the session

function run() {
  if (done) return; done = true;
  try { main(); } catch {}
  if (event === "start" || event === "prompt") { try { ensureBoard(); } catch {} }
  process.exit(0);
}

// Self-healing: if the board's heartbeat (board.json, rewritten every 5 s) has gone stale, it crashed or
// was killed — start it again. Not when the user closed it on purpose (closedByUser). Only on session
// start / prompt, so a busy tool loop pays nothing. Skipped in isolated test homes (TASKBOARD_HOME).
function ensureBoard() {
  if (process.platform !== "win32" || process.env.TASKBOARD_HOME) return;
  const hbFile = path.join(home, "board.json");
  let hb = {};
  try { hb = JSON.parse(fs.readFileSync(hbFile, "utf8")); } catch {}
  if (hb.closedByUser) return;
  const now = Math.floor(Date.now() / 1000);
  if (now - (hb.ts || 0) < 30) return; // alive (or another hook just launched it)

  const fallback = path.join(process.env.LOCALAPPDATA || "", "ClaudeTaskBoard", "ClaudeTaskBoard.exe");
  const exe = hb.exe && fs.existsSync(hb.exe) ? hb.exe : fallback;
  if (!fs.existsSync(exe)) return;

  // Claim the launch first so hooks firing in parallel don't all start one.
  fs.writeFileSync(hbFile, JSON.stringify({ ...hb, ts: now, launching: true }));
  // Through explorer.exe: the board then runs as a normal desktop app, outside the app container the
  // Claude desktop app runs its hooks in (where it would see a virtualized registry and die with it).
  require("child_process").spawn("explorer.exe", [exe], { detached: true, stdio: "ignore" }).unref();
}

function main() {
  let p = {};
  try { p = JSON.parse(raw || "{}"); } catch {}
  const sid = safeId(p.session_id);
  const file = path.join(stateDir, sid + ".json");

  if (event === "end") { try { fs.rmSync(file, { force: true }); } catch {} return; }

  let prev = {};
  try { prev = JSON.parse(fs.readFileSync(file, "utf8")); } catch {}

  const ts = Math.floor(Date.now() / 1000);
  const tool = p.tool_name || "";
  let state = prev.state || "idle", label = prev.label || "", startedAt = prev.startedAt || 0;
  let notice = prev.notice || null, started = !!prev.started;
  const asking = (k) => notice && notice.kind === k;

  switch (event) {
    case "start": // opened or resumed: no turn running yet
      state = "idle"; label = ""; startedAt = 0; notice = null; break;
    case "prompt":
      state = "thinking"; label = "Thinking…"; startedAt = ts; notice = null; started = true; break;
    case "pre":
      started = true;
      if (!startedAt) startedAt = ts;
      if (shortTool(tool) === "AskUserQuestion") {
        const q = (p.tool_input && Array.isArray(p.tool_input.questions) && p.tool_input.questions[0]) || {};
        state = "question"; label = "Waiting for answer";
        notice = { kind: "question", title: "質問への回答待ち", text: clip(q.question || "Claude が質問しています"), ts };
      } else if (shortTool(tool) === "ExitPlanMode") {
        state = "question"; label = "Waiting for plan approval";
        notice = { kind: "plan", title: "プランの承認待ち", text: "プランを確認して承認してください", ts };
      } else {
        state = "tool"; label = TOOL_LABELS[tool] || "Using tool";
        if (asking("idle") || asking("info")) notice = null;
      }
      break;
    case "post":
      // The tool ran, so whatever was being asked about it has been answered.
      state = "thinking"; label = "Thinking…"; started = true;
      if (!startedAt) startedAt = ts;
      if (asking("permission") || asking("question") || asking("plan")) notice = null;
      break;
    case "permreq":
      state = "permission"; label = "Awaiting permission"; started = true;
      notice = { kind: "permission", title: "許可の確認", text: describe(tool, p.tool_input), ts };
      break;
    case "notify": {
      const type = p.notification_type || "";
      const msg = clip(p.message || "");
      const m = msg.toLowerCase();
      if (type === "permission_prompt" || (!type && (m.includes("permission") || m.includes("approve")))) {
        state = "permission"; label = "Awaiting permission"; started = true;
        // Keep the richer PermissionRequest detail when it already arrived for this prompt.
        if (!asking("permission")) notice = { kind: "permission", title: "許可の確認", text: msg || "許可が必要です", ts };
      } else if (type === "idle_prompt") {
        notice = { kind: "idle", title: "入力待ち", text: msg || "Claude が入力を待っています", ts };
      } else if (msg) {
        if (!asking("permission") && !asking("question") && !asking("plan"))
          notice = { kind: "info", title: clip(p.title || "通知", 40), text: msg, ts };
      } else return;
      break;
    }
    case "stop":
      state = "done"; label = "Done"; startedAt = 0; started = true;
      if (notice && notice.kind !== "info") notice = null; // the turn is over; nothing is pending on it
      break;
    default:
      return;
  }

  const out = {
    sessionId: p.session_id || prev.sessionId || "",
    state, label, tool, startedAt, ts, started, notice,
    project: p.cwd ? path.basename(p.cwd) : prev.project || "",
    transcript: p.transcript_path || prev.transcript || "",
    entrypoint: process.env.CLAUDE_CODE_ENTRYPOINT || prev.entrypoint || "",
    pid: process.ppid,
  };
  fs.mkdirSync(stateDir, { recursive: true });
  const tmp = file + "." + process.pid + ".tmp";
  fs.writeFileSync(tmp, JSON.stringify(out));
  fs.renameSync(tmp, file);
}
