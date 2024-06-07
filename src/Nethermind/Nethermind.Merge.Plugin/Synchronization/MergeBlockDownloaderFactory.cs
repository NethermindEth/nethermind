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
using Nethermind.State;
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
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly IBetterPeerStrategy _betterPeerStrategy;
        private readonly ILogManager _logManager;
        private readonly IStateReader _stateReader;
        private readonly ISyncConfig _syncConfig;

        public MergeBlockDownloaderFactory(
            IPoSSwitcher poSSwitcher,
            IBeaconPivot beaconPivot,
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            ISyncConfig syncConfig,
            IBetterPeerStrategy betterPeerStrategy,
            IStateReader stateReader,
            ILogManager logManager)
        {
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
            _beaconPivot = beaconPivot ?? throw new ArgumentNullException(nameof(beaconPivot));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _betterPeerStrategy = betterPeerStrategy ?? throw new ArgumentNullException(nameof(betterPeerStrategy));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader)); ;
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig)); ;
        }

        public BlockDownloader Create(ISyncFeed<BlocksRequest?> syncFeed, IBlockTree blockTree, IReceiptStorage receiptStorage,
            ISyncPeerPool syncPeerPool, ISyncReport syncReport)
        {
            ChainLevelHelper chainLevelHelper = new ChainLevelHelper(blockTree, _beaconPivot, _syncConfig, _logManager);
            return new MergeBlockDownloader(
                _poSSwitcher,
                _beaconPivot,
                syncFeed,
                syncPeerPool,
                blockTree,
                _blockValidator,
                _sealValidator,
                syncReport,
                receiptStorage,
                _specProvider,
                _betterPeerStrategy,
                chainLevelHelper,
                _stateReader,
                _logManager);
        }

        public IPeerAllocationStrategyFactory<BlocksRequest> CreateAllocationStrategyFactory()
        {
            return new MergeBlocksSyncPeerAllocationStrategyFactory(_poSSwitcher, _beaconPivot, _logManager);
        }
    }
}
