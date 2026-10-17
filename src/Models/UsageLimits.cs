namespace Loupedeck.ClaudeControllerPlugin.Models
{
    using System;
    using System.Text.Json.Serialization;

    // The limit windows shown on the keypad
    public enum LimitWindow
    {
        FiveHour,     // Rolling 5-hour session limit (all models)
        Weekly,       // Weekly limit (all models)
        ModelWeekly   // Weekly limit for one model (e.g. "Fable"); the model name comes from the API
    }

    public class QuotaWindow
    {
        // False if the account has no such window (e.g. no model-scoped weekly limit)
        public Boolean Available { get; set; }
        public Double UsedPercent { get; set; }
        public DateTime? ResetTimeUtc { get; set; }

        // When this value was measured; the newest source wins (API poll or statusline)
        public DateTime UpdatedUtc { get; set; }

        [JsonIgnore]
        public TimeSpan? RemainingUntilReset => this.ResetTimeUtc.HasValue
            ? TimeSpan.FromTicks(Math.Max(0, (this.ResetTimeUtc.Value - DateTime.UtcNow).Ticks))
            : null;
    }

    public class ExtraUsage
    {
        public Boolean Available { get; set; }
        public Boolean Enabled { get; set; }
        public Double UsedPercent { get; set; }
    }

    public class UsageSummary
    {
        public QuotaWindow FiveHour { get; set; } = new();
        public QuotaWindow Weekly { get; set; } = new();
        public QuotaWindow ModelWeekly { get; set; } = new();
        public ExtraUsage Extra { get; set; } = new();

        // Display name of the model with its own weekly limit ("Fable"); empty if there is none
        public String ModelName { get; set; } = String.Empty;

        // False until a source answered once; tiles then show placeholders instead of 0%
        public Boolean IsLive { get; set; }

        public QuotaWindow For(LimitWindow window) => window switch
        {
            LimitWindow.Weekly => this.Weekly,
            LimitWindow.ModelWeekly => this.ModelWeekly,
            _ => this.FiveHour
        };
    }
}
