// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.Eez.Execution.Stateless;

/// <summary>
/// The transaction root, receipts root and bloom of growing prefixes of one block. Each prefix extends the one before
/// it, so every transaction and receipt is encoded, inserted and accumulated once, and only the changed trie paths are
/// hashed for the next root. The values are the ones <see cref="Nethermind.State.Proofs.TxTrie"/>,
/// <see cref="Nethermind.State.Proofs.ReceiptTrie"/> and a bloom accumulated from the first receipt give for the same prefix.
/// </summary>
/// <remarks>
/// Transactions are encoded from their fields. Block processing hashes every transaction, which drops the encoding it
/// was received with, so the transaction trie of an executed block encodes them the same way.
/// </remarks>
internal sealed class CandidatePrefix : IDisposable
{
    private static readonly IRlpDecoder<TxReceipt> ReceiptTrieDecoder = Rlp.GetDecoderOrThrow<TxReceipt>(RlpDecoderKey.Trie);

    private readonly TrackingCappedArrayPool _pool;
    private readonly PatriciaTree _transactions;
    private readonly PatriciaTree _receipts;
    private readonly RlpBehaviors _receiptBehavior;
    private readonly Bloom _bloom = new();

    public CandidatePrefix(IReceiptSpec spec, int capacity)
    {
        _pool = new TrackingCappedArrayPool(capacity * 4, canBeParallel: false);
        _transactions = new PatriciaTree(NullDb.Instance, PatriciaTree.EmptyTreeHash, false, NullLogManager.Instance, _pool);
        _receipts = new PatriciaTree(NullDb.Instance, PatriciaTree.EmptyTreeHash, false, NullLogManager.Instance, _pool);
        _receiptBehavior = (spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None)
            | (spec.IsEip7668Enabled ? RlpBehaviors.Eip7668Receipts : RlpBehaviors.None)
            | RlpBehaviors.SkipTypedWrapping;
    }

    /// <summary>The number of transactions and receipts in the prefix.</summary>
    public int Count { get; private set; }

    public Hash256 TxRoot { get; private set; } = Keccak.EmptyTreeHash;

    public Hash256 ReceiptsRoot { get; private set; } = Keccak.EmptyTreeHash;

    /// <summary>The bloom of the prefix; it changes when the prefix grows.</summary>
    public Bloom Bloom => _bloom;

    /// <summary>Grows the prefix to the first <paramref name="count"/> transactions and receipts and hashes its roots.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> shrinks the prefix or exceeds the block.</exception>
    public void Extend(ReadOnlySpan<Transaction> transactions, ReadOnlySpan<TxReceipt> receipts, int count)
    {
        if (count < Count || count > transactions.Length || count > receipts.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "A prefix can only grow within the block.");
        }

        for (int index = Count; index < count; index++)
        {
            _transactions.Set(Rlp.EncodeToCappedArray(index, _pool).AsSpan(),
                TxDecoder.Instance.EncodeToCappedArray(transactions[index], RlpBehaviors.SkipTypedWrapping, _pool));
            TxReceipt receipt = receipts[index];
            _receipts.Set(Rlp.EncodeToCappedArray(index, _pool).AsSpan(), ReceiptTrieDecoder.EncodeToCappedArray(receipt, _receiptBehavior, _pool));
            _bloom.Accumulate(receipt.Bloom);
        }

        if (count != Count)
        {
            Count = count;
            _transactions.UpdateRootHash();
            _receipts.UpdateRootHash();
            TxRoot = _transactions.RootHash;
            ReceiptsRoot = _receipts.RootHash;
        }
    }

    public void Dispose() => _pool.Dispose();
}
