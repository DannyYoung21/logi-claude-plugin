namespace Loupedeck.ClaudeControllerPlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using Loupedeck.ClaudeControllerPlugin.Models;

    // Answering permission requests and stopping sessions from the keypad.
    //
    // A PermissionRequest hook is held open until the user answers on the keypad. Meanwhile Claude shows its own
    // dialog as usual; whoever answers first wins. When the session moves on without us (answered in the app,
    // turn ended, interrupted), the held hook is released with an empty reply, which means "no decision".
    public partial class SessionStore
    {
        // Answer the hook a little before its own timeout (330 s), so Claude Code never waits for a dead request
        private static readonly TimeSpan PermissionHoldTime = TimeSpan.FromSeconds(320);

        // Stop without a pending request: if no tool hook comes along to carry the stop, the desktop app gets an Esc
        private static readonly TimeSpan EscapeFallbackDelay = TimeSpan.FromMilliseconds(1500);

        private const String KeypadNote = "from the MX Creative Keypad";

        private sealed class PendingPermission
        {
            public String ToolName;
            public String ToolInput;
            public JsonNode Suggestions;
            public readonly TaskCompletionSource<String> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Held requests per Claude Code session id, oldest first (parallel tool calls can ask at the same time)
        private readonly Dictionary<String, List<PendingPermission>> _pending = new(StringComparer.OrdinalIgnoreCase);

        // Sessions to stop at their next tool hook
        private readonly HashSet<String> _stopRequested = new(StringComparer.OrdinalIgnoreCase);

        // Sessions stopped by the keypad: hooks that still trickle in (PostToolUse of the last tool) keep them "Stopped"
        // until the next prompt, because Claude sends no Stop hook after a hook-requested stop
        private readonly HashSet<String> _haltedByKeypad = new(StringComparer.OrdinalIgnoreCase);

        // ---------------------------------------------------------------- Hook side (called under _lock)

        // Releases held requests the session no longer waits for; returns a new held request for PermissionRequest
        // and the stop reply if a stop was requested for this session. halted: the keypad stopped this turn.
        private PendingPermission TrackControl(ClaudeSession session, String evt, JsonElement payload, out String stopReply, out Boolean halted)
        {
            stopReply = null;
            if (evt is "UserPromptSubmit" or "SessionStart")
            {
                this._haltedByKeypad.Remove(session.Id);
            }
            switch (evt)
            {
                case "PostToolUse":
                case "PostToolUseFailure":
                    // That tool ran or failed: its request was answered somewhere else
                    var tool = GetString(payload, "tool_name");
                    var input = payload.TryGetProperty("tool_input", out var i) ? i.GetRawText() : String.Empty;
                    this.ReleasePending(session.Id, p => p.ToolName == tool && p.ToolInput == input);
                    break;
                case "Stop":
                case "StopFailure":
                case "UserPromptSubmit":
                    this.ReleasePending(session.Id, _ => true);
                    this._stopRequested.Remove(session.Id);
                    break;
            }

            // A requested stop rides on the next tool hook: Claude stops right after it
            if (evt is "PreToolUse" or "PostToolUse" or "PostToolUseFailure" && this._stopRequested.Remove(session.Id))
            {
                stopReply = new JsonObject { ["continue"] = false, ["stopReason"] = $"Stopped {KeypadNote}" }.ToJsonString();
                this._haltedByKeypad.Add(session.Id);
                session.MainTurnRunning = false;
            }
            halted = this._haltedByKeypad.Contains(session.Id);

            if (evt != "PermissionRequest")
            {
                return null;
            }

            var pending = new PendingPermission
            {
                ToolName = GetString(payload, "tool_name") ?? String.Empty,
                ToolInput = payload.TryGetProperty("tool_input", out var ti) ? ti.GetRawText() : String.Empty,
                Suggestions = payload.TryGetProperty("permission_suggestions", out var s) && s.ValueKind == JsonValueKind.Array
                    ? JsonNode.Parse(s.GetRawText())
                    : null
            };
            if (!this._pending.TryGetValue(session.Id, out var list))
            {
                list = new List<PendingPermission>();
                this._pending[session.Id] = list;
            }
            list.Add(pending);
            return pending;
        }

        // After the event's own state change: a session with a held request still waits for the user
        private void KeepWaitingState(ClaudeSession session)
        {
            if (this._pending.TryGetValue(session.Id, out var list) && list.Count > 0)
            {
                session.State = AgentSessionState.Permission;
                session.PendingTool = list[0].ToolName;
            }
        }

        private void ReleasePending(String sessionId, Func<PendingPermission, Boolean> match, String reply = null)
        {
            if (!this._pending.TryGetValue(sessionId, out var list))
            {
                return;
            }
            foreach (var p in list.Where(match).ToList())
            {
                p.Reply.TrySetResult(reply);
                list.Remove(p);
            }
            if (list.Count == 0)
            {
                this._pending.Remove(sessionId);
            }
        }

        // Waits for the keypad answer (or until the request is released or times out)
        private async Task<String> WaitForAnswerAsync(String sessionId, PendingPermission pending)
        {
            try
            {
                return await pending.Reply.Task.WaitAsync(PermissionHoldTime);
            }
            catch (TimeoutException)
            {
                lock (this._lock)
                {
                    this.ReleasePending(sessionId, p => ReferenceEquals(p, pending));
                }
                return null;
            }
        }

        // ---------------------------------------------------------------- Keypad side

        public Boolean HasPendingRequest(ClaudeSession session)
        {
            lock (this._lock)
            {
                return session != null && this._pending.TryGetValue(session.Id, out var list) && list.Count > 0;
            }
        }

        // Session the answer keys act on: the given one, else the last opened slot, else the one waiting longest
        public ClaudeSession GetPendingSession(ClaudeSession preferred = null)
        {
            if (preferred?.State == AgentSessionState.Permission) return preferred;
            var active = this.GetActiveSession();
            if (active?.State == AgentSessionState.Permission) return active;
            return this.GetWaitingSessions().FirstOrDefault(s => s.State == AgentSessionState.Permission);
        }

        // Answers the session's oldest held request. Without a held request (e.g. it timed out) the session is
        // opened in Claude, where the dialog still waits.
        public void AnswerPermission(ClaudeSession session, PermissionDecision decision)
        {
            if (session == null)
            {
                return;
            }

            Boolean answered;
            lock (this._lock)
            {
                answered = this._pending.TryGetValue(session.Id, out var list) && list.Count > 0;
                if (answered)
                {
                    var pending = list[0];
                    this.ReleasePending(session.Id, p => ReferenceEquals(p, pending), BuildDecision(pending, decision));

                    var now = DateTime.UtcNow;
                    // A background subagent asked: after the answer it keeps working in the background
                    session.State = decision == PermissionDecision.Interrupt ? AgentSessionState.Stopped
                        : session.MainTurnRunning ? AgentSessionState.Thinking : AgentSessionState.Background;
                    if (decision == PermissionDecision.Interrupt)
                    {
                        session.MainTurnRunning = false;
                    }
                    session.StatusSince = now;
                    session.LastUpdated = now;
                    session.PendingTool = String.Empty;
                    this.KeepWaitingState(session);
                }
            }

            Log($"[{decision}] {session.DisplayName}: {(answered ? "answered" : "no held request, opening the session")}");
            if (!answered)
            {
                ClaudeAppLauncher.OpenSession(session);
            }
            this.Recompute(force: true);
        }

        private static String BuildDecision(PendingPermission pending, PermissionDecision decision)
        {
            var result = new JsonObject();
            switch (decision)
            {
                case PermissionDecision.Deny:
                    result["behavior"] = "deny";
                    result["message"] = $"Denied {KeypadNote}";
                    break;
                case PermissionDecision.Interrupt:
                    result["behavior"] = "deny";
                    result["message"] = $"Stopped {KeypadNote}";
                    result["interrupt"] = true;
                    break;
                case PermissionDecision.AllowSession:
                    result["behavior"] = "allow";
                    result["updatedPermissions"] = PermissionUpdates(pending, "session");
                    break;
                case PermissionDecision.AlwaysAllow:
                    result["behavior"] = "allow";
                    result["updatedPermissions"] = PermissionUpdates(pending, null);
                    break;
                default:
                    result["behavior"] = "allow";
                    break;
            }

            return new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PermissionRequest",
                    ["decision"] = result
                }
            }.ToJsonString();
        }

        // The rules Claude itself suggests for "don't ask again" (e.g. Bash "npm run lint:*"), kept where Claude
        // suggests them (Always) or only for this session. Without a suggestion only this session allows the tool:
        // a permanent rule for a whole tool (all of Bash) would be far too broad.
        private static JsonArray PermissionUpdates(PendingPermission pending, String destination)
        {
            var updates = new JsonArray();
            if (pending.Suggestions is JsonArray suggestions && suggestions.Count > 0)
            {
                foreach (var suggestion in suggestions)
                {
                    if (suggestion?.DeepClone() is JsonObject update)
                    {
                        if (destination != null)
                        {
                            update["destination"] = destination;
                        }
                        updates.Add(update);
                    }
                }
                return updates;
            }

            updates.Add(new JsonObject
            {
                ["type"] = "addRules",
                ["rules"] = new JsonArray(new JsonObject { ["toolName"] = pending.ToolName }),
                ["behavior"] = "allow",
                ["destination"] = "session"
            });
            return updates;
        }

        // ---------------------------------------------------------------- Stop

        // Stop key: a waiting request is denied with "interrupt"; a running session stops at its next tool call.
        // Pure text generation calls no tool, so a desktop session additionally gets an Esc if it is still running.
        public void Stop(ClaudeSession session)
        {
            if (session == null)
            {
                return;
            }

            if (this.HasPendingRequest(session))
            {
                this.AnswerPermission(session, PermissionDecision.Interrupt);
                return;
            }

            if (session.State != AgentSessionState.Thinking)
            {
                return;
            }

            String id;
            lock (this._lock)
            {
                id = session.Id;
                this._stopRequested.Add(id);
            }
            Log($"[STOP] {session.DisplayName}: stop requested");

            if (session.IsDesktop)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(EscapeFallbackDelay);
                    Boolean stillRunning;
                    lock (this._lock)
                    {
                        stillRunning = this._stopRequested.Contains(id) && session.State == AgentSessionState.Thinking;
                    }
                    if (stillRunning)
                    {
                        Log($"[STOP] {session.DisplayName}: no tool call, sending Esc to the app");
                        ClaudeAppLauncher.SendEscape(session);
                    }
                });
            }
        }

        // The session the global Stop key acts on: the last opened slot if it runs, else the most recently active one
        public ClaudeSession GetInterruptibleSession()
        {
            static Boolean Interruptible(ClaudeSession s) => s.State is AgentSessionState.Thinking or AgentSessionState.Permission;
            var active = this.GetActiveSession();
            return active != null && Interruptible(active)
                ? active
                : this.Sessions.Where(Interruptible).OrderByDescending(s => s.LastUpdated).FirstOrDefault();
        }
    }
}
