namespace Loupedeck.ClaudeControllerPlugin.Services
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text.Encodings.Web;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    // Adds the plugin's hooks and statusline bridge to ~/.claude/settings.json (and removes them again).
    // Only our own entries (marked with client=claude-controller) are touched; everything else stays as it is.
    // The hooks are harmless while the plugin is not running: curl gives up after 0.5 s and the hook exits 0.
    public static class HookInstaller
    {
        public const Int32 Port = 47821;
        public const String ClientMarker = "client=claude-controller";

        private const String OfficialPluginMarker = "client=logi-claude-desktop";

        private static readonly String HomeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private static readonly String SettingsPath = Path.Combine(HomeDir, ".claude", "settings.json");
        private static readonly String BackupPath = SettingsPath + ".claude-controller.bak";

        public static readonly String DataDir = Path.Combine(HomeDir, "Library", "Application Support", "ClaudeController");
        private static readonly String BridgePath = Path.Combine(DataDir, "statusline-bridge.sh");
        private static readonly String OriginalStatusLinePath = Path.Combine(DataDir, "statusline-original.json");
        private static readonly String OriginalCommandPath = Path.Combine(DataDir, "statusline-original-command");

        // Event name, matcher needed, endpoint, timeout in seconds
        private static readonly (String Event, Boolean Matcher, String Endpoint, Int32 Timeout)[] Hooks =
        {
            ("SessionStart", false, "hook", 5),
            ("UserPromptSubmit", false, "hook", 5),
            ("PreToolUse", true, "hook", 5),
            ("PostToolUse", true, "hook", 5),
            ("PostToolUseFailure", true, "hook", 5),
            ("Notification", false, "hook", 5),
            ("Stop", false, "hook", 5),
            ("StopFailure", false, "hook", 5),
            ("SubagentStop", false, "hook", 5),
            ("SessionEnd", false, "hook", 5),
            ("PermissionRequest", true, "permission", 330)
        };

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            IndentSize = 2,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // TERM_PROGRAM tells which terminal app runs a session (empty in the desktop app),
        // CLAUDE_CODE_ENTRYPOINT whether it is the desktop app or the CLI,
        // PPID the Claude Code process (terminal sessions leave their slot once it is gone; closing a terminal sends no SessionEnd)
        private static String HookCommand(String evt, String endpoint, Int32 timeout) =>
            $"curl -s --connect-timeout 0.5 -m {timeout} -X POST --data-binary @- " +
            $"\"http://127.0.0.1:{Port}/{endpoint}?event={evt}&{ClientMarker}&term=${{TERM_PROGRAM}}&entry=${{CLAUDE_CODE_ENTRYPOINT}}&pid=${{PPID}}\"; exit 0";

        private static String BridgeCommand => $"\"{BridgePath}\"";

        public static void Install() => Update(install: true);

        public static void Remove() => Update(install: false);

        private static void Update(Boolean install)
        {
            try
            {
                var original = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : "{}";
                if (JsonNode.Parse(String.IsNullOrWhiteSpace(original) ? "{}" : original) is not JsonObject root)
                {
                    PluginLog.Warning("settings.json is not a JSON object, hooks not changed");
                    return;
                }

                var hooks = root["hooks"] as JsonObject;
                if (hooks == null)
                {
                    hooks = new JsonObject();
                    root["hooks"] = hooks;
                }

                RemoveOwnHooks(hooks);
                if (install)
                {
                    AddHooks(hooks);
                    WrapStatusLine(root);
                }
                else
                {
                    UnwrapStatusLine(root);
                }
                if (hooks.Count == 0)
                {
                    root.Remove("hooks");
                }

                WarnAboutOfficialPlugin(root);

                var updated = root.ToJsonString(WriteOptions);
                if (JsonNode.DeepEquals(JsonNode.Parse(original.Length == 0 ? "{}" : original), JsonNode.Parse(updated)))
                {
                    PluginLog.Info(install ? "Hooks already installed" : "No hooks to remove");
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                if (File.Exists(SettingsPath))
                {
                    File.Copy(SettingsPath, BackupPath, overwrite: true);
                }

                // Write next to the original and swap, so Claude Code never reads a half-written file
                var temp = SettingsPath + ".claude-controller.tmp";
                File.WriteAllText(temp, updated + "\n");
                File.Move(temp, SettingsPath, overwrite: true);
                PluginLog.Info(install ? "Hooks installed in ~/.claude/settings.json" : "Hooks removed from ~/.claude/settings.json");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Could not update ~/.claude/settings.json");
            }
        }

        public static Boolean IsInstalled()
        {
            try
            {
                return File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Contains(ClientMarker);
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- Hooks

        private static void RemoveOwnHooks(JsonObject hooks)
        {
            foreach (var evt in hooks.Select(kv => kv.Key).ToList())
            {
                if (hooks[evt] is not JsonArray entries)
                {
                    continue;
                }

                foreach (var entry in entries.ToList())
                {
                    if (IsOwnEntry(entry))
                    {
                        entries.Remove(entry);
                    }
                }
                if (entries.Count == 0)
                {
                    hooks.Remove(evt);
                }
            }
        }

        private static Boolean IsOwnEntry(JsonNode entry) =>
            entry?["hooks"] is JsonArray list &&
            list.Any(h => h?["command"]?.GetValueKind() == JsonValueKind.String && h["command"].GetValue<String>().Contains(ClientMarker));

        private static void AddHooks(JsonObject hooks)
        {
            foreach (var (evt, matcher, endpoint, timeout) in Hooks)
            {
                if (hooks[evt] is not JsonArray entries)
                {
                    entries = new JsonArray();
                    hooks[evt] = entries;
                }

                var entry = new JsonObject();
                if (matcher)
                {
                    entry["matcher"] = "*";
                }
                entry["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = HookCommand(evt, endpoint, timeout),
                    ["timeout"] = timeout
                });
                entries.Add(entry);
            }
        }

        private static void WarnAboutOfficialPlugin(JsonObject root)
        {
            if (root.ToJsonString().Contains(OfficialPluginMarker))
            {
                PluginLog.Warning("settings.json still has hooks of the official Claude Desktop plugin (port 8765). " +
                    "Two blocking permission hooks get in each other's way; uninstall that plugin to remove them.");
            }
        }

        // ---------------------------------------------------------------- Statusline bridge

        // The statusline command is replaced by a small bridge that forwards the statusline JSON (rate limits,
        // context window) to the plugin and then runs the user's original statusline with the same input.
        private static void WrapStatusLine(JsonObject root)
        {
            Directory.CreateDirectory(DataDir);
            var current = root["statusLine"] as JsonObject;
            var currentCommand = current?["command"]?.GetValueKind() == JsonValueKind.String ? current["command"].GetValue<String>() : null;

            if (currentCommand != BridgeCommand)
            {
                // Remember what the user had (also "nothing"), so removing the hooks restores it exactly
                File.WriteAllText(OriginalStatusLinePath, current?.ToJsonString(WriteOptions) ?? "null");
                File.WriteAllText(OriginalCommandPath, currentCommand ?? String.Empty);

                var wrapped = current?.DeepClone() as JsonObject ?? new JsonObject { ["padding"] = 0 };
                wrapped["type"] = "command";
                wrapped["command"] = BridgeCommand;
                root["statusLine"] = wrapped;
            }

            WriteBridgeScript();
        }

        private static void UnwrapStatusLine(JsonObject root)
        {
            var currentCommand = root["statusLine"]?["command"]?.GetValueKind() == JsonValueKind.String
                ? root["statusLine"]["command"].GetValue<String>()
                : null;
            if (currentCommand != BridgeCommand)
            {
                return;
            }

            var original = File.Exists(OriginalStatusLinePath) ? JsonNode.Parse(File.ReadAllText(OriginalStatusLinePath)) : null;
            if (original == null)
            {
                root.Remove("statusLine");
            }
            else
            {
                root["statusLine"] = original;
            }
        }

        private static void WriteBridgeScript()
        {
            var script =
$@"#!/bin/bash
# Claude Controller statusline bridge (written by the Logi plugin, see README).
# Sends the statusline JSON to the plugin in the background, then runs your original statusline unchanged.
INPUT=""$(cat)""
printf '%s' ""$INPUT"" | curl -s --connect-timeout 0.3 -m 2 -X POST --data-binary @- ""http://127.0.0.1:{Port}/statusline?{ClientMarker}"" >/dev/null 2>&1 &
ORIGINAL=""$(cat ""{OriginalCommandPath}"" 2>/dev/null)""
if [ -n ""$ORIGINAL"" ]; then
  printf '%s' ""$INPUT"" | /bin/bash -c ""$ORIGINAL""
fi
";
            if (!File.Exists(BridgePath) || File.ReadAllText(BridgePath) != script)
            {
                File.WriteAllText(BridgePath, script);
            }
            if (OperatingSystem.IsWindows())
            {
                return;
            }
            File.SetUnixFileMode(BridgePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
