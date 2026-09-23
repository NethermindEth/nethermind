// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>Adapts a writable snapshot bundle to the canonical <see cref="TrieUpdater"/> store contract.</summary>
internal sealed class PbtSnapshotStore(PbtSnapshotBundle bundle) : IPbtStore
{
    private static readonly PbtNodeGroupReadLabel[] _foundLabels = ReadLabels("found");
    private static readonly PbtNodeGroupReadLabel[] _nullLabels = ReadLabels("null");

    // Detailed observers stay no-ops unless Metrics.EnableDetailedMetric, so the timestamps are skipped with them.
    private readonly bool _recordReadTimes = Metrics.PbtTrieUpdaterNodeGroupReadTimes is not NoopMetricObserver;

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

    public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload) => bundle.SetNodeGroup(groupKey.ToPath<PbtStorageNodePath>(), groupHash, payload);
}
