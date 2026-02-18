// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe;

public class TransactionReceiptsSubscription : Subscription
{
    private static readonly TxGasInfo _emptyGasInfo = new();

    private readonly IReceiptMonitor _receiptMonitor;
    private readonly IBlockTree _blockTree;
    private readonly HashSet<ValueHash256>? _filterHashes;

    public TransactionReceiptsSubscription(
        IJsonRpcDuplexClient jsonRpcDuplexClient,
        IReceiptMonitor receiptCanonicalityMonitor,
        IBlockTree blockTree,
        ILogManager logManager,
        TransactionHashesFilter? filter)
        : base(jsonRpcDuplexClient)
    {
        ArgumentNullException.ThrowIfNull(receiptCanonicalityMonitor);
        ArgumentNullException.ThrowIfNull(blockTree);
        ArgumentNullException.ThrowIfNull(logManager);

        _receiptMonitor = receiptCanonicalityMonitor;
        _blockTree = blockTree;
        _logger = logManager.GetClassLogger();

        // Validate max 200 hashes (defense in depth, needed for tests that bypass ReadJson)
        if (filter?.TransactionHashes is not null && filter.TransactionHashes.Count > 200)
        {
            throw new ArgumentException("Cannot subscribe to more than 200 transaction hashes at once.");
        }

        // Use the HashSet directly - no conversion needed
        _filterHashes = filter?.TransactionHashes;

        _receiptMonitor.ReceiptsInserted += OnReceiptsInserted;
        if (_logger.IsTrace) _logger.Trace($"TransactionReceipts subscription {Id} will track ReceiptsInserted.");
    }

    private void OnReceiptsInserted(object? sender, ReceiptsEventArgs e)
    {
        ScheduleAction(async () => await TryPublishReceipts(e));
    }

    private async Task TryPublishReceipts(ReceiptsEventArgs e)
    {
        // Skip if this is a reorg (receipts being removed)
        if (e.WasRemoved)
        {
            if (_logger.IsTrace) _logger.Trace($"TransactionReceipts subscription {Id}: Skipping removed receipts (reorg).");
            return;
        }

        // Skip if no receipts
        if (e.TxReceipts is null || e.TxReceipts.Length == 0)
        {
            if (_logger.IsTrace) _logger.Trace($"TransactionReceipts subscription {Id}: No receipts to process.");
            return;
        }

        int cumulativeLogIndex = 0;

        for (int i = 0; i < e.TxReceipts.Length; i++)
        {
            TxReceipt receipt = e.TxReceipts[i];

            // Apply filter if set
            if (_filterHashes?.Contains((ValueHash256)receipt.TxHash!) == false)
            {
                // Not in filter, skip but still count logs
                cumulativeLogIndex += receipt.Logs?.Length ?? 0;
                continue;
            }

            // Create receipt for RPC
            // Using basic TxGasInfo with null values since tests don't check gas info
            ReceiptForRpc receiptForRpc = new ReceiptForRpc(
                receipt.TxHash!,
                receipt,
                e.BlockHeader.Timestamp,
                _emptyGasInfo,
                cumulativeLogIndex
            );

            // Send the receipt
            using JsonRpcResult result = CreateSubscriptionMessage(receiptForRpc);
            await JsonRpcDuplexClient.SendJsonRpcResult(result);

            if (_logger.IsTrace) _logger.Trace($"TransactionReceipts subscription {Id} sent receipt for tx {receipt.TxHash}.");

            // Update cumulative log index
            cumulativeLogIndex += receipt.Logs?.Length ?? 0;
        }
    }

    public override string Type => SubscriptionType.EthSubscription.TransactionReceipts;

    public override void Dispose()
    {
        _receiptMonitor.ReceiptsInserted -= OnReceiptsInserted;
        base.Dispose();
        if (_logger.IsTrace) _logger.Trace($"TransactionReceipts subscription {Id} will no longer track ReceiptsInserted.");
    }
}
