// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism;

/// <summary>Finds receipts with the decorated finder, but gives no stored receipts as a receipts message carries them.</summary>
/// <remarks>
/// Each Optimism block starts with a deposit transaction, and stored receipts are not copied for a deposit receipt.
/// The copy would read the block and the receipts for nothing, so the receipts are decoded at once.
/// </remarks>
public sealed class OptimismReceiptFinder(IReceiptFinder receiptFinder) : IReceiptFinder
{
    public Hash256? FindBlockHash(Hash256 txHash) => receiptFinder.FindBlockHash(txHash);

    public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true) => receiptFinder.Get(block, recover, recoverSender);

    public TxReceipt[] Get(Hash256 blockHash, bool recover = true) => receiptFinder.Get(blockHash, recover);

    public bool CanGetReceiptsByHash(ulong blockNumber) => receiptFinder.CanGetReceiptsByHash(blockNumber);

    public bool TryGetReceiptsIterator(ulong blockNumber, Hash256 blockHash, out ReceiptsIterator iterator) =>
        receiptFinder.TryGetReceiptsIterator(blockNumber, blockHash, out iterator);

    public bool TryGetReceipt(ReceiptRecoveryBlock block, Hash256 txHash, [NotNullWhen(true)] out TxReceipt? receipt, out int txIndex, out int logIndexStart) =>
        receiptFinder.TryGetReceipt(block, txHash, out receipt, out txIndex, out logIndexStart);
}
