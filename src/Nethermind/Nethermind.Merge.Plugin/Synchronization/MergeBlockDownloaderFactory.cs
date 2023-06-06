// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;


namespace Nethermind.Merge.Plugin.Synchronization
{
    public class MergeBlockDownloaderFactory : IBlockDownloaderFactory
    {
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly IBeaconPivot _beaconPivot;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly IBetterPeerStrategy _betterPeerStrategy;
        private readonly ILogManager _logManager;
        private readonly ISyncReport _syncReport;
        private readonly ISyncProgressResolver _syncProgressResolver;
        private readonly IChainLevelHelper _chainLevelHelper;
        private readonly int _maxNumberOfProcessingThread;

        public MergeBlockDownloaderFactory(
            int maxNumberOfProcessingThread,
            IPoSSwitcher poSSwitcher,
            IBeaconPivot beaconPivot,
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IBlockCacheService blockCacheService,
            IReceiptStorage receiptStorage,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            ISyncPeerPool peerPool,
            ISyncConfig syncConfig,
            IBetterPeerStrategy betterPeerStrategy,
            ISyncReport syncReport,
            ISyncProgressResolver syncProgressResolver,
            ILogManager logManager)
        {
            _maxNumberOfProcessingThread = maxNumberOfProcessingThread;
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
            _beaconPivot = beaconPivot ?? throw new ArgumentNullException(nameof(beaconPivot));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _syncPeerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _betterPeerStrategy = betterPeerStrategy ?? throw new ArgumentNullException(nameof(betterPeerStrategy));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _chainLevelHelper = new ChainLevelHelper(_blockTree, _beaconPivot, syncConfig, _logManager);
            _syncProgressResolver = syncProgressResolver ?? throw new ArgumentNullException(nameof(syncProgressResolver)); ;
        }

        public BlockDownloader Create(ISyncFeed<BlocksRequest?> syncFeed)
        {
            return new MergeBlockDownloader(
                _maxNumberOfProcessingThread, _poSSwitcher, _beaconPivot, syncFeed, _syncPeerPool, _blockTree, _blockValidator,
                _sealValidator, _syncReport, _receiptStorage, _specProvider, _betterPeerStrategy, _chainLevelHelper,
                _syncProgressResolver, _logManager);
        }
    }
}
