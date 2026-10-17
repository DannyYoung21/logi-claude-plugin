namespace Loupedeck.ClaudeControllerPlugin.Actions
{
    using System;
    using Loupedeck.ClaudeControllerPlugin.Helpers;
    using Loupedeck.ClaudeControllerPlugin.Models;
    using Loupedeck.ClaudeControllerPlugin.Services;

    // Usage limit keys. Class names are part of saved keypad layouts, so they must not be renamed.

    // Redraws whenever the limits change (and every 30 s for the countdowns); a press refreshes now
    public abstract class LimitCommand : PluginDynamicCommand
    {
        protected const String LimitsGroup = "LIMITS";

        protected LimitCommand(String displayName, String description)
            : base(displayName: displayName, description: description, groupName: LimitsGroup)
        {
            UsageLimitService.Instance.OnLimitsChanged += () => this.ActionImageChanged();
        }

        protected static UsageSummary Usage => UsageLimitService.Instance.Usage;

        protected override void RunCommand(String actionParameter) => UsageLimitService.Instance.RefreshNow();
    }

    public abstract class QuotaCommand : LimitCommand
    {
        private readonly LimitWindow _window;
        private readonly Boolean _isReset;

        protected QuotaCommand(LimitWindow window, Boolean isReset, String displayName, String description)
            : base(displayName, description)
        {
            this._window = window;
            this._isReset = isReset;
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            this._isReset
                ? SessionTileRenderer.RenderResetKey(this._window, Usage, imageSize)
                : SessionTileRenderer.RenderLimitKey(this._window, Usage, imageSize);
    }

    public class FiveHourLimitCommand : QuotaCommand
    {
        public FiveHourLimitCommand()
            : base(LimitWindow.FiveHour, false, "Claude 5h Limit", "Used share of the rolling 5-hour Claude session limit")
        {
        }
    }

    public class FiveHourResetTimeCommand : QuotaCommand
    {
        public FiveHourResetTimeCommand()
            : base(LimitWindow.FiveHour, true, "Claude 5h Reset Time", "Time until the 5-hour Claude session limit resets")
        {
        }
    }

    public class WeeklyLimitCommand : QuotaCommand
    {
        public WeeklyLimitCommand()
            : base(LimitWindow.Weekly, false, "Claude 7d Limit", "Used share of the weekly Claude limit (all models)")
        {
        }
    }

    public class WeeklyResetTimeCommand : QuotaCommand
    {
        public WeeklyResetTimeCommand()
            : base(LimitWindow.Weekly, true, "Claude 7d Reset Time", "Time until the weekly Claude limit (all models) resets")
        {
        }
    }

    // The model with its own weekly limit (currently "Fable") is only known from the API, so the Options+ name
    // ("Fable 7d Limit") uses the last seen model and follows when it changes
    public abstract class ModelQuotaCommand : QuotaCommand
    {
        private readonly String _suffix;

        protected ModelQuotaCommand(Boolean isReset, String suffix, String description)
            : base(LimitWindow.ModelWeekly, isReset, NameFor(ClaudeControllerPlugin.Instance?.LoadModelLimitName(), suffix), description)
        {
            this._suffix = suffix;
            UsageLimitService.Instance.OnLimitsChanged += this.FollowModelName;
        }

        private static String NameFor(String model, String suffix) =>
            $"{(String.IsNullOrEmpty(model) ? "Claude Model" : model)} 7d {suffix}";

        private void FollowModelName()
        {
            var model = Usage.ModelName;
            var name = NameFor(model, this._suffix);
            if (String.IsNullOrEmpty(model) || this.DisplayName == name)
            {
                return;
            }

            this.DisplayName = name;
            ClaudeControllerPlugin.Instance?.SaveModelLimitName(model);
        }
    }

    public class ModelWeeklyLimitCommand : ModelQuotaCommand
    {
        public ModelWeeklyLimitCommand()
            : base(false, "Limit", "Used share of the separate weekly Claude limit of a single model (e.g. Fable)")
        {
        }
    }

    public class ModelWeeklyResetTimeCommand : ModelQuotaCommand
    {
        public ModelWeeklyResetTimeCommand()
            : base(true, "Reset Time", "Time until the separate weekly Claude limit of a single model (e.g. Fable) resets")
        {
        }
    }

    public class ExtraUsageCommand : LimitCommand
    {
        public ExtraUsageCommand()
            : base("Claude Extra Usage", "Used share of the monthly Claude extra usage (paid credits beyond the plan); \"Off\" while it is disabled")
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderExtraUsageKey(Usage, imageSize);
    }
}
