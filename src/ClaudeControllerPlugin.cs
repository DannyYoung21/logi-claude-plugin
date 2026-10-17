namespace Loupedeck.ClaudeControllerPlugin
{
    using System;
    using Loupedeck.ClaudeControllerPlugin.Models;
    using Loupedeck.ClaudeControllerPlugin.Services;

    public class ClaudeControllerPlugin : Plugin
    {
        private const String ModelLimitNameSetting = "modelLimitName";
        private const String SlotOrderSetting = "slotOrder";
        private const String HooksDisabledSetting = "hooksDisabled";

        public static ClaudeControllerPlugin Instance { get; private set; }

        public override Boolean UsesApplicationApiOnly => true;
        public override Boolean HasNoApplication => true;

        public ClaudeControllerPlugin()
        {
            Instance = this;
            PluginLog.Init(this.Log);
        }

        public override void Load()
        {
            PluginLog.Info("Claude Controller is loading...");

            // Logi reports no uninstall, so the hooks go in on every start unless the user removed them on purpose
            if (!this.HooksDisabled)
            {
                HookInstaller.Install();
            }
            HookServer.Instance.Start();
            SessionStore.Instance.Start();
            UsageLimitService.Instance.Start();
            _ = TileAnimationService.Instance;
            this.RestoreSlotOrder();
        }

        public override void Unload()
        {
            PluginLog.Info("Claude Controller unloading...");
            HookServer.Instance.Dispose();
            SessionStore.Instance.Dispose();
            UsageLimitService.Instance.Dispose();
            TileAnimationService.Instance.Dispose();
        }

        // ---------------------------------------------------------------- Settings

        // Actions load before Load(), so whoever comes first restores the saved slot order
        public void RestoreSlotOrder()
        {
            if (this.TryGetPluginSetting(SlotOrderSetting, out var savedOrder) &&
                Enum.TryParse<SlotOrderStrategy>(savedOrder, ignoreCase: true, out var strategy))
            {
                SessionStore.Instance.SetSlotOrder(strategy);
            }
        }

        public void SaveSlotOrder(SlotOrderStrategy strategy) =>
            this.SetPluginSetting(SlotOrderSetting, strategy.ToString().ToLowerInvariant(), false);

        public Boolean HooksDisabled
        {
            get => this.TryGetPluginSetting(HooksDisabledSetting, out var value) && value == "true";
            set => this.SetPluginSetting(HooksDisabledSetting, value ? "true" : "false", false);
        }

        // Last seen model with its own weekly limit ("Fable"), so the Options+ action names are right before the first poll
        public String LoadModelLimitName() =>
            this.TryGetPluginSetting(ModelLimitNameSetting, out var name) && !String.IsNullOrWhiteSpace(name) ? name : String.Empty;

        public void SaveModelLimitName(String name) => this.SetPluginSetting(ModelLimitNameSetting, name ?? String.Empty, false);
    }
}
