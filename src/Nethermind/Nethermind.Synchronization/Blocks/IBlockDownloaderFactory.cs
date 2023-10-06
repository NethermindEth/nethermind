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
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.Synchronization.Blocks
{
    public class BlockDownloaderFactory : IBlockDownloaderFactory
    {
        private readonly ISpecProvider _specProvider;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly IBetterPeerStrategy _betterPeerStrategy;
        private readonly ILogManager _logManager;
        private readonly ISyncReport _syncReport;

        public BlockDownloaderFactory(
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            ISyncPeerPool peerPool,
            IBetterPeerStrategy betterPeerStrategy,
            ISyncReport syncReport,
            ILogManager logManager)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _syncPeerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _betterPeerStrategy = betterPeerStrategy ?? throw new ArgumentNullException(nameof(betterPeerStrategy));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        }

        public BlockDownloader Create(ISyncFeed<BlocksRequest?> syncFeed)
        {
            return new(
                syncFeed,
                _syncPeerPool,
                _blockTree,
                _blockValidator,
                 _sealValidator,
                _syncReport,
                _receiptStorage,
                _specProvider,
                _betterPeerStrategy,
                _logManager);
        }

        public IPeerAllocationStrategyFactory<BlocksRequest> CreateAllocationStrategyFactory()
        {
            return new BlocksSyncPeerAllocationStrategyFactory();
        }
    }

    public interface IBlockDownloaderFactory
    {
        BlockDownloader Create(ISyncFeed<BlocksRequest?> syncFeed);
        IPeerAllocationStrategyFactory<BlocksRequest> CreateAllocationStrategyFactory();
    }
}
