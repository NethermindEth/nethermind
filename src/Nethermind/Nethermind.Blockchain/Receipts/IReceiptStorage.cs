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

        /// <summary>Starts a batch that inserts the receipts of several canonical blocks in one write.</summary>
        IReceiptsInsertBatch StartInsertBatch() => new ImmediateReceiptsInsertBatch(this);

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

        /// <summary>Stores a block's receipts as a receipts message carries them, without decoding them.</summary>
        /// <remarks>
        /// Check the receipts against the block's receipts root first. An implementation that cannot store this form
        /// returns <see langword="false"/> and stores nothing; the caller then decodes the receipts and inserts those.
        /// </remarks>
        /// <param name="block">The block, whose transactions need only their hashes.</param>
        /// <param name="blockReceipts">The encoded sequence of the block's receipts, each one <c>NetworkReceipt.TryRead</c> reads.</param>
        /// <param name="withBloom">Whether the receipts have blooms: the eth/63 to eth/68 form.</param>
        /// <param name="ensureCanonical">Whether to index the block's transactions as canonical.</param>
        /// <exception cref="System.IO.InvalidDataException">The receipts and the transactions differ in number.</exception>
        bool TryInsertNetworkReceipts(Block block, ReadOnlyMemory<byte> blockReceipts, bool withBloom, bool ensureCanonical) => false;

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
