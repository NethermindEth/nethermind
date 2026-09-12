// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Block-level access list as decoded from the network or storage. Optimised for reading:
/// account lookup is O(1) via hash map. Iteration order matches insertion order — the decoder
/// inserts accounts in the order they arrive on the wire (which it has already validated as
/// sorted by address), so enumerating <see cref="AccountChanges"/> walks accounts in sorted
/// address order. The declared content is immutable after construction; the one mutable member is
/// the lazily built, internally synchronised code index, so concurrent readers stay safe.
/// </summary>
public sealed class ReadOnlyBlockAccessList : IEquatable<ReadOnlyBlockAccessList>
{
    private readonly Dictionary<AddressAsKey, ReadOnlyAccountChanges> _accountChanges;
    private readonly ReadOnlyAccountChanges[] _orderedAccounts;
    private bool _codeChangesInitialized;
    private FrozenDictionary<ValueHash256, (uint Index, byte[] Code)>? _codeChangesByHash;
    private object? _codeChangesLock;

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
    /// Keccak of the BAL's wire (RLP) encoding, cached by the decoder so the consensus-side hash
    /// check avoids re-hashing per block. <c>null</c> for BALs synthesised in-process.
    /// </summary>
    [JsonIgnore]
    public Hash256? WireHash { get; }

    /// <summary>
    /// Address-sorted view over the BAL's accounts. <c>foreach</c> walks the underlying array
    /// via <see cref="ReadOnlySpan{T}"/> with no enumerator allocation; <see cref="ReadOnlyAccountChangesView.AsSpan"/>
    /// exposes the raw span for span-only call sites.
    /// </summary>
    public ReadOnlyAccountChangesView AccountChanges => new(_orderedAccounts);

    public bool HasAccount(Address address) => _accountChanges.ContainsKey(address);

    public ReadOnlyAccountChanges? GetAccountChanges(Address address)
        => _accountChanges.TryGetValue(address, out ReadOnlyAccountChanges? value) ? value : null;

    public ReadOnlyBlockAccessList() : this([], 0) { }

    /// <summary>
    /// Constructs a read-only BAL from accounts already in sorted address order (as guaranteed
    /// by the RLP decoder). The dictionary preserves insertion order during iteration provided
    /// no entries are removed — and this type is immutable post-construction, so the sorted
    /// iteration is preserved.
    /// </summary>
    public ReadOnlyBlockAccessList(ReadOnlyAccountChanges[] orderedAccounts, int itemCount)
        : this(orderedAccounts, itemCount, wireHash: null) { }

    public ReadOnlyBlockAccessList(ReadOnlyAccountChanges[] orderedAccounts, int itemCount, Hash256? wireHash)
    {
        _orderedAccounts = orderedAccounts;
        _accountChanges = new Dictionary<AddressAsKey, ReadOnlyAccountChanges>(orderedAccounts.Length);
        int totalReads = 0;
        int totalChangeEvents = 0;
        foreach (ReadOnlyAccountChanges a in orderedAccounts)
        {
            _accountChanges.Add(a.Address, a);
            totalReads += a.StorageReads.Length;
            foreach (ReadOnlySlotChanges slot in a.StorageChanges) totalChangeEvents += slot.Changes.Length;
        }
        ItemCount = itemCount;
        TotalStorageReads = totalReads;
        TotalStorageChangeEvents = totalChangeEvents;
        WireHash = wireHash;
    }

    /// <summary>Returns the shared code index for this BAL, built once and frozen.</summary>
    internal FrozenDictionary<ValueHash256, (uint Index, byte[] Code)>? GetCodeChangesByHash()
        => _orderedAccounts.Length == 0 ? null
            : Volatile.Read(ref _codeChangesInitialized) ? _codeChangesByHash : InitializeCodeChangesByHash();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private FrozenDictionary<ValueHash256, (uint Index, byte[] Code)>? InitializeCodeChangesByHash()
        => LazyInitializer.EnsureInitialized(ref _codeChangesByHash, ref _codeChangesInitialized, ref _codeChangesLock, BuildCodeChangesByHash);

    private FrozenDictionary<ValueHash256, (uint Index, byte[] Code)>? BuildCodeChangesByHash()
    {
        Dictionary<ValueHash256, (uint Index, byte[] Code)>? result = null;
        foreach (ReadOnlyAccountChanges account in _orderedAccounts)
        {
            foreach (CodeChange change in account.CodeChanges)
            {
                result ??= new(GenericEqualityComparer.GetOptimized<ValueHash256>());
                if (!result.TryGetValue(change.CodeHash, out (uint Index, byte[] Code) existing) || change.Index < existing.Index)
                {
                    result[change.CodeHash] = (change.Index, change.Code);
                }
            }
        }

        // Frozen both to enforce the shared-read contract and because the index is built once per
        // block and probed by every worker.
        return result?.ToFrozenDictionary(GenericEqualityComparer.GetOptimized<ValueHash256>());
    }

    public bool Equals(ReadOnlyBlockAccessList? other)
    {
        if (other is null) return false;
        if (_accountChanges.Count != other._accountChanges.Count) return false;
        foreach (KeyValuePair<AddressAsKey, ReadOnlyAccountChanges> kv in _accountChanges)
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
