// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Threading;
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.TxPool;

namespace Nethermind.Network.Benchmarks;

internal sealed class RetryBenchmarkPeer : IMessageHandler<PooledTransactionRequestMessage>
{
    public void HandleMessage(PooledTransactionRequestMessage message) { }
}

[MemoryDiagnoser]
public class RetryAnnouncementBenchmarks
{
    private const int Operations = 32_768;
    private const int Tracked = 1024;
    private readonly ValueHash256[] _hashes = Enumerable.Range(0, Tracked + Operations)
        .Select(static i => ValueKeccak.Compute(BitConverter.GetBytes(i))).ToArray();
    private RetryCache<PooledTransactionRequestMessage, ValueHash256> _cache = null!;
    private RetryBenchmarkPeer[] _peers = null!;

    [Params(1, 8, 32)]
    public int Workers { get; set; }

    [IterationSetup]
    public void Setup()
    {
        _cache = new(NullLogManager.Instance, new ManualTimeProvider(), maxPendingResourcesPerHandler: Operations);
        _peers = Enumerable.Range(0, Workers).Select(static _ => new RetryBenchmarkPeer()).ToArray();
        RetryBenchmarkPeer source = new();
        for (int i = 0; i < Tracked; i++) _cache.Announced(_hashes[i], source);
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public void AnnounceTracked() => RunWorkers(true);

    [Benchmark(OperationsPerInvoke = Operations)]
    public void AnnounceAndReceive() => RunWorkers(false);

    private void RunWorkers(bool tracked)
    {
        using Barrier start = new(Workers);
        Thread[] workers = new Thread[Workers];
        for (int i = 0; i < Workers; i++)
        {
            int index = i;
            workers[i] = new Thread(() =>
            {
                start.SignalAndWait();
                for (int j = index; j < Operations; j += Workers)
                {
                    if (tracked) _cache.Announced(_hashes[j % Tracked], _peers[index]);
                    else
                    {
                        ValueHash256 hash = _hashes[Tracked + j];
                        _cache.Announced(hash, _peers[index]);
                        _cache.Received(hash);
                    }
                }
            });
            workers[i].Start();
        }
        foreach (Thread worker in workers) worker.Join();
    }

    [IterationCleanup]
    public void Cleanup() => _cache.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

[MemoryDiagnoser]
public class RetryTickBenchmarks
{
    private readonly ValueHash256[] _hashes = Enumerable.Range(0, 10_000)
        .Select(static i => ValueKeccak.Compute(BitConverter.GetBytes(i))).ToArray();
    private RetryCache<PooledTransactionRequestMessage, ValueHash256> _cache = null!;

    [Params(2, 50, 500)]
    public int Peers { get; set; }

    [IterationSetup]
    public void Setup()
    {
        ManualTimeProvider clock = new();
        _cache = new(NullLogManager.Instance, clock, maxPendingResourcesPerHandler: _hashes.Length);
        RetryBenchmarkPeer[] peers = Enumerable.Range(0, Peers).Select(static _ => new RetryBenchmarkPeer()).ToArray();
        for (int i = 0; i < _hashes.Length; i++)
        {
            _cache.Announced(_hashes[i], peers[i % Peers]);
            _cache.Announced(_hashes[i], peers[(i + 1) % Peers]);
        }
        clock.Advance(TimeSpan.FromMinutes(1));
    }

    [Benchmark]
    public void ProcessRetryTick() => _cache.ProcessRetryTick();

    [IterationCleanup]
    public void Cleanup() => _cache.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
