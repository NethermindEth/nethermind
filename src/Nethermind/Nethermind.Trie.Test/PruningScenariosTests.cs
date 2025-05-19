// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
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
            private readonly Dictionary<string, (long blockNumber, Hash256 rootHash)> _branchingPoints = new();
            private readonly IDbProvider _dbProvider;
            private IWorldState _stateProvider;
            private IStateReader _stateReader;
            private readonly ILogManager _logManager;
            private readonly ILogger _logger;
            private TrieStore _trieStore;
            private readonly IPersistenceStrategy _persistenceStrategy;
            private readonly TestPruningStrategy _pruningStrategy;
            private readonly IPruningConfig _pruningConfig;

            [DebuggerStepThrough]
            private PruningContext(TestPruningStrategy pruningStrategy, IPersistenceStrategy persistenceStrategy, IPruningConfig? pruningConfig = null)
            {
                _logManager = LimboLogs.Instance;
                //new TestLogManager(LogLevel.Trace);
                _logger = _logManager.GetClassLogger();
                _dbProvider = TestMemDbProvider.Init();
                _persistenceStrategy = persistenceStrategy;
                _pruningStrategy = pruningStrategy;

                _pruningConfig = pruningConfig ?? new PruningConfig() { TrackPastKeys = false };
                _trieStore = TestTrieStoreFactory.Build(_dbProvider.StateDb, _pruningStrategy, _persistenceStrategy, _pruningConfig, _logManager);
                _stateProvider = new WorldState(_trieStore, _dbProvider.CodeDb, _logManager);
                _stateReader = new StateReader(_trieStore, _dbProvider.CodeDb, _logManager);
            }


            public static PruningContext ArchiveWithManualPruning
            {
                [DebuggerStepThrough]
                get => new(new TestPruningStrategy(true), Persist.EveryBlock);
            }

            public static PruningContext SnapshotEveryOtherBlockWithManualPruning
            {
                [DebuggerStepThrough]
                get => new(new TestPruningStrategy(true), new ConstantInterval(2));
            }

            public static PruningContext InMemory
            {
                [DebuggerStepThrough]
                get => new(new TestPruningStrategy(true), No.Persistence);
            }

            public static PruningContext InMemoryWithPastKeyTracking
            {
                [DebuggerStepThrough]
                get => new(new TestPruningStrategy(true), No.Persistence, new PruningConfig()
                {
                    TrackPastKeys = true,
                });
            }

            public static PruningContext InMemoryAlwaysPrune
            {
                [DebuggerStepThrough]
                get => new(new TestPruningStrategy(true, true), No.Persistence, new PruningConfig()
                {
                    TrackPastKeys = true,
                });
            }

            public static PruningContext SetupWithPersistenceEveryEightBlocks
            {
                [DebuggerStepThrough]
                get => new(new TestPruningStrategy(true), new ConstantInterval(8));
            }

            public Hash256 CurrentStateRoot => _stateProvider.StateRoot;
            public long TotalMemoryUsage => _trieStore.MemoryUsedByDirtyCache;

            public void VerifyNodeInCache(Hash256 root, bool hasStateRoot)
            {
                _trieStore.IsNodeCached(null, TreePath.Empty, root).Should().Be(hasStateRoot);
            }

            public PruningContext CreateAccount(int accountIndex)
            {
                _stateProvider.CreateAccount(Address.FromNumber((UInt256)accountIndex), 1);
                return this;
            }

            public PruningContext SetAccountBalance(int accountIndex, UInt256 balance)
            {
                _stateProvider.CreateAccount(Address.FromNumber((UInt256)accountIndex), balance);
                return this;
            }

            public PruningContext SetManyAccountWithSameBalance(int startNum, int numOfAccount, UInt256 balance)
            {
                for (int i = 0; i < numOfAccount; i++)
                {
                    this.SetAccountBalance(startNum + i, balance);
                }
                return this;
            }

            public PruningContext WithMaxDepth(int maxDepth)
            {
                _pruningConfig.PruningBoundary = maxDepth;
                return new PruningContext(_pruningStrategy, _persistenceStrategy, _pruningConfig);
            }

            public PruningContext PruneOldBlock()
            {
                return this;
            }

            public PruningContext TurnOnPrune()
            {
                _pruningStrategy.ShouldPruneEnabled = true;
                _pruningStrategy.ShouldPrunePersistedEnabled = true;
                return this;
            }

            public PruningContext TurnOffAlwaysPrunePersistedNode()
            {
                _pruningStrategy.ShouldPrunePersistedEnabled = false;
                return this;
            }

            public PruningContext SetPruningMemoryLimit(int? memoryLimit)
            {
                _pruningStrategy.WithMemoryLimit = memoryLimit;
                return this;
            }

            public PruningContext SetStorage(int accountIndex, int storageKey, int storageValue = 1)
            {
                _stateProvider.Set(
                    new StorageCell(Address.FromNumber((UInt256)accountIndex), (UInt256)storageKey),
                    ((UInt256)storageValue).ToBigEndian());
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
                    new(Address.FromNumber((UInt256)accountIndex), (UInt256)storageKey);
                _stateProvider.Get(storageCell);
                return this;
            }

            public PruningContext ReadAccount(int accountIndex)
            {
                _logger.Info($"READ   ACCOUNT {accountIndex}");
                _stateProvider.TryGetAccount(Address.FromNumber((UInt256)accountIndex), out _);
                return this;
            }

            public PruningContext ReadAccountViaStateReader(int accountIndex)
            {
                _logger.Info($"READ   ACCOUNT {accountIndex}");
                _stateReader.TryGetAccount(_stateProvider.StateRoot, Address.FromNumber((UInt256)accountIndex), out _);
                return this;
            }

            public PruningContext CommitWithRandomChange()
            {
                SetAccountBalance(Random.Shared.Next(), (UInt256)Random.Shared.Next());
                Commit();
                return this;
            }

            public PruningContext Commit()
            {
                _stateProvider.Commit(MuirGlacier.Instance);
                _stateProvider.CommitTree(_blockNumber);
                _blockNumber++;

                // This causes the root node to be reloaded instead of keeping old one
                // The root hash will now be unresolved, which mean it will need to reload from trie store.
                // `BlockProcessor.InitBranch` does this.
                _stateProvider.Reset();
                _stateProvider.StateRoot = _stateProvider.StateRoot;
                return this;
            }

            public PruningContext DisposeAndRecreate()
            {
                _trieStore.Dispose();
                _trieStore = TestTrieStoreFactory.Build(_dbProvider.StateDb, _pruningStrategy, _persistenceStrategy, _logManager);
                _stateProvider = new WorldState(_trieStore, _dbProvider.CodeDb, _logManager);
                _stateReader = new StateReader(_trieStore, _dbProvider.CodeDb, _logManager);
                return this;
            }

            public PruningContext WaitForPruning()
            {
                _trieStore.WaitForPruning();
                _trieStore.Prune();
                _trieStore.WaitForPruning();
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

            public PruningContext VerifyAccountBalance(int account, int balance)
            {
                _stateProvider.GetBalance(Address.FromNumber((UInt256)account))
                    .Should().BeEquivalentTo((UInt256)balance);
                return this;
            }

            public PruningContext VerifyStorageValue(int account, UInt256 index, int value)
            {
                _stateProvider.Get(new StorageCell(Address.FromNumber((UInt256)account), index)).ToArray()
                    .Should().BeEquivalentTo(((UInt256)value).ToBigEndian());
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

            public PruningContext AssertThatCachedNodeCountIs(long cachedNodeCount)
            {
                _trieStore.CachedNodesCount.Should().Be(cachedNodeCount);
                return this;
            }

            public PruningContext AssertThatCachedNodeCountMoreThan(long cachedNodeCount)
            {
                _trieStore.CachedNodesCount.Should().BeGreaterThan(cachedNodeCount);
                return this;
            }

            public PruningContext AssertThatDirtyNodeCountIs(long dirtyNodeCount)
            {
                _trieStore.DirtyCachedNodesCount.Should().Be(dirtyNodeCount);
                return this;
            }

            public PruningContext DumpCache()
            {
                _trieStore.Dump();
                return this;
            }

            public PruningContext SaveBranchingPoint(string name)
            {
                _branchingPoints[name] = (_blockNumber, _stateProvider.StateRoot);
                return this;
            }

            public PruningContext RestoreBranchingPoint(string name)
            {
                (long blockNumber, Hash256 rootHash) branchPoint = _branchingPoints[name];
                _blockNumber = branchPoint.blockNumber;
                Hash256 rootHash = branchPoint.rootHash;
                _stateProvider.Reset();
                _stateProvider.StateRoot = rootHash;
                return this;
            }

            public PruningContext WithPersistedMemoryLimit(long persistedMemoryLimit)
            {
                _pruningStrategy.WithPersistedMemoryLimit = persistedMemoryLimit;
                return this;
            }

            public PruningContext WithPrunePersistedNodeParameter(long minimumTarget, double portion)
            {
                _pruningConfig.PrunePersistedNodeMinimumTarget = minimumTarget;
                _pruningConfig.PrunePersistedNodePortion = portion;

                return new PruningContext(_pruningStrategy, _persistenceStrategy, _pruningConfig);
            }

            public void AssertThatTotalMemoryUsedIs(long memoryUsage)
            {
                _trieStore.MemoryUsedByDirtyCache.Should().Be(memoryUsage);
            }

            public void AssertThatTotalMemoryUsedIsNoLessThan(long memoryUsage)
            {
                _trieStore.MemoryUsedByDirtyCache.Should().BeGreaterThan(memoryUsage);
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
            // When persisting account 2, storage should get persisted again.

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
                .VerifyPersisted(12);
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
        public void Two_accounts_adding_same_storage_in_same_block()
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
                .VerifyPersisted(9)
                .VerifyCached(9);
        }

        [Test]
        public void Two_accounts_adding_same_storage_in_same_block_then_one_account_storage_is_cleared()
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
                .VerifyCached(11);
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

        [Test]
        public void Persist_alternate_commitset()
        {
            PruningContext.InMemory
                .WithMaxDepth(3)
                .SetAccountBalance(1, 100)
                .Commit()
                .SetAccountBalance(2, 10)
                .Commit()

                .SaveBranchingPoint("revert_main")
                .CreateAccount(3)
                .SetStorage(3, 1, 999)
                .Commit()
                .SaveBranchingPoint("main")

                    // We need this to get persisted
                    // Storage is not set here, but commit set will commit this instead of previous block 3
                    .RestoreBranchingPoint("revert_main")
                    .SetManyAccountWithSameBalance(100, 20, 1)
                    .Commit()
                    .RestoreBranchingPoint("main")

                .Commit()
                .Commit()
                .Commit()

                .SetPruningMemoryLimit(10000)
                // First commit it should prune and persist alternate block 3, memory usage should go down from 16k to 1.2k
                .Commit()

                .SetManyAccountWithSameBalance(100, 2, 1)
                .Commit()
                .VerifyStorageValue(3, 1, 999)
                .SetManyAccountWithSameBalance(100, 2, 2)
                .Commit()
                .VerifyStorageValue(3, 1, 999)
                .SetManyAccountWithSameBalance(100, 2, 3)
                .Commit()
                .VerifyStorageValue(3, 1, 999)
                .SetManyAccountWithSameBalance(100, 2, 4)
                .Commit()
                .VerifyStorageValue(3, 1, 999)
                .SetManyAccountWithSameBalance(100, 2, 5)
                .Commit()
                .VerifyStorageValue(3, 1, 999)
                .SetManyAccountWithSameBalance(100, 2, 6)
                .Commit()
                .VerifyStorageValue(3, 1, 999)
                .SetManyAccountWithSameBalance(100, 2, 7)
                .Commit()
                .VerifyStorageValue(3, 1, 999)
                .SetManyAccountWithSameBalance(100, 2, 8)
                .Commit()
                .VerifyStorageValue(3, 1, 999)
                .SetManyAccountWithSameBalance(100, 2, 9)
                .Commit()

                // Storage root actually never got pruned even-though another parallel branch get persisted. This
                // is because the condition `LastSeen < LastPersistedBlock` never turn to true.
                .VerifyStorageValue(3, 1, 999);
        }

        [Test]
        public void Persist_alternate_branch_commitset_of_length_2()
        {
            PruningContext.InMemory
                .WithMaxDepth(3)
                .SetAccountBalance(1, 100)
                .Commit()
                .SetAccountBalance(2, 10)
                .Commit()

                .SaveBranchingPoint("revert_main")

                // Block 3
                .CreateAccount(3)
                .SetStorage(3, 1, 999)
                .Commit()

                // Block 4
                .Commit()
                .SaveBranchingPoint("main")

                    // Block 3 - 2
                    .RestoreBranchingPoint("revert_main")
                    .Commit()

                    // Block 4 - 2
                    .SetManyAccountWithSameBalance(100, 20, 1)
                    .Commit()
                    .RestoreBranchingPoint("main")

                .Commit()
                .Commit()
                .Commit()

                .TurnOnPrune()
                .Commit()

                .VerifyStorageValue(3, 1, 999);
        }

        [Test]
        public void Should_persist_all_block_of_same_level_on_dispose()
        {
            PruningContext.InMemory
                .WithMaxDepth(3)
                .SetAccountBalance(1, 100)
                .Commit()
                .SetAccountBalance(2, 10)
                .CreateAccount(3)
                .Commit()

                .SaveBranchingPoint("revert_main")

                .SetStorage(3, 1, 1)
                .Commit()
                .SaveBranchingPoint("branch_1")

                .RestoreBranchingPoint("revert_main")
                .SetStorage(3, 1, 2)
                .Commit()
                .SaveBranchingPoint("branch_2")

                .RestoreBranchingPoint("revert_main")
                .SetStorage(3, 1, 3)
                .Commit()
                .SaveBranchingPoint("branch_3")

                .RestoreBranchingPoint("branch_1")
                .Commit()
                .Commit()
                .Commit()
                .Commit()

                // The `TrieStoreBoundaryWatcher` only reports the block number, but previously TrieStore only persist one of the
                // multiple possible block of the same number. So if the persisted block is not the same as main,
                // you'll get trie exception on restart.
                .DisposeAndRecreate()

                // This should pass because the last committed branch is branch 3.
                .RestoreBranchingPoint("branch_3")
                .VerifyStorageValue(3, 1, 3)

                // Previously this does not.
                .RestoreBranchingPoint("branch_1")
                .VerifyStorageValue(3, 1, 1)
                .RestoreBranchingPoint("branch_2")
                .VerifyStorageValue(3, 1, 2);
        }

        [Test]
        public void Persist_with_2_alternate_branch_consecutive_of_each_other()
        {
            PruningContext.InMemory
                .WithMaxDepth(3)
                .SetAccountBalance(1, 100)
                .Commit()
                .SetAccountBalance(2, 10)
                .Commit()

                .SaveBranchingPoint("revert_main")

                // Block 3
                .CreateAccount(3)
                .SetStorage(3, 1, 999)
                .Commit()
                .SaveBranchingPoint("main")

                    // Block 3 - 2
                    .RestoreBranchingPoint("revert_main")
                    .Commit()

                    .RestoreBranchingPoint("main")

                // Block 4
                .SaveBranchingPoint("revert_main")
                .Commit()
                .SaveBranchingPoint("main")

                    .RestoreBranchingPoint("revert_main") // Go back to block 3
                                                          // Block 4 - 2
                    .SetStorage(3, 1, 1)
                    .Commit()
                    .RestoreBranchingPoint("main") // Go back to block 4 on main

                .TurnOnPrune()
                .Commit()
                .Commit()
                .Commit()
                .Commit()

                .VerifyStorageValue(3, 1, 999);
        }

        [Test]
        public void StorageRoot_reset_at_lower_level()
        {
            PruningContext.InMemoryAlwaysPrune
                .WithMaxDepth(3)
                .SetAccountBalance(1, 100)
                .SetAccountBalance(2, 100)
                .Commit()
                .SetAccountBalance(1, 100)
                .SetAccountBalance(1, 101)
                .Commit()
                .SaveBranchingPoint("revert_main")

                .SetAccountBalance(1, 102)
                .Commit()
                .SetAccountBalance(1, 103)
                .Commit()
                .CreateAccount(3)
                .SetStorage(3, 1, 999)
                .Commit()
                .SaveBranchingPoint("main")

                // The storage root will now get reset at a lower LastSeen
                .RestoreBranchingPoint("revert_main")
                .CreateAccount(3)
                .SetStorage(3, 1, 999)
                .Commit()

                .RestoreBranchingPoint("main")
                .VerifyStorageValue(3, 1, 999)
                .SetAccountBalance(1, 105)
                .Commit()
                .SetAccountBalance(1, 106)
                .Commit()
                .SetAccountBalance(1, 107)
                .Commit()
                .SetAccountBalance(1, 108)
                .Commit()

                .VerifyStorageValue(3, 1, 999);
        }

        [Test]
        public void StateRoot_reset_at_lower_level_and_accessed_at_just_the_right_time()
        {
            PruningContext.InMemory
                .WithMaxDepth(2)
                .SetAccountBalance(1, 100)
                .SetAccountBalance(2, 100)
                .Commit()
                .SaveBranchingPoint("revert_main")

                .SetAccountBalance(1, 10)
                .SetAccountBalance(2, 100)
                .Commit()
                .SetAccountBalance(2, 101)
                .Commit()
                .SetAccountBalance(3, 101)
                .Commit()
                .SaveBranchingPoint("main")

                // This will result in the same state root, but it's `LastSeen` reduced.
                .RestoreBranchingPoint("revert_main")
                .SetAccountBalance(1, 10)
                .SetAccountBalance(2, 101)
                .SetAccountBalance(3, 101)
                .Commit()

                .RestoreBranchingPoint("main")
                .TurnOnPrune()

                // Exactly 2 commit
                .Commit()
                .Commit()

                .VerifyAccountBalance(1, 10)
                .VerifyAccountBalance(2, 101)
                .VerifyAccountBalance(3, 101);
        }

        [TestCase(10)]
        [TestCase(64)]
        [TestCase(100)]
        public void Keep_OnlySomeDepth(int maxDepth)
        {
            PruningContext ctx = PruningContext.InMemory
                .WithMaxDepth(maxDepth)
                .TurnOnPrune();

            using ArrayPoolList<Hash256> stateRoots = new ArrayPoolList<Hash256>(256);
            for (int i = 0; i < 256; i++)
            {
                ctx
                    .SetAccountBalance(0, (UInt256)i)
                    .Commit();
                stateRoots.Add(ctx.CurrentStateRoot);
            }

            ctx.WaitForPruning();

            for (int i = 0; i < 256; i++)
            {
                ctx.VerifyNodeInCache(stateRoots[i], i >= 255 - maxDepth - 1);
            }
        }

        [Test]
        public void When_Reorg_OldValueIsNotRemoved()
        {
            Reorganization.MaxDepth = 2;

            PruningContext.InMemoryAlwaysPrune
                .SetAccountBalance(1, 100)
                .SetAccountBalance(2, 100)
                .Commit()

                .SetAccountBalance(3, 100)
                .SetAccountBalance(4, 100)
                .Commit()

                .SaveBranchingPoint("revert_main")

                .SetAccountBalance(4, 200)
                .Commit()

                .RestoreBranchingPoint("revert_main")

                .Commit()
                .Commit()
                .Commit()
                .Commit()

                .VerifyAccountBalance(4, 100);
        }

        [Test]
        public void Keep_PersistedNode_EvenAfterPersist()
        {
            PruningContext ctx = PruningContext.InMemory
                .WithMaxDepth(1)
                .WithPersistedMemoryLimit(100.MiB())
                .TurnOnPrune()
                .TurnOffAlwaysPrunePersistedNode();

            for (int i = 0; i < 256; i++)
            {
                ctx
                    .SetAccountBalance(i, (UInt256)i)
                    .Commit()
                    .WaitForPruning();
            }

            ctx
                .AssertThatDirtyNodeCountIs(9)
                .AssertThatCachedNodeCountIs(951)
                .AssertThatTotalMemoryUsedIs(853528);
        }

        [Test]
        public void Keep_DeleteCachedPersistedNode_IfReplaced()
        {
            PruningContext ctx = PruningContext.InMemoryWithPastKeyTracking
                .WithMaxDepth(1)
                .WithPersistedMemoryLimit(100.MiB())
                .TurnOnPrune()
                .TurnOffAlwaysPrunePersistedNode();

            ctx
                .SetAccountBalance(0, (UInt256)1)
                .Commit()
                .WaitForPruning()
                .AssertThatDirtyNodeCountIs(1)
                .AssertThatCachedNodeCountIs(1);

            for (int i = 1; i < 256; i++)
            {
                ctx
                    .SetAccountBalance(0, (UInt256)(i + 1))
                    .Commit()
                    .WaitForPruning();
            }

            ctx
                .AssertThatDirtyNodeCountIs(2)
                .AssertThatCachedNodeCountIs(3)
                .AssertThatTotalMemoryUsedIs(1680);
        }

        [Test]
        public void Retain_Some_PersistedNodes()
        {
            PruningContext ctx = PruningContext.InMemory
                .WithMaxDepth(1)
                .WithPersistedMemoryLimit(200.KiB())
                .WithPrunePersistedNodeParameter(1, 0.1)
                .TurnOnPrune()
                .TurnOffAlwaysPrunePersistedNode();

            bool thresholdReached = false;
            for (int i = 0; i < 256; i++)
            {
                ctx
                    .SetAccountBalance(i, (UInt256)i)
                    .Commit()
                    .WaitForPruning();

                if (thresholdReached)
                {
                    ctx.AssertThatTotalMemoryUsedIsNoLessThan((long)(200.KiB() * 0.1));
                }
                else if (ctx.TotalMemoryUsage > 190.KiB())
                {
                    thresholdReached = true;
                }
            }

            ctx
                .AssertThatDirtyNodeCountIs(9)
                .AssertThatCachedNodeCountMoreThan(280);
        }
    }
}
