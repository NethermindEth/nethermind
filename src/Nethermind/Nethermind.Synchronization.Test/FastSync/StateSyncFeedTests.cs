// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync
{
    [TestFixture(1, 0)]
    [TestFixture(1, 100)]
    [TestFixture(4, 0)]
    [TestFixture(4, 100)]
    [Parallelizable(ParallelScope.All)]
    public class StateSyncFeedTests : StateSyncFeedTestsBase
    {
        // Useful for set and forget run. But this test is taking a long time to have it set to other than 1.
        private const int TestRepeatCount = 1;

        public StateSyncFeedTests(int peerCount, int maxNodeLatency) : base(peerCount, maxNodeLatency)
        {
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Big_test((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager)
            {
                RemoteCodeDb =
                {
                    [Keccak.Compute(TrieScenarios.Code0).Bytes] = TrieScenarios.Code0,
                    [Keccak.Compute(TrieScenarios.Code1).Bytes] = TrieScenarios.Code1,
                    [Keccak.Compute(TrieScenarios.Code2).Bytes] = TrieScenarios.Code2,
                    [Keccak.Compute(TrieScenarios.Code3).Bytes] = TrieScenarios.Code3,
                },
            };
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);

            dbContext.CompareTrees("BEFORE FIRST SYNC", true);

            SafeContext ctx = PrepareDownloader(dbContext, mock =>
                mock.SetFilter(((MemDb)dbContext.RemoteStateDb).Keys.Take(((MemDb)dbContext.RemoteStateDb).Keys.Count - 4).Select(k =>
                {
                    return HashKey(k);
                }).ToArray()));

            await ActivateAndWait(ctx, dbContext, 1024);

            dbContext.CompareTrees("AFTER FIRST SYNC", true);

            dbContext.LocalStateTree.RootHash = dbContext.RemoteStateTree.RootHash;
            for (byte i = 0; i < 8; i++)
                dbContext.RemoteStateTree
                    .Set(TestItem.Addresses[i], TrieScenarios.AccountJustState0.WithChangedBalance(i)
                        .WithChangedNonce(1)
                        .WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code3))
                        .WithChangedStorageRoot(SetStorage(dbContext.RemoteTrieStore, i, TestItem.Addresses[i]).RootHash));

            dbContext.RemoteStateTree.UpdateRootHash();
            dbContext.RemoteStateTree.Commit(0);

            ctx.Feed.FallAsleep();
            ctx.Pool.WakeUpAll();

            await ActivateAndWait(ctx, dbContext, 1024);

            dbContext.CompareTrees("AFTER SECOND SYNC", true);

            dbContext.LocalStateTree.RootHash = dbContext.RemoteStateTree.RootHash;
            for (byte i = 0; i < 16; i++)
                dbContext.RemoteStateTree
                    .Set(TestItem.Addresses[i], TrieScenarios.AccountJustState0.WithChangedBalance(i)
                        .WithChangedNonce(2)
                        .WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code3))
                        .WithChangedStorageRoot(SetStorage(dbContext.RemoteTrieStore, (byte)(i % 7), TestItem.Addresses[i]).RootHash));

            dbContext.RemoteStateTree.UpdateRootHash();
            dbContext.RemoteStateTree.Commit(0);

            ctx.Feed.FallAsleep();

            ctx.Pool.WakeUpAll();
            foreach (SyncPeerMock mock in ctx.SyncPeerMocks)
            {
                mock.SetFilter(null);
            }

            await ActivateAndWait(ctx, dbContext, 1024);

            dbContext.CompareTrees("END");
            dbContext.AssertFlushed();
        }

        private static Hash256 HashKey(byte[] k)
        {
            return new Hash256(k[^32..]);
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Can_download_a_full_state((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);


            dbContext.CompareTrees("BEGIN");

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 1024, 10000000);

            dbContext.CompareTrees("END");
        }

        [Test]
        [Repeat(TestRepeatCount)]
        public async Task Can_download_an_empty_tree()
        {
            DbContext dbContext = new(_logger, _logManager);
            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 1000);
            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Can_download_in_multiple_connections((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);


            SafeContext ctx = PrepareDownloader(dbContext, mock =>
                mock.SetFilter(new[] { dbContext.RemoteStateTree.RootHash }));
            await ActivateAndWait(ctx, dbContext, 1024, 1000);


            ctx.Pool.WakeUpAll();
            foreach (SyncPeerMock mock in ctx.SyncPeerMocks)
            {
                mock.SetFilter(null);
            }

            ctx.Feed.FallAsleep();
            await ActivateAndWait(ctx, dbContext, 1024);


            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Can_download_when_executor_sends_shorter_responses((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);


            dbContext.CompareTrees("BEGIN");

            SafeContext ctx = PrepareDownloader(dbContext, mock => mock.MaxResponseLength = 1);
            await ActivateAndWait(ctx, dbContext, 1024);


            dbContext.CompareTrees("END");
        }

        [Test]
        [Repeat(TestRepeatCount)]
        public async Task When_saving_root_goes_asleep()
        {
            DbContext dbContext = new(_logger, _logManager);
            dbContext.RemoteStateTree.Set(TestItem.KeccakA, Build.An.Account.TestObject);
            dbContext.RemoteStateTree.Commit(0);


            dbContext.CompareTrees("BEGIN");

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 1024);

            dbContext.CompareTrees("END");

            ctx.Feed.CurrentState.Should().Be(SyncFeedState.Dormant);
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Can_download_with_moving_target((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);

            dbContext.CompareTrees("BEFORE FIRST SYNC");

            SafeContext ctx = PrepareDownloader(dbContext, mock =>
                mock.SetFilter(((MemDb)dbContext.RemoteStateDb).Keys.Take(((MemDb)dbContext.RemoteStateDb).Keys.Count - 1).Select(k => HashKey(k)).ToArray()));
            await ActivateAndWait(ctx, dbContext, 1024, 1000);


            dbContext.CompareTrees("AFTER FIRST SYNC");

            dbContext.LocalStateTree.RootHash = dbContext.RemoteStateTree.RootHash;
            dbContext.RemoteStateTree.Set(TestItem.AddressA, TrieScenarios.AccountJustState0.WithChangedBalance(123.Ether()));
            dbContext.RemoteStateTree.Set(TestItem.AddressB, TrieScenarios.AccountJustState1.WithChangedBalance(123.Ether()));
            dbContext.RemoteStateTree.Set(TestItem.AddressC, TrieScenarios.AccountJustState2.WithChangedBalance(123.Ether()));

            dbContext.CompareTrees("BEFORE ROOT HASH UPDATE");

            dbContext.RemoteStateTree.UpdateRootHash();

            dbContext.CompareTrees("BEFORE COMMIT");

            dbContext.RemoteStateTree.Commit(1);


            ctx.Pool.WakeUpAll();

            ctx.Feed.FallAsleep();

            foreach (SyncPeerMock mock in ctx.SyncPeerMocks)
            {
                mock.SetFilter(null);
            }

            await ActivateAndWait(ctx, dbContext, 1024, 2000);


            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Dependent_branch_counter_is_zero_and_leaf_is_short((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);


            StorageTree remoteStorageTree = new(dbContext.RemoteTrieStore.GetTrieStore(TestItem.AddressD.ToAccountPath), Keccak.EmptyTreeHash, LimboLogs.Instance);
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb000"), new byte[] { 1 });
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb111"), new byte[] { 1 });
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeb000000000000000000000000000000000000000000"), new byte[] { 1 });
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeb111111111111111111111111111111111111111111"), new byte[] { 1 });
            remoteStorageTree.Commit(0);

            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 1024);


            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Scenario_plus_one_code((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);


            dbContext.RemoteCodeDb.Set(Keccak.Compute(TrieScenarios.Code0), TrieScenarios.Code0);

            Account changedAccount = TrieScenarios.AccountJustState0.WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code0));
            dbContext.RemoteStateTree.Set(TestItem.AddressD, changedAccount);
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 1024);


            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Scenario_plus_one_code_one_storage((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);


            dbContext.RemoteCodeDb.Set(Keccak.Compute(TrieScenarios.Code0), TrieScenarios.Code0);

            StorageTree remoteStorageTree = new(dbContext.RemoteTrieStore.GetTrieStore(TestItem.AddressD.ToAccountPath), Keccak.EmptyTreeHash, _logManager);
            remoteStorageTree.Set((UInt256)1, new byte[] { 1 });
            remoteStorageTree.Commit(0);

            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code0)).WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 1024);


            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Scenario_plus_one_storage((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);

            StorageTree remoteStorageTree = new(dbContext.RemoteTrieStore.GetTrieStore(TestItem.AddressD.ToAccountPath), Keccak.EmptyTreeHash, _logManager);
            remoteStorageTree.Set((UInt256)1, new byte[] { 1 });
            remoteStorageTree.Commit(0);

            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 1024);


            dbContext.CompareTrees("END");
        }

        [Test]
        public async Task When_empty_response_received_return_lesser_quality()
        {
            DbContext dbContext = new(_logger, _logManager);
            dbContext.RemoteStateTree.Set(TestItem.KeccakA, Build.An.Account.TestObject);
            dbContext.RemoteStateTree.Commit(0);

            SafeContext ctx = new();

            BlockTree blockTree = Build.A.BlockTree().OfChainLength((int)BlockTree.BestSuggestedHeader!.Number).TestObject;

            ctx.TreeFeed = new(SyncMode.StateNodes, dbContext.LocalCodeDb, dbContext.LocalNodeStorage, dbContext.LocalStateFactory, blockTree, _logManager);
            ctx.Feed = new StateSyncFeed(ctx.TreeFeed, _logManager);
            ctx.Feed.SyncModeSelectorOnChanged(SyncMode.StateNodes | SyncMode.FastBlocks);
            ctx.TreeFeed.ResetStateRoot(100, dbContext.RemoteStateTree.RootHash, SyncFeedState.Dormant);

            using StateSyncBatch? request = await ctx.Feed.PrepareRequest();
            request.Should().NotBeNull();

            ctx.Feed.HandleResponse(request, new PeerInfo(Substitute.For<ISyncPeer>()))
                .Should().Be(SyncResponseHandlingResult.LesserQuality);
        }

        [Test]
        public async Task When_empty_response_received_with_no_peer_return_not_allocated()
        {
            DbContext dbContext = new(_logger, _logManager);
            dbContext.RemoteStateTree.Set(TestItem.KeccakA, Build.An.Account.TestObject);
            dbContext.RemoteStateTree.Commit(0);

            SafeContext ctx = new();

            BlockTree blockTree = Build.A.BlockTree().OfChainLength((int)BlockTree.BestSuggestedHeader!.Number).TestObject;

            ctx.TreeFeed = new(SyncMode.StateNodes, dbContext.LocalCodeDb, dbContext.LocalNodeStorage, dbContext.LocalStateFactory, blockTree, _logManager);
            ctx.Feed = new StateSyncFeed(ctx.TreeFeed, _logManager);
            ctx.Feed.SyncModeSelectorOnChanged(SyncMode.StateNodes | SyncMode.FastBlocks);
            ctx.TreeFeed.ResetStateRoot(100, dbContext.RemoteStateTree.RootHash, SyncFeedState.Dormant);

            using StateSyncBatch? request = await ctx.Feed.PrepareRequest();
            request.Should().NotBeNull();

            ctx.Feed.HandleResponse(request, peer: null)
                .Should().Be(SyncResponseHandlingResult.NotAssigned);
        }


        [Test]
        public async Task Delete_account_when_changing_pivot_scenario_1()
        {
            DbContext dbContext = new(_logger, _logManager);

            Account account1 = Build.An.Account.WithBalance(1).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            Account account3 = Build.An.Account.WithBalance(3).TestObject;

            Hash256[] paths = new Hash256[3];
            paths[0] = new Hash256("0xccccccccccccccccccccddddddddddddddddeeeeeeeeeeeeeeeeb12233445566");
            paths[1] = new Hash256("0xccccccccccccccccccccddddddddddddddddeeeeeeeeeeeeeeeeb21233445566");
            paths[2] = new Hash256("0xccccccccccccccccccccddddddddddddddddeeeeeeeeeeeeeeeeb22233445566");

            //Create following state tree
            //B
            //- - - - B - - - - - L - - - - -
            //- - - - L - - - - - - - - L - -
            //Deleting leaf at 5e will cause branch to be replaced with last leaf (extend key)

            dbContext.RemoteStateTree.Set(paths[0], account1);
            dbContext.RemoteStateTree.Set(paths[1], account2);
            dbContext.RemoteStateTree.Set(paths[2], account3);
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 1024);

            dbContext.RemoteStateTree.Set(TestItem.AddressB, null);
            dbContext.RemoteStateTree.Commit(1);

            ctx.Feed.FallAsleep();
            ctx.Pool.WakeUpAll();

            await ActivateAndWait(ctx, dbContext, 1025, 100_000);

            dbContext.CompareTrees("END");

            IState localState = dbContext.LocalStateFactory.Get(dbContext.RemoteStateTree.RootHash);
            Account? lac1 = localState.Get(TestItem.AddressA.ToAccountPath);
            Account? lac2 = localState.Get(TestItem.AddressB.ToAccountPath);
            Account? lac3 = localState.Get(TestItem.AddressC.ToAccountPath);

            //Account? lac1 = dbContext.LocalStateTree.Get(TestItem.AddressB);
            Assert.IsNull(lac2);
        }

        [Test]
        public async Task Delete_account_when_changing_pivot_scenario_2()
        {
            DbContext dbContext = new(_logger, _logManager);

            //Create following state tree
            //E
            //B - - - - - - - - - - - - - - -
            //L L L - - - - - - - - - - - - -
            //Deleting one leaf at does not make any changes to tree - only remove leaf data
            Hash256[] paths = new Hash256[3];
            paths[0] = new Hash256("0xccccccccccccccccccccddddddddddddddddeeeeeeeeeeeeeeeeb12233445566");
            paths[1] = new Hash256("0xccccccccccccccccccccddddddddddddddddeeeeeeeeeeeeeeeeb22233445566");
            paths[2] = new Hash256("0xccccccccccccccccccccddddddddddddddddeeeeeeeeeeeeeeeeb32233445566");

            dbContext.RemoteStateTree.Set(paths[0], Build.An.Account.WithBalance(0).WithNonce(1).TestObject);
            dbContext.RemoteStateTree.Set(paths[1], Build.An.Account.WithBalance(1).TestObject);
            dbContext.RemoteStateTree.Set(paths[2], Build.An.Account.WithBalance(2).TestObject);
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 1024);

            dbContext.RemoteStateTree.Set(paths[1], null);
            dbContext.RemoteStateTree.Commit(1);

            ctx.Feed.FallAsleep();
            ctx.Pool.WakeUpAll();

            await ActivateAndWait(ctx, dbContext, 1025, 200_000);

            //dbContext.DbPrunner.Wait();
            dbContext.CompareTrees("END");

            IState localState = dbContext.LocalStateFactory.Get(dbContext.RemoteStateTree.RootHash);
            Account? lac1 = localState.Get(paths[0]);
            Account? lac2 = localState.Get(paths[1]);
            Account? lac3 = localState.Get(paths[2]);

            //Account?[] localAccounts = GetLocalAccounts(paths, dbContext);

            Assert.IsNotNull(lac1);
            Assert.IsNull(lac2);
            Assert.IsNotNull(lac3);
        }

        [Test]
        public async Task Delete_account_when_changing_pivot_scenario_22()
        {
            DbContext dbContext = new(_logger, _logManager);

            //Create following state tree
            //E
            //B - - - - - - - - - - - - - - -
            //L L L - - - - - - - - - - - - -
            //Deleting one leaf at does not make any changes to tree - only remove leaf data
            Hash256[] paths = new Hash256[3];
            paths[0] = new Hash256("0xccccccccccccccccccccddddddddddddddddeeeeeeeeeeeeeeeeb12233445566");
            paths[1] = new Hash256("0xccccccccccccccccccccddddddddddddddddeeeeeeeeeeeeeeeeb22233445566");
            paths[2] = new Hash256("0xccccccccccccccccccccddddddddddddddddeeeeeeeeeeeeeeeeb32233445566");

            StorageTree st = new StorageTree(new ScopedTrieStore(dbContext.RemoteTrieStore, paths[1]),
                NullLogManager.Instance);
            st.Set(new UInt256(100), new byte[] { 1, 2, 3 });
            st.Set(new UInt256(200), new byte[] { 10, 20, 30 });
            st.Commit(0);

            var slotKey = new byte[32];
            new UInt256(100).ToBigEndian(slotKey);
            KeccakHash.ComputeHashBytesToSpan(slotKey, slotKey);

            dbContext.RemoteStateTree.Set(paths[0], Build.An.Account.WithBalance(0).WithNonce(1).TestObject);
            dbContext.RemoteStateTree.Set(paths[1], Build.An.Account.WithBalance(1).WithStorageRoot(st.RootHash).TestObject);
            dbContext.RemoteStateTree.Set(paths[2], Build.An.Account.WithBalance(2).TestObject);
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 1024);

            st.Set(slotKey, Array.Empty<byte>());
            st.Commit(1);

            dbContext.RemoteStateTree.Set(paths[1], Build.An.Account.WithBalance(1).WithStorageRoot(st.RootHash).TestObject);
            dbContext.RemoteStateTree.Commit(1);

            ctx.Feed.FallAsleep();
            ctx.Pool.WakeUpAll();

            await ActivateAndWait(ctx, dbContext, 1025, 200_000);

            //dbContext.DbPrunner.Wait();
            dbContext.CompareTrees("END");

            IState localState = dbContext.LocalStateFactory.Get(dbContext.RemoteStateTree.RootHash);
            Account? lac1 = localState.Get(paths[0]);
            Account? lac2 = localState.Get(paths[1]);
            Account? lac3 = localState.Get(paths[2]);

            Assert.IsNotNull(lac1);
            //Assert.IsNull(lac2);
            Assert.IsNotNull(lac3);

            var slotBytes = localState.GetStorageAt(paths[1], new ValueHash256(slotKey));

            Assert.IsTrue(slotBytes.IsZero());
        }

        [Test]
        public async Task Delete_account_when_changing_pivot_scenario_3()
        {
            DbContext dbContext = new(_logger, _logManager);

            //Create following state tree
            //E
            //B - - - - - - - - - - - - - - -
            //- L E - - - - - - - - - - - - - 
            //- B - - - - - - - - - - - - - -
            //L L - - - - - - - - - - - - - -
            //removing last leaf will cause remaining branch to be converted to leaf and then E->L to be converted into a single leaf
            //whole subtree needs to be cleaned
            Hash256[] paths = new Hash256[3];
            paths[0] = new Hash256("0xcccc100000000000000000000000000000000000000000000000000000000000");
            paths[1] = new Hash256("0xcccc200000000000000000000000000000000000000000000000000000000000");
            paths[2] = new Hash256("0xcccc200100000000000000000000000000000000000000000000000000000000");

            dbContext.RemoteStateTree.Set(paths[0], Build.An.Account.WithBalance(0).WithNonce(1).TestObject);
            dbContext.RemoteStateTree.Set(paths[1], Build.An.Account.WithBalance(1).TestObject);
            dbContext.RemoteStateTree.Set(paths[2], Build.An.Account.WithBalance(2).TestObject);
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 1024);

            dbContext.RemoteStateTree.Set(paths[2], null);
            dbContext.RemoteStateTree.Commit(1);

            await ActivateAndWait(ctx, dbContext, 1025);

            dbContext.CompareTrees("END");
            //dbContext.DbPrunner.Wait();

            IState localState = dbContext.LocalStateFactory.Get(dbContext.RemoteStateTree.RootHash);
            Account? lac1 = localState.Get(paths[0]);
            Account? lac2 = localState.Get(paths[1]);
            Account? lac3 = localState.Get(paths[2]);

            Assert.IsNotNull(lac1);
            Assert.IsNotNull(lac2);
            Assert.IsNull(lac3);

            //Account?[] localAccounts = GetLocalAccounts(paths, dbContext);
        }

        [Test]
        public async Task Delete_account_when_changing_pivot_scenario_4()
        {
            DbContext dbContext = new(_logger, _logManager);

            //Create following state tree
            //E
            //B - - - - - - - - - - - - - - -
            //L L L - - - - - - - - - - - - -
            //Deleting one leaf at does not make any changes to tree - only remove leaf data
            Hash256[] paths = new Hash256[4];
            paths[0] = new Hash256("0xccccccccccccccccccccddddddddddddddddeeeeeeeeeeeeeeeeb12233445566");
            paths[1] = new Hash256("0xccccccccccccccccccccddddddddddddddddeeeeeeeeeeeeeeeeb22233445566");

            paths[2] = new Hash256("0xaaaaaaaaaaaaaaaaaaaabbbbbbbbbbbbbbbbeeeeeeeeeeeeeeeeb12233445566");
            paths[3] = new Hash256("0xaaaaaaaaaaaaaaaaaaaabbbbbbbbbbbbbbbbeeeeeeeeeeeeeeeeb22233445566");

            StorageTree st = new StorageTree(new ScopedTrieStore(dbContext.RemoteTrieStore, paths[0]),
                NullLogManager.Instance);
            st.Set(new UInt256(100), new byte[] { 1, 2, 3});
            st.Set(new UInt256(200), new byte[] { 10, 20, 30 });
            st.Commit(0);

            var slotKey = new byte[32];
            new UInt256(100).ToBigEndian(slotKey);
            KeccakHash.ComputeHashBytesToSpan(slotKey, slotKey);

            dbContext.RemoteStateTree.Set(paths[0], Build.An.Account.WithBalance(0).WithNonce(1).WithStorageRoot(st.RootHash).TestObject);
            dbContext.RemoteStateTree.Set(paths[1], Build.An.Account.WithBalance(1).TestObject);
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 1024, 200_000);

            dbContext.RemoteStateTree.Set(paths[0], null);
            dbContext.RemoteStateTree.Set(paths[1], null);
            dbContext.RemoteStateTree.Set(paths[2], Build.An.Account.WithBalance(10).TestObject);
            dbContext.RemoteStateTree.Set(paths[3], Build.An.Account.WithBalance(11).TestObject);
            dbContext.RemoteStateTree.Commit(1);

            ctx.Feed.FallAsleep();
            ctx.Pool.WakeUpAll();

            await ActivateAndWait(ctx, dbContext, 1025, 200_000);

            //dbContext.DbPrunner.Wait();
            dbContext.CompareTrees("END");

            IState localState = dbContext.LocalStateFactory.Get(dbContext.RemoteStateTree.RootHash);
            Account? lac1 = localState.Get(paths[0]);
            var slotBytes = localState.GetStorageAt(paths[0], new ValueHash256(slotKey));
            Account? lac2 = localState.Get(paths[1]);
            Account? lac3 = localState.Get(paths[2]);
            Account? lac4 = localState.Get(paths[3]);

            //Account?[] localAccounts = GetLocalAccounts(paths, dbContext);

            Assert.IsNull(lac1);
            Assert.IsNull(lac2);
            Assert.IsNotNull(lac3);
            Assert.IsNotNull(lac4);
        }
    }
}
