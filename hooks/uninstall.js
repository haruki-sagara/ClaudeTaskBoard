#!/usr/bin/env node
// Removes only the ClaudeTaskBoard hook entries from ~/.claude/settings.json.

const fs = require("fs");
const os = require("os");
const path = require("path");

const MARKER = path.join(os.homedir(), ".claude", "taskboard", "hook.js");
const settingsPath = path.join(os.homedir(), ".claude", "settings.json");
if (!fs.existsSync(settingsPath)) { console.log("No settings.json; nothing to do."); process.exit(0); }

const settings = JSON.parse(fs.readFileSync(settingsPath, "utf8"));
for (const evt of Object.keys(settings.hooks || {})) {
  settings.hooks[evt] = (settings.hooks[evt] || [])
    .map((e) => ({ ...e, hooks: (e.hooks || []).filter((h) => !(h.command || "").includes(MARKER)) }))
    .filter((e) => (e.hooks || []).length > 0);
  if (settings.hooks[evt].length === 0) delete settings.hooks[evt];
}
fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2) + "\n");
console.log("Removed ClaudeTaskBoard hooks from", settingsPath);
