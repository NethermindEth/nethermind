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
/// Prefetched values are parent-state reads of slots the block never writes, so caching them
/// is correct for the whole block; warming is best-effort throughout — readers swallow
/// failures and the pattern detector simply disengages on mismatch.
/// </remarks>
internal sealed class StorageStridePrefetcher(
    IWorldStateScopeProvider.IStorageTree tree,
    SeqlockCache<StorageCell, byte[]> cache,
    Address address,
    CancellationToken token) : IDisposable
{
    /// <summary>On-pattern reads required before readers start.</summary>
    private const int EngageRunLength = 4;

    /// <summary>Consecutive off-pattern reads before the pattern is declared broken. Tolerates
    /// interleaved unrelated reads (counters, config slots) within a striding scan.</summary>
    private const int BreakRunLength = 16;

    /// <summary>Maximum slots issued beyond the consumer position; bounds wasted reads when the
    /// pattern ends and bounds cache pressure.</summary>
    private const int MaxLookahead = 4096;

    /// <summary>Lookahead-gate polls (1 ms apart) tolerated before readers conclude the consumer
    /// has left the pattern.</summary>
    private const int IdlePollLimit = 250;

    private static readonly int ReaderConcurrency = Math.Min(2 * Environment.ProcessorCount, 32);

    private readonly IWorldStateScopeProvider.IStorageTree _tree = tree;
    private readonly SeqlockCache<StorageCell, byte[]> _cache = cache;
    private readonly Address _address = address;
    private readonly CancellationToken _token = token;

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

        UInt256 delta = index - _lastIndex;
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
        _runLength = _runLength == 0 || delta.IsZero ? 1 : 2;
        _missRunLength = 0;
    }

    private void Engage(in UInt256 index)
    {
        if (_token.IsCancellationRequested)
        {
            _broken = true;
            return;
        }

        _engaged = true;
        _engageIndex = index;
        _readers = new Task[ReaderConcurrency];
        for (int t = 0; t < ReaderConcurrency; t++)
        {
            _readers[t] = Task.Factory.StartNew(ReadAhead, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private void ReadAhead()
    {
        int idlePolls = 0;
        while (!_token.IsCancellationRequested && !_broken)
        {
            long k = Interlocked.Increment(ref _issued);
            while (k - Volatile.Read(ref _consumed) > MaxLookahead)
            {
                if (_token.IsCancellationRequested || _broken) return;
                if (++idlePolls > IdlePollLimit) return;
                Thread.Sleep(1);
            }

            idlePolls = 0;
            UInt256 offset = (UInt256)(ulong)(k + 1) * _stride;
            UInt256 index = _engageIndex + offset;

            try
            {
                byte[] value = _tree.Get(in index);
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

    /// <summary>Blocks until all readers exited; the caller cancels the token first.</summary>
    public void Dispose()
    {
        _broken = true;
        if (_readers is not null)
        {
            Task.WaitAll(_readers);
        }
    }
}
