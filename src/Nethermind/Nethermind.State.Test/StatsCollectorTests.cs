//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
            MemDb memDb = new MemDb();
            IDb stateDb = memDb;
            TrieStore trieStore = new TrieStore(stateDb, new MemoryLimit(0.MB()), Persist.EveryBlock, LimboLogs.Instance);
            StateProvider stateProvider = new StateProvider(trieStore, stateDb, LimboLogs.Instance);
            StorageProvider storageProvider = new StorageProvider(trieStore, stateProvider, LimboLogs.Instance);

            stateProvider.CreateAccount(TestItem.AddressA, 1);
            Keccak codeHash = stateProvider.UpdateCode(new byte[] {1, 2, 3});
            stateProvider.UpdateCodeHash(TestItem.AddressA, codeHash, Istanbul.Instance);
            
            stateProvider.CreateAccount(TestItem.AddressB, 1);
            Keccak codeHash2 = stateProvider.UpdateCode(new byte[] {1, 2, 3, 4});
            stateProvider.UpdateCodeHash(TestItem.AddressB, codeHash2, Istanbul.Instance);

            for (int i = 0; i < 1000; i++)
            {
                StorageCell storageCell = new StorageCell(TestItem.AddressA, (UInt256)i);
                storageProvider.Set(storageCell, new byte[] {(byte)i});    
            }

            storageProvider.Commit();
            stateProvider.Commit(Istanbul.Instance);

            storageProvider.CommitTrees(0);
            stateProvider.CommitTree(0);
            storageProvider.CommitTrees(1);
            stateProvider.CommitTree(1);

            memDb.Delete(codeHash2); // missing code
            Keccak storageKey = new Keccak("0x345e54154080bfa9e8f20c99d7a0139773926479bc59e5b4f830ad94b6425332");
            memDb.Delete(storageKey); // deletes some storage
            trieStore.ClearCache();

            TrieStatsCollector statsCollector = new TrieStatsCollector(stateDb, LimboLogs.Instance)
            {
                SupportsParallelVisits = parallel
            };
            stateProvider.Accept(statsCollector, stateProvider.StateRoot);
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
