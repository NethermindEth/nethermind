// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Block-level access list as decoded from the network or storage. Optimised for reading:
/// account lookup is O(1) via hash map. Iteration order matches insertion order — the decoder
/// inserts accounts in the order they arrive on the wire (which it has already validated as
/// sorted by address), so enumerating <see cref="AccountChanges"/> walks accounts in sorted
/// address order. The only mutation permitted is the prestate load.
/// </summary>
public class ReadOnlyBlockAccessList : IEquatable<ReadOnlyBlockAccessList>
{
    private readonly Dictionary<Address, ReadOnlyAccountChanges> _accountChanges;
    private readonly ReadOnlyAccountChanges[] _orderedAccounts;

    [JsonIgnore]
    public int ItemCount { get; }

    /// <summary>
    /// Sum of <see cref="ReadOnlyAccountChanges.StorageReads"/> lengths across all accounts.
    /// Cached once at construction so per-block validation doesn't re-walk the BAL.
    /// </summary>
    [JsonIgnore]
    public int TotalStorageReads { get; }

    /// <summary>
    /// Sum of per-slot change-event counts (<c>StorageChanges[i].Changes.Length</c>) across all
    /// accounts. Bounds the total (slot, tx) pairs the generator can produce in a valid block.
    /// </summary>
    [JsonIgnore]
    public int TotalStorageChangeEvents { get; }

    /// <summary>
    /// Keccak of the BAL's wire (RLP) encoding, or <c>null</c> when the instance was synthesised
    /// in-process rather than decoded from the wire.
    /// </summary>
    /// <remarks>
    /// Populated by <see cref="Nethermind.Serialization.Rlp.Eip7928.BlockAccessListDecoder"/>
    /// so the consensus-side hash check in <c>BlockValidator</c> avoids re-hashing the same
    /// bytes once per block. Immutable for the lifetime of the BAL.
    /// </remarks>
    [JsonIgnore]
    public Hash256? WireHash { get; }

    /// <summary>
    /// Per-lane row counts indexed by wire <c>Index</c>: how many balance / nonce / code / storage
    /// changes the BAL declares at each block-access index. Sized to <c>maxIndex + 1</c> across
    /// every lane (length 0 when the BAL has no indexed changes at all).
    /// </summary>
    /// <remarks>
    /// Computed once in the constructor over the same pass that fills the address dictionary so
    /// <see cref="Nethermind.Consensus.Processing.BlockAccessListValidationIndex.Build"/> can
    /// pre-size lane buffers without a second walk of every change. Out-of-range indices
    /// (<c>Index &gt; lastIndex</c>) are ignored at build time by copying only the prefix that
    /// fits into the validation index's row count.
    /// </remarks>
    [JsonIgnore]
    public LaneRowCounts RowCounts { get; }

    public EnumerableWithCount<ReadOnlyAccountChanges> AccountChanges
        => new(_accountChanges.Values, _accountChanges.Count);

    /// <summary>
    /// Span over the address-sorted accounts (same data as <see cref="AccountChanges"/>, but
    /// skips the dictionary's enumerator for hot walks).
    /// </summary>
    [JsonIgnore]
    public ReadOnlySpan<ReadOnlyAccountChanges> AccountChangesAsSpan => _orderedAccounts;

    public bool HasAccount(Address address) => _accountChanges.ContainsKey(address);

    public ReadOnlyAccountChanges? GetAccountChanges(Address address)
        => _accountChanges.TryGetValue(address, out ReadOnlyAccountChanges? value) ? value : null;

    public ReadOnlyBlockAccessList() : this([], 0) { }

    /// <summary>
    /// Constructs a read-only BAL from accounts already in sorted address order (as guaranteed
    /// by the RLP decoder). The dictionary preserves insertion order during iteration provided
    /// no entries are removed — and this type is immutable post-construction except for prestate
    /// loading, which only mutates per-account fields, so the sorted iteration is preserved.
    /// </summary>
    public ReadOnlyBlockAccessList(ReadOnlyAccountChanges[] orderedAccounts, int itemCount)
        : this(orderedAccounts, itemCount, wireHash: null) { }

    /// <inheritdoc cref="ReadOnlyBlockAccessList(ReadOnlyAccountChanges[], int)"/>
    /// <remarks>
    /// Decoder-only overload that caches the keccak of the BAL's wire RLP encoding on the
    /// instance — see <see cref="WireHash"/>.
    /// </remarks>
    public ReadOnlyBlockAccessList(ReadOnlyAccountChanges[] orderedAccounts, int itemCount, Hash256? wireHash)
    {
        _orderedAccounts = orderedAccounts;
        _accountChanges = new Dictionary<Address, ReadOnlyAccountChanges>(orderedAccounts.Length);
        int totalReads = 0;
        int totalChangeEvents = 0;
        foreach (ReadOnlyAccountChanges a in orderedAccounts)
        {
            _accountChanges.Add(a.Address, a);
            totalReads += a.StorageReads.Length;
            foreach (ReadOnlySlotChanges slot in a.StorageChanges) totalChangeEvents += slot.Changes.Length;
        }
        RowCounts = BuildRowCounts(orderedAccounts);
        ItemCount = itemCount;
        TotalStorageReads = totalReads;
        TotalStorageChangeEvents = totalChangeEvents;
        WireHash = wireHash;
    }

    // Hard ceiling on per-lane cache length. EIP-7928 caps a block at MaxTxs transactions; valid
    // wire indices fall in [0, MaxTxs + 1]. Indices above this cap can never be matched against a
    // valid validation row count, so caching them would waste arbitrary memory on malformed input.
    // Build() will silently drop such entries via its own range check (matching prior behaviour).
    private const int MaxCachedRows = Eip7928Constants.MaxTxs + 2;

    private static LaneRowCounts BuildRowCounts(ReadOnlyAccountChanges[] orderedAccounts)
    {
        // Each per-account change list is already sorted by Index (validated by the decoder), so
        // the last entry holds the lane-local maximum; one cheap peek per account avoids a second
        // walk over every change to size the per-lane counts arrays.
        uint maxIndex = 0;
        bool hasInRangeChange = false;
        foreach (ReadOnlyAccountChanges a in orderedAccounts)
        {
            UpdateMaxIndex(a.BalanceChanges, ref maxIndex, ref hasInRangeChange);
            UpdateMaxIndex(a.NonceChanges, ref maxIndex, ref hasInRangeChange);
            UpdateMaxIndex(a.CodeChanges, ref maxIndex, ref hasInRangeChange);
            foreach (ReadOnlySlotChanges slot in a.StorageChanges)
            {
                UpdateMaxIndex(slot.Changes, ref maxIndex, ref hasInRangeChange);
            }
        }

        if (!hasInRangeChange) return LaneRowCounts.Empty;

        int length = (int)maxIndex + 1;
        int[] balance = new int[length];
        int[] nonce = new int[length];
        int[] code = new int[length];
        int[] storage = new int[length];
        uint cap = maxIndex;
        foreach (ReadOnlyAccountChanges a in orderedAccounts)
        {
            foreach (BalanceChange c in a.BalanceChanges)
            {
                if (c.Index <= cap) balance[(int)c.Index]++;
            }

            foreach (NonceChange c in a.NonceChanges)
            {
                if (c.Index <= cap) nonce[(int)c.Index]++;
            }

            foreach (CodeChange c in a.CodeChanges)
            {
                if (c.Index <= cap) code[(int)c.Index]++;
            }

            foreach (ReadOnlySlotChanges slot in a.StorageChanges)
            {
                foreach (StorageChange c in slot.Changes)
                {
                    if (c.Index <= cap) storage[(int)c.Index]++;
                }
            }
        }
        return new LaneRowCounts(balance, nonce, code, storage);
    }

    private static void UpdateMaxIndex<TChange>(TChange[] changes, ref uint maxIndex, ref bool hasInRangeChange)
        where TChange : struct, IIndexedChange
    {
        // Scan back from the end since changes are sorted by Index ascending: we want the largest
        // in-range entry. Out-of-range entries (above MaxCachedRows - 1) are skipped — they can't
        // be matched in any valid validation row count, so they're not worth caching.
        for (int i = changes.Length - 1; i >= 0; i--)
        {
            uint idx = changes[i].Index;
            if (idx >= MaxCachedRows) continue;
            hasInRangeChange = true;
            if (idx > maxIndex) maxIndex = idx;
            return;
        }
    }

    /// <summary>
    /// Precomputed per-row counts of balance / nonce / code / storage changes, indexed by wire
    /// <c>Index</c>. All four arrays share the same length (<c>maxIndex + 1</c>) for the BAL, or
    /// are all empty for a BAL with no indexed changes.
    /// </summary>
    public readonly record struct LaneRowCounts(int[] Balance, int[] Nonce, int[] Code, int[] Storage)
    {
        public static LaneRowCounts Empty { get; } = new([], [], [], []);

        public int Length => Balance.Length;
    }

    public bool Equals(ReadOnlyBlockAccessList? other)
    {
        if (other is null) return false;
        if (_accountChanges.Count != other._accountChanges.Count) return false;
        foreach (KeyValuePair<Address, ReadOnlyAccountChanges> kv in _accountChanges)
        {
            if (!other._accountChanges.TryGetValue(kv.Key, out ReadOnlyAccountChanges? otherAcc)) return false;
            if (!kv.Value.Equals(otherAcc)) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is ReadOnlyBlockAccessList other && Equals(other);

    public override int GetHashCode() => _accountChanges.Count.GetHashCode();

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine($"ReadOnlyBlockAccessList (Accounts={_accountChanges.Count})");
        foreach (ReadOnlyAccountChanges ac in _accountChanges.Values)
        {
            sb.Append("  ").AppendLine(ac.ToString());
        }
        return sb.ToString();
    }
}
