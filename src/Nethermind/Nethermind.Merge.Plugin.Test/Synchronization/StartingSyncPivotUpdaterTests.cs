// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using NSubstitute;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
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
        private ISyncModeSelector? _syncModeSelector;
        private ISyncPeerPool? _syncPeerPool;
        private ISyncConfig? _syncConfig;
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
            _syncModeSelector = Substitute.For<ISyncModeSelector>();
            _syncConfig = new SyncConfig()
            {
                MaxAttemptsToUpdatePivot = 1
            };
            _blockCacheService = new BlockCacheService();
            _beaconSyncStrategy = Substitute.For<IBeaconSyncStrategy>();
        }

        [Test]
        public void TrySetFreshPivot_saves_FinalizedHash_in_db()
        {
            _ = new StartingSyncPivotUpdater(
                _blockTree!,
                _syncModeSelector!,
                _syncPeerPool!,
                _syncConfig!,
                _blockCacheService!,
                _beaconSyncStrategy!,
                LimboLogs.Instance
            );

            SyncModeChangedEventArgs args = new(SyncMode.FastSync, SyncMode.UpdatingPivot);
            Hash256 expectedFinalizedHash = _externalPeerBlockTree!.HeadHash;
            long expectedPivotBlockNumber = _externalPeerBlockTree!.Head!.Number;
            _beaconSyncStrategy!.GetFinalizedHash().Returns(expectedFinalizedHash);

            _syncModeSelector!.Changed += Raise.EventWith(args);

            byte[] storedData = _metadataDb!.Get(MetadataDbKeys.UpdatedPivotData)!;
            Rlp.ValueDecoderContext ctx = new(storedData!);
            long storedPivotBlockNumber = ctx.DecodeLong();
            Hash256 storedFinalizedHash = ctx.DecodeKeccak()!;

            Assert.That(storedFinalizedHash, Is.EqualTo(expectedFinalizedHash));
            Assert.That(storedPivotBlockNumber, Is.EqualTo(expectedPivotBlockNumber));
        }

        [Test]
        public void TrySetFreshPivot_falls_back_to_FinalizedHash_persisted_in_block_tree_when_no_FCU_received()
        {
            Hash256 persistedFinalizedHash = _externalPeerBlockTree!.HeadHash!;
            long expectedPivotBlockNumber = _externalPeerBlockTree!.Head!.Number;
            _blockTree!.ForkChoiceUpdated(persistedFinalizedHash, persistedFinalizedHash);

            _ = CreateUpdaterWithRealBeaconSync();

            _syncModeSelector!.Changed += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.FastSync, SyncMode.UpdatingPivot));

            byte[]? storedData = _metadataDb!.Get(MetadataDbKeys.UpdatedPivotData);
            Assert.That(storedData, Is.Not.Null, "pivot should be updated from the persisted finalized hash without any FCU");
            Rlp.ValueDecoderContext ctx = new(storedData!);
            Assert.That(ctx.DecodeLong(), Is.EqualTo(expectedPivotBlockNumber));
            Assert.That(ctx.DecodeKeccak(), Is.EqualTo(persistedFinalizedHash));
        }

        [Test]
        public void TrySetFreshPivot_keeps_waiting_when_persisted_FinalizedHash_is_zero()
        {
            _syncConfig!.MaxAttemptsToUpdatePivot = 3;
            _blockTree!.ForkChoiceUpdated(Keccak.Zero, Keccak.Zero);

            _ = CreateUpdaterWithRealBeaconSync();

            SyncModeChangedEventArgs args = new(SyncMode.FastSync, SyncMode.UpdatingPivot);
            _syncModeSelector!.Changed += Raise.EventWith(args);
            _syncModeSelector!.Changed += Raise.EventWith(args);

            Assert.That(_metadataDb!.Get(MetadataDbKeys.UpdatedPivotData), Is.Null,
                "a zero finalized hash means no data, so no pivot should be set");
            Assert.That(_syncConfig!.MaxAttemptsToUpdatePivot, Is.Not.Zero,
                "attempts remain, so the updater must keep waiting instead of falling back to the static pivot");
        }

        [Test]
        public void TrySetFreshPivot_ignores_zero_cached_FinalizedHash_and_falls_back_to_block_tree()
        {
            Hash256 persistedFinalizedHash = _externalPeerBlockTree!.HeadHash!;
            long expectedPivotBlockNumber = _externalPeerBlockTree!.Head!.Number;
            _blockTree!.ForkChoiceUpdated(persistedFinalizedHash, persistedFinalizedHash);
            _blockCacheService!.FinalizedHash = Keccak.Zero;

            _ = CreateUpdaterWithRealBeaconSync();

            _syncModeSelector!.Changed += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.FastSync, SyncMode.UpdatingPivot));

            byte[]? storedData = _metadataDb!.Get(MetadataDbKeys.UpdatedPivotData);
            Assert.That(storedData, Is.Not.Null, "a zero cached finalized hash must not shadow the persisted one");
            Rlp.ValueDecoderContext ctx = new(storedData!);
            Assert.That(ctx.DecodeLong(), Is.EqualTo(expectedPivotBlockNumber));
            Assert.That(ctx.DecodeKeccak(), Is.EqualTo(persistedFinalizedHash));
        }

        // Real BeaconSync over an empty block cache: the only finalized hash source is the block tree
        private StartingSyncPivotUpdater CreateUpdaterWithRealBeaconSync()
        {
            BeaconSync beaconSync = new(
                Substitute.For<IBeaconPivot>(),
                _blockTree!,
                _syncConfig!,
                _blockCacheService!,
                Substitute.For<IPoSSwitcher>(),
                LimboLogs.Instance);

            return new StartingSyncPivotUpdater(
                _blockTree!,
                _syncModeSelector!,
                _syncPeerPool!,
                _syncConfig!,
                _blockCacheService!,
                beaconSync,
                LimboLogs.Instance);
        }

        [TestCase(2, 0, TestName = "Finite_attempts_fall_back_to_static_pivot_after_exhaustion")]
        [TestCase(ISyncConfig.InfiniteAttempts, ISyncConfig.InfiniteAttempts, TestName = "Infinite_attempts_never_fall_back_to_static_pivot")]
        public void TrySetFreshPivot_fallback_respects_MaxAttemptsToUpdatePivot(int maxAttempts, int expectedFinalConfigValue)
        {
            _syncConfig!.MaxAttemptsToUpdatePivot = maxAttempts;
            // Finalized hash unset → TrySetFreshPivot returns null → counts as a failed attempt.
            _beaconSyncStrategy!.GetFinalizedHash().Returns((Hash256?)null);

            _ = new StartingSyncPivotUpdater(
                _blockTree!,
                _syncModeSelector!,
                _syncPeerPool!,
                _syncConfig!,
                _blockCacheService!,
                _beaconSyncStrategy!,
                LimboLogs.Instance
            );

            SyncModeChangedEventArgs args = new(SyncMode.FastSync, SyncMode.UpdatingPivot);
            for (int i = 0; i < 100; i++)
            {
                _syncModeSelector!.Changed += Raise.EventWith(args);
            }

            Assert.That(_syncConfig.MaxAttemptsToUpdatePivot, Is.EqualTo(expectedFinalConfigValue));
        }

        [Test]
        public void TrySetFreshPivot_for_unsafe_updater_saves_pivot_64_blocks_behind_HeadBlockHash_in_db()
        {
            _ = new UnsafeStartingSyncPivotUpdater(
                _blockTree!,
                _syncModeSelector!,
                _syncPeerPool!,
                _syncConfig!,
                _blockCacheService!,
                _beaconSyncStrategy!,
                LimboLogs.Instance
            );

            SyncModeChangedEventArgs args = new(SyncMode.FastSync, SyncMode.UpdatingPivot);
            Hash256 expectedHeadBlockHash = _externalPeerBlockTree!.HeadHash;
            long expectedPivotBlockNumber = _externalPeerBlockTree!.Head!.Number - 64;
            Hash256 expectedPivotBlockHash = _externalPeerBlockTree!.FindLevel(expectedPivotBlockNumber)!.BlockInfos[0].BlockHash;
            _beaconSyncStrategy!.GetHeadBlockHash().Returns(expectedHeadBlockHash);

            _syncModeSelector!.Changed += Raise.EventWith(args);

            byte[] storedData = _metadataDb!.Get(MetadataDbKeys.UpdatedPivotData)!;
            Rlp.ValueDecoderContext ctx = new(storedData!);
            long storedPivotBlockNumber = ctx.DecodeLong();
            Hash256 storedPivotBlockHash = ctx.DecodeKeccak()!;

            Assert.That(storedPivotBlockNumber, Is.EqualTo(expectedPivotBlockNumber));
            Assert.That(storedPivotBlockHash, Is.EqualTo(expectedPivotBlockHash));
        }

        [Test]
        public void TrySetFreshPivot_for_unsafe_updater_ignores_peer_header_with_mismatched_number()
        {
            long requestedPivotNumber = _externalPeerBlockTree!.Head!.Number - 64;
            Hash256 wrongNumberHash = _externalPeerBlockTree!.FindLevel(requestedPivotNumber + 5)!.BlockInfos[0].BlockHash;
            _syncPeer!.GetBlockHeaders(requestedPivotNumber, 1, 0, default)
                .ReturnsForAnyArgs(_ => _externalPeerBlockTree!.FindHeaders(wrongNumberHash, 1, 0, default));

            _ = new UnsafeStartingSyncPivotUpdater(
                _blockTree!,
                _syncModeSelector!,
                _syncPeerPool!,
                _syncConfig!,
                _blockCacheService!,
                _beaconSyncStrategy!,
                LimboLogs.Instance
            );

            _beaconSyncStrategy!.GetHeadBlockHash().Returns(_externalPeerBlockTree!.HeadHash);

            _syncModeSelector!.Changed += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.FastSync, SyncMode.UpdatingPivot));

            Assert.That(_metadataDb!.Get(MetadataDbKeys.UpdatedPivotData), Is.Null,
                "a peer header at a number other than the requested one must not set the pivot");
        }
    }
}
