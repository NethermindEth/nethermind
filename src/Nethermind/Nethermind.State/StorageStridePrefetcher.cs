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
/// <para>
/// All of this prefetcher's readers drive the single tree the factory returns, so the backend's read
/// path must be safe to enter from several threads at once. That is what
/// <see cref="IWorldStateScopeProvider.SupportsConcurrentScopes"/> gates engagement on: the flat
/// snapshot read path qualifies, its storage-trie cross-check does not.
/// </para>
/// </remarks>
internal sealed class StorageStridePrefetcher(
    Func<IWorldStateScopeProvider.IStorageTree> treeFactory,
    SeqlockCache<StorageCell, byte[]> cache,
    Address address,
    CancellationToken token,
    int readerConcurrency,
    Func<bool> tryReserveEngagement) : IDisposable
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
    private readonly Func<bool> _tryReserveEngagement = tryReserveEngagement;

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
    private int _publishing;
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
        // The owner's engagement budget is the only bound on how much work one block can trigger here:
        // every engagement creates reader threads and issues up to MaxLookahead speculative reads.
        if (_token.IsCancellationRequested || !_tryReserveEngagement())
        {
            _broken = true;
            return;
        }

        _engaged = true;
        _engageIndex = index;
        // Engagement runs on the block-processing thread inside an EVM storage read, so nothing that
        // can block belongs here: opening the readers' isolated scope (the flat backend retries a
        // snapshot-bundle gather to a deadline) happens on the starter thread instead. That thread
        // owns the readers, so joining it joins them all.
        _readers = [Task.Factory.StartNew(StartReaders, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)];
    }

    private void StartReaders()
    {
        // Teardown may already have run: opening the readers' scope now would only buy a scope for the
        // teardown continuation to dispose.
        if (_broken || _token.IsCancellationRequested) return;

        try
        {
            _tree = _treeFactory();
        }
        catch (Exception)
        {
            // Best-effort warming: a tree-creation failure (a scope racing block-end teardown, a
            // backend that cannot open one) only means no prefetch for this contract.
            _broken = true;
            return;
        }

        if (_broken || _token.IsCancellationRequested) return;

        // This thread is one of the readers; the others run alongside it, and waiting on them here is
        // what lets this single task stand in for the whole reader set.
        Task[] readers = new Task[_readerConcurrency - 1];
        for (int t = 0; t < readers.Length; t++)
        {
            readers[t] = Task.Factory.StartNew(ReadAhead, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        ReadAhead();
        Task.WaitAll(readers);
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
                // The cancelled token marks the end of the block these parent-state values are valid
                // for; re-check under the publish latch so a straggler cannot repopulate a cache that
                // is being handed to the next block.
                if (!TryBeginPublish()) return;
                try
                {
                    StorageCell cell = new(_address, in index);
                    _cache.Set(in cell, value);
                }
                finally
                {
                    EndPublish();
                }
            }
            catch (Exception)
            {
                // Best-effort warming: a failed read (e.g. racing scope teardown) only means
                // fewer cache hits. Stop this reader rather than spin on a failing tree.
                return;
            }
        }
    }

    /// <summary>Enters the publish section, returning <c>false</c> when publishing is already sealed.</summary>
    /// <remarks>
    /// Teardown seals publishing and then waits for this section to drain, so a reader that gets past
    /// the check here always completes its cache write before the owner hands the cache to the next
    /// block — closing the window between the check and the write. Internal only so the tests can
    /// assert that seal; production enters it from <see cref="ReadAhead"/>.
    /// </remarks>
    internal bool TryBeginPublish()
    {
        Interlocked.Increment(ref _publishing);
        if (!_broken && !_token.IsCancellationRequested) return true;

        EndPublish();
        return false;
    }

    /// <inheritdoc cref="TryBeginPublish"/>
    internal void EndPublish() => Interlocked.Decrement(ref _publishing);

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
        // Full fence between the seal and the drain: without it a reader's latch increment and this
        // thread's read of it can each miss the other (store-buffered), letting a reader publish
        // parent state into the next block's cache after teardown returned. The wait only covers
        // threads inside the publish section — a cache write, never an in-flight _tree.Get.
        Interlocked.MemoryBarrier();
        SpinWait spin = new();
        while (Volatile.Read(ref _publishing) != 0) spin.SpinOnce();

        return _readers ?? [];
    }

    /// <summary>Signals readers to stop and blocks until they exit.</summary>
    /// <remarks>
    /// Test-only. Production tears down through <see cref="StopAndGetReaders"/> and joins the returned
    /// tasks in the background, because a synchronous join at block-end would stall the hot path on an
    /// in-flight storage read; nothing leaks by not disposing, as this type owns no unmanaged state.
    /// </remarks>
    public void Dispose() => Task.WaitAll(StopAndGetReaders());
}
