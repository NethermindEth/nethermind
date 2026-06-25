// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NSubstitute;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
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
            fakePeer.GetHeadBlockHeader(default, default).ReturnsForAnyArgs(x => _externalPeerBlockTree!.Head!.Header);

            // for unsafe pivot updater
            Hash256 pivotHash = _externalPeerBlockTree!.FindLevel(35)!.BlockInfos[0].BlockHash;
            fakePeer.GetBlockHeaders(35, 1, 0, default).ReturnsForAnyArgs(x => _externalPeerBlockTree!.FindHeaders(pivotHash, 1, 0, default));

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

        [Test]
        public async Task TrySetFreshPivot_saves_FinalizedHash_in_db()
        {
            Hash256 expectedFinalizedHash = _externalPeerBlockTree!.HeadHash;
            ulong expectedPivotBlockNumber = _externalPeerBlockTree!.Head!.Number;
            _beaconSyncStrategy!.GetFinalizedHash().Returns(expectedFinalizedHash);

            await CreateUpdater().EnsureSyncPivot(default);

            byte[] storedData = _metadataDb!.Get(MetadataDbKeys.UpdatedPivotData)!;
            RlpReader ctx = new(storedData!);
            ulong storedPivotBlockNumber = ctx.DecodeULong();
            Hash256 storedFinalizedHash = ctx.DecodeKeccak()!;

            Assert.That(storedFinalizedHash, Is.EqualTo(expectedFinalizedHash));
            Assert.That(storedPivotBlockNumber, Is.EqualTo(expectedPivotBlockNumber));
        }

        [TestCase(2, 0, TestName = "Finite_attempts_fall_back_to_static_pivot_after_exhaustion")]
        [TestCase(ISyncConfig.InfiniteAttempts, ISyncConfig.InfiniteAttempts, TestName = "Infinite_attempts_never_fall_back_to_static_pivot")]
        public async Task TrySetFreshPivot_fallback_respects_MaxAttemptsToUpdatePivot(int maxAttempts, int expectedFinalConfigValue)
        {
            _syncConfig!.MaxAttemptsToUpdatePivot = maxAttempts;
            // Finalized hash unset → TrySetFreshPivot returns null → counts as a failed attempt.
            _beaconSyncStrategy!.GetFinalizedHash().Returns((Hash256?)null);

            using CancellationTokenSource cts = new();
            Task task = CreateUpdater().EnsureSyncPivot(cts.Token);

            // Finite attempts: the loop gives up on its own. Infinite attempts: it never completes, so cancel it.
            await Task.WhenAny(task, Task.Delay(500));
            cts.Cancel();
            try { await task; } catch (OperationCanceledException) { }

            Assert.That(_syncConfig.MaxAttemptsToUpdatePivot, Is.EqualTo(expectedFinalConfigValue));
        }

        [Test]
        public async Task TrySetFreshPivot_for_unsafe_updater_saves_pivot_64_blocks_behind_HeadBlockHash_in_db()
        {
            Hash256 expectedHeadBlockHash = _externalPeerBlockTree!.HeadHash;
            ulong expectedPivotBlockNumber = _externalPeerBlockTree!.Head!.Number - 64;
            Hash256 expectedPivotBlockHash = _externalPeerBlockTree!.FindLevel(expectedPivotBlockNumber)!.BlockInfos[0].BlockHash;
            _beaconSyncStrategy!.GetHeadBlockHash().Returns(expectedHeadBlockHash);

            await CreateUnsafeUpdater().EnsureSyncPivot(default);

            byte[] storedData = _metadataDb!.Get(MetadataDbKeys.UpdatedPivotData)!;
            RlpReader ctx = new(storedData!);
            ulong storedPivotBlockNumber = ctx.DecodeULong();
            Hash256 storedPivotBlockHash = ctx.DecodeKeccak()!;

            Assert.That(storedPivotBlockNumber, Is.EqualTo(expectedPivotBlockNumber));
            Assert.That(storedPivotBlockHash, Is.EqualTo(expectedPivotBlockHash));
        }

        [Test]
        public async Task TrySetFreshPivot_for_unsafe_updater_ignores_peer_header_with_mismatched_number()
        {
            ulong requestedPivotNumber = _externalPeerBlockTree!.Head!.Number - 64;
            Hash256 wrongNumberHash = _externalPeerBlockTree!.FindLevel(requestedPivotNumber + 5)!.BlockInfos[0].BlockHash;
            _syncPeer!.GetBlockHeaders(requestedPivotNumber, 1, 0, default)
                .ReturnsForAnyArgs(_ => _externalPeerBlockTree!.FindHeaders(wrongNumberHash, 1, 0, default));

            _beaconSyncStrategy!.GetHeadBlockHash().Returns(_externalPeerBlockTree!.HeadHash);

            await CreateUnsafeUpdater().EnsureSyncPivot(default);

            Assert.That(_metadataDb!.Get(MetadataDbKeys.UpdatedPivotData), Is.Null,
                "a peer header at a number other than the requested one must not set the pivot");
        }
    }
}
