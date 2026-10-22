// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe;

public class TransactionReceiptsSubscription : Subscription
{
    private readonly IReceiptMonitor _receiptMonitor;
    private readonly IBlockTree _blockTree;
    private readonly ISpecProvider _specProvider;
    private readonly HashSet<ValueHash256>? _filterHashes;

    public TransactionReceiptsSubscription(
        IJsonRpcDuplexClient jsonRpcDuplexClient,
        IReceiptMonitor receiptCanonicalityMonitor,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        ILogManager logManager,
        TransactionHashesFilter? filter)
        : base(jsonRpcDuplexClient, MaxQueuedBlocks)
    {
        ArgumentNullException.ThrowIfNull(receiptCanonicalityMonitor);
        ArgumentNullException.ThrowIfNull(blockTree);
        ArgumentNullException.ThrowIfNull(specProvider);
        ArgumentNullException.ThrowIfNull(logManager);

        _receiptMonitor = receiptCanonicalityMonitor;
        _blockTree = blockTree;
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger<TransactionReceiptsSubscription>();

        // Validate max 200 hashes
        if (filter?.TransactionHashes is not null && filter.TransactionHashes.Count > 200)
        {
            throw new ArgumentException("Cannot subscribe to more than 200 transaction hashes at once.");
        }

        _filterHashes = filter?.TransactionHashes;

        _receiptMonitor.ReceiptsInserted += OnReceiptsInserted;
        if (_logger.IsTrace) _logger.Trace($"TransactionReceipts subscription {Id} will track ReceiptsInserted.");
    }

    private void OnReceiptsInserted(object? sender, ReceiptsEventArgs e) => ScheduleAction(() => TryPublishReceipts(e));

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

        BlockHeader header = e.BlockHeader;
        IReleaseSpec spec = _specProvider.GetSpec(header);
        using ArrayPoolList<ReceiptForRpc> receipts = new(_filterHashes is null ? e.TxReceipts.Length : Math.Min(_filterHashes.Count, e.TxReceipts.Length));
        int cumulativeLogIndex = 0;

        for (int i = 0; i < e.TxReceipts.Length; i++)
        {
            TxReceipt receipt = e.TxReceipts[i];
            if (e.LogIndexPerReceipt) cumulativeLogIndex = 0;

            // Apply filter if set
            if (_filterHashes?.Contains((ValueHash256)receipt.TxHash!) == false)
            {
                // Not in filter, skip but still count logs
                cumulativeLogIndex += receipt.Logs?.Length ?? 0;
                continue;
            }

            receipts.Add(new ReceiptForRpc(
                receipt.TxHash!,
                receipt,
                header.Timestamp,
                e.Transactions?[i].GetGasInfo(spec, header) ?? default,
                cumulativeLogIndex
            ));

            // Update cumulative log index
            cumulativeLogIndex += receipt.Logs?.Length ?? 0;
        }

        if (receipts.Count == 0)
        {
            return;
        }

        using JsonRpcResult result = CreateSubscriptionMessage(receipts);
        await JsonRpcDuplexClient.SendJsonRpcResult(result);

        if (_logger.IsTrace) _logger.Trace($"TransactionReceipts subscription {Id} sent {receipts.Count} receipts of block {header.Number}.");
    }

    public override string Type => SubscriptionType.EthSubscription.TransactionReceipts;

    public override void Dispose()
    {
        _receiptMonitor.ReceiptsInserted -= OnReceiptsInserted;
        base.Dispose();
        if (_logger.IsTrace) _logger.Trace($"TransactionReceipts subscription {Id} will no longer track ReceiptsInserted.");
    }
}
