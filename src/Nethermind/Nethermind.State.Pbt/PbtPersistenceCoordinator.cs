// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
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

    // Raised to at least one CompactSize above MinReorgDepth so the finalized trigger always has
    // room to act before the backstop fires.
    private readonly ulong _backstopReorgDepth = Math.Max((ulong)config.MaxReorgDepth, (ulong)(config.MinReorgDepth + config.CompactSize));

    // StateId is a 40-byte struct, so a direct field read/write could be observed torn by query
    // threads. Publish it as an immutable boxed reference instead.
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
    public bool CheckPersistence()
    {
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
        // the schedule's boundary, not a fixed stride from the persisted state: it is offset-shifted
        // per node, and it is where a full-width compaction lands
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
        // the using must cover the early return too: the chain rents its backing array before
        // TryLeaseChain can discover the walk is broken, and CheckPersistence takes this no-op path
        // on every signal that has nothing to persist
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

        foreach ((AddressAsKey address, Account? account) in content.Accounts)
        {
            batch.SetAccount(address, account);
        }

        WriteSlots(content, batch);

        foreach ((Stem stem, byte[] blob) in content.LeafBlobs)
        {
            batch.SetLeafBlob(stem, blob);
        }

        foreach ((TrieNodeKey key, byte[]? node) in content.TrieNodes)
        {
            batch.SetTrieNode(key, node);
        }
    }

    /// <remarks>
    /// A flat storage key is the slot's tree key, which costs two BLAKE3 hashes to derive from
    /// scratch. <see cref="PbtSnapshotContent.Slots"/> is an unordered map, so writing it as it
    /// enumerates would pay both per slot; ordering by address then slot lets the batch's
    /// <see cref="PbtSlotKeyDeriver"/> charge one hash per address plus one per 256-slot run. The
    /// order buys nothing on disk — tree keys are digests, so any enumeration order lands randomly
    /// in the column.
    /// </remarks>
    private static void WriteSlots(PbtSnapshotContent content, IPbtPersistence.IWriteBatch batch)
    {
        using ArrayPoolList<KeyValuePair<(AddressAsKey Address, UInt256 Slot), EvmWord>> slots = new(content.Slots.Count);
        foreach (KeyValuePair<(AddressAsKey Address, UInt256 Slot), EvmWord> slot in content.Slots)
        {
            slots.Add(slot);
        }

        slots.Sort(static (a, b) =>
        {
            int byAddress = a.Key.Address.Value.Bytes.SequenceCompareTo(b.Key.Address.Value.Bytes);
            return byAddress != 0 ? byAddress : a.Key.Slot.CompareTo(b.Key.Slot);
        });

        foreach (KeyValuePair<(AddressAsKey Address, UInt256 Slot), EvmWord> slot in slots.AsSpan())
        {
            batch.SetSlot(slot.Key.Address, slot.Key.Slot, slot.Value);
        }
    }
}
