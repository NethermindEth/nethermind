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
using System.Threading.Tasks;
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
        private readonly IBlockFinder _blockFinder;
        private readonly LogFilter _filter;
        private readonly ILogger _logger;

        public LogsSubscription(IReceiptStorage? receiptStorage, IFilterStore? store, IBlockFinder? blockFinder, ILogManager? logManager, Filter? filter = null)
        {
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
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
            if(_logger.IsTrace) _logger.Trace($"Logs subscription {Id} will track ReceiptsInserted.");
        }

        private void OnReceiptsInserted(object? sender, ReceiptsEventArgs e)
        {
            Task.Run(() =>
            {
                BlockHeader fromBlock = _blockFinder.FindHeader(_filter.FromBlock);
                BlockHeader toBlock = _blockFinder.FindHeader(_filter.ToBlock, true);

                bool isAfterFromBlock = e.BlockHeader.Number >= fromBlock.Number;
                bool isBeforeToBlock = e.BlockHeader.Number <= toBlock.Number || _filter.ToBlock.Equals(BlockParameter.Latest) || _filter.ToBlock.Equals(BlockParameter.Pending);

                if (isAfterFromBlock && isBeforeToBlock)
                {
                    var filterLogs = GetFilterLogs(e);
                    
                    foreach (var filterLog in filterLogs)
                    {
                        JsonRpcResult result = GetJsonRpcResult(filterLog);
                        JsonRpcDuplexClient.SendJsonRpcResult(result);
                        if(_logger.IsTrace) _logger.Trace($"Logs subscription {Id} printed new log.");
                    }
                }
                else
                {
                    if(_logger.IsTrace) _logger.Trace($"Logs subscription {Id}: OnReceiptsInserted event happens, but there are no logs matching filter.");

                }
            }).ContinueWith(
                t =>
                    t.Exception?.Handle(ex =>
                    {
                        if (_logger.IsDebug) _logger.Debug($"Logs subscription {Id}: Failed Task.Run after ReceiptsInserted event.");
                        return true;
                    })
                , TaskContinuationOptions.OnlyOnFaulted
            );
        }

        private List<FilterLog> GetFilterLogs(ReceiptsEventArgs e)
        {
            List<FilterLog> filterLogs = new List<FilterLog>();

            if (_filter.Matches(e.BlockHeader.Bloom))
            {
                int logIndex = 0;
                for (int i = 0; i < e.TxReceipts.Length; i++)
                {
                    TxReceipt receipt = e.TxReceipts[i];
                    if (_filter.Matches(receipt.Bloom))
                    {
                        int transactionLogIndex = 0;
                        for (int j = 0; j < receipt.Logs.Length; j++)
                        {
                            var receiptLog = receipt.Logs[j];
                            if (_filter.Accepts(receiptLog))
                            {
                                FilterLog filterLog = new FilterLog(
                                    logIndex++,
                                    transactionLogIndex++,
                                    receipt,
                                    receiptLog);
                                filterLogs.Add(filterLog);
                            }
                        }
                    }
                }
            }
            return filterLogs;
        }

        private JsonRpcResult GetJsonRpcResult(FilterLog filterLog)
        {
            JsonRpcResult result =
                JsonRpcResult.Single(
                    new JsonRpcSubscriptionResponse()
                    {
                        MethodName = nameof(ISubscribeModule.eth_subscribe),
                        Params = new JsonRpcSubscriptionResult()
                        {
                            Result = filterLog,
                            Subscription = Id
                        }
                    }, default);
            return result;
        }

        public override SubscriptionType Type => SubscriptionType.Logs;
        public override void Dispose()
        {
            _receiptStorage.ReceiptsInserted -= OnReceiptsInserted;
            if(_logger.IsTrace) _logger.Trace($"Logs subscription {Id} will no longer track ReceiptsInserted.");
        }
    }
}
