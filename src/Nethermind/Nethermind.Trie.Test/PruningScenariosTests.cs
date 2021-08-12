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

using System;
using System.Diagnostics;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.Trie.Test.Pruning;
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
            private IStateReader _stateReader;
            private IStorageProvider _storageProvider;
            private ILogManager _logManager;
            private ILogger _logger;
            private TrieStore _trieStore;
            private IPersistenceStrategy _persistenceStrategy;
            private IPruningStrategy _pruningStrategy;

            [DebuggerStepThrough]
            private PruningContext(IPruningStrategy pruningStrategy, IPersistenceStrategy persistenceStrategy)
            {
                _logManager = new TestLogManager(LogLevel.Trace);
                _logger = _logManager.GetClassLogger();
                _dbProvider = TestMemDbProvider.Init();
                _persistenceStrategy = persistenceStrategy;
                _pruningStrategy = pruningStrategy;
                _trieStore = new TrieStore(_dbProvider.StateDb, _pruningStrategy, _persistenceStrategy, _logManager);
                _stateProvider = new StateProvider(_trieStore, _dbProvider.CodeDb, _logManager);
                _storageProvider = new StorageProvider(_trieStore, _stateProvider, _logManager);
                _stateReader = new StateReader(_trieStore, _dbProvider.CodeDb, _logManager);
            }


            public static PruningContext ArchiveWithManualPruning
            {
                [DebuggerStepThrough] get => new(new TestPruningStrategy(true), Persist.EveryBlock);
            }

            public static PruningContext SnapshotEveryOtherBlockWithManualPruning
            {
                [DebuggerStepThrough] get => new(new TestPruningStrategy(true), new ConstantInterval(2));
            }

            public static PruningContext InMemory
            {
                [DebuggerStepThrough] get => new(new TestPruningStrategy(true), No.Persistence);
            }

            public static PruningContext SetupWithPersistenceEveryEightBlocks
            {
                [DebuggerStepThrough] get => new(new TestPruningStrategy(true), new ConstantInterval(8));
            }

            public PruningContext CreateAccount(int accountIndex)
            {
                _stateProvider.CreateAccount(Address.FromNumber((UInt256) accountIndex), 1);
                return this;
            }

            public PruningContext PruneOldBlock()
            {
                return this;
            }

            public PruningContext SetStorage(int accountIndex, int storageKey, int storageValue = 1)
            {
                _storageProvider.Set(
                    new StorageCell(Address.FromNumber((UInt256) accountIndex), (UInt256) storageKey),
                    ((UInt256) storageValue).ToBigEndian());
                return this;
            }

            public PruningContext DeleteStorage(int accountIndex, int storageKey)
            {
                _logger.Info($"DELETE STORAGE {accountIndex}.{storageKey}");
                SetStorage(accountIndex, storageKey, 0);
                return this;
            }

            public PruningContext ReadStorage(int accountIndex, int storageKey)
            {
                _logger.Info($"READ   STORAGE {accountIndex}.{storageKey}");
                StorageCell storageCell =
                    new(Address.FromNumber((UInt256) accountIndex), (UInt256) storageKey);
                _storageProvider.Get(storageCell);
                return this;
            }

            public PruningContext ReadAccount(int accountIndex)
            {
                _logger.Info($"READ   ACCOUNT {accountIndex}");
                _stateProvider.GetAccount(Address.FromNumber((UInt256)accountIndex));
                return this;
            }
            
            public PruningContext ReadAccountViaStateReader(int accountIndex)
            {
                _logger.Info($"READ   ACCOUNT {accountIndex}");
                _stateReader.GetAccount(_stateProvider.StateRoot, Address.FromNumber((UInt256)accountIndex));
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
                Commit(); // same, just for better test readability
                return this;
            }

            public PruningContext VerifyPersisted(int i)
            {
                _trieStore.PersistedNodesCount.Should().Be(i);
                return this;
            }
            
            public PruningContext VerifyCached(int i)
            {
                GC.Collect();
                GC.WaitForFullGCComplete(1000);
                GC.WaitForPendingFinalizers();
                _trieStore.Prune();
                _trieStore.CachedNodesCount.Should().Be(i);
                return this;
            }

            public PruningContext DumpCache()
            {
                _trieStore.Dump();
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
            // Then we create an account 2 with a same storage trie
            //     2
            //     B
            //  L1     L2  
            // Then we read L2 from account 1 so that B is resolved from cache.
            // When persisting account 2, storage should not get persisted again.

            PruningContext.SnapshotEveryOtherBlockWithManualPruning
                .CreateAccount(1)
                .SetStorage(1, 1)
                .SetStorage(1, 2)
                .Commit()
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(4)
                .CreateAccount(2)
                .Commit()
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(7) // not the length of the leaf path has changed
                .DumpCache()
                .SetStorage(2, 1)
                .SetStorage(2, 2)
                .Commit()
                .ReadStorage(1, 1)
                .Commit()
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(9);
        }
        
        [Test]
        public void Aura_scenario_asking_about_a_not_yet_persisted_root()
        {
            // AuRa can make calls asking about the state in one of the previous blocks while processing / finalizing
            // if the state root is neither in the cache nor in the database (but only held as a commit root in the trie store)
            // then it would throw an exception
            PruningContext.SnapshotEveryOtherBlockWithManualPruning
                .CreateAccount(1)
                .Commit()
                .ReadAccountViaStateReader(1)
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(1);
        }

        [Test]
        public void Delete_storage()
        {
            PruningContext.SnapshotEveryOtherBlockWithManualPruning
                .CreateAccount(1)
                .SetStorage(1, 1)
                .SetStorage(1, 2)
                .Commit()
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(4)
                .DeleteStorage(1, 1)
                .DeleteStorage(1, 2)
                .Commit()
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(5);
        }
        
        [Test]
        public void Do_not_delete_storage_before_persisting()
        {
            PruningContext.SnapshotEveryOtherBlockWithManualPruning
                .CreateAccount(1)
                .SetStorage(1, 1)
                .SetStorage(1, 2)
                .Commit()
                .DeleteStorage(1, 1)
                .DeleteStorage(1, 2)
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(1)
                .VerifyCached(5);
        }
        
        [Test]
        public void Two_accounts_adding_shared_storage_in_same_block()
        {
            PruningContext.SnapshotEveryOtherBlockWithManualPruning
                .CreateAccount(1)
                .SetStorage(1, 1)
                .SetStorage(1, 2)
                .CreateAccount(2)
                .SetStorage(2, 1)
                .SetStorage(2, 2)
                .Commit()
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(6)
                .VerifyCached(6);
        }
        
        [Test]
        public void Two_accounts_adding_shared_storage_in_same_block_then_one_account_storage_is_cleared()
        {
            PruningContext.SnapshotEveryOtherBlockWithManualPruning
                .CreateAccount(1)
                .SetStorage(1, 1)
                .SetStorage(1, 2)
                .CreateAccount(2)
                .SetStorage(2, 1)
                .SetStorage(2, 2)
                .Commit()
                .DeleteStorage(2, 1)
                .DeleteStorage(2, 2)
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(6)
                .VerifyCached(8);
        }
        
        [Test]
        public void Delete_and_revive()
        {
            PruningContext.SnapshotEveryOtherBlockWithManualPruning
                .CreateAccount(1)
                .SetStorage(1, 1)
                .SetStorage(1, 2)
                .Commit()
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(4)
                .DeleteStorage(1, 1)
                .DeleteStorage(1, 2)
                .SetStorage(1, 1)
                .SetStorage(1, 2)
                .Commit()
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(4);
        }
        
        [Test]
        public void Update_storage()
        {
            PruningContext.SnapshotEveryOtherBlockWithManualPruning
                .CreateAccount(1)
                .SetStorage(1, 1)
                .SetStorage(1, 2)
                .Commit()
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(4)
                .SetStorage(1, 1, 1000)
                .Commit()
                .CommitEmptyBlock()
                .PruneOldBlock()
                .PruneOldBlock()
                .VerifyPersisted(7);
        }

        [Test]
        public void Simple_in_memory_scenario()
        {
            PruningContext.InMemory
                .CreateAccount(1)
                .SetStorage(1, 1)
                .SetStorage(1, 2)
                .Commit()
                .PruneOldBlock()
                .VerifyPersisted(0);
        }

        [Test]
        public void Single_storage_trie_persistence()
        {
            // here we expect that the account and the storage will be saved into the database
            PruningContext.ArchiveWithManualPruning
                .CreateAccount(1)
                .SetStorage(1, 1)
                .SetStorage(1, 2)
                .Commit()
                .PruneOldBlock()
                .VerifyPersisted(4);  // account leaf, storage branch, 2x storage leaf
        }

        [Test]
        public void Two_accounts_persistence()
        {
            PruningContext.ArchiveWithManualPruning
                .CreateAccount(1)
                .CreateAccount(2)
                .Commit()
                .PruneOldBlock()
                .VerifyPersisted(3); // branch and two leaves 
        }
    }
}
