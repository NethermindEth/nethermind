// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>The slots a run of consecutive covered blocks wrote, newest block first, so that a trace of the block after
/// them reads a slot those blocks touched from memory and only a slot they never touched from the parent state. It
/// holds no accounts: the trace reads every account from the parent state anyway, as the basis the block's own
/// writes are laid over. Each node holds one block flattened to its final values and points at the node before it;
/// a node never changes once published, so any number of traces may read a chain while a newer node is added ahead
/// of it.
/// The rows hold what the transactions wrote; what the block wrote after them (withdrawals, the end-of-block system
/// calls, a reward) is not in them, so an address any block of the chain can have written afterwards is refused by
/// the whole chain and always read from the parent state, where that block's real value is. The refusal is
/// cumulative: an address excluded by one block is refused by every chain built on it, because an older node holding
/// it would otherwise answer with a value that block has since changed.
/// Only a proof-of-stake block of a chain whose processing <see cref="PostTransactionWriters.Describes"/> accepts is
/// chained, so no uncle, non-zero reward or plugin of its own reaches an address
/// <see cref="PostTransactionWriters"/> cannot name.</summary>
internal sealed class RangeOverlay : IStorageReadOverlay
{
    private readonly RangeOverlay? _older;
    private readonly HashSet<AddressAsKey> _wiped = [];
    private readonly Dictionary<StorageCell, UInt256> _slots = [];
    private readonly HashSet<AddressAsKey> _storageAccounts = [];
    private readonly HashSet<AddressAsKey> _refused;

    private RangeOverlay(RangeOverlay? older, ulong lastBlock, Hash256 lastHash, IReadOnlySet<AddressAsKey> excluded)
    {
        _older = older;
        _refused = older is null ? [.. excluded] : [.. older._refused, .. excluded];
        LastBlock = lastBlock;
        LastHash = lastHash;
        Length = (older?.Length ?? 0) + 1;
        // Every node copies the refusal, so the chain holds it once per block: charged in full here, or the budget
        // the flag points at would under-count what the chain costs as it grows.
        Entries = (older?.Entries ?? 0) + _refused.Count;
    }

    public ulong LastBlock { get; }

    public Hash256 LastHash { get; }

    /// <summary>Blocks in the chain.</summary>
    public int Length { get; }

    /// <summary>Wipe and slot entries held by the whole chain, what it costs to keep.</summary>
    public long Entries { get; private set; }

    /// <summary>A new chain head: <paramref name="block"/>, folded through its last transaction, in front of
    /// <paramref name="older"/>.</summary>
    /// <param name="excluded">Addresses this block may have written after its transactions. They are refused by the
    /// whole chain from here on, this block's own transaction writes to them included.</param>
    public static RangeOverlay Extend(RangeOverlay? older, MidBlockOverlay block, Hash256 blockHash, IReadOnlySet<AddressAsKey> excluded)
    {
        RangeOverlay node = new(older, block.Block, blockHash, excluded);
        Dictionary<AddressAsKey, MidBlockOverlay.AccountOverlay>.Enumerator accounts = block.Accounts;
        while (accounts.MoveNext())
        {
            MidBlockOverlay.AccountOverlay account = accounts.Current.Value;
            bool gone = account.Emptied && !account.Exists;
            if ((gone || account.StorageClearedAt != MidBlockOverlay.NeverCleared) && !node._refused.Contains(accounts.Current.Key))
            {
                node._wiped.Add(accounts.Current.Key);
            }
        }

        Dictionary<StorageCell, MidBlockOverlay.StorageWrite>.Enumerator writes = block.Writes;
        while (writes.MoveNext())
        {
            StorageCell cell = writes.Current.Key;
            if (node._refused.Contains(cell.Address) || !block.TryGetStorage(cell, out UInt256 value)) continue;

            node._slots[cell] = value;
            node._storageAccounts.Add(cell.Address);
        }

        node.Entries += node._wiped.Count + node._slots.Count;
        return node;
    }

    public bool TryGetStorage(Address address, in UInt256 index, out UInt256 value)
    {
        if (_refused.Contains(address))
        {
            value = UInt256.Zero;
            return false;
        }

        StorageCell cell = new(address, index);
        for (RangeOverlay? node = this; node is not null; node = node._older)
        {
            if (node._slots.TryGetValue(cell, out value)) return true;
            if (node._wiped.Contains(address))
            {
                value = UInt256.Zero;
                return true;
            }
        }

        value = UInt256.Zero;
        return false;
    }

    public bool HasStorage(Address address)
    {
        if (_refused.Contains(address)) return false;

        for (RangeOverlay? node = this; node is not null; node = node._older)
        {
            if (node._storageAccounts.Contains(address) || node._wiped.Contains(address)) return true;
        }

        return false;
    }
}
