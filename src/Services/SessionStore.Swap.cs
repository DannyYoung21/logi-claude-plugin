namespace Loupedeck.ClaudeControllerPlugin.Services
{
    using System;
    using Loupedeck.ClaudeControllerPlugin.Models;

    // Swap Slots: press the key, then two slot tiles, and their sessions trade places. Only Sticky keeps an order made
    // by hand, so a swap switches to Sticky (Recency and Settled would sort it away again at once).
    public partial class SessionStore
    {
        // A swap nobody finishes ends by itself, so the slot tiles don't stay in picking mode
        private static readonly TimeSpan SwapTimeout = TimeSpan.FromSeconds(20);

        private Boolean _swapActive;
        private Int32? _swapFirst;
        private DateTime _swapSince;

        // The Swap Slots key and the slot tiles redraw on this
        public event Action OnSwapChanged;

        public Boolean SwapActive { get { lock (this._lock) { return this._swapActive; } } }

        // The slot picked first, until the second one is pressed
        public Int32? SwapFirstSlot { get { lock (this._lock) { return this._swapActive ? this._swapFirst : null; } } }

        // Starts a swap, or cancels the one that is running
        public void ToggleSwap()
        {
            Boolean started;
            lock (this._lock)
            {
                started = !this._swapActive;
                this._swapActive = started;
                this._swapFirst = null;
                this._swapSince = DateTime.UtcNow;
            }
            Log(started ? "[SLOTS] Swap started" : "[SLOTS] Swap cancelled");
            this.OnSwapChanged?.Invoke();
        }

        // A slot tile was pressed. True if the press belongs to a running swap: the slot's folder must not open then.
        public Boolean PickSwapSlot(Int32 slot)
        {
            String swapped = null;
            lock (this._lock)
            {
                if (!this._swapActive)
                {
                    return false;
                }
                this._swapSince = DateTime.UtcNow;
                if (this._swapFirst == null)
                {
                    this._swapFirst = slot;
                }
                else if (this._swapFirst == slot)
                {
                    // The same tile again takes the choice back
                    this._swapFirst = null;
                }
                else
                {
                    var first = this._swapFirst.Value;
                    this._slotAssignment.Swap(first, slot);
                    swapped = $"[SLOTS] Swapped slot {first} and slot {slot}";
                    this._swapActive = false;
                    this._swapFirst = null;
                }
            }
            if (swapped != null)
            {
                Log(swapped);
                this.Recompute(force: true);
            }
            this.OnSwapChanged?.Invoke();
            return true;
        }

        // Ends the swap when the slot order leaves Sticky or nobody picked a slot for a while
        private void CancelSwap(String reason)
        {
            lock (this._lock)
            {
                if (!this._swapActive)
                {
                    return;
                }
                this._swapActive = false;
                this._swapFirst = null;
            }
            Log($"[SLOTS] Swap cancelled ({reason})");
            this.OnSwapChanged?.Invoke();
        }

        private void ExpireSwap()
        {
            Boolean expired;
            lock (this._lock)
            {
                expired = this._swapActive && DateTime.UtcNow - this._swapSince > SwapTimeout;
            }
            if (expired)
            {
                this.CancelSwap("timed out");
            }
        }

        private void CancelSwapUnlessSticky(SlotOrderStrategy strategy)
        {
            if (strategy != SlotOrderStrategy.Sticky)
            {
                this.CancelSwap($"slot order set to {strategy}");
            }
        }
    }
}
