// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth;

namespace Nethermind.Network.Benchmarks;

[MemoryDiagnoser]
public class PooledTransactionRequestsBenchmarks
{
    private readonly ClockCache<long, ValueHash256[]> _baseline = new(2048, lockPartition: 1);
    private readonly PooledTransactionRequests _requests = new(2048);
    private ValueHash256[] _hashes = null!;
    private long _id;

    [Params(1, 64, 256)]
    public int HashCount { get; set; }

    [GlobalSetup]
    public void Setup() => _hashes = new ValueHash256[HashCount];

    [Benchmark(Baseline = true)]
    public ValueHash256 CopyAndClaim()
    {
        long id = ++_id;
        _baseline.Set(id, _hashes.AsSpan().ToArray());
        _baseline.Delete(id, out ValueHash256[] hashes);
        return hashes[^1];
    }

    [Benchmark]
    public ValueHash256 PoolAndClaim()
    {
        long id = ++_id;
        _requests.Add(id, _hashes);
        _requests.TryClaim(id, out PooledTransactionRequests.Request request);
        using (request) return request.Hashes[^1];
    }

    [GlobalCleanup]
    public void Cleanup() => _requests.Dispose();
}

[MemoryDiagnoser]
public class PooledTransactionRequestsConstructionBenchmarks
{
    [Benchmark(Baseline = true)]
    public ClockCache<long, ValueHash256[]> CreateClockCache() => new(2048, lockPartition: 1);

    [Benchmark]
    public void CreateAndDisposeTracker()
    {
        using PooledTransactionRequests requests = new(2048);
    }
}
