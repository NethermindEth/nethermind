// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Collections.Pooled;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.State.Flat.Collections;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.NodeBuffer;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>The sorted, pooled counterpart of <see cref="PbtSnapshotContent"/> used for compacted snapshots.</summary>
/// <remarks>Built once by <see cref="BuildFromMerge"/>, then read-only. This layer owns its runs. Reset requires exclusive ownership.</remarks>
public sealed class PbtSortedSnapshotContent : IDisposable, IResettable
{
    private readonly SortedMergeDictionary<ValueHash256, PbtAccount?> _accounts = new();
    private readonly SortedMergeDictionary<HashedKey<PbtPath>, PackedSlotRun> _headerStorages = new();
    private readonly SortedMergeDictionary<HashedKey<PbtStoragePath>, PackedSlotRun> _storages = new();
    private readonly SortedMergeDictionary<ValueHash256, CodeInfo> _codes = new();
    private readonly SortedMergeDictionary<ValueHash256, bool> _selfDestructedStorageAddresses = new();

    public IReadOnlyCollection<KeyValuePair<ValueHash256, PbtAccount?>> Accounts => _accounts;
    public IReadOnlyCollection<KeyValuePair<HashedKey<PbtPath>, PackedSlotRun>> HeaderStorages => _headerStorages;
    public IReadOnlyCollection<KeyValuePair<HashedKey<PbtStoragePath>, PackedSlotRun>> Storages => _storages;
    public IReadOnlyCollection<KeyValuePair<ValueHash256, CodeInfo>> Codes => _codes;
    public IReadOnlyCollection<KeyValuePair<ValueHash256, bool>> SelfDestructedStorageAddresses => _selfDestructedStorageAddresses;

    /// <summary>The pin on the oldest group record buffer generation of the merged layers, or null when none wrote groups.</summary>
    public IPbtGroupRecordBufferPin? NodeGroupPin { get; private set; }

    /// <summary>The pointer of the root group of the newest merged layer's tree, or 0 for an empty tree.</summary>
    public ulong RootGroupPointer { get; private set; }

    public bool TryGetAccount(in ValueHash256 addressHash, out PbtAccount? account) => _accounts.TryGetValue(addressHash, out account);

    public bool TryGetCode(in ValueHash256 codeHash, [NotNullWhen(true)] out CodeInfo? code) => _codes.TryGetValue(codeHash, out code);

    public bool HasSelfDestruct(in ValueHash256 addressHash) => _selfDestructedStorageAddresses.TryGetValue(addressHash, out _);

    /// <inheritdoc cref="PbtSnapshotContent.TryGetSlotRun{TKey}"/>
    public bool TryGetSlotRun<TKey>(in HashedKey<TKey> runKey, [NotNullWhen(true)] out PackedSlotRun? run) where TKey : struct, IPbtKey<TKey> =>
        SlotRun.ByZone<TKey, SortedMergeDictionary<HashedKey<TKey>, PackedSlotRun>>(_headerStorages, _storages).TryGetValue(runKey, out run);

    /// <inheritdoc cref="PbtSnapshotContent.SlotFilterKeyCount"/>
    public long SlotFilterKeyCount => _headerStorages.Count + _storages.Count + _selfDestructedStorageAddresses.Count;

    /// <inheritdoc cref="PbtSnapshotContent.AddSlotFilterKeysTo"/>
    public void AddSlotFilterKeysTo(BloomFilter filter)
    {
        // Entry.HashCode is (uint)key.GetHashCode(), i.e. the slot filter key, and reading it skips copying the keys.
        foreach (ref readonly SortedMergeDictionary<HashedKey<PbtPath>, PackedSlotRun>.Entry entry in _headerStorages.Entries) filter.AddUnsynchronized(entry.HashCode);
        foreach (ref readonly SortedMergeDictionary<HashedKey<PbtStoragePath>, PackedSlotRun>.Entry entry in _storages.Entries) filter.AddUnsynchronized(entry.HashCode);
        foreach (ref readonly SortedMergeDictionary<ValueHash256, bool>.Entry entry in _selfDestructedStorageAddresses.Entries) filter.AddUnsynchronized(entry.HashCode);
    }

    /// <summary>Merges <paramref name="sourcesOldestFirst"/> into this empty content, the newest write of each key winning.</summary>
    /// <remarks>
    /// A run written before the newest clear of its address is dropped by the merge rather than added then removed. The sources
    /// keep their runs; this layer owns copies of the ones it keeps. Sources must be sealed.
    /// </remarks>
    public void BuildFromMerge(IReadOnlyList<PbtSnapshot> sourcesOldestFirst)
    {
        using PooledDictionary<ValueHash256, int> slotClearBoundary = new();
        for (int i = 0; i < sourcesOldestFirst.Count; i++)
        {
            foreach ((ValueHash256 addressHash, _) in sourcesOldestFirst[i].SelfDestructedStorageAddresses) slotClearBoundary[addressHash] = i;
        }

        using ArrayPoolListRef<Task> mergeTasks = new(3);
        try
        {
            mergeTasks.Add(Task.Run(() => MergeInto(
                _accounts, sourcesOldestFirst, default(HashComparer), static s => s._accounts, static m => m.Accounts)));
            mergeTasks.Add(Task.Run(() => MergeRunsInto(
                _headerStorages, sourcesOldestFirst, static s => s._headerStorages, static m => m.HeaderStorages, slotClearBoundary)));
            mergeTasks.Add(Task.Run(() => MergeRunsInto(
                _storages, sourcesOldestFirst, static s => s._storages, static m => m.Storages, slotClearBoundary)));
            MergeInto(_codes, sourcesOldestFirst, default(HashComparer), static s => s._codes, static m => m.Codes);
            MergeInto(_selfDestructedStorageAddresses, sourcesOldestFirst, default(HashComparer),
                static s => s._selfDestructedStorageAddresses, static m => m.SelfDestructedStorageAddresses);
        }
        finally
        {
            Task.WaitAll(mergeTasks.AsSpan());
        }

        RootGroupPointer = sourcesOldestFirst[^1].RootGroupPointer;
        // The oldest pin covers every merged layer, whose pins may be released before this one.
        NodeGroupPin = OldestNodeGroupPin(sourcesOldestFirst)?.Copy();
    }

    private static IPbtGroupRecordBufferPin? OldestNodeGroupPin(IReadOnlyList<PbtSnapshot> sources)
    {
        IPbtGroupRecordBufferPin? oldest = null;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].NodeGroupPin is { } pin && (oldest is null || pin.WriteBoundary < oldest.WriteBoundary)) oldest = pin;
        }

        return oldest;
    }

    private static void MergeRunsInto<TKey>(
        SortedMergeDictionary<HashedKey<TKey>, PackedSlotRun> target,
        IReadOnlyList<PbtSnapshot> sources,
        Func<PbtSortedSnapshotContent, SortedMergeDictionary<HashedKey<TKey>, PackedSlotRun>> fromSorted,
        Func<PbtSnapshotContent, IReadOnlyCollection<KeyValuePair<HashedKey<TKey>, PackedSlotRun>>> fromMutable,
        PooledDictionary<ValueHash256, int> slotClearBoundary)
        where TKey : struct, IPbtKey<TKey>
    {
        MergeInto(target, sources, default(RunKeyComparer<TKey>), fromSorted, fromMutable, new StorageBoundaryKeep<TKey>(slotClearBoundary));
        target.MapValues(static run => run.Clone());
    }

    private static void MergeInto<TKey, TValue, TComparer>(
        SortedMergeDictionary<TKey, TValue> target,
        IReadOnlyList<PbtSnapshot> sources,
        TComparer comparer,
        Func<PbtSortedSnapshotContent, SortedMergeDictionary<TKey, TValue>> fromSorted,
        Func<PbtSnapshotContent, IReadOnlyCollection<KeyValuePair<TKey, TValue>>> fromMutable)
        where TKey : IEquatable<TKey>
        where TComparer : IComparer<TKey>
        => MergeInto(target, sources, comparer, fromSorted, fromMutable, default(KeepAll<TKey>));

    private static void MergeInto<TKey, TValue, TComparer, TKeep>(
        SortedMergeDictionary<TKey, TValue> target,
        IReadOnlyList<PbtSnapshot> sources,
        TComparer comparer,
        Func<PbtSortedSnapshotContent, SortedMergeDictionary<TKey, TValue>> fromSorted,
        Func<PbtSnapshotContent, IReadOnlyCollection<KeyValuePair<TKey, TValue>>> fromMutable,
        TKeep keep)
        where TKey : IEquatable<TKey>
        where TComparer : IComparer<TKey>
        where TKeep : struct, IMergeKeep<TKey>
    {
        int count = sources.Count;
        SortedMergeDictionary<TKey, TValue>.Run[] runs = new SortedMergeDictionary<TKey, TValue>.Run[count];

        // Mutable inputs are sorted into transients that are disposed once the merge has copied them.
        List<SortedMergeDictionary<TKey, TValue>.PooledRun>? transients = null;
        try
        {
            for (int i = 0; i < count; i++)
            {
                PbtSnapshot source = sources[i];
                if (source.IsSorted)
                {
                    runs[i] = fromSorted(source.SortedContent).AsRun();
                }
                else
                {
                    SortedMergeDictionary<TKey, TValue>.PooledRun transient =
                        SortedMergeDictionary<TKey, TValue>.BuildRunFromUnsorted(fromMutable(source.Content), comparer);
                    runs[i] = transient.AsRun();
                    (transients ??= []).Add(transient);
                }
            }

            target.BuildFromMerge(runs, comparer, keep);
        }
        finally
        {
            if (transients is not null)
                foreach (SortedMergeDictionary<TKey, TValue>.PooledRun transient in transients) transient.Dispose();
        }
    }

    public void Reset()
    {
        _accounts.NoResizeClear();
        foreach ((_, PackedSlotRun run) in _headerStorages) SlotRun.Return(run);
        _headerStorages.NoResizeClear();
        foreach ((_, PackedSlotRun run) in _storages) SlotRun.Return(run);
        _storages.NoResizeClear();
        _codes.NoResizeClear();
        _selfDestructedStorageAddresses.NoResizeClear();
        RootGroupPointer = 0;
        NodeGroupPin?.Dispose();
        NodeGroupPin = null;
    }

    public void Dispose()
    {
        Reset();
        _accounts.Dispose();
        _headerStorages.Dispose();
        _storages.Dispose();
        _codes.Dispose();
        _selfDestructedStorageAddresses.Dispose();
    }

    private readonly struct HashComparer : IComparer<ValueHash256>
    {
        public int Compare(ValueHash256 x, ValueHash256 y) => x.CompareTo(in y);
    }

    private readonly struct RunKeyComparer<TKey> : IComparer<HashedKey<TKey>> where TKey : struct, IPbtKey<TKey>
    {
        public int Compare(HashedKey<TKey> x, HashedKey<TKey> y) => x.Key.CompareTo(y.Key);
    }

    private struct StorageBoundaryKeep<TKey>(PooledDictionary<ValueHash256, int> boundaries) : IMergeKeep<HashedKey<TKey>>
        where TKey : struct, IPbtKey<TKey>
    {
        private ValueHash256 _address;
        private bool _hasAddress;
        private int _boundary;
        private bool _hasBoundary;

        public bool Keep(int sourceIndex, HashedKey<TKey> key)
        {
            ValueHash256 address = Eip8297KeyDerivation.AddressHashOf(key.Key);
            if (!_hasAddress || address != _address)
            {
                _address = address;
                _hasAddress = true;
                _hasBoundary = boundaries.TryGetValue(address, out _boundary);
            }
            return !_hasBoundary || sourceIndex >= _boundary;
        }
    }
}
