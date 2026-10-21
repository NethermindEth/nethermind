// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
    IPbtPersistence persistence, PbtSnapshotRepository repository, ICompactionSchedule schedule,
    IStatePersistenceBarrier persistenceBarrier, ILogManager logManager, IPbtPersistedSnapshotLoader loader,
    IPbtPersistedSnapshotCompactor compactor, CancellationToken shutdown) : IDisposable
{
    private readonly IPbtConfig _config = config;
    private readonly IStateHeaderProvider _finalized = finalizedStateProvider;
    private readonly IPbtPersistence _persistence = persistence;
    private readonly PbtSnapshotRepository _repository = repository;
    private readonly ICompactionSchedule _schedule = schedule;
    private readonly IStatePersistenceBarrier _barrier = persistenceBarrier;
    private readonly IPbtPersistedSnapshotLoader _loader = loader;
    private readonly IPbtPersistedSnapshotCompactor _compactor = compactor;
    private readonly CancellationToken _shutdown = shutdown;
    private readonly ILogger _logger = logManager.GetClassLogger<PbtPersistenceManager>();
    private readonly SemaphoreSlim _persistenceLock = new(1, 1);
    private readonly ulong _backstopReorgDepth = Math.Max(config.EnableLongFinality ? config.LongFinalityMaxReorgDepth : (ulong)config.MaxReorgDepth,
        (ulong)config.MinReorgDepth + (ulong)config.CompactSize);
    // StateId is wider than an atomic write; publish immutable boxes so readers cannot observe torn roots.
    private StrongBox<StateId>? _currentPersistedState;

    public IPbtConfig Configuration => _config;

    public StateId GetCurrentPersistedStateId()
    {
        StrongBox<StateId>? current = Volatile.Read(ref _currentPersistedState);
        if (current is null)
        {
            using IPbtPersistence.IReader reader = _persistence.CreateReader();
            StrongBox<StateId> loaded = new(reader.CurrentState);
            current = Interlocked.CompareExchange(ref _currentPersistedState, loaded, null) ?? loaded;
        }
        return current.Value;
    }

    public void ResetPersistedStateId()
    {
        _persistence.ClearCaches();
        using IPbtPersistence.IReader reader = _persistence.CreateReader();
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
        ulong nextBoundary = _schedule.NextFullCompactionAfter(persisted);
        if (_finalized.FinalizedBlockNumber >= nextBoundary
            && latest.BlockNumber.SaturatingSub(nextBoundary) >= (ulong)_config.MinReorgDepth
            && _finalized.GetFinalizedHeader(nextBoundary)?.StateRoot is Hash256 root)
        {
            (PbtPersistedSnapshot?, PbtSnapshot?) candidate = FindCandidate(new(nextBoundary, root), persisted);
            if (candidate is not (null, null)) return new() { Persist = candidate };
        }
        if (depth > _backstopReorgDepth)
        {
            (PbtPersistedSnapshot?, PbtSnapshot?) candidate = FindCandidate(ForcedSeed(latest), persisted);
            if (candidate is not (null, null)) return new() { Persist = candidate };
        }
        if (_config.EnableLongFinality && _repository.Count > _config.MaxInMemoryBaseSnapshotCount)
        {
            using ArrayPoolList<StateId> ordered = _repository.GetInMemoryStates();
            foreach (StateId state in ordered)
            {
                if (!_repository.TryLeaseMemoryState(state, SnapshotTier.InMemoryCompacted, out PbtSnapshot? compacted)) continue;
                if (unchecked(compacted!.To.BlockNumber - compacted.From.BlockNumber) == (ulong)_config.CompactSize
                    && _repository.IsOnDisk(compacted.From, persisted)) return new() { Convert = compacted, ConvertRange = true };
                compacted.Dispose();
            }
            foreach (StateId state in ordered)
            {
                if (!_repository.TryLeaseMemoryState(state, SnapshotTier.InMemoryBase, out PbtSnapshot? snapshot)) continue;
                if (_repository.IsOnDisk(snapshot!.From, persisted)) return new() { Convert = snapshot };
                snapshot.Dispose();
            }
        }
        if (_config.MaxInMemorySnapshotBytes > 0 && depth > (ulong)_config.MinReorgDepth
            && (ulong)_repository.InMemorySnapshotBytes > _config.MaxInMemorySnapshotBytes)
        {
            // Do not skip a persisted candidate to find a memory candidate: it is the next edge to the base.
            (PbtPersistedSnapshot? persistedCandidate, PbtSnapshot? memoryCandidate) = FindCandidate(ForcedSeed(latest), persisted);
            persistedCandidate?.Dispose();
            if (memoryCandidate is not null)
            {
                StateId head = _repository.GetLastCommittedStateId() ?? latest;
                if (head.BlockNumber.SaturatingSub(memoryCandidate.To.BlockNumber) >= (ulong)_config.MinReorgDepth)
                    return new() { Persist = (null, memoryCandidate) };
                memoryCandidate.Dispose();
            }
        }
        return new();
    }

    private StateId ForcedSeed(in StateId latest) => _repository.GetLastCommittedStateId() ?? _repository.GetLastSnapshotId() ?? latest;
    private bool AcceptFinalizedRoot(StateId state) => _finalized.GetFinalizedHeader(state.BlockNumber)?.StateRoot is not { } root || state.StateRoot == root.ValueHash256;
    private (PbtPersistedSnapshot? Persisted, PbtSnapshot? InMemory) FindCandidate(in StateId seed, in StateId persisted) =>
        _repository.FindCandidateToPersist(seed, persisted, (ulong)_config.CompactSize, AcceptFinalizedRoot);

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
            _repository.RemoveFinalizedPersistedForks(GetCurrentPersistedStateId(), _finalized);
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
            using ArrayPoolList<StateId> states = range ? _repository.GetInMemoryStates() : new(1) { candidate.To };
            foreach (StateId state in states)
            {
                _shutdown.ThrowIfCancellationRequested();
                if (range && ((long)state.BlockNumber <= (long)candidate.From.BlockNumber || (long)state.BlockNumber > (long)candidate.To.BlockNumber)) continue;
                if (!_repository.TryLeaseMemoryState(state, SnapshotTier.InMemoryBase, out PbtSnapshot? snapshot)) continue;
                using (snapshot)
                {
                    if (!_repository.IsOnDisk(snapshot!.From, persisted) && !converted.Contains(snapshot.From)) continue;
                    if (!_loader.ConvertAndRegister(snapshot)) continue;
                    converted.Add(state);
                    _repository.RemoveMemorySource(snapshot);
                    if (_repository.TryLeaseMemoryState(state, SnapshotTier.InMemoryCompacted, out PbtSnapshot? compacted))
                    {
                        using (compacted) _repository.RemoveMemorySource(compacted!);
                    }
                }
            }
            return converted.Count > 0;
        }
        finally
        {
            if (converted.Count == 0) converted.Dispose();
            else await _compactor.EnqueueAsync(converted, GetCurrentPersistedStateId().BlockNumber, _shutdown);
        }
    }

    public StateId FlushToPersistenceState(CancellationToken cancellationToken)
    {
        _persistenceLock.Wait();
        try
        {
            StateId persisted = GetCurrentPersistedStateId();
            StateId? head = _repository.GetLastCommittedStateId() ?? _repository.GetLastSnapshotId();
            if (head is null) return persisted;
            while (!cancellationToken.IsCancellationRequested && (persisted == StateId.PreGenesis || persisted.BlockNumber < head.Value.BlockNumber))
            {
                StateId seed = head.Value;
                ulong finalized = _finalized.FinalizedBlockNumber;
                if ((persisted == StateId.PreGenesis || finalized > persisted.BlockNumber)
                    && _finalized.GetFinalizedHeader(finalized)?.StateRoot is Hash256 root) seed = new(finalized, root);
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
        _barrier.FlushDeferred();
        StateId to = candidate.InMemory?.To ?? candidate.Persisted!.To;
        using (IPbtPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(candidate.InMemory?.From ?? candidate.Persisted!.From, to,
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
                foreach (PbtGroupRecord group in content.EnumerateNodeGroups())
                {
                    using RefCountingMemory payload = PooledRefCountingMemoryProvider.Instance.Rent(group.Payload.Length);
                    group.Payload.Span.CopyTo(payload.GetSpan());
                    batch.SetNodeGroup(group.Path, payload);
                }
            }
            else candidate.Persisted!.ApplyTo(batch, cancellationToken);
            batch.Commit();
        }
        Volatile.Write(ref _currentPersistedState, new StrongBox<StateId>(to));
        _repository.RemoveSiblingAndDescendents(to);
        _repository.RemoveStatesUntil(to.BlockNumber);
        _repository.RemovePersistedStatesBefore(to.BlockNumber);
    }

    public bool DropStateNotReachableFrom(in StateId head)
    {
        _persistenceLock.Wait();
        try
        {
            StateId persisted = GetCurrentPersistedStateId();
            if (!_repository.TryRemoveUnreachableFrom(head, persisted, out int removed))
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
