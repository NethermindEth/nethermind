// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Memory;
using Nethermind.Core.Metric;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>Adapts a writable snapshot bundle to the canonical <see cref="TrieUpdater"/> store contract.</summary>
/// <remarks>
/// One store serves one root fold. Workers reserve buffer records and hand their references over on disposal.
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
    private readonly ArrayPoolList<ArrayPoolList<ulong>> _handedOff = new(16);
    private bool _complete;
    private IPbtGroupRecordBuffer Buffer => bundle.GroupRecordBuffer;

    /// <inheritdoc/>
    public ulong RootGroupPointer => bundle.RootGroupPointer;

    /// <inheritdoc/>
    public bool TryGetNodeGroupRecord(ulong pointer, scoped in PbtTraversalPath groupKey, out PbtGroupRecord record) =>
        bundle.GroupRecordBuffer.TryLocate(pointer, out record)
        && PbtNodePathOperations.Equal(record.Path, groupKey.ToPath<PbtStorageNodePath>());

    /// <inheritdoc/>
    public void CompleteFold() => _complete = true;

    /// <inheritdoc/>
    /// <remarks>
    /// A miss also searches the in-memory snapshot trees: an overlay buffer cannot reach a shared snapshot written after its
    /// pin, so such a branch's root lookup misses.
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
                ulong? root = null;
                int count = 0;
                long bytes = 0;
                foreach (ArrayPoolList<ulong> buffer in _handedOff)
                    foreach (ulong pointer in buffer)
                    {
                        if (pointer == 0)
                        {
                            root = 0;
                            continue;
                        }
                        if (!Buffer.TryLocate(pointer, out PbtGroupRecord group)) throw new InvalidOperationException("A pinned group record no longer resolves.");
                        PbtStorageNodePath path = group.Path;
                        if (path.BitDepth == 0) root = pointer;
                        count++;
                        bytes += ((path.BitDepth + 7) >> 3) + group.Payload.Length;
                    }
                if (root is { } published) bundle.PublishNodeGroups(published, count, bytes);
            }
        }
        finally
        {
            foreach (ArrayPoolList<ulong> buffer in _handedOff) buffer.Dispose();
            _handedOff.Dispose();
        }
    }

    private sealed class ConcurrentWriter(PbtSnapshotStore store) : IPbtConcurrentWriter
    {
        // The pointers of the written records, and 0 for a removed root group.
        private readonly ArrayPoolList<ulong> _buffer = new(16);

        public bool TryStartWriteBatch(int alignedTotalLength, [NotNullWhen(true)] out IPbtGroupRecordWriteBatch? batch)
        {
            batch = store.Buffer.StartWriteBatch(alignedTotalLength);
            _buffer.Add(batch.BasePointer);
            return true;
        }

        /// <remarks>Only called for a removed group: every write batch here starts, so folds write groups in place.</remarks>
        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, RefCountingMemory? payload)
        {
            if (payload is not null) throw new NotSupportedException("Snapshot folds write buffer records.");
            if (groupKey.BitDepth == 0) _buffer.Add(0);
        }

        public void Dispose()
        {
            if (_buffer.Count == 0) _buffer.Dispose();
            else lock (store._handedOff) store._handedOff.Add(_buffer);
        }
    }
}
