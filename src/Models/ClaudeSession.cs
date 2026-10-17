namespace Loupedeck.ClaudeControllerPlugin.Models
{
    using System;
    using System.IO;

    public enum AgentSessionState
    {
        Idle,
        Thinking,
        Complete,
        Error,
        Permission,
        Stopped,    // Interrupted by the user
        Choose,     // Claude asks a question with options (AskUserQuestion)
        Background  // The turn ended, but subagents or background commands still run and will wake the session
    }

    // How sessions claim slots (same model as the Claude Desktop plugin)
    public enum SlotOrderStrategy
    {
        Sticky,   // A session keeps the slot it first appeared in.
        Recency,  // The session that did something most recently comes first.
        Settled   // The session whose status changed most recently comes first.
    }

    // Answers to a pending permission request
    public enum PermissionDecision
    {
        AllowOnce,
        AllowSession,   // Allow and don't ask again in this session (like "Allow in Conversation" in Antigravity)
        AlwaysAllow,    // Allow and keep the rule Claude suggests ("Yes, and don't ask again")
        Deny,
        Interrupt       // Deny and stop the turn
    }

    // One local Claude Code session (desktop app Code tab or terminal)
    public class ClaudeSession
    {
        // Claude Code session id (hook "session_id", desktop file "cliSessionId")
        public String Id { get; set; } = String.Empty;

        // Desktop app session id ("local_…"); empty for terminal sessions
        public String LocalId { get; set; } = String.Empty;

        public String Title { get; set; } = String.Empty;
        public String Cwd { get; set; } = String.Empty;
        public String TranscriptPath { get; set; } = String.Empty;
        public String Model { get; set; } = String.Empty;

        // Terminal app the session runs in ($TERM_PROGRAM, e.g. "Apple_Terminal", "iTerm.app"); empty in the desktop app
        public String TermProgram { get; set; } = String.Empty;

        // Claude Code process that runs the session (0 = unknown)
        public Int32 ProcessId { get; set; }

        public AgentSessionState State { get; set; } = AgentSessionState.Idle;
        public DateTime LastUpdated { get; set; } = DateTime.MinValue;
        public DateTime StatusSince { get; set; } = DateTime.MinValue;

        // Tool waiting for permission (PermissionRequest) or the question being asked
        public String PendingTool { get; set; } = String.Empty;

        public Int64 InputTokens { get; set; }
        public Int64 OutputTokens { get; set; }
        public Int64 CacheReadTokens { get; set; }
        public Int64 CacheCreationTokens { get; set; }

        // Context of the last request (input + cache read + cache creation) and the model's window (0 = unknown)
        public Int64 ContextUsedTokens { get; set; }
        public Int64 ContextMaxTokens { get; set; }

        public Boolean IsDesktop => !String.IsNullOrEmpty(this.LocalId);

        public String DisplayName => !String.IsNullOrWhiteSpace(this.Title)
            ? this.Title
            : !String.IsNullOrWhiteSpace(this.Cwd) ? Path.GetFileName(this.Cwd.TrimEnd('/')) : "Session";

        public Int64 TotalContextTokens => this.ContextUsedTokens;

        // Share of the context window, only where Claude Code reports the window size (terminal statusline).
        // The desktop app keeps it to itself, and guessing 200k vs. 1M would show wrong numbers.
        public Double? ContextUsagePercent => this.ContextMaxTokens > 0
            ? Math.Clamp((Double)this.TotalContextTokens / this.ContextMaxTokens * 100.0, 0.0, 100.0)
            : null;

        // Reported by the hook (CLAUDE_CODE_ENTRYPOINT=claude-desktop) before the app's session file names it
        public Boolean DesktopHint { get; set; }

        // A turn of the main agent is running (prompt sent, no Stop yet). Hooks of background subagents arrive
        // outside such a turn and must not make the session look busy in the foreground.
        public Boolean MainTurnRunning { get; set; }

        // The last Stop / SubagentStop listed work still in flight: subagents and workflows ...
        public Boolean BackgroundTasksRunning { get; set; }

        // ... and background commands or monitors. Claude Code keeps listing some of these as running after they are
        // gone, so they only count while the session's process still has a shell running.
        public Boolean BackgroundShellsRunning { get; set; }

        public Boolean HasBackgroundWork => this.BackgroundTasksRunning || this.BackgroundShellsRunning;

        // Session id the desktop app used before it restarted Claude Code under this one (its file may still name it)
        public String PredecessorId { get; set; }

        // One slot per session. The desktop app restarts Claude Code under a new session id (after Esc, or when idle),
        // so its sessions are keyed by the app's own id.
        public String SlotKey => this.IsDesktop ? this.LocalId : this.Id;

        public String StatusPillText => this.State switch
        {
            AgentSessionState.Thinking => "Thinking",
            AgentSessionState.Complete => "Complete",
            AgentSessionState.Permission => "Permission",
            AgentSessionState.Error => "Error",
            AgentSessionState.Stopped => "Stopped",
            AgentSessionState.Choose => "Choose",
            AgentSessionState.Background => "Background",
            _ => "Idle"
        };

        // Waits for the user: a permission request or a question to choose an answer for
        public Boolean IsWaitingForUser => this.State == AgentSessionState.Permission || this.State == AgentSessionState.Choose;

        // Done for now: what "Clear Finished" removes from the slots
        public Boolean IsFinished => this.State is AgentSessionState.Complete or AgentSessionState.Idle
            or AgentSessionState.Stopped or AgentSessionState.Error;

        public ClaudeSession Clone() => (ClaudeSession)this.MemberwiseClone();
    }
}
