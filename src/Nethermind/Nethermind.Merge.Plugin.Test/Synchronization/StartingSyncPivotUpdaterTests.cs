// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NSubstitute;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Test.Synchronization
{
    public class StartingSyncPivotUpdaterTests
    {
        private IBlockTree? _blockTree;
        private ISyncPeerPool? _syncPeerPool;
        private ISyncConfig? _syncConfig;
        private ISyncProgressResolver? _syncProgressResolver;
        private IBlockCacheService? _blockCacheService;
        private IBeaconSyncStrategy? _beaconSyncStrategy;
        private IDb? _metadataDb;
        private IBlockTree? _externalPeerBlockTree;
        private ISyncPeer? _syncPeer;

        [SetUp]
        public void Setup()
        {
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            TestSpecProvider specProvider = new(Cancun.Instance);
            specProvider.TerminalTotalDifficulty = 0;
            _externalPeerBlockTree = Build.A.BlockTree(genesisBlock, specProvider)
                .WithoutSettingHead
                .OfChainLength(100)
                .TestObject;

            ISyncPeer? fakePeer = Substitute.For<ISyncPeer>();
            fakePeer.GetHeadBlockHeader(default, default).ReturnsForAnyArgs(x =>
                _externalPeerBlockTree!.FindHeader(x.ArgAt<Hash256>(0), BlockTreeLookupOptions.None));

            // for unsafe pivot updater
            Hash256 pivotHash = _externalPeerBlockTree!.FindLevel(35)!.BlockInfos[0].BlockHash;
            fakePeer.GetBlockHeaders(35, 1, 0, default).ReturnsForAnyArgs(_ => FindNonNullHeaders(_externalPeerBlockTree!, pivotHash, 1, 0, default));

            NetworkNode node = new(TestItem.PublicKeyA, "127.0.0.1", 30303, 100L);
            fakePeer.Node.Returns(new Node(node));
            _syncPeer = fakePeer;

            _syncPeerPool = Substitute.For<ISyncPeerPool>();
            _syncPeerPool.InitializedPeers.Returns(new[] { new PeerInfo(fakePeer) });

            _metadataDb = new MemDb();
            _blockTree = Build.A.BlockTree()
                .WithMetadataDb(_metadataDb)
                .TestObject;
            _syncConfig = new SyncConfig()
            {
                FastSync = true,
                MaxAttemptsToUpdatePivot = 1,
                MultiSyncModeSelectorLoopTimerMs = 1
            };
            // Eligibility inputs that MultiSyncModeSelector used to gate UpdatingPivot on are now checked by the updater itself.
            _syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            _syncProgressResolver.FindBestFullState().Returns(0UL);
            _blockCacheService = new BlockCacheService();
            _beaconSyncStrategy = Substitute.For<IBeaconSyncStrategy>();
            _beaconSyncStrategy.MergeTransitionFinished.Returns(true);
        }

        private StartingSyncPivotUpdater CreateUpdater() =>
            new(_blockTree!, _syncPeerPool!, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance);

        private UnsafeStartingSyncPivotUpdater CreateUnsafeUpdater() =>
            new(_blockTree!, _syncPeerPool!, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance);

        // Real BeaconSync over an empty block cache: the only finalized hash source is the block tree.
        private StartingSyncPivotUpdater CreateUpdaterWithRealBeaconSync()
        {
            IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
            poSSwitcher.TransitionFinished.Returns(true);
            BeaconSync beaconSync = new(
                Substitute.For<IBeaconPivot>(),
                _blockTree!,
                _syncConfig!,
                _blockCacheService!,
                poSSwitcher,
                LimboLogs.Instance);

            return new StartingSyncPivotUpdater(
                _blockTree!, _syncPeerPool!, _syncConfig!, _syncProgressResolver!, _blockCacheService!, beaconSync, LimboLogs.Instance);
        }

        [Test]
        public async Task TrySetFreshPivot_saves_FinalizedHash_in_db()
        {
            Hash256 expectedFinalizedHash = _externalPeerBlockTree!.HeadHash!;
            ulong expectedPivotBlockNumber = _externalPeerBlockTree!.Head!.Number;
            _beaconSyncStrategy!.GetFinalizedHash().Returns(expectedFinalizedHash);

            await CreateUpdater().EnsureSyncPivot(default);

            byte[] storedData = _metadataDb!.Get(MetadataDbKeys.UpdatedPivotData)!;
            RlpReader ctx = new(storedData!);
            ulong storedPivotBlockNumber = ctx.DecodeULong();
            Hash256 storedFinalizedHash = ctx.DecodeKeccak()!;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(storedFinalizedHash, Is.EqualTo(expectedFinalizedHash));
                Assert.That(storedPivotBlockNumber, Is.EqualTo(expectedPivotBlockNumber));
            }
        }

        [Test]
        public async Task TrySetFreshPivot_falls_back_to_FinalizedHash_persisted_in_block_tree_when_no_FCU_received()
        {
            Hash256 persistedFinalizedHash = _externalPeerBlockTree!.HeadHash!;
            ulong expectedPivotBlockNumber = _externalPeerBlockTree!.Head!.Number;
            _blockTree!.ForkChoiceUpdated(persistedFinalizedHash, persistedFinalizedHash);

            await CreateUpdaterWithRealBeaconSync().EnsureSyncPivot(default);

            byte[]? storedData = _metadataDb!.Get(MetadataDbKeys.UpdatedPivotData);
            Assert.That(storedData, Is.Not.Null, "pivot should be updated from the persisted finalized hash without any FCU");
            RlpReader ctx = new(storedData!);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.DecodeULong(), Is.EqualTo(expectedPivotBlockNumber));
                Assert.That(ctx.DecodeKeccak(), Is.EqualTo(persistedFinalizedHash));
            }
        }

        [Test]
        public async Task TrySetFreshPivot_keeps_waiting_when_persisted_FinalizedHash_is_zero()
        {
            _syncConfig!.MaxAttemptsToUpdatePivot = ISyncConfig.InfiniteAttempts;
            _blockTree!.ForkChoiceUpdated(Keccak.Zero, Keccak.Zero);

            using CancellationTokenSource cts = new();
            Task task = CreateUpdaterWithRealBeaconSync().EnsureSyncPivot(cts.Token);
            await Task.WhenAny(task, Task.Delay(200));
            cts.Cancel();
            try { await task; } catch (OperationCanceledException) { }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_metadataDb!.Get(MetadataDbKeys.UpdatedPivotData), Is.Null,
                    "a zero finalized hash means no data, so no pivot should be set");
                Assert.That(_syncConfig!.MaxAttemptsToUpdatePivot, Is.EqualTo(ISyncConfig.InfiniteAttempts),
                    "attempts remain, so the updater must keep waiting instead of falling back to the static pivot");
            }
        }

        [Test]
        public async Task TrySetFreshPivot_ignores_zero_cached_FinalizedHash_and_falls_back_to_block_tree()
        {
            Hash256 persistedFinalizedHash = _externalPeerBlockTree!.HeadHash!;
            ulong expectedPivotBlockNumber = _externalPeerBlockTree!.Head!.Number;
            _blockTree!.ForkChoiceUpdated(persistedFinalizedHash, persistedFinalizedHash);
            _blockCacheService!.FinalizedHash = Keccak.Zero;

            await CreateUpdaterWithRealBeaconSync().EnsureSyncPivot(default);

            byte[]? storedData = _metadataDb!.Get(MetadataDbKeys.UpdatedPivotData);
            Assert.That(storedData, Is.Not.Null, "a zero cached finalized hash must not shadow the persisted one");
            RlpReader ctx = new(storedData!);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.DecodeULong(), Is.EqualTo(expectedPivotBlockNumber));
                Assert.That(ctx.DecodeKeccak(), Is.EqualTo(persistedFinalizedHash));
            }
        }

        [Test]
        public async Task Finite_attempts_fall_back_to_static_pivot_after_exhaustion()
        {
            _syncConfig!.MaxAttemptsToUpdatePivot = 2;
            // Finalized hash unset → TrySetFreshPivot returns null → counts as a failed attempt.
            _beaconSyncStrategy!.GetFinalizedHash().Returns((Hash256?)null);

            // The loop gives up on its own, so wait for it rather than for a fixed time a cold runner can overrun.
            await CreateUpdater().EnsureSyncPivot(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.That(_syncConfig.MaxAttemptsToUpdatePivot, Is.EqualTo(0));
        }

        [Test]
        public async Task Infinite_attempts_never_fall_back_to_static_pivot()
        {
            _syncConfig!.MaxAttemptsToUpdatePivot = ISyncConfig.InfiniteAttempts;
            _beaconSyncStrategy!.GetFinalizedHash().Returns((Hash256?)null);

            using CancellationTokenSource cts = new();
            Task task = CreateUpdater().EnsureSyncPivot(cts.Token);

            // The loop never completes, so cancel it.
            await Task.WhenAny(task, Task.Delay(500));
            cts.Cancel();
            try { await task; } catch (OperationCanceledException) { }

            Assert.That(_syncConfig.MaxAttemptsToUpdatePivot, Is.EqualTo(ISyncConfig.InfiniteAttempts));
        }

        [Test]
        public async Task TrySetFreshPivot_for_unsafe_updater_saves_pivot_64_blocks_behind_HeadBlockHash_in_db([Values] bool cached)
        {
            Hash256 expectedHeadBlockHash = _externalPeerBlockTree!.HeadHash!;
            ulong expectedPivotBlockNumber = _externalPeerBlockTree!.Head!.Number - 64;
            Hash256 expectedPivotBlockHash = _externalPeerBlockTree!.FindLevel(expectedPivotBlockNumber)!.BlockInfos[0].BlockHash;
            _beaconSyncStrategy!.GetHeadBlockHash().Returns(expectedHeadBlockHash);
            if (cached)
            {
                for (ulong number = expectedPivotBlockNumber; number <= _externalPeerBlockTree.Head.Number; number++)
                {
                    Block block = _externalPeerBlockTree.FindBlock(number, BlockTreeLookupOptions.None)!;
                    _blockCacheService!.TryAddBlock(block);
                }
            }

            await CreateUnsafeUpdater().EnsureSyncPivot(default);

            byte[] storedData = _metadataDb!.Get(MetadataDbKeys.UpdatedPivotData)!;
            RlpReader ctx = new(storedData!);
            ulong storedPivotBlockNumber = ctx.DecodeULong();
            Hash256 storedPivotBlockHash = ctx.DecodeKeccak()!;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(storedPivotBlockNumber, Is.EqualTo(expectedPivotBlockNumber));
                Assert.That(storedPivotBlockHash, Is.EqualTo(expectedPivotBlockHash));
            }
        }

        [Test]
        public async Task TrySetFreshPivot_for_unsafe_updater_ignores_unanchored_peer_header([Values(0UL, 5UL)] ulong numberOffset)
        {
            ulong requestedPivotNumber = _externalPeerBlockTree!.Head!.Number - 64;
            BlockHeader fabricated = Build.A.BlockHeader.WithNumber(requestedPivotNumber + numberOffset).TestObject;
            _syncPeer!.GetBlockHeaders(requestedPivotNumber, 1, 0, default)
                .ReturnsForAnyArgs(_ => new ArrayPoolList<BlockHeader>(1) { fabricated });
            _syncPeer.GetHeadBlockHeader(default, default).ReturnsForAnyArgs(call =>
            {
                BlockHeader? header = _externalPeerBlockTree.FindHeader(call.ArgAt<Hash256>(0), BlockTreeLookupOptions.None);
                return header?.Number == requestedPivotNumber ? fabricated : header;
            });

            _beaconSyncStrategy!.GetHeadBlockHash().Returns(_externalPeerBlockTree!.HeadHash!);

            await CreateUnsafeUpdater().EnsureSyncPivot(default);

            Assert.That(_metadataDb!.Get(MetadataDbKeys.UpdatedPivotData), Is.Null,
                "a self-consistent header without CL ancestry must not set the pivot");
        }

        [Test]
        public async Task Unsafe_updater_skips_fabricated_first_peer_and_persists_anchored_pivot()
        {
            ISyncPeer attacker = Substitute.For<ISyncPeer>();
            Node node = _syncPeer!.Node;
            attacker.Node.Returns(node);
            BlockHeader fabricated = Build.A.BlockHeader.WithNumber(35).TestObject;
            attacker.GetHeadBlockHeader(default, default).ReturnsForAnyArgs(call =>
                call.ArgAt<Hash256>(0) == _externalPeerBlockTree!.HeadHash ? _externalPeerBlockTree.Head!.Header : fabricated);
            attacker.GetBlockHeaders(35, 1, 0, default)
                .ReturnsForAnyArgs(_ => new ArrayPoolList<BlockHeader>(1) { fabricated });
            _syncPeerPool!.InitializedPeers.Returns([new PeerInfo(attacker), new PeerInfo(_syncPeer)]);
            _beaconSyncStrategy!.GetHeadBlockHash().Returns(_externalPeerBlockTree!.HeadHash!);

            using UnsafeStartingSyncPivotUpdater updater = CreateUnsafeUpdater();
            await updater.EnsureSyncPivot(default);

            Hash256 expectedHash = _externalPeerBlockTree.FindLevel(35)!.BlockInfos[0].BlockHash;
            IBlockTree restarted = Build.A.BlockTree().WithMetadataDb(_metadataDb!).TestObject;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_blockTree!.SyncPivot, Is.EqualTo((35UL, expectedHash)));
                Assert.That(restarted.SyncPivot, Is.EqualTo(_blockTree.SyncPivot));
                Assert.That(_syncConfig!.MaxAttemptsToUpdatePivot, Is.Zero);
            }
        }

        [Test]
        public async Task Unsafe_updater_retries_after_missing_or_broken_ancestry([Values] bool brokenLink)
        {
            _syncConfig!.MaxAttemptsToUpdatePivot = ISyncConfig.InfiniteAttempts;
            _beaconSyncStrategy!.GetHeadBlockHash().Returns(_externalPeerBlockTree!.HeadHash!);
            using CancellationTokenSource cancellation = new();
            bool unavailable = true;
            _syncPeer!.GetHeadBlockHeader(default, default).ReturnsForAnyArgs(call =>
            {
                BlockHeader? header = _externalPeerBlockTree.FindHeader(call.ArgAt<Hash256>(0), BlockTreeLookupOptions.None);
                if (unavailable && header?.Number == 98)
                {
                    cancellation.Cancel();
                    return brokenLink ? Build.A.BlockHeader.WithNumber(98).TestObject : null;
                }
                return header;
            });

            using UnsafeStartingSyncPivotUpdater updater = CreateUnsafeUpdater();
            await updater.EnsureSyncPivot(cancellation.Token);
            IBlockTree restarted = Build.A.BlockTree().WithMetadataDb(_metadataDb!).TestObject;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_metadataDb!.Get(MetadataDbKeys.UpdatedPivotData), Is.Null);
                Assert.That(_syncConfig.MaxAttemptsToUpdatePivot, Is.EqualTo(ISyncConfig.InfiniteAttempts));
                Assert.That(restarted.SyncPivot, Is.EqualTo(_blockTree!.SyncPivot));
            }

            unavailable = false;
            await updater.EnsureSyncPivot(default);
            Assert.That(_blockTree!.SyncPivot.BlockHash,
                Is.EqualTo(_externalPeerBlockTree.FindLevel(35)!.BlockInfos[0].BlockHash));
        }

        private void RestartWithStoredPivot((ulong, Hash256) stored, int configuredAttempts)
        {
            _blockTree!.SyncPivot = stored;
            _syncConfig!.MaxAttemptsToUpdatePivot = configuredAttempts;
            BlockTreeBuilder builder = Build.A.BlockTree().WithMetadataDb(_metadataDb!);
            builder.SyncConfig = _syncConfig;
            _blockTree = builder.TestObject;
            _beaconSyncStrategy!.GetHeadBlockHash().Returns(_externalPeerBlockTree!.HeadHash!);
            _beaconSyncStrategy.GetFinalizedHash().Returns(_externalPeerBlockTree.HeadHash!);
        }

        [TestCase(1, false, false, true, TestName = "Unsafe_updater_replaces_a_stored_pivot_off_the_verified_chain")]
        [TestCase(1, false, true, true, TestName = "Unsafe_updater_replaces_a_stored_pivot_with_stored_parents_and_clears_them")]
        [TestCase(1, true, false, false, TestName = "Unsafe_updater_keeps_a_stored_pivot_once_state_is_synced")]
        [TestCase(0, false, false, false, TestName = "Unsafe_updater_keeps_a_stored_pivot_when_pivot_updates_are_off")]
        public async Task Stored_pivot_is_checked_against_the_CL_head_while_no_state_is_synced(
            int configuredAttempts, bool stateSynced, bool parentsStored, bool replaced)
        {
            (ulong, Hash256) stored = (1_000_000UL, TestItem.KeccakF);
            RestartWithStoredPivot(stored, configuredAttempts);
            if (stateSynced) _syncProgressResolver!.FindBestFullState().Returns(5UL);
            if (parentsStored) _blockTree!.LowestInsertedHeader = Build.A.BlockHeader.WithNumber(999_999).TestObject;

            using UnsafeStartingSyncPivotUpdater updater = CreateUnsafeUpdater();
            await updater.EnsureSyncPivot(default);

            Hash256 verified = _externalPeerBlockTree!.FindLevel(35)!.BlockInfos[0].BlockHash;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_blockTree!.SyncPivot, Is.EqualTo(replaced ? (35UL, verified) : stored));
                Assert.That(_blockTree.LowestInsertedHeader is null, Is.EqualTo(replaced || !parentsStored));
            }
        }

        [Test]
        public async Task Unsafe_updater_keeps_a_stored_pivot_on_the_verified_chain()
        {
            Hash256 verified = _externalPeerBlockTree!.FindLevel(35)!.BlockInfos[0].BlockHash;
            RestartWithStoredPivot((35UL, verified), configuredAttempts: 1);
            BlockHeader storedParent = _externalPeerBlockTree.FindHeader(30, BlockTreeLookupOptions.None)!;
            _blockTree!.LowestInsertedHeader = storedParent;

            using UnsafeStartingSyncPivotUpdater updater = CreateUnsafeUpdater();
            await updater.EnsureSyncPivot(default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_blockTree.SyncPivot, Is.EqualTo((35UL, verified)));
                Assert.That(_blockTree.LowestInsertedHeader, Is.SameAs(storedParent));
            }
        }

        private (ulong, Hash256) StoreVerifiedChainFrom(ulong storedNumber)
        {
            for (ulong number = storedNumber; number <= 35; number++)
            {
                _blockTree!.Insert(_externalPeerBlockTree!.FindHeader(number, BlockTreeLookupOptions.None)!,
                    BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded | BlockTreeInsertHeaderOptions.NotOnMainChain);
            }

            return ExternalPivot(storedNumber);
        }

        private (ulong, Hash256) ExternalPivot(ulong number) =>
            (number, _externalPeerBlockTree!.FindHeader(number, BlockTreeLookupOptions.None)!.Hash!);

        private void ServeReverseHeaders(Action? onRequest = null) =>
            _syncPeer!.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    onRequest?.Invoke();
                    return FindNonNullHeaders(_externalPeerBlockTree!, call.ArgAt<Hash256>(0), call.ArgAt<int>(1), 0, true);
                });

        private int ReverseHeaderRequests() => _syncPeer!.ReceivedCalls().Count(static call =>
            call.GetMethodInfo().Name == nameof(ISyncPeer.GetAncestorHeaders));

        [TestCase(true, 5UL, false, TestName = "Unsafe_updater_keeps_an_ancestor_stored_pivot_with_its_descendants_in_the_block_tree")]
        [TestCase(false, 5UL, false, TestName = "Unsafe_updater_keeps_an_ancestor_stored_pivot_from_peer_headers")]
        [TestCase(true, 4UL, true, TestName = "Unsafe_updater_replaces_an_ancestor_stored_pivot_below_the_walk_limit")]
        [TestCase(false, 4UL, true, TestName = "Unsafe_updater_replaces_an_ancestor_stored_pivot_below_the_walk_limit_without_stored_descendants")]
        public async Task Unsafe_updater_walks_from_the_verified_pivot_to_an_ancestor_stored_pivot(bool descendantsStored, ulong maxWalk, bool replaced)
        {
            (ulong, Hash256) stored = ExternalPivot(30);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            if (descendantsStored) StoreVerifiedChainFrom(31);
            ServeReverseHeaders();
            _syncPeer!.ClearReceivedCalls();

            using UnsafeStartingSyncPivotUpdater updater = new(_blockTree!, _syncPeerPool!, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance)
            {
                MaxStoredPivotAncestorWalk = maxWalk
            };
            await updater.EnsureSyncPivot(default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_blockTree!.SyncPivot, Is.EqualTo(replaced ? ExternalPivot(35) : stored));
                Assert.That(_syncConfig!.MaxAttemptsToUpdatePivot, Is.Zero);
                Assert.That(ReverseHeaderRequests(), Is.EqualTo(replaced || descendantsStored ? 1 : 2), "one request resolves the verified pivot, one walks below it");
            }
        }

        [Test]
        public async Task Unsafe_updater_replaces_a_stored_pivot_when_peer_headers_prove_another_block_at_its_number()
        {
            (ulong, Hash256) stored = (30UL, TestItem.KeccakF);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            ServeReverseHeaders();

            using UnsafeStartingSyncPivotUpdater updater = CreateUnsafeUpdater();
            await updater.EnsureSyncPivot(default);

            Assert.That(_blockTree!.SyncPivot, Is.EqualTo(ExternalPivot(35)));
        }

        [Test]
        public async Task Unsafe_updater_keeps_the_stored_pivot_and_retries_while_peers_do_not_serve_its_ancestors()
        {
            (ulong, Hash256) stored = ExternalPivot(30);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            using CancellationTokenSource cts = new();
            int requests = 0;
            _syncPeer!.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (call.ArgAt<Hash256>(0) == _externalPeerBlockTree!.HeadHash)
                        return FindNonNullHeaders(_externalPeerBlockTree, call.ArgAt<Hash256>(0), call.ArgAt<int>(1), 0, true);
                    if (++requests == 3) cts.Cancel();
                    return ArrayPoolList<BlockHeader>.Empty();
                });

            using UnsafeStartingSyncPivotUpdater updater = CreateUnsafeUpdater();
            await updater.EnsureSyncPivot(cts.Token);
            (ulong, Hash256) afterRetries = _blockTree!.SyncPivot;
            ServeReverseHeaders();
            await updater.EnsureSyncPivot(default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(requests, Is.EqualTo(3));
                Assert.That(afterRetries, Is.EqualTo(stored));
                Assert.That(_blockTree.SyncPivot, Is.EqualTo(stored));
                Assert.That(_syncConfig!.MaxAttemptsToUpdatePivot, Is.Zero);
            }
        }

        [Test]
        public async Task Unsafe_updater_checks_the_stored_pivot_again_after_a_walk_is_canceled()
        {
            (ulong, Hash256) stored = ExternalPivot(30);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            StoreVerifiedChainFrom(31);
            IBlockTree storedTree = _blockTree!;
            using CancellationTokenSource cts = new();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.StoredPivotDisabledUpdater.Returns(storedTree.StoredPivotDisabledUpdater);
            blockTree.SyncPivot.Returns(stored);
            blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(x =>
            {
                cts.Cancel();
                return storedTree.FindHeader(x.ArgAt<Hash256>(0), x.ArgAt<BlockTreeLookupOptions>(1), x.ArgAt<ulong?>(2));
            });

            using UnsafeStartingSyncPivotUpdater updater = new(blockTree, _syncPeerPool!, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance);
            await updater.EnsureSyncPivot(cts.Token);
            int attemptsAfterCancel = _syncConfig!.MaxAttemptsToUpdatePivot;
            await updater.EnsureSyncPivot(default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(storedTree.StoredPivotDisabledUpdater, Is.True);
                Assert.That(attemptsAfterCancel, Is.EqualTo(ISyncConfig.InfiniteAttempts));
                Assert.That(_syncConfig.MaxAttemptsToUpdatePivot, Is.Zero);
                blockTree.DidNotReceive().SyncPivot = Arg.Any<(ulong, Hash256)>();
                blockTree.DidNotReceive().ReplaceSyncPivot(Arg.Any<(ulong, Hash256)>());
            }
        }

        private ISyncPeer PeerServingOneHeaderPerRequest(List<Hash256> walkRequests)
        {
            ISyncPeer peer = Substitute.For<ISyncPeer>();
            peer.Node.Returns(_syncPeer!.Node);
            peer.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Hash256 start = call.ArgAt<Hash256>(0);
                    if (start == _externalPeerBlockTree!.HeadHash) return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(null);
                    walkRequests.Add(start);
                    return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(FindNonNullHeaders(_externalPeerBlockTree, start, 1, 0, true));
                });
            return peer;
        }

        [Test]
        public async Task Unsafe_updater_asks_a_peer_that_serves_one_header_at_a_time_last()
        {
            (ulong, Hash256) stored = ExternalPivot(25);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            ServeReverseHeaders();
            List<Hash256> drippedRequests = [];
            ISyncPeer dripping = PeerServingOneHeaderPerRequest(drippedRequests);
            _syncPeerPool!.InitializedPeers.Returns([new PeerInfo(dripping), new PeerInfo(_syncPeer!)]);

            using UnsafeStartingSyncPivotUpdater updater = new(_blockTree!, _syncPeerPool, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance)
            {
                AncestorHeadersPerRequest = 4
            };
            await updater.EnsureSyncPivot(default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_blockTree!.SyncPivot, Is.EqualTo(stored));
                Assert.That(drippedRequests, Has.Count.EqualTo(1), "after one short batch the peer is asked last");
            }
        }

        [Test]
        public async Task Unsafe_updater_leaves_the_stored_pivot_undecided_when_the_walk_runs_out_of_time()
        {
            (ulong, Hash256) stored = ExternalPivot(30);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            using CancellationTokenSource cts = new();
            int headRequests = 0;
            _syncPeer!.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Hash256 start = call.ArgAt<Hash256>(0);
                    if (start == _externalPeerBlockTree!.HeadHash && ++headRequests == 3) cts.Cancel();
                    return FindNonNullHeaders(_externalPeerBlockTree, start, call.ArgAt<int>(1), 0, true);
                });

            using UnsafeStartingSyncPivotUpdater updater = new(_blockTree!, _syncPeerPool!, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance)
            {
                MaxStoredPivotWalkTime = TimeSpan.Zero
            };
            await updater.EnsureSyncPivot(cts.Token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(headRequests, Is.EqualTo(3));
                Assert.That(_blockTree!.SyncPivot, Is.EqualTo(stored));
                Assert.That(_syncConfig!.MaxAttemptsToUpdatePivot, Is.EqualTo(ISyncConfig.InfiniteAttempts));
            }
        }

        [Test]
        public async Task Unsafe_updater_keeps_checking_the_stored_pivot_after_an_unexpected_error([Values] bool disposed)
        {
            (ulong, Hash256) stored = ExternalPivot(30);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            StoreVerifiedChainFrom(31);
            IBlockTree storedTree = _blockTree!;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.StoredPivotDisabledUpdater.Returns(storedTree.StoredPivotDisabledUpdater);
            blockTree.SyncPivot.Returns(stored);
            int lookups = 0;
            blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(x =>
                ++lookups == 1
                    ? throw (disposed ? new ObjectDisposedException("lookup") : new InvalidOperationException("lookup failed"))
                    : storedTree.FindHeader(x.ArgAt<Hash256>(0), x.ArgAt<BlockTreeLookupOptions>(1), x.ArgAt<ulong?>(2)));

            using UnsafeStartingSyncPivotUpdater updater = new(blockTree, _syncPeerPool!, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance);
            await updater.EnsureSyncPivot(default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(lookups, Is.GreaterThan(1));
                Assert.That(_syncConfig!.MaxAttemptsToUpdatePivot, Is.Zero);
                blockTree.DidNotReceive().SyncPivot = Arg.Any<(ulong, Hash256)>();
                blockTree.DidNotReceive().ReplaceSyncPivot(Arg.Any<(ulong, Hash256)>());
            }
        }

        [Test]
        public async Task Unsafe_updater_continues_the_walk_where_the_last_attempt_stopped()
        {
            (ulong, Hash256) stored = ExternalPivot(30);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            using CancellationTokenSource cts = new();
            List<Hash256> walkRequests = [];
            bool serve = false;
            _syncPeer!.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Hash256 start = call.ArgAt<Hash256>(0);
                    if (start != _externalPeerBlockTree!.HeadHash)
                    {
                        walkRequests.Add(start);
                        if (!serve && walkRequests.Count == 3)
                        {
                            cts.Cancel();
                            return ArrayPoolList<BlockHeader>.Empty();
                        }
                    }

                    return FindNonNullHeaders(_externalPeerBlockTree, start, call.ArgAt<int>(1), 0, true);
                });

            using UnsafeStartingSyncPivotUpdater updater = new(_blockTree!, _syncPeerPool!, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance)
            {
                AncestorHeadersPerRequest = 2
            };
            await updater.EnsureSyncPivot(cts.Token);
            serve = true;
            walkRequests.Clear();
            await updater.EnsureSyncPivot(default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_blockTree!.SyncPivot, Is.EqualTo(stored));
                Assert.That(walkRequests, Is.EqualTo(new[] { ExternalPivot(31).Item2 }), "the retry starts below the blocks proven before");
            }
        }

        [Test]
        public async Task Unsafe_updater_continues_the_walk_after_the_verified_pivot_moved()
        {
            (ulong, Hash256) stored = ExternalPivot(25);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            Hash256 firstHead = _externalPeerBlockTree!.FindHeader(98, BlockTreeLookupOptions.None)!.Hash!;
            _beaconSyncStrategy!.GetHeadBlockHash().Returns(firstHead);
            using CancellationTokenSource cts = new();
            List<Hash256> walkRequests = [];
            bool serve = false;
            _syncPeer!.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Hash256 start = call.ArgAt<Hash256>(0);
                    if (start != firstHead && start != _externalPeerBlockTree.HeadHash)
                    {
                        walkRequests.Add(start);
                        if (!serve && walkRequests.Count == 3)
                        {
                            cts.Cancel();
                            return ArrayPoolList<BlockHeader>.Empty();
                        }
                    }

                    return FindNonNullHeaders(_externalPeerBlockTree, start, call.ArgAt<int>(1), 0, true);
                });

            using UnsafeStartingSyncPivotUpdater updater = new(_blockTree!, _syncPeerPool!, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy, LimboLogs.Instance)
            {
                AncestorHeadersPerRequest = 2
            };
            await updater.EnsureSyncPivot(cts.Token);
            serve = true;
            walkRequests.Clear();
            _beaconSyncStrategy.GetHeadBlockHash().Returns(_externalPeerBlockTree.HeadHash!);
            await updater.EnsureSyncPivot(default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_blockTree!.SyncPivot, Is.EqualTo(stored));
                Assert.That(walkRequests.Take(2), Is.EqualTo(new[] { ExternalPivot(35).Item2, ExternalPivot(30).Item2 }),
                    "from the new pivot the walk reaches the old one and continues below the blocks proven before");
            }
        }

        [Test]
        public async Task Unsafe_updater_asks_a_peer_that_serves_whole_batches_slowly_last()
        {
            (ulong, Hash256) stored = ExternalPivot(25);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            ServeReverseHeaders();
            ManualTimeProvider clock = new();
            List<Hash256> slowRequests = [];
            ISyncPeer slow = Substitute.For<ISyncPeer>();
            slow.Node.Returns(_syncPeer!.Node);
            slow.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Hash256 start = call.ArgAt<Hash256>(0);
                    if (start == _externalPeerBlockTree!.HeadHash) return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(null);
                    slowRequests.Add(start);
                    clock.Advance(TimeSpan.FromSeconds(5));
                    return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(FindNonNullHeaders(_externalPeerBlockTree, start, call.ArgAt<int>(1), 0, true));
                });
            _syncPeerPool!.InitializedPeers.Returns([new PeerInfo(slow), new PeerInfo(_syncPeer!)]);

            using UnsafeStartingSyncPivotUpdater updater = new(_blockTree!, _syncPeerPool, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance)
            {
                AncestorHeadersPerRequest = 4,
                AncestryTimeProvider = clock
            };
            await updater.EnsureSyncPivot(default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_blockTree!.SyncPivot, Is.EqualTo(stored));
                Assert.That(slowRequests, Has.Count.EqualTo(1), "after one slow batch the peer is asked last");
            }
        }

        [Test]
        public async Task Unsafe_updater_keeps_the_older_walk_segment_until_a_later_attempt_joins_it()
        {
            (ulong, Hash256) stored = ExternalPivot(20);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            Hash256 head96 = _externalPeerBlockTree!.FindHeader(96, BlockTreeLookupOptions.None)!.Hash!;
            Hash256 head99 = _externalPeerBlockTree.HeadHash!;
            _beaconSyncStrategy!.GetHeadBlockHash().Returns(head96);
            CancellationTokenSource cts = new();
            List<Hash256> walkRequests = [];
            int failAt = 3;
            _syncPeer!.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Hash256 start = call.ArgAt<Hash256>(0);
                    if (start != head96 && start != head99)
                    {
                        walkRequests.Add(start);
                        if (walkRequests.Count == failAt)
                        {
                            cts.Cancel();
                            return ArrayPoolList<BlockHeader>.Empty();
                        }
                    }

                    return FindNonNullHeaders(_externalPeerBlockTree, start, call.ArgAt<int>(1), 0, true);
                });

            using UnsafeStartingSyncPivotUpdater updater = new(_blockTree!, _syncPeerPool!, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy, LimboLogs.Instance)
            {
                AncestorHeadersPerRequest = 2
            };
            await updater.EnsureSyncPivot(cts.Token);

            _beaconSyncStrategy.GetHeadBlockHash().Returns(head99);
            walkRequests.Clear();
            failAt = 2;
            cts.Dispose();
            cts = new CancellationTokenSource();
            await updater.EnsureSyncPivot(cts.Token);

            walkRequests.Clear();
            failAt = 0;
            await updater.EnsureSyncPivot(default);
            cts.Dispose();

            Hash256[] expected = [.. new ulong[] { 33, 28, 26, 24, 22 }.Select(n => ExternalPivot(n).Item2)];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_blockTree!.SyncPivot, Is.EqualTo(stored));
                Assert.That(walkRequests, Is.EqualTo(expected), "the third attempt skips both previously verified segments");
            }
        }

        [Test]
        public async Task Unsafe_updater_checks_the_stored_pivot_again_when_replacing_it_fails()
        {
            (ulong, Hash256) stored = (1_000_000UL, TestItem.KeccakF);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            IBlockTree storedTree = _blockTree!;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.StoredPivotDisabledUpdater.Returns(storedTree.StoredPivotDisabledUpdater);
            blockTree.SyncPivot.Returns(stored);
            int writes = 0;
            blockTree.When(static tree => tree.ReplaceSyncPivot(Arg.Any<(ulong, Hash256)>())).Do(_ =>
            {
                if (++writes == 1) throw new InvalidOperationException("write failed");
            });

            using UnsafeStartingSyncPivotUpdater updater = new(blockTree, _syncPeerPool!, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance);
            await updater.EnsureSyncPivot(default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(writes, Is.EqualTo(2), "the failed replacement is tried again");
                Assert.That(_syncConfig!.MaxAttemptsToUpdatePivot, Is.Zero);
            }
        }

        [Test]
        public async Task Finalized_updater_keeps_a_stored_pivot()
        {
            (ulong, Hash256) stored = (1_000_000UL, TestItem.KeccakF);
            RestartWithStoredPivot(stored, configuredAttempts: 1);

            using StartingSyncPivotUpdater updater = CreateUpdater();
            await updater.EnsureSyncPivot(default);

            Assert.That(_blockTree!.SyncPivot, Is.EqualTo(stored));
        }

        private sealed class MetadataDbStoppingBeforeLoneMarkerWrite : MemDb
        {
            private static readonly byte[] MarkerKey = [MetadataDbKeys.LowestInsertedFastHeaderHash];
            private bool _committingBatch;

            public bool Stopping { get; set; }
            public bool FailStaging { get; set; }

            public override IWriteBatch StartWriteBatch() => new Batch(this);

            public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
            {
                if (Stopping && !_committingBatch && key.SequenceEqual(MarkerKey)) throw new IOException("process stopped");
                base.Set(key, value, flags);
            }

            private sealed class Batch(MetadataDbStoppingBeforeLoneMarkerWrite db) : IWriteBatch
            {
                private readonly InMemoryWriteBatch _writes = new(db);

                public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
                {
                    if (db.FailStaging && key.SequenceEqual(MarkerKey)) throw new IOException("marker staging failed");
                    _writes.Set(key, value, flags);
                }

                public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => _writes.Merge(key, value, flags);

                public void Clear() => _writes.Clear();

                public void Dispose()
                {
                    db._committingBatch = true;
                    try
                    {
                        _writes.Dispose();
                    }
                    finally
                    {
                        db._committingBatch = false;
                    }
                }
            }
        }

        [Test]
        public void Failed_marker_staging_keeps_the_original_pivot_and_header()
        {
            MetadataDbStoppingBeforeLoneMarkerWrite db = new();
            BlockTree tree = Build.A.BlockTree().WithMetadataDb(db).TestObject;
            tree.SyncPivot = (20UL, TestItem.KeccakF);
            tree.LowestInsertedHeader = Build.A.BlockHeader.WithNumber(19).TestObject;
            (ulong, Hash256) originalPivot = tree.SyncPivot;
            BlockHeader? originalHeader = tree.LowestInsertedHeader;
            byte[]? pivotData = db.Get(MetadataDbKeys.UpdatedPivotData);
            byte[]? marker = db.Get(MetadataDbKeys.LowestInsertedFastHeaderHash);
            db.FailStaging = true;

            Assert.Throws<IOException>(() => tree.ReplaceSyncPivot(ExternalPivot(35)));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tree.SyncPivot, Is.EqualTo(originalPivot));
                Assert.That(tree.LowestInsertedHeader, Is.SameAs(originalHeader));
                Assert.That(db.Get(MetadataDbKeys.UpdatedPivotData), Is.EqualTo(pivotData));
                Assert.That(db.Get(MetadataDbKeys.LowestInsertedFastHeaderHash), Is.EqualTo(marker));
            }
        }

        [Test]
        public async Task Unsafe_updater_writes_the_replaced_pivot_and_the_cleared_header_marker_together()
        {
            MetadataDbStoppingBeforeLoneMarkerWrite metadataDb = new();
            _metadataDb = metadataDb;
            _blockTree = Build.A.BlockTree().WithMetadataDb(_metadataDb).TestObject;
            RestartWithStoredPivot((1_000_000UL, TestItem.KeccakF), configuredAttempts: 1);
            _blockTree!.LowestInsertedHeader = Build.A.BlockHeader.WithNumber(999_999).TestObject;
            metadataDb.Stopping = true;

            using UnsafeStartingSyncPivotUpdater updater = CreateUnsafeUpdater();
            await updater.EnsureSyncPivot(default);

            BlockTreeBuilder builder = Build.A.BlockTree().WithMetadataDb(_metadataDb);
            builder.SyncConfig = new SyncConfig { FastSync = true, MaxAttemptsToUpdatePivot = 1 };
            IBlockTree restarted = builder.TestObject;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(restarted.SyncPivot, Is.EqualTo(ExternalPivot(35)));
                Assert.That(restarted.LowestInsertedHeader, Is.Null);
                Assert.That(_blockTree.SyncPivot, Is.EqualTo(ExternalPivot(35)));
                Assert.That(_syncConfig!.MaxAttemptsToUpdatePivot, Is.Zero);
            }
        }

        [Test]
        public void Restart_restores_header_progress_proved_against_the_replacement_pivot([Values] bool descendant)
        {
            IDb blocks = new MemDb();
            IDb headers = new MemDb();
            IDb blockNumbers = new MemDb();
            IDb blockInfos = new MemDb();
            BlockTree NewTree(bool initialize = false)
            {
                BlockTreeBuilder builder = Build.A.BlockTree().WithoutSettingHead
                    .WithMetadataDb(_metadataDb!)
                    .WithBlocksDb(blocks)
                    .WithHeadersDb(headers)
                    .WithBlocksNumberDb(blockNumbers)
                    .WithBlockInfoDb(blockInfos);
                builder.SyncConfig = new SyncConfig { FastSync = true };
                if (initialize) builder.OfChainLength(1);
                return builder.TestObject;
            }

            BlockTree tree = NewTree(initialize: true);
            (ulong number, Hash256 hash) = ExternalPivot(35);
            tree.ReplaceSyncPivot((number, hash));
            BlockHeader pivot = _externalPeerBlockTree!.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!;
            Block branchBody = _externalPeerBlockTree.FindBlock(36, BlockTreeLookupOptions.None)!;
            BlockHeader candidate = descendant
                ? _externalPeerBlockTree.FindHeader(37, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!
                : Build.A.BlockHeader.WithNumber(37).WithParentHash(TestItem.KeccakE).TestObject;
            const BlockTreeInsertHeaderOptions options = BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded | BlockTreeInsertHeaderOptions.NotOnMainChain;
            tree.Insert(pivot, options);
            tree.Insert(branchBody, BlockTreeInsertBlockOptions.SaveHeader, options);
            Block sibling = Build.A.Block.WithNumber(37).WithParentHash(TestItem.KeccakF).TestObject;
            tree.Insert(sibling, BlockTreeInsertBlockOptions.SaveHeader, options);
            tree.Insert(candidate, options);

            BlockTree restarted = NewTree();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(restarted.BestSuggestedHeader?.Hash, Is.EqualTo(descendant ? candidate.Hash : pivot.Hash));
                Assert.That(restarted.BestKnownNumber, Is.EqualTo(descendant ? candidate.Number : pivot.Number));
                Assert.That(restarted.BestSuggestedBody?.Hash, Is.EqualTo(descendant ? branchBody.Hash : restarted.Head?.Hash));
            }
        }

        [Test]
        public async Task Unsafe_updater_moves_the_best_suggested_header_off_the_replaced_pivot_branch()
        {
            RestartWithStoredPivot((1_000_000UL, TestItem.KeccakF), configuredAttempts: 1);
            BlockHeader rejected = Build.A.BlockHeader.WithNumber(1_000_000).WithParentHash(TestItem.KeccakE).TestObject;
            _blockTree!.Insert(rejected, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded | BlockTreeInsertHeaderOptions.NotOnMainChain);
            _blockTree.LowestInsertedHeader = rejected;
            BlockHeader? head = _blockTree.Head?.Header;

            using UnsafeStartingSyncPivotUpdater updater = CreateUnsafeUpdater();
            await updater.EnsureSyncPivot(default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_blockTree.SyncPivot, Is.EqualTo(ExternalPivot(35)));
                Assert.That(_blockTree.BestSuggestedHeader, Is.SameAs(head));
                Assert.That(_blockTree.BestKnownNumber, Is.EqualTo(head?.Number ?? 0));
            }
        }

        [Test]
        public async Task Unsafe_updater_drops_ancestry_scores_of_peers_that_left()
        {
            (ulong, Hash256) stored = ExternalPivot(25);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            using CancellationTokenSource cts = new();
            int walkRequests = 0;
            _syncPeer!.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Hash256 start = call.ArgAt<Hash256>(0);
                    if (start != _externalPeerBlockTree!.HeadHash && ++walkRequests == 4) cts.Cancel();
                    return FindNonNullHeaders(_externalPeerBlockTree!, start, call.ArgAt<int>(1), 0, true);
                });
            _syncPeerPool!.InitializedPeers.Returns(_ => [new PeerInfo(_syncPeer)]);

            using UnsafeStartingSyncPivotUpdater updater = new(_blockTree!, _syncPeerPool, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance)
            {
                AncestorHeadersPerRequest = 2
            };
            await updater.EnsureSyncPivot(cts.Token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(walkRequests, Is.EqualTo(4));
                Assert.That(updater.AncestryPeerScores, Is.EqualTo(1), "each request went to a new session; only the live one keeps a score");
            }
        }

        [Test]
        public async Task Unsafe_updater_keeps_the_linked_part_of_a_batch_when_the_walk_runs_out_of_time()
        {
            (ulong, Hash256) stored = ExternalPivot(25);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            WalkDeadlineTimeProvider clock = new();
            using CancellationTokenSource cts = new();
            ISyncPeer partial = Substitute.For<ISyncPeer>();
            partial.Node.Returns(_syncPeer!.Node);
            partial.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(
                    FindNonNullHeaders(_externalPeerBlockTree!, call.ArgAt<Hash256>(0), call.ArgAt<Hash256>(0) == _externalPeerBlockTree!.HeadHash ? call.ArgAt<int>(1) : 1, 0, true)));
            List<Hash256> stalledRequests = [];
            ISyncPeer stalled = Substitute.For<ISyncPeer>();
            stalled.Node.Returns(_syncPeer.Node);
            stalled.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Hash256 start = call.ArgAt<Hash256>(0);
                    if (start == _externalPeerBlockTree!.HeadHash)
                        return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(FindNonNullHeaders(_externalPeerBlockTree, start, call.ArgAt<int>(1), 0, true));
                    stalledRequests.Add(start);
                    if (stalledRequests.Count == 1) clock.ExpireWalk();
                    else cts.Cancel();
                    return Task.FromCanceled<IOwnedReadOnlyList<BlockHeader>?>(call.ArgAt<CancellationToken>(2));
                });
            _syncPeerPool!.InitializedPeers.Returns([new PeerInfo(partial), new PeerInfo(stalled)]);

            using UnsafeStartingSyncPivotUpdater updater = new(_blockTree!, _syncPeerPool, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance)
            {
                AncestryTimeProvider = clock,
                MaxStoredPivotWalkTime = WalkDeadlineTimeProvider.WalkTimeout
            };
            await updater.EnsureSyncPivot(cts.Token);

            Assert.That(stalledRequests, Is.EqualTo(new[] { ExternalPivot(35).Item2, ExternalPivot(34).Item2 }),
                "the next attempt starts below the header the partial peer linked before the walk ran out of time");
        }

        [Test]
        public async Task Unsafe_updater_asks_a_peer_that_served_a_whole_batch_before_new_sessions()
        {
            (ulong, Hash256) stored = ExternalPivot(25);
            RestartWithStoredPivot(stored, configuredAttempts: 1);
            ManualTimeProvider clock = new();
            _syncPeer!.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    clock.Advance(TimeSpan.FromSeconds(1));
                    return FindNonNullHeaders(_externalPeerBlockTree!, call.ArgAt<Hash256>(0), call.ArgAt<int>(1), 0, true);
                });
            List<Hash256> freshRequests = [];
            ISyncPeer fresh = Substitute.For<ISyncPeer>();
            fresh.Node.Returns(_syncPeer.Node);
            fresh.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Hash256 start = call.ArgAt<Hash256>(0);
                    if (start == _externalPeerBlockTree!.HeadHash) return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(null);
                    freshRequests.Add(start);
                    return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(ArrayPoolList<BlockHeader>.Empty());
                });
            PeerInfo responsive = new(_syncPeer);
            _syncPeerPool!.InitializedPeers.Returns(_ => [new PeerInfo(fresh), responsive]);

            using UnsafeStartingSyncPivotUpdater updater = new(_blockTree!, _syncPeerPool, _syncConfig!, _syncProgressResolver!, _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance)
            {
                AncestorHeadersPerRequest = 2,
                AncestryTimeProvider = clock
            };
            await updater.EnsureSyncPivot(default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_blockTree!.SyncPivot, Is.EqualTo(stored));
                Assert.That(freshRequests, Has.Count.EqualTo(1), "a new session is asked only before a peer served a whole batch");
            }
        }

        [Test]
        public async Task Stored_pivot_walk_resumes_with_the_next_peer_after_the_deadline()
        {
            WalkDeadlineTimeProvider clock = new();
            ISyncPeer timedOut = Substitute.For<ISyncPeer>();
            timedOut.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    clock.ExpireWalk();
                    return Task.FromCanceled<IOwnedReadOnlyList<BlockHeader>?>(call.ArgAt<CancellationToken>(2));
                });
            ServeReverseHeaders();
            PeerInfo livePeer = new(_syncPeer!);
            _syncPeerPool!.InitializedPeers.Returns(_ => [new PeerInfo(timedOut), livePeer]);

            using UnsafeStartingSyncPivotUpdater updater = new(_blockTree!, _syncPeerPool, _syncConfig!, _syncProgressResolver!,
                _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance)
            {
                AncestryTimeProvider = clock,
                MaxStoredPivotWalkTime = WalkDeadlineTimeProvider.WalkTimeout
            };
            (ulong storedNumber, Hash256 storedHash) = ExternalPivot(25);
            (ulong verifiedNumber, Hash256 verifiedHash) = ExternalPivot(35);

            StartingSyncPivotUpdater.StoredPivotCheck first = await updater.CheckStoredPivot(storedHash, storedNumber, verifiedHash, verifiedNumber, default);
            StartingSyncPivotUpdater.StoredPivotCheck second = await updater.CheckStoredPivot(storedHash, storedNumber, verifiedHash, verifiedNumber, default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(first, Is.EqualTo(StartingSyncPivotUpdater.StoredPivotCheck.Undecided));
                Assert.That(second, Is.EqualTo(StartingSyncPivotUpdater.StoredPivotCheck.OnVerifiedChain));
            }
        }

        [Test]
        public async Task Stored_pivot_walk_resumes_after_a_local_header_read_expires_the_deadline([Values] bool advances)
        {
            WalkDeadlineTimeProvider clock = new();
            IBlockTree localTree = Substitute.For<IBlockTree>();
            List<ulong> reads = [];
            localTree.FindHeader(Arg.Any<Hash256>(), BlockTreeLookupOptions.TotalDifficultyNotNeeded, Arg.Any<ulong?>())
                .Returns(call =>
                {
                    ulong number = call.Arg<ulong?>()!.Value;
                    reads.Add(number);
                    if (reads.Count == 3 || (advances && reads.Count is 5 or 7)) clock.ExpireWalk();
                    return _externalPeerBlockTree!.FindHeader(call.Arg<Hash256>(), BlockTreeLookupOptions.TotalDifficultyNotNeeded, number);
                });
            using UnsafeStartingSyncPivotUpdater updater = new(localTree, _syncPeerPool!, _syncConfig!, _syncProgressResolver!,
                _blockCacheService!, _beaconSyncStrategy!, LimboLogs.Instance)
            {
                AncestryTimeProvider = clock,
                MaxStoredPivotWalkTime = WalkDeadlineTimeProvider.WalkTimeout
            };
            (ulong storedNumber, Hash256 storedHash) = ExternalPivot(25);
            (ulong verifiedNumber, Hash256 verifiedHash) = ExternalPivot(35);

            StartingSyncPivotUpdater.StoredPivotCheck first = await updater.CheckStoredPivot(storedHash, storedNumber, verifiedHash, verifiedNumber, default);
            if (advances)
            {
                (verifiedNumber, verifiedHash) = ExternalPivot(40);
                for (int i = 0; i < 2; i++)
                {
                    StartingSyncPivotUpdater.StoredPivotCheck timedOut = await updater.CheckStoredPivot(storedHash, storedNumber, verifiedHash, verifiedNumber, default);
                    Assert.That(timedOut, Is.EqualTo(StartingSyncPivotUpdater.StoredPivotCheck.Undecided));
                }
            }
            StartingSyncPivotUpdater.StoredPivotCheck finished = await updater.CheckStoredPivot(storedHash, storedNumber, verifiedHash, verifiedNumber, default);

            ulong[] expected = advances
                ? [35, 34, 33, 40, 39, 38, 37, 36, 32, 31, 30, 29, 28, 27, 26]
                : [35, 34, 33, 32, 31, 30, 29, 28, 27, 26];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(first, Is.EqualTo(StartingSyncPivotUpdater.StoredPivotCheck.Undecided));
                Assert.That(finished, Is.EqualTo(StartingSyncPivotUpdater.StoredPivotCheck.OnVerifiedChain));
                Assert.That(reads, Is.EqualTo(expected));
            }
        }

        private sealed class WalkDeadlineTimeProvider : TimeProvider
        {
            internal static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(1);
            private Action? _expireWalk;

            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == WalkTimeout) _expireWalk = () => callback(state);
                return base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, period);
            }

            public void ExpireWalk() => _expireWalk!();
        }

        private static IOwnedReadOnlyList<BlockHeader> FindNonNullHeaders(IBlockTree blockTree, Hash256 hash, int numberOfBlocks, int skip, bool reverse)
        {
            using IOwnedReadOnlyList<BlockHeader> headers = blockTree.FindHeaders(hash, numberOfBlocks, skip, reverse);
            ArrayPoolList<BlockHeader> result = [with(headers.Count)];
            for (int i = 0; i < headers.Count; i++)
            {
                result.Add(headers[i]);
            }

            return result;
        }
    }
}
