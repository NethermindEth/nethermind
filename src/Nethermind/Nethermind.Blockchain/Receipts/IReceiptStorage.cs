// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using System;

namespace Nethermind.Blockchain.Receipts
{
    public interface IReceiptStorage : IReceiptFinder
    {
        void Insert(Block block, params TxReceipt[]? txReceipts) => Insert(block, txReceipts, true);
        void Insert(Block block, TxReceipt[]? txReceipts, bool ensureCanonical, WriteFlags writeFlags = WriteFlags.None, ulong? lastBlockNumber = null);
        void Insert(Block block, TxReceipt[]? txReceipts, IReleaseSpec spec, bool ensureCanonical, WriteFlags writeFlags = WriteFlags.None, ulong? lastBlockNumber = null);

        /// <summary>
        /// Inserts receipts for a freshly processed block, deferring the durable write off the
        /// block-processing path when the implementation supports it.
        /// </summary>
        /// <remarks>
        /// Visibility is synchronous: after this returns, the receipts are readable through all
        /// finder methods regardless of whether the underlying database write has completed.
        /// Canonical indexing is not performed here - it happens when the block is added to the
        /// main chain.
        /// </remarks>
        void InsertDeferred(Block block, TxReceipt[]? txReceipts, IReleaseSpec spec) =>
            Insert(block, txReceipts, spec, ensureCanonical: false);
        ulong MigratedBlockNumber { get; set; }
        bool HasBlock(ulong blockNumber, Hash256 hash);
        void EnsureCanonical(Block block);
        void RemoveReceipts(Block block);

        /// <summary>Drops the receipts of every block in <c>[fromInclusive, toExclusive)</c> in one operation,
        /// without reading any of them. Leaves the transaction index to <see cref="SweepTransactionIndex"/>.
        /// </summary>
        void RemoveReceiptsRange(ulong fromInclusive, ulong toExclusive) => throw new NotSupportedException();

        /// <summary>
        /// Drops transaction-index entries pointing at blocks below <paramref name="retainedFromBlock"/>, at most
        /// <paramref name="maxEntries"/> of them, starting from <paramref name="resumeFrom"/>. The index is keyed by
        /// transaction hash so it cannot be addressed by block range, but each value carries the block number, which
        /// is enough to decide staleness without reading a block.
        /// </summary>
        /// <returns>The key to resume from, or <c>null</c> once the column has been walked end to end - at which
        /// point a caller should start over, since the retained boundary will have moved on.</returns>
        byte[]? SweepTransactionIndex(ulong retainedFromBlock, byte[]? resumeFrom, int maxEntries, out int removed)
        {
            removed = 0;
            return null;
        }

        /// <summary>
        /// Receipts for canonical chain changed.
        /// </summary>
        event EventHandler<BlockReplacementEventArgs>? NewCanonicalReceipts;

        /// <summary>
        /// Receipts for any block are inserted.
        /// </summary>
        /// <remarks>
        /// This is invoked for both canonical and non-canonical blocks.
        /// </remarks>
        event EventHandler<ReceiptsEventArgs>? ReceiptsInserted;
    }
}
