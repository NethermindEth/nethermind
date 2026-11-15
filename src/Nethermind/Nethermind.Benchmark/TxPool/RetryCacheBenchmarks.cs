// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.TxPool;

namespace Nethermind.Benchmarks.TxPool;

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class RetryCacheBenchmarks
{
    private const int BatchSize = 65536;
    private const int RequestingCacheSize = 13_107;

    private RetryCache<PooledTransactionRequestMessage, ValueHash256> _cache = null!;
    private Action<int> _announceAndReceive = null!;
    private Action<int> _receiveUntracked = null!;

    [Params(1, 8, 16)]
    public int Peers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _cache = new RetryCache<PooledTransactionRequestMessage, ValueHash256>(
            LimboLogs.Instance,
            TimeProvider.System,
            requestingCacheSize: RequestingCacheSize,
            overflowRequestLimit: RetryCache<PooledTransactionRequestMessage, ValueHash256>.DefaultOverflowRequestLimit);

        int iterations = BatchSize / Peers;
        Random random = new(42);
        byte[] bytes = new byte[ValueHash256.MemorySize];
        ValueHash256[][] hashes = new ValueHash256[Peers][];
        Handler[] handlers = new Handler[Peers];
        for (int peer = 0; peer < Peers; peer++)
        {
            handlers[peer] = new Handler();
            hashes[peer] = new ValueHash256[iterations];
            for (int i = 0; i < iterations; i++)
            {
                random.NextBytes(bytes);
                hashes[peer][i] = new ValueHash256(bytes);
            }
        }

        RetryCache<PooledTransactionRequestMessage, ValueHash256> cache = _cache;
        _announceAndReceive = peer =>
        {
            ValueHash256[] peerHashes = hashes[peer];
            Handler handler = handlers[peer];
            for (int i = 0; i < peerHashes.Length; i++)
            {
                cache.Announced(in peerHashes[i], handler);
                cache.Received(in peerHashes[i]);
            }
        };
        _receiveUntracked = peer =>
        {
            ValueHash256[] peerHashes = hashes[peer];
            for (int i = 0; i < peerHashes.Length; i++) cache.Received(in peerHashes[i]);
        };
    }

    [GlobalCleanup]
    public void Cleanup() => _cache.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public void AnnounceAndReceive() => Parallel.For(0, Peers, _announceAndReceive);

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public void ReceiveUntracked() => Parallel.For(0, Peers, _receiveUntracked);

    private sealed class Handler : IMessageHandler<PooledTransactionRequestMessage>
    {
        public void HandleMessage(PooledTransactionRequestMessage message) { }
    }
}
