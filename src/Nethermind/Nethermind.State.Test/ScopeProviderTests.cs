// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture(false)]
[TestFixture(true)]
[Parallelizable(ParallelScope.All)]
public class ScopeProviderTests(bool useFlat)
{
    private class Context : IDisposable
    {
        public IWorldStateScopeProvider ScopeProvider { get; }
        public TestMemDb Kv { get; }
        public TestMemDb CodeKv { get; }
        private readonly IContainer _container;

        public Context(bool useFlat, TestMemDb kv = null, TestMemDb codeKv = null)
        {
            if (useFlat)
            {
                (ScopeProvider, _container) = TestWorldStateFactory.CreateFlatScopeProvider();
            }
            else
            {
                Kv = kv ?? new TestMemDb();
                CodeKv = codeKv ?? new TestMemDb();
                ScopeProvider = new TrieStoreScopeProvider(new TestRawTrieStore(Kv), CodeKv, LimboLogs.Instance);
            }
        }

        public void Dispose() => _container?.Dispose();
    }

    [Test]
    public void Test_CanSaveToState()
    {
        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (var scope = ctx.ScopeProvider.BeginScope(null))
        {
            scope.Get(TestItem.AddressA).Should().Be(null);
            using (var writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        stateRoot.Should().NotBe(Keccak.EmptyTreeHash);
        if (!useFlat) ctx.Kv.WritesCount.Should().Be(1);

        using (var scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            scope.Get(TestItem.AddressA).Balance.Should().Be(100);
        }
    }

    [Test]
    public void Test_CanSaveToStorage()
    {
        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (var scope = ctx.ScopeProvider.BeginScope(null))
        {
            scope.Get(TestItem.AddressA).Should().Be(null);

            using (var writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));

                using (var storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1))
                {
                    storageSet.Set(1, [1, 2, 3]);
                }
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        stateRoot.Should().NotBe(Keccak.EmptyTreeHash);
        if (!useFlat) ctx.Kv.WritesCount.Should().Be(2);

        using (var scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            var storage = scope.CreateStorageTree(TestItem.AddressA);
            storage.Get(1).Should().BeEquivalentTo([1, 2, 3]);
        }
    }

    [Test]
    public void Test_CanSaveToCode()
    {
        using Context ctx = new(useFlat);

        using (var scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (var writer = scope.CodeDb.BeginCodeWrite())
            {
                writer.Set(TestItem.KeccakA, [1, 2, 3]);
            }
        }

        if (!useFlat)
        {
            ctx.CodeKv.WritesCount.Should().Be(1);
        }
        else
        {
            using var scope = ctx.ScopeProvider.BeginScope(null);
            scope.CodeDb.GetCode(TestItem.KeccakA).Should().BeEquivalentTo([1, 2, 3]);
        }
    }

    [Test]
    public void Test_NullAccountWithNonEmptyStorageDoesNotThrow()
    {
        using Context ctx = new(useFlat);
        using var scope = ctx.ScopeProvider.BeginScope(null);

        // Simulates the EIP-161 scenario: storage is flushed for an account that was
        // then deleted (set to null) during state commit. The write batch Dispose should
        // skip the storage root update for the deleted account instead of throwing.
        using (var writeBatch = scope.StartWriteBatch(1))
        {
            using (var storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1))
            {
                storageSet.Set(1, [1, 2, 3]);
            }

            writeBatch.Set(TestItem.AddressA, null);
        }
    }

    [Test]
    public void Test_ReadBalAsync_MatchesIndividualReads()
    {
        using Context ctx = new(useFlat);

        // Setup: write accounts with storage
        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(2))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
                writeBatch.Set(TestItem.AddressB, new Account(200, 200));

                using (IWorldStateScopeProvider.IStorageWriteBatch storageA = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 2))
                {
                    storageA.Set(1, [10, 20]);
                    storageA.Set(2, [30, 40]);
                }

                using (IWorldStateScopeProvider.IStorageWriteBatch storageB = writeBatch.CreateStorageWriteBatch(TestItem.AddressB, 1))
                {
                    storageB.Set(5, [50, 60]);
                }
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        // Build a BAL referencing these accounts and storage slots
        BlockAccessList bal = new();
        bal.AddAccountRead(TestItem.AddressA);
        bal.AddAccountRead(TestItem.AddressB);
        bal.AddAccountRead(TestItem.AddressC); // not in state — should be null
        bal.AddStorageRead(TestItem.AddressA, 1);
        bal.AddStorageRead(TestItem.AddressA, 2);
        bal.AddStorageRead(TestItem.AddressB, 5);

        // Collect results via ReadBalAsync
        CollectingBalSink sink = new();
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            scope.ReadBalAsync(bal, sink, CancellationToken.None).Wait();

            // Verify accounts match individual reads
            sink.Accounts.Should().ContainKey(TestItem.AddressA);
            sink.Accounts[TestItem.AddressA]!.Balance.Should().Be(100);

            sink.Accounts.Should().ContainKey(TestItem.AddressB);
            sink.Accounts[TestItem.AddressB]!.Balance.Should().Be(200);

            sink.NullAccounts.Should().Contain(TestItem.AddressC);

            // Verify storage matches individual reads
            IWorldStateScopeProvider.IStorageTree storageTreeA = scope.CreateStorageTree(TestItem.AddressA);
            IWorldStateScopeProvider.IStorageTree storageTreeB = scope.CreateStorageTree(TestItem.AddressB);

            StorageCell cellA1 = new(TestItem.AddressA, 1);
            StorageCell cellA2 = new(TestItem.AddressA, 2);
            StorageCell cellB5 = new(TestItem.AddressB, 5);

            sink.Storage.Should().ContainKey(cellA1);
            sink.Storage[cellA1].Should().BeEquivalentTo(storageTreeA.Get(1));

            sink.Storage.Should().ContainKey(cellA2);
            sink.Storage[cellA2].Should().BeEquivalentTo(storageTreeA.Get(2));

            sink.Storage.Should().ContainKey(cellB5);
            sink.Storage[cellB5].Should().BeEquivalentTo(storageTreeB.Get(5));
        }
    }

    private class CollectingBalSink : IWorldStateScopeProvider.IAsyncBalReaderSink
    {
        public Dictionary<Address, Account> Accounts { get; } = new();
        public HashSet<Address> NullAccounts { get; } = new();
        public Dictionary<StorageCell, byte[]> Storage { get; } = new();

        public void OnAccountRead(Address address, Account account)
        {
            if (account is null)
                NullAccounts.Add(address);
            else
                Accounts[address] = account;
        }

        public void OnStorageRead(in StorageCell storageCell, byte[] value)
        {
            Storage[storageCell] = value;
        }
    }
}
