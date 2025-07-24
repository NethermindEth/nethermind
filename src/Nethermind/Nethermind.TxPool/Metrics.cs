// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
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
    }
}
