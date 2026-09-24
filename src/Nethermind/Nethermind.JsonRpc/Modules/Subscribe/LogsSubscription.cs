// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Facade.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class LogsSubscription : Subscription
    {
        private readonly IReceiptMonitor _receiptCanonicalityMonitor;
        private readonly IBlockTree _blockTree;
        private readonly LogFilter _filter;
        private readonly bool _isBlockHashFilter;

        public LogsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            IReceiptMonitor receiptCanonicalityMonitor,
            FilterStore? store,
            IBlockTree? blockTree,
            ILogManager? logManager,
            Filter? filter = null)
            : base(jsonRpcDuplexClient, MaxQueuedBlocks)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptCanonicalityMonitor = receiptCanonicalityMonitor ?? throw new ArgumentNullException(nameof(receiptCanonicalityMonitor));
            _logger = logManager?.GetClassLogger<LogsSubscription>() ?? throw new ArgumentNullException(nameof(logManager));
            FilterStore filterStore = store ?? throw new ArgumentNullException(nameof(store));

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

            _isBlockHashFilter = _filter.FromBlock.Equals(_filter.ToBlock);
            _receiptCanonicalityMonitor.ReceiptsInserted += OnReceiptsInserted;
            if (_logger.IsTrace) _logger.Trace($"Logs subscription {Id} will track ReceiptsInserted.");
        }

        /// <remarks>
        /// A block without matches is not queued. A matching block is queued as one entry, so a log-heavy block cannot
        /// overflow the queue of a client that keeps up; the entry references the receipts shared by all subscribers,
        /// and its logs are created one at a time while sending, so nothing is copied per block or per subscriber.
        /// </remarks>
        private void OnReceiptsInserted(object? sender, ReceiptsEventArgs e)
        {
            BlockHeader blockHeader = e.BlockHeader;
            if (!IsWithinBound(_filter.FromBlock, blockHeader, lowerBound: true) || !IsWithinBound(_filter.ToBlock, blockHeader, lowerBound: false))
            {
                if (_logger.IsTrace) _logger.Trace($"Logs subscription {Id}: {nameof(_receiptCanonicalityMonitor.ReceiptsInserted)} event happens, but there are no logs matching filter.");
                return;
            }

            if (HasMatchingLog(blockHeader, e.TxReceipts))
            {
                ScheduleAction(() => PublishLogs(e));
            }
        }

        private async Task PublishLogs(ReceiptsEventArgs e)
        {
            foreach (FilterLog filterLog in GetFilterLogs(e.BlockHeader, e.TxReceipts, e.WasRemoved))
            {
                using JsonRpcResult result = CreateSubscriptionMessage(filterLog);
                await JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"Logs subscription {Id} printed new log.");
            }
        }

        /// <summary>
        /// Whether <paramref name="header"/> lies on the right side of <paramref name="bound"/>.
        /// </summary>
        /// <remarks>
        /// "latest"/"pending" mean an open-ended subscription, not the head at publish time: the head moves before each
        /// block's event is handled, so resolving them would drop every block below it, including a reorg's removed side.
        /// A hash is exact only in the {"blockHash": ..} form (both bounds equal); one-sided it is a position on the chain.
        /// </remarks>
        private bool IsWithinBound(BlockParameter bound, BlockHeader header, bool lowerBound) => bound.Type switch
        {
            BlockParameterType.Latest or BlockParameterType.Pending => true,
            BlockParameterType.Earliest when lowerBound => true,
            BlockParameterType.BlockNumber => IsOnSide(header.Number, bound.BlockNumber, lowerBound),
            BlockParameterType.BlockHash when _isBlockHashFilter => header.Hash == bound.BlockHash,
            _ => IsOnSide(header.Number, _blockTree.FindHeader(bound)?.Number, lowerBound),
        };

        private static bool IsOnSide(ulong number, ulong? bound, bool lowerBound) => lowerBound ? number >= bound : number <= bound;

        private bool HasMatchingLog(BlockHeader blockHeader, TxReceipt[] receipts)
        {
            if (!_filter.Matches(blockHeader.Bloom!)) return false;

            foreach (TxReceipt receipt in receipts)
            {
                if (!_filter.Matches(receipt.Bloom!)) continue;

                foreach (LogEntry log in receipt.Logs!)
                {
                    if (_filter.Accepts(log)) return true;
                }
            }

            return false;
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
                            LogEntry receiptLog = receipt.Logs[j];
                            if (_filter.Accepts(receiptLog))
                            {
                                yield return new FilterLog(
                                    logIndex,
                                    receipt,
                                    receiptLog,
                                    blockHeader.Timestamp,
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

        public override string Type => SubscriptionType.EthSubscription.Logs;
        public override void Dispose()
        {
            _receiptCanonicalityMonitor.ReceiptsInserted -= OnReceiptsInserted;
            base.Dispose();
            if (_logger.IsTrace) _logger.Trace($"Logs subscription {Id} will no longer track ReceiptsInserted.");
        }
    }
}
