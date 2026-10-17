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
    // refreshing it here would log Claude Code out.
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

                var token = ReadAccessToken();
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

        // Claude Code keeps its login in the keychain as JSON: {"claudeAiOauth":{"accessToken":…,"expiresAt":<ms>,…}}
        private static String ReadAccessToken()
        {
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
                    PluginLog.Info("No Claude Code login found in the keychain");
                    return null;
                }

                using var doc = JsonDocument.Parse(output);
                if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                    !oauth.TryGetProperty("accessToken", out var tokenElement))
                {
                    return null;
                }

                if (oauth.TryGetProperty("expiresAt", out var expiresAt) && expiresAt.TryGetInt64(out var expiresMs) &&
                    DateTimeOffset.FromUnixTimeMilliseconds(expiresMs) <= DateTimeOffset.UtcNow)
                {
                    // Expired: Claude Code refreshes it the next time it runs; never refresh it ourselves
                    PluginLog.Info("Claude Code login token is expired, waiting for Claude Code to renew it");
                    return null;
                }

                return tokenElement.GetString();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not read the Claude Code login from the keychain");
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
                        UsedPercent = GetDouble(extra, "utilization") ?? 0
                    };
                }
                else
                {
                    usage.Extra = new ExtraUsage { Available = false };
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
