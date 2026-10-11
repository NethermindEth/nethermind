// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>The slots of stored blocks named by gossip messages, each decoded once per root and only within the caller's per-slot budget.</summary>
internal sealed class StoredBlockSlots(BeaconChainStore store, SlotClock slotClock, ILogger logger, string cacheName)
{
    private const int CacheSize = 1024;

    // A stored block never changes, so its slot is decoded once per root.
    private readonly LruCache<Hash256, ulong> _slots = new(CacheSize, cacheName);

    /// <summary>Reads the slot of the stored block <paramref name="root"/>, taking from <paramref name="budget"/> only for a decode.</summary>
    /// <returns><c>false</c> when the block is not stored, is unreadable or the budget is spent; the caller then skips its rule, which the state transition or fork choice asserts again.</returns>
    /// <remarks>A cached root was held and passed validation, so it is answered without a store lookup.</remarks>
    public bool TryRead(Hash256 root, PerSlotBudget budget, out ulong slot)
    {
        if (_slots.TryGet(root, out slot))
        {
            return true;
        }

        // A root not held costs a key lookup only, so it never spends the budget.
        if (!store.HasBlock(root) || !budget.TryTake(slotClock.CurrentSlot))
        {
            return false;
        }

        try
        {
            if (!store.TryGetForkedBlock(root, out ForkedSignedBeaconBlock? stored))
            {
                return false;
            }

            slot = stored.Slot;
        }
        // A read failure must not turn a valid message into an SSZ REJECT in the router.
        catch (Exception e)
        {
            if (logger.IsWarn) logger.Warn($"Unreadable stored block {root} named by a gossip message: {e.Message}");
            return false;
        }

        _slots.Set(root, slot);
        return true;
    }
}
