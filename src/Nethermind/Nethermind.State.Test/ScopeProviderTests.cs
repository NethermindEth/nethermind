// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
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
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            scope.Get(TestItem.AddressA).Should().Be(null);
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        stateRoot.Should().NotBe(Keccak.EmptyTreeHash);
        if (!useFlat) ctx.Kv.WritesCount.Should().Be(1);

        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            scope.Get(TestItem.AddressA).Balance.Should().Be(100);
        }
    }

    [Test]
    public void Test_CanSaveToStorage()
    {
        using Context ctx = new(useFlat);

        Hash256 stateRoot;
        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            scope.Get(TestItem.AddressA).Should().Be(null);

            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(TestItem.AddressA, new Account(100, 100));

                using (IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1))
                {
                    storageSet.Set(1, [1, 2, 3]);
                }
            }

            scope.Commit(1);
            stateRoot = scope.RootHash;
        }

        stateRoot.Should().NotBe(Keccak.EmptyTreeHash);
        if (!useFlat) ctx.Kv.WritesCount.Should().Be(2);

        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            IWorldStateScopeProvider.IStorageTree storage = scope.CreateStorageTree(TestItem.AddressA);
            storage.Get(1).Should().BeEquivalentTo([1, 2, 3]);
        }
    }

    [Test]
    public void Test_CanSaveToCode()
    {
        using Context ctx = new(useFlat);

        using (IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null))
        {
            using (IWorldStateScopeProvider.ICodeSetter writer = scope.CodeDb.BeginCodeWrite())
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
            using IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null);
            scope.CodeDb.GetCode(TestItem.KeccakA).Should().BeEquivalentTo([1, 2, 3]);
        }
    }

    [Test]
    public void Test_NullAccountWithNonEmptyStorageDoesNotThrow()
    {
        using Context ctx = new(useFlat);
        using IWorldStateScopeProvider.IScope scope = ctx.ScopeProvider.BeginScope(null);

        // Simulates the EIP-161 scenario: storage is flushed for an account that was
        // then deleted (set to null) during state commit. The write batch Dispose should
        // skip the storage root update for the deleted account instead of throwing.
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            using (IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1))
            {
                storageSet.Set(1, [1, 2, 3]);
            }

            writeBatch.Set(TestItem.AddressA, null);
        }
    }

#nullable enable

    [Test]
    public void Cross_block_storage_cache_uses_write_through_value_on_next_block()
    {
        RecordingScopeProvider baseProvider = new();
        PrewarmerScopeProvider provider = new(baseProvider, new PreBlockCaches(), populatePreBlockCache: false, new CrossBlockCaches());

        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(null))
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1);
            writeBatch.Set(TestItem.AddressA, new Account(1, 1));
            using IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storageSet.Set(1, [1, 2, 3]);
            scope.Commit(1);
        }

        baseProvider.ResetStats();

        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(Build.A.BlockHeader.WithNumber(1).TestObject))
        {
            scope.CreateStorageTree(TestItem.AddressA).Get(1).Should().BeEquivalentTo([1, 2, 3]);
        }

        baseProvider.StorageReads.Should().Be(0);
        baseProvider.StorageHintGets.Should().Be(0);
    }

    [Test]
    public void Cross_block_storage_cache_clears_on_discontinuity()
    {
        RecordingScopeProvider baseProvider = new();
        PrewarmerScopeProvider provider = new(baseProvider, new PreBlockCaches(), populatePreBlockCache: false, new CrossBlockCaches());

        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(null))
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1);
            using IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storageSet.Set(1, [1, 2, 3]);
            scope.Commit(1);
        }

        baseProvider.ResetStats();

        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(Build.A.BlockHeader.WithNumber(0).TestObject))
        {
            scope.CreateStorageTree(TestItem.AddressA).Get(1).Should().BeEquivalentTo([1, 2, 3]);
        }

        baseProvider.StorageReads.Should().Be(1);
    }

    [Test]
    public void Cross_block_storage_cache_discards_uncommitted_writes()
    {
        RecordingScopeProvider baseProvider = new();
        PrewarmerScopeProvider provider = new(baseProvider, new PreBlockCaches(), populatePreBlockCache: false, new CrossBlockCaches());

        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(null))
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1);
            using IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storageSet.Set(1, [1, 2, 3]);
            scope.Commit(1);
        }

        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(Build.A.BlockHeader.WithNumber(1).TestObject))
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1);
            using IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storageSet.Set(1, [9, 9, 9]);
        }

        baseProvider.ResetStats();

        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(Build.A.BlockHeader.WithNumber(1).TestObject))
        {
            scope.CreateStorageTree(TestItem.AddressA).Get(1).Should().BeEquivalentTo([1, 2, 3]);
        }

        baseProvider.StorageReads.Should().Be(1);
    }

    [Test]
    public void Cross_block_storage_cache_clears_after_storage_clear_commit()
    {
        RecordingScopeProvider baseProvider = new();
        PrewarmerScopeProvider provider = new(baseProvider, new PreBlockCaches(), populatePreBlockCache: false, new CrossBlockCaches());

        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(null))
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1);
            using IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storageSet.Set(1, [1, 2, 3]);
            scope.Commit(1);
        }

        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(Build.A.BlockHeader.WithNumber(1).TestObject))
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1);
            using IWorldStateScopeProvider.IStorageWriteBatch storageSet = writeBatch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storageSet.Clear();
            scope.Commit(2);
        }

        baseProvider.ResetStats();

        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(Build.A.BlockHeader.WithNumber(2).TestObject))
        {
            scope.CreateStorageTree(TestItem.AddressA).Get(1).Should().BeEquivalentTo(Nethermind.State.StorageTree.ZeroBytes);
        }

        baseProvider.StorageReads.Should().Be(1);
    }

    private sealed class RecordingScopeProvider : IWorldStateScopeProvider
    {
        private readonly Dictionary<Address, Account?> _accounts = [];
        private readonly Dictionary<StorageCell, byte[]> _storage = [];

        public int StorageReads { get; private set; }
        public int StorageHintGets { get; private set; }

        public bool HasRoot(BlockHeader? baseBlock) => true;

        public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock) => new Scope(this);

        public void ResetStats()
        {
            StorageReads = 0;
            StorageHintGets = 0;
        }

        private sealed class Scope(RecordingScopeProvider owner) : IWorldStateScopeProvider.IScope
        {
            private readonly Dictionary<Address, Account?> _accountWrites = [];
            private readonly Dictionary<StorageCell, byte[]> _storageWrites = [];
            private readonly HashSet<Address> _storageClears = [];

            public void Dispose()
            {
            }

            public Hash256 RootHash => Keccak.EmptyTreeHash;

            public void UpdateRootHash()
            {
            }

            public Account? Get(Address address) => owner._accounts.GetValueOrDefault(address);

            public void HintGet(Address address, Account? account)
            {
            }

            public IWorldStateScopeProvider.ICodeDb CodeDb => NoCodeDb.Instance;

            public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => new RecordingStorageTree(owner, address);

            public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => new WriteBatch(_accountWrites, _storageWrites, _storageClears);

            public void Commit(long blockNumber)
            {
                foreach (Address address in _storageClears)
                {
                    List<StorageCell> keysToRemove = [];
                    foreach (StorageCell cell in owner._storage.Keys)
                    {
                        if (cell.Address == address)
                        {
                            keysToRemove.Add(cell);
                        }
                    }

                    foreach (StorageCell cell in keysToRemove)
                    {
                        owner._storage.Remove(cell);
                    }
                }

                foreach (KeyValuePair<StorageCell, byte[]> entry in _storageWrites)
                {
                    owner._storage[entry.Key] = entry.Value;
                }

                foreach (KeyValuePair<Address, Account?> entry in _accountWrites)
                {
                    owner._accounts[entry.Key] = entry.Value;
                }
            }
        }

        private sealed class RecordingStorageTree(RecordingScopeProvider owner, Address address) : IWorldStateScopeProvider.IStorageTree
        {
            public Hash256 RootHash => Keccak.EmptyTreeHash;

            public byte[] Get(in UInt256 index)
            {
                owner.StorageReads++;
                return owner._storage.TryGetValue(new StorageCell(address, in index), out byte[]? value) ? value : Nethermind.State.StorageTree.ZeroBytes;
            }

            public void HintGet(in UInt256 index, byte[]? value) => owner.StorageHintGets++;

            public byte[] Get(in ValueHash256 hash) => Nethermind.State.StorageTree.ZeroBytes;
        }

        private sealed class WriteBatch(
            Dictionary<Address, Account?> accountWrites,
            Dictionary<StorageCell, byte[]> storageWrites,
            HashSet<Address> storageClears) : IWorldStateScopeProvider.IWorldStateWriteBatch
        {
            public void Dispose()
            {
            }

            public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

            public void Set(Address key, Account? account)
            {
                accountWrites[key] = account;
                OnAccountUpdated?.Invoke(this, new IWorldStateScopeProvider.AccountUpdated(key, account));
            }

            public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) => new StorageWriteBatch(key, storageWrites, storageClears);
        }

        private sealed class StorageWriteBatch(
            Address address,
            Dictionary<StorageCell, byte[]> storageWrites,
            HashSet<Address> storageClears) : IWorldStateScopeProvider.IStorageWriteBatch
        {
            public void Dispose()
            {
            }

            public void Set(in UInt256 index, byte[] value) => storageWrites[new StorageCell(address, in index)] = value;

            public void Clear() => storageClears.Add(address);
        }

        private sealed class NoCodeDb : IWorldStateScopeProvider.ICodeDb
        {
            public static NoCodeDb Instance { get; } = new();

            public byte[]? GetCode(in ValueHash256 codeHash) => null;

            public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => NoCodeSetter.Instance;
        }

        private sealed class NoCodeSetter : IWorldStateScopeProvider.ICodeSetter
        {
            public static NoCodeSetter Instance { get; } = new();

            public void Dispose()
            {
            }

            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code)
            {
            }
        }
    }
}
