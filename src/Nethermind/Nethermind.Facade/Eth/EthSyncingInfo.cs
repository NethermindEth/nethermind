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
            bool isCloseToPivot = bestSuggestedNumber <= _syncConfig.PivotNumberParsed + 8;
            bool isSyncing = bestSuggestedNumber > headNumberOrZero + 8 || (_blockTree.Head?.IsGenesis ?? true) ||  isCloseToPivot;

            if (_logger.IsInfo) _logger.Info($"Start - EthSyncingInfo - BestSuggestedNumber: {bestSuggestedNumber}, HeadNumberOrZero: {headNumberOrZero}, IsCloseToPivot: {isCloseToPivot}, IsSyncing: {isSyncing} {_syncConfig}. LowestInsertedBodyNumber: {_blockTree.LowestInsertedBodyNumber } LowestInsertedReceiptBlockNumber: {_receiptStorage.LowestInsertedReceiptBlockNumber}");
            if (isSyncing)
            {
                return new SyncingResult
                {
                    CurrentBlock = headNumberOrZero,
                    HighestBlock = bestSuggestedNumber,
                    StartingBlock = 0L,
                    IsSyncing = true
                };
            }

            if (_syncConfig.FastSync)
            {
                if (_syncConfig.DownloadReceiptsInFastSync &&
                    (_receiptStorage.LowestInsertedReceiptBlockNumber > _syncConfig.AncientReceiptsBarrierCalc || _receiptStorage.LowestInsertedReceiptBlockNumber == null))
                {
                    if (_logger.IsInfo) _logger.Info($"Receipts not finished - EthSyncingInfo - BestSuggestedNumber: {bestSuggestedNumber}, HeadNumberOrZero: {headNumberOrZero}, IsCloseToPivot: {isCloseToPivot}, IsSyncing: {isSyncing} {_syncConfig}. LowestInsertedBodyNumber: {_blockTree.LowestInsertedBodyNumber } LowestInsertedReceiptBlockNumber: {_receiptStorage.LowestInsertedReceiptBlockNumber}");
                    return new SyncingResult { IsSyncing = true };
                }

                if (_syncConfig.DownloadBodiesInFastSync &&
                    (_blockTree.LowestInsertedBodyNumber > _syncConfig.AncientBodiesBarrierCalc || _blockTree.LowestInsertedBodyNumber == null))
                {
                    if (_logger.IsInfo) _logger.Info($"Bodies not finished - EthSyncingInfo - BestSuggestedNumber: {bestSuggestedNumber}, HeadNumberOrZero: {headNumberOrZero}, IsCloseToPivot: {isCloseToPivot}, IsSyncing: {isSyncing} {_syncConfig}. LowestInsertedBodyNumber: {_blockTree.LowestInsertedBodyNumber } LowestInsertedReceiptBlockNumber: {_receiptStorage.LowestInsertedReceiptBlockNumber}");
                    return new SyncingResult() {IsSyncing = true};
                }
            }

            if (_logger.IsInfo) _logger.Info($"Node is not syncing - EthSyncingInfo - BestSuggestedNumber: {bestSuggestedNumber}, HeadNumberOrZero: {headNumberOrZero}, IsCloseToPivot: {isCloseToPivot}, IsSyncing: {isSyncing} {_syncConfig}. LowestInsertedBodyNumber: {_blockTree.LowestInsertedBodyNumber } LowestInsertedReceiptBlockNumber: {_receiptStorage.LowestInsertedReceiptBlockNumber}");
            return SyncingResult.NotSyncing;
        }

        public bool IsSyncing()
        {
            return GetFullInfo().IsSyncing;
        }
    }
}
