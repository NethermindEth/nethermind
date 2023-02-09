// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class StatsCollectorTests
    {
        [Test]
        public void Can_collect_stats([Values(false, true)] bool parallel)
        {
            MemDb memDb = new();
            IDb stateDb = memDb;
            TrieStore trieStore = new(stateDb, new MemoryLimit(0.MB()), Persist.EveryBlock, LimboLogs.Instance);
            StateProvider stateProvider = new(trieStore, stateDb, LimboLogs.Instance);
            StorageProvider storageProvider = new(trieStore, stateProvider, LimboLogs.Instance);

            stateProvider.CreateAccount(TestItem.AddressA, 1);
            Keccak codeHash = stateProvider.UpdateCode(new byte[] { 1, 2, 3 });
            stateProvider.UpdateCodeHash(TestItem.AddressA, codeHash, Istanbul.Instance);

            stateProvider.CreateAccount(TestItem.AddressB, 1);
            Keccak codeHash2 = stateProvider.UpdateCode(new byte[] { 1, 2, 3, 4 });
            stateProvider.UpdateCodeHash(TestItem.AddressB, codeHash2, Istanbul.Instance);

            for (int i = 0; i < 1000; i++)
            {
                StorageCell storageCell = new(TestItem.AddressA, (UInt256)i);
                storageProvider.Set(storageCell, new byte[] { (byte)i });
            }

            storageProvider.Commit();
            stateProvider.Commit(Istanbul.Instance);

            storageProvider.CommitTrees(0);
            stateProvider.CommitTree(0);
            storageProvider.CommitTrees(1);
            stateProvider.CommitTree(1);

            memDb.Delete(codeHash2); // missing code
            Keccak storageKey = new("0x345e54154080bfa9e8f20c99d7a0139773926479bc59e5b4f830ad94b6425332");
            memDb.Delete(storageKey); // deletes some storage
            trieStore.ClearCache();

            TrieStatsCollector statsCollector = new(stateDb, LimboLogs.Instance);
            VisitingOptions visitingOptions = new VisitingOptions()
            {
                MaxDegreeOfParallelism = parallel ? 0 : 1
            };

            stateProvider.Accept(statsCollector, stateProvider.StateRoot, visitingOptions);
            var stats = statsCollector.Stats;

            stats.CodeCount.Should().Be(1);
            stats.MissingCode.Should().Be(1);

            stats.NodesCount.Should().Be(1348);

            stats.StateBranchCount.Should().Be(1);
            stats.StateExtensionCount.Should().Be(1);
            stats.AccountCount.Should().Be(2);

            stats.StorageCount.Should().Be(1343);
            stats.StorageBranchCount.Should().Be(337);
            stats.StorageExtensionCount.Should().Be(12);
            stats.StorageLeafCount.Should().Be(994);
            stats.MissingStorage.Should().Be(1);
        }
    }
}
