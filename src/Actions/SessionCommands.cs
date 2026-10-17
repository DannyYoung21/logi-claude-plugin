namespace Loupedeck.ClaudeControllerPlugin.Actions
{
    using System;
    using Loupedeck.ClaudeControllerPlugin.Helpers;
    using Loupedeck.ClaudeControllerPlugin.Models;
    using Loupedeck.ClaudeControllerPlugin.Services;

    // Standalone session keys. Class names are part of saved keypad layouts, so they must not be renamed.

    // Redraws whenever sessions change
    public abstract class LiveCommand : PluginDynamicCommand
    {
        protected const String SessionGroup = "SESSION CONTROLS";

        protected LiveCommand(String displayName, String description, String groupName)
            : base(displayName: displayName, description: description, groupName: groupName)
        {
            SessionStore.Instance.OnSessionsChanged += () => this.ActionImageChanged();
        }

        protected static SessionStore Store => SessionStore.Instance;
    }

    public class ContextWindowCommand : LiveCommand
    {
        public ContextWindowCommand()
            : base("Context", "Context window usage of the last opened slot", SessionGroup)
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderContextKey(Store.GetActiveSession(), imageSize);

        protected override void RunCommand(String actionParameter) => Store.OpenSession(Store.GetActiveSession());
    }

    public class TokenUsageCommand : LiveCommand
    {
        public TokenUsageCommand()
            : base("Tokens", "Token usage (input, output, cache) of the last opened slot", SessionGroup)
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderTokenStatsKey(Store.GetActiveSession(), imageSize);

        protected override void RunCommand(String actionParameter) => Store.TriggerManualSync();
    }

    // ---------------------------------------------------------------- Permission answers

    // Shared by the answer keys: they act on the last opened slot if it waits, else on the session waiting longest
    public abstract class AnswerCommand : LiveCommand
    {
        private readonly PermissionDecision _decision;

        protected AnswerCommand(PermissionDecision decision, String displayName, String description)
            : base(displayName, description, SessionGroup)
        {
            this._decision = decision;
        }

        protected override void RunCommand(String actionParameter)
        {
            var session = Store.GetPendingSession();
            if (session != null)
            {
                Store.AnswerPermission(session, this._decision);
            }
        }
    }

    public class AllowOnceCommand : AnswerCommand
    {
        public AllowOnceCommand()
            : base(PermissionDecision.AllowOnce, "Allow Once", "Approves the pending request once (last opened slot first, else the session waiting longest)")
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderAllowOnceKey(Store.GetPendingSession(), imageSize);
    }

    public class AllowSessionCommand : AnswerCommand
    {
        public AllowSessionCommand()
            : base(PermissionDecision.AllowSession, "Allow in Session", "Approves the pending request and does not ask again for it in this session")
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderAllowSessionKey(Store.GetPendingSession(), imageSize);
    }

    public class AlwaysAllowCommand : AnswerCommand
    {
        public AlwaysAllowCommand()
            : base(PermissionDecision.AlwaysAllow, "Always Allow", "Approves the pending request and keeps the rule Claude suggests (like \"Yes, and don't ask again\")")
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderAlwaysAllowKey(Store.GetPendingSession(), imageSize);
    }

    public class DenyCommand : AnswerCommand
    {
        public DenyCommand()
            : base(PermissionDecision.Deny, "Deny", "Rejects the pending request (last opened slot first, else the session waiting longest)")
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderDenyKey(Store.GetPendingSession(), imageSize);
    }

    // ---------------------------------------------------------------- Session controls

    public class StopCommand : LiveCommand
    {
        public StopCommand()
            : base("Stop", "Stops the last opened session if it runs, else the most recently active running session", SessionGroup)
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderStopKey(Store.GetInterruptibleSession() != null, imageSize);

        protected override void RunCommand(String actionParameter) => Store.Stop(Store.GetInterruptibleSession());
    }

    public class NewCodeSessionCommand : PluginDynamicCommand
    {
        public NewCodeSessionCommand()
            : base(displayName: "New Code Session", description: "Starts a new Claude Code session in the desktop app", groupName: "SESSION CONTROLS")
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderNewKey("New Code", imageSize);

        protected override void RunCommand(String actionParameter) => ClaudeAppLauncher.NewCodeSession();
    }

    // Claude merged chat and Cowork into one composer that picks what to start
    public class NewChatCommand : PluginDynamicCommand
    {
        public NewChatCommand()
            : base(displayName: "New Chat / Cowork Task", description: "Opens a new conversation in the Claude desktop app; Claude decides whether it becomes a chat or a Cowork task", groupName: "SESSION CONTROLS")
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderNewKey("Chat/Cowork", imageSize);

        protected override void RunCommand(String actionParameter) => ClaudeAppLauncher.NewChat();
    }

    // Pulses while sessions wait; each press opens the next waiting session in Claude
    public class NextWaitingCommand : LiveCommand
    {
        public NextWaitingCommand()
            : base("Next Waiting Session", "Lights up while a session waits for permission or for you to choose an answer; press to jump to it (repeated presses cycle)", SessionGroup)
        {
            TileAnimationService.Instance.OnAnimationTick += _ =>
            {
                if (Store.GetWaitingSessions().Count > 0)
                {
                    this.ActionImageChanged();
                }
            };
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderNextWaitingKey(Store.GetWaitingSessions(), TileAnimationService.Instance.CurrentBreath, imageSize);

        protected override void RunCommand(String actionParameter) => Store.ShowNextWaitingSession();
    }

    public class ClearFinishedCommand : LiveCommand
    {
        public ClearFinishedCommand()
            : base("Clear Finished", "Frees the slots of finished sessions (nothing changes in Claude; a session comes back when it does something again)", SessionGroup)
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderClearFinishedKey(Store.FinishedCount, imageSize);

        protected override void RunCommand(String actionParameter) => Store.ClearFinished();
    }

    // Installs or removes the plugin's hooks and statusline bridge in ~/.claude/settings.json. Logi reports no
    // uninstall, so this is the way to leave Claude Code exactly as it was before the plugin.
    public class ClaudeHooksCommand : PluginDynamicCommand
    {
        public ClaudeHooksCommand()
            : base(displayName: "Claude Hooks On/Off", description: "Adds or removes the plugin's hooks in ~/.claude/settings.json (Off restores your settings and statusline; live status and keypad answers stop until you turn it on again)", groupName: "SESSION CONTROLS")
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderHooksKey(HookInstaller.IsInstalled(), imageSize);

        protected override void RunCommand(String actionParameter)
        {
            var plugin = ClaudeControllerPlugin.Instance;
            if (HookInstaller.IsInstalled())
            {
                HookInstaller.Remove();
                plugin.HooksDisabled = true;
            }
            else
            {
                HookInstaller.Install();
                plugin.HooksDisabled = false;
            }
            this.ActionImageChanged();
        }
    }

    public class FocusClaudeCommand : PluginDynamicCommand
    {
        public FocusClaudeCommand()
            : base(displayName: "Open Claude", description: "Brings the Claude desktop app to the foreground", groupName: "SESSION CONTROLS")
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderFocusKey(imageSize);

        protected override void RunCommand(String actionParameter) => ClaudeAppLauncher.FocusClaude();
    }

    public class RefreshSessionsCommand : LiveCommand
    {
        public RefreshSessionsCommand()
            : base("Refresh", "Refreshes sessions and limits now", SessionGroup)
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderRefreshKey(imageSize, Store.IsSyncing);

        protected override void RunCommand(String actionParameter) => Store.TriggerManualSync();
    }
}
