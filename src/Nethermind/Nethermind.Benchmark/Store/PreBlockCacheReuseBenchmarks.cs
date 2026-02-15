// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Benchmarks.Store;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 6)]
public class PreBlockCacheReuseBenchmarks
{
    private readonly Task _completedTask = Task.CompletedTask;
    private readonly PreBlockCaches _preBlockCaches = new();
    private readonly FakeEnvPool _envPool = new();

    private Address[] _withdrawalAddresses = null!;
    private int[] _txGroupStarts = null!;
    private int[] _txGroupLengths = null!;
    private Address[] _txGroupedAddresses = null!;

    [Params(1024)]
    public int WithdrawalCount { get; set; }

    [Params(8)]
    public int Concurrency { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(1);
        byte[] bytes = new byte[20];
        _withdrawalAddresses = new Address[WithdrawalCount];

        for (int i = 0; i < WithdrawalCount; i++)
        {
            random.NextBytes(bytes);
            _withdrawalAddresses[i] = new Address((byte[])bytes.Clone());
        }

        const int groups = 512;
        _txGroupStarts = new int[groups];
        _txGroupLengths = new int[groups];
        int totalTxs = 0;
        for (int i = 0; i < groups; i++)
        {
            _txGroupStarts[i] = totalTxs;
            int groupLength = 1 + (i % 4);
            _txGroupLengths[i] = groupLength;
            totalTxs += groupLength;
        }

        _txGroupedAddresses = new Address[totalTxs];
        for (int i = 0; i < totalTxs; i++)
        {
            random.NextBytes(bytes);
            _txGroupedAddresses[i] = new Address((byte[])bytes.Clone());
        }
    }

    [Benchmark(Baseline = true)]
    public void Legacy_DoubleClearWithContinuation()
    {
        Task clearTask = _completedTask.ContinueWith(
            static (_, state) => ((PreBlockCaches)state!).ClearCaches(),
            _preBlockCaches,
            TaskContinuationOptions.RunContinuationsAsynchronously);

        clearTask.GetAwaiter().GetResult();
        _preBlockCaches.ClearCaches();
    }

    [Benchmark]
    public void Current_SingleStartClear()
    {
        _preBlockCaches.ClearCaches();
    }

    [Benchmark]
    public void Legacy_WithdrawalsPerItemScope()
    {
        ParallelOptions options = new() { MaxDegreeOfParallelism = Concurrency };

        Parallel.For(0, _withdrawalAddresses.Length, options, i =>
        {
            FakeEnv env = _envPool.Get();
            try
            {
                using FakeScope scope = env.Build();
                scope.WarmUp(_withdrawalAddresses[i]);
            }
            finally
            {
                _envPool.Return(env);
            }
        });
    }

    [Benchmark]
    public void Current_WithdrawalsPerThreadScope()
    {
        ParallelOptions options = new() { MaxDegreeOfParallelism = Concurrency };

        Parallel.For<WithdrawalThreadState>(
            0,
            _withdrawalAddresses.Length,
            options,
            () =>
            {
                FakeEnv env = _envPool.Get();
                return new WithdrawalThreadState(env, env.Build(), _envPool);
            },
            (i, _, state) =>
            {
                state.Scope.WarmUp(_withdrawalAddresses[i]);
                return state;
            },
            static state => state.Dispose());
    }

    [Benchmark]
    public void Legacy_TxGroupsPerGroupScope()
    {
        ParallelOptions options = new() { MaxDegreeOfParallelism = Concurrency };

        Parallel.For(0, _txGroupStarts.Length, options, groupIndex =>
        {
            FakeEnv env = _envPool.Get();
            try
            {
                using FakeScope scope = env.Build();
                int start = _txGroupStarts[groupIndex];
                int end = start + _txGroupLengths[groupIndex];
                for (int i = start; i < end; i++)
                {
                    scope.WarmUp(_txGroupedAddresses[i]);
                }
            }
            finally
            {
                _envPool.Return(env);
            }
        });
    }

    [Benchmark]
    public void Current_TxGroupsPerThreadScope()
    {
        ParallelOptions options = new() { MaxDegreeOfParallelism = Concurrency };

        Parallel.For<WithdrawalThreadState>(
            0,
            _txGroupStarts.Length,
            options,
            () =>
            {
                FakeEnv env = _envPool.Get();
                return new WithdrawalThreadState(env, env.Build(), _envPool);
            },
            (groupIndex, _, state) =>
            {
                int start = _txGroupStarts[groupIndex];
                int end = start + _txGroupLengths[groupIndex];
                for (int i = start; i < end; i++)
                {
                    state.Scope.WarmUp(_txGroupedAddresses[i]);
                }

                return state;
            },
            static state => state.Dispose());
    }

    private sealed class FakeEnvPool
    {
        private readonly ConcurrentQueue<FakeEnv> _queue = new();

        public FakeEnv Get()
        {
            if (_queue.TryDequeue(out FakeEnv env))
            {
                return env;
            }

            return new FakeEnv();
        }

        public void Return(FakeEnv env) => _queue.Enqueue(env);
    }

    private sealed class FakeEnv
    {
        public FakeScope Build() => new();
    }

    private sealed class FakeScope : IDisposable
    {
        private static int _sink;

        public void WarmUp(Address address)
        {
            _sink ^= address.GetHashCode();
        }

        public void Dispose()
        {
        }
    }

    private sealed class WithdrawalThreadState(FakeEnv env, FakeScope scope, FakeEnvPool envPool) : IDisposable
    {
        public FakeScope Scope { get; } = scope;
        private FakeEnv Env { get; } = env;
        private FakeEnvPool EnvPool { get; } = envPool;

        public void Dispose()
        {
            Scope.Dispose();
            EnvPool.Return(Env);
        }
    }
}
