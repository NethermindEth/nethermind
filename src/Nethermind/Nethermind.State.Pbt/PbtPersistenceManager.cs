// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt;

/// <summary>Coordinates base persistence and durable snapshot conversion.</summary>
public class PbtPersistenceManager(IPbtConfig config, IStateHeaderProvider finalizedStateProvider,
    IPbtPersistence persistence, PbtSnapshotRepository repository, [KeyFilter(DbNames.Pbt)] ICompactionSchedule schedule,
    IStatePersistenceBarrier persistenceBarrier, ILogManager logManager, IPbtPersistedSnapshotLoader loader,
    IPbtPersistedSnapshotCompactor compactor, IProcessExitSource processExitSource) : IDisposable
{
    private readonly CancellationToken _shutdown = processExitSource.Token;
    private readonly ILogger _logger = logManager.GetClassLogger<PbtPersistenceManager>();
    private readonly SemaphoreSlim _persistenceLock = new(1, 1);
    private readonly ulong _backstopReorgDepth = Math.Max(config.EnableLongFinality ? config.LongFinalityMaxReorgDepth : (ulong)config.MaxReorgDepth,
        (ulong)config.MinReorgDepth + (ulong)config.CompactSize);
    // StateId is wider than an atomic write; publish immutable boxes so readers cannot observe torn roots.
    private StrongBox<StateId>? _currentPersistedState;

    public StateId GetCurrentPersistedStateId()
    {
        StrongBox<StateId>? current = Volatile.Read(ref _currentPersistedState);
        if (current is null)
        {
            using IPbtPersistence.IReader reader = persistence.CreateReader();
            StrongBox<StateId> loaded = new(reader.CurrentState);
            current = Interlocked.CompareExchange(ref _currentPersistedState, loaded, null) ?? loaded;
        }
        return current.Value;
    }

    public void ResetPersistedStateId()
    {
        persistence.ClearCaches();
        using IPbtPersistence.IReader reader = persistence.CreateReader();
        Volatile.Write(ref _currentPersistedState, new StrongBox<StateId>(reader.CurrentState));
    }

    private sealed class SnapshotAction : IDisposable
    {
        public (PbtPersistedSnapshot? Persisted, PbtSnapshot? InMemory) Persist { get; init; }
        public PbtSnapshot? Convert { get; init; }
        public bool ConvertRange { get; init; }
        public void Dispose() { Persist.Persisted?.Dispose(); Persist.InMemory?.Dispose(); Convert?.Dispose(); }
    }

    private SnapshotAction DetermineSnapshotAction(in StateId latest)
    {
        StateId persisted = GetCurrentPersistedStateId();
        ulong depth = persisted == StateId.PreGenesis ? latest.BlockNumber + 1 : latest.BlockNumber.SaturatingSub(persisted.BlockNumber);
        if (persisted != StateId.PreGenesis && latest.BlockNumber < persisted.BlockNumber && _logger.IsWarn)
            _logger.Warn($"Latest PBT snapshot {latest} is below persisted state {persisted}.");
        ulong nextBoundary = schedule.NextFullCompactionAfter(persisted);
        if (finalizedStateProvider.FinalizedBlockNumber >= nextBoundary
            && latest.BlockNumber.SaturatingSub(nextBoundary) >= (ulong)config.MinReorgDepth
            && finalizedStateProvider.GetFinalizedHeader(nextBoundary)?.StateRoot is Hash256 root)
        {
            (PbtPersistedSnapshot?, PbtSnapshot?) candidate = FindCandidate(new(nextBoundary, root), persisted);
            if (candidate is not (null, null)) return new() { Persist = candidate };
        }
        if (depth > _backstopReorgDepth)
        {
            (PbtPersistedSnapshot?, PbtSnapshot?) candidate = FindCandidate(ForcedSeed(latest), persisted);
            if (candidate is not (null, null)) return new() { Persist = candidate };
        }
        if (config.EnableLongFinality && repository.Count > config.MaxInMemoryBaseSnapshotCount)
        {
            using ArrayPoolList<StateId> ordered = repository.GetInMemoryStates();
            foreach (StateId state in ordered)
            {
                if (!repository.TryLeaseMemoryState(state, SnapshotTier.InMemoryCompacted, out PbtSnapshot? compacted)) continue;
                if (unchecked(compacted!.To.BlockNumber - compacted.From.BlockNumber) == (ulong)config.CompactSize
                    && repository.IsOnDisk(compacted.From, persisted)) return new() { Convert = compacted, ConvertRange = true };
                compacted.Dispose();
            }
            foreach (StateId state in ordered)
            {
                if (!repository.TryLeaseMemoryState(state, SnapshotTier.InMemoryBase, out PbtSnapshot? snapshot)) continue;
                if (repository.IsOnDisk(snapshot!.From, persisted)) return new() { Convert = snapshot };
                snapshot.Dispose();
            }
        }
        if (config.MaxInMemorySnapshotBytes > 0 && depth > (ulong)config.MinReorgDepth
            && (ulong)repository.InMemorySnapshotBytes > config.MaxInMemorySnapshotBytes)
        {
            // Do not skip a persisted candidate to find a memory candidate: it is the next edge to the base.
            (PbtPersistedSnapshot? persistedCandidate, PbtSnapshot? memoryCandidate) = FindCandidate(ForcedSeed(latest), persisted);
            persistedCandidate?.Dispose();
            if (memoryCandidate is not null)
            {
                StateId head = repository.GetLastCommittedStateId() ?? latest;
                if (head.BlockNumber.SaturatingSub(memoryCandidate.To.BlockNumber) >= (ulong)config.MinReorgDepth)
                    return new() { Persist = (null, memoryCandidate) };
                memoryCandidate.Dispose();
            }
        }
        return new();
    }

    private StateId ForcedSeed(in StateId latest) => repository.GetLastCommittedStateId() ?? repository.GetLastSnapshotId() ?? latest;
    private bool AcceptFinalizedRoot(StateId state) => finalizedStateProvider.GetFinalizedHeader(state.BlockNumber)?.StateRoot is not { } root || state.StateRoot == root.ValueHash256;
    private (PbtPersistedSnapshot? Persisted, PbtSnapshot? InMemory) FindCandidate(in StateId seed, in StateId persisted) =>
        repository.FindCandidateToPersist(seed, persisted, (ulong)config.CompactSize, AcceptFinalizedRoot);

    /// <summary>Evaluates at most four persistence or conversion actions.</summary>
    public async Task<bool> CheckPersistenceAsync(StateId latestSnapshot)
    {
        await _persistenceLock.WaitAsync();
        bool changed = false;
        try
        {
            for (int iteration = 0; iteration < 4; iteration++)
            {
                using SnapshotAction action = DetermineSnapshotAction(latestSnapshot);
                if (action.Persist is not (null, null)) { Persist(action.Persist, _shutdown); changed = true; }
                else if (action.Convert is { } snapshot)
                {
                    changed |= await Convert(snapshot, action.ConvertRange);
                }
                else break;
            }
            repository.RemoveFinalizedPersistedForks(GetCurrentPersistedStateId(), finalizedStateProvider);
            return changed;
        }
        finally { _persistenceLock.Release(); }
    }

    private async Task<bool> Convert(PbtSnapshot candidate, bool range)
    {
        ArrayPoolList<StateId> converted = new(64);
        try
        {
            StateId persisted = GetCurrentPersistedStateId();
            using ArrayPoolList<StateId> states = range ? repository.GetInMemoryStates() : new(1) { candidate.To };
            foreach (StateId state in states)
            {
                _shutdown.ThrowIfCancellationRequested();
                if (range && ((long)state.BlockNumber <= (long)candidate.From.BlockNumber || (long)state.BlockNumber > (long)candidate.To.BlockNumber)) continue;
                if (!repository.TryLeaseMemoryState(state, SnapshotTier.InMemoryBase, out PbtSnapshot? snapshot)) continue;
                using (snapshot)
                {
                    if (!repository.IsOnDisk(snapshot!.From, persisted) && !converted.Contains(snapshot.From)) continue;
                    if (!loader.ConvertAndRegister(snapshot)) continue;
                    converted.Add(state);
                    repository.RemoveMemorySource(snapshot);
                    if (repository.TryLeaseMemoryState(state, SnapshotTier.InMemoryCompacted, out PbtSnapshot? compacted))
                    {
                        using (compacted) repository.RemoveMemorySource(compacted!);
                    }
                }
            }
            return converted.Count > 0;
        }
        finally
        {
            if (converted.Count == 0) converted.Dispose();
            else await compactor.EnqueueAsync(converted, GetCurrentPersistedStateId().BlockNumber, _shutdown);
        }
    }

    public StateId FlushToPersistenceState(CancellationToken cancellationToken)
    {
        _persistenceLock.Wait();
        try
        {
            StateId persisted = GetCurrentPersistedStateId();
            StateId? head = repository.GetLastCommittedStateId() ?? repository.GetLastSnapshotId();
            if (head is null) return persisted;
            while (!cancellationToken.IsCancellationRequested && (persisted == StateId.PreGenesis || persisted.BlockNumber < head.Value.BlockNumber))
            {
                StateId seed = head.Value;
                ulong finalized = finalizedStateProvider.FinalizedBlockNumber;
                if ((persisted == StateId.PreGenesis || finalized > persisted.BlockNumber)
                    && finalizedStateProvider.GetFinalizedHeader(finalized)?.StateRoot is Hash256 root) seed = new(finalized, root);
                (PbtPersistedSnapshot? persistedCandidate, PbtSnapshot? memoryCandidate) = FindCandidate(seed, persisted);
                if (persistedCandidate is null && memoryCandidate is null) break;
                using (persistedCandidate)
                using (memoryCandidate)
                    Persist((persistedCandidate, memoryCandidate), cancellationToken);
                persisted = GetCurrentPersistedStateId();
            }
            return persisted;
        }
        finally { _persistenceLock.Release(); }
    }

    private void Persist((PbtPersistedSnapshot? Persisted, PbtSnapshot? InMemory) candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        persistenceBarrier.FlushDeferred();
        StateId to = candidate.InMemory?.To ?? candidate.Persisted!.To;
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(candidate.InMemory?.From ?? candidate.Persisted!.From, to,
            candidate.InMemory?.TreeRoot ?? candidate.Persisted!.TreeRoot))
        {
            if (candidate.InMemory is { } snapshot)
            {
                PbtSnapshotContent content = snapshot.Content;
                foreach ((ValueHash256 addressHash, PbtAccount? account) in content.Accounts) batch.SetAccount(addressHash, account);
                foreach ((HashedKey<PbtPath> runKey, PackedSlotRun run) in content.HeaderStorages) batch.SetSlotRun(runKey.Key, run);
                foreach ((HashedKey<PbtStoragePath> runKey, PackedSlotRun run) in content.Storages) batch.SetSlotRun(runKey.Key, run);
                foreach ((ValueHash256 codeHash, CodeInfo code) in content.Codes) batch.SetCode(codeHash, code);
                // Only groups reachable from the new root are written; a removed group's stale bytes stay unreachable.
                foreach (PbtGroupRecord group in content.EnumerateNodeGroups()) batch.SetNodeGroup(group.Path, group.Payload.Span);
            }
            else candidate.Persisted!.ApplyTo(batch, cancellationToken);
            batch.Commit();
        }
        Volatile.Write(ref _currentPersistedState, new StrongBox<StateId>(to));
        repository.RemoveSiblingAndDescendents(to);
        repository.RemoveStatesUntil(to.BlockNumber);
        repository.RemovePersistedStatesBefore(to.BlockNumber);
    }

    public bool DropStateNotReachableFrom(in StateId head)
    {
        _persistenceLock.Wait();
        try
        {
            StateId persisted = GetCurrentPersistedStateId();
            if (!repository.TryRemoveUnreachableFrom(head, persisted, out int removed))
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot reset PBT head to {head}: its state is unavailable or does not descend from persisted state {persisted}.");
                return false;
            }
            if (removed > 0 && _logger.IsInfo) _logger.Info($"Pruned {removed} PBT state(s) unreachable from head {head}.");
            return true;
        }
        finally { _persistenceLock.Release(); }
    }

    public void Dispose() => _persistenceLock.Dispose();
}
