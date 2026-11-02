// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>
/// A sealed canonical EIP-8297 diff covering <see cref="From"/> to <see cref="To"/>, backed by either the mutable
/// <see cref="PbtSnapshotContent"/> or the sorted <see cref="PbtSortedSnapshotContent"/>, selected by <see cref="IsSorted"/>.
/// </summary>
/// <param name="content">The mutable content; null when <paramref name="sortedContent"/> is given.</param>
/// <param name="sortedContent">The sorted content of a compacted snapshot; null when <paramref name="content"/> is given.</param>
public class PbtSnapshot(
    in StateId from,
    in StateId to,
    in ValueHash256 treeRoot,
    PbtSnapshotContent? content,
    PbtSortedSnapshotContent? sortedContent,
    IPbtResourcePool resourcePool,
    PbtResourcePool.Usage usage) : RefCountingDisposable
{
    private readonly bool _isSorted = sortedContent is not null;
    private PbtSnapshotPayloadSize? _payloadSize;
    private int _bloomPopulated;

    public StateId From { get; } = from;
    public StateId To { get; } = to;
    public ValueHash256 TreeRoot { get; } = treeRoot;
    /// <summary>The pointer of the snapshot's root group in its content's buffer, or 0 for the empty tree; only the root hash is persisted.</summary>
    public ulong RootGroupPointer { get; } = sortedContent?.RootGroupPointer ?? content!.RootGroupPointer;

    public bool IsSorted => _isSorted;

    /// <summary>The mutable content; only valid for snapshots that are not <see cref="IsSorted"/>.</summary>
    public PbtSnapshotContent Content => content!;

    /// <summary>The sorted content; only valid for snapshots that are <see cref="IsSorted"/>.</summary>
    public PbtSortedSnapshotContent SortedContent => sortedContent!;

    /// <summary>The pin protecting the group records this snapshot references, or null when it wrote none.</summary>
    public IPbtGroupRecordBufferPin? NodeGroupPin => _isSorted ? sortedContent!.NodeGroupPin : content!.NodeGroupPin;

    public IReadOnlyCollection<KeyValuePair<ValueHash256, PbtAccount?>> Accounts => _isSorted ? sortedContent!.Accounts : content!.Accounts;
    public IReadOnlyCollection<KeyValuePair<HashedKey<PbtPath>, PackedSlotRun>> HeaderStorages => _isSorted ? sortedContent!.HeaderStorages : content!.HeaderStorages;
    public IReadOnlyCollection<KeyValuePair<HashedKey<PbtStoragePath>, PackedSlotRun>> Storages => _isSorted ? sortedContent!.Storages : content!.Storages;
    public IReadOnlyCollection<KeyValuePair<ValueHash256, CodeInfo>> Codes => _isSorted ? sortedContent!.Codes : content!.Codes;
    public IReadOnlyCollection<KeyValuePair<ValueHash256, bool>> SelfDestructedStorageAddresses => _isSorted ? sortedContent!.SelfDestructedStorageAddresses : content!.SelfDestructedStorageAddresses;

    public PbtSnapshotPayloadSize PayloadSize => _payloadSize ??= new PbtSnapshotPayloadSize(EstimateLeafBytes(), EstimatePinnedNodeBytes());

    public bool TryGetAccount(in ValueHash256 addressHash, out PbtAccount? account) => _isSorted
        ? sortedContent!.TryGetAccount(addressHash, out account)
        : content!.Accounts.TryGetValue(addressHash, out account);

    public bool TryGetCode(in ValueHash256 codeHash, [NotNullWhen(true)] out CodeInfo? code) => _isSorted
        ? sortedContent!.TryGetCode(codeHash, out code)
        : content!.Codes.TryGetValue(codeHash, out code);

    public bool HasSelfDestruct(in ValueHash256 addressHash) => _isSorted
        ? sortedContent!.HasSelfDestruct(addressHash)
        : content!.SelfDestructedStorageAddresses.ContainsKey(addressHash);

    /// <inheritdoc cref="PbtSnapshotContent.TryGetSlotRun{TKey}"/>
    public bool TryGetSlotRun<TKey>(in HashedKey<TKey> runKey, [NotNullWhen(true)] out PackedSlotRun? run) where TKey : struct, IPbtKey<TKey> => _isSorted
        ? sortedContent!.TryGetSlotRun(runKey, out run)
        : content!.TryGetSlotRun(runKey, out run);

    /// <inheritdoc cref="PbtSnapshotContent.SlotFilterKeyCount"/>
    public long SlotFilterKeyCount => _isSorted ? sortedContent!.SlotFilterKeyCount : content!.SlotFilterKeyCount;

    /// <inheritdoc cref="PbtSnapshotContent.AddSlotFilterKeysTo"/>
    public void AddSlotFilterKeysTo(BloomFilter filter)
    {
        if (_isSorted) sortedContent!.AddSlotFilterKeysTo(filter);
        else content!.AddSlotFilterKeysTo(filter);
    }

    /// <summary>Enumerates the groups of this snapshot's tree that were written since its generation pin, in <see cref="PbtNodeGroupKey.InKeyOrder"/> order.</summary>
    /// <remarks>
    /// Groups the tree inherits from earlier layers, and groups replaced within this snapshot's range, are not enumerated.
    /// Records must not be used after the snapshot is released.
    /// </remarks>
    public PbtNodeGroupKey.KeyOrderEnumerable EnumerateNodeGroups() =>
        NodeGroupPin is { } pin ? PbtNodeGroupKey.InKeyOrder(pin.Buffer, RootGroupPointer, pin.WriteBoundary) : default;

    /// <summary>Estimates the group record buffer bytes of this snapshot: those from its pin to its root group record.</summary>
    /// <remarks>
    /// The pin keeps them from being dropped. The root group is published last, so the range covers the records the snapshot wrote,
    /// along with any other writer's records interleaved there. Generations are taken to be <see cref="PbtGroupRecordBuffer.DefaultGenerationBytes"/> long.
    /// </remarks>
    public long EstimatePinnedNodeBytes()
    {
        if (NodeGroupPin is not { } pin) return 0;

        ulong from = pin.WriteBoundary;
        ulong to = RootGroupPointer;
        if (to <= from) return 0;

        long generations = (long)((to >> PbtGroupRecordCodec.OffsetBits) - (from >> PbtGroupRecordCodec.OffsetBits));
        return generations * PbtGroupRecordBuffer.DefaultGenerationBytes + (uint)to - (uint)from;
    }

    private long EstimateLeafBytes()
    {
        long leafBytes = Accounts.Count * (ValueHash256.MemorySize + 128L)
            + SelfDestructedStorageAddresses.Count * ValueHash256.MemorySize;
        foreach ((_, PackedSlotRun run) in HeaderStorages) leafBytes += PbtPath.KeyLength + run.Count * ValueHash256.MemorySize;
        foreach ((_, PackedSlotRun run) in Storages) leafBytes += PbtStoragePath.KeyLength + run.Count * ValueHash256.MemorySize;
        foreach ((_, CodeInfo code) in Codes) leafBytes += ValueHash256.MemorySize + code.Code.Length;
        return leafBytes;
    }

    public bool TryLease() => TryAcquireLease();

    /// <summary>Adds the path key hashes of the groups written within this snapshot's range to the blooms of their buffer generations, once.</summary>
    /// <remarks>
    /// Called as the compactor first takes in a base snapshot, keeping it off the commit path. A path key hash missing from its
    /// bloom, of a snapshot not yet taken in or of a group no final root reaches, is fine: buffer compaction then keeps a superseded
    /// record, which only makes the compacted generation unnecessarily bigger.
    /// </remarks>
    public void PopulateBloom()
    {
        if (NodeGroupPin is not { Buffer: PbtGroupRecordBuffer buffer } pin || Interlocked.Exchange(ref _bloomPopulated, 1) != 0) return;

        buffer.AddToBlooms(RootGroupPointer, pin.WriteBoundary);
    }

    protected override void CleanUp()
    {
        if (_isSorted) resourcePool.ReturnSortedSnapshotContent(usage, sortedContent!);
        else resourcePool.ReturnSnapshotContent(usage, content!);
    }
}
