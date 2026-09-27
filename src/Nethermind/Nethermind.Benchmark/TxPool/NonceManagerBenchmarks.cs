// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.TxPool;

namespace Nethermind.Benchmarks.TxPool;

/// <summary>
/// Managed-nonce reservations for one sender whose account nonce stays fixed while its pending backlog grows,
/// as happens when a wallet sends faster than blocks include its transactions.
/// </summary>
[MemoryDiagnoser]
public class NonceManagerBenchmarks
{
    private static readonly Address Sender = TestItem.AddressA;

    private Transaction[] _transactions;
    private FakePool _pool;
    private NonceManager _backlogged;

    [Params(1, 16, 64, 256, 1024)]
    public int Pending { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _transactions = new Transaction[Pending + 1];
        _pool = new FakePool();
        for (int i = 0; i < _transactions.Length; i++)
        {
            _transactions[i] = Build.A.Transaction
                .WithNonce((ulong)i)
                .WithSenderAddress(Sender)
                .WithHash(Keccak.Compute(i.ToString()))
                .TestObject;
            _pool.Add(_transactions[i]);
        }

        _backlogged = new NonceManager(new FixedNonceAccounts());
        for (int i = 0; i < Pending; i++)
        {
            ReserveAndAccept(_backlogged, i);
        }
    }

    [Benchmark]
    public ulong ReserveAtBacklog()
    {
        using NonceLocker locker = _backlogged.ReserveNonce(Sender, _pool, out ulong nonce);
        return nonce;
    }

    [Benchmark]
    public ulong BurstOfSequentialSends()
    {
        NonceManager nonceManager = new(new FixedNonceAccounts());
        ulong last = 0;
        for (int i = 0; i < Pending; i++)
        {
            last = ReserveAndAccept(nonceManager, i);
        }

        return last;
    }

    private ulong ReserveAndAccept(NonceManager nonceManager, int index)
    {
        using NonceLocker locker = nonceManager.ReserveNonce(Sender, _pool, out ulong nonce);
        locker.Accept(_transactions[index]);
        return nonce;
    }

    private sealed class FixedNonceAccounts : IAccountStateProvider
    {
        public bool TryGetAccount(Address address, out AccountStruct account)
        {
            account = AccountStruct.TotallyEmpty;
            return true;
        }
    }

    private sealed class FakePool : IPendingTxsBySender
    {
        private readonly McsLock _lock = new();
        private readonly Dictionary<Hash256, Transaction> _transactions = [];

        public void Add(Transaction transaction) => _transactions[transaction.Hash!] = transaction;

        public Transaction[] GetPendingTransactionsBySender(Address address) => [.. _transactions.Values];

        public Transaction[] GetPendingLightBlobTransactionsBySender(Address address) => [];

        public bool ContainsTx(Hash256 hash, TxType txType)
        {
            using McsLock.Disposable handle = _lock.Acquire();
            return _transactions.ContainsKey(hash);
        }
    }
}
