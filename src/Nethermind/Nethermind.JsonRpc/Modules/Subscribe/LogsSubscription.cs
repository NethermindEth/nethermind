// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Facade.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        private readonly ReceiptsEventPayloadCache.Membership _payloads;

        public LogsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            IReceiptMonitor receiptCanonicalityMonitor,
            FilterStore? store,
            IBlockTree? blockTree,
            ILogManager? logManager,
            Filter? filter = null)
            : this(jsonRpcDuplexClient, receiptCanonicalityMonitor, store, blockTree, logManager, CreatePayloadCache(), filter)
        {
        }

        internal LogsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            IReceiptMonitor receiptCanonicalityMonitor,
            FilterStore? store,
            IBlockTree? blockTree,
            ILogManager? logManager,
            ReceiptsEventPayloadCache payloads,
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
            _payloads = payloads.Join(GetFilterKey(filter));
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

        /// <summary>Creates the cache the logs subscriptions of one factory share their serialized logs through.</summary>
        internal static ReceiptsEventPayloadCache CreatePayloadCache() => new(static e =>
        {
            int logs = 0;
            foreach (TxReceipt receipt in e.TxReceipts) logs += receipt.Logs?.Length ?? 0;
            return logs;
        });

        /// <summary>
        /// A key equal for filters accepting the same logs of the same blocks, or <c>null</c> for one accepting every log
        /// of every block.
        /// </summary>
        private static string? GetFilterKey(Filter? filter)
        {
            if (filter is null || (filter.Address is null && filter.Topics is null
                && filter.FromBlock.Type == BlockParameterType.Latest && filter.ToBlock.Type == BlockParameterType.Latest))
            {
                return null;
            }

            StringBuilder key = new();
            key.Append(filter.FromBlock).Append('|').Append(filter.ToBlock).Append('|');
            if (filter.Address is not null) key.AppendJoin(',', filter.Address.Select(static a => a.Value.ToString()).Order(StringComparer.Ordinal));
            foreach (Hash256[]? topics in filter.Topics ?? [])
            {
                key.Append('|');
                if (topics is null) key.Append('*');
                else key.AppendJoin(',', topics.Select(static t => t.ToString()).Order(StringComparer.Ordinal));
            }

            return key.ToString();
        }

        private async Task PublishLogs(ReceiptsEventArgs e)
        {
            foreach (object log in GetFilterLogs(e))
            {
                using JsonRpcResult result = log is byte[] payload
                    ? CreateSubscriptionMessage(new SerializedJson(payload))
                    : CreateSubscriptionMessage((FilterLog)log);
                await JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"Logs subscription {Id} printed new log.");
            }
        }

        /// <returns>The log's shared serialized payload, or the <see cref="FilterLog"/> itself when it is not shared.</returns>
        private static object GetFilterLog(EventPayloads payloads, int position, ReceiptsEventArgs e, long logIndex, TxReceipt receipt, LogEntry log)
        {
            if (payloads.TryGet(position, out SerializedJson payload)) return payload.Utf8Json;

            FilterLog filterLog = new(logIndex, receipt, log, e.BlockHeader.Timestamp, e.WasRemoved);
            return payloads.Stores ? payloads.Store(position, filterLog).Utf8Json : filterLog;
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

        /// <returns>Each accepted log, as <see cref="GetFilterLog"/> gives it.</returns>
        private IEnumerable<object> GetFilterLogs(ReceiptsEventArgs e)
        {
            if (_filter.Matches(e.BlockHeader.Bloom!))
            {
                EventPayloads payloads = _payloads.ForEvent(e);
                int position = 0;
                int logIndex = 0;
                for (int i = 0; i < e.TxReceipts.Length; i++)
                {
                    TxReceipt receipt = e.TxReceipts[i];
                    if (e.LogIndexPerReceipt) logIndex = 0;
                    if (_filter.Matches(receipt.Bloom!))
                    {
                        for (int j = 0; j < receipt.Logs!.Length; j++)
                        {
                            LogEntry receiptLog = receipt.Logs[j];
                            if (_filter.Accepts(receiptLog))
                            {
                                yield return GetFilterLog(payloads, position + j, e, logIndex, receipt, receiptLog);
                            }

                            logIndex++;
                        }
                    }
                    else
                    {
                        logIndex += receipt.Logs.Length;
                    }

                    position += receipt.Logs.Length;
                }
            }
        }

        public override string Type => SubscriptionType.EthSubscription.Logs;
        public override void Dispose()
        {
            _receiptCanonicalityMonitor.ReceiptsInserted -= OnReceiptsInserted;
            _payloads.Dispose();
            base.Dispose();
            if (_logger.IsTrace) _logger.Trace($"Logs subscription {Id} will no longer track ReceiptsInserted.");
        }
    }
}
