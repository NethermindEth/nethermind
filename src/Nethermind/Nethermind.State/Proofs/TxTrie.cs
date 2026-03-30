// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Trie;
using Nethermind.Trie;

namespace Nethermind.State.Proofs;

/// <summary>
/// Represents a Patricia trie built of a collection of <see cref="Transaction"/>.
/// </summary>
public sealed class TxTrie : PatriciaTrie<Transaction>
{
    private static readonly TxDecoder _txDecoder = TxDecoder.Instance;

    /// <inheritdoc/>
    /// <param name="transactions">The transactions to build the trie of.</param>
    public TxTrie(ReadOnlySpan<Transaction> transactions, bool canBuildProof = false, ICappedArrayPool? bufferPool = null, bool canBeParallel = true)
        : base(transactions, canBuildProof, bufferPool: bufferPool, canBeParallel: canBeParallel) { }

    protected override void Initialize(ReadOnlySpan<Transaction> list)
    {
        int key = 0;

        foreach (Transaction? transaction in list)
        {
            ref readonly Memory<byte> rlp = ref transaction.PreHash;
            CappedArray<byte> buffer = (rlp.Length > 0) ?
                CopyExistingRlp(rlp.Span, _bufferPool) :
                _txDecoder.EncodeToCappedArray(transaction, rlpBehaviors: RlpBehaviors.SkipTypedWrapping, bufferPool: _bufferPool);
            CappedArray<byte> keyBuffer = key.EncodeToCappedArray(_bufferPool);
            key++;

            Set(keyBuffer.AsSpan(), buffer);
        }

        static CappedArray<byte> CopyExistingRlp(ReadOnlySpan<byte> rlp, ICappedArrayPool? bufferPool)
        {
            CappedArray<byte> buffer = bufferPool.SafeRent(rlp.Length);
            rlp.CopyTo(buffer.AsSpan());
            return buffer;
        }
    }

    private void InitializeFromEncodedTransactions(ReadOnlySpan<byte[]> list)
    {
        for (int key = 0; key < list.Length; key++)
        {
            CappedArray<byte> keyBuffer = key.EncodeToCappedArray(_bufferPool);
            Set(keyBuffer.AsSpan(), list[key]);
        }
    }

    public static byte[][] CalculateProof(ReadOnlySpan<Transaction> transactions, int index)
    {
        bool canBeParallel = transactions.Length > MinItemsForParallelRootHash;
        using TrackingCappedArrayPool cappedArray = new(transactions.Length * 4, canBeParallel: canBeParallel);
        byte[][] rootHash = new TxTrie(transactions, canBuildProof: true, bufferPool: cappedArray, canBeParallel: canBeParallel).BuildProof(index);
        return rootHash;
    }

    public static Hash256 CalculateRoot(ReadOnlySpan<Transaction> transactions)
    {
        bool canBeParallel = transactions.Length > MinItemsForParallelRootHash;
        using TrackingCappedArrayPool cappedArray = new(transactions.Length * 4, canBeParallel: canBeParallel);
        Hash256 rootHash = new TxTrie(transactions, canBuildProof: false, bufferPool: cappedArray, canBeParallel: canBeParallel).RootHash;
        return rootHash;
    }

    public static Hash256 CalculateRoot(ReadOnlySpan<byte[]> encodedTransactions)
    {
        bool canBeParallel = encodedTransactions.Length > MinItemsForParallelRootHash;
        using TrackingCappedArrayPool cappedArray = new(encodedTransactions.Length * 4, canBeParallel: canBeParallel);
        TxTrie txTrie = new(ReadOnlySpan<Transaction>.Empty, canBuildProof: false, bufferPool: cappedArray, canBeParallel: canBeParallel);
        txTrie.InitializeFromEncodedTransactions(encodedTransactions);
        txTrie.UpdateRootHash(canBeParallel);
        return txTrie.RootHash;
    }
}
