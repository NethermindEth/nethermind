// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Pbt;

/// <summary>
/// Decides when in-memory diff layers move to disk and performs the move: a finality-driven
/// trigger persists the canonical segment up to the next <see cref="IPbtConfig.CompactSize"/>
/// boundary once it is deeper than <see cref="IPbtConfig.MinReorgDepth"/>, and a backstop
/// force-persists from the committed head when the unpersisted depth exceeds
/// <see cref="IPbtConfig.MaxReorgDepth"/>. Segments are compacted into one layer and written in
/// a single atomic batch; layers are pruned only after the persisted state id advances.
/// </summary>
public class PbtPersistenceCoordinator(
    IPbtConfig config,
    IFinalizedStateProvider finalizedStateProvider,
    IPbtPersistence persistence,
    PbtSnapshotRepository repository,
    PbtSnapshotCompactor compactor,
    PbtCompactionSchedule schedule,
    IStatePersistenceBarrier persistenceBarrier,
    ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtPersistenceCoordinator>();
    private readonly Lock _persistenceLock = new();
    private readonly ulong _compactSize = (ulong)config.CompactSize;
    private readonly ulong _minReorgDepth = (ulong)config.MinReorgDepth;

    // Mirror mode follows the flat backend's ranges to keep persisted pointers aligned.
    private readonly bool _externallyDriven = config.MirrorFlat;

    // Leave one compact window for finality-driven persistence before the backstop fires.
    private readonly ulong _backstopReorgDepth = Math.Max((ulong)config.MaxReorgDepth, (ulong)(config.MinReorgDepth + config.CompactSize));

    // Publish this 40-byte value as an immutable reference to prevent torn reads.
    private StrongBox<StateId>? _currentPersistedState;

    public StateId GetCurrentPersistedStateId()
    {
        StrongBox<StateId>? current = Volatile.Read(ref _currentPersistedState);
        if (current is null)
        {
            using IPbtPersistence.IReader reader = persistence.CreateReader();
            current = new StrongBox<StateId>(reader.CurrentState);
            Volatile.Write(ref _currentPersistedState, current);
        }

        return current.Value;
    }

    /// <summary>Evaluates the persistence triggers, persisting at most a few segments per call; re-invoked on every committed block.</summary>
    /// <returns>Whether anything was persisted, and so whether the persisted state id has advanced.</returns>
    /// <remarks>Does nothing when persistence is driven externally; see <see cref="PersistUpTo"/>.</remarks>
    public bool CheckPersistence()
    {
        if (_externallyDriven) return false;

        lock (_persistenceLock)
        {
            const int maxDrainIterations = 4;
            int persisted = 0;
            for (; persisted < maxDrainIterations && TryPersistOneSegment(); persisted++)
            {
            }

            return persisted > 0;
        }
    }

    /// <summary>
    /// Persists the chain from <paramref name="seed"/> down to the persisted state, whatever the
    /// finality triggers would have said.
    /// </summary>
    /// <remarks>
    /// For a caller that owns the persistence schedule itself — the mirror, which follows the flat
    /// backend's ranges so the two persisted pointers stay equal.
    /// </remarks>
    /// <returns>Whether anything was persisted; false when no chain reaches <paramref name="seed"/>.</returns>
    public bool PersistUpTo(in StateId seed)
    {
        lock (_persistenceLock) return PersistSegment(seed);
    }

    /// <summary>Persists everything up to the last committed head, e.g. after genesis processing or on shutdown.</summary>
    public void FlushToPersistence()
    {
        lock (_persistenceLock)
        {
            if (repository.GetLastCommittedStateId() is { } head && head != GetCurrentPersistedStateId())
            {
                PersistSegment(head);
            }
        }
    }

    private bool TryPersistOneSegment()
    {
        StateId persisted = GetCurrentPersistedStateId();
        if (repository.GetLastCommittedStateId() is not { } head) return false;
        if (persisted != StateId.PreGenesis && head.BlockNumber < persisted.BlockNumber)
        {
            if (_logger.IsWarn) _logger.Warn($"Committed head {head} is below persisted state {persisted}; persisted base may be on an orphaned fork. Skipping persistence.");
            return false;
        }

        ulong depth = persisted == StateId.PreGenesis
            ? head.BlockNumber + 1
            : head.BlockNumber.SaturatingSub(persisted.BlockNumber);
        // The per-node-offset boundary is where full-width compaction lands.
        ulong nextBoundary = schedule.NextFullCompactionAfter(persisted);

        if (finalizedStateProvider.FinalizedBlockNumber >= nextBoundary
            && depth + _compactSize > _minReorgDepth
            && finalizedStateProvider.GetFinalizedStateRootAt(nextBoundary) is Hash256 canonicalRoot
            && PersistSegment(new StateId(nextBoundary, canonicalRoot)))
        {
            return true;
        }

        if (depth > _backstopReorgDepth)
        {
            if (_logger.IsWarn) _logger.Warn($"In-memory state depth {depth} exceeded the force-persist backstop {_backstopReorgDepth}; forcing persistence to bound memory.");
            return PersistSegment(head);
        }

        return false;
    }

    private bool PersistSegment(in StateId seed)
    {
        StateId persisted = GetCurrentPersistedStateId();
        // TryLeaseChain may rent its backing array before discovering a broken walk.
        using PbtSnapshotPooledList chain = new(1);
        if (!repository.TryLeaseChain(seed, persisted, chain) || chain.Count == 0) return false;

        using (PbtSnapshot merged = compactor.Compact(chain))
        {
            persistenceBarrier.FlushDeferred();
            Persist(merged);
            Volatile.Write(ref _currentPersistedState, new StrongBox<StateId>(merged.To));
        }

        repository.RemoveStatesUntil(seed.BlockNumber);
        if (_logger.IsDebug) _logger.Debug($"Persisted pbt state segment up to {seed}");
        return true;
    }

    private void Persist(PbtSnapshot merged)
    {
        PbtSnapshotContent content = merged.Content;
        using IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(merged.From, merged.To, merged.TreeRoot, WriteFlags.None);

        foreach ((PbtFullKey key, ValueHash256? value) in content.Leaves) batch.SetLeaf(key, value);
        foreach ((PbtFullKey locator, byte[]? node) in content.Nodes) batch.SetNode(locator, node ?? []);
        foreach ((ValueHash256 codeHash, ulong? count) in content.CodeReferences) batch.SetCodeReference(codeHash, count);
    }
}
