// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
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
    public class PivotUpdatorTests
    {
        private IBlockTree? _blockTree;
        private ISyncModeSelector? _syncModeSelector;
        private ISyncPeerPool? _syncPeerPool;
        private ISyncConfig? _syncConfig;
        private IBlockCacheService? _blockCacheService;
        private IBeaconSyncStrategy? _beaconSyncStrategy;
        private IDb? _metadataDb;
        private IBlockTree? _externalPeerBlockTree;

        [SetUp]
        public void Setup()
        {
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            TestSpecProvider specProvider = new(Cancun.Instance);
            specProvider.TerminalTotalDifficulty = 0;
            _externalPeerBlockTree = Build.A.BlockTree(genesisBlock, specProvider)
                .WithoutSettingHead
                .OfChainLength(20)
                .TestObject;

            ISyncPeer? fakePeer = Substitute.For<ISyncPeer>();
            fakePeer.GetHeadBlockHeader(default, default).ReturnsForAnyArgs(x => _externalPeerBlockTree!.Head!.Header);
            NetworkNode node = new(TestItem.PublicKeyA, "127.0.0.1", 30303, 100L);
            fakePeer.Node.Returns(new Node(node));

            _syncPeerPool = Substitute.For<ISyncPeerPool>();
            _syncPeerPool.InitializedPeers.Returns(new[] { new PeerInfo(fakePeer) });

            _blockTree = Substitute.For<IBlockTree>();
            _syncModeSelector = Substitute.For<ISyncModeSelector>();
            _syncConfig = Substitute.For<ISyncConfig>();
            _blockCacheService = new BlockCacheService();
            _beaconSyncStrategy = Substitute.For<IBeaconSyncStrategy>();
            _metadataDb = new MemDb();

            PivotUpdator pivotUpdator = new(
              _blockTree,
              _syncModeSelector,
              _syncPeerPool,
              _syncConfig,
              _blockCacheService,
              _beaconSyncStrategy,
              _metadataDb,
              LimboLogs.Instance
            );
        }

        [Test]
        public void TrySetFreshPivot_SavesFinalizedHashInDb()
        {
            SyncModeChangedEventArgs args = new(SyncMode.FastSync, SyncMode.UpdatingPivot);
            Hash256 expectedFinalizedHash = _externalPeerBlockTree!.HeadHash;
            long expectedPivotBlockNumber = _externalPeerBlockTree!.Head!.Number;
            _beaconSyncStrategy!.GetFinalizedHash().Returns(expectedFinalizedHash);

            _syncModeSelector!.Changed += Raise.EventWith(args);

            byte[] storedData = _metadataDb!.Get(MetadataDbKeys.UpdatedPivotData)!;
            RlpStream pivotStream = new(storedData!);
            long storedPivotBlockNumber = pivotStream.DecodeLong();
            Hash256 storedFinalizedHash = pivotStream.DecodeKeccak()!;

            storedFinalizedHash.Should().Be(expectedFinalizedHash);
            expectedPivotBlockNumber.Should().Be(storedPivotBlockNumber);
        }
    }
}
