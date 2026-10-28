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
        private readonly ReceiptsEventPayloadCache<Filter>.Membership _payloads;

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
            ReceiptsEventPayloadCache<Filter> payloads,
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

            _isBlockHashFilter = _filter.IsBlockHashFilter;
            _payloads = payloads.Join(GetFilterKey(filter));
            _receiptCanonicalityMonitor.ReceiptsInserted += OnReceiptsInserted;
            if (_logger.IsTrace) _logger.Trace($"Logs subscription {Id} will track ReceiptsInserted.");
        }

        /// <remarks>
        /// A block without matches is not queued. A matching block is queued as one entry, so a log-heavy block cannot
        /// overflow the queue of a client that keeps up; the entry references the receipts shared by all subscribers,
        /// and its logs are created one at a time while sending. When subscriptions share logs, the first one to send
        /// a log stores its serialized form on the event, where it stays while any queue still holds the event.
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
        internal static ReceiptsEventPayloadCache<Filter> CreatePayloadCache() => new(static e =>
        {
            int logs = 0;
            foreach (TxReceipt receipt in e.TxReceipts) logs += receipt.Logs?.Length ?? 0;
            return logs;
        }, FilterComparer.Instance);

        /// <summary>
        /// The filter as its payload cache key, or <c>null</c> for one accepting every log of every block.
        /// </summary>
        /// <remarks>A wildcard topic position still rejects logs with fewer topics, so only a filter without topic positions qualifies.</remarks>
        private Filter? GetFilterKey(Filter? filter)
        {
            if (filter is null)
                return null;
            if (_filter is not { AcceptsAnyBlock: true, FromBlock.Type: BlockParameterType.Latest, ToBlock.Type: BlockParameterType.Latest })
                return filter;

            foreach (Hash256[]? topic in filter.Topics ?? [])
                return filter;

            return null;
        }

        /// <summary>Equates filters accepting the same logs of the same blocks.</summary>
        /// <remarks>Addresses are compared as a set; topics position by position, in order.</remarks>
        internal sealed class FilterComparer : IEqualityComparer<Filter>
        {
            public static readonly FilterComparer Instance = new();

            public bool Equals(Filter? x, Filter? y)
            {
                if (ReferenceEquals(x, y))
                    return true;
                if (x is null || y is null
                    || x.FromBlock != y.FromBlock
                    || x.ToBlock != y.ToBlock
                    || (x.Address is null ? y.Address is not null : y.Address is null || !x.Address.SetEquals(y.Address)))
                    return false;

                using IEnumerator<Hash256[]?> left = (x.Topics ?? []).GetEnumerator();
                using IEnumerator<Hash256[]?> right = (y.Topics ?? []).GetEnumerator();
                while (left.MoveNext())
                {
                    if (!right.MoveNext() || !TopicComparer.Instance.Equals(left.Current, right.Current))
                        return false;
                }
                return !right.MoveNext();
            }

            public int GetHashCode(Filter filter)
            {
                int addresses = 0;
                foreach (AddressAsKey address in filter.Address ?? []) addresses += address.GetHashCode();

                HashCode hash = new();
                hash.Add(filter.FromBlock);
                hash.Add(filter.ToBlock);
                hash.Add(addresses);
                foreach (Hash256[]? slot in filter.Topics ?? [])
                {
                    hash.Add(slot?.Length ?? -1);
                    foreach (Hash256 topic in slot ?? []) hash.Add(topic);
                }

                return hash.ToHashCode();
            }

            private sealed class TopicComparer : IEqualityComparer<Hash256[]?>
            {
                public static readonly TopicComparer Instance = new();

                public bool Equals(Hash256[]? x, Hash256[]? y) => x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);

                public int GetHashCode(Hash256[]? obj) => obj?.Length ?? -1;
            }
        }

        private async Task PublishLogs(ReceiptsEventArgs e)
        {
            foreach (JsonRpcResult message in GetLogMessages(e))
            {
                using JsonRpcResult result = message;
                await JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"Logs subscription {Id} printed new log.");
            }
        }

        private JsonRpcResult CreateLogMessage(EventPayloads payloads, int position, ReceiptsEventArgs e, long logIndex, TxReceipt receipt, LogEntry log)
        {
            if (payloads.TryGet(position, out SerializedJson payload)) return CreateSubscriptionMessage(payload);

            FilterLog filterLog = new(logIndex, receipt, log, e.BlockHeader.Timestamp, e.WasRemoved);
            return payloads.Stores ? CreateSubscriptionMessage(payloads.Store(position, filterLog)) : CreateSubscriptionMessage(filterLog);
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

        private IEnumerable<JsonRpcResult> GetLogMessages(ReceiptsEventArgs e)
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
                                yield return CreateLogMessage(payloads, position + j, e, logIndex, receipt, receiptLog);
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
