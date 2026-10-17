namespace Loupedeck.ClaudeControllerPlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

    // Sessions of the Claude desktop app (Code tab), one JSON file each:
    // ~/Library/Application Support/Claude/claude-code-sessions/<uuid>/<orgId>/local_<id>.json
    // They give us the desktop session id (for the claude://code/continue deep link) and the title shown in the app.
    public class DesktopSessionIndex
    {
        public sealed class Entry
        {
            public String LocalId;
            public String CliSessionId;
            public String Title;
            public String Cwd;
            public String Model;
            public DateTime LastActivityUtc;
            public Boolean IsArchived;

            public Entry Clone() => (Entry)this.MemberwiseClone();
        }

        private static readonly String SessionsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Claude", "claude-code-sessions");

        // Parsed files, reused until the file changes
        private readonly Dictionary<String, (DateTime WriteTime, Entry Entry)> _fileCache = new(StringComparer.Ordinal);

        private Dictionary<String, Entry> _byCliSessionId = new(StringComparer.OrdinalIgnoreCase);

        public Entry Find(String cliSessionId) =>
            cliSessionId != null && this._byCliSessionId.TryGetValue(cliSessionId, out var entry) ? entry : null;

        public Entry FindByLocalId(String localId) =>
            String.IsNullOrEmpty(localId) ? null : this._byCliSessionId.Values.FirstOrDefault(e => e.LocalId == localId);

        public IReadOnlyCollection<Entry> Entries => this._byCliSessionId.Values;

        // False if the folder could not be read; the previous result is kept then
        public Boolean Scan()
        {
            var result = new Dictionary<String, Entry>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<String>(StringComparer.Ordinal);

            try
            {
                if (!Directory.Exists(SessionsDir))
                {
                    this._byCliSessionId = result;
                    return true;
                }

                foreach (var orgDir in Directory.EnumerateDirectories(SessionsDir).SelectMany(Directory.EnumerateDirectories))
                {
                    var archived = ReadArchivedIndex(Path.Combine(orgDir, "archived-sessions.idx"));

                    foreach (var file in Directory.EnumerateFiles(orgDir, "local_*.json"))
                    {
                        seen.Add(file);
                        var entry = this.ReadCached(file);
                        if (entry == null || String.IsNullOrEmpty(entry.CliSessionId))
                        {
                            continue;
                        }

                        entry.IsArchived |= archived.Contains(entry.LocalId);

                        // Several desktop sessions can point at the same CLI session (fork); the most recent one wins
                        if (!result.TryGetValue(entry.CliSessionId, out var existing) || existing.LastActivityUtc < entry.LastActivityUtc)
                        {
                            result[entry.CliSessionId] = entry;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not read the desktop app sessions");
                return false;
            }

            foreach (var gone in this._fileCache.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                this._fileCache.Remove(gone);
            }
            this._byCliSessionId = result;
            return true;
        }

        private Entry ReadCached(String file)
        {
            try
            {
                var writeTime = File.GetLastWriteTimeUtc(file);
                if (this._fileCache.TryGetValue(file, out var cached) && cached.WriteTime == writeTime)
                {
                    // A copy: the archived flag is combined with the index file on every scan
                    return cached.Entry.Clone();
                }

                using var stream = File.OpenRead(file);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                var entry = new Entry
                {
                    LocalId = GetString(root, "sessionId"),
                    CliSessionId = GetString(root, "cliSessionId"),
                    Title = GetString(root, "title"),
                    Cwd = GetString(root, "cwd"),
                    Model = GetString(root, "model"),
                    LastActivityUtc = root.TryGetProperty("lastActivityAt", out var at) && at.TryGetInt64(out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                        : writeTime,
                    IsArchived = root.TryGetProperty("isArchived", out var a) && a.ValueKind == JsonValueKind.True
                };
                this._fileCache[file] = (writeTime, entry);
                return entry.Clone();
            }
            catch (Exception)
            {
                return null;  // being written right now; read again on the next scan
            }
        }

        private static HashSet<String> ReadArchivedIndex(String path)
        {
            var set = new HashSet<String>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("archived", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var id in list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String))
                        {
                            set.Add(id.GetString());
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return set;
        }

        private static String GetString(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
