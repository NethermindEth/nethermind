// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Facade.Filters;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class LogsSubscription : Subscription
    {
        private readonly IReceiptMonitor _receiptCanonicalityMonitor;
        private readonly IBlockTree _blockTree;
        private readonly LogFilter _filter;

        public LogsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            IReceiptMonitor receiptCanonicalityMonitor,
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

            if (filter is not null)
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
            if (_logger.IsTrace) _logger.Trace($"Logs subscription {Id} will track ReceiptsInserted.");
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
            if (_filter.Matches(blockHeader.Bloom!))
            {
                int logIndex = 0;
                for (int i = 0; i < receipts.Length; i++)
                {
                    TxReceipt receipt = receipts[i];
                    if (_filter.Matches(receipt.Bloom!))
                    {
                        for (int j = 0; j < receipt.Logs!.Length; j++)
                        {
                            var receiptLog = receipt.Logs[j];
                            if (_filter.Accepts(receiptLog))
                            {
                                yield return new FilterLog(
                                    logIndex,
                                    j,
                                    receipt,
                                    receiptLog,
                                    removed);
                            }

                            logIndex++;
                        }
                    }
                    else
                    {
                        logIndex += receipt.Logs.Length;
                    }
                }
            }
        }

        public override string Type => SubscriptionType.Logs;
        public override void Dispose()
        {
            _receiptCanonicalityMonitor.ReceiptsInserted -= OnReceiptsInserted;
            base.Dispose();
            if (_logger.IsTrace) _logger.Trace($"Logs subscription {Id} will no longer track ReceiptsInserted.");
        }
    }
}
