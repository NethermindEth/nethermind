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
/// Sequential EVM execution issues cold storage reads one at a time, so a contract scanning slots at
/// a fixed stride can become bound by single-read latency. Slot keys hash to random database
/// positions, so once a stride is detected the prefetcher overlaps that latency by reading ahead.
/// <para>
/// The <paramref name="treeFactory"/> must create a private scope anchored at the executing block's
/// parent. Readers must never share the live scope used by block execution, because the live scope
/// starts serving in-block values after writes land while the pre-block cache may only contain
/// parent-state values.
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

    /// <summary>
    /// Minimum slot index for engagement. Low storage slots are commonly loop cursors,
    /// configuration, or compact hand-written layouts where read-ahead competes with execution
    /// more often than it helps.
    /// </summary>
    private static readonly UInt256 MinEngageIndex = uint.MaxValue;

    /// <summary>
    /// Consecutive off-pattern reads before the pattern is declared broken. This tolerates
    /// interleaved unrelated reads such as counters or configuration slots within a striding scan.
    /// </summary>
    private const int BreakRunLength = 16;

    /// <summary>
    /// Maximum slots issued beyond the consumer position; bounds wasted reads when the pattern ends.
    /// </summary>
    private const int MaxLookahead = 256;

    /// <summary>
    /// Lookahead-gate polls, 1 ms apart, tolerated before readers conclude the consumer left the pattern.
    /// </summary>
    private const int IdlePollLimit = 250;

    private readonly Func<IWorldStateScopeProvider.IStorageTree> _treeFactory = treeFactory;
    private readonly SeqlockCache<StorageCell, byte[]> _cache = cache;
    private readonly Address _address = address;
    private readonly CancellationToken _token = token;
    private readonly int _readerConcurrency = Math.Max(1, readerConcurrency);

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

            if (++_runLength >= EngageRunLength && index > MinEngageIndex)
            {
                Engage(index);
            }

            return;
        }

        if (_engaged)
        {
            if (++_missRunLength >= BreakRunLength)
            {
                _broken = true;
            }

            return;
        }

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
                if (_broken || _token.IsCancellationRequested) return;
                StorageCell cell = new(_address, in index);
                _cache.Set(in cell, value);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    /// <summary>True once a sustained off-pattern run has disengaged this prefetcher.</summary>
    /// <remarks>
    /// Broken prefetchers no longer issue reads, so they do not count against the active reader
    /// budget. They are still retained until block-end so the owner can join their tasks before
    /// disposing the shared private scope.
    /// </remarks>
    internal bool IsBroken => _broken;

    /// <summary>Signals the readers to stop and hands their tasks back for the caller to join.</summary>
    /// <remarks>
    /// Joining is deliberately left to the caller. A reader can be inside an uncancellable storage
    /// read, so block-end cancellation should not synchronously wait on that I/O tail.
    /// </remarks>
    internal Task[] StopAndGetReaders()
    {
        _broken = true;
        return _readers ?? [];
    }

    /// <summary>Signals readers to stop and blocks until they exit.</summary>
    public void Dispose() => Task.WaitAll(StopAndGetReaders());
}
