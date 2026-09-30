// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>Adapts a writable snapshot bundle to the canonical <see cref="TrieUpdater"/> store contract.</summary>
/// <remarks>
/// The bundle's node groups are written only from the owner thread: writers hand their buffered groups over on
/// dispose, and <see cref="Dispose"/> applies them, so they stay invisible until the store is disposed.
/// </remarks>
internal sealed class PbtSnapshotStore(PbtSnapshotBundle bundle) : IPbtStore, IDisposable
{
    private static readonly PbtNodeGroupReadLabel[] _foundLabels = ReadLabels("found");
    private static readonly PbtNodeGroupReadLabel[] _nullLabels = ReadLabels("null");

    // Detailed observers stay no-ops unless Metrics.EnableDetailedMetric, so the timestamps are skipped with them.
    private readonly bool _recordReadTimes = Metrics.PbtTrieUpdaterNodeGroupReadTimes is not NoopMetricObserver;
    private readonly ArrayPoolList<ArrayPoolList<BufferedNodeGroup>> _handedOff = new(16);

    public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash)
    {
        PbtStorageNodePath storagePath = groupKey.ToPath<PbtStorageNodePath>();
        if (!_recordReadTimes) return bundle.GetNodeGroup(storagePath, groupHash);

        long start = Stopwatch.GetTimestamp();
        RefCountingMemory? payload = bundle.GetNodeGroup(storagePath, groupHash);
        Metrics.PbtTrieUpdaterNodeGroupReadTimes.Observe(
            Stopwatch.GetTimestamp() - start,
            (payload is null ? _nullLabels : _foundLabels)[ReadLabelIndex(storagePath)]);
        return payload;
    }

    // Indexed by GetNodeGroupPartition, offset by three for groups the persistence keeps in the top column.
    private static PbtNodeGroupReadLabel[] ReadLabels(string result) =>
        [new("account", result), new("code", result), new("storage", result), new("account_top", result), new("code_top", result), new("storage_top", result)];

    private static int ReadLabelIndex(PbtStorageNodePath groupKey)
    {
        int partition = PbtReadOnlySnapshotBundle.GetNodeGroupPartition(groupKey);
        return PbtRocksDbPersistence.NodeGroupColumn(groupKey) is PbtColumns.TopNodeGroups or PbtColumns.Metadata ? partition + 3 : partition;
    }

    public IPbtConcurrentWriter CreateWriter() => new ConcurrentWriter(this);

    /// <summary>Applies the groups every disposed concurrent writer handed over; must run on the owner thread after the workers finished.</summary>
    public void Dispose()
    {
        try
        {
            foreach (ArrayPoolList<BufferedNodeGroup> buffer in _handedOff)
                foreach (BufferedNodeGroup group in buffer) bundle.SetNodeGroup(group.Path, group.Hash, group.Payload);
        }
        finally
        {
            foreach (ArrayPoolList<BufferedNodeGroup> buffer in _handedOff) ConcurrentWriter.Release(buffer);
            _handedOff.Dispose();
        }
    }

    private readonly record struct BufferedNodeGroup(PbtStorageNodePath Path, ValueHash256 Hash, RefCountingMemory? Payload);

    private sealed class ConcurrentWriter(PbtSnapshotStore store) : IPbtConcurrentWriter
    {
        private readonly ArrayPoolList<BufferedNodeGroup> _buffer = new(16);

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload)
        {
            payload?.AcquireLease();
            _buffer.Add(new BufferedNodeGroup(groupKey.ToPath<PbtStorageNodePath>(), groupHash, payload));
        }

        public void Dispose()
        {
            if (_buffer.Count == 0)
            {
                _buffer.Dispose();
                return;
            }
            lock (store._handedOff) store._handedOff.Add(_buffer);
        }

        internal static void Release(ArrayPoolList<BufferedNodeGroup> buffer)
        {
            foreach (BufferedNodeGroup group in buffer) ((IDisposable?)group.Payload)?.Dispose();
            buffer.Dispose();
        }
    }
}
