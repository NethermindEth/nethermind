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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;

namespace Nethermind.Facade.Eth
{
    public class EthSyncingInfo : IEthSyncingInfo
    {
        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;
        private readonly ILogger _logger;
        private readonly IReceiptStorage _receiptStorage;

        public EthSyncingInfo(
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            ISyncConfig syncConfig,
            ILogManager logManager)
        {
            _blockTree = blockTree;
            _syncConfig = syncConfig;
            _logger = logManager.GetClassLogger();
            _receiptStorage = receiptStorage;
        }

        public SyncingResult GetFullInfo()
        {
            long bestSuggestedNumber = _blockTree.FindBestSuggestedHeader().Number;
            long headNumberOrZero = _blockTree.Head?.Number ?? 0;
            bool isSyncing = bestSuggestedNumber > headNumberOrZero + 8;

            if (_logger.IsTrace) _logger.Trace($"Start - EthSyncingInfo - BestSuggestedNumber: {bestSuggestedNumber}, HeadNumberOrZero: {headNumberOrZero}, IsSyncing: {isSyncing} {_syncConfig}. LowestInsertedBodyNumber: {_blockTree.LowestInsertedBodyNumber} LowestInsertedReceiptBlockNumber: {_receiptStorage.LowestInsertedReceiptBlockNumber}");
            if (isSyncing)
            {
                if (_logger.IsTrace) _logger.Trace($"Too far from head - EthSyncingInfo - HighestBlock: {bestSuggestedNumber}, CurrentBlock: {headNumberOrZero}");
                return ReturnSyncing(headNumberOrZero, bestSuggestedNumber);
            }

            if (_syncConfig.FastSync)
            {
                if (_syncConfig.DownloadReceiptsInFastSync &&
                    (_receiptStorage.LowestInsertedReceiptBlockNumber == null || _receiptStorage.LowestInsertedReceiptBlockNumber > _syncConfig.AncientReceiptsBarrierCalc))
                {
                    if (_logger.IsTrace) _logger.Trace($"Receipts not finished - EthSyncingInfo - HighestBlock: {bestSuggestedNumber}, CurrentBlock: {headNumberOrZero}, AncientReceiptsBarrier: {_syncConfig.AncientReceiptsBarrierCalc}. LowestInsertedBodyNumber: {_blockTree.LowestInsertedBodyNumber} LowestInsertedReceiptBlockNumber: {_receiptStorage.LowestInsertedReceiptBlockNumber}");
                    return ReturnSyncing(headNumberOrZero, bestSuggestedNumber);
                }

                if (_syncConfig.DownloadBodiesInFastSync &&
                    (_blockTree.LowestInsertedBodyNumber == null || _blockTree.LowestInsertedBodyNumber > _syncConfig.AncientBodiesBarrierCalc))
                {
                    if (_logger.IsTrace) _logger.Trace($"Bodies not finished - EthSyncingInfo - HighestBlock: {bestSuggestedNumber}, CurrentBlock: {headNumberOrZero}, AncientBodiesBarrier: {_syncConfig.AncientBodiesBarrierCalc}. LowestInsertedBodyNumber: {_blockTree.LowestInsertedBodyNumber} LowestInsertedReceiptBlockNumber: {_receiptStorage.LowestInsertedReceiptBlockNumber}");
                    return ReturnSyncing(headNumberOrZero, bestSuggestedNumber);
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Node is not syncing - EthSyncingInfo - HighestBlock: {bestSuggestedNumber}, CurrentBlock: {headNumberOrZero}. LowestInsertedBodyNumber: {_blockTree.LowestInsertedBodyNumber} LowestInsertedReceiptBlockNumber: {_receiptStorage.LowestInsertedReceiptBlockNumber}");
            return SyncingResult.NotSyncing;
        }

        private SyncingResult ReturnSyncing(long headNumberOrZero, long bestSuggestedNumber)
        {
            return new SyncingResult
            {
                CurrentBlock = headNumberOrZero,
                HighestBlock = bestSuggestedNumber,
                StartingBlock = 0L,
                IsSyncing = true
            };
        }


        public bool IsSyncing()
        {
            return GetFullInfo().IsSyncing;
        }
    }
}
