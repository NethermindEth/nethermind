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
            MemDb codeDb = new();
            MemDb stateDb = new MemDb();
            NodeStorage nodeStorage = new NodeStorage(stateDb);
            TrieStore trieStore = new(nodeStorage, new MemoryLimit(0.MB()), Persist.EveryBlock, new PruningConfig(), LimboLogs.Instance);
            WorldState stateProvider = new(trieStore, codeDb, LimboLogs.Instance);

            stateProvider.CreateAccount(TestItem.AddressA, 1);
            stateProvider.InsertCode(TestItem.AddressA, new byte[] { 1, 2, 3 }, Istanbul.Instance);

            stateProvider.CreateAccount(TestItem.AddressB, 1);
            stateProvider.InsertCode(TestItem.AddressB, new byte[] { 1, 2, 3, 4 }, Istanbul.Instance);

            for (int i = 0; i < 1000; i++)
            {
                StorageCell storageCell = new(TestItem.AddressA, (UInt256)i);
                stateProvider.Set(storageCell, new byte[] { (byte)i });
            }

            stateProvider.Commit(Istanbul.Instance);

            stateProvider.CommitTree(0);
            stateProvider.CommitTree(1);

            codeDb.Delete(Keccak.Compute(new byte[] { 1, 2, 3, 4 })); // missing code

            // delete some storage
            Hash256 address = new("0x55227dead52ea912e013e7641ccd6b3b174498e55066b0c174a09c8c3cc4bf5e");
            TreePath path = new TreePath(new ValueHash256("0x1800000000000000000000000000000000000000000000000000000000000000"), 2);
            Hash256 storageKey = new("0x345e54154080bfa9e8f20c99d7a0139773926479bc59e5b4f830ad94b6425332");
            nodeStorage.Set(address, path, storageKey, null);

            trieStore.ClearCache();

            TrieStatsCollector statsCollector = new(codeDb, LimboLogs.Instance);
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
