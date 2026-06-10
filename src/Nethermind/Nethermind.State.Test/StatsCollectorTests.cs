// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;
using System;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class StatsCollectorTests
    {
        [Test]
        public void Can_collect_stats([Values(false, true)] bool parallel)
        {
            MemDb codeDb = new();
            MemDb stateDb = new();
            NodeStorage nodeStorage = new(stateDb);
            TestRawTrieStore trieStore = new(nodeStorage);
            WorldState stateProvider = new(new TrieStoreScopeProvider(trieStore, codeDb, LimboLogs.Instance), LimboLogs.Instance);
            StateReader stateReader = new(trieStore, codeDb, LimboLogs.Instance);
            BlockHeader baseBlock;

            using (IDisposable _ = stateProvider.BeginScope(IWorldState.PreGenesis))
            {
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
                baseBlock = Build.A.BlockHeader.WithNumber(1).WithStateRoot(stateProvider.StateRoot).TestObject;
            }

            codeDb.Delete(Keccak.Compute(new byte[] { 1, 2, 3, 4 })); // missing code

            // delete some storage
            Hash256 address = new("0x55227dead52ea912e013e7641ccd6b3b174498e55066b0c174a09c8c3cc4bf5e");
            TreePath path = new(new ValueHash256("0x1800000000000000000000000000000000000000000000000000000000000000"), 2);
            Hash256 storageKey = new("0x345e54154080bfa9e8f20c99d7a0139773926479bc59e5b4f830ad94b6425332");
            nodeStorage.Set(address, path, storageKey, null);

            TrieStatsCollector statsCollector = new(codeDb, LimboLogs.Instance);
            VisitingOptions visitingOptions = new()
            {
                MaxDegreeOfParallelism = parallel ? 0 : 1
            };

            stateReader.RunTreeVisitor(statsCollector, baseBlock, visitingOptions);
            TrieStats stats = statsCollector.Stats;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.CodeCount, Is.EqualTo(1));
                Assert.That(stats.MissingCode, Is.EqualTo(1));

                Assert.That(stats.NodesCount, Is.EqualTo(1348));

                Assert.That(stats.StateBranchCount, Is.EqualTo(1));
                Assert.That(stats.StateExtensionCount, Is.EqualTo(1));
                Assert.That(stats.AccountCount, Is.EqualTo(2));

                Assert.That(stats.StorageCount, Is.EqualTo(1343));
                Assert.That(stats.StorageBranchCount, Is.EqualTo(337));
                Assert.That(stats.StorageExtensionCount, Is.EqualTo(12));
                Assert.That(stats.StorageLeafCount, Is.EqualTo(994));
                Assert.That(stats.MissingStorage, Is.EqualTo(1));
            }
        }
    }
}
