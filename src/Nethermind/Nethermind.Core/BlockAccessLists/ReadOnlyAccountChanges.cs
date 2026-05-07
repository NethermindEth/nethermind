// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-account changes from a decoded BAL. Index-keyed change families are stored as plain
/// arrays kept sorted by <see cref="IIndexedChange.Index"/> (the decoder validates ordering),
/// so reads can binary-search via <see cref="System.MemoryExtensions"/>. Storage changes are
/// kept in two parallel structures: a hash map for O(1) <see cref="TryGetSlotChanges"/>
/// lookups (used during EVM execution) and an array sorted by slot key for ordered iteration
/// (used by the cache prewarmer's sorted-merge with <see cref="StorageReads"/>). The only
/// mutations permitted are those performed by prestate loading (additions at index <c>-1</c>
/// + the related flags); both representations are updated in lockstep when prestate loading
/// adds a previously read-only slot.
/// </summary>
public class ReadOnlyAccountChanges : IEquatable<ReadOnlyAccountChanges>
{
    [JsonConverter(typeof(AddressConverter))]
    public Address Address { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ReadOnlySlotChanges[] StorageChanges
    {
        get
        {
            WaitForPrestate();
            EnsureSorted();
            return _orderedStorageChanges;
        }
    }

    /// <summary>Slot keys, sorted ascending — exposed as <see cref="IList{T}"/> for indexed access.</summary>
    [JsonIgnore]
    public IList<UInt256> ChangedSlots
    {
        get
        {
            WaitForPrestate();
            EnsureSorted();
            return _changedSlots;
        }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public UInt256[] StorageReads => _storageReads;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public BalanceChange[] BalanceChanges => _balanceChanges;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public NonceChange[] NonceChanges => _nonceChanges;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CodeChange[] CodeChanges => _codeChanges;

    [JsonIgnore]
    public bool ExistedBeforeBlock { get; private set; }

    [JsonIgnore]
    public bool EmptyBeforeBlock { get; private set; }

    [JsonIgnore]
    public bool AccountChanged { get; private set; }

    private readonly Dictionary<UInt256, ReadOnlySlotChanges> _storageChanges;
    private ReadOnlySlotChanges[] _orderedStorageChanges;
    private UInt256[] _changedSlots;
    private readonly UInt256[] _storageReads;
    private BalanceChange[] _balanceChanges;
    private NonceChange[] _nonceChanges;
    private CodeChange[] _codeChanges;

    // Set by LoadPreStateStorage when it adds a new slot to _storageChanges; cleared by
    // EnsureSorted under _sortLock after a single Array.Sort rebuild populates the parallel
    // sorted arrays. Volatile-paired with the array writes so a reader observing dirty=false
    // also sees the new array references.
    private bool _sortedDirty;
    private readonly object _sortLock = new();

    /// <summary>
    /// Per-account gate that lets parallel transaction workers wait for prestate loading to
    /// complete before reading prestate-dependent state (balance/nonce/code/storage arrays plus
    /// the <see cref="ExistedBeforeBlock"/>/<see cref="EmptyBeforeBlock"/>/<see cref="AccountChanged"/>
    /// flags). Enabled by <see cref="EnablePrestateGate"/> in
    /// <c>BlockAccessListManager.PrepareForProcessing</c> when parallel execution is on, and
    /// signaled by <see cref="SignalPrestateLoaded"/> after the loader has finished mutating
    /// this account in <c>LoadPreStateToSuggestedBlockAccessList</c>. Null when no parallel
    /// loading is expected — read methods then short-circuit without waiting.
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> ensures the loader's
    /// thread isn't hijacked to run any future <c>await</c> continuations attached by callers.
    /// </summary>
    private TaskCompletionSource? _prestateGate;

    public ReadOnlyAccountChanges(
        Address address,
        ReadOnlySlotChanges[] storageChanges,
        UInt256[] storageReads,
        BalanceChange[] balanceChanges,
        NonceChange[] nonceChanges,
        CodeChange[] codeChanges)
    {
        Address = address;
        _orderedStorageChanges = storageChanges;
        _storageChanges = new Dictionary<UInt256, ReadOnlySlotChanges>(storageChanges.Length);
        _changedSlots = new UInt256[storageChanges.Length];
        for (int i = 0; i < storageChanges.Length; i++)
        {
            ReadOnlySlotChanges sc = storageChanges[i];
            _storageChanges.Add(sc.Key, sc);
            _changedSlots[i] = sc.Key;
        }
        _storageReads = storageReads;
        _balanceChanges = balanceChanges;
        _nonceChanges = nonceChanges;
        _codeChanges = codeChanges;
    }

    public ReadOnlyAccountChanges(Address address) : this(address, [], [], [], [], []) { }

    public bool TryGetSlotChanges(UInt256 key, [NotNullWhen(true)] out ReadOnlySlotChanges? slotChanges)
    {
        WaitForPrestate();
        return _storageChanges.TryGetValue(key, out slotChanges);
    }

    public BalanceChange? BalanceChangeAtIndex(uint index)
    {
        WaitForPrestate();
        return GetExact(_balanceChanges, index);
    }

    public NonceChange? NonceChangeAtIndex(uint index)
    {
        WaitForPrestate();
        return GetExact(_nonceChanges, index);
    }

    public CodeChange? CodeChangeAtIndex(uint index)
    {
        WaitForPrestate();
        return GetExact(_codeChanges, index);
    }

    public bool HasSlotChangesAtIndex(uint index)
    {
        foreach (ReadOnlySlotChanges slotChanges in StorageChanges)
        {
            if (HasExactIndex(slotChanges.Changes, index)) return true;
        }
        return false;
    }

    public IEnumerable<SlotChangeAtIndex> SlotChangesAtIndex(uint index)
    {
        foreach (ReadOnlySlotChanges slotChanges in StorageChanges)
        {
            StorageChange? change = GetExact(slotChanges.Changes, index);
            if (change is not null)
            {
                yield return new SlotChangeAtIndex(slotChanges.Key, change.Value);
            }
        }
    }

    /// <summary>True iff this account has no balance/nonce/code/slot change at <paramref name="index"/>.
    /// Storage reads are not changes; this only inspects mutating entries.</summary>
    public bool HasNoChangesAtIndex(uint index)
        => BalanceChangeAtIndex(index) is null
        && NonceChangeAtIndex(index) is null
        && CodeChangeAtIndex(index) is null
        && !HasSlotChangesAtIndex(index);

    /// <summary>Most recent balance strictly before <paramref name="blockAccessIndex"/>; null if none.</summary>
    public UInt256? GetBalance(uint blockAccessIndex)
    {
        WaitForPrestate();
        return TryGetLastBefore(_balanceChanges, blockAccessIndex, out BalanceChange last) ? last.Value : null;
    }

    public UInt256? GetNonce(uint blockAccessIndex)
    {
        WaitForPrestate();
        return TryGetLastBefore(_nonceChanges, blockAccessIndex, out NonceChange last) ? last.Value : null;
    }

    public byte[] GetCode(uint blockAccessIndex)
    {
        WaitForPrestate();
        return TryGetLastBefore(_codeChanges, blockAccessIndex, out CodeChange last) ? last.Code : [];
    }

    public ValueHash256 GetCodeHash(uint blockAccessIndex)
    {
        WaitForPrestate();
        return TryGetLastBefore(_codeChanges, blockAccessIndex, out CodeChange last) ? last.CodeHash : Keccak.OfAnEmptyString.ValueHash256;
    }

    public bool AccountExists(uint blockAccessIndex)
    {
        WaitForPrestate();
        if (ExistedBeforeBlock)
        {
            return true;
        }

        // Skip the prestate entry at PrestateIndex (added by LoadPreStateBalance / LoadPreStateNonce
        // for every account in the BAL, including accounts that did not yet exist). Existence
        // by `blockAccessIndex` requires a real tx-level balance or nonce change at index in
        // [0, blockAccessIndex) — only that proves the account was created in a prior tx.
        // Prestate is sorted first via PrestateAwareIndexComparer, so it appears before all real entries.
        foreach (NonceChange change in _nonceChanges)
        {
            if (change.Index == Eip7928Constants.PrestateIndex) continue;
            if (change.Index < blockAccessIndex) return true;
            break;
        }

        foreach (BalanceChange change in _balanceChanges)
        {
            if (change.Index == Eip7928Constants.PrestateIndex) continue;
            if (change.Index < blockAccessIndex) return true;
            break;
        }

        // EIP-7702 / EIP-7928: a code-only modification (e.g. SetCode) at a prior tx also
        // implies existence at this index, but only when the resulting code is non-empty.
        CodeChange? lastCodeChange = null;
        foreach (CodeChange change in _codeChanges)
        {
            if (change.Index == Eip7928Constants.PrestateIndex) continue;
            if (change.Index >= blockAccessIndex) break;
            lastCodeChange = change;
        }

        if (lastCodeChange is { Code.Length: > 0 })
        {
            return true;
        }

        return false;
    }

    public bool Equals(ReadOnlyAccountChanges? other)
    {
        if (other is null) return false;
        if (Address != other.Address) return false;
        if (_storageChanges.Count != other._storageChanges.Count) return false;
        foreach (KeyValuePair<UInt256, ReadOnlySlotChanges> kv in _storageChanges)
        {
            if (!other._storageChanges.TryGetValue(kv.Key, out ReadOnlySlotChanges? otherVal) || !kv.Value.Equals(otherVal))
                return false;
        }
        return _storageReads.SequenceEqual(other._storageReads)
            && _balanceChanges.SequenceEqual(other._balanceChanges)
            && _nonceChanges.SequenceEqual(other._nonceChanges)
            && _codeChanges.SequenceEqual(other._codeChanges);
    }

    public override bool Equals(object? obj) => obj is ReadOnlyAccountChanges other && Equals(other);

    public override int GetHashCode() => Address.GetHashCode();

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.Append(Address);
        if (BalanceChanges.Length > 0) sb.Append($" balance=[{string.Join(", ", BalanceChanges)}]");
        if (NonceChanges.Length > 0) sb.Append($" nonce=[{string.Join(", ", NonceChanges)}]");
        if (CodeChanges.Length > 0) sb.Append($" code=[{string.Join(", ", CodeChanges)}]");
        if (StorageChanges.Length > 0) sb.Append($" storage=[{string.Join(", ", (object[])StorageChanges)}]");
        if (StorageReads.Length > 0) sb.Append($" reads=[{string.Join(", ", StorageReads)}]");
        return sb.ToString();
    }

    // === Mutations limited to LoadPreStateToSuggestedBlockAccessList (called from BlockAccessListManager) ===
    // Prestate is keyed at index -1, smaller than every real change, so we prepend (one realloc)
    // to preserve the sorted-by-index invariant.

    public void LoadPreStateBalance(UInt256 balance) => _balanceChanges = [new BalanceChange(Eip7928Constants.PrestateIndex, balance), .. _balanceChanges];
    public void LoadPreStateNonce(ulong nonce) => _nonceChanges = [new NonceChange(Eip7928Constants.PrestateIndex, nonce), .. _nonceChanges];
    public void LoadPreStateCode(byte[] code) => _codeChanges = [new CodeChange(Eip7928Constants.PrestateIndex, code), .. _codeChanges];

    public void LoadPreStateStorage(UInt256 slot, UInt256 value)
    {
        if (!_storageChanges.TryGetValue(slot, out ReadOnlySlotChanges? slotChanges))
        {
            slotChanges = new ReadOnlySlotChanges(slot);
            _storageChanges.Add(slot, slotChanges);
            // Defer sorting: a single Array.Sort on first read is O(n log n) vs the prior
            // per-insert O(n) shifting that became O(n²) over many prestate loads.
            _sortedDirty = true;
        }
        slotChanges.LoadPreStateChange(new StorageChange(Eip7928Constants.PrestateIndex, value));
    }

    public IEnumerable<UInt256> GetSlotsForPreStateLoad()
    {
        foreach (UInt256 slot in _changedSlots) yield return slot;
        foreach (UInt256 r in _storageReads) yield return r;
    }

    public void SetExistedBeforeBlock(bool value) => ExistedBeforeBlock = value;
    public void SetEmptyBeforeBlock(bool value) => EmptyBeforeBlock = value;
    public void RecordWasChanged()
        => AccountChanged = _balanceChanges.Length > 0 || _nonceChanges.Length > 0 || _codeChanges.Length > 0 || _storageChanges.Count > 0;

    // === Prestate gate ===
    // Decoupling parallel tx execution from prestate loading: instead of blocking the whole
    // block on a serial pre-pass, PrepareForProcessing flips the gate on each account, slot 0
    // of the parallel loop loads prestate per-account and signals as soon as the account is
    // done, and worker reads above wait per-account. Idempotent: an already-set gate stays set
    // so a re-prepared block (already loaded) doesn't block workers.

    public void EnablePrestateGate()
        => _prestateGate ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void SignalPrestateLoaded() => _prestateGate?.TrySetResult();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WaitForPrestate()
    {
        TaskCompletionSource? gate = _prestateGate;
        if (gate is not null && !gate.Task.IsCompleted)
        {
            // GetResult (vs .Wait) so any future SetException surfaces unwrapped.
            gate.Task.GetAwaiter().GetResult();
        }
    }

    /// <summary>Rebuilds the parallel sorted arrays from <see cref="_storageChanges"/> in a single
    /// O(n log n) pass. Double-checked under <see cref="_sortLock"/> so that concurrent readers
    /// (post-prestate-gate) see exactly one rebuild. The trailing <see cref="Volatile.Write"/> on
    /// <see cref="_sortedDirty"/> publishes the new array references with release semantics, so
    /// any reader observing dirty=false also sees the updated arrays.</summary>
    private void EnsureSorted()
    {
        if (!Volatile.Read(ref _sortedDirty)) return;
        lock (_sortLock)
        {
            if (!_sortedDirty) return;
            int count = _storageChanges.Count;
            UInt256[] keys = new UInt256[count];
            ReadOnlySlotChanges[] ordered = new ReadOnlySlotChanges[count];
            int i = 0;
            foreach (KeyValuePair<UInt256, ReadOnlySlotChanges> kv in _storageChanges)
            {
                keys[i] = kv.Key;
                ordered[i] = kv.Value;
                i++;
            }
            Array.Sort(keys, ordered);
            _changedSlots = keys;
            _orderedStorageChanges = ordered;
            Volatile.Write(ref _sortedDirty, false);
        }
    }

    /// <summary>Returns the change with <c>Index == index</c> if any; otherwise null.</summary>
    private static T? GetExact<T>(T[] changes, uint index) where T : struct, IIndexedChange
    {
        ReadOnlySpan<T> span = changes;
        int idx = span.BinarySearch(new IndexKey<T>(index));
        return idx >= 0 ? span[idx] : null;
    }

    private static bool HasExactIndex<T>(T[] changes, uint index) where T : struct, IIndexedChange
    {
        ReadOnlySpan<T> span = changes;
        return span.BinarySearch(new IndexKey<T>(index)) >= 0;
    }

    /// <summary>Returns the entry with the largest Index strictly less than <paramref name="blockAccessIndex"/>, or false if none.</summary>
    private static bool TryGetLastBefore<T>(T[] changes, uint blockAccessIndex, out T last) where T : struct, IIndexedChange
    {
        ReadOnlySpan<T> span = changes;
        int idx = span.BinarySearch(new IndexKey<T>(blockAccessIndex));
        // (idx if found, ~idx otherwise) is the position of the first entry with Index >= target;
        // the last strictly-before is one step earlier.
        int lastBefore = (idx >= 0 ? idx : ~idx) - 1;
        if (lastBefore < 0)
        {
            last = default;
            return false;
        }
        last = span[lastBefore];
        return true;
    }
}
