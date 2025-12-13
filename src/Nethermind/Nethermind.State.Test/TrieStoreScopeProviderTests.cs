// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class TrieStoreScopeProviderTests
{
    [Test]
    public void Test_CanSaveToState()
    {
        TestMemDb kv = new TestMemDb();
        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(new TestRawTrieStore(kv), new MemDb(), LimboLogs.Instance);

        Hash256 stateRoot;
        using (var scope = scopeProvider.BeginScope(null))
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
        kv.WritesCount.Should().Be(1);

        using (var scope = scopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            scope.Get(TestItem.AddressA).Balance.Should().Be(100);
        }
    }

    [Test]
    public void Test_CanSaveToStorage()
    {
        TestMemDb kv = new TestMemDb();
        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(new TestRawTrieStore(kv), new MemDb(), LimboLogs.Instance);

        Hash256 stateRoot;
        using (var scope = scopeProvider.BeginScope(null))
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
        kv.WritesCount.Should().Be(2);

        using (var scope = scopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            var storage = scope.CreateStorageTree(TestItem.AddressA);
            storage.Get(1).Should().BeEquivalentTo([1, 2, 3]);
        }
    }

    [Test]
    public void Test_CanSaveToCode()
    {
        TestMemDb kv = new TestMemDb();
        TestMemDb codeKv = new TestMemDb();
        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(new TestRawTrieStore(kv), codeKv, LimboLogs.Instance);

        using (var scope = scopeProvider.BeginScope(null))
        {
            using (var writer = scope.CodeDb.BeginCodeWrite())
            {
                writer.Set(TestItem.KeccakA, [1, 2, 3]);
            }
        }

        codeKv.WritesCount.Should().Be(1);
    }
}
