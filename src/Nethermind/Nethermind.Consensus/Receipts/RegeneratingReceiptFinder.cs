// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Receipts;

/// <summary>
/// Serves receipts that are not on disk by re-executing their block, so an archive can drop stored receipt bodies
/// for any range it still holds state history for.
/// </summary>
/// <remarks>
/// Decorates the read path only. Block processing writes through <see cref="IReceiptStorage"/>, which is left
/// untouched, so nothing here can affect what gets persisted. A block with transactions but no stored receipts is
/// the signal to regenerate; regeneration that fails its root check falls through to the stored (empty) result
/// rather than substituting unverified receipts.
/// </remarks>
public sealed class RegeneratingReceiptFinder(
    IReceiptFinder inner,
    IReceiptStorage storage,
    IBlockFinder blockFinder,
    ReceiptsRegenerator regenerator) : IReceiptFinder
{
    private const int CacheSize = 16;
    private readonly LruCache<ValueHash256, TxReceipt[]> _regenerated = new(CacheSize, CacheSize, "regenerated receipts");

    public Hash256? FindBlockHash(Hash256 txHash) => inner.FindBlockHash(txHash);

    public bool CanGetReceiptsByHash(ulong blockNumber) => inner.CanGetReceiptsByHash(blockNumber);

    public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true)
    {
        TxReceipt[] stored = inner.Get(block, recover, recoverSender);
        if (stored.Length > 0) return stored;
        return TryRegenerate(block, out TxReceipt[]? regenerated) ? regenerated : Unavailable(block, stored);
    }

    public TxReceipt[] Get(Hash256 blockHash, bool recover = true)
    {
        TxReceipt[] stored = inner.Get(blockHash, recover);
        if (stored.Length > 0) return stored;
        Block? block = FindBlock(blockHash);
        return TryRegenerate(block, out TxReceipt[]? regenerated) ? regenerated : Unavailable(block, stored);
    }

    public bool TryGetReceiptsIterator(ulong blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
    {
        // The storage answers true with an empty iterator when the body is absent, so it cannot be used to detect
        // that; probe for the body first, or eth_getLogs reports no logs at all for a derived block.
        if (storage.HasBlock(blockNumber, blockHash))
        {
            bool got = inner.TryGetReceiptsIterator(blockNumber, blockHash, out iterator);
            // The probe answers from the receipts cache too. If the entry is evicted between the probe and the read,
            // the read falls through to disk and yields the empty iterator the probe exists to rule out — re-probing
            // detects the eviction (nothing re-inserts a block whose body is deliberately not written).
            if (got && storage.HasBlock(blockNumber, blockHash)) return true;
            iterator.Dispose();
        }

        Block? found = FindBlock(blockHash);
        if (!TryRegenerate(found, out TxReceipt[]? regenerated))
        {
            ThrowIfUnanswerable(found);
            return inner.TryGetReceiptsIterator(blockNumber, blockHash, out iterator);
        }

        // The array-backed iterator is the same one InMemoryReceiptStorage uses; the obsoletion targets the
        // storage-side callers that should stream from disk, which a regenerated block by definition cannot.
#pragma warning disable 618
        iterator = new ReceiptsIterator(regenerated);
#pragma warning restore 618
        return true;
    }

    /// <summary>
    /// Reports that a block's receipts are neither stored nor reproducible, unless it has no receipts to begin with.
    /// </summary>
    /// <remarks>
    /// A caller cannot tell an empty result from an unanswerable one, so a block that does have transactions must
    /// fail loudly: silently returning no receipts reads as "no logs here" to an indexer.
    /// </remarks>
    private static TxReceipt[] Unavailable(Block? block, TxReceipt[] stored)
    {
        ThrowIfUnanswerable(block);
        return stored;
    }

    private static void ThrowIfUnanswerable(Block? block)
    {
        if (block is null || block.Transactions.Length == 0) return;
        throw new ResourceNotFoundException(
            $"Receipts for block {block.Number} ({block.Hash}) are neither stored nor reproducible from state history.");
    }

    private Block? FindBlock(Hash256 blockHash) =>
        blockFinder.FindBlock(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);

    private bool TryRegenerate(Block? block, [NotNullWhen(true)] out TxReceipt[]? receipts)
    {
        if (block is null || block.Transactions.Length == 0)
        {
            receipts = null;
            return false;
        }

        // Without this every repeated query for the same block pays another full block execution.
        if (_regenerated.TryGet(block.Hash!, out receipts)) return true;

        if (!regenerator.TryRegenerate(block, out receipts)) return false;

        _regenerated.Set(block.Hash!, receipts);
        return true;
    }
}
