// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs;

/// <summary>
/// Represents a Patricia trie built of a collection of <see cref="Transaction"/>.
/// </summary>
public class TxTrie : PatriciaTrie<Transaction>
{
    /// <inheritdoc/>
    /// <param name="transactions">The transactions to build the trie of.</param>
    /// <param name="bufferPool"></param>
    /// <param name="canBuildProof"></param>
    public TxTrie(Transaction[] transactions, ICappedArrayPool bufferPool, bool canBuildProof = false)
        : base(transactions, TxDecoder.Instance, bufferPool, canBuildProof) => ArgumentNullException.ThrowIfNull(transactions);

    public static byte[][] CalculateProof(Transaction[] transactions, int index)
    {
        using TrackingCappedArrayPool cappedArray = new(transactions.Length * 4);
        return new TxTrie(transactions, bufferPool: cappedArray, canBuildProof: true).BuildProof(index);
    }

    public static Hash256 CalculateRoot(Transaction[] transactions)
    {
        using TrackingCappedArrayPool cappedArray = new(transactions.Length * 4);
        return new TxTrie(transactions, bufferPool: cappedArray, canBuildProof: false).RootHash;
    }
}
