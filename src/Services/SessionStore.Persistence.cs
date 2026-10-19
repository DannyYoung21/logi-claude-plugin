namespace Loupedeck.ClaudeControllerPlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using Loupedeck.ClaudeControllerPlugin.Models;

    // Keeps sessions, their status and the slot layout across plugin restarts (Logi reloads the plugin on updates
    // and when the service restarts). Without it every session would show "Idle" until its next hook, which can
    // take a while when a session is busy with a long tool call or subagents.
    public partial class SessionStore
    {
        private static readonly String StatePath = Path.Combine(HookInstaller.DataDir, "session-state.json");

        // Older saves are ignored: the sessions moved on in the meantime
        private static readonly TimeSpan StateMaxAge = TimeSpan.FromMinutes(30);

        // A running status (Thinking, Permission, Choose) is only trusted after a short restart; after a longer gap
        // the turn may have ended without us hearing the Stop hook
        private static readonly TimeSpan RunningStateMaxAge = TimeSpan.FromMinutes(2);

        private sealed class SavedState
        {
            public DateTime SavedAtUtc { get; set; }
            public List<ClaudeSession> Sessions { get; set; } = new();
            public List<String> Dismissed { get; set; } = new();
            public Dictionary<Int32, String> Slots { get; set; } = new();
        }

        private void SaveState()
        {
            try
            {
                SavedState state;
                lock (this._lock)
                {
                    state = new SavedState
                    {
                        SavedAtUtc = DateTime.UtcNow,
                        Sessions = this._sessions.Values.Select(s => s.Clone()).ToList(),
                        Dismissed = this._dismissed.ToList(),
                        Slots = this._slotAssignment.Snapshot()
                    };
                }
                Directory.CreateDirectory(HookInstaller.DataDir);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not save the session state");
            }
        }

        private void RestoreState()
        {
            SavedState state;
            try
            {
                if (!File.Exists(StatePath))
                {
                    return;
                }
                state = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StatePath));
                File.Delete(StatePath);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not read the saved session state");
                return;
            }

            var age = DateTime.UtcNow - (state?.SavedAtUtc ?? DateTime.MinValue);
            if (state == null || age > StateMaxAge)
            {
                return;
            }

            lock (this._lock)
            {
                foreach (var session in state.Sessions.Where(s => !String.IsNullOrEmpty(s.Id)))
                {
                    // Terminal closed while the plugin was away
                    if (!session.IsDesktop && session.ProcessId > 1 && !IsProcessAlive(session.ProcessId))
                    {
                        continue;
                    }
                    if (session.State is AgentSessionState.Thinking or AgentSessionState.Permission or AgentSessionState.Choose
                            or AgentSessionState.Background && age > RunningStateMaxAge && !session.AwaitsReply)
                    {
                        session.MainTurnRunning = false;
                        session.State = AgentSessionState.Idle;
                        session.PendingTool = String.Empty;
                    }

                    this._sessions[session.Id] = session;
                    if (!String.IsNullOrEmpty(session.TranscriptPath))
                    {
                        this._transcripts[session.Id] = new TranscriptReader(session.TranscriptPath);
                    }
                }
                foreach (var key in state.Dismissed)
                {
                    this._dismissed.Add(key);
                }
                this._slotAssignment.Restore(state.Slots ?? new Dictionary<Int32, String>());
            }
            Log($"[SESSION] Restored {state.Sessions.Count} sessions saved {age.TotalSeconds:0} s ago");
        }
    }
}
