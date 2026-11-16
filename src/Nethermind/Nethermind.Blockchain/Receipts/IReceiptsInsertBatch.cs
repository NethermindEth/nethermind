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

    /// <summary>
    /// Adds the receipts of a canonical block to the batch in their network encoding, without decoding them.
    /// </summary>
    /// <param name="block">The block, with the transactions the receipts belong to.</param>
    /// <param name="blockReceipts">The RLP list of the block's receipts as a peer sends it in a receipts message.</param>
    /// <param name="withBloom"><see langword="true"/> when each receipt carries its bloom (eth/63 to eth/68);
    /// <see langword="false"/> when the blooms are left out (eth/69 and later).</param>
    /// <returns><see langword="false"/> when the store does not take encoded receipts and adds nothing; the caller then
    /// decodes the receipts and adds them with <see cref="Insert"/>.</returns>
    bool TryInsertNetworkReceipts(Block block, ReadOnlyMemory<byte> blockReceipts, bool withBloom);
}

internal sealed class ImmediateReceiptsInsertBatch(IReceiptStorage receiptStorage) : IReceiptsInsertBatch
{
    public void Insert(Block block, TxReceipt[]? receipts) => receiptStorage.Insert(block, receipts, ensureCanonical: true);

    public bool TryInsertNetworkReceipts(Block block, ReadOnlyMemory<byte> blockReceipts, bool withBloom) =>
        receiptStorage.TryInsertNetworkReceipts(block, blockReceipts, withBloom, ensureCanonical: true);

    public void Dispose() { }
}
