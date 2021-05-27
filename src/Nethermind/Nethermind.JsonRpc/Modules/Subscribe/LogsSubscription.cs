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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class LogsSubscription : Subscription
    {
        private readonly IReceiptStorage _receiptStorage;
        private readonly IBlockTree _blockTree;
        private readonly LogFilter _filter;
        private readonly ILogger _logger;

        public LogsSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, IReceiptStorage? receiptStorage, IFilterStore? store, IBlockTree? blockTree, ILogManager? logManager, Filter? filter = null) 
            : base(jsonRpcDuplexClient)
        {
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            IFilterStore filterStore = store ?? throw new ArgumentNullException(nameof(store));

            if (filter != null)
            {
                _filter = filterStore.CreateLogFilter(
                    filter.FromBlock,
                    filter.ToBlock,
                    filter.Address,
                    filter.Topics);
                if (_logger.IsTrace) _logger.Trace($"Logs Subscription {Id}: Created LogFilter with the same arguments like \"filter\"");
            }
            else
            {
                _filter = filterStore.CreateLogFilter(
                    BlockParameter.Latest,
                    BlockParameter.Latest);
                if (_logger.IsTrace) _logger.Trace($"Logs Subscription {Id}: Argument \"filter\" was null and created LogFilter with arguments: FromBlock: BlockParameter.Latest, ToBlock: BlockParameter.Latest");
            }
            
            _receiptStorage.ReceiptsInserted += OnReceiptsInserted;
            _blockTree.NewHeadBlock += OnNewHeadBlock;
            if(_logger.IsTrace) _logger.Trace($"Logs subscription {Id} will track ReceiptsInserted.");
        }

        private void OnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            if (e.Block is not null)
            {
                TryPublishReceiptsInBackground(e.Block.Header, () =>  _receiptStorage.Get(e.Block), nameof(_blockTree.NewHeadBlock));
            }
        }

        private void OnReceiptsInserted(object? sender, ReceiptsEventArgs e)
        {
            bool isReceiptRemoved = e.TxReceipts.FirstOrDefault()?.Removed == true;
            if (isReceiptRemoved)
            {
                TryPublishReceiptsInBackground(e.BlockHeader, () => e.TxReceipts, nameof(_receiptStorage.ReceiptsInserted));
            }
        }
        
        private void TryPublishReceiptsInBackground(BlockHeader blockHeader, Func<TxReceipt[]> getReceipts, string eventName)
        {
            Task.Run(() => TryPublishEvent(blockHeader, getReceipts(), eventName))
                .ContinueWith(t =>
                        t.Exception?.Handle(ex =>
                        {
                            if (_logger.IsDebug) _logger.Debug($"Logs subscription {Id}: Failed Task.Run after {eventName} event.");
                            return true;
                        })
                    , TaskContinuationOptions.OnlyOnFaulted
                );
        }

        private void TryPublishEvent(BlockHeader blockHeader, TxReceipt[] receipts, string eventName)
        {
            BlockHeader fromBlock = _blockTree.FindHeader(_filter.FromBlock);
            BlockHeader toBlock = _blockTree.FindHeader(_filter.ToBlock, true);

            bool isAfterFromBlock = blockHeader.Number >= fromBlock?.Number;
            bool isBeforeToBlock = blockHeader.Number <= toBlock?.Number;

            if (isAfterFromBlock && isBeforeToBlock)
            {
                var filterLogs = GetFilterLogs(blockHeader, receipts);

                foreach (var filterLog in filterLogs)
                {
                    JsonRpcResult result = CreateSubscriptionMessage(filterLog);
                    JsonRpcDuplexClient.SendJsonRpcResult(result);
                    if (_logger.IsTrace) _logger.Trace($"Logs subscription {Id} printed new log.");
                }
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Logs subscription {Id}: {eventName} event happens, but there are no logs matching filter.");
            }
        }

        private IEnumerable<FilterLog> GetFilterLogs(BlockHeader blockHeader, TxReceipt[] receipts)
        {
            if (_filter.Matches(blockHeader.Bloom))
            {
                int logIndex = 0;
                for (int i = 0; i < receipts.Length; i++)
                {
                    TxReceipt receipt = receipts[i];
                    if (_filter.Matches(receipt.Bloom))
                    {
                        int transactionLogIndex = 0;
                        for (int j = 0; j < receipt.Logs.Length; j++)
                        {
                            var receiptLog = receipt.Logs[j];
                            if (_filter.Accepts(receiptLog))
                            {
                                FilterLog filterLog = new(
                                    logIndex++,
                                    transactionLogIndex++,
                                    receipt,
                                    receiptLog);

                                yield return filterLog;
                            }
                        }
                    }
                }
            }
        }

        public override SubscriptionType Type => SubscriptionType.Logs;
        public override void Dispose()
        {
            _receiptStorage.ReceiptsInserted -= OnReceiptsInserted;
            _blockTree.NewHeadBlock -= OnNewHeadBlock;
            if(_logger.IsTrace) _logger.Trace($"Logs subscription {Id} will no longer track ReceiptsInserted.");
        }
    }
}
