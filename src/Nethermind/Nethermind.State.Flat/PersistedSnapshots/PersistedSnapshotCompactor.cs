// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Storage;
using Prometheus;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Manages conversion of in-memory snapshots to persisted snapshots (HSST files)
/// and compaction of persisted snapshots. Mirrors <see cref="SnapshotCompactor"/>'s
/// logarithmic compaction strategy for the persisted layer.
/// </summary>
public class PersistedSnapshotCompactor(
    IPersistedSnapshotRepository persistedSnapshotRepository,
    IArenaManager arenaManager,
    IFlatDbConfig config,
    ILogManager logManager) : IPersistedSnapshotCompactor
{
    private readonly ILogger _logger = logManager.GetClassLogger<PersistedSnapshotCompactor>();
    private readonly int _compactSize = config.CompactSize;
    private readonly int _persistedSnapshotMaxCompactSize = config.PersistedSnapshotMaxCompactSize;
    private readonly int _minCompactSize = Math.Max(config.MinCompactSize, 2);

    /// <summary>
    /// Try to compact persisted snapshots using logarithmic compaction.
    /// Mirrors <see cref="SnapshotCompactor.GetSnapshotsToCompact"/> logic.
    /// Skips compactSize == _compactSize since persistable snapshots are now produced
    /// directly by PersistenceManager from in-memory compacted snapshots.
    /// </summary>
    public void DoCompactSnapshot(StateId snapshotTo)
    {
        if (_compactSize <= 1) return;

        long blockNumber = snapshotTo.BlockNumber;
        if (blockNumber == 0) return;

        int compactSize = (int)Math.Min(blockNumber & -blockNumber, _persistedSnapshotMaxCompactSize);
        if (compactSize < _minCompactSize) return;
        if (compactSize == _compactSize) return; // persistable snapshots produced by PersistenceManager now

        // We need at least 2 snapshots to compact
        if (persistedSnapshotRepository.SnapshotCount < 2) return;

        long startingBlockNumber = ((blockNumber - 1) / compactSize) * compactSize;
        CompactRange(snapshotTo, startingBlockNumber, compactSize, isPersistable: false);
    }


    private Histogram _persistedSnapshotSize =
        Prometheus.Metrics.CreateHistogram("persisted_snapshot_compacted_size", "persisted_snapshot_compacted_size", "size");
    private Histogram _persistedSnapshotCompactTime =
        Prometheus.Metrics.CreateHistogram("persisted_snapshot_compact_time", "persisted_snapshot_compact_time", "size");

    private void CompactRange(StateId snapshotTo, long startingBlockNumber, int compactSize, bool isPersistable)
    {
        using PersistedSnapshotList snapshots = persistedSnapshotRepository.AssembleSnapshotsForCompaction(snapshotTo, startingBlockNumber);
        if (snapshots.Count < 2) return;

        if (snapshots[0].From.BlockNumber != startingBlockNumber)
        {
            if (_logger.IsDebug) _logger.Debug($"Unable to compile persisted snapshots to compact. {snapshots[0].From.BlockNumber} -> {snapshots[snapshots.Count - 1].To.BlockNumber}. Starting block number should be {startingBlockNumber}");
            return;
        }

        if (_logger.IsDebug) _logger.Debug($"Compacting {snapshots.Count} persisted snapshots at block {snapshotTo.BlockNumber}, compact size {compactSize}, persistable {isPersistable}");

        StateId from = snapshots[0].From;
        StateId to = snapshots[snapshots.Count - 1].To;

        // Collect all base snapshot IDs that the compacted result will reference via NodeRefs
        HashSet<int> referencedIds = new();
        for (int i = 0; i < snapshots.Count; i++)
        {
            if (snapshots[i].Type == PersistedSnapshotType.Full)
            {
                referencedIds.Add(snapshots[i].Id);
            }
            else if (snapshots[i].ReferencedSnapshotIds is int[] ids)
            {
                for (int j = 0; j < ids.Length; j++) referencedIds.Add(ids[j]);
            }
        }

        int totalSize = 0;
        for (int i = 0; i < snapshots.Count; i++) totalSize += snapshots[i].Size;
        totalSize += 4096;

        ArenaReservation reservation = arenaManager.ReserveForWrite(totalSize);
        try
        {
            long sw = Stopwatch.GetTimestamp();
            SpanBufferWriter compactWriter = new(reservation.GetSpan());
            PersistedSnapshotBuilder.NWayMergeSnapshots(snapshots, ref compactWriter, referencedIds);
            int len = compactWriter.Written;
            _persistedSnapshotSize.WithLabels($"size{compactSize}").Observe(len);
            _persistedSnapshotCompactTime.WithLabels($"size{compactSize}").Observe(Stopwatch.GetTimestamp() - sw);

            reservation.Size = len;
            PersistedSnapshot compacted = new(0, from, to, PersistedSnapshotType.Linked, reservation);
            try
            {
                PersistedSnapshotUtils.ValidateCompactedPersistedSnapshot(compacted, snapshots, true);
            }
            finally
            {
                compacted.Dispose();
            }

            persistedSnapshotRepository.AddCompactedSnapshot(from, to, reservation, len, referencedIds, isPersistable);
        }
        catch
        {
            reservation.Return();
            throw;
        }

        Metrics.PersistedSnapshotCompactions++;
        Metrics.PersistedSnapshotCount = persistedSnapshotRepository.SnapshotCount;
        Metrics.PersistedSnapshotMemory = persistedSnapshotRepository.BaseSnapshotMemory;
        Metrics.CompactedPersistedSnapshotMemory = persistedSnapshotRepository.CompactedSnapshotMemory;
    }
}
