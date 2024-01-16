// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
public class TxTrie : PatriciaTrie<Transaction>
{
    private static readonly TxDecoder _txDecoder = new();

    /// <inheritdoc/>
    /// <param name="transactions">The transactions to build the trie of.</param>
    public TxTrie(IEnumerable<Transaction> transactions, bool canBuildProof = false, ICappedArrayPool? bufferPool = null)
        : base(transactions, canBuildProof, bufferPool: bufferPool) => ArgumentNullException.ThrowIfNull(transactions);

    protected override void Initialize(IEnumerable<Transaction> list)
    {
        int key = 0;

        foreach (Transaction? transaction in list)
        {
            CappedArray<byte> buffer = _txDecoder.EncodeToCappedArray(transaction, RlpBehaviors.SkipTypedWrapping, _bufferPool);
            CappedArray<byte> keyBuffer = (key++).EncodeToCappedArray(_bufferPool);

            Set(keyBuffer.AsSpan(), buffer);
        }
    }

    public static byte[][] CalculateProof(IList<Transaction> transactions, int index)
    {
        using TrackingCappedArrayPool cappedArray = new TrackingCappedArrayPool(transactions.Count * 4);
        byte[][] rootHash = new TxTrie(transactions, canBuildProof: true, bufferPool: cappedArray).BuildProof(index);
        return rootHash;
    }

    public static Hash256 CalculateRoot(IList<Transaction> transactions)
    {
        using TrackingCappedArrayPool cappedArray = new TrackingCappedArrayPool(transactions.Count * 4);
        Hash256 rootHash = new TxTrie(transactions, canBuildProof: false, bufferPool: cappedArray).RootHash;
        return rootHash;
    }
}
