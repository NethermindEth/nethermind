// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.Synchronization.Blocks
{
    public class BlockDownloaderFactory : IBlockDownloaderFactory
    {
        private readonly ISpecProvider _specProvider;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly IBetterPeerStrategy _betterPeerStrategy;
        private readonly ILogManager _logManager;

        public BlockDownloaderFactory(
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            IBetterPeerStrategy betterPeerStrategy,
            ILogManager logManager)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _betterPeerStrategy = betterPeerStrategy ?? throw new ArgumentNullException(nameof(betterPeerStrategy));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        }

        public BlockDownloader Create(ISyncFeed<BlocksRequest?> syncFeed, IBlockTree blockTree, IReceiptStorage receiptStorage, ISyncPeerPool peerPool, ISyncReport syncReport)
        {
            return new(
                syncFeed,
                peerPool,
                blockTree,
                _blockValidator,
                _sealValidator,
                syncReport,
                receiptStorage,
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
        BlockDownloader Create(ISyncFeed<BlocksRequest?> syncFeed, IBlockTree blockTree, IReceiptStorage receiptStorage, ISyncPeerPool syncPeerPool, ISyncReport syncReport);
        IPeerAllocationStrategyFactory<BlocksRequest> CreateAllocationStrategyFactory();
    }
}
