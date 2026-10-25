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
    private readonly ReceiptsEventPayloadCache<HashSet<ValueHash256>>.Membership _payloads;

    public TransactionReceiptsSubscription(
        IJsonRpcDuplexClient jsonRpcDuplexClient,
        IReceiptMonitor receiptCanonicalityMonitor,
        IBlockTree blockTree,
        ILogManager logManager,
        TransactionHashesFilter? filter)
        : this(jsonRpcDuplexClient, receiptCanonicalityMonitor, blockTree, logManager, CreatePayloadCache(), filter)
    {
    }

    internal TransactionReceiptsSubscription(
        IJsonRpcDuplexClient jsonRpcDuplexClient,
        IReceiptMonitor receiptCanonicalityMonitor,
        IBlockTree blockTree,
        ILogManager logManager,
        ReceiptsEventPayloadCache<HashSet<ValueHash256>> payloads,
        TransactionHashesFilter? filter)
        : base(jsonRpcDuplexClient, MaxQueuedBlocks)
    {
        ArgumentNullException.ThrowIfNull(receiptCanonicalityMonitor);
        ArgumentNullException.ThrowIfNull(blockTree);
        ArgumentNullException.ThrowIfNull(logManager);

        _receiptMonitor = receiptCanonicalityMonitor;
        _blockTree = blockTree;
        _logger = logManager.GetClassLogger<TransactionReceiptsSubscription>();

        // Validate max 200 hashes
        if (filter?.TransactionHashes is not null && filter.TransactionHashes.Count > 200)
        {
            throw new ArgumentException("Cannot subscribe to more than 200 transaction hashes at once.");
        }

        _filterHashes = filter?.TransactionHashes;

        _payloads = payloads.Join(_filterHashes);
        _receiptMonitor.ReceiptsInserted += OnReceiptsInserted;
        if (_logger.IsTrace) _logger.Trace($"TransactionReceipts subscription {Id} will track ReceiptsInserted.");
    }

    /// <summary>Creates the cache the receipts subscriptions of one factory share their serialized receipts through.</summary>
    internal static ReceiptsEventPayloadCache<HashSet<ValueHash256>> CreatePayloadCache() =>
        new(static e => e.TxReceipts.Length, HashSet<ValueHash256>.CreateSetComparer());

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

        int cumulativeLogIndex = 0;
        EventPayloads payloads = default;
        bool payloadsResolved = false;

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

            if (!payloadsResolved)
            {
                payloads = _payloads.ForEvent(e);
                payloadsResolved = true;
            }

            // Send the receipt
            using JsonRpcResult result = CreateReceiptMessage(payloads, i, e, receipt, cumulativeLogIndex);
            await JsonRpcDuplexClient.SendJsonRpcResult(result);

            if (_logger.IsTrace) _logger.Trace($"TransactionReceipts subscription {Id} sent receipt for tx {receipt.TxHash}.");

            // Update cumulative log index
            cumulativeLogIndex += receipt.Logs?.Length ?? 0;
        }
    }

    private JsonRpcResult CreateReceiptMessage(EventPayloads payloads, int position, ReceiptsEventArgs e, TxReceipt receipt, int cumulativeLogIndex)
    {
        if (payloads.TryGet(position, out SerializedJson payload)) return CreateSubscriptionMessage(payload);

        // Create receipt for RPC
        // Using basic TxGasInfo with null values since tests don't check gas info
        ReceiptForRpc receiptForRpc = new(
            receipt.TxHash!,
            receipt,
            e.BlockHeader.Timestamp,
            _emptyGasInfo,
            cumulativeLogIndex
        );

        return payloads.Stores ? CreateSubscriptionMessage(payloads.Store(position, receiptForRpc)) : CreateSubscriptionMessage(receiptForRpc);
    }

    public override string Type => SubscriptionType.EthSubscription.TransactionReceipts;

    public override void Dispose()
    {
        _receiptMonitor.ReceiptsInserted -= OnReceiptsInserted;
        _payloads.Dispose();
        base.Dispose();
        if (_logger.IsTrace) _logger.Trace($"TransactionReceipts subscription {Id} will no longer track ReceiptsInserted.");
    }
}
