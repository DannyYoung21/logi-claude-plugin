namespace Loupedeck.ClaudeControllerPlugin.Services
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;

    // Follows one Claude Code transcript (~/.claude/projects/<project>/<session>.jsonl) and reads only what was appended:
    // token totals, the context of the last request, the session title and whether the user interrupted the last turn.
    public class TranscriptReader
    {
        private const String InterruptMarker = "[Request interrupted by user";
        private const String SessionIdField = "\"sessionId\":\"";

        // A background subagent that pauses while its own background work (a monitor, a test run) goes on reports back
        // with this note; the turn of the main agent then ends with nothing "in flight", although the subagent resumes later
        private const String TaskNotification = "<task-notification>";
        private const String PausedNote = "background work of its own still running";

        // A paused subagent that never reports again (killed, app closed) stops counting after this long
        private static readonly TimeSpan PausedAgentMaxAge = TimeSpan.FromHours(1);

        private static readonly System.Text.RegularExpressions.Regex TaskIdPattern = new("<task-id>([^<]+)</task-id>");
        private static readonly System.Text.RegularExpressions.Regex TimestampPattern = new("\"timestamp\":\"([^\"]+)\"");

        // Paused background subagents by task id, with the time they paused
        private readonly System.Collections.Generic.Dictionary<String, DateTime> _pausedAgents = new();

        private Int64 _offset;
        private String _lastMessageId;

        public String Path { get; private set; }

        public Int64 InputTokens { get; private set; }
        public Int64 OutputTokens { get; private set; }
        public Int64 CacheReadTokens { get; private set; }
        public Int64 CacheCreationTokens { get; private set; }

        // input + cache read + cache creation of the last request = what is in the context window now
        public Int64 ContextUsedTokens { get; private set; }

        public String Model { get; private set; } = String.Empty;
        public String CustomTitle { get; private set; } = String.Empty;

        // When the last turn ended with "[Request interrupted by user]" (Esc, or a rejected tool); null if it did not
        public DateTime? InterruptedAtUtc { get; private set; }

        // The desktop app restarts Claude Code under a new session id and copies the transcript, whose old lines keep
        // the old id: the last id that is not this transcript's own is the session it continues
        public String PredecessorId { get; private set; }

        private readonly String _ownId;

        public Boolean HasPausedAgents(DateTime nowUtc) => this._pausedAgents.Values.Any(at => nowUtc - at < PausedAgentMaxAge);

        public TranscriptReader(String path)
        {
            this.Path = path;
            this._ownId = System.IO.Path.GetFileNameWithoutExtension(path ?? String.Empty);
        }

        // Reads what was appended since the last call; true if anything new came in
        public Boolean Update()
        {
            if (String.IsNullOrEmpty(this.Path) || !File.Exists(this.Path))
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(this.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < this._offset)
                {
                    // Rewritten (e.g. compacted): start over
                    this.Reset();
                }
                if (stream.Length == this._offset)
                {
                    return false;
                }

                stream.Seek(this._offset, SeekOrigin.Begin);
                var buffer = new Byte[stream.Length - this._offset];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = stream.Read(buffer, read, buffer.Length - read);
                    if (n == 0) break;
                    read += n;
                }

                // Only complete lines; a line still being written is read next time
                var end = Array.LastIndexOf(buffer, (Byte)'\n', read - 1);
                if (end < 0)
                {
                    return false;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, end + 1);
                this._offset += end + 1;

                foreach (var line in text.Split('\n'))
                {
                    if (line.Length > 0)
                    {
                        this.ReadLine(line);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Verbose(ex, $"Could not read transcript {this.Path}");
                return false;
            }
        }

        private void Reset()
        {
            this._offset = 0;
            this._lastMessageId = null;
            this.InputTokens = this.OutputTokens = this.CacheReadTokens = this.CacheCreationTokens = 0;
            this.ContextUsedTokens = 0;
            this.InterruptedAtUtc = null;
            this._pausedAgents.Clear();
        }

        private void ReadLine(String line)
        {
            var at = line.IndexOf(SessionIdField, StringComparison.Ordinal);
            if (at >= 0)
            {
                var start = at + SessionIdField.Length;
                var end = line.IndexOf('"', start);
                if (end > start && !String.Equals(line[start..end], this._ownId, StringComparison.OrdinalIgnoreCase))
                {
                    this.PredecessorId = line[start..end];
                }
            }

            // Cheap checks first: most lines are tool output we do not need to parse
            var isAssistant = line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal);
            var isUser = !isAssistant && line.Contains("\"type\":\"user\"", StringComparison.Ordinal);
            var isTitle = !isAssistant && !isUser && line.Contains("\"type\":\"custom-title\"", StringComparison.Ordinal);

            if (isUser && line.Contains(TaskNotification, StringComparison.Ordinal))
            {
                this.ReadTaskNotification(line);
            }
            if (isUser)
            {
                // A user line after the answer: either a new prompt / tool result, or the interrupt marker
                this.InterruptedAtUtc = line.Contains(InterruptMarker, StringComparison.Ordinal) ? ReadInterruptTime(line) : null;
                return;
            }
            if (!isAssistant && !isTitle)
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (isTitle)
                {
                    if (root.TryGetProperty("customTitle", out var title) && title.ValueKind == JsonValueKind.String)
                    {
                        this.CustomTitle = title.GetString() ?? String.Empty;
                    }
                    return;
                }

                this.InterruptedAtUtc = null;
                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                {
                    return;
                }
                if (message.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String &&
                    !model.GetString().StartsWith("<", StringComparison.Ordinal))
                {
                    this.Model = model.GetString();
                }
                if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                var input = GetInt(usage, "input_tokens");
                var cacheRead = GetInt(usage, "cache_read_input_tokens");
                var cacheCreation = GetInt(usage, "cache_creation_input_tokens");
                this.ContextUsedTokens = input + cacheRead + cacheCreation;

                // One answer is split into several lines (one per content block) with the same usage: count it once
                var id = message.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
                if (id != null && id == this._lastMessageId)
                {
                    return;
                }
                this._lastMessageId = id;

                this.InputTokens += input;
                this.OutputTokens += GetInt(usage, "output_tokens");
                this.CacheReadTokens += cacheRead;
                this.CacheCreationTokens += cacheCreation;
            }
            catch (Exception)
            {
                // Not valid JSON (should not happen for complete lines): skip it
            }
        }

        // Every report of a background subagent: paused (waits for its own background work) or done for now
        private void ReadTaskNotification(String line)
        {
            var id = TaskIdPattern.Match(line);
            if (!id.Success)
            {
                return;
            }
            if (line.Contains(PausedNote, StringComparison.Ordinal))
            {
                var ts = TimestampPattern.Match(line);
                this._pausedAgents[id.Groups[1].Value] = ts.Success &&
                    DateTimeOffset.TryParse(ts.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    ? at.UtcDateTime
                    : DateTime.UtcNow;
            }
            else
            {
                this._pausedAgents.Remove(id.Groups[1].Value);
            }
        }

        // Time of the interrupt if the marker is the user's own text (not inside a tool result that happens to contain it)
        private static DateTime? ReadInterruptTime(String line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
                {
                    return null;
                }

                var isInterrupt = content.ValueKind == JsonValueKind.String
                    ? content.GetString().StartsWith(InterruptMarker, StringComparison.Ordinal)
                    : content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(block =>
                        block.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                        block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
                        text.GetString().StartsWith(InterruptMarker, StringComparison.Ordinal));
                if (!isInterrupt)
                {
                    return null;
                }

                return root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    ? at.UtcDateTime
                    : DateTime.UtcNow;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Int64 GetInt(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

        // ~/.claude/projects/<cwd with every non-alphanumeric character replaced by '-'>/<session>.jsonl
        public static String GuessPath(String cwd, String sessionId)
        {
            if (String.IsNullOrEmpty(sessionId))
            {
                return null;
            }

            var projects = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (!String.IsNullOrEmpty(cwd))
            {
                var sb = new StringBuilder(cwd.Length);
                foreach (var c in cwd)
                {
                    sb.Append(Char.IsAsciiLetterOrDigit(c) ? c : '-');
                }
                var direct = System.IO.Path.Combine(projects, sb.ToString(), sessionId + ".jsonl");
                if (File.Exists(direct))
                {
                    return direct;
                }
            }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(projects))
                {
                    var candidate = System.IO.Path.Combine(dir, sessionId + ".jsonl");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }
    }
}
