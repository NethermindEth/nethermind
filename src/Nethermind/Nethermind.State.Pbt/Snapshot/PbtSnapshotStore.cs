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
/// The owner publishes them on store disposal only after a successful fold, while its operation lease still protects
/// all borrowed and unpublished records.
/// </remarks>
public sealed class PbtSnapshotStore(PbtSnapshotBundle bundle) : IPbtStore, IDisposable
{
    private static readonly PbtNodeGroupReadLabel[] _foundLabels = ReadLabels("found");
    private static readonly PbtNodeGroupReadLabel[] _nullLabels = ReadLabels("null");

    // Detailed observers stay no-ops unless Metrics.EnableDetailedMetric, so the timestamps are skipped with them.
    private readonly bool _recordReadTimes = Metrics.PbtTrieUpdaterNodeGroupReadTimes is not NoopMetricObserver;
    private readonly ArrayPoolList<ArrayPoolList<BufferedNodeGroup>> _handedOff = new(16);
    private readonly PbtGroupLog.OperationLease _operation = bundle.NodeGroupLog.BeginOperation();
    private bool _complete;
    private PbtGroupLog Log => bundle.NodeGroupLog;

    /// <inheritdoc/>
    public PbtGroupReference RootGroupReference => bundle.RootGroupReference;

    /// <inheritdoc/>
    public bool TryGetNodeGroupRecord(PbtGroupReference reference, scoped in PbtTraversalPath groupKey, out PbtGroupRecord record) =>
        bundle.NodeGroupLog.TryGetRecord(_operation, reference, out record)
        && PbtNodePathOperations.Equal(record.Path, groupKey.ToPath<PbtStorageNodePath>());

    /// <inheritdoc/>
    public void CompleteFold() => _complete = true;

    /// <inheritdoc/>
    /// <remarks>Snapshot pins keep in-memory changes in the log, so misses read only the captured persisted layers.</remarks>
    public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey)
    {
        PbtStorageNodePath storagePath = groupKey.ToPath<PbtStorageNodePath>();
        if (!_recordReadTimes) return bundle.GetPersistedNodeGroup(storagePath);

        long start = Stopwatch.GetTimestamp();
        RefCountingMemory? payload = bundle.GetPersistedNodeGroup(storagePath);
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

    /// <summary>Publishes a successful fold's handed-off groups on the owner thread and releases the operation lease.</summary>
    public void Dispose()
    {
        using PbtGroupLog.OperationLease operation = _operation;
        try
        {
            if (_complete)
                foreach (ArrayPoolList<BufferedNodeGroup> buffer in _handedOff)
                    foreach (BufferedNodeGroup group in buffer) bundle.SetNodeGroupReference(group.Path, group.Reference);
        }
        finally
        {
            foreach (ArrayPoolList<BufferedNodeGroup> buffer in _handedOff) buffer.Dispose();
            _handedOff.Dispose();
        }
    }

    private readonly record struct BufferedNodeGroup(PbtStorageNodePath Path, PbtGroupReference Reference);

    private sealed class ConcurrentWriter(PbtSnapshotStore store) : IPbtConcurrentWriter
    {
        private readonly ArrayPoolList<BufferedNodeGroup> _buffer = new(16);

        public PbtGroupReference ReserveNodeGroup(scoped in PbtTraversalPath groupKey, int length, in PbtGroupLinks links, out Span<byte> payload)
        {
            PbtStorageNodePath path = groupKey.ToPath<PbtStorageNodePath>();
            payload = default;
            PbtGroupReference reference = length == 0 ? PbtGroupReference.None
                : store.Log.Reserve(store._operation, path, length, links, out payload);
            _buffer.Add(new BufferedNodeGroup(path, reference));
            return reference;
        }

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, RefCountingMemory? payload)
        {
            if (payload is not null) PbtNodeGroupCodec.DebugValidateNodes(groupKey.ToPath<PbtStorageNodePath>(), payload.GetSpan());
            ReserveNodeGroup(groupKey, payload?.Memory.Length ?? 0, PbtGroupLinks.Unknown, out Span<byte> destination);
            if (payload is not null) payload.GetSpan().CopyTo(destination);
        }

        public void Dispose()
        {
            if (_buffer.Count == 0) _buffer.Dispose();
            else lock (store._handedOff) store._handedOff.Add(_buffer);
        }
    }
}
