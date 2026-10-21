// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Memory;
using Nethermind.Core.Metric;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeLog;
using Nethermind.State.Pbt.Common;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>Adapts a writable snapshot bundle to the canonical <see cref="TrieUpdater"/> store contract.</summary>
/// <remarks>
/// One store serves one root fold. Workers reserve log records and hand their references over on disposal.
/// The owner publishes the new tree's root on store disposal only after a successful fold. The bundle's read-only base pins
/// every record the fold borrows, and its write buffer's generation pin every record the fold writes. A removed group is
/// simply no longer linked, so nothing records its removal.
/// </remarks>
public sealed class PbtSnapshotStore(PbtSnapshotBundle bundle) : IPbtStore, IDisposable
{
    private static readonly PbtNodeGroupReadLabel[] _foundLabels = ReadLabels("found");
    private static readonly PbtNodeGroupReadLabel[] _nullLabels = ReadLabels("null");

    // Detailed observers stay no-ops unless Metrics.EnableDetailedMetric, so the timestamps are skipped with them.
    private readonly bool _recordReadTimes = Metrics.PbtTrieUpdaterNodeGroupReadTimes is not NoopMetricObserver;
    private readonly ArrayPoolList<ArrayPoolList<BufferedNodeGroup>> _handedOff = new(16);
    private bool _complete;
    private IPbtGroupLog Log => bundle.NodeGroupLog;

    /// <inheritdoc/>
    public PbtGroupReference RootGroupReference => bundle.RootGroupReference;

    /// <inheritdoc/>
    public bool TryGetNodeGroupRecord(PbtGroupReference reference, scoped in PbtTraversalPath groupKey, out PbtGroupRecord record) =>
        bundle.NodeGroupLog.TryGetRecord(reference, out record)
        && PbtNodePathOperations.Equal(record.Path, groupKey.ToPath<PbtStorageNodePath>());

    /// <inheritdoc/>
    public void CompleteFold() => _complete = true;

    /// <inheritdoc/>
    /// <remarks>
    /// A miss also searches the in-memory snapshot trees: an overlay log cannot reach a shared snapshot written after its
    /// pin, so such a branch starts from an unresolved root.
    /// </remarks>
    public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey)
    {
        PbtStorageNodePath storagePath = groupKey.ToPath<PbtStorageNodePath>();
        if (!_recordReadTimes) return bundle.GetNodeGroup(storagePath);

        long start = Stopwatch.GetTimestamp();
        RefCountingMemory? payload = bundle.GetNodeGroup(storagePath);
        Metrics.PbtTrieUpdaterNodeGroupReadTimes.Observe(
            Stopwatch.GetTimestamp() - start,
            (payload is null ? _nullLabels : _foundLabels)[ReadLabelIndex(storagePath)]);
        return payload;
    }

    // Indexed by PbtPartitions.PartitionOfPath, offset by three for groups the persistence keeps in the top column.
    private static PbtNodeGroupReadLabel[] ReadLabels(string result) =>
        [new("account", result), new("code", result), new("storage", result), new("account_top", result), new("code_top", result), new("storage_top", result)];

    private static int ReadLabelIndex(PbtStorageNodePath groupKey)
    {
        int partition = (int)PbtPartitions.PartitionOfPath(groupKey);
        return PbtNodeGroupLayout.IsTopGroup(groupKey) ? partition + 3 : partition;
    }

    public IPbtConcurrentWriter CreateWriter() => new ConcurrentWriter(this);

    /// <summary>Publishes a successful fold's handed-off groups on the owner thread.</summary>
    public void Dispose()
    {
        try
        {
            if (_complete)
            {
                PbtGroupReference? root = null;
                int count = 0;
                long bytes = 0;
                foreach (ArrayPoolList<BufferedNodeGroup> buffer in _handedOff)
                    foreach (BufferedNodeGroup group in buffer)
                    {
                        if (group.Path.BitDepth == 0) root = group.Reference;
                        if (!group.Reference.IsDirect) continue;
                        count++;
                        bytes += ((group.Path.BitDepth + 7) >> 3) + group.Length;
                    }
                if (root is { } published) bundle.PublishNodeGroups(published, count, bytes);
            }
        }
        finally
        {
            foreach (ArrayPoolList<BufferedNodeGroup> buffer in _handedOff) buffer.Dispose();
            _handedOff.Dispose();
        }
    }

    private readonly record struct BufferedNodeGroup(PbtStorageNodePath Path, PbtGroupReference Reference, int Length);

    private sealed class ConcurrentWriter(PbtSnapshotStore store) : IPbtConcurrentWriter
    {
        private readonly ArrayPoolList<BufferedNodeGroup> _buffer = new(16);

        public PbtGroupReference ReserveNodeGroup(scoped in PbtTraversalPath groupKey, int length, in PbtGroupLinks links, out Span<byte> payload)
        {
            PbtStorageNodePath path = groupKey.ToPath<PbtStorageNodePath>();
            payload = default;
            PbtGroupReference reference = length == 0 ? PbtGroupReference.None
                : store.Log.Reserve(path, length, links, out payload);
            _buffer.Add(new BufferedNodeGroup(path, reference, length));
            return reference;
        }

        /// <remarks>Never called: reservations here always return a log reference, so folds write groups in place.</remarks>
        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, RefCountingMemory? payload) =>
            throw new NotSupportedException("Snapshot folds write reserved log records.");

        public void Dispose()
        {
            if (_buffer.Count == 0) _buffer.Dispose();
            else lock (store._handedOff) store._handedOff.Add(_buffer);
        }
    }
}
