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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Common.Concurrency;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Synchronization.Test.FastSync
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class StateSyncFeedTests : StateSyncFeedTestsBase
    {
        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(3)]
        public async Task Big_test((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            dbContext.RemoteCodeDb[Keccak.Compute(TrieScenarios.Code0).Bytes] = TrieScenarios.Code0;
            dbContext.RemoteCodeDb[Keccak.Compute(TrieScenarios.Code1).Bytes] = TrieScenarios.Code1;
            dbContext.RemoteCodeDb[Keccak.Compute(TrieScenarios.Code2).Bytes] = TrieScenarios.Code2;
            dbContext.RemoteCodeDb[Keccak.Compute(TrieScenarios.Code3).Bytes] = TrieScenarios.Code3;
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);

            SyncPeerMock mock = new(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            mock.SetFilter(((MemDb) dbContext.RemoteStateDb).Keys.Take(((MemDb) dbContext.RemoteStateDb).Keys.Count - 4).Select(k => new Keccak(k)).ToArray());

            dbContext.CompareTrees("BEFORE FIRST SYNC", true);

            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);

            dbContext.CompareTrees("AFTER FIRST SYNC", true);

            dbContext.LocalStateTree.RootHash = dbContext.RemoteStateTree.RootHash;
            for (byte i = 0; i < 8; i++)
                dbContext.RemoteStateTree
                    .Set(TestItem.Addresses[i], TrieScenarios.AccountJustState0.WithChangedBalance(i)
                        .WithChangedNonce(1)
                        .WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code3))
                        .WithChangedStorageRoot(SetStorage(dbContext.RemoteTrieStore, i).RootHash));

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
                        .WithChangedStorageRoot(SetStorage(dbContext.RemoteTrieStore, (byte)(i % 7)).RootHash));

            dbContext.RemoteStateTree.UpdateRootHash();
            dbContext.RemoteStateTree.Commit(0);

            ctx.Feed.FallAsleep();

            ctx.Pool.WakeUpAll();
            mock.SetFilter(null);
            await ActivateAndWait(ctx, dbContext, 1024);


            dbContext.CompareTrees("END");
            dbContext.CompareCodeDbs();
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        // [Retry(3)]
        public async Task Can_download_a_full_state((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);


            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);

            dbContext.CompareTrees("END");
        }

        [Test]
        public async Task Can_download_an_empty_tree()
        {
            DbContext dbContext = new(_logger, _logManager);
            SyncPeerMock mock = new(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1000);
            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(3)]
        public async Task Can_download_in_multiple_connections((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);


            SyncPeerMock mock = new(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            mock.SetFilter(new[] {dbContext.RemoteStateTree.RootHash});

            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024, 1000);


            ctx.Pool.WakeUpAll();
            mock.SetFilter(null);
            ctx.Feed.FallAsleep();
            await ActivateAndWait(ctx, dbContext, 1024);


            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        // [Retry(3)]
        public async Task Can_download_when_executor_sends_shorter_responses((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);


            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            mock.MaxResponseLength = 1;

            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);


            dbContext.CompareTrees("END");
        }

        [Test]
        [Retry(3)]
        public async Task When_saving_root_goes_asleep()
        {
            DbContext dbContext = new(_logger, _logManager);
            dbContext.RemoteStateTree.Set(TestItem.KeccakA, Build.An.Account.TestObject);
            dbContext.RemoteStateTree.Commit(0);


            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);

            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);

            dbContext.CompareTrees("END");

            ctx.Feed.CurrentState.Should().Be(SyncFeedState.Dormant);
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(3)]
        public async Task Can_download_with_moving_target((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);


            SyncPeerMock mock = new(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            mock.SetFilter(((MemDb) dbContext.RemoteStateDb).Keys.Take(((MemDb) dbContext.RemoteStateDb).Keys.Count - 1).Select(k => new Keccak(k)).ToArray());

            dbContext.CompareTrees("BEFORE FIRST SYNC");

            SafeContext ctx = PrepareDownloader(dbContext, mock);
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
            mock.SetFilter(null);
            await ActivateAndWait(ctx, dbContext, 1024, 2000);


            dbContext.CompareTrees("END");
            dbContext.CompareCodeDbs();
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(3)]
        public async Task Dependent_branch_counter_is_zero_and_leaf_is_short((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);


            StorageTree remoteStorageTree = new(dbContext.RemoteTrieStore, Keccak.EmptyTreeHash, LimboLogs.Instance);
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

            SyncPeerMock mock = new(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);


            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(3)]
        public async Task Scenario_plus_one_code((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);


            dbContext.RemoteCodeDb.Set(Keccak.Compute(TrieScenarios.Code0), TrieScenarios.Code0);

            var changedAccount = TrieScenarios.AccountJustState0.WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code0));
            dbContext.RemoteStateTree.Set(TestItem.AddressD, changedAccount);
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);


            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(3)]
        public async Task Scenario_plus_one_code_one_storage((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);


            dbContext.RemoteCodeDb.Set(Keccak.Compute(TrieScenarios.Code0), TrieScenarios.Code0);

            StorageTree remoteStorageTree = new(dbContext.RemoteTrieStore, Keccak.EmptyTreeHash, _logManager);
            remoteStorageTree.Set((UInt256) 1, new byte[] {1});
            remoteStorageTree.Commit(0);

            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code0)).WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);


            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(3)]
        public async Task Scenario_plus_one_storage((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);


            StorageTree remoteStorageTree = new(dbContext.RemoteTrieStore, Keccak.EmptyTreeHash, _logManager);
            remoteStorageTree.Set((UInt256) 1, new byte[] {1});
            remoteStorageTree.Commit(0);

            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);


            dbContext.CompareTrees("END");
        }

        // [Test, Retry(5)]
        // public async Task Silences_bad_peers()
        // {
        //     DbContext dbContext = new DbContext(_logger, _logManager);
        //     SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb, SyncPeerMock.NotPreimage);
        //     SafeContext ctx = PrepareDownloader(mock);
        //     _feed.SetNewStateRoot(1024, Keccak.Compute("the_peer_has_no_data"));
        //     _feed.Activate();
        //     await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(1000)).Unwrap()
        //         .ContinueWith(t =>
        //         {
        //             Assert.AreEqual(0, _pool.InitializedPeers.Count(p => p.CanBeAllocated(AllocationContexts.All)));
        //         });
        // }

        // [Test]
        // [Retry(3)]
        // public async Task Silences_when_peer_sends_empty_byte_arrays()
        // {
        //     DbContext dbContext = new DbContext(_logger, _logManager);
        //     SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb, SyncPeerMock.EmptyArraysInResponses);
        //     SafeContext ctx = PrepareDownloader(mock);
        //     _feed.SetNewStateRoot(1024, Keccak.Compute("the_peer_has_no_data"));
        //     _feed.Activate();
        //     await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(1000)).Unwrap()
        //         .ContinueWith(t =>
        //         {
        //             _pool.InitializedPeers.Count(p => p.CanBeAllocated(AllocationContexts.All)).Should().Be(0);
        //         });
        // }
    }
}
