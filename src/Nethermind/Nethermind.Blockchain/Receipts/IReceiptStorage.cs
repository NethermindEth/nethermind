// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using System;
using System.Collections.Generic;
using System.Threading;

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
        bool TryInsertNetworkReceipts(Block block, ReadOnlySpan<byte> blockReceipts, bool withBloom, bool ensureCanonical) => false;

        ulong MigratedBlockNumber { get; set; }
        bool HasBlock(ulong blockNumber, Hash256 hash);
        void EnsureCanonical(Block block);
        void RemoveReceipts(Block block);

        /// <summary>Drops one block's receipts named by hash, so the caller does not have to load its body. Leaves the
        /// transaction index to <see cref="SweepTransactionIndex"/>, exactly as <see cref="RemoveReceiptsRange"/> does.
        /// Required rather than defaulted: a throwing default would let a pruning node fail its first pass.</summary>
        void RemoveReceipts(ulong blockNumber, Hash256 blockHash);

        /// <summary>Drops the receipts of every block in <c>[fromInclusive, toExclusive)</c> without reading any of
        /// them. Leaves the transaction index to <see cref="SweepTransactionIndex"/>.</summary>
        void RemoveReceiptsRange(ulong fromInclusive, ulong toExclusive) => throw new NotSupportedException();

        /// <summary>Drops many ranges in one call, so a store keeping a hash-keyed cache can invalidate it once
        /// instead of once per range. The default loops <see cref="RemoveReceiptsRange"/>.</summary>
        void RemoveReceiptsRanges(IReadOnlyList<(ulong FromInclusive, ulong ToExclusive)> ranges)
        {
            foreach ((ulong fromInclusive, ulong toExclusive) in ranges) RemoveReceiptsRange(fromInclusive, toExclusive);
        }

        /// <summary>Drops up to <paramref name="maxEntries"/> transaction-index entries naming blocks below
        /// <paramref name="retainedFromBlock"/>, from <paramref name="resumeFrom"/> on. Keyed by transaction hash, so
        /// the column has to be walked. <paramref name="maxEntries"/> must exceed one, because the resume key is
        /// re-examined and counted; a budget of one makes no progress and is treated as nothing to do.</summary>
        /// <returns>Where to resume, on cancellation as well as on budget exhaustion, or <c>null</c> at the end.</returns>
        byte[]? SweepTransactionIndex(ulong retainedFromBlock, byte[]? resumeFrom, int maxEntries, CancellationToken cancellationToken, out int removed)
        {
            removed = 0;
            return null;
        }

        /// <summary>As above, except blocks <paramref name="isHeightRetained"/> answers true for keep their entries -
        /// their bodies and receipts outlive the boundary, so their transactions must stay findable by hash. Defaults
        /// to the retention-blind overload rather than being required: an implementation overriding the shape that
        /// already shipped keeps sweeping, and the retention only matters to stores a sliced node actually runs.</summary>
        byte[]? SweepTransactionIndex(ulong retainedFromBlock, byte[]? resumeFrom, int maxEntries, Func<ulong, bool>? isHeightRetained, CancellationToken cancellationToken, out int removed) =>
            SweepTransactionIndex(retainedFromBlock, resumeFrom, maxEntries, cancellationToken, out removed);

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
