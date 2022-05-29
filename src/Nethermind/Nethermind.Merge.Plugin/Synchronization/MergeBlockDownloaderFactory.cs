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
// 

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Logging;
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
        private readonly IChainLevelHelper _chainLevelHelper;

        public MergeBlockDownloaderFactory(
            IPoSSwitcher poSSwitcher,
            IBeaconPivot beaconPivot,
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            ISyncPeerPool peerPool,
            ISyncConfig syncConfig,
            IBetterPeerStrategy betterPeerStrategy,
            ISyncReport syncReport,
            ILogManager logManager)
        {
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
            _chainLevelHelper = new ChainLevelHelper(_blockTree, syncConfig, _logManager);
        }

        public BlockDownloader Create(ISyncFeed<BlocksRequest?> syncFeed)
        {
            return new MergeBlockDownloader(_poSSwitcher, _beaconPivot, syncFeed, _syncPeerPool, _blockTree, _blockValidator,
                _sealValidator, _syncReport, _receiptStorage, _specProvider, _betterPeerStrategy, _chainLevelHelper, _logManager);
        }
    }
}
