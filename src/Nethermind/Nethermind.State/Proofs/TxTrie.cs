// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    public TxTrie(ReadOnlySpan<Transaction> transactions, bool canBuildProof = false, ICappedArrayPool? bufferPool = null)
        : base(transactions, canBuildProof, bufferPool: bufferPool) { }

    protected override void Initialize(ReadOnlySpan<Transaction> list)
    {
        int key = 0;

        foreach (Transaction? transaction in list)
        {
            SpanSource buffer = _txDecoder.EncodeToSpanSource(transaction, rlpBehaviors: RlpBehaviors.SkipTypedWrapping, bufferPool: _bufferPool);
            SpanSource keyBuffer = key.EncodeToSpanSource(_bufferPool);
            key++;

            Set(keyBuffer.Span, buffer);
        }
    }

    public static byte[][] CalculateProof(ReadOnlySpan<Transaction> transactions, int index)
    {
        using TrackingCappedArrayPool cappedArray = new(transactions.Length * 4);
        byte[][] rootHash = new TxTrie(transactions, canBuildProof: true, bufferPool: cappedArray).BuildProof(index);
        return rootHash;
    }

    public static Hash256 CalculateRoot(ReadOnlySpan<Transaction> transactions)
    {
        using TrackingCappedArrayPool cappedArray = new(transactions.Length * 4);
        Hash256 rootHash = new TxTrie(transactions, canBuildProof: false, bufferPool: cappedArray).RootHash;
        return rootHash;
    }
}
