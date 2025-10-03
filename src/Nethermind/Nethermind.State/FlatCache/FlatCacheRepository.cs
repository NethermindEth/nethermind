// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    private Lock _repoLock = new Lock();
    private readonly ICanonicalStateRootFinder _stateRootFinder;
    private Dictionary<StateId, Snapshot> _compactedKnownStates = new();
    private Dictionary<StateId, Snapshot> _knownStates = new();
    private SortedSet<StateId> _sortedKnownStates = new();
    private IBigCache _bigCache;

    // private ConcurrentBag<(StateId, StateId)> _usedRange = new ConcurrentBag<(StateId, StateId)>();

    internal int KnownStatesCount => _knownStates.Count;
    private readonly int _maxStateInMemory;

    private Channel<StateId> _compactorJobs;
    private long _compactSize;
    private readonly bool _inlineCompaction;
    private ILogger _logger;

    public record Configuration(
        int MaxStateInMemory = 1024 * 8,
        int MaxInFlightCompactJob = 32,
        int CompactSize = 64,
        bool InlineCompaction = false
    );

    public FlatCacheRepository(IProcessExitSource exitSource, PersistedBigCache bigCache, ICanonicalStateRootFinder stateRootFinder, ILogManager logManager, Configuration? config = null)
    {
        if (config is null) config = new Configuration();
        _maxStateInMemory = config.MaxStateInMemory;
        // _bigCache = bigCache;
        _bigCache = new BigCache();
        _compactSize = config.CompactSize;
        _inlineCompaction = config.InlineCompaction;
        _stateRootFinder = stateRootFinder;
        _logger = logManager.GetClassLogger<FlatCacheRepository>();

        _compactorJobs = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);

        _ = RunCompactor(exitSource.Token);
    }

    private async Task RunCompactor(CancellationToken cancellationToken)
    {
        await foreach (var stateId in _compactorJobs.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await NotifyWhenSlow($"compact {stateId}", () => CompactLevel(stateId));
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
        Task jobTask = Task.Run(closure);
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
        CompactLevel(stateId);
        CleanIfNeeded().Wait();
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
                _compactedKnownStates[stateId] = snapshot;
            }

            gatheredCache.Dispose();
        }
        catch (Exception e)
        {
            _logger.Error($"Compactor failed {e}");
        }
    }

    public SnapshotBundle GatherCache(StateId baseBlock, long? earliestBlockNumber = null)
    {
        using var _ = _repoLock.EnterScope();

        ArrayPoolList<Snapshot> knownStates = new(_knownStates.Count / 32);

        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}. Earliest is {earliestBlockNumber}");

        StateId current = baseBlock;

        int tryCount = 0;

        while(_compactedKnownStates.TryGetValue(current, out var entry) || _knownStates.TryGetValue(current, out entry))
        {
            Snapshot state = entry;
            if (_logger.IsTrace) _logger.Trace($"Got {state.From} -> {state.To}");
            knownStates.Add(state);
            if (current.blockNumber == earliestBlockNumber) break;
            if (current.blockNumber <= _bigCache.CurrentBlockNumber) break;
            if (state.From == current) break; // Some test commit two block with the same id, so we dont know the parent anymore.
            current = state.From;
            tryCount++;
            if (tryCount > 100000) throw new Exception("Many try 1");
        }

        knownStates.Reverse();

        if (_logger.IsTrace) _logger.Trace($"Gathered {baseBlock}. Earliest is {earliestBlockNumber}, Got {knownStates.Count} known states");
        return new SnapshotBundle(knownStates, _bigCache);
    }

    public void RegisterKnownState(StateId startingBlock, StateId endBlock, Snapshot snapshot)
    {
        using (_repoLock.EnterScope())
        {
            if (snapshot.Storages is null) throw new Exception("No null storages pplease");


            if (_logger.IsTrace) _logger.Trace($"Registering {startingBlock.blockNumber} to {endBlock.blockNumber}");
            if (endBlock.blockNumber <= _bigCache.CurrentBlockNumber)
                throw new InvalidOperationException(
                    $"Cannot register snapshot earlier than bigcache. Snapshot number {endBlock.blockNumber}, bigcache number: {_bigCache.CurrentBlockNumber}");

            _knownStates[endBlock] = snapshot;
            _sortedKnownStates.Add(endBlock);
        }

        if (_inlineCompaction)
        {
            RunCompactJob(endBlock);
        }
        else
        {
            if (!_compactorJobs.Writer.TryWrite(endBlock))
            {
                _compactorJobs.Writer.WriteAsync(endBlock).AsTask().Wait();
            }
        }
    }

    private int _boundary = 128;
    private async Task CleanIfNeeded()
    {
        await NotifyWhenSlow("add to bigcache", () => AddToBigCache());
        await NotifyWhenSlow("subtract from bigcache", () => SubtractFromBigCache());
    }

    private void SubtractFromBigCache()
    {
        int tryCount = 0;
        while(_knownStates.Count > _maxStateInMemory)
        {
            tryCount++;
            if (tryCount > 100000) throw new Exception("Many try 3");

            StateId firstKey;
            Snapshot snapshot;

            using (_repoLock.EnterScope())
            {
                firstKey = _sortedKnownStates.First();
                snapshot = _knownStates[firstKey];
            }

            _bigCache.Subtract(firstKey, snapshot);

            using (_repoLock.EnterScope())
            {
                _knownStates.Remove(firstKey);
                _sortedKnownStates.Remove(firstKey);
                _compactedKnownStates.Remove(firstKey);
            }
        }
    }

    private void AddToBigCache()
    {
        // Attempt to add snapshots into bigcache
        int tryCount = 0;
        while (true)
        {
            tryCount++;
            if (tryCount > 100000) throw new Exception("Many try 2");

            Snapshot pickedState;
            StateId? pickedSnapshot = null;
            using (_repoLock.EnterScope())
            {
                if (_knownStates.Count - _bigCache.SnapshotCount <= _boundary)
                {
                    break;
                }
                var toAddCandidate = _sortedKnownStates.GetViewBetween(new StateId(_bigCache.CurrentBlockNumber + 1, ValueKeccak.Zero), new StateId(long.MaxValue, ValueKeccak.Zero));

                List<StateId> candidateToAdd = new List<StateId>();
                long? blockNumber = null;
                foreach (var stateId in toAddCandidate)
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

                // TODO: Need to make sure no processing is using other branch at this point
                pickedState = _knownStates[pickedSnapshot.Value];
            }

            _bigCache.Add(pickedSnapshot.Value, pickedState);

            using (_repoLock.EnterScope())
            {
                _compactedKnownStates.Remove(pickedSnapshot.Value);
            }
        }
    }
}

public interface ICanonicalStateRootFinder
{
    public Hash256? GetCanonicalStateRootAtBlock(long blockNumber);
}
