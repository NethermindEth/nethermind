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
