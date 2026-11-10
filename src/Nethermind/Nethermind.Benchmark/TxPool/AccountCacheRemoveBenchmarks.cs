// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using AccountCache = Nethermind.TxPool.TxPool.AccountCache;

namespace Nethermind.Benchmarks.TxPool;

/// <summary>
/// The pool's account cache dropping the accounts a new block changed. This runs under the new-head write lock, which
/// every transaction submission waits on. The cache is full and the changed accounts are not in it, as for most
/// accounts a block touches; with a busy thread pool, as right after block processing.
/// </summary>
[MemoryDiagnoser]
public class AccountCacheRemoveBenchmarks
{
    private const int CachedAccounts = 16 * 1024;

    private AccountCache _cache;
    private ArrayPoolList<AddressAsKey> _changed;
    private CancellationTokenSource _load;
    private Task[] _loadTasks = [];

    [Params(256, 1000, 2000)]
    public int ChangedAccounts { get; set; }

    [Params(false, true)]
    public bool BusyThreadPool { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        byte[] bytes = new byte[Address.Size];
        _cache = new AccountCache(new EmptyAccounts());
        for (int i = 0; i < CachedAccounts; i++)
        {
            random.NextBytes(bytes);
            _cache.TryGetAccount(new Address(bytes), out _);
        }

        _changed = [with(ChangedAccounts)];
        for (int i = 0; i < ChangedAccounts; i++)
        {
            random.NextBytes(bytes);
            _changed.Add(new Address(bytes));
        }

        if (BusyThreadPool)
        {
            _load = new CancellationTokenSource();
            CancellationToken token = _load.Token;
            _loadTasks = new Task[Environment.ProcessorCount];
            for (int i = 0; i < _loadTasks.Length; i++)
            {
                _loadTasks[i] = Task.Run(() =>
                {
                    while (!token.IsCancellationRequested) Thread.SpinWait(1_000);
                });
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _load?.Cancel();
        Task.WaitAll(_loadTasks);
        _load?.Dispose();
        _changed.Dispose();
    }

    [Benchmark]
    public void RemoveAccounts() => _cache.RemoveAccounts(_changed);

    private sealed class EmptyAccounts : IAccountStateProvider
    {
        public bool TryGetAccount(Address address, out AccountStruct account)
        {
            account = AccountStruct.TotallyEmpty;
            return true;
        }
    }
}
