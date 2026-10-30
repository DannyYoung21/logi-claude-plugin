namespace Loupedeck.ClaudeControllerPlugin.Services
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Loupedeck.ClaudeControllerPlugin.Models;

    // Claude usage limits (5h, weekly, model-scoped weekly, extra usage) from two sources:
    //  - GET https://api.anthropic.com/api/oauth/usage with the Claude Code login from the macOS keychain (works for the desktop app)
    //  - the statusline JSON of terminal sessions (rate_limits, pushed after every answer)
    // The newest value per window wins. The OAuth token is only read, never refreshed or stored:
    // refreshing it here would log Claude Code out. An expired token is renewed by Claude Code itself (TryRenewLogin).
    public class UsageLimitService : IDisposable
    {
        private static readonly Lazy<UsageLimitService> _instance = new(() => new UsageLimitService());
        public static UsageLimitService Instance => _instance.Value;

        private const String UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        private const String KeychainService = "Claude Code-credentials";
        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan ManualRefreshThrottle = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan RedrawInterval = TimeSpan.FromSeconds(30);

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        // Last known limits, so keys (and the Options+ editor, which keeps the first image it gets) show values right after a start
        private static readonly String CachePath = Path.Combine(HookInstaller.DataDir, "usage-cache.json");

        private readonly Object _lock = new();
        private readonly SemaphoreSlim _fetchGate = new(1, 1);
        private readonly Timer _pollTimer;
        private readonly Timer _redrawTimer;
        private TimeSpan _currentInterval = PollInterval;
        private DateTime _lastFetchUtc = DateTime.MinValue;
        private DateTime _lastRenewUtc = DateTime.MinValue;

        // Claude Code is started to renew an expired login at most this often, and given this long to finish
        private static readonly TimeSpan RenewThrottle = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan RenewTimeout = TimeSpan.FromSeconds(60);

        public event Action OnLimitsChanged;

        public UsageSummary Usage { get; private set; } = new();

        public UsageLimitService()
        {
            this.LoadCache();

            this._pollTimer = new Timer(_ => _ = this.FetchAsync(), null, Timeout.Infinite, Timeout.Infinite);

            // Reset countdowns tick down locally between polls
            this._redrawTimer = new Timer(_ => this.OnLimitsChanged?.Invoke(), null, RedrawInterval, RedrawInterval);
        }

        public void Start() => this._pollTimer.Change(TimeSpan.Zero, this._currentInterval);

        // Key press: fetch now unless we just did
        public void RefreshNow()
        {
            if (DateTime.UtcNow - this._lastFetchUtc >= ManualRefreshThrottle)
            {
                _ = this.FetchAsync();
            }
        }

        // ---------------------------------------------------------------- API source

        private async Task FetchAsync()
        {
            if (!await this._fetchGate.WaitAsync(0))
            {
                return;
            }

            try
            {
                this._lastFetchUtc = DateTime.UtcNow;

                var token = ReadAccessToken(out var loginMissing, out var expired);
                if (token == null && expired && !loginMissing && this.TryRenewLogin())
                {
                    token = ReadAccessToken(out loginMissing, out _);
                }
                this.SetLoginMissing(loginMissing);
                if (token == null)
                {
                    return;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                request.Headers.TryAddWithoutValidation("User-Agent", "ClaudeController-LogiPlugin");

                using var response = await Http.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    this.SetInterval(TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, this._currentInterval.Ticks * 2)));
                    PluginLog.Warning($"Usage API rate limited, next poll in {this._currentInterval.TotalMinutes:0} min");
                    return;
                }
                if (!response.IsSuccessStatusCode)
                {
                    // 401 = token just expired; Claude Code renews it, the next poll reads the new one
                    PluginLog.Warning($"Usage API answered {(Int32)response.StatusCode}");
                    return;
                }

                this.SetInterval(PollInterval);
                var json = await response.Content.ReadAsStringAsync();
                this.ApplyApiResponse(json);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Usage API request failed");
            }
            finally
            {
                this._fetchGate.Release();
            }
        }

        private void SetInterval(TimeSpan interval)
        {
            if (interval != this._currentInterval)
            {
                this._currentInterval = interval;
                this._pollTimer.Change(interval, interval);
            }
        }

        private void SetLoginMissing(Boolean missing)
        {
            if (this.Usage.LoginMissing == missing)
            {
                return;
            }
            this.Usage.LoginMissing = missing;
            PluginLog.Info(missing
                ? "No usable Claude Code login in the keychain: run `claude auth login` in the terminal to update the limits"
                : "Claude Code login found in the keychain");
            this.OnLimitsChanged?.Invoke();
        }

        // Claude Code keeps its login in the keychain as JSON: {"claudeAiOauth":{"accessToken":…,"expiresAt":<ms>,
        // "refreshTokenExpiresAt":<ms>,…}}. The desktop app signs in on its own and does not keep this one alive:
        // it is renewed by Claude Code in the terminal, and cleared when its sign-in runs out.
        // loginMissing: there is no token, and none will come until the user signs in again.
        // expired: the token ran out, Claude Code can renew it.
        private static String ReadAccessToken(out Boolean loginMissing, out Boolean expired)
        {
            loginMissing = false;
            expired = false;
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/security")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("find-generic-password");
                psi.ArgumentList.Add("-s");
                psi.ArgumentList.Add(KeychainService);
                psi.ArgumentList.Add("-w");

                // Runs on a timer thread: if macOS asks once whether the plugin may read the item,
                // this simply waits until the user answered
                using var process = Process.Start(psi);
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    loginMissing = true;
                    return null;
                }

                using var doc = JsonDocument.Parse(output);
                var token = doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) &&
                    oauth.TryGetProperty("accessToken", out var tokenElement) && tokenElement.ValueKind == JsonValueKind.String
                    ? tokenElement.GetString()
                    : null;
                if (String.IsNullOrEmpty(token))
                {
                    loginMissing = true;
                    return null;
                }

                if (IsPast(oauth, "expiresAt"))
                {
                    // Expired: Claude Code refreshes it the next time it runs; never refresh it ourselves.
                    // Once the sign-in behind it has run out too, nothing can renew it.
                    loginMissing = IsPast(oauth, "refreshTokenExpiresAt");
                    expired = true;
                    PluginLog.Verbose("Claude Code login token is expired, waiting for Claude Code to renew it");
                    return null;
                }

                return token;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not read the Claude Code login from the keychain");
                return null;
            }
        }

        // Claude Code renews an expired login whenever it runs. With only the desktop app in use nothing runs it, so the
        // plugin starts it once for its local /usage command: Claude Code renews the token itself and saves it in the
        // keychain. No prompt goes to a model, no hooks run and no session is saved.
        private Boolean TryRenewLogin()
        {
            var now = DateTime.UtcNow;
            if (now - this._lastRenewUtc < RenewThrottle)
            {
                return false;
            }
            this._lastRenewUtc = now;

            var claude = FindClaudeCode();
            if (claude == null)
            {
                PluginLog.Info("Claude Code login token is expired and Claude Code was not found to renew it");
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo(claude)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = HookInstaller.DataDir
                };
                foreach (var arg in new[] { "-p", "/usage", "--no-session-persistence", "--settings", "{\"disableAllHooks\":true}" })
                {
                    psi.ArgumentList.Add(arg);
                }
                psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
                psi.Environment.Remove("CLAUDECODE");
                Directory.CreateDirectory(HookInstaller.DataDir);

                using var process = Process.Start(psi);
                process.StandardInput.Close();
                // The output is not needed, but must be drained so Claude Code never blocks on a full pipe
                _ = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit((Int32)RenewTimeout.TotalMilliseconds))
                {
                    process.Kill(entireProcessTree: true);
                    PluginLog.Warning("Claude Code did not finish renewing its login");
                    return false;
                }

                PluginLog.Info($"Asked Claude Code to renew its expired login (exit code {process.ExitCode})");
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not run Claude Code to renew its login");
                return false;
            }
        }

        // Claude Code in the terminal first (it holds the keychain login), else the desktop app's own copy
        // (claude-code/<version>/<build>/claude.app/Contents/MacOS/claude)
        private static String FindClaudeCode()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var installed = new[]
            {
                Path.Combine(home, ".local", "bin", "claude"),
                "/opt/homebrew/bin/claude",
                "/usr/local/bin/claude",
                Path.Combine(home, ".claude", "local", "claude")
            }.FirstOrDefault(File.Exists);
            if (installed != null)
            {
                return installed;
            }

            try
            {
                var bundled = Path.Combine(home, "Library", "Application Support", "Claude", "claude-code");
                return Directory.Exists(bundled)
                    ? Directory.EnumerateFiles(bundled, "claude", SearchOption.AllDirectories)
                        .Where(p => p.EndsWith("/Contents/MacOS/claude", StringComparison.Ordinal))
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault()
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal void ApplyApiResponse(String json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var now = DateTime.UtcNow;

            lock (this._lock)
            {
                var usage = this.Usage;

                if (TryReadWindow(root, "five_hour", now, out var fiveHour))
                {
                    usage.FiveHour = Newer(usage.FiveHour, fiveHour);
                }
                if (TryReadWindow(root, "seven_day", now, out var weekly))
                {
                    usage.Weekly = Newer(usage.Weekly, weekly);
                }

                // Model-scoped weekly limits come in limits[] (kind "weekly_scoped"); show the most used one
                var scoped = root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array
                    ? limits.EnumerateArray().Where(l => GetString(l, "kind") == "weekly_scoped").OrderByDescending(l => GetDouble(l, "percent") ?? 0).FirstOrDefault()
                    : default;
                if (scoped.ValueKind == JsonValueKind.Object)
                {
                    usage.ModelWeekly = new QuotaWindow
                    {
                        Available = true,
                        UsedPercent = GetDouble(scoped, "percent") ?? 0,
                        ResetTimeUtc = ParseTime(GetString(scoped, "resets_at")),
                        UpdatedUtc = now
                    };
                    usage.ModelName = scoped.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object &&
                        scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object
                        ? GetString(model, "display_name") ?? String.Empty
                        : String.Empty;
                }
                else
                {
                    usage.ModelWeekly = new QuotaWindow { Available = false, UpdatedUtc = now };
                    usage.ModelName = String.Empty;
                }

                if (root.TryGetProperty("extra_usage", out var extra) && extra.ValueKind == JsonValueKind.Object)
                {
                    usage.Extra = new ExtraUsage
                    {
                        Available = true,
                        Enabled = extra.TryGetProperty("is_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
                        UsedPercent = GetDouble(extra, "utilization") ?? 0,
                        UpdatedUtc = now
                    };
                }
                else
                {
                    usage.Extra = new ExtraUsage { Available = false, UpdatedUtc = now };
                }

                usage.IsLive = true;
            }

            this.SaveCache();
            PluginLog.Info($"Usage limits: 5h {this.Usage.FiveHour.UsedPercent:0}%, 7d {this.Usage.Weekly.UsedPercent:0}%, " +
                $"model 7d {(this.Usage.ModelWeekly.Available ? $"{this.Usage.ModelName} {this.Usage.ModelWeekly.UsedPercent:0}%" : "n/a")}, " +
                $"extra {(this.Usage.Extra.Enabled ? $"{this.Usage.Extra.UsedPercent:0}%" : "off")}");
            this.OnLimitsChanged?.Invoke();
        }

        // ---------------------------------------------------------------- Statusline source

        // rate_limits from the statusline JSON: {"five_hour":{"used_percentage":42,"resets_at":<unix seconds>},"seven_day":{…}}
        public void ApplyStatuslineRateLimits(JsonElement rateLimits)
        {
            if (rateLimits.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var now = DateTime.UtcNow;
            lock (this._lock)
            {
                if (TryReadStatuslineWindow(rateLimits, "five_hour", now, out var fiveHour))
                {
                    this.Usage.FiveHour = Newer(this.Usage.FiveHour, fiveHour);
                }
                if (TryReadStatuslineWindow(rateLimits, "seven_day", now, out var weekly))
                {
                    this.Usage.Weekly = Newer(this.Usage.Weekly, weekly);
                }
                this.Usage.IsLive = true;
            }
            this.SaveCache();
            this.OnLimitsChanged?.Invoke();
        }

        private void LoadCache()
        {
            try
            {
                if (File.Exists(CachePath) &&
                    JsonSerializer.Deserialize<UsageSummary>(File.ReadAllText(CachePath)) is UsageSummary cached && cached.IsLive)
                {
                    this.Usage = cached;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Verbose(ex, "Usage cache not readable");
            }
        }

        private void SaveCache()
        {
            try
            {
                String json;
                lock (this._lock)
                {
                    json = JsonSerializer.Serialize(this.Usage);
                }
                Directory.CreateDirectory(HookInstaller.DataDir);
                File.WriteAllText(CachePath, json);
            }
            catch (Exception ex)
            {
                PluginLog.Verbose(ex, "Usage cache not writable");
            }
        }

        private static Boolean TryReadStatuslineWindow(JsonElement parent, String name, DateTime now, out QuotaWindow window)
        {
            window = null;
            if (!parent.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            window = new QuotaWindow
            {
                Available = true,
                UsedPercent = GetDouble(e, "used_percentage") ?? 0,
                ResetTimeUtc = e.TryGetProperty("resets_at", out var reset) && reset.TryGetInt64(out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                    : null,
                UpdatedUtc = now
            };
            return true;
        }

        // ---------------------------------------------------------------- Helpers

        private static QuotaWindow Newer(QuotaWindow current, QuotaWindow candidate) =>
            current == null || !current.Available || candidate.UpdatedUtc >= current.UpdatedUtc ? candidate : current;

        // {"utilization":13.0,"resets_at":"2026-10-17T12:40:00.206625+00:00", …}
        private static Boolean TryReadWindow(JsonElement root, String name, DateTime now, out QuotaWindow window)
        {
            window = null;
            if (!root.TryGetProperty(name, out var e))
            {
                return false;
            }

            window = e.ValueKind == JsonValueKind.Object
                ? new QuotaWindow
                {
                    Available = true,
                    UsedPercent = GetDouble(e, "utilization") ?? 0,
                    ResetTimeUtc = ParseTime(GetString(e, "resets_at")),
                    UpdatedUtc = now
                }
                : new QuotaWindow { Available = false, UpdatedUtc = now };
            return true;
        }

        // A time in unix milliseconds that has passed (false if missing)
        private static Boolean IsPast(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.TryGetInt64(out var ms) && DateTimeOffset.FromUnixTimeMilliseconds(ms) <= DateTimeOffset.UtcNow;

        private static String GetString(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static Double? GetDouble(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        private static DateTime? ParseTime(String value) =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.UtcDateTime : null;

        public void Dispose()
        {
            this._pollTimer?.Dispose();
            this._redrawTimer?.Dispose();
        }
    }
}
