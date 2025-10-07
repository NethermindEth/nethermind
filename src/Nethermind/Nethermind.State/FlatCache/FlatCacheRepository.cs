// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.State.FlatCache;

public sealed class FlatCacheRepository
{
    private Lock _repoLock = new Lock(); // Note: lock is for proteccting in memory and compacted states only
    private readonly ICanonicalStateRootFinder _stateRootFinder;
    private Dictionary<StateId, Snapshot> _compactedKnownStates = new();
    private InMemorySnapshotStore _inMemorySnapshotStore;
    private SnapshotsStore _snapshotsStore;
    private IBigCache _bigCache;
    private int _boundary;

    private Channel<StateId> _compactorJobs;
    private long _compactSize;
    private readonly bool _inlineCompaction;
    private ILogger _logger;

    public record Configuration(
        int MaxStateInMemory = 512,
        int MaxInFlightCompactJob = 32,
        int CompactSize = 64,
        int Boundary = 128,
        bool InlineCompaction = true
    )
    {
    }

    public FlatCacheRepository(
        IProcessExitSource exitSource,
        SnapshotsStore snapshotsStore,
        ICanonicalStateRootFinder stateRootFinder,
        PersistedBigCache persistedBigCache,
        ILogManager logManager,
        Configuration? config = null)
    {
        if (config is null) config = new Configuration();
        _inMemorySnapshotStore = new InMemorySnapshotStore();
        _snapshotsStore = snapshotsStore;
        _bigCache = persistedBigCache;
        _compactSize = config.CompactSize;
        _inlineCompaction = config.InlineCompaction;
        _stateRootFinder = stateRootFinder;
        _logger = logManager.GetClassLogger<FlatCacheRepository>();

        _compactorJobs = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);
        _boundary = config.Boundary;
        WarmUp();

        _ = RunCompactor(exitSource.Token);
    }

    private void WarmUp()
    {
        _logger.Info($"Warming up here");

        StateId? lastState;
        using (_repoLock.EnterScope())
        {
            lastState = _snapshotsStore.GetLast();
        }

        if (lastState == null)
        {
            _logger.Info("No persisted snapshot");
            return;
        }

        foreach (var stateId in _snapshotsStore.GetKeysBetween(_bigCache.CurrentState.blockNumber, long.MaxValue))
        {
            _snapshotsStore.TryGetValue(stateId, out var snapshot);
            _inMemorySnapshotStore.AddBlock(stateId, snapshot);
            CompactLevel(stateId);
        }
    }

    private async Task RunCompactor(CancellationToken cancellationToken)
    {
        await foreach (var stateId in _compactorJobs.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                PersistLevel(stateId);
                CompactLevel(stateId);
                await CleanIfNeeded();
            }
            catch (Exception ex)
            {
                _logger.Error("Compact job failed", ex);
                throw;
            }
        }
    }

    private async Task NotifyWhenSlow(string name, Action closure)
    {
        Task jobTask = Task.Run(() =>
        {
            try
            {
                closure();
            }
            catch (Exception ex)
            {
                _logger.Error($"job {name} failed", ex);
                throw;
            }
        });
        Task waiterTask = Task.Run(async () =>
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                await Task.Delay(1000);
                if (jobTask.IsCompleted) break;
                _logger.Info($"Task {name} took {sw.Elapsed}");
            }
        });

        await Task.WhenAny(jobTask, waiterTask);
    }

    private void RunCompactJob(StateId stateId)
    {
        PersistLevel(stateId);
        CompactLevel(stateId);
        CleanIfNeeded().Wait();
    }

    private void PersistLevel(StateId stateId)
    {
        try
        {
            Snapshot snapshot;
            using (_repoLock.EnterScope())
            {
                _inMemorySnapshotStore.TryGetValue(stateId, out snapshot);
            }

            _snapshotsStore.AddBlock(stateId, snapshot);
        }
        catch (Exception ex)
        {
            _logger.Info($"Persisting {stateId} ex {ex}");
            throw;
        }
    }

    private void CompactLevel(StateId stateId)
    {
        try
        {
            if (_compactSize <= 1) return; // Disabled
            long blockNumber = stateId.blockNumber;
            if (blockNumber == 0) return;
            long startingBlockNumber = ((blockNumber - 1) / _compactSize) * _compactSize;

            SnapshotBundle gatheredCache = GatherCache(stateId, startingBlockNumber);
            if (gatheredCache.SnapshotCount == 1) return;

            if (_logger.IsDebug) _logger.Debug($"Compacting {stateId}");
            Snapshot snapshot = gatheredCache.CompactToKnownState();

            using (_repoLock.EnterScope())
            {
                if (_logger.IsDebug) _logger.Debug($"Compacted {gatheredCache.SnapshotCount} to {stateId}");
                _compactedKnownStates[stateId] = snapshot;
            }

            gatheredCache.Dispose();
        }
        catch (Exception e)
        {
            _logger.Error($"Compactor failed {e}");
        }
    }

    public SnapshotBundle GatherCache(StateId baseBlock, long? earliestExclusive = null)
    {
        using var _ = _repoLock.EnterScope();

        ArrayPoolList<Snapshot> knownStates = new(_inMemorySnapshotStore.KnownStatesCount / 32);

        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}. Earliest is {earliestExclusive}");

        StateId current = baseBlock;
        while(_compactedKnownStates.TryGetValue(current, out var entry) || _inMemorySnapshotStore.TryGetValue(current, out entry))
        {
            Snapshot state = entry;
            if (_logger.IsTrace) _logger.Trace($"Got {state.From} -> {state.To}");
            knownStates.Add(state);
            if (state.From.blockNumber <= _bigCache.CurrentState.blockNumber) break; // Or equal?
            if (state.From.blockNumber <= earliestExclusive) break;
            if (state.From == current) break; // Some test commit two block with the same id, so we dont know the parent anymore.
            current = state.From;
        }

        knownStates.Reverse();

        if (_logger.IsTrace) _logger.Trace($"Gathered {baseBlock}. Earliest is {earliestExclusive}, Got {knownStates.Count} known states, {_bigCache.CurrentState}");
        return new SnapshotBundle(knownStates, _bigCache);
    }

    public void RegisterKnownState(StateId startingBlock, StateId endBlock, Snapshot snapshot)
    {
        using (_repoLock.EnterScope())
        {
            if (_logger.IsTrace) _logger.Trace($"Registering {startingBlock.blockNumber} to {endBlock.blockNumber}");
            if (endBlock.blockNumber <= _bigCache.CurrentState.blockNumber)
                throw new InvalidOperationException(
                    $"Cannot register snapshot earlier than bigcache. Snapshot number {endBlock.blockNumber}, bigcache number: {_bigCache.CurrentState}");

            _inMemorySnapshotStore.AddBlock(endBlock, snapshot);
        }

        if (_inlineCompaction)
        {
            RunCompactJob(endBlock);
        }
        else
        {
            if (!_compactorJobs.Writer.TryWrite(endBlock))
            {
                _logger.Warn("Compactor job stall!");
                _compactorJobs.Writer.WriteAsync(endBlock).AsTask().Wait();
            }
        }
    }

    private async Task CleanIfNeeded()
    {
        await NotifyWhenSlow("add to bigcache", () => AddToBigCache());
    }

    private void AddToBigCache()
    {
        // Attempt to add snapshots into bigcache
        while (true)
        {
            Snapshot pickedState;
            StateId? pickedSnapshot = null;
            using (_repoLock.EnterScope())
            {
                long lastSnapshotNumber = _snapshotsStore.GetLast()?.blockNumber ?? 0;
                StateId currentState = _bigCache.CurrentState;
                if (lastSnapshotNumber - currentState.blockNumber <= _boundary)
                {
                    break;
                }

                List<StateId> candidateToAdd = new List<StateId>();
                long? blockNumber = null;
                foreach (var stateId in _inMemorySnapshotStore.GetStatesAfterBlock(currentState.blockNumber))
                {
                    if (blockNumber is null)
                    {
                        blockNumber = stateId.blockNumber;
                        candidateToAdd.Add(stateId);
                    }
                    else if (blockNumber == stateId.blockNumber)
                    {
                        candidateToAdd.Add(stateId);
                    }
                    else
                    {
                        break;
                    }
                }

                Debug.Assert(candidateToAdd.Count > 0);

                if (candidateToAdd.Count > 1)
                {
                    Hash256? canonicalStateRoot = _stateRootFinder.GetCanonicalStateRootAtBlock(blockNumber.Value);
                    if (canonicalStateRoot is null)
                    {
                        _logger.Warn($"Canonical state root for block {blockNumber} not known");
                        return;
                    }

                    foreach (var stateId in candidateToAdd)
                    {
                        if (stateId.stateRoot == canonicalStateRoot)
                        {
                            pickedSnapshot = stateId;
                        }
                    }
                }
                else
                {
                    pickedSnapshot = candidateToAdd[0];
                }

                if (!pickedSnapshot.HasValue)
                {
                    _logger.Warn($"Unable to determine canonicaal snapshot");
                    return;
                }

                // Remove non-canon snapshots
                using (_repoLock.EnterScope())
                {
                    foreach (var stateId in candidateToAdd)
                    {
                        if (stateId != pickedSnapshot)
                        {
                            _snapshotsStore.Remove(stateId);
                            _compactedKnownStates.Remove(stateId);
                            _inMemorySnapshotStore.Remove(stateId);
                        }
                    }
                }

                _inMemorySnapshotStore.TryGetValue(pickedSnapshot.Value, out pickedState);
            }

            // Add the canon snapshot
            _bigCache.Add(pickedSnapshot.Value, pickedState);

            // And we remove it
            using (_repoLock.EnterScope())
            {
                _compactedKnownStates.Remove(pickedSnapshot.Value);
                _inMemorySnapshotStore.Remove(pickedSnapshot.Value);
                _snapshotsStore.Remove(pickedSnapshot.Value);
            }
        }
    }
}

public interface ICanonicalStateRootFinder
{
    public Hash256? GetCanonicalStateRootAtBlock(long blockNumber);
}
