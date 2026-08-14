// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State;

/// <summary>
/// Detects constant-stride storage read patterns on a contract and prefetches ahead of the
/// consumer into the pre-block cache on dedicated reader threads.
/// </summary>
/// <remarks>
/// Sequential EVM execution issues cold storage reads one at a time, so a contract scanning
/// slots at a fixed stride (arrays, token bloat scans) is bound by single-read latency. Slot
/// keys hash to random database positions, so the only way to overlap that latency without
/// knowing the access list in advance is to recognize the index pattern and read ahead.
/// <para>
/// Isolation invariant: <paramref name="treeFactory"/> must produce a tree over a scope that is
/// private to the prefetcher and anchored at the executing block's parent, so readers observe
/// parent state only and never share mutable scope state with the executing thread (the live
/// scope's tree is unsafe here because it serves in-block values once the block's write batch lands,
/// and its backing structures gate their own background readers around writes). Parent-state
/// values are correct to cache for the whole block because the cache sits below the in-block
/// write layers. Warming is best-effort throughout: readers swallow failures and the pattern
/// detector simply disengages on mismatch.
/// </para>
/// </remarks>
internal sealed class StorageStridePrefetcher(
    Func<IWorldStateScopeProvider.IStorageTree> treeFactory,
    SeqlockCache<StorageCell, byte[]> cache,
    Address address,
    CancellationToken token,
    int readerConcurrency) : IDisposable
{
    /// <summary>On-pattern reads required before readers start.</summary>
    private const int EngageRunLength = 8;

    /// <summary>Consecutive off-pattern reads before the pattern is declared broken. Tolerates
    /// interleaved unrelated reads (counters, config slots) within a striding scan.</summary>
    private const int BreakRunLength = 16;

    /// <summary>Maximum slots issued beyond the consumer position; bounds wasted reads when the
    /// pattern ends and bounds cache pressure.</summary>
    private const int MaxLookahead = 4096;

    /// <summary>Lookahead-gate polls (1 ms apart) tolerated before readers conclude the consumer
    /// has left the pattern.</summary>
    private const int IdlePollLimit = 250;

    private readonly Func<IWorldStateScopeProvider.IStorageTree> _treeFactory = treeFactory;
    private readonly SeqlockCache<StorageCell, byte[]> _cache = cache;
    private readonly Address _address = address;
    private readonly CancellationToken _token = token;
    private readonly int _readerConcurrency = readerConcurrency;

    private IWorldStateScopeProvider.IStorageTree? _tree;

    private UInt256 _lastIndex;
    private UInt256 _stride;
    private int _runLength;
    private int _missRunLength;

    private UInt256 _engageIndex;
    private long _issued = -1;
    private long _consumed;
    private volatile bool _engaged;
    private volatile bool _broken;
    private Task[]? _readers;

    /// <summary>Feeds a consumer read into the detector; engages or advances the readers.</summary>
    public void OnRead(in UInt256 index)
    {
        if (_broken) return;

        bool hasForwardDelta = index > _lastIndex;
        UInt256 delta = hasForwardDelta ? index - _lastIndex : UInt256.Zero;
        bool onPattern = _runLength > 0 && delta == _stride && !delta.IsZero;

        if (onPattern)
        {
            _missRunLength = 0;
            _lastIndex = index;
            if (_engaged)
            {
                Interlocked.Increment(ref _consumed);
                return;
            }

            if (++_runLength >= EngageRunLength)
            {
                Engage(index);
            }

            return;
        }

        if (_engaged)
        {
            // Off-pattern reads are expected mid-scan (e.g. a loop counter slot); only a sustained
            // run of them means the scan is over.
            if (++_missRunLength >= BreakRunLength)
            {
                _broken = true;
            }

            return;
        }

        // Not engaged: restart the detector from this read.
        _stride = delta;
        _lastIndex = index;
        _runLength = _runLength == 0 || !hasForwardDelta ? 1 : 2;
        _missRunLength = 0;
    }

    private void Engage(in UInt256 index)
    {
        if (_token.IsCancellationRequested)
        {
            _broken = true;
            return;
        }

        try
        {
            _tree = _treeFactory();
        }
        catch (Exception)
        {
            // Engagement runs inside an EVM storage read; a tree-creation failure must degrade
            // to "no prefetch", never fault block processing.
            _broken = true;
            return;
        }

        _engaged = true;
        _engageIndex = index;
        _readers = new Task[_readerConcurrency];
        for (int t = 0; t < _readerConcurrency; t++)
        {
            _readers[t] = Task.Factory.StartNew(ReadAhead, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private void ReadAhead()
    {
        // _broken is checked before the token so that once teardown has signalled this reader (the
        // owner sets _broken synchronously at block-end), it exits without touching the token — the
        // owning scope disposes the token source once the block is done, while this reader may still
        // be draining in the background.
        int idlePolls = 0;
        while (!_broken && !_token.IsCancellationRequested)
        {
            long k = Interlocked.Increment(ref _issued);
            while (k - Volatile.Read(ref _consumed) > MaxLookahead)
            {
                if (_broken || _token.IsCancellationRequested) return;
                if (++idlePolls > IdlePollLimit) return;
                Thread.Sleep(1);
            }

            idlePolls = 0;
            try
            {
                UInt256 offset = (UInt256)(ulong)(k + 1) * _stride;
                UInt256 index = _engageIndex + offset;
                byte[] value = _tree!.Get(in index);
                // The cancelled token marks the end of the block these parent-state values are
                // valid for; re-check after the read so a straggler cannot repopulate a cache
                // that is being handed to the next block.
                if (_broken || _token.IsCancellationRequested) return;
                StorageCell cell = new(_address, in index);
                _cache.Set(in cell, value);
            }
            catch (Exception)
            {
                // Best-effort warming: a failed read (e.g. racing scope teardown) only means
                // fewer cache hits. Stop this reader rather than spin on a failing tree.
                return;
            }
        }
    }

    /// <summary>True once a sustained off-pattern run has disengaged this prefetcher.</summary>
    /// <remarks>
    /// A broken prefetcher's readers have stopped issuing reads, so its scope slot can be treated as
    /// free when deciding whether a new contract may engage. It is still kept in the owner's map so
    /// its (already exited) readers are joined before their shared scope is disposed.
    /// </remarks>
    internal bool IsBroken => _broken;

    /// <summary>Signals the readers to stop and hands their tasks back for the caller to join.</summary>
    /// <remarks>
    /// Joining must happen off the block-processing thread: a reader mid-<c>_tree.Get</c> is inside an
    /// uncancellable storage read, so a synchronous join at block-end (write batch / commit) would
    /// stall the hot path on that read's tail latency. The caller waits on the returned tasks in the
    /// background and only then disposes the readers' shared scope.
    /// </remarks>
    internal Task[] StopAndGetReaders()
    {
        _broken = true;
        return _readers ?? [];
    }

    /// <summary>Signals readers to stop and blocks until they exit.</summary>
    public void Dispose() => Task.WaitAll(StopAndGetReaders());
}
