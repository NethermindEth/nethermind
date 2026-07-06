// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Caching;
using Nethermind.Core.Memory;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test
{
    [TestFixture]
    public class MemoryHintManTests
    {
        private const ulong GB = 1000 * 1000 * 1000;
        private const ulong MB = 1000 * 1000;
        private const long AdaptiveMB = 1000 * 1000;

        private IDbConfig _dbConfig;
        private ISyncConfig _syncConfig;
        private IInitConfig _initConfig;
        private ITxPoolConfig _txPoolConfig;
        private INetworkConfig _networkConfig;
        private MemoryHintMan _memoryHintMan;
        private MallocHelper _mallocHelper;

        [SetUp]
        public void Setup()
        {
            _dbConfig = new DbConfig();
            _syncConfig = new SyncConfig();
            _initConfig = new InitConfig();
            _txPoolConfig = new TxPoolConfig();
            _networkConfig = new NetworkConfig();
            _mallocHelper = Substitute.For<MallocHelper>();
            _memoryHintMan = new MemoryHintMan(LimboLogs.Instance, _mallocHelper);
        }

        private void SetMemoryAllowances(uint cpuCount) => _memoryHintMan.SetMemoryAllowances(
                _dbConfig,
                _initConfig,
                _networkConfig,
                _syncConfig,
                _txPoolConfig,
                cpuCount);

        [TestCase(4 * GB, 2u, 4u, 11)]
        [TestCase(4 * GB, 4u, 8u, 11)]
        [TestCase(8 * GB, 1u, 2u, 11)]
        [TestCase(1 * GB, 4u, 8u, 11)]
        [TestCase(512 * MB, 4u, 8u, 10)]
        [TestCase(256 * MB, 6u, 12u, 8)]
        [TestCase(1000 * MB, 12u, 24u, 9)]
        [TestCase(2000 * MB, 12u, 24u, 10)]
        [TestCase(1000 * MB, 12u, 8u, 11)]
        [TestCase(2000 * MB, 12u, 8u, 11)]
        public void Netty_arena_order_is_configured_correctly(ulong memoryHint, uint cpuCount, uint maxArenaCount, int expectedArenaOrder)
        {
            _txPoolConfig.Size = 128;
            _initConfig.DiagnosticMode = DiagnosticMode.MemDb;
            _initConfig.MemoryHint = memoryHint;
            _networkConfig.MaxNettyArenaCount = maxArenaCount;
            SetMemoryAllowances(cpuCount);
            Assert.That(_networkConfig.NettyArenaOrder, Is.EqualTo(expectedArenaOrder));
        }

        [Test]
        public void Db_size_are_computed_correctly(
            [Values(256 * MB, 512 * MB, 1 * GB, 4 * GB, 6 * GB, 16 * GB, 32 * GB, 64 * GB, 128 * GB)]
            ulong memoryHint,
            [Values(1u, 2u, 3u, 4u, 8u, 32u)] uint cpuCount,
            [Values(true, false)] bool fastSync)
        {
            // OK to throw here
            if (memoryHint == 256 * MB)
            {
                _txPoolConfig.Size = 128;
                _initConfig.DiagnosticMode = DiagnosticMode.MemDb;
            }

            _initConfig.MemoryHint = memoryHint;
            SetMemoryAllowances(cpuCount);

            SyncConfig syncConfig = new();
            syncConfig.FastSync = fastSync;

            Assert.That(_memoryHintMan.DbMemory, Is.GreaterThan((memoryHint - 100 * MB) / 2));
            Assert.That(_memoryHintMan.DbMemory, Is.LessThan((memoryHint - 100 * MB) * 9 / 10));
        }

        [TestCase(100 * GB, 16u, -1)]
        [TestCase(100 * GB, 16u, 1)]
        [TestCase(384 * MB, 1u, -1)]
        [TestCase(384 * MB, 1u, 1)]
        public void Will_not_change_non_default_arena_order(ulong memoryHint, uint cpuCount, int differenceFromDefault)
        {
            _initConfig.MemoryHint = memoryHint;
            int manuallyConfiguredArenaOrder = INetworkConfig.DefaultNettyArenaOrder + differenceFromDefault;
            _networkConfig.NettyArenaOrder = manuallyConfiguredArenaOrder;
            SetMemoryAllowances(cpuCount);
            Assert.That(_networkConfig.NettyArenaOrder, Is.EqualTo(manuallyConfiguredArenaOrder));
        }

        [TestCase(4 * GB, 0u)]
        public void Incorrect_input_throws(ulong memoryHint, uint cpuCount)
        {
            _initConfig.MemoryHint = memoryHint;
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SetMemoryAllowances(cpuCount));
        }

        [TestCase(500 * GB)]
        public void Big_value_at_memory_hint(ulong memoryHint)
        {
            _initConfig.MemoryHint = memoryHint;
            SetMemoryAllowances(1);
            Assert.That(_dbConfig.StateDbRowCacheSize, Is.GreaterThan(0));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Big_value_at_memory_hint(bool shouldSetMallocOpts)
        {
            _initConfig.DisableMallocOpts = !shouldSetMallocOpts;
            SetMemoryAllowances(1);

            if (shouldSetMallocOpts)
            {
                _mallocHelper.Received().MallOpt(Arg.Any<MallocHelper.Option>(), Arg.Any<int>());
            }
            else
            {
                _mallocHelper.DidNotReceive().MallOpt(Arg.Any<MallocHelper.Option>(), Arg.Any<int>());
            }
        }

        [Test]
        public void Adaptive_cache_budget_reclaims_capacity_from_least_utilized_cache()
        {
            using AdaptiveCacheManager manager = new(1000 * AdaptiveMB, LimboLogs.Instance);
            TestAdaptiveCache underused = new("underused", 200 * AdaptiveMB, 50 * AdaptiveMB, 50 * AdaptiveMB, 1000 * AdaptiveMB);
            TestAdaptiveCache saturated = new("saturated", 100 * AdaptiveMB, 100 * AdaptiveMB, 50 * AdaptiveMB, 1000 * AdaptiveMB);
            manager.Register(underused);
            manager.Register(saturated);

            manager.Rebalance(650 * AdaptiveMB);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(underused.Capacity, Is.EqualTo(150 * AdaptiveMB));
                Assert.That(saturated.Capacity, Is.EqualTo(100 * AdaptiveMB));
            }
        }

        [Test]
        public void Adaptive_cache_budget_grows_most_constrained_cache_when_memory_is_available()
        {
            using AdaptiveCacheManager manager = new(1000 * AdaptiveMB, LimboLogs.Instance);
            TestAdaptiveCache saturated = new("saturated", 100 * AdaptiveMB, 90 * AdaptiveMB, 25 * AdaptiveMB, 1000 * AdaptiveMB);
            TestAdaptiveCache idle = new("idle", 100 * AdaptiveMB, 10 * AdaptiveMB, 25 * AdaptiveMB, 1000 * AdaptiveMB);
            manager.Register(saturated);
            manager.Register(idle);

            manager.Rebalance(500 * AdaptiveMB);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(saturated.Capacity, Is.EqualTo(125 * AdaptiveMB));
                Assert.That(idle.Capacity, Is.EqualTo(100 * AdaptiveMB));
            }
        }

        [Test]
        public void Adaptive_cache_budget_shrinks_all_caches_under_memory_pressure()
        {
            using AdaptiveCacheManager manager = new(1000 * AdaptiveMB, LimboLogs.Instance);
            TestAdaptiveCache first = new("first", 400 * AdaptiveMB, 400 * AdaptiveMB, 50 * AdaptiveMB, 1000 * AdaptiveMB);
            TestAdaptiveCache second = new("second", 100 * AdaptiveMB, 100 * AdaptiveMB, 50 * AdaptiveMB, 1000 * AdaptiveMB);
            manager.Register(first);
            manager.Register(second);

            manager.Rebalance(900 * AdaptiveMB);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(first.Capacity, Is.EqualTo(300 * AdaptiveMB));
                Assert.That(second.Capacity, Is.EqualTo(75 * AdaptiveMB));
            }
        }

        private sealed class TestAdaptiveCache(
            string name,
            long capacity,
            long usage,
            long minimumCapacity,
            long maximumCapacity) : IAdaptiveCache
        {
            public string Name { get; } = name;
            public long Capacity { get; private set; } = capacity;
            public long Usage { get; } = usage;
            public long MinimumCapacity { get; } = minimumCapacity;
            public long MaximumCapacity { get; } = maximumCapacity;

            public void SetCapacity(long newCapacity) => Capacity = newCapacity;
        }
    }
}
