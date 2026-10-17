namespace Loupedeck.ClaudeControllerPlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using Loupedeck.ClaudeControllerPlugin.Models;

    // When does a session that ended its turn stop being "Background"? Claude Code wakes the session with a
    // task-notification when background work ends, but not always: a monitor can stay listed as running after it is
    // gone, and a paused subagent may never report again. So between hooks the plugin checks what really still runs.
    public partial class SessionStore
    {
        // Background commands and monitors run in shells Claude Code starts from its shell snapshot
        private const String ShellSnapshotMarker = "/.claude/shell-snapshots/";

        private static readonly TimeSpan ShellCheckInterval = TimeSpan.FromSeconds(5);

        // Subagents at work send hooks; one that is still listed but silent this long is gone (killed, app closed)
        private static readonly TimeSpan BackgroundAgentMaxSilence = TimeSpan.FromHours(1);

        private HashSet<Int32> _shellParents;
        private DateTime _shellParentsReadUtc = DateTime.MinValue;

        // Pids of processes that run such a shell, read with ps at most every few seconds and only while a session needs
        // it. Null if ps failed: the listed background work then counts as running.
        private HashSet<Int32> ReadShellParentsIfNeeded(IEnumerable<ClaudeSession> sessions, DateTime now)
        {
            var needed = sessions.Any(s => s.State == AgentSessionState.Background && s.BackgroundShellsRunning && s.ProcessId > 1);
            if (needed && now - this._shellParentsReadUtc >= ShellCheckInterval)
            {
                this._shellParents = ReadShellParents();
                this._shellParentsReadUtc = now;
            }
            return needed ? this._shellParents : null;
        }

        private static HashSet<Int32> ReadShellParents()
        {
            try
            {
                var info = new ProcessStartInfo("/bin/ps", "-axo ppid=,command=")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var ps = Process.Start(info);
                var output = ps.StandardOutput.ReadToEnd();
                ps.WaitForExit(2000);

                var parents = new HashSet<Int32>();
                foreach (var line in output.Split('\n'))
                {
                    var text = line.TrimStart();
                    var space = text.IndexOf(' ');
                    if (space > 0 && text.Contains(ShellSnapshotMarker, StringComparison.Ordinal) &&
                        Int32.TryParse(text[..space], out var ppid))
                    {
                        parents.Add(ppid);
                    }
                }
                return parents;
            }
            catch (Exception ex)
            {
                PluginLog.Verbose(ex, "Could not list processes");
                return null;
            }
        }

        // Between hooks: a subagent that paused on its own background work keeps the session in Background (or brings it
        // back there). Once nothing counts as running anymore, the session is done.
        private static void UpdateBackgroundState(ClaudeSession session, TranscriptReader reader, HashSet<Int32> shellParents, DateTime now)
        {
            if (session.MainTurnRunning)
            {
                return;
            }

            if (session.State == AgentSessionState.Background)
            {
                if (session.BackgroundShellsRunning && shellParents != null && session.ProcessId > 1 &&
                    !shellParents.Contains(session.ProcessId))
                {
                    session.BackgroundShellsRunning = false;
                    Log($"[STATE] {session.DisplayName}: no background command or monitor runs anymore");
                }
                if (session.BackgroundTasksRunning && now - session.LastUpdated > BackgroundAgentMaxSilence)
                {
                    session.BackgroundTasksRunning = false;
                    Log($"[STATE] {session.DisplayName}: background subagents silent for {BackgroundAgentMaxSilence.TotalMinutes:0} min");
                }
            }

            var paused = reader.HasPausedAgents(now);
            var next = session.State switch
            {
                AgentSessionState.Complete when paused => AgentSessionState.Background,
                AgentSessionState.Background when !paused && !session.HasBackgroundWork => AgentSessionState.Complete,
                _ => session.State
            };
            if (next != session.State)
            {
                Log($"[STATE] {session.DisplayName}: {session.State} -> {next} (background work)");
                session.State = next;
                session.StatusSince = now;
            }
        }
    }
}
