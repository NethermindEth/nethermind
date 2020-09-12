//  Copyright (c) 2020 Demerzel Solutions Limited
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

using System.Diagnostics;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test
{
    [TestFixture]
    public class PruningScenariosTests
    {
        /* When analyzing the tests below please remember that the way we store accounts is by calculating a hash
           of Address bytes. Address bytes here are created from an UInt256 of the account index.
           Analysis of branch / extension might be more difficult because of the hashing of addresses.*/

        /* TODO: fuzz here with a single seed number */

        public class PruningContext
        {
            private long _blockNumber = 1;
            private IDbProvider _dbProvider;
            private IStateProvider _stateProvider;
            private IStorageProvider _storageProvider;
            private ILogManager _logManager;
            private TrieStore _trieStore;
            private TrieNodeCache _trieNodeCache;
            private IPersistenceStrategy _persistenceStrategy;
            private IPruningStrategy _pruningStrategy;

            [DebuggerStepThrough]
            private PruningContext(IPruningStrategy pruningStrategy, IPersistenceStrategy persistenceStrategy)
            {
                _logManager = new TestLogManager(LogLevel.Trace);
                _dbProvider = new MemDbProvider();
                _trieNodeCache = new TrieNodeCache(_logManager);
                _persistenceStrategy = persistenceStrategy;
                _pruningStrategy = pruningStrategy;
                _trieStore = new TrieStore(
                    _trieNodeCache, _dbProvider.StateDb, _pruningStrategy, _persistenceStrategy, _logManager);
                StateTree stateTree = new StateTree(_trieStore, _logManager);
                _stateProvider = new StateProvider(stateTree, _dbProvider.CodeDb, _logManager);
                _storageProvider = new StorageProvider(_trieStore, _stateProvider, _logManager);
            }


            public static PruningContext ArchiveWithManualPruning
            {
                [DebuggerStepThrough] get => new PruningContext(No.Pruning, Full.Archive);
            }
            
            public static PruningContext SnapshotEveryOtherBlockWithManualPruning
            {
                [DebuggerStepThrough] get => new PruningContext(No.Pruning, new ConstantInterval(2));
            }

            public static PruningContext InMemory
            {
                [DebuggerStepThrough] get => new PruningContext(No.Pruning, No.Persistence);
            }

            public static PruningContext SetupWithPersistenceEveryEightBlocks
            {
                [DebuggerStepThrough] get => new PruningContext(No.Pruning, new ConstantInterval(8));
            }

            public PruningContext CreateAccount(int accountIndex)
            {
                _stateProvider.CreateAccount(Address.FromNumber((UInt256) accountIndex), 1);
                return this;
            }

            public PruningContext PruneOldBlock()
            {
                _trieStore.TryPruningOldBlock();
                return this;
            }

            public PruningContext AddStorage(int accountIndex, int storageKey, int storageValue = 1)
            {
                _storageProvider.Set(
                    new StorageCell(Address.FromNumber((UInt256) accountIndex), (UInt256) storageKey),
                    ((UInt256) storageValue).ToBigEndian());
                return this;
            }

            public PruningContext ReadStorage(int accountIndex, int storageKey)
            {
                StorageCell storageCell =
                    new StorageCell(Address.FromNumber((UInt256) accountIndex), (UInt256) storageKey);
                _storageProvider.Get(storageCell);
                return this;
            }

            public PruningContext Commit()
            {
                _storageProvider.Commit();
                _storageProvider.CommitTrees(_blockNumber);
                _stateProvider.Commit(MuirGlacier.Instance);
                _stateProvider.CommitTree(_blockNumber);
                _blockNumber++;
                return this;
            }
            
            public PruningContext CommitEmptyBlock()
            {
                Commit(); // same, just for better test redability
                return this;
            }

            public PruningContext VerifyPersisted(int i)
            {
                _trieStore.PersistedNodesCount.Should().Be(i);
                return this;
            }

            public PruningContext VerifyDropped(int i)
            {
                _trieStore.DroppedNodesCount.Should().Be(i);
                return this;
            }
        }

        [Test]
        public void Storage_subtree_resolution()
        {
            // Imagine that we have an account 1 with a storage trie
            //     1
            //     B
            //  L1     L2
            // and we persist such trie
            // Then we create an account 2 with a storage trie that is a subtrie of account 1 storage
            //     2
            //     L2  
            // Then we read L2 from account 1 so that B is resolved from persistent storage and L2 is read from cache.
            // When persisting the root everything should be fine.

            PruningContext.SnapshotEveryOtherBlockWithManualPruning
                .CreateAccount(1)
                .AddStorage(1, 1)
                .AddStorage(1, 2)
                .Commit()
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .CreateAccount(2)
                .Commit()
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(7)
                .AddStorage(2, 1)
                .Commit()
                .ReadStorage(1, 1)
                .Commit()
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(9)
                .VerifyDropped(0);
        }

        [Test]
        public void Simple_in_memory_scenario()
        {
            PruningContext.InMemory
                .CreateAccount(1)
                .AddStorage(1, 1)
                .AddStorage(1, 2)
                .Commit()
                .PruneOldBlock()
                .VerifyPersisted(0)
                .VerifyDropped(0);
        }

        [Test]
        public void Single_storage_trie_persistence()
        {
            // here we expect that the account and the storage will be saved into the database
            PruningContext.ArchiveWithManualPruning
                .CreateAccount(1)
                .AddStorage(1, 1)
                .AddStorage(1, 2)
                .Commit()
                .PruneOldBlock()
                .VerifyPersisted(4) // account leaf, storage branch, 2x storage leaf
                .VerifyDropped(0);
        }

        [Test]
        public void Two_accounts_persistence()
        {
            PruningContext.ArchiveWithManualPruning
                .CreateAccount(1)
                .CreateAccount(2)
                .Commit()
                .PruneOldBlock()
                .VerifyPersisted(3) // branch and two leaves 
                .VerifyDropped(0);
        }
    }
}