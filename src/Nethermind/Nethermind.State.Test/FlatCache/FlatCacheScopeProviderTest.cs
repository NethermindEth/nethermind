// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.FlatCache;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class FlatCacheScopeProviderTest
{
    private TrieStoreScopeProvider _baseScopeProvider;
    private TestMemDb _stateDb;
    private FlatCacheScopeProvider _cachedScopeProvider;
    private FlatCacheRepository _cacheRepository;
    private SnapshotsStore _snapshotsStore;
    private PersistedBigCache _persistedBigCache;
    private ICanonicalStateRootFinder _canonicalStateRootFinder;

    [SetUp]
    public void Setup()
    {
        var logManager = SimpleConsoleLogManager.Instance;
        _stateDb = new TestMemDb();
        var codeDb = new TestMemDb();
        _canonicalStateRootFinder = Substitute.For<ICanonicalStateRootFinder>();
        _baseScopeProvider = new TrieStoreScopeProvider(new TestRawTrieStore(_stateDb), codeDb, logManager);
        _snapshotsStore = new SnapshotsStore(new TestSortedMemDb(), new EthereumJsonSerializer(), logManager);
        _persistedBigCache = new PersistedBigCache(new MemDb(), new MemDb());
        _cacheRepository = new FlatCacheRepository(
            Substitute.For<IProcessExitSource>(),
            _snapshotsStore,
            _canonicalStateRootFinder,
            _persistedBigCache,
            logManager,
            new FlatCacheRepository.Configuration(
                MaxInFlightCompactJob: 32,
                CompactSize: 32,
                InlineCompaction: true
            ));
        _cachedScopeProvider = new FlatCacheScopeProvider(_baseScopeProvider, _cacheRepository, false, logManager);
    }

    private (FlatCacheScopeProvider, FlatCacheRepository, TestMemDb) CustomFlatCacheProvider(FlatCacheRepository.Configuration config)
    {
        var stateDb = new TestMemDb();
        var codeDb = new TestMemDb();
        var baseScopeProvider = new TrieStoreScopeProvider(new TestRawTrieStore(stateDb), codeDb, LimboLogs.Instance);
        SnapshotsStore snapshotsStore = new SnapshotsStore(new TestSortedMemDb(), new EthereumJsonSerializer(), LimboLogs.Instance);
        PersistedBigCache persistedBigCache = new PersistedBigCache(new MemDb(), new MemDb());
        var cacheRepository = new FlatCacheRepository(
            Substitute.For<IProcessExitSource>(),
            snapshotsStore,
            _canonicalStateRootFinder,
            persistedBigCache,
            LimboLogs.Instance, config);
        var cachedScopeProvider = new FlatCacheScopeProvider(baseScopeProvider, cacheRepository, false, LimboLogs.Instance);
        return (cachedScopeProvider, cacheRepository, stateDb);
    }

    [TearDown]
    public void TearDown()
    {
        _stateDb.Dispose();
    }

    [Test]
    public void TestReadUncached()
    {
        var baseWorldState = new WorldState(_baseScopeProvider, LimboLogs.Instance);
        BlockHeader baseBlock;
        using (baseWorldState.BeginScope(null))
        {
            baseWorldState.AddToBalanceAndCreateIfNotExists(TestItem.AddressA, 10, London.Instance);
            baseWorldState.Set(new StorageCell(TestItem.AddressA, 30), Bytes.FromHexString("01234567"));
            baseWorldState.Commit(London.Instance);
            baseWorldState.CommitTree(0);

            baseBlock = Build.A.BlockHeader.WithStateRoot(baseWorldState.StateRoot).TestObject;
        }

        var cacheRepository = new FlatCacheRepository(
            Substitute.For<IProcessExitSource>(),
            _snapshotsStore,
            _canonicalStateRootFinder,
            _persistedBigCache,
            LimboLogs.Instance);
        var cachedScopeProvider = new FlatCacheScopeProvider(_baseScopeProvider, cacheRepository, false, LimboLogs.Instance);
        var cachedWorldState = new WorldState(cachedScopeProvider, LimboLogs.Instance);

        using (cachedWorldState.BeginScope(baseBlock))
        {
            cachedWorldState.GetBalance(TestItem.AddressA).Should().Be(10);
            cachedWorldState.Get(new StorageCell(TestItem.AddressA, 30)).ToHexString().Should().Be("01234567");
        }
    }

    [Test]
    public void TestCacheStates()
    {
        var cachedWorldState = new WorldState(_cachedScopeProvider, LimboLogs.Instance);

        BlockHeader baseBlock;
        using (cachedWorldState.BeginScope(null))
        {
            cachedWorldState.AddToBalanceAndCreateIfNotExists(TestItem.AddressA, 10, London.Instance);
            cachedWorldState.Set(new StorageCell(TestItem.AddressA, 30), Bytes.FromHexString("01234567"));
            cachedWorldState.Commit(London.Instance);
            cachedWorldState.CommitTree(0);

            baseBlock = Build.A.BlockHeader.WithStateRoot(cachedWorldState.StateRoot).TestObject;
        }

        // _cacheRepository.KnownStatesCount.Should().Be(1);
        // _stateDb.Clear();

        using (cachedWorldState.BeginScope(baseBlock))
        {
            cachedWorldState.GetBalance(TestItem.AddressA).Should().Be(10);
            cachedWorldState.Get(new StorageCell(TestItem.AddressA, 30)).ToHexString().Should().Be("01234567");
        }
    }

    [Test]
    public void TestMaxStateInMemory()
    {
        int maxStateInMemory = 10;
        (FlatCacheScopeProvider cachedScopeProvider, FlatCacheRepository cacheRepository, TestMemDb stateDb) = CustomFlatCacheProvider(new FlatCacheRepository.Configuration(
            MaxStateInMemory: maxStateInMemory,
            InlineCompaction: true
        ));
        var cachedWorldState = new WorldState(cachedScopeProvider, LimboLogs.Instance);

        List<BlockHeader?> baseBlocks = new List<BlockHeader?>();

        BlockHeader? baseBlock = null;
        for (int i = 0; i < 20; i++)
        {
            using (cachedWorldState.BeginScope(baseBlock))
            {
                cachedWorldState.AddToBalanceAndCreateIfNotExists(TestItem.AddressA, (UInt256)10, London.Instance);
                cachedWorldState.Set(new StorageCell(TestItem.AddressA, 30), ((UInt256) i * 10 + 10).ToBigEndian());
                cachedWorldState.Commit(London.Instance);
                cachedWorldState.CommitTree(i);

                baseBlock = Build.A.BlockHeader.WithNumber(i).WithStateRoot(cachedWorldState.StateRoot).TestObject;
                baseBlocks.Add(baseBlock);
            }
        }

        // cacheRepository.KnownStatesCount.Should().Be(maxStateInMemory);
    }

    [TestCase(2)]
    [TestCase(2)]
    [TestCase(10)]
    [TestCase(30)]
    [TestCase(127)]
    [TestCase(128)]
    [TestCase(129)]
    [TestCase(256)]
    public void TestCacheMultipleBlock(int blockCount)
    {
        var cachedWorldState = new WorldState(_cachedScopeProvider, LimboLogs.Instance);

        List<BlockHeader?> baseBlocks = new List<BlockHeader?>();

        BlockHeader? baseBlock = null;
        for (int i = 0; i < blockCount; i++)
        {
            using (cachedWorldState.BeginScope(baseBlock))
            {
                cachedWorldState.AddToBalanceAndCreateIfNotExists(TestItem.AddressA, (UInt256)10, London.Instance);
                cachedWorldState.Set(new StorageCell(TestItem.AddressA, 30), ((UInt256) i * 10 + 10).ToBigEndian());
                cachedWorldState.Commit(London.Instance);
                cachedWorldState.CommitTree(i);

                baseBlock = Build.A.BlockHeader.WithNumber(i).WithStateRoot(cachedWorldState.StateRoot).TestObject;
                baseBlocks.Add(baseBlock);
            }
        }

        // _cacheRepository.KnownStatesCount.Should().Be(blockCount);
        // _stateDb.Clear();

        for (int i = blockCount - 1; i >= 0; i--)
        {
            Console.Error.WriteLine($"At block {i}");
            using (cachedWorldState.BeginScope(baseBlocks[i]))
            {
                cachedWorldState.GetBalance(TestItem.AddressA).Should().Be((UInt256)(i * 10 + 10));
                var value = cachedWorldState.Get(new StorageCell(TestItem.AddressA, 30));
                new UInt256(value, true).Should().Be((UInt256) i * 10 + 10);
            }
        }
    }

    [TestCase(2)]
    [TestCase(2)]
    [TestCase(10)]
    [TestCase(11)]
    [TestCase(127)]
    [TestCase(128)]
    [TestCase(129)]
    [TestCase(256)]
    public void TestSelfDestruct(int blockCount)
    {
        var cachedWorldState = new WorldState(_cachedScopeProvider, LimboLogs.Instance);

        List<BlockHeader?> baseBlocks = new List<BlockHeader?>();

        int selfDestructInterval = 10;

        BlockHeader? baseBlock = null;
        for (int i = 0; i < blockCount; i++)
        {
            using (cachedWorldState.BeginScope(baseBlock))
            {
                cachedWorldState.AddToBalanceAndCreateIfNotExists(TestItem.AddressA, (UInt256)10, London.Instance);

                UInt256 currentValue = new UInt256(cachedWorldState.Get(new StorageCell(TestItem.AddressA, 30)), true);
                cachedWorldState.Get(new StorageCell(TestItem.AddressA, 30));
                cachedWorldState.Set(new StorageCell(TestItem.AddressA, 30), (currentValue + 10).ToBigEndian());

                if (i % selfDestructInterval == 0)
                {
                    cachedWorldState.ClearStorage(TestItem.AddressA);
                }

                cachedWorldState.Commit(London.Instance);
                cachedWorldState.CommitTree(i);

                baseBlock = Build.A.BlockHeader.WithNumber(i).WithStateRoot(cachedWorldState.StateRoot).TestObject;
                baseBlocks.Add(baseBlock);
            }
        }

        // _cacheRepository.KnownStatesCount.Should().Be(blockCount);

        for (int i = 0; i < blockCount; i++)
        {
            Console.Error.WriteLine($"at {baseBlocks[i]?.Number.ToString()}");
            using (cachedWorldState.BeginScope(baseBlocks[i]))
            {
                cachedWorldState.GetBalance(TestItem.AddressA).Should().Be((UInt256)(i * 10 + 10));
                var value = cachedWorldState.Get(new StorageCell(TestItem.AddressA, 30));
                int counter = i % selfDestructInterval;
                UInt256 actualValue = new UInt256(value, true);
                actualValue.Should().Be((UInt256) counter * 10);
            }
        }
    }

    // TODO: Can handle branch
    // TODO: Can handle selfdestruct
    // TODO: Check for cycle
}
