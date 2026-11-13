namespace Loupedeck.ClaudeControllerPlugin.Actions
{
    using System;
    using Loupedeck.ClaudeControllerPlugin.Helpers;
    using Loupedeck.ClaudeControllerPlugin.Models;
    using Loupedeck.ClaudeControllerPlugin.Services;

    // Press, then two slot tiles: their sessions trade places. Switches the slot order to Sticky, the only one that keeps
    // an order made by hand. Pressing it again cancels.
    public class SwapSlotsCommand : PluginDynamicCommand
    {
        public SwapSlotsCommand()
            : base(displayName: "Swap Slots", description: "Press, then tap two slot tiles: their sessions trade places. Switches Slot Order to Sticky, which keeps your order. Press again to cancel.", groupName: "SESSION CONTROLS")
        {
            SessionStore.Instance.OnSwapChanged += () => this.ActionImageChanged();
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var store = SessionStore.Instance;
            return SessionTileRenderer.RenderSwapKey(store.SwapActive, store.SwapFirstSlot, imageSize);
        }

        protected override void RunCommand(String actionParameter)
        {
            var store = SessionStore.Instance;
            if (!store.SwapActive && store.SlotOrder != SlotOrderStrategy.Sticky)
            {
                store.SetSlotOrder(SlotOrderStrategy.Sticky);
                ClaudeControllerPlugin.Instance?.SaveSlotOrder(SlotOrderStrategy.Sticky);
            }
            store.ToggleSwap();
        }
    }
}
