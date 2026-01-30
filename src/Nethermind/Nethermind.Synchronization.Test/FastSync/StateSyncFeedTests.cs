// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
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
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync
{
    [TestFixtureSource(typeof(StateSyncFeedTestsFixtureSource))]
    [Parallelizable(ParallelScope.Fixtures)]
    public class StateSyncFeedTests(
        Action<ContainerBuilder> registerTreeSyncStore,
        int peerCount,
        int maxNodeLatency)
        : StateSyncFeedTestsBase(registerTreeSyncStore, peerCount, maxNodeLatency)
    {
        // Useful for set and forget run. But this test is taking a long time to have it set to other than 1.
        private const int TestRepeatCount = 1;

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        [Explicit("This test is not stable, especially on slow Github Actions machines")]
        public async Task Big_test((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            remote.CodeDb[Keccak.Compute(TrieScenarios.Code0).Bytes] = TrieScenarios.Code0;
            remote.CodeDb[Keccak.Compute(TrieScenarios.Code1).Bytes] = TrieScenarios.Code1;
            remote.CodeDb[Keccak.Compute(TrieScenarios.Code2).Bytes] = TrieScenarios.Code2;
            remote.CodeDb[Keccak.Compute(TrieScenarios.Code3).Bytes] = TrieScenarios.Code3;
            testCase.SetupTree(remote.StateTree, remote.TrieStore, remote.CodeDb);

            CompareTrees(local, remote, _logger, "BEFORE FIRST SYNC", true);

            await using IContainer container = PrepareDownloader(local, remote, mock =>
                mock.SetFilter(((MemDb)remote.StateDb).Keys.Take(((MemDb)remote.StateDb).Keys.Count - 4).Select(HashKey).ToArray()));

            SafeContext ctx = container.Resolve<SafeContext>();
            await ActivateAndWait(ctx);

            CompareTrees(local, remote, _logger, "AFTER FIRST SYNC", true);

            local.StateTree.RootHash = remote.StateTree.RootHash;
            for (byte i = 0; i < 8; i++)
                remote.StateTree
                    .Set(TestItem.Addresses[i], TrieScenarios.AccountJustState0.WithChangedBalance(i)
                        .WithChangedNonce(1)
                        .WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code3))
                        .WithChangedStorageRoot(SetStorage(remote.TrieStore, i, TestItem.Addresses[i]).RootHash));

            remote.StateTree.UpdateRootHash();
            remote.StateTree.Commit();

            await ctx.SuggestBlocksWithUpdatedRootHash(remote.StateTree.RootHash);

            ctx.Feed.FallAsleep();
            ctx.Pool.WakeUpAll();

            await ActivateAndWait(ctx);

            CompareTrees(local, remote, _logger, "AFTER SECOND SYNC", true);

            local.StateTree.RootHash = remote.StateTree.RootHash;
            for (byte i = 0; i < 16; i++)
                remote.StateTree
                    .Set(TestItem.Addresses[i], TrieScenarios.AccountJustState0.WithChangedBalance(i)
                        .WithChangedNonce(2)
                        .WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code3))
                        .WithChangedStorageRoot(SetStorage(remote.TrieStore, (byte)(i % 7), TestItem.Addresses[i]).RootHash));

            remote.StateTree.UpdateRootHash();
            remote.StateTree.Commit();

            await ctx.SuggestBlocksWithUpdatedRootHash(remote.StateTree.RootHash);

            ctx.Feed.FallAsleep();
            ctx.Pool.WakeUpAll();
            foreach (SyncPeerMock mock in ctx.SyncPeerMocks)
            {
                mock.SetFilter(null);
            }

            await ActivateAndWait(ctx);

            CompareTrees(local, remote, _logger, "END");
            local.AssertFlushed();
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
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            testCase.SetupTree(remote.StateTree, remote.TrieStore, remote.CodeDb);

            CompareTrees(local, remote, _logger, "BEGIN");

            await using IContainer container = PrepareDownloader(local, remote);
            SafeContext ctx = container.Resolve<SafeContext>();
            await ActivateAndWait(ctx);

            CompareTrees(local, remote, _logger, "END");
        }

        [Test]
        [Repeat(TestRepeatCount)]
        public async Task Can_download_an_empty_tree()
        {
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            await using IContainer container = PrepareDownloader(local, remote);
            SafeContext ctx = container.Resolve<SafeContext>();
            await ActivateAndWait(ctx);
            CompareTrees(local, remote, _logger, "END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Can_download_in_multiple_connections((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            testCase.SetupTree(remote.StateTree, remote.TrieStore, remote.CodeDb);

            await using IContainer container = PrepareDownloader(local, remote, mock =>
                mock.SetFilter(new[] { remote.StateTree.RootHash }));
            SafeContext ctx = container.Resolve<SafeContext>();
            await ActivateAndWait(ctx, 1000);

            ctx.Pool.WakeUpAll();
            foreach (SyncPeerMock mock in ctx.SyncPeerMocks)
            {
                mock.SetFilter(null);
            }

            ctx.Feed.FallAsleep();
            await ActivateAndWait(ctx);

            CompareTrees(local, remote, _logger, "END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Can_download_when_executor_sends_shorter_responses((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            testCase.SetupTree(remote.StateTree, remote.TrieStore, remote.CodeDb);

            CompareTrees(local, remote, _logger, "BEGIN");

            await using IContainer container = PrepareDownloader(local, remote, static mock => mock.MaxResponseLength = 1);
            SafeContext ctx = container.Resolve<SafeContext>();
            await ActivateAndWait(ctx);

            CompareTrees(local, remote, _logger, "END");
        }

        [Test]
        public async Task When_saving_root_goes_asleep_and_then_restart_to_new_tree_when_reactivated()
        {
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            remote.StateTree.Set(TestItem.KeccakA, Build.An.Account.TestObject);
            remote.StateTree.Commit();

            CompareTrees(local, remote, _logger, "BEGIN");

            await using IContainer container = PrepareDownloader(local, remote);
            SafeContext ctx = container.Resolve<SafeContext>();
            await ActivateAndWait(ctx);

            CompareTrees(local, remote, _logger, "END");

            ctx.Feed.CurrentState.Should().Be(SyncFeedState.Dormant);
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        [Retry(3)]
        public async Task Can_download_with_moving_target((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            testCase.SetupTree(remote.StateTree, remote.TrieStore, remote.CodeDb);

            CompareTrees(local, remote, _logger, "BEFORE FIRST SYNC");

            await using IContainer container = PrepareDownloader(local, remote, mock =>
                mock.SetFilter(((MemDb)remote.StateDb).Keys.Take(((MemDb)remote.StateDb).Keys.Count - 1).Select(k => HashKey(k)).ToArray()));
            SafeContext ctx = container.Resolve<SafeContext>();
            await ActivateAndWait(ctx, TimeoutLength);

            CompareTrees(local, remote, _logger, "AFTER FIRST SYNC");

            local.StateTree.RootHash = remote.StateTree.RootHash;
            remote.StateTree.Set(TestItem.AddressA, TrieScenarios.AccountJustState0.WithChangedBalance(123.Ether()));
            remote.StateTree.Set(TestItem.AddressB, TrieScenarios.AccountJustState1.WithChangedBalance(123.Ether()));
            remote.StateTree.Set(TestItem.AddressC, TrieScenarios.AccountJustState2.WithChangedBalance(123.Ether()));

            CompareTrees(local, remote, _logger, "BEFORE ROOT HASH UPDATE");

            remote.StateTree.UpdateRootHash();

            CompareTrees(local, remote, _logger, "BEFORE COMMIT");

            remote.StateTree.Commit();

            ctx.Pool.WakeUpAll();
            ctx.Feed.FallAsleep();

            await ctx.SuggestBlocksWithUpdatedRootHash(remote.StateTree.RootHash);

            foreach (SyncPeerMock mock in ctx.SyncPeerMocks)
            {
                mock.SetFilter(null);
            }

            await ActivateAndWait(ctx, TimeoutLength);

            CompareTrees(local, remote, _logger, "END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Dependent_branch_counter_is_zero_and_leaf_is_short((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            testCase.SetupTree(remote.StateTree, remote.TrieStore, remote.CodeDb);

            StorageTree remoteStorageTree = new(remote.TrieStore.GetTrieStore(TestItem.AddressD), Keccak.EmptyTreeHash, LimboLogs.Instance);
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb000"), new byte[] { 1 });
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb111"), new byte[] { 1 });
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeb000000000000000000000000000000000000000000"), new byte[] { 1 });
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeb111111111111111111111111111111111111111111"), new byte[] { 1 });
            remoteStorageTree.Commit();

            remote.StateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            remote.StateTree.Commit();

            CompareTrees(local, remote, _logger, "BEGIN");

            await using IContainer container = PrepareDownloader(local, remote);
            SafeContext ctx = container.Resolve<SafeContext>();
            await ActivateAndWait(ctx);

            CompareTrees(local, remote, _logger, "END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Scenario_plus_one_code((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            testCase.SetupTree(remote.StateTree, remote.TrieStore, remote.CodeDb);

            remote.CodeDb.Set(Keccak.Compute(TrieScenarios.Code0), TrieScenarios.Code0);

            Account changedAccount = TrieScenarios.AccountJustState0.WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code0));
            remote.StateTree.Set(TestItem.AddressD, changedAccount);
            remote.StateTree.Commit();

            CompareTrees(local, remote, _logger, "BEGIN");

            await using IContainer container = PrepareDownloader(local, remote);
            SafeContext ctx = container.Resolve<SafeContext>();
            await ActivateAndWait(ctx);

            CompareTrees(local, remote, _logger, "END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Scenario_plus_one_code_one_storage((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            testCase.SetupTree(remote.StateTree, remote.TrieStore, remote.CodeDb);

            remote.CodeDb.Set(Keccak.Compute(TrieScenarios.Code0), TrieScenarios.Code0);

            StorageTree remoteStorageTree = new(remote.TrieStore.GetTrieStore(TestItem.AddressD), Keccak.EmptyTreeHash, _logManager);
            remoteStorageTree.Set((UInt256)1, new byte[] { 1 });
            remoteStorageTree.Commit();

            remote.StateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code0)).WithChangedStorageRoot(remoteStorageTree.RootHash));
            remote.StateTree.Commit();

            CompareTrees(local, remote, _logger, "BEGIN");

            await using IContainer container = PrepareDownloader(local, remote);
            SafeContext ctx = container.Resolve<SafeContext>();
            await ActivateAndWait(ctx);

            CompareTrees(local, remote, _logger, "END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Repeat(TestRepeatCount)]
        public async Task Scenario_plus_one_storage((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            testCase.SetupTree(remote.StateTree, remote.TrieStore, remote.CodeDb);

            StorageTree remoteStorageTree = new(remote.TrieStore.GetTrieStore(TestItem.AddressD), Keccak.EmptyTreeHash, _logManager);
            remoteStorageTree.Set((UInt256)1, new byte[] { 1 });
            remoteStorageTree.Commit();

            remote.StateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            remote.StateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            remote.StateTree.Commit();

            CompareTrees(local, remote, _logger, "BEGIN");

            await using IContainer container = PrepareDownloader(local, remote);
            SafeContext ctx = container.Resolve<SafeContext>();
            await ActivateAndWait(ctx);

            CompareTrees(local, remote, _logger, "END");
        }

        [Test]
        public async Task When_empty_response_received_return_lesser_quality()
        {
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            remote.StateTree.Set(TestItem.KeccakA, Build.An.Account.TestObject);
            remote.StateTree.Commit();

            await using IContainer container = BuildTestContainerBuilder(local, remote)
                .Build();
            SafeContext ctx = container.Resolve<SafeContext>();

            ctx.Feed.SyncModeSelectorOnChanged(SyncMode.StateNodes);
            using StateSyncBatch? request = await ctx.Feed.PrepareRequest();
            request.Should().NotBeNull();

            ctx.Feed.HandleResponse(request, new PeerInfo(Substitute.For<ISyncPeer>()))
                .Should().Be(SyncResponseHandlingResult.LesserQuality);
        }

        [Test]
        public async Task When_empty_response_received_with_no_peer_return_not_allocated()
        {
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            remote.StateTree.Set(TestItem.KeccakA, Build.An.Account.TestObject);
            remote.StateTree.Commit();

            await using IContainer container = BuildTestContainerBuilder(local, remote)
                .Build();
            SafeContext ctx = container.Resolve<SafeContext>();

            ctx.Feed.SyncModeSelectorOnChanged(SyncMode.StateNodes);
            using StateSyncBatch? request = await ctx.Feed.PrepareRequest();
            request.Should().NotBeNull();

            ctx.Feed.HandleResponse(request, peer: null)
                .Should().Be(SyncResponseHandlingResult.NotAssigned);
        }

        [Test]
        [Repeat(TestRepeatCount)]
        public async Task RepairPossiblyMissingStorage()
        {
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            remote.CodeDb[Keccak.Compute(TrieScenarios.Code0).Bytes] = TrieScenarios.Code0;
            remote.CodeDb[Keccak.Compute(TrieScenarios.Code1).Bytes] = TrieScenarios.Code1;
            remote.CodeDb[Keccak.Compute(TrieScenarios.Code2).Bytes] = TrieScenarios.Code2;
            remote.CodeDb[Keccak.Compute(TrieScenarios.Code3).Bytes] = TrieScenarios.Code3;

            Hash256 theAccount = TestItem.KeccakA;
            StorageTree storageTree = new StorageTree(remote.TrieStore.GetTrieStore(theAccount), LimboLogs.Instance);
            for (int i = 0; i < 10; i++)
            {
                storageTree.Set((UInt256)i, TestItem.Keccaks[i].BytesToArray());
            }
            storageTree.Commit();

            StateTree state = remote.StateTree;
            state.Set(TestItem.KeccakA, Build.An.Account.WithNonce(1).WithStorageRoot(storageTree.RootHash).TestObject);
            state.Set(TestItem.KeccakB, Build.An.Account.WithNonce(1).TestObject);
            state.Set(TestItem.KeccakC, Build.An.Account.WithNonce(1).TestObject);
            state.Commit();

            // Local state only have the state
            state = local.StateTree;
            state.Set(TestItem.KeccakA, Build.An.Account.WithNonce(1).WithStorageRoot(storageTree.RootHash).TestObject);
            state.Set(TestItem.KeccakB, Build.An.Account.WithNonce(1).TestObject);
            state.Set(TestItem.KeccakC, Build.An.Account.WithNonce(1).TestObject);
            state.Commit();

            // Local state missing root so that it would start
            local.NodeStorage.Set(null, TreePath.Empty, state.RootHash, null);

            await using IContainer container = PrepareDownloader(local, remote);
            container.Resolve<StateSyncPivot>().UpdatedStorages.Add(theAccount);

            SafeContext ctx = container.Resolve<SafeContext>();
            await ActivateAndWait(ctx);

            CompareTrees(local, remote, _logger, "END");
        }

        [Test]
        [Repeat(TestRepeatCount)]
        [CancelAfter(10000)]
        public async Task Pending_items_cache_mechanism_works_across_root_changes(CancellationToken cancellation)
        {
            LocalDbContext local = new(_logManager);
            RemoteDbContext remote = new(_logManager);
            remote.CodeDb[Keccak.Compute(TrieScenarios.Code0).Bytes] = TrieScenarios.Code0;
            remote.CodeDb[Keccak.Compute(TrieScenarios.Code1).Bytes] = TrieScenarios.Code1;

            // Set some data
            for (byte i = 0; i < 12; i++)
            {
                StorageTree storage = SetStorage(remote.TrieStore, (byte)(i + 1), TestItem.Addresses[i]);
                remote.StateTree.Set(
                    TestItem.Addresses[i],
                    TrieScenarios.AccountJustState0
                        .WithChangedBalance((UInt256)(i + 10))
                        .WithChangedNonce((UInt256)1)
                        .WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code0))
                        .WithChangedStorageRoot(storage.RootHash));
            }
            remote.StateTree.UpdateRootHash();
            remote.StateTree.Commit();

            await using IContainer container = PrepareDownloader(local, remote);
            SafeContext ctx = container.Resolve<SafeContext>();

            ctx.Feed.SyncModeSelectorOnChanged(SyncMode.StateNodes);

            async Task<int> RunOneRequest()
            {
                using StateSyncBatch request = (await ctx.Feed.PrepareRequest(cancellation))!;
                if (request is null) return 0;
                PeerInfo peer = new PeerInfo(ctx.SyncPeerMocks[0]);
                await ctx.Downloader.Dispatch(peer, request!, cancellation);
                int requestCount = request.RequestedNodes?.Count ?? 0;
                ctx.Feed.HandleResponse(request, peer);
                return requestCount;
            }

            int totalRequest = 0;
            for (int i = 0; i < 5; i++)
            {
                int oneCycleRequest = await RunOneRequest();
                if (oneCycleRequest == 0) break;
                totalRequest += oneCycleRequest;
            }

            for (byte i = 0; i < 4; i++)
            {
                StorageTree storage = SetStorage(remote.TrieStore, (byte)(i + 2), TestItem.Addresses[i]);
                remote.StateTree.Set(
                    TestItem.Addresses[i],
                    TrieScenarios.AccountJustState0
                        .WithChangedBalance((UInt256)(i + 100))
                        .WithChangedNonce((UInt256)2)
                        .WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code1))
                        .WithChangedStorageRoot(storage.RootHash));
            }
            remote.StateTree.UpdateRootHash();
            remote.StateTree.Commit();

            await ctx.SuggestBlocksWithUpdatedRootHash(remote.StateTree.RootHash);

            ctx.Feed.FallAsleep();
            ctx.Feed.SyncModeSelectorOnChanged(SyncMode.StateNodes);

            int remainingRequest = 0;
            for (int i = 0; i < 1000; i++)
            {
                int requestCount = await RunOneRequest();
                if (requestCount is 0) break;
                remainingRequest += requestCount;
            }

            remainingRequest.Should().Be(100); // Without the cache this would be 111
        }
    }
}
