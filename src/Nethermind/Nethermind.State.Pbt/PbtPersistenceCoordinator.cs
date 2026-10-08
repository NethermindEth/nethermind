// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.PersistedSnapshots;

namespace Nethermind.State.Pbt;

/// <summary>Coordinates base persistence and durable snapshot conversion.</summary>
public class PbtPersistenceCoordinator : IDisposable
{
    private readonly IPbtConfig _config;
    private readonly IStateHeaderProvider _finalized;
    private readonly IPbtPersistence _persistence;
    private readonly PbtSnapshotRepository _repository;
    private readonly ICompactionSchedule _schedule;
    private readonly IStatePersistenceBarrier _barrier;
    private readonly IPbtRetainedSnapshotLoader _loader;
    private readonly IPbtRetainedSnapshotCompactor _compactor;
    private readonly CancellationToken _shutdown;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _persistenceLock = new(1, 1);
    private readonly ulong _backstopReorgDepth;
    // StateId is wider than an atomic write; publish immutable boxes so readers cannot observe torn roots.
    private StrongBox<StateId>? _currentPersistedState;

    public PbtPersistenceCoordinator(IPbtConfig config, IStateHeaderProvider finalizedStateProvider,
        IPbtPersistence persistence, PbtSnapshotRepository repository, [KeyFilter(DbNames.Pbt)] ICompactionSchedule schedule,
        IStatePersistenceBarrier persistenceBarrier, ILogManager logManager)
        : this(config, finalizedStateProvider, persistence, repository, schedule, persistenceBarrier, logManager,
            NullPbtRetainedSnapshotLoader.Instance, NullPbtRetainedSnapshotCompactor.Instance, CancellationToken.None)
    { }

    internal PbtPersistenceCoordinator(IPbtConfig config, IStateHeaderProvider finalizedStateProvider,
        IPbtPersistence persistence, PbtSnapshotRepository repository, ICompactionSchedule schedule,
        IStatePersistenceBarrier persistenceBarrier, ILogManager logManager, IPbtRetainedSnapshotLoader loader,
        IPbtRetainedSnapshotCompactor compactor, CancellationToken shutdown)
    {
        _config = config;
        _finalized = finalizedStateProvider;
        _persistence = persistence;
        _repository = repository;
        _schedule = schedule;
        _barrier = persistenceBarrier;
        _loader = loader;
        _compactor = compactor;
        _shutdown = shutdown;
        _logger = logManager.GetClassLogger<PbtPersistenceCoordinator>();
        _backstopReorgDepth = Math.Max(config.EnableLongFinality ? config.LongFinalityMaxReorgDepth : (ulong)config.MaxReorgDepth,
            (ulong)config.MinReorgDepth + (ulong)config.CompactSize);
    }

    internal IPbtConfig Configuration => _config;

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

    internal sealed class SnapshotAction : IDisposable
    {
        internal PbtSnapshotLease? Persist { get; init; }
        internal PbtSnapshot? Convert { get; init; }
        internal bool ConvertRange { get; init; }
        public void Dispose() { Persist?.Dispose(); Convert?.Dispose(); }
    }

    internal SnapshotAction DetermineSnapshotAction(in StateId latest)
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
            PbtSnapshotLease? candidate = FindCandidate(new(nextBoundary, root), persisted);
            if (candidate is not null) return new() { Persist = candidate };
        }
        if (depth > _backstopReorgDepth)
        {
            PbtSnapshotLease? candidate = FindCandidate(ForcedSeed(latest), persisted);
            if (candidate is not null) return new() { Persist = candidate };
        }
        if (_config.EnableLongFinality && _repository.Count > _config.MaxInMemoryBaseSnapshotCount)
        {
            StateId[] ordered = _repository.GetInMemoryStates();
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
            // Do not skip a retained candidate to find a memory candidate: it is the next edge to the base.
            PbtSnapshotLease? candidate = FindCandidate(ForcedSeed(latest), persisted);
            if (candidate is not null)
            {
                StateId head = _repository.GetLastCommittedStateId() ?? latest;
                if (candidate.Memory is not null && head.BlockNumber.SaturatingSub(candidate.To.BlockNumber) >= (ulong)_config.MinReorgDepth)
                    return new() { Persist = candidate };
                candidate.Dispose();
            }
        }
        return new();
    }

    private StateId ForcedSeed(in StateId latest) => _repository.GetLastCommittedStateId() ?? _repository.GetLastSnapshotId() ?? latest;
    private bool AcceptFinalizedRoot(StateId state) => _finalized.GetFinalizedHeader(state.BlockNumber)?.StateRoot is not { } root || state.StateRoot == root.ValueHash256;
    private PbtSnapshotLease? FindCandidate(in StateId seed, in StateId persisted) =>
        _repository.FindCandidateToPersist(seed, persisted, (ulong)_config.CompactSize, AcceptFinalizedRoot);

    /// <summary>Evaluates at most four persistence or conversion actions.</summary>
    public bool CheckPersistence(in StateId latestSnapshot) => CheckPersistenceAsync(latestSnapshot).GetAwaiter().GetResult();

    internal async Task<bool> CheckPersistenceAsync(StateId latestSnapshot)
    {
        await _persistenceLock.WaitAsync();
        bool changed = false;
        try
        {
            for (int iteration = 0; iteration < 4; iteration++)
            {
                using SnapshotAction action = DetermineSnapshotAction(latestSnapshot);
                if (action.Persist is { } candidate) { Persist(candidate, _shutdown); changed = true; }
                else if (action.Convert is { } snapshot)
                {
                    changed |= await Convert(snapshot, action.ConvertRange);
                }
                else break;
            }
            _repository.RemoveFinalizedRetainedForks(GetCurrentPersistedStateId(), _finalized);
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
            StateId[] states = range ? _repository.GetInMemoryStates() : [candidate.To];
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

    public void FlushToPersistence(CancellationToken cancellationToken) => FlushToPersistenceState(cancellationToken);

    internal StateId FlushToPersistenceState(CancellationToken cancellationToken)
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
                using PbtSnapshotLease? candidate = FindCandidate(seed, persisted);
                if (candidate is null) break;
                Persist(candidate, cancellationToken);
                persisted = GetCurrentPersistedStateId();
            }
            return persisted;
        }
        finally { _persistenceLock.Release(); }
    }

    private void Persist(PbtSnapshotLease candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _barrier.FlushDeferred();
        using (IPbtPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(candidate.From, candidate.To, candidate.TreeRoot, WriteFlags.None))
        {
            if (candidate.Memory is { } snapshot)
            {
                PbtSnapshotContent content = snapshot.Content;
                foreach ((ValueHash256 addressHash, PbtAccount? account) in content.Accounts) batch.SetAccount(addressHash, account);
                foreach ((HashedKey<PbtPath> runKey, PackedSlotRun run) in content.HeaderStorages) batch.SetSlotRun(runKey.Key, run);
                foreach ((HashedKey<PbtStoragePath> runKey, PackedSlotRun run) in content.Storages) batch.SetSlotRun(runKey.Key, run);
                foreach ((ValueHash256 codeHash, CodeInfo code) in content.Codes) batch.SetCode(codeHash, code);
                foreach ((PbtNodePath groupKey, RefCountingMemory? payload) in content.AccountNodeGroups) batch.SetNodeGroup(groupKey, payload);
                foreach ((PbtNodePath groupKey, RefCountingMemory? payload) in content.CodeNodeGroups) batch.SetNodeGroup(groupKey, payload);
                foreach ((PbtStorageNodePath groupKey, RefCountingMemory? payload) in content.StorageNodeGroups) batch.SetNodeGroup(groupKey, payload);
            }
            else candidate.Retained!.ApplyTo(batch, cancellationToken);
            batch.Commit();
        }
        Volatile.Write(ref _currentPersistedState, new StrongBox<StateId>(candidate.To));
        _repository.RemoveSiblingAndDescendents(candidate.To);
        _repository.RemoveStatesUntil(candidate.To.BlockNumber);
        _repository.RemoveRetainedStatesBefore(candidate.To.BlockNumber);
    }

    internal bool DropStateNotReachableFrom(in StateId head)
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
