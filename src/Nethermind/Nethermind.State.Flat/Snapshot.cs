// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.State.Flat.Collections;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Flat;

/// <summary>
/// Flat-value changes and final trie roots between <see cref="From"/> and <see cref="To"/>, backed by either the mutable
/// <see cref="SnapshotContent"/> or the sorted <see cref="SortedSnapshotContent"/>, selected by <see cref="IsSorted"/>.
/// </summary>
public class Snapshot : RefCountingDisposable
{
    private readonly StateId _from;
    private readonly StateId _to;
    private readonly IResourcePool _resourcePool;
    private readonly ResourcePool.Usage _usage;
    private readonly SnapshotContent? _mutable;
    private readonly SortedSnapshotContent? _sorted;
    private readonly bool _isSorted;
    // Counts sealed on first observation: ConcurrentDictionary.Count acquires every stripe lock, so
    // serving live counts would stall concurrent writers on each estimate, and the repository's
    // add/remove memory ledger needs the same value on both sides even if stragglers still write.
    // Sealing lazily (not in the constructor) keeps ResourcePool.CreateSnapshot's
    // construct-then-populate contract working: population always precedes the first count observation.
    // Boxed so publication is a single reference CAS.
    private StrongBox<SnapshotContentCounts>? _sealedCounts;
    private int _bloomPopulated;

    /// <param name="bufferPin">The pin protecting the trie node records this snapshot references; the snapshot takes ownership.</param>
    public Snapshot(in StateId from, in StateId to, SnapshotContent content, IResourcePool resourcePool, ResourcePool.Usage usage, INodeRecordBufferPin? bufferPin)
    {
        _from = from;
        _to = to;
        _mutable = content;
        _resourcePool = resourcePool;
        _usage = usage;
        _isSorted = false;
        BufferPin = bufferPin;
    }

    /// <inheritdoc cref="Snapshot(in StateId, in StateId, SnapshotContent, IResourcePool, ResourcePool.Usage, INodeRecordBufferPin?)"/>
    public Snapshot(in StateId from, in StateId to, SortedSnapshotContent content, IResourcePool resourcePool, ResourcePool.Usage usage, INodeRecordBufferPin? bufferPin)
    {
        _from = from;
        _to = to;
        _sorted = content;
        _resourcePool = resourcePool;
        _usage = usage;
        _isSorted = true;
        BufferPin = bufferPin;
    }

    public long EstimateMemory() => _isSorted ? _sorted!.EstimateMemory() : Counts.EstimateMemory();
    public long EstimateCompactedMemory() => _isSorted ? _sorted!.EstimateCompactedMemory() : Counts.EstimateCompactedMemory();

    /// <summary>
    /// The mutable content's counts, captured once on first access and immutable afterwards. Racing
    /// first observers may compute different candidates, but CompareExchange publishes exactly one,
    /// so every reader (including the repository's add and remove ledger sides) sees the same value.
    /// </summary>
    private SnapshotContentCounts Counts
    {
        get
        {
            StrongBox<SnapshotContentCounts>? sealedCounts = Volatile.Read(ref _sealedCounts);
            if (sealedCounts is null)
            {
                StrongBox<SnapshotContentCounts> candidate = new(_mutable!.CaptureCounts());
                sealedCounts = Interlocked.CompareExchange(ref _sealedCounts, candidate, null) ?? candidate;
            }

            return sealedCounts.Value;
        }
    }
    // Test-only observability (SnapshotCompactorTests); not consumed by production.
    internal ResourcePool.Usage Usage => _usage;

    public bool IsSorted => _isSorted;

    /// <summary>The pin on the trie node buffer generation that was active when this snapshot's first block started writing; released with the snapshot.</summary>
    /// <remarks>Protects this generation and later writes. Traversals into inherited older records rely on the reader's own pin.</remarks>
    internal INodeRecordBufferPin? BufferPin { get; }

    public StateId From => _from;
    public StateId To => _to;
    public IEnumerable<KeyValuePair<HashedKey<Address>, Account?>> Accounts => _isSorted ? _sorted!.Accounts : _mutable!.Accounts;
    public IEnumerable<KeyValuePair<HashedKey<Address>, bool>> SelfDestructedStorageAddresses => _isSorted ? _sorted!.SelfDestructedStorageAddresses : _mutable!.SelfDestructedStorageAddresses;
    public IEnumerable<KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?>> Storages => _isSorted ? _sorted!.Storages : _mutable!.Storages;

    /// <summary>Reachable state nodes written within this snapshot's range, borrowed from the buffer records the snapshot pins.</summary>
    /// <remarks>
    /// Nodes come in ascending <see cref="TreePath.CompareTo(in TreePath)"/> order: the walk visits each node before its subtree and children by ascending nibble,
    /// so a persisted snapshot is built from them without sorting. Records must not be used after the snapshot is released.
    /// </remarks>
    public IEnumerable<(TreePath Path, ulong Pointer, NodeRecord Record)> StateNodes => EnumerateStateNodes();

    /// <summary>Reachable storage nodes written within this snapshot's range, by ascending address hash and then as in <see cref="StateNodes"/>.</summary>
    /// <remarks>Records must not be used after the snapshot is released.</remarks>
    public IEnumerable<(Hash256 Address, TreePath Path, ulong Pointer, NodeRecord Record)> StorageNodes => EnumerateStorageNodes();

    public int AccountsCount => _isSorted ? _sorted!.AccountsCount : Counts.Accounts;
    public int StoragesCount => _isSorted ? _sorted!.StoragesCount : Counts.Storages;

    internal ulong StateRootPointer => _isSorted ? _sorted!.StateRootPointer : _mutable!.StateRootPointer;
    internal IEnumerable<KeyValuePair<Hash256, ulong>> StorageRoots => _isSorted ? _sorted!.StorageRoots : _mutable!.StorageRoots;

    internal bool TryGetStorageRoot(Hash256 address, out ulong pointer) => _isSorted
        ? _sorted!.StorageRoots.TryGetValue(address, out pointer)
        : _mutable!.StorageRoots.TryGetValue(address, out pointer);

    private IEnumerable<(TreePath Path, ulong Pointer, NodeRecord Record)> EnumerateStateNodes()
    {
        if (BufferPin is null || From.StateRoot == To.StateRoot) yield break;
        foreach ((TreePath Path, ulong Pointer, NodeRecord Record) node in BufferPin.Buffer.EnumerateNodes(StateRootPointer, BufferPin.WriteBoundary))
            yield return node;
    }

    private IEnumerable<(Hash256 Address, TreePath Path, ulong Pointer, NodeRecord Record)> EnumerateStorageNodes()
    {
        if (BufferPin is null) yield break;
        using ArrayPoolList<KeyValuePair<Hash256, ulong>> roots = new(0, StorageRoots);
        roots.Sort(static (a, b) => a.Key.CompareTo(b.Key));
        foreach ((Hash256 address, ulong root) in roots)
        {
            foreach ((TreePath path, ulong pointer, NodeRecord record) in BufferPin.Buffer.EnumerateNodes(root, BufferPin.WriteBoundary))
                yield return (address, path, pointer, record);
        }
    }

    /// <summary>Adds the node key hashes of the nodes written within this snapshot's range to the blooms of their buffer generations, once.</summary>
    /// <remarks>
    /// Called as the compactor worker first takes in a base snapshot, keeping it off the commit path. A node key hash missing from its
    /// bloom, of a snapshot not yet taken in or of a node no final root reaches, is fine: buffer compaction then keeps a superseded
    /// record, which only makes the compacted generation unnecessarily bigger.
    /// </remarks>
    internal void PopulateBloom()
    {
        if (BufferPin is not { Buffer: NodeRecordBuffer buffer } pin || Interlocked.Exchange(ref _bloomPopulated, 1) != 0) return;

        if (From.StateRoot != To.StateRoot) buffer.AddToBlooms(StateRootPointer, pin.WriteBoundary);
        foreach ((_, ulong root) in StorageRoots) buffer.AddToBlooms(root, pin.WriteBoundary);
    }

    /// <summary>The mutable content; only valid for snapshots created for commit (not compacted ones).</summary>
    public SnapshotContent Content => _mutable!;

    internal SortedSnapshotContent SortedContent => _sorted!;

    public bool TryGetAccount(HashedKey<Address> key, out Account? acc)
        => _isSorted ? _sorted!.TryGetAccount(key, out acc) : _mutable!.Accounts.TryGetValue(key, out acc);

    public bool HasSelfDestruct(HashedKey<Address> key)
        => _isSorted ? _sorted!.HasSelfDestruct(key) : _mutable!.SelfDestructedStorageAddresses.TryGetValue(key, out bool _);

    public bool TryGetStorage(HashedKey<(Address, UInt256)> key, out UInt256? value)
        => _isSorted ? _sorted!.TryGetStorage(key, out value) : _mutable!.Storages.TryGetValue(key, out value);

    /// <summary>The key a storage key is filed under in a slot filter built by <see cref="AddStorageKeysTo"/>.</summary>
    /// <remarks>The hash the dictionaries already bucket on, so a probe hashes nothing new.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong StorageFilterKey(in HashedKey<(Address, UInt256)> key) => (uint)key.GetHashCode();

    /// <summary>Adds every storage key of this snapshot to <paramref name="filter"/> under <see cref="StorageFilterKey"/>.</summary>
    /// <remarks>
    /// Requires storage keys to be fully populated before the snapshot is published to readers and unchanged
    /// afterwards (see <see cref="SnapshotContent.Storages"/>), so a built filter cannot miss a later key.
    /// </remarks>
    internal void AddStorageKeysTo(BloomFilter filter)
    {
        if (_isSorted)
        {
            // Entry.HashCode is (uint)key.GetHashCode(), i.e. StorageFilterKey, and reading it skips copying the keys.
            foreach (ref readonly SortedMergeDictionary<HashedKey<(Address, UInt256)>, UInt256?>.Entry entry in _sorted!.SortedStorages.Entries)
            {
                filter.AddUnsynchronized(entry.HashCode);
            }
        }
        else
        {
            foreach (KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?> kv in _mutable!.Storages)
            {
                filter.AddUnsynchronized(StorageFilterKey(kv.Key));
            }
        }
    }

    public bool TryGetStateNode(HashedKey<TreePath> key, [NotNullWhen(true)] out TrieNode? node)
    {
        node = null;
        return BufferPin is not null && BufferPin.Buffer.TryGetNode(StateRootPointer, key.Key, out node);
    }

    public bool TryGetStorageNode(HashedKey<(Hash256, TreePath)> key, [NotNullWhen(true)] out TrieNode? node)
    {
        node = null;
        return BufferPin is not null && TryGetStorageRoot(key.Key.Item1, out ulong root)
            && BufferPin.Buffer.TryGetNode(root, key.Key.Item2, out node);
    }

    /// <summary>The record a node pointer refers to, borrowed for as long as the snapshot holds its pin.</summary>
    internal NodeRecord GetNodeRecord(ulong pointer) => BufferPin!.Buffer.TryLocate(pointer, out NodeRecord record)
        ? record
        : throw new TrieException($"Trie node buffer pointer {pointer:x16} does not resolve to a record.");

    /// <summary>The RLP of a node pointer, borrowed for as long as the snapshot holds its pin.</summary>
    internal ReadOnlySpan<byte> GetRlp(ulong pointer) => GetNodeRecord(pointer).Rlp;

    protected override void CleanUp()
    {
        if (_isSorted) _resourcePool.ReturnSortedSnapshotContent(_usage, _sorted!);
        else _resourcePool.ReturnSnapshotContent(_usage, _mutable!);
        BufferPin?.Dispose();
    }

    public bool TryAcquire() => TryAcquireLease();
}

public sealed class SnapshotContent : IDisposable, IResettable
{
    // ConcurrentDictionary: lock-free reads, best read latency for accounts/slots
    public readonly ConcurrentDictionary<HashedKey<Address>, Account?> Accounts = new();

    /// <summary>Slot writes; <c>null</c> is a zero write (a deletion).</summary>
    /// <remarks>
    /// Slot writes must finish before the snapshot is published to readers, including when content is populated
    /// after construction through <see cref="ResourcePool.CreateSnapshot"/>. The negative slot filter of
    /// <see cref="ReadOnlySnapshotBundle"/> relies on this: a key added afterwards would be missing from a filter
    /// already built and a filtered read would skip it.
    /// </remarks>
    public readonly ConcurrentDictionary<HashedKey<(Address, UInt256)>, UInt256?> Storages = new();
    public readonly ConcurrentDictionary<HashedKey<Address>, bool> SelfDestructedStorageAddresses = new();

    /// <summary>The final published state-root pointer, zero only for the empty root.</summary>
    public ulong StateRootPointer;
    /// <summary>Final roots of changed storage tries. Missing addresses are unchanged; zero marks an empty trie.</summary>
    public readonly ConcurrentDictionary<Hash256, ulong> StorageRoots = new();

    /// <summary>Accounts the block only read, cached for its later reads.</summary>
    /// <remarks>
    /// Kept apart from <see cref="Accounts"/>, so a snapshot never exports them: persisting them would rewrite
    /// unchanged values and evict them from the carry-forward cache.
    /// </remarks>
    public readonly ConcurrentDictionary<HashedKey<Address>, Account?> ReadAccounts = new();

    public void Reset()
    {
        // Reset runs at a quiescent pool-return boundary (final snapshot lease released, or the
        // bundle returning its current content on disposal), so no writer can hold a stripe;
        // the lock-free clear skips ~thousands of inflated Monitor acquisitions per return.
        Accounts.NoLockClear();
        ReadAccounts.NoLockClear();
        Storages.NoLockClear();
        SelfDestructedStorageAddresses.NoLockClear();
        StateRootPointer = 0;
        StorageRoots.NoLockClear();
    }

    /// <summary>
    /// Captures flat-value and root metadata counts. <see cref="ConcurrentDictionary{TKey,TValue}.Count"/>
    /// acquires every stripe lock, so callers should capture once at a writer-quiescent boundary and
    /// reuse the result instead of re-reading live counts.
    /// </summary>
    internal SnapshotContentCounts CaptureCounts() => new(
        Accounts.Count,
        Storages.Count,
        SelfDestructedStorageAddresses.Count,
        StateRootPointer != 0 ? 1 : 0,
        StorageRoots.Count);

    public long EstimateMemory() => CaptureCounts().EstimateMemory();

    /// <inheritdoc cref="SnapshotContentCounts.EstimateCompactedMemory"/>
    public long EstimateCompactedMemory() => CaptureCounts().EstimateCompactedMemory();

    public void Dispose()
    {
    }
}

/// <summary>
/// Entry counts of a <see cref="SnapshotContent"/>, captured once so memory estimates are pure
/// functions of a single consistent capture.
/// </summary>
internal readonly record struct SnapshotContentCounts(
    int Accounts,
    int Storages,
    int SelfDestructedStorageAddresses,
    int StateRoots,
    int StorageRoots)
{
    public long EstimateMemory() =>
        // Cast Count to long before multiplying to avoid int overflow for large snapshots
        (long)Accounts * 172 +                         // Key (12B: ref 8B + hash 4B) + Value ref (8B) + CD overhead (48) + Account object (~104B)
            (long)Storages * 136 +                         // Key (44B: addr ref 8B + UInt256 32B + hash 4B) + Value (40B UInt256?) + CD overhead (48) + Value ref (4B)
            (long)SelfDestructedStorageAddresses * 64 +    // Key (12B: ref 8B + hash 4B) + Value (4B) + CD overhead (48)
            (long)StateRoots * 16 +                       // Pointer and publication flag, including alignment
            (long)StorageRoots * 120;                     // Hash256 key, pointer and concurrent dictionary entry

    /// <summary>
    /// Estimates memory for compacted snapshots, counting only dictionary overhead + keys + value-type values.
    /// Does not count Account objects as they are shared with the original snapshots.
    /// </summary>
    public long EstimateCompactedMemory() =>
        // ConcurrentDictionary entry overhead ~48 bytes
        // Account objects are already accounted by non-compacted snapshots
        (long)Accounts * 68 +                          // Key (12B: ref 8B + hash 4B) + Value ref (8B) + CD overhead (48)
            (long)Storages * 136 +                         // Key (44B: addr ref 8B + UInt256 32B + hash 4B) + Value (40B UInt256?) + CD overhead (48) + Value ref (4B)
            (long)SelfDestructedStorageAddresses * 64 +    // Key (12B: ref 8B + hash 4B) + Value (4B) + CD overhead (48)
            (long)StateRoots * 16 +                       // Pointer and publication flag, including alignment
            (long)StorageRoots * 48;                      // Shared Hash256 key, pointer and dictionary entry
}
