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
        private readonly IReceiptCanonicalityMonitor _receiptCanonicalityMonitor;
        private readonly IBlockTree _blockTree;
        private readonly LogFilter _filter;

        public LogsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            IReceiptCanonicalityMonitor receiptCanonicalityMonitor,
            IFilterStore? store,
            IBlockTree? blockTree,
            ILogManager? logManager,
            Filter? filter = null)
            : base(jsonRpcDuplexClient)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptCanonicalityMonitor = receiptCanonicalityMonitor ?? throw new ArgumentNullException(nameof(receiptCanonicalityMonitor));
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
            
            _receiptCanonicalityMonitor.ReceiptsInserted += OnReceiptsInserted;
            if(_logger.IsTrace) _logger.Trace($"Logs subscription {Id} will track ReceiptsInserted.");
        }

        private void OnReceiptsInserted(object? sender, ReceiptsEventArgs e)
        {
            TryPublishReceiptsInBackground(e.BlockHeader, () => e.TxReceipts, nameof(_receiptCanonicalityMonitor.ReceiptsInserted), e.WasRemoved);
        }
        
        private void TryPublishReceiptsInBackground(BlockHeader blockHeader, Func<TxReceipt[]> getReceipts, string eventName, bool removed)
        {
            ScheduleAction(() => TryPublishEvent(blockHeader, getReceipts(), eventName, removed));
        }

        private void TryPublishEvent(BlockHeader blockHeader, TxReceipt[] receipts, string eventName, bool removed)
        {
            BlockHeader fromBlock = _blockTree.FindHeader(_filter.FromBlock);
            BlockHeader toBlock = _blockTree.FindHeader(_filter.ToBlock, true);

            bool isAfterFromBlock = blockHeader.Number >= fromBlock?.Number;
            bool isBeforeToBlock = blockHeader.Number <= toBlock?.Number;

            if (isAfterFromBlock && isBeforeToBlock)
            {
                var filterLogs = GetFilterLogs(blockHeader, receipts, removed);

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

        private IEnumerable<FilterLog> GetFilterLogs(BlockHeader blockHeader, TxReceipt[] receipts, bool removed)
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
                                    receiptLog,
                                    removed);

                                yield return filterLog;
                            }
                        }
                    }
                }
            }
        }

        public override string Type => SubscriptionType.Logs;
        public override void Dispose()
        {
            _receiptCanonicalityMonitor.ReceiptsInserted -= OnReceiptsInserted;
            base.Dispose();
            if(_logger.IsTrace) _logger.Trace($"Logs subscription {Id} will no longer track ReceiptsInserted.");
        }
    }
}
