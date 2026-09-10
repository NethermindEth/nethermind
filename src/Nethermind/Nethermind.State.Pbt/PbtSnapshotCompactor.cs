// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.Logging;

namespace Nethermind.State.Pbt;

/// <summary>Merges consecutive canonical snapshot diffs without changing newest-write precedence.</summary>
public class PbtSnapshotCompactor(
    IPbtResourcePool resourcePool,
    PbtCompactionSchedule schedule,
    PbtSnapshotRepository repository,
    IPbtConfig config,
    ILogManager? logManager = null)
{
    private readonly ILogger _logger = (logManager ?? NullLogManager.Instance).GetClassLogger<PbtSnapshotCompactor>();

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
        PbtSnapshot compacted = Compact(chain);
        bool added = repository.TryAddCompacted(compacted);
        if (_logger.IsDebug) _logger.Debug($"Completed Pbt compaction up to {stateId}: added={added}, snapshots={repository.Count}, compactedSnapshots={repository.CompactedCount}, managedBytes={GC.GetTotalMemory(false)}");
        return added;
    }

    public PbtSnapshot Compact(IReadOnlyList<PbtSnapshot> chainOldestFirst)
    {
        PbtResourcePool.Usage usage = PbtResourcePool.CompactUsage(chainOldestFirst.Count);
        PbtSnapshotContent merged = resourcePool.GetSnapshotContent(usage);
        try
        {
            for (int i = 0; i < chainOldestFirst.Count; i++)
            {
                PbtSnapshotContent content = chainOldestFirst[i].Content;
                foreach ((ValueHash256 addressHash, _) in content.SelfDestructedStorageAddresses) merged.ClearStorage(addressHash);
                foreach ((ValueHash256 addressHash, Account? account) in content.Accounts) merged.Accounts[addressHash] = account;
                foreach ((PbtStorageFullKey key, EvmWord value) in content.Storages) merged.Storages[key] = value;
                foreach ((ValueHash256 codeHash, CodeInfo code) in content.Codes) merged.Codes[codeHash] = code;
                foreach ((IPbtNodePath groupKey, RefCountingMemory? payload) in content.NodeGroups) merged.SetNodeGroup(groupKey, payload);
                foreach ((ValueHash256 hash, ulong? count) in content.CodeReferences) merged.SetCodeReference(hash, count);
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
