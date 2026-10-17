namespace Loupedeck.ClaudeControllerPlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Loupedeck.ClaudeControllerPlugin.Models;

    // All local Claude Code sessions (desktop app Code tab and terminal) and the slots they sit in.
    // Live status comes from the hooks; titles and the deep-link id from the desktop app's session files;
    // tokens, context and "interrupted by the user" from the transcripts.
    public partial class SessionStore : IDisposable
    {
        private static readonly Lazy<SessionStore> _instance = new(() => new SessionStore());
        public static SessionStore Instance => _instance.Value;

        // Desktop sessions active within this window fill the slots after a plugin restart
        private static readonly TimeSpan PrefillWindow = TimeSpan.FromHours(24);

        // Finished terminal sessions without any event for this long leave their slot (closing a terminal sends no SessionEnd)
        private static readonly TimeSpan StaleWindow = TimeSpan.FromHours(24);

        // Desktop sessions the hook announced but the app's session file does not name yet are shown after this long anyway
        private static readonly TimeSpan DesktopAttachGrace = TimeSpan.FromSeconds(15);

        // Transcripts are followed every second: Esc sends no hook, only the transcript tells
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan DesktopScanInterval = TimeSpan.FromSeconds(10);

        private readonly Object _lock = new();
        private readonly Dictionary<String, ClaudeSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<String, TranscriptReader> _transcripts = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<String> _dismissed = new(StringComparer.OrdinalIgnoreCase);
        private readonly DesktopSessionIndex _desktop = new();
        private readonly SlotAssignmentService _slotAssignment = new();
        private readonly Timer _tickTimer;

        private Dictionary<Int32, ClaudeSession> _sessionsBySlot = new();
        private String _heldKey;
        private String _lastSignature = "";
        private DateTime _lastDesktopScan = DateTime.MinValue;
        private Int32 _nextWaitingIndex;
        private Int32 _isTicking;

        public Int32 CurrentActiveSlot { get; set; } = 1;
        public Boolean IsSyncing { get; private set; }
        public SlotOrderStrategy SlotOrder { get; private set; } = SlotOrderStrategy.Sticky;

        public event Action OnSessionsChanged;

        // Every second, changed or not: lets tiles check that what the keypad shows is still current
        public event Action OnHeartbeat;

        public SessionStore()
        {
            this._tickTimer = new Timer(_ => this.Tick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                this.RestoreState();
                this.ScanDesktop(prefill: true);
                this.Recompute(force: true);
                this._tickTimer.Change(TickInterval, TickInterval);
            });
        }

        // ---------------------------------------------------------------- Queries

        // Sessions that sit in a slot
        public IReadOnlyList<ClaudeSession> Sessions
        {
            get
            {
                lock (this._lock)
                {
                    return this._sessionsBySlot.Values.ToList();
                }
            }
        }

        public ClaudeSession GetSessionForSlot(Int32 slotNumber)
        {
            lock (this._lock)
            {
                return this._sessionsBySlot.TryGetValue(slotNumber, out var session) ? session : null;
            }
        }

        public ClaudeSession GetActiveSession() => this.GetSessionForSlot(this.CurrentActiveSlot) ?? this.GetSessionForSlot(1);

        // Sessions waiting for the user (permission or a choice), the one waiting longest first
        public List<ClaudeSession> GetWaitingSessions() =>
            this.Sessions.Where(s => s.IsWaitingForUser).OrderBy(s => s.StatusSince).ToList();

        // ---------------------------------------------------------------- Hook events

        // Returns the hook's stdout (null = no decision). A PermissionRequest waits here for the keypad answer.
        public async Task<String> HandleHookAsync(String evt, JsonElement payload, HookHints hints)
        {
            var id = GetString(payload, "session_id");
            if (String.IsNullOrEmpty(id))
            {
                return null;
            }

            var now = DateTime.UtcNow;
            PendingPermission pending = null;
            String reply = null;
            if (evt is "SessionStart" or "SessionEnd")
            {
                Log($"[HOOK] {evt} {id} ({GetString(payload, evt == "SessionStart" ? "source" : "reason")}, {hints.Entrypoint})");
            }
            else if (evt is "Stop" or "SubagentStop" && payload.TryGetProperty("background_tasks", out var tasks) &&
                     tasks.ValueKind == JsonValueKind.Array && tasks.GetArrayLength() > 0)
            {
                Log($"[HOOK] {evt} {id}: background {String.Join(", ", tasks.EnumerateArray().Select(t => $"{GetString(t, "type")}/{GetString(t, "status")}"))}");
            }
            lock (this._lock)
            {
                if (evt == "SessionEnd")
                {
                    this.EndSession(id, GetString(payload, "reason"), now);
                }
                else
                {
                    var session = this.GetOrCreate(id, now);
                    this.UpdateFromPayload(session, payload, hints);
                    this.TryAdoptPredecessor(session, readTranscript: true);
                    this._dismissed.Remove(session.SlotKey);
                    var before = session.State;
                    pending = this.TrackControl(session, evt, payload, out reply, out var halted);
                    var agentsPaused = evt == "Stop" && this.HasPausedAgents(session, now);
                    if (agentsPaused)
                    {
                        Log($"[HOOK] Stop {id}: a background subagent paused on its own background work");
                    }
                    ApplyEvent(session, evt, payload, agentsPaused);
                    this.KeepWaitingState(session);
                    if (halted)
                    {
                        session.State = AgentSessionState.Stopped;
                        session.PendingTool = String.Empty;
                    }
                    if (session.State != before)
                    {
                        session.StatusSince = now;
                        Log($"[STATE] {session.DisplayName}: {before} -> {session.State} ({evt})");
                    }
                    session.LastUpdated = now;
                }
            }

            // A new desktop session (or a restarted one under a new id) may not be in the app's files yet: look again now
            if (evt == "SessionStart" || evt == "UserPromptSubmit")
            {
                this._lastDesktopScan = DateTime.MinValue;
            }

            this.Recompute();
            return pending != null ? await this.WaitForAnswerAsync(id, pending) : reply;
        }

        private ClaudeSession GetOrCreate(String id, DateTime now)
        {
            if (!this._sessions.TryGetValue(id, out var session))
            {
                session = new ClaudeSession { Id = id, StatusSince = now, LastUpdated = now };
                this._sessions[id] = session;
                this.AttachDesktopInfo(session);
                Log($"[SESSION] New session {id} ({(session.IsDesktop ? "desktop" : "terminal")})");
            }
            return session;
        }

        private void UpdateFromPayload(ClaudeSession session, JsonElement payload, HookHints hints)
        {
            var cwd = GetString(payload, "cwd");
            if (!String.IsNullOrEmpty(cwd) && String.IsNullOrEmpty(session.Cwd))
            {
                session.Cwd = cwd;
            }

            var transcript = GetString(payload, "transcript_path");
            if (!String.IsNullOrEmpty(transcript) && transcript != session.TranscriptPath)
            {
                session.TranscriptPath = transcript;
                this._transcripts[session.Id] = new TranscriptReader(transcript);
            }

            var model = GetString(payload, "model");
            if (!String.IsNullOrEmpty(model))
            {
                session.Model = model;
            }

            if (!String.IsNullOrEmpty(hints.TermProgram))
            {
                session.TermProgram = hints.TermProgram;
            }
            if (hints.ProcessId > 1)
            {
                session.ProcessId = hints.ProcessId;
            }
            if (hints.Entrypoint == "claude-desktop")
            {
                session.DesktopHint = true;
            }
        }

        private static void ApplyEvent(ClaudeSession session, String evt, JsonElement payload, Boolean agentsPaused = false)
        {
            var state = session.State;

            // Tool hooks of a subagent carry its agent_id. Outside a turn of the main agent they come from background
            // subagents: the session keeps working in the background, not in the foreground.
            var fromSubagent = !String.IsNullOrEmpty(GetString(payload, "agent_id"));
            var working = fromSubagent && !session.MainTurnRunning ? AgentSessionState.Background : AgentSessionState.Thinking;
            if (evt == "UserPromptSubmit" || (!fromSubagent && evt is "PreToolUse" or "PostToolUse" or "PostToolUseFailure"))
            {
                session.MainTurnRunning = true;
            }
            else if (working == AgentSessionState.Background && evt is "PreToolUse" or "PostToolUse" or "PostToolUseFailure")
            {
                session.BackgroundTasksRunning = true;  // a background subagent is clearly at work
            }

            switch (evt)
            {
                case "SessionStart":
                    // The desktop app restarts Claude Code on its own (source "resume"): a finished session keeps showing
                    // how it ended. A turn that was running is gone with the old process.
                    var source = GetString(payload, "source");
                    if (source != "compact" && !(source == "resume" && session.IsFinished))
                    {
                        state = AgentSessionState.Idle;
                    }
                    break;
                case "UserPromptSubmit":
                    state = AgentSessionState.Thinking;
                    break;
                case "PreToolUse":
                    // AskUserQuestion shows options in the app: the session waits for a choice
                    var tool = GetString(payload, "tool_name");
                    state = tool == "AskUserQuestion" ? AgentSessionState.Choose : working;
                    session.PendingTool = tool == "AskUserQuestion" ? tool : String.Empty;
                    break;
                case "PermissionRequest":
                    state = AgentSessionState.Permission;
                    session.PendingTool = GetString(payload, "tool_name") ?? String.Empty;
                    break;
                case "PostToolUse":
                case "PostToolUseFailure":
                    state = working;
                    session.PendingTool = String.Empty;
                    break;
                case "Notification":
                    var type = GetString(payload, "notification_type");
                    if (type == "permission_prompt" && state is AgentSessionState.Thinking or AgentSessionState.Background)
                    {
                        state = AgentSessionState.Permission;
                    }
                    else if (type == "elicitation_dialog" && state == AgentSessionState.Thinking)
                    {
                        state = AgentSessionState.Choose;
                    }
                    break;
                case "Stop":
                    session.MainTurnRunning = false;
                    ReadBackgroundWork(session, payload);
                    state = session.HasBackgroundWork || agentsPaused ? AgentSessionState.Background : AgentSessionState.Complete;
                    session.PendingTool = String.Empty;
                    break;
                case "SubagentStop":
                    // A background subagent finished (or paused): what else still runs decides
                    if (!session.MainTurnRunning && state is AgentSessionState.Background or AgentSessionState.Complete)
                    {
                        ReadBackgroundWork(session, payload);
                        state = session.HasBackgroundWork ? AgentSessionState.Background : AgentSessionState.Complete;
                    }
                    break;
                case "StopFailure":
                    session.MainTurnRunning = false;
                    state = AgentSessionState.Error;
                    session.PendingTool = String.Empty;
                    break;
            }

            // StatusSince is set by the caller once the final state is known (a held request keeps "Permission")
            session.State = state;
        }

        // The transcript is read up to now first: the report of a paused subagent comes in right before the Stop hook
        private Boolean HasPausedAgents(ClaudeSession session, DateTime now)
        {
            if (!this._transcripts.TryGetValue(session.Id, out var reader))
            {
                return false;
            }
            reader.Update();
            return reader.HasPausedAgents(now);
        }

        // Stop and SubagentStop list the work still in flight (subagents, background shell commands, monitors, workflows)
        // that will wake the session again. Scheduled wakeups (session_crons) do not count: nothing runs until they fire.
        private static void ReadBackgroundWork(ClaudeSession session, JsonElement payload)
        {
            var running = payload.TryGetProperty("background_tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array
                ? tasks.EnumerateArray()
                    .Where(t => GetString(t, "status") is not ("completed" or "failed" or "killed" or "stopped"))
                    .Select(t => GetString(t, "type"))
                    .ToList()
                : new List<String>();
            session.BackgroundShellsRunning = running.Any(IsShellTask);
            session.BackgroundTasksRunning = running.Any(t => !IsShellTask(t));
        }

        private static Boolean IsShellTask(String type) => type is "shell" or "monitor";

        // rate_limits go to UsageLimitService; here the exact context window of terminal sessions
        public void ApplyStatusline(JsonElement payload)
        {
            var id = GetString(payload, "session_id");
            if (String.IsNullOrEmpty(id))
            {
                return;
            }

            lock (this._lock)
            {
                if (!this._sessions.TryGetValue(id, out var session))
                {
                    return;
                }
                if (payload.TryGetProperty("context_window", out var window) && window.ValueKind == JsonValueKind.Object &&
                    window.TryGetProperty("context_window_size", out var size) && size.TryGetInt64(out var max))
                {
                    session.ContextMaxTokens = max;
                }
                if (payload.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
                {
                    session.Model = GetString(model, "id") ?? session.Model;
                }
            }
            this.Recompute();
        }

        // ---------------------------------------------------------------- Background work

        private void Tick()
        {
            if (Interlocked.Exchange(ref this._isTicking, 1) == 1)
            {
                return;
            }

            try
            {
                Boolean waitingForFile;
                lock (this._lock)
                {
                    waitingForFile = this._sessions.Values.Any(s => s.DesktopHint && !s.IsDesktop);
                }
                if (waitingForFile || DateTime.UtcNow - this._lastDesktopScan >= DesktopScanInterval)
                {
                    this.ScanDesktop(prefill: false);
                }
                this.ReadTranscripts();
                lock (this._lock)
                {
                    foreach (var session in this._sessions.Values.Where(s => s.DesktopHint && !s.IsDesktop).ToList())
                    {
                        this.TryAdoptPredecessor(session, readTranscript: false);
                    }
                }
                this.DropStaleSessions();
                this.Recompute();
                this.OnHeartbeat?.Invoke();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Session update failed");
            }
            finally
            {
                Interlocked.Exchange(ref this._isTicking, 0);
            }
        }

        private void ScanDesktop(Boolean prefill)
        {
            var scanned = this._desktop.Scan();
            this._lastDesktopScan = DateTime.UtcNow;
            var now = DateTime.UtcNow;

            lock (this._lock)
            {
                if (prefill)
                {
                    foreach (var entry in this._desktop.Entries.Where(e => !e.IsArchived && now - e.LastActivityUtc <= PrefillWindow))
                    {
                        if (this._sessions.ContainsKey(entry.CliSessionId))
                        {
                            continue;
                        }
                        var session = new ClaudeSession
                        {
                            Id = entry.CliSessionId,
                            Cwd = entry.Cwd ?? String.Empty,
                            State = AgentSessionState.Idle,
                            LastUpdated = entry.LastActivityUtc,
                            StatusSince = entry.LastActivityUtc
                        };
                        this._sessions[session.Id] = session;
                        var path = TranscriptReader.GuessPath(session.Cwd, session.Id);
                        if (path != null)
                        {
                            session.TranscriptPath = path;
                            this._transcripts[session.Id] = new TranscriptReader(path);
                        }
                    }
                }

                foreach (var session in this._sessions.Values.ToList())
                {
                    this.AttachDesktopInfo(session);
                    if (!scanned || !session.IsDesktop)
                    {
                        continue;
                    }

                    // Archived or deleted in the app: the slot frees up
                    var entry = this._desktop.FindByLocalId(session.LocalId);
                    if (entry == null || entry.IsArchived)
                    {
                        this.Forget(session);
                        Log($"[SESSION] {session.DisplayName} archived or deleted in the app");
                        continue;
                    }

                    // The app restarted Claude Code for this session under a new session id
                    // (its file may still name the old one for a while after the transcript told us)
                    if (!String.Equals(entry.CliSessionId, session.Id, StringComparison.OrdinalIgnoreCase) &&
                        !String.Equals(entry.CliSessionId, session.PredecessorId, StringComparison.OrdinalIgnoreCase))
                    {
                        this.FollowNewId(session, entry.CliSessionId);
                    }
                }
            }
        }

        private void AttachDesktopInfo(ClaudeSession session)
        {
            var entry = this._desktop.Find(session.Id);
            if (entry == null)
            {
                return;
            }

            session.LocalId = entry.LocalId ?? String.Empty;
            if (!String.IsNullOrWhiteSpace(entry.Title))
            {
                session.Title = entry.Title;
            }
            if (String.IsNullOrEmpty(session.Cwd) && !String.IsNullOrEmpty(entry.Cwd))
            {
                session.Cwd = entry.Cwd;
            }
            if (String.IsNullOrEmpty(session.Model) && !String.IsNullOrEmpty(entry.Model))
            {
                session.Model = entry.Model;
            }
        }

        private void ReadTranscripts()
        {
            List<(ClaudeSession Session, TranscriptReader Reader)> work;
            lock (this._lock)
            {
                work = this._sessions.Values
                    .Where(s => this._transcripts.ContainsKey(s.Id))
                    .Select(s => (s, this._transcripts[s.Id]))
                    .ToList();
            }

            var now = DateTime.UtcNow;
            var shellParents = this.ReadShellParentsIfNeeded(work.Select(w => w.Session), now);
            foreach (var (session, reader) in work)
            {
                reader.Update();
                lock (this._lock)
                {
                    session.InputTokens = reader.InputTokens;
                    session.OutputTokens = reader.OutputTokens;
                    session.CacheReadTokens = reader.CacheReadTokens;
                    session.CacheCreationTokens = reader.CacheCreationTokens;
                    session.ContextUsedTokens = reader.ContextUsedTokens;
                    if (!String.IsNullOrEmpty(reader.Model))
                    {
                        session.Model = reader.Model;
                    }
                    if (!session.IsDesktop && !String.IsNullOrWhiteSpace(reader.CustomTitle))
                    {
                        session.Title = reader.CustomTitle;
                    }

                    if (MarkIfInterrupted(session, reader, now))
                    {
                        this.ReleasePending(session.Id, _ => true);
                        this._stopRequested.Remove(session.Id);
                    }
                    UpdateBackgroundState(session, reader, shellParents, now);
                }
            }
        }

        // Esc (or rejecting a tool) ends the turn without a Stop hook; the transcript says so.
        // Only an interrupt after the current turn started counts (an old one stays in the transcript until the next prompt).
        private static Boolean MarkIfInterrupted(ClaudeSession session, TranscriptReader reader, DateTime now)
        {
            var running = session.State is AgentSessionState.Thinking or AgentSessionState.Permission or AgentSessionState.Choose;
            if (!running || reader?.InterruptedAtUtc is not DateTime at || at < session.StatusSince.AddSeconds(-1))
            {
                return false;
            }

            Log($"[STATE] {session.DisplayName}: {session.State} -> Stopped (interrupted)");
            session.State = AgentSessionState.Stopped;
            session.StatusSince = now;
            session.PendingTool = String.Empty;
            session.MainTurnRunning = false;
            return true;
        }

        // SessionEnd: terminal sessions leave their slot. The desktop app ends Claude Code after Esc or when a session
        // is idle and starts it again (under a new id) for the next prompt, so its sessions stay.
        private void EndSession(String id, String reason, DateTime now)
        {
            if (!this._sessions.TryGetValue(id, out var session))
            {
                return;
            }

            // The process is gone: nobody waits for an answer anymore
            this.ReleasePending(id, _ => true);
            this._stopRequested.Remove(id);

            if (session.IsDesktop || session.DesktopHint)
            {
                this._transcripts.TryGetValue(id, out var reader);
                reader?.Update();
                if (!MarkIfInterrupted(session, reader, now) &&
                    session.State is AgentSessionState.Thinking or AgentSessionState.Permission or AgentSessionState.Choose
                        or AgentSessionState.Background)
                {
                    session.MainTurnRunning = false;
                    session.State = AgentSessionState.Idle;
                    session.StatusSince = now;
                    session.PendingTool = String.Empty;
                }
                Log($"[SESSION] {session.DisplayName}: Claude Code process ended ({reason}), session stays");
                return;
            }

            this.Forget(session);
            Log($"[SESSION] {session.DisplayName} ended ({reason})");
        }

        private void Forget(ClaudeSession session)
        {
            this.ReleasePending(session.Id, _ => true);
            this._stopRequested.Remove(session.Id);
            this._haltedByKeypad.Remove(session.Id);
            this._sessions.Remove(session.Id);
            this._transcripts.Remove(session.Id);
        }

        // Moves a desktop session to the session id the app now uses for it. If hooks already created a session
        // under the new id, that one takes over (keeping the old state if it has none of its own yet).
        private void FollowNewId(ClaudeSession session, String newId)
        {
            if (String.IsNullOrEmpty(newId))
            {
                return;
            }

            if (this._sessions.TryGetValue(newId, out var successor) && !ReferenceEquals(successor, session))
            {
                if (successor.State == AgentSessionState.Idle && session.State != AgentSessionState.Idle)
                {
                    successor.State = session.State;
                    successor.StatusSince = session.StatusSince;
                }
                this.Forget(session);
                Log($"[SESSION] {session.DisplayName} continues as {newId}");
                return;
            }

            this.Forget(session);
            session.Id = newId;
            session.TranscriptPath = TranscriptReader.GuessPath(session.Cwd, newId) ?? String.Empty;
            this._sessions[newId] = session;
            if (!String.IsNullOrEmpty(session.TranscriptPath))
            {
                this._transcripts[newId] = new TranscriptReader(session.TranscriptPath);
            }
            Log($"[SESSION] {session.DisplayName} continues as {newId}");
        }

        // A desktop session restarted under a new id: its copied transcript names the session it continues, so it takes
        // over that slot at once instead of waiting (seconds) until the app's session file names the new id
        private void TryAdoptPredecessor(ClaudeSession session, Boolean readTranscript)
        {
            if (!session.DesktopHint || session.IsDesktop || !this._transcripts.TryGetValue(session.Id, out var reader))
            {
                return;
            }
            if (readTranscript)
            {
                reader.Update();
            }
            var oldId = reader.PredecessorId;
            if (String.IsNullOrEmpty(oldId) || !this._sessions.TryGetValue(oldId, out var old) || !old.IsDesktop)
            {
                return;
            }

            session.LocalId = old.LocalId;
            session.PredecessorId = oldId;
            session.Title = String.IsNullOrWhiteSpace(session.Title) ? old.Title : session.Title;
            session.Cwd = String.IsNullOrEmpty(session.Cwd) ? old.Cwd : session.Cwd;
            session.Model = String.IsNullOrEmpty(session.Model) ? old.Model : session.Model;
            session.ContextMaxTokens = session.ContextMaxTokens > 0 ? session.ContextMaxTokens : old.ContextMaxTokens;
            if (session.State == AgentSessionState.Idle && old.State != AgentSessionState.Idle)
            {
                // Started but no prompt yet: keep showing how the session ended (e.g. Stopped)
                session.State = old.State;
                session.StatusSince = old.StatusSince;
            }
            this.Forget(old);
            Log($"[SESSION] {session.DisplayName} continues as {session.Id} (from transcript)");
        }

        // Terminal sessions whose Claude Code process is gone (terminal closed), or finished and quiet for a day.
        // Desktop sessions stay: the app stops idle processes on its own, they leave when archived or deleted.
        private void DropStaleSessions()
        {
            var now = DateTime.UtcNow;
            lock (this._lock)
            {
                foreach (var session in this._sessions.Values.Where(s => !s.IsDesktop && !s.DesktopHint).ToList())
                {
                    var gone = session.ProcessId > 1 && !IsProcessAlive(session.ProcessId);
                    if (gone || (session.IsFinished && now - session.LastUpdated > StaleWindow))
                    {
                        this.Forget(session);
                        Log($"[SESSION] {session.DisplayName} left ({(gone ? "process ended" : "inactive")})");
                    }
                }
            }
        }

        private static Boolean IsProcessAlive(Int32 pid)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- Slots

        private void Recompute(Boolean force = false)
        {
            String signature;
            lock (this._lock)
            {
                // Newest first, so on a fresh start the most recent sessions get the first slots
                var now = DateTime.UtcNow;
                var visible = this._sessions.Values
                    .Where(s => !this._dismissed.Contains(s.SlotKey))
                    .Where(s => !(s.DesktopHint && !s.IsDesktop && now - s.StatusSince < DesktopAttachGrace))
                    .OrderByDescending(s => s.LastUpdated)
                    .ToList();
                var byKey = visible.GroupBy(s => s.SlotKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                this._slotAssignment.Sync(visible, this.SlotOrder, this._heldKey);

                var bySlot = new Dictionary<Int32, ClaudeSession>();
                for (var slot = 1; slot <= SlotAssignmentService.MaxSlots; slot++)
                {
                    var key = this._slotAssignment.GetKey(slot);
                    if (key != null && byKey.TryGetValue(key, out var session))
                    {
                        bySlot[slot] = session;
                    }
                }
                this._sessionsBySlot = bySlot;

                var sb = new StringBuilder();
                foreach (var (slot, s) in bySlot.OrderBy(kv => kv.Key))
                {
                    sb.Append(slot).Append(':').Append(s.Id).Append(':').Append(s.State).Append(':').Append(s.DisplayName)
                      .Append(':').Append(s.ContextUsedTokens).Append(':').Append(s.OutputTokens).Append(':').Append(s.ContextMaxTokens).Append('|');
                }
                signature = sb.ToString();
                if (!force && signature == this._lastSignature)
                {
                    return;
                }
                this._lastSignature = signature;
            }
            this.OnSessionsChanged?.Invoke();
        }

        public void SetSlotOrder(SlotOrderStrategy strategy)
        {
            lock (this._lock)
            {
                if (this.SlotOrder == strategy) return;
                this.SlotOrder = strategy;
                this._slotAssignment.RequestResort();
            }
            Log($"[SLOTS] Slot order set to {strategy}");
            this.Recompute(force: true);
        }

        // While a slot folder is open its session stays in that slot (same as the Claude Desktop plugin)
        public void HoldSlot(Int32 slot)
        {
            lock (this._lock)
            {
                this._heldKey = this._slotAssignment.GetKey(slot);
            }
        }

        public void ReleaseSlot(Int32 slot)
        {
            lock (this._lock)
            {
                var heldSlot = this._slotAssignment.GetSlot(this._heldKey);
                if (heldSlot == null || heldSlot == slot)
                {
                    this._heldKey = null;
                }
            }
        }

        // ---------------------------------------------------------------- Actions

        public void TriggerManualSync()
        {
            this.IsSyncing = true;
            this.OnSessionsChanged?.Invoke();
            UsageLimitService.Instance.RefreshNow();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    this.ScanDesktop(prefill: false);
                    this.ReadTranscripts();
                    Thread.Sleep(300); // keep "Sync..." visible for a moment
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, "Manual sync failed");
                }
                finally
                {
                    this.IsSyncing = false;
                    this.Recompute(force: true);
                }
            });
        }

        // Frees the slots of sessions that are done (Complete, Idle, Stopped, Error). Nothing changes in the app;
        // a session comes back as soon as it does something again.
        public void ClearFinished()
        {
            Int32 count;
            lock (this._lock)
            {
                var finished = this._sessionsBySlot.Values.Where(s => s.IsFinished).Select(s => s.SlotKey).ToList();
                foreach (var id in finished)
                {
                    this._dismissed.Add(id);
                }
                count = finished.Count;
            }
            Log($"[SLOTS] Cleared {count} finished sessions");
            this.Recompute(force: true);
        }

        public Int32 FinishedCount => this.Sessions.Count(s => s.IsFinished);

        // Next session waiting for the user (cycles on repeated presses): opened in the app
        // and made the "last opened slot" so Context/Tokens keys follow it
        public void ShowNextWaitingSession()
        {
            var waiting = this.GetWaitingSessions();
            if (waiting.Count == 0)
            {
                return;
            }

            var session = waiting[this._nextWaitingIndex++ % waiting.Count];
            Int32? slot;
            lock (this._lock)
            {
                slot = this._slotAssignment.GetSlot(session.SlotKey);
            }
            if (slot.HasValue)
            {
                this.CurrentActiveSlot = slot.Value;
            }
            ClaudeAppLauncher.OpenSession(session);
            this.OnSessionsChanged?.Invoke();
        }

        public void OpenSession(ClaudeSession session) => ClaudeAppLauncher.OpenSession(session);

        private static String GetString(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public void Dispose()
        {
            this._tickTimer?.Dispose();
            this.SaveState();
        }
    }
}
