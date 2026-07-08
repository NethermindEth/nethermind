// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading;
using Nethermind.Core.Attributes;

namespace Nethermind.TxPool
{
    public static class Metrics
    {
        [CounterMetric]
        [Description("Number of pending transactions broadcasted to peers.")]
        public static long PendingTransactionsSent { get; set; }

        [CounterMetric]
        [Description("Number of hashes of pending transactions broadcasted to peers.")]
        public static long PendingTransactionsHashesSent { get; set; }

        [CounterMetric]
        [Description("Number of pending transactions received from peers.")]
        public static long PendingTransactionsReceived { get; set; }

        [CounterMetric]
        [Description("Number of hashes of pending transactions received from peers.")]
        public static long PendingTransactionsHashesReceived { get; set; }

        [CounterMetric]
        [Description("Number of pending transaction retry handlers skipped because the transaction was received before the retry timeout.")]
        public static long PendingTransactionRetryHandlersSkippedOnReceived;

        [CounterMetric]
        [Description("Number of pending transaction retry resources received before timeout with at least one retry handler skipped.")]
        public static long PendingTransactionRetryResourcesSkippedOnReceived;

        [CounterMetric]
        [Description("Cumulative milliseconds pending transaction retry resources spent waiting before being received with retry handlers skipped.")]
        public static long PendingTransactionRetryResourcesSkippedOnReceivedAgeMilliseconds;

        [CounterMetric]
        [Description("Number of pending transaction retry handler-resource pairs called after the original transaction request timed out.")]
        public static long PendingTransactionRetryHandlersCalledOnTimeout;

        [CounterMetric]
        [Description("Number of batched pending transaction retry handler invocations called after original transaction requests timed out.")]
        public static long PendingTransactionRetryBatchHandlersCalledOnTimeout;

        [CounterMetric]
        [Description("Number of pending transaction retry resources included in batched handler invocations after timeout.")]
        public static long PendingTransactionRetryBatchResourcesCalledOnTimeout;

        [CounterMetric]
        [Description("Number of pending transaction retry handler invocations that used the single-message fallback after timeout.")]
        public static long PendingTransactionRetryFallbackHandlersCalledOnTimeout;

        [CounterMetric]
        [Description("Number of pending transaction retry resources that timed out and called at least one retry handler.")]
        public static long PendingTransactionRetryResourcesTimedOutWithHandlers;

        [CounterMetric]
        [Description("Cumulative milliseconds pending transaction retry resources spent waiting before timeout when retry handlers were called.")]
        public static long PendingTransactionRetryResourcesTimedOutWithHandlersAgeMilliseconds;

        [CounterMetric]
        [Description("Number of pending transaction retry handlers rejected because the per-resource retry handler limit was reached.")]
        public static long PendingTransactionRetryHandlersRejectedByLimit;

        [CounterMetric]
        [Description("Number of pending transaction announcements requested immediately because the retry queue was full.")]
        public static long PendingTransactionRetryQueueFull;

        [CounterMetric]
        [Description("Number of pending transaction hashes announced by peers, grouped by peer client.")]
        [KeyIsLabel("client")]
        public static ConcurrentDictionary<string, long> NewPooledTransactionsAnnouncedByClient { get; } = new();

        [CounterMetric]
        [Description("Number of pending transactions requested from peers, grouped by peer client.")]
        [KeyIsLabel("client")]
        public static ConcurrentDictionary<string, long> NewPooledTransactionsRequestedByClient { get; } = new();

        [CounterMetric]
        [Description("Number of pending transactions requested from peers, grouped by peer client and request reason.")]
        [KeyIsLabel("client", "reason")]
        public static ConcurrentDictionary<(string Client, string Reason), long> NewPooledTransactionsRequestedByClientAndReason { get; } = new();

        [CounterMetric]
        [Description("Number of pooled transaction request messages sent to peers, grouped by peer client and request reason.")]
        [KeyIsLabel("client", "reason")]
        public static ConcurrentDictionary<(string Client, string Reason), long> NewPooledTransactionRequestMessagesByClientAndReason { get; } = new();

        [CounterMetric]
        [Description("Number of pending transactions returned by peers, grouped by peer client.")]
        [KeyIsLabel("client")]
        public static ConcurrentDictionary<string, long> NewPooledTransactionsReturnedByClient { get; } = new();

        [CounterMetric]
        [Description("Number of pooled transaction response messages received from peers, grouped by peer client.")]
        [KeyIsLabel("client")]
        public static ConcurrentDictionary<string, long> NewPooledTransactionResponseMessagesByClient { get; } = new();

        [CounterMetric]
        [Description("Number of empty pooled transaction response messages received from peers, grouped by peer client.")]
        [KeyIsLabel("client")]
        public static ConcurrentDictionary<string, long> NewPooledTransactionEmptyResponseMessagesByClient { get; } = new();

        [CounterMetric]
        [Description("Number of pending transactions received that were ignored.")]
        public static long PendingTransactionsDiscarded { get; set; }

        [CounterMetric]
        [Description("Number of pending transactions received that were ignored because of not supported transaction type.")]
        public static long PendingTransactionsNotSupportedTxType { get; set; }

        [CounterMetric]
        [Description(
            "Number of pending transactions received that were ignored because of not having preceding nonce of this sender in TxPool.")]
        public static long PendingTransactionsNonceGap { get; set; }

        [CounterMetric]
        [Description("Number of pending transactions received that were ignored because of priority fee lower than minimal requirement.")]
        public static long PendingTransactionsTooLowPriorityFee { get; set; }

        [CounterMetric]
        [Description("Number of pending transactions received that were ignored because of fee per blob gas lower than minimal requirement.")]
        public static long PendingTransactionsTooLowFeePerBlobGas { get; set; }

        [CounterMetric]
        [Description("Number of pending transactions received that were ignored because of fee lower than the lowest fee in transaction pool.")]
        public static long PendingTransactionsTooLowFee { get; set; }

        [CounterMetric]
        [Description(
            "Number of pending transactions received that were ignored because balance is zero and cannot pay gas.")]
        public static long PendingTransactionsZeroBalance { get; set; }

        [CounterMetric]
        [Description(
            "Number of pending transactions received that were ignored because balance is less than txn value.")]
        public static long PendingTransactionsBalanceBelowValue { get; set; }

        [CounterMetric]
        [Description(
            "Number of pending transactions received that were ignored because balance too low for fee to be higher than the lowest fee in transaction pool.")]
        public static long PendingTransactionsTooLowBalance { get; set; }

        [CounterMetric]
        [Description(
            "Number of pending transactions received that were ignored because the sender couldn't be resolved.")]
        public static long PendingTransactionsUnresolvableSender { get; set; }

        [CounterMetric]
        [Description(
            "Number of pending transactions received that were ignored because the gas limit was to high for the block.")]
        public static long PendingTransactionsGasLimitTooHigh { get; set; }

        [CounterMetric]
        [Description("Number of pending transactions received that were ignored after passing early rejections as balance is too low to compete with lowest effective fee in transaction pool.")]
        public static long PendingTransactionsPassedFiltersButCannotCompeteOnFees { get; set; }

        [CounterMetric]
        [Description("Number of pending transactions received that were trying to replace tx with the same sender and nonce and failed.")]
        public static long PendingTransactionsPassedFiltersButCannotReplace { get; set; }

        [CounterMetric]
        [Description("Number of pending transactions that reached filters which are resource expensive")]
        public static long PendingTransactionsWithExpensiveFiltering { get; set; }

        [CounterMetric]
        [Description("Number of already known pending transactions.")]
        public static long PendingTransactionsKnown { get; set; }

        [CounterMetric]
        [Description("Number of malformed transactions.")]
        public static long PendingTransactionsMalformed { get; set; }

        [CounterMetric]
        [Description("Number of transactions with already used nonce.")]
        public static long PendingTransactionsLowNonce { get; set; }

        [CounterMetric]
        [Description("Number of transactions with nonce too far in future.")]
        public static long PendingTransactionsNonceTooFarInFuture { get; set; }

        [CounterMetric]
        [Description("Number of transactions rejected because of already pending tx of other type (allowed blob txs or others, not both at once).")]
        public static long PendingTransactionsConflictingTxType { get; set; }

        [CounterMetric]
        [Description("Number of pending transactions added to transaction pool.")]
        public static long PendingTransactionsAdded;

        [CounterMetric]
        [Description("Number of pending 1559-type transactions added to transaction pool.")]
        public static long Pending1559TransactionsAdded { get; set; }

        [CounterMetric]
        [Description("Number of pending blob-type transactions added to transaction pool.")]
        public static long PendingBlobTransactionsAdded { get; set; }

        [CounterMetric]
        [Description("Number of pending transactions evicted from transaction pool.")]
        public static long PendingTransactionsEvicted { get; set; }

        [GaugeMetric]
        [Description("Ratio of 1559-type transactions in the block.")]
        public static float Eip1559TransactionsRatio { get; set; }

        [GaugeMetric]
        [Description("Number of 7702-type transactions in the block.")]
        public static long Eip7702TransactionsInBlock { get; set; }

        [GaugeMetric]
        [Description("Number of blob transactions in the block.")]
        public static long BlobTransactionsInBlock { get; set; }

        [GaugeMetric]
        [Description("Number of blobs in the block.")]
        public static long BlobsInBlock { get; set; }

        [GaugeMetric]
        [Description("Ratio of transactions in the block absent in hashCache.")]
        public static float DarkPoolRatioLevel1 { get; set; }

        [GaugeMetric]
        [Description("Ratio of transactions in the block absent in pending transactions.")]
        public static float DarkPoolRatioLevel2 { get; set; }

        [GaugeMetric]
        [Description("Number of transactions in pool.")]
        public static long TransactionCount { get; set; }

        [GaugeMetric]
        [Description("Number of blob transactions in pool.")]
        public static long BlobTransactionCount { get; set; }

        [Description("Number of pending transactions rejected due to excessive size.")]
        public static long PendingTransactionsSizeTooLarge { get; set; }

        [Description("Number of pending transactions rejected with a null hash.")]
        public static long PendingTransactionsNullHash { get; set; }

        [Description("Number of transactions sourced from private order flow.")]
        public static long TransactionsSourcedPrivateOrderFlow { get; internal set; }

        [Description("Number of transactions sourced from the mempool.")]
        public static long TransactionsSourcedMemPool { get; internal set; }

        [Description("Number of transactions reorganized during chain reorg.")]
        public static long TransactionsReorged { get; internal set; }

        public static void AddNewPooledTransactionsAnnouncedByClient(string client, long count) =>
            AddBy(NewPooledTransactionsAnnouncedByClient, client, count);

        public static void AddNewPooledTransactionsRequestedByClient(string client, long count) =>
            AddBy(NewPooledTransactionsRequestedByClient, client, count);

        public static void AddNewPooledTransactionsRequestedByClient(string client, long count, PooledTransactionRequestReason reason)
        {
            AddNewPooledTransactionsRequestedByClient(client, count);
            AddBy(NewPooledTransactionsRequestedByClientAndReason, (client, GetReasonLabel(reason)), count);
        }

        public static void AddNewPooledTransactionRequestMessagesByClient(string client, long count, PooledTransactionRequestReason reason) =>
            AddBy(NewPooledTransactionRequestMessagesByClientAndReason, (client, GetReasonLabel(reason)), count);

        public static void AddNewPooledTransactionsReturnedByClient(string client, long count) =>
            AddBy(NewPooledTransactionsReturnedByClient, client, count);

        public static void AddNewPooledTransactionResponseMessagesByClient(string client, long count) =>
            AddBy(NewPooledTransactionResponseMessagesByClient, client, count);

        public static void AddNewPooledTransactionEmptyResponseMessagesByClient(string client, long count) =>
            AddBy(NewPooledTransactionEmptyResponseMessagesByClient, client, count);

        public static void AddPendingTransactionRetryHandlersSkippedOnReceived(long count) =>
            Add(ref PendingTransactionRetryHandlersSkippedOnReceived, count);

        public static void AddPendingTransactionRetryResourcesSkippedOnReceived(long count) =>
            Add(ref PendingTransactionRetryResourcesSkippedOnReceived, count);

        public static void AddPendingTransactionRetryResourcesSkippedOnReceivedAgeMilliseconds(long count) =>
            Add(ref PendingTransactionRetryResourcesSkippedOnReceivedAgeMilliseconds, count);

        public static void AddPendingTransactionRetryHandlersCalledOnTimeout(long count) =>
            Add(ref PendingTransactionRetryHandlersCalledOnTimeout, count);

        public static void AddPendingTransactionRetryBatchHandlersCalledOnTimeout(long count) =>
            Add(ref PendingTransactionRetryBatchHandlersCalledOnTimeout, count);

        public static void AddPendingTransactionRetryBatchResourcesCalledOnTimeout(long count) =>
            Add(ref PendingTransactionRetryBatchResourcesCalledOnTimeout, count);

        public static void AddPendingTransactionRetryFallbackHandlersCalledOnTimeout(long count) =>
            Add(ref PendingTransactionRetryFallbackHandlersCalledOnTimeout, count);

        public static void AddPendingTransactionRetryResourcesTimedOutWithHandlers(long count) =>
            Add(ref PendingTransactionRetryResourcesTimedOutWithHandlers, count);

        public static void AddPendingTransactionRetryResourcesTimedOutWithHandlersAgeMilliseconds(long count) =>
            Add(ref PendingTransactionRetryResourcesTimedOutWithHandlersAgeMilliseconds, count);

        public static void AddPendingTransactionRetryHandlersRejectedByLimit(long count) =>
            Add(ref PendingTransactionRetryHandlersRejectedByLimit, count);

        public static void AddPendingTransactionRetryQueueFull(long count) =>
            Add(ref PendingTransactionRetryQueueFull, count);

        private static void Add(ref long metric, long count)
        {
            if (count <= 0)
            {
                return;
            }

            Interlocked.Add(ref metric, count);
        }

        private static string GetReasonLabel(PooledTransactionRequestReason reason) => reason switch
        {
            PooledTransactionRequestReason.Retry => "retry",
            _ => "initial"
        };

        private static void AddBy<TKey>(ConcurrentDictionary<TKey, long> metric, TKey key, long count)
            where TKey : notnull
        {
            if (count <= 0)
            {
                return;
            }

            metric.AddOrUpdate(key, static (_, added) => added, static (_, current, added) => current + added, count);
        }
    }

    /// <summary>
    /// Reason why a pooled transaction request was sent to a peer.
    /// </summary>
    public enum PooledTransactionRequestReason
    {
        /// <summary>
        /// The request follows a new pooled transaction hash announcement.
        /// </summary>
        Initial,

        /// <summary>
        /// The request follows a RetryCache timeout for a previously requested transaction.
        /// </summary>
        Retry
    }
}
