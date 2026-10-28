// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.Core.Threading;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>The tree of a <see cref="PbtSnapshotBundle"/>, written by <see cref="TrieUpdater"/>.</summary>
/// <remarks>
/// Written groups go to the bundle's <see cref="PbtSnapshotBundle.GroupRecordBuffer"/> at once, but reach the bundle only on
/// <see cref="Publish"/>. Until then the folds read them back from the buffer by pointer, which the write buffer's pin keeps
/// readable. The bundle's read-only base pins every record the fold borrows. A removed group is simply no longer linked, so
/// nothing records its removal.
/// </remarks>
/// <param name="root">The tree's root and its group's pointer, or 0 when the buffer does not hold it.</param>
public sealed class PbtSnapshotStore(PbtSnapshotBundle bundle, (ValueHash256 Hash, ulong Pointer) root) : IPbtStore
{
    private static readonly PbtNodeGroupReadLabel[] _foundLabels = ReadLabels("found");
    private static readonly PbtNodeGroupReadLabel[] _nullLabels = ReadLabels("null");

    // Detailed observers stay no-ops unless Metrics.EnableDetailedMetric, so the timestamps are skipped with them.
    private readonly bool _recordReadTimes = Metrics.PbtTrieUpdaterNodeGroupReadTimes is not NoopMetricObserver;
    private bool _rootDirty;
    private IPbtGroupRecordBuffer Buffer => bundle.GroupRecordBuffer;

    public (ValueHash256 Hash, ulong Pointer) Root { get; private set; } = root;

    /// <inheritdoc cref="TrieUpdater.UpdateRoot"/>
    public void UpdateRoot(PbtPartitionBatches changes, ConcurrencyController foldQuota, FoldFanOut fanOut, IMetricObserver? partitionFoldTime, IMetricObserver? phaseTime) =>
        SetRoot(TrieUpdater.UpdateRoot(this, Root, changes, foldQuota, fanOut, partitionFoldTime, phaseTime));

    /// <summary>Makes the final root readable from the bundle.</summary>
    public void Publish()
    {
        // A published root always points at its record, so a root the buffer no longer holds is copied back in.
        if (Root.Pointer == 0 && Root.Hash != default) SetRoot((Root.Hash, CopyRootRecord()));
        if (_rootDirty && Root.Pointer != 0) Buffer.SetRoot(Root.Hash, Root.Pointer);
        bundle.PublishTreeRoot(Root);

        _rootDirty = false;
    }

    /// <exception cref="InvalidDataException">Neither the warmed groups nor persistence hold the root group.</exception>
    private ulong CopyRootRecord()
    {
        using PbtLoadedRecordBuffer loaded = new();
        PbtTraversalPath rootPath = new(Span<byte>.Empty);
        if (!TryLoad(rootPath, loaded, out ReadOnlyMemory<byte> loadedPayload))
            throw new InvalidDataException("A referenced PBT node group is missing.");

        ReadOnlySpan<byte> payload = loadedPayload.Span;
        using IPbtGroupRecordWriteBatch batch = StartWriteBatch(PbtGroupRecordCodec.AlignedLength(PbtGroupRecordCodec.Length(payload.Length)));
        payload.CopyTo(PbtGroupRecordCodec.WriteGroup(batch.Bytes, rootPath.ToPath<PbtStorageNodePath>(), default, payload.Length));
        return batch.BasePointer;
    }

    /// <inheritdoc/>
    public bool TryLocate(ulong pointer, out PbtGroupRecord record) => bundle.TryLocate(pointer, out record);

    /// <inheritdoc/>
    public bool TryLoad(scoped in PbtTraversalPath path, PbtLoadedRecordBuffer loaded, out ReadOnlyMemory<byte> payload)
    {
        PbtStorageNodePath storagePath = path.ToPath<PbtStorageNodePath>();
        if (!_recordReadTimes) return bundle.TryLoadNodeGroup(storagePath, loaded, out payload);

        long start = Stopwatch.GetTimestamp();
        bool found = bundle.TryLoadNodeGroup(storagePath, loaded, out payload);
        Metrics.PbtTrieUpdaterNodeGroupReadTimes.Observe(
            Stopwatch.GetTimestamp() - start,
            (found ? _foundLabels : _nullLabels)[ReadLabelIndex(storagePath)]);
        return found;
    }

    // Indexed by PbtPartitions.PartitionOfPath, offset by three for groups the persistence keeps in the top column.
    private static PbtNodeGroupReadLabel[] ReadLabels(string result) =>
        [new("account", result), new("code", result), new("storage", result), new("account_top", result), new("code_top", result), new("storage_top", result)];

    private static int ReadLabelIndex(PbtStorageNodePath groupKey)
    {
        int partition = (int)PbtPartitions.PartitionOfPath(groupKey);
        return PbtNodeGroupLayout.IsTopGroup(groupKey) ? partition + 3 : partition;
    }

    /// <inheritdoc/>
    public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => Buffer.StartWriteBatch(alignedTotalLength);

    private void SetRoot(in (ValueHash256 Hash, ulong Pointer) updated)
    {
        _rootDirty |= updated != Root;
        Root = updated;
    }
}
