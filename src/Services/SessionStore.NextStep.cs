namespace Loupedeck.ClaudeControllerPlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Loupedeck.ClaudeControllerPlugin.Models;

    // The desktop app's next-step box can be closed with its X. Nothing reaches Claude Code then, so no hook says so: the
    // session would show "Choose" until the next prompt. The app only notes it in its local storage, as
    // epitaxy.dismissedTurnReceipts = {"local_…":["toolu_…"]}, with the id of the wrap-up tool call that showed the box.
    // The app writes that storage to disk only about once a minute, so the tile can follow the X by up to that long.
    public partial class SessionStore
    {
        private static readonly String AppStorageDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Claude", "Local Storage", "leveldb");

        // The storage files are compressed: a repeated start like "toolu_01" may be stored as a back reference,
        // the random end of the id is stored as it is
        private const Int32 ToolIdTailLength = 16;

        private String _dismissedCheckedState;

        private static Boolean WaitsOnBox(ClaudeSession session) => session.State == AgentSessionState.Choose && session.AwaitsReply;

        // Ids of the boxes the user closed, among those sessions wait on. Read only while a session waits on a box, and
        // only when the storage files changed. Null if there is nothing new.
        private HashSet<String> ReadDismissedBoxes(IEnumerable<(ClaudeSession Session, TranscriptReader Reader)> work)
        {
            var ids = work.Where(w => WaitsOnBox(w.Session))
                .Select(w => w.Reader.LastWrapUpToolUseId)
                .Where(id => !String.IsNullOrEmpty(id))
                .Distinct()
                .ToList();
            if (ids.Count == 0)
            {
                return null;
            }

            try
            {
                var files = new DirectoryInfo(AppStorageDir).EnumerateFiles()
                    .Where(f => f.Extension is ".log" or ".ldb")
                    .ToList();
                var state = String.Join("|", ids.Concat(files.Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}")));
                if (state == this._dismissedCheckedState)
                {
                    return null;
                }
                this._dismissedCheckedState = state;

                var dismissed = new HashSet<String>();
                foreach (var file in files)
                {
                    var data = ReadAppStorageFile(file.FullName);
                    dismissed.UnionWith(ids.Where(id => data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(id[^Math.Min(ToolIdTailLength, id.Length)..])) >= 0));
                }
                return dismissed;
            }
            catch (Exception ex)
            {
                // Files come and go while the app compacts its storage: the next check sees the new ones
                this._dismissedCheckedState = null;
                PluginLog.Verbose(ex, "Could not read the app's local storage");
                return null;
            }
        }

        private static Byte[] ReadAppStorageFile(String path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var data = new Byte[stream.Length];
            var read = stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
            return read == data.Length ? data : data[..read];
        }

        // The user closed the box: nothing waits for an answer anymore, the turn simply ended
        private static void EndDismissedWait(ClaudeSession session, TranscriptReader reader, HashSet<String> dismissed, DateTime now)
        {
            if (dismissed == null || !WaitsOnBox(session) || reader.LastWrapUpToolUseId == null || !dismissed.Contains(reader.LastWrapUpToolUseId))
            {
                return;
            }

            var before = session.State;
            EndBoxWait(session, reader.HasPausedAgents(now));
            Log($"[STATE] {session.DisplayName}: {before} -> {session.State} (next-step box closed)");
            session.StatusSince = now;
        }

        // /compact instead of an answer: the app drops the box along with the old conversation. An automatic compact runs
        // in the middle of a turn, when no box is waiting, and changes nothing.
        private void EndCompactedWait(ClaudeSession session, DateTime now)
        {
            if (WaitsOnBox(session))
            {
                EndBoxWait(session, this.HasPausedAgents(session, now));
            }
        }

        private static void EndBoxWait(ClaudeSession session, Boolean agentsPaused)
        {
            session.AwaitsReply = false;
            session.WaitingAgentId = null;
            session.State = session.HasBackgroundWork || agentsPaused ? AgentSessionState.Background : AgentSessionState.Complete;
        }
    }
}
