#!/usr/bin/env node
// Registers the ClaudeTaskBoard hook in ~/.claude/settings.json. Merges — other hooks are left untouched —
// and is re-runnable: our own entries (recognized by the hook path) are replaced, never duplicated.

const fs = require("fs");
const os = require("os");
const path = require("path");

const home = path.join(os.homedir(), ".claude", "taskboard");
const hookDest = path.join(home, "hook.js");
const settingsPath = path.join(os.homedir(), ".claude", "settings.json");
const MARKER = hookDest;

fs.mkdirSync(home, { recursive: true });
fs.copyFileSync(path.join(__dirname, "hook.js"), hookDest);

// Quote both paths: profiles and Node installs routinely contain spaces on Windows.
const cmd = (evt) => `"${process.execPath}" "${hookDest}" ${evt}`;

let settings = {};
if (fs.existsSync(settingsPath)) {
  settings = JSON.parse(fs.readFileSync(settingsPath, "utf8"));
  const bak = settingsPath + ".bak-taskboard";
  if (!fs.existsSync(bak)) fs.copyFileSync(settingsPath, bak);
}
settings.hooks = settings.hooks || {};

const stripOurs = (arr) => (arr || [])
  .map((e) => ({ ...e, hooks: (e.hooks || []).filter((h) => !(h.command || "").includes(MARKER)) }))
  .filter((e) => (e.hooks || []).length > 0);

const add = (evt, arg, matched) => {
  settings.hooks[evt] = stripOurs(settings.hooks[evt]);
  settings.hooks[evt].push({ ...(matched ? { matcher: "*" } : {}), hooks: [{ type: "command", command: cmd(arg) }] });
};

add("SessionStart", "start", false);
add("SessionEnd", "end", false);
add("UserPromptSubmit", "prompt", false);
add("PreToolUse", "pre", true);
add("PostToolUse", "post", true);
add("PermissionRequest", "permreq", true);
add("Notification", "notify", false);
add("Stop", "stop", false);

fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2) + "\n");
console.log("Hook installed:", hookDest);
console.log("Registered in:", settingsPath, "(backup on first run: settings.json.bak-taskboard)");
