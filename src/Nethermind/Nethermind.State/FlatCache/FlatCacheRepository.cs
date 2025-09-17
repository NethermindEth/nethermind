// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
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
    private Dictionary<StateId, Snapshot> _compactedKnownStates = new();
    private Dictionary<StateId, Snapshot> _knownStates = new();
    private SortedSet<StateId> _sortedKnownStates = new();
    private BigCache _bigCache = new BigCache();

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
        bool InlineCompaction = true
    );

    public FlatCacheRepository(IProcessExitSource exitSource, ILogManager logManager, Configuration? config = null)
    {
        if (config is null) config = new Configuration();
        _maxStateInMemory = config.MaxStateInMemory;
        _compactSize = config.CompactSize;
        _inlineCompaction = config.InlineCompaction;
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
                RunCompactJob(stateId);
            }
            catch (Exception ex)
            {
                _logger.Error("Compact job failed", ex);
                throw;
            }
        }
    }

    private void RunCompactJob(StateId stateId)
    {
        CompactLevel(stateId);
        CleanIfNeeded();
    }

    private void CompactLevel(StateId stateId)
    {
        try
        {
            if (_compactSize <= 1) return; // Disabled
            long blockNumber = stateId.blockNumber;
            if (blockNumber == 0) return;
            long startingBlockNumber = ((blockNumber - 1) / _compactSize) * _compactSize;

            (ArrayPoolList<StateId> statesAdded, SnapshotBundle gatheredCache) = GatherCache(stateId, startingBlockNumber);
            if (statesAdded.Count == 1) return;

            if (_logger.IsDebug) _logger.Debug($"Compacting {stateId}");
            Snapshot snapshot = gatheredCache.CompactToKnownState();

            using var _ = _repoLock.EnterScope();
            _compactedKnownStates[stateId] = snapshot;

            gatheredCache.Dispose();
            statesAdded.Clear();
        }
        catch (Exception e)
        {
            _logger.Error($"Compactor failed {e}");
        }
    }

    public (ArrayPoolList<StateId>, SnapshotBundle) GatherCache(StateId baseBlock, long? earliestBlockNumber = null)
    {
        using var _ = _repoLock.EnterScope();

        ArrayPoolList<Snapshot> knownStates = new(_knownStates.Count / 32);
        ArrayPoolList<StateId> statesAdded = new(_knownStates.Count / 32);

        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}. Earliest is {earliestBlockNumber}");

        StateId current = baseBlock;

        int tryCount = 0;

        while(_compactedKnownStates.TryGetValue(current, out var entry) || _knownStates.TryGetValue(current, out entry))
        {
            Snapshot state = entry;
            if (_logger.IsTrace) _logger.Trace($"Got {state.From} -> {state.To}");
            knownStates.Add(state);
            statesAdded.Add(current);
            if (current.blockNumber == earliestBlockNumber) break;
            if (current.blockNumber <= _bigCache.CurrentBlockNumber) break;
            if (state.From == current) break; // Some test commit two block with the same id, so we dont know the parent anymore.
            current = state.From;
            tryCount++;
            if (tryCount > 100000) throw new Exception("Many try 1");
        }

        knownStates.Reverse();
        statesAdded.Reverse();

        if (_logger.IsTrace) _logger.Trace($"Gathered {baseBlock}. Earliest is {earliestBlockNumber}, Got {knownStates.Count} known states");
        return (statesAdded, new SnapshotBundle(knownStates, _bigCache));
    }

    public void RegisterKnownState(StateId startingBlock, StateId endBlock, Snapshot snapshot)
    {
        using var _ = _repoLock.EnterScope();
        if (snapshot.Storages is null) throw new Exception("No null storages pplease");


        if (_logger.IsTrace) _logger.Trace($"Registering {startingBlock.blockNumber} to {endBlock.blockNumber}");
        if (endBlock.blockNumber <= _bigCache.CurrentBlockNumber)
            throw new InvalidOperationException(
                $"Cannot register snapshot earlier than bigcache. Snapshot number {endBlock.blockNumber}, bigcache number: {_bigCache.CurrentBlockNumber}");

        if (_sortedKnownStates.GetViewBetween(new StateId(endBlock.blockNumber, Keccak.Zero),
                new StateId(long.MaxValue, Keccak.Zero)).Count > 0)
        {
            _logger.Warn($"Ignoring {endBlock} as non consecutive. Not implemented yet.");
            return;
        }

        _knownStates[endBlock] = snapshot;
        _sortedKnownStates.Add(endBlock);

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
    private void CleanIfNeeded()
    {
        using var _ = _repoLock.EnterScope();

        // Attempt to add snapshots into bigcache
        int tryCount = 0;
        while (_knownStates.Count - _bigCache.SnapshotCount > _boundary)
        {
            tryCount++;
            if (tryCount > 100000) throw new Exception("Many try 2");
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

            StateId pickedSnapshot = candidateToAdd[0];
            if (candidateToAdd.Count > 1)
            {
                // TODO: Fix
                // TODO: Remove reorg from known states
                throw new NotImplementedException("Reorg handling incomplete, sorry.");
            }

            // TODO: Need to make sure no processing is using other branch at this point
            var pickedState = _knownStates[pickedSnapshot];

            _repoLock.Exit();
            try
            {
                _bigCache.Add(pickedSnapshot, pickedState);
            }
            finally
            {
                _repoLock.EnterScope();
            }

            _compactedKnownStates.Remove(pickedSnapshot);
        }

        tryCount = 0;
        while(_knownStates.Count > _maxStateInMemory)
        {
            tryCount++;
            if (tryCount > 100000) throw new Exception("Many try 3");
            var firstKey = _sortedKnownStates.First();
            Snapshot snapshot = _knownStates[firstKey];

            _repoLock.Exit();
            try
            {
                _bigCache.Subtract(firstKey, snapshot);
            }
            finally
            {
                _repoLock.EnterScope();
            }

            _knownStates.Remove(firstKey);
            _sortedKnownStates.Remove(firstKey);
            _compactedKnownStates.Remove(firstKey);
        }
    }
}
