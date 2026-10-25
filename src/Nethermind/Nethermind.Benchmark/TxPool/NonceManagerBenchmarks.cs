// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;

namespace Nethermind.Benchmarks.TxPool;

/// <summary>
/// Managed-nonce reservations for one sender whose account nonce stays fixed while its pending backlog grows,
/// as happens when a wallet sends faster than blocks include its transactions.
/// </summary>
[MemoryDiagnoser]
public class NonceManagerBenchmarks
{
    private const int OwnRemovalInterval = 16;

    private static readonly Address Sender = TestItem.AddressA;

    private Transaction[] _transactions;
    private Transaction[] _otherSenderTransactions;
    private FakePool _pool;
    private IChainHeadInfoProvider _chainHead;
    private NonceManager _backlogged;

    [Params(1, 16, 64, 256, 1024)]
    public int Pending { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _pool = new FakePool();
        _transactions = BuildTransactions(Sender, Pending + 1, "own");
        _otherSenderTransactions = BuildTransactions(OtherSender(), Pending, "other");
        foreach (Transaction transaction in _transactions)
        {
            _pool.Add(transaction);
        }

        foreach (Transaction transaction in _otherSenderTransactions)
        {
            _pool.Add(transaction);
        }

        _chainHead = Substitute.For<IChainHeadInfoProvider>();
        _chainHead.ReadOnlyStateProvider.Returns(new TestReadOnlyStateProvider());
        _backlogged = CreateNonceManager();
        for (int i = 0; i < Pending; i++)
        {
            ReserveAndAccept(_backlogged);
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
        NonceManager nonceManager = CreateNonceManager();
        ulong last = 0;
        for (int i = 0; i < Pending; i++)
        {
            last = ReserveAndAccept(nonceManager);
        }

        return last;
    }

    /// <summary>Another sender's transaction leaves the pool before every send.</summary>
    [Benchmark]
    public ulong BurstWithOtherSenderRemovals()
    {
        NonceManager nonceManager = CreateNonceManager();
        ulong last = 0;
        for (int i = 0; i < Pending; i++)
        {
            _pool.Remove(_otherSenderTransactions[i]);
            last = ReserveAndAccept(nonceManager);
        }

        foreach (Transaction transaction in _otherSenderTransactions)
        {
            _pool.Add(transaction);
        }

        return last;
    }

    /// <summary>Every <see cref="OwnRemovalInterval"/> sends one of the sender's own pending transactions leaves the
    /// pool, and the next send refills its nonce.</summary>
    [Benchmark]
    public ulong BurstWithOwnRemovals()
    {
        NonceManager nonceManager = CreateNonceManager();
        ulong last = 0;
        for (int i = 0; i < Pending; i++)
        {
            if (i % OwnRemovalInterval == OwnRemovalInterval - 1)
            {
                _pool.Remove(_transactions[i / 2]);
            }

            last = ReserveAndAccept(nonceManager);
        }

        return last;
    }

    private NonceManager CreateNonceManager() =>
        new(_chainHead, Substitute.For<IStateHeaderProvider>(), Substitute.For<IStateReader>());

    private ulong ReserveAndAccept(NonceManager nonceManager)
    {
        using NonceLocker locker = nonceManager.ReserveNonce(Sender, _pool, out ulong nonce);
        Transaction transaction = _transactions[nonce];
        _pool.Add(transaction);
        locker.Accept(transaction);
        return nonce;
    }

    private static Transaction[] BuildTransactions(Address sender, int count, string seed)
    {
        Transaction[] transactions = new Transaction[count];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithNonce((ulong)i)
                .WithSenderAddress(sender)
                .WithHash(Keccak.Compute(seed + i))
                .TestObject;
        }

        return transactions;
    }

    private static Address OtherSender()
    {
        foreach (Address address in TestItem.Addresses)
        {
            if (FakePool.Stripe(address) != FakePool.Stripe(Sender))
            {
                return address;
            }
        }

        return TestItem.AddressB;
    }


    /// <summary>Counts removals in address-hash stripes, as the pool does.</summary>
    private sealed class FakePool : IPendingTxsBySender
    {
        private const int Stripes = 256;

        private readonly McsLock _lock = new();
        private readonly Dictionary<Hash256, Transaction> _transactions = [];
        private readonly long[] _removalGenerations = new long[Stripes];

        public static int Stripe(Address sender) => sender.GetHashCode() & (Stripes - 1);

        public void Add(Transaction transaction)
        {
            using McsLock.Disposable handle = _lock.Acquire();
            _transactions[transaction.Hash!] = transaction;
        }

        public void Remove(Transaction transaction)
        {
            using McsLock.Disposable handle = _lock.Acquire();
            if (_transactions.Remove(transaction.Hash!))
            {
                ref long generation = ref _removalGenerations[Stripe(transaction.SenderAddress!)];
                Volatile.Write(ref generation, generation + 1);
            }
        }

        public Transaction[] GetPendingTransactionsBySender(Address address)
        {
            using McsLock.Disposable handle = _lock.Acquire();
            List<Transaction> pending = [];
            foreach (Transaction transaction in _transactions.Values)
            {
                if (transaction.SenderAddress == address)
                {
                    pending.Add(transaction);
                }
            }

            return [.. pending];
        }

        public Transaction[] GetPendingLightBlobTransactionsBySender(Address address) => [];

        public bool ContainsTx(Hash256 hash, TxType txType)
        {
            using McsLock.Disposable handle = _lock.Acquire();
            return _transactions.ContainsKey(hash);
        }

        public long GetRemovalGeneration(Address sender) => Volatile.Read(ref _removalGenerations[Stripe(sender)]);
    }
}
