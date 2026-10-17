namespace Loupedeck.ClaudeControllerPlugin.Actions
{
    using System;
    using Loupedeck.ClaudeControllerPlugin.Helpers;
    using Loupedeck.ClaudeControllerPlugin.Models;
    using Loupedeck.ClaudeControllerPlugin.Services;

    // One keypad tile per session slot. Pressing it opens a 3x3 page with the session's details.
    public abstract class SessionSlotDynamicFolder : PluginDynamicFolder
    {
        // Keys inside the folder (position 0 is the native Back button)
        private static readonly String[] DetailKeys =
        {
            "Context", "ProjectStatus",
            "FiveHourLimit", "FiveHourResetTime", "AllowOnce",
            "WeeklyLimit", "WeeklyResetTime", "Stop"
        };

        private Boolean _isActive;

        // What the slot tile and the folder's status key last showed. A redraw request that arrives while the device
        // is still drawing the previous pulse frame can get lost, leaving e.g. "Thinking" on a finished session.
        private String _drawnTile;
        private String _drawnStatusKey;

        public abstract Int32 SlotNumber { get; }

        protected SessionSlotDynamicFolder()
        {
            this.GroupName = "CLAUDE SESSIONS";

            SessionStore.Instance.OnSessionsChanged += this.OnSessionsChanged;
            UsageLimitService.Instance.OnLimitsChanged += this.OnLimitsChanged;
            TileAnimationService.Instance.OnAnimationTick += this.OnAnimationTick;
            SessionStore.Instance.OnHeartbeat += this.OnHeartbeat;
        }

        private static String Shown(ClaudeSession session) => session == null ? "empty" : $"{session.SlotKey}:{session.State}:{session.DisplayName}";

        private void OnHeartbeat()
        {
            var shown = Shown(this.Session);
            // Only tiles the device has drawn at all (a slot that is not on the keypad is never asked for)
            if (this._drawnTile != null && shown != this._drawnTile)
            {
                this.RefreshTile();
            }
            if (this._isActive && shown != this._drawnStatusKey)
            {
                this.CommandImageChanged("ProjectStatus");
            }
        }

        private ClaudeSession Session => SessionStore.Instance.GetSessionForSlot(this.SlotNumber);

        private void OnSessionsChanged()
        {
            this.RefreshTile();
            if (this._isActive)
            {
                foreach (var key in DetailKeys)
                {
                    this.CommandImageChanged(key);
                }
            }
        }

        private void OnLimitsChanged()
        {
            if (this._isActive)
            {
                foreach (var key in new[] { "FiveHourLimit", "WeeklyLimit", "FiveHourResetTime", "WeeklyResetTime" })
                {
                    this.CommandImageChanged(key);
                }
            }
        }

        // Thinking and Background tiles pulse, so only they are redrawn on every animation frame
        private void OnAnimationTick(Double breath)
        {
            if (this.Session?.State is AgentSessionState.Thinking or AgentSessionState.Background)
            {
                this.RefreshTile();
                if (this._isActive)
                {
                    this.CommandImageChanged("ProjectStatus");
                }
            }
        }

        // Same notification the Claude Desktop plugin uses to redraw a folder tile on the keypad
        private void RefreshTile() =>
            this.Plugin?.OnActionImageChanged("#DynamicFolder", "DynamicFolder#" + this.GetType().FullName, false);

        public override Boolean Activate()
        {
            this._isActive = true;
            SessionStore.Instance.CurrentActiveSlot = this.SlotNumber;
            SessionStore.Instance.HoldSlot(this.SlotNumber);
            this.OnSessionsChanged();
            return base.Activate();
        }

        public override Boolean Deactivate()
        {
            this._isActive = false;
            SessionStore.Instance.ReleaseSlot(this.SlotNumber);
            return base.Deactivate();
        }

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType _) => PluginDynamicFolderNavigation.None;

        // Row 0: Back, Context, Project status (opens the session)
        // Row 1: 5h limit, 5h reset time, Allow once
        // Row 2: 7d limit, 7d reset time, Stop
        public override String[] GetButtonPressActionNames(DeviceType deviceType)
        {
            var names = new String[DetailKeys.Length + 1];
            names[0] = PluginDynamicFolder.NavigateUpActionName;
            for (var i = 0; i < DetailKeys.Length; i++)
            {
                names[i + 1] = this.CreateCommandName(DetailKeys[i]);
            }
            return names;
        }

        public override BitmapImage GetButtonImage(PluginImageSize imageSize)
        {
            var session = this.Session;
            this._drawnTile = Shown(session);
            return SessionTileRenderer.RenderSlotTile(this.SlotNumber, session, TileAnimationService.Instance.CurrentBreath, imageSize, isFolder: true);
        }

        public override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var session = this.Session;
            var usage = UsageLimitService.Instance.Usage;

            return actionParameter switch
            {
                "ProjectStatus" => this.RenderStatusKey(session, imageSize),
                "AllowOnce" => SessionTileRenderer.RenderAllowOnceKey(session, imageSize),
                "Stop" => SessionTileRenderer.RenderStopKey(session?.State is AgentSessionState.Thinking or AgentSessionState.Permission, imageSize),
                "Context" => SessionTileRenderer.RenderContextKey(session, imageSize),
                "FiveHourLimit" => SessionTileRenderer.RenderLimitKey(LimitWindow.FiveHour, usage, imageSize),
                "WeeklyLimit" => SessionTileRenderer.RenderLimitKey(LimitWindow.Weekly, usage, imageSize),
                "FiveHourResetTime" => SessionTileRenderer.RenderResetKey(LimitWindow.FiveHour, usage, imageSize),
                "WeeklyResetTime" => SessionTileRenderer.RenderResetKey(LimitWindow.Weekly, usage, imageSize),
                _ => this.GetButtonImage(imageSize)
            };
        }

        private BitmapImage RenderStatusKey(ClaudeSession session, PluginImageSize imageSize)
        {
            this._drawnStatusKey = Shown(session);
            return SessionTileRenderer.RenderSlotTile(this.SlotNumber, session, TileAnimationService.Instance.CurrentBreath, imageSize);
        }

        public override void RunCommand(String actionParameter)
        {
            var store = SessionStore.Instance;
            var session = this.Session;
            switch (actionParameter)
            {
                case "ProjectStatus":
                case "Context":
                    store.OpenSession(session);
                    break;
                // Answers this slot's own request; while it waits for a choice (AskUserQuestion) the session is opened
                case "AllowOnce":
                    if (session?.IsWaitingForUser == true)
                    {
                        store.AnswerPermission(session, PermissionDecision.AllowOnce);
                    }
                    break;
                case "Stop":
                    store.Stop(session);
                    break;
                case "FiveHourLimit":
                case "WeeklyLimit":
                case "FiveHourResetTime":
                case "WeeklyResetTime":
                    store.TriggerManualSync();
                    break;
            }
        }
    }

    // Concrete slot folders for Logi Options+ (class names are part of saved keypad layouts)
    public class SessionSlot01DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 1;
        public SessionSlot01DynamicFolder() { this.DisplayName = "Session Slot 1"; }
    }

    public class SessionSlot02DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 2;
        public SessionSlot02DynamicFolder() { this.DisplayName = "Session Slot 2"; }
    }

    public class SessionSlot03DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 3;
        public SessionSlot03DynamicFolder() { this.DisplayName = "Session Slot 3"; }
    }

    public class SessionSlot04DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 4;
        public SessionSlot04DynamicFolder() { this.DisplayName = "Session Slot 4"; }
    }

    public class SessionSlot05DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 5;
        public SessionSlot05DynamicFolder() { this.DisplayName = "Session Slot 5"; }
    }

    public class SessionSlot06DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 6;
        public SessionSlot06DynamicFolder() { this.DisplayName = "Session Slot 6"; }
    }

    public class SessionSlot07DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 7;
        public SessionSlot07DynamicFolder() { this.DisplayName = "Session Slot 7"; }
    }

    public class SessionSlot08DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 8;
        public SessionSlot08DynamicFolder() { this.DisplayName = "Session Slot 8"; }
    }

    public class SessionSlot09DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 9;
        public SessionSlot09DynamicFolder() { this.DisplayName = "Session Slot 9"; }
    }

    public class SessionSlot10DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 10;
        public SessionSlot10DynamicFolder() { this.DisplayName = "Session Slot 10"; }
    }

    public class SessionSlot11DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 11;
        public SessionSlot11DynamicFolder() { this.DisplayName = "Session Slot 11"; }
    }

    public class SessionSlot12DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 12;
        public SessionSlot12DynamicFolder() { this.DisplayName = "Session Slot 12"; }
    }

    public class SessionSlot13DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 13;
        public SessionSlot13DynamicFolder() { this.DisplayName = "Session Slot 13"; }
    }

    public class SessionSlot14DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 14;
        public SessionSlot14DynamicFolder() { this.DisplayName = "Session Slot 14"; }
    }

    public class SessionSlot15DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 15;
        public SessionSlot15DynamicFolder() { this.DisplayName = "Session Slot 15"; }
    }

    public class SessionSlot16DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 16;
        public SessionSlot16DynamicFolder() { this.DisplayName = "Session Slot 16"; }
    }
}
