// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Memory;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>Merges consecutive canonical snapshot diffs without changing newest-write precedence.</summary>
public class PbtSnapshotCompactor(
    IPbtResourcePool resourcePool,
    [KeyFilter(DbNames.Pbt)] ICompactionSchedule schedule,
    PbtSnapshotRepository repository,
    IPbtConfig config,
    ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtSnapshotCompactor>();

    public bool DoCompactSnapshot(in StateId stateId)
    {
        ulong width = schedule.GetCompactSize(stateId.BlockNumber);
        if (width <= 1) return false;
        if (width < (ulong)config.CompactSize && stateId.BlockNumber >= (ulong)config.CompactSize)
            repository.RemoveCompactedAt(stateId.BlockNumber - (ulong)config.CompactSize);
        using PbtSnapshotPooledList chain = new((int)width);
        long floor = checked((long)stateId.BlockNumber - (long)width);
        if (!repository.TryLeaseCompactionWindow(stateId, floor, chain)) return false;
        if (_logger.IsDebug) _logger.Debug($"Compacting Pbt snapshots {chain[0].From} -> {stateId}: chainCount={chain.Count}, snapshots={repository.Count}, compactedSnapshots={repository.CompactedCount}, managedBytes={GC.GetTotalMemory(false)}");
        PbtSnapshot compacted = Compact(chain, PbtResourcePool.CompactUsage((int)width));
        bool added = repository.TryAddCompacted(compacted);
        if (_logger.IsDebug) _logger.Debug($"Completed Pbt compaction up to {stateId}: added={added}, snapshots={repository.Count}, compactedSnapshots={repository.CompactedCount}, managedBytes={GC.GetTotalMemory(false)}");
        return added;
    }

    public PbtSnapshot Compact(IReadOnlyList<PbtSnapshot> chainOldestFirst) =>
        Compact(chainOldestFirst, PbtResourcePool.CompactUsage(chainOldestFirst.Count));

    private PbtSnapshot Compact(IReadOnlyList<PbtSnapshot> chainOldestFirst, PbtResourcePool.Usage usage)
    {
        PbtSnapshotContent merged = resourcePool.GetSnapshotContent(usage);
        try
        {
            for (int i = 0; i < chainOldestFirst.Count; i++)
            {
                PbtSnapshotContent content = chainOldestFirst[i].Content;
                foreach ((ValueHash256 addressHash, _) in content.SelfDestructedStorageAddresses) merged.ClearStorage(addressHash);
                foreach ((ValueHash256 addressHash, PbtAccount? account) in content.Accounts) merged.Accounts[addressHash] = account;
                foreach ((HashedKey<PbtPath> runKey, PackedSlotRun run) in content.HeaderStorages) merged.SetRun(runKey, run.Clone());
                foreach ((HashedKey<PbtStoragePath> runKey, PackedSlotRun run) in content.Storages) merged.SetRun(runKey, run.Clone());
                foreach ((ValueHash256 codeHash, CodeInfo code) in content.Codes) merged.Codes[codeHash] = code;
                foreach ((PbtNodePath groupKey, RefCountingMemory? payload) in content.AccountNodeGroups) merged.SetNodeGroup(groupKey, payload);
                foreach ((PbtNodePath groupKey, RefCountingMemory? payload) in content.CodeNodeGroups) merged.SetNodeGroup(groupKey, payload);
                foreach ((PbtStorageNodePath groupKey, RefCountingMemory? payload) in content.StorageNodeGroups) merged.SetNodeGroup(groupKey, payload);
            }

            PbtSnapshot newest = chainOldestFirst[^1];
            return new PbtSnapshot(chainOldestFirst[0].From, newest.To, newest.TreeRoot, merged, resourcePool, usage);
        }
        catch
        {
            resourcePool.ReturnSnapshotContent(usage, merged);
            throw;
        }
    }
}
