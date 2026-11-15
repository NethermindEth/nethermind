// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Blockchain.Receipts;

/// <summary>Receipts of several canonical blocks that are stored together when the batch is disposed.</summary>
public interface IReceiptsInsertBatch : IDisposable
{
    /// <summary>Adds the receipts of a canonical block to the batch.</summary>
    void Insert(Block block, TxReceipt[]? receipts);

    bool TryInsertNetworkReceipts(Block block, ReadOnlyMemory<byte> blockReceipts, bool withBloom);
}

internal sealed class ImmediateReceiptsInsertBatch(IReceiptStorage receiptStorage) : IReceiptsInsertBatch
{
    public void Insert(Block block, TxReceipt[]? receipts) => receiptStorage.Insert(block, receipts, ensureCanonical: true);

    public bool TryInsertNetworkReceipts(Block block, ReadOnlyMemory<byte> blockReceipts, bool withBloom) =>
        receiptStorage.TryInsertNetworkReceipts(block, blockReceipts, withBloom, ensureCanonical: true);

    public void Dispose() { }
}
