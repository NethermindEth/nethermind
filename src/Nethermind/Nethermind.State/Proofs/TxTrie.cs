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
using Nethermind.Trie.Pruning;

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

        // 3% allocations (2GB) on a Goerli 3M blocks fast sync due to calling transaction encoder here
        // Avoiding it would require pooling byte arrays and passing them as Spans to temporary trees
        // a temporary trie would be a trie that exists to create a state root only and then be disposed of
        foreach (Transaction? transaction in list)
        {
            int size = _txDecoder.GetLength(transaction, RlpBehaviors.SkipTypedWrapping);
            CappedArray<byte> buffer = _bufferPool.SafeRentBuffer(size);
            _txDecoder.Encode(buffer.AsRlpStream(), transaction, RlpBehaviors.SkipTypedWrapping);

            int theKey = key++;
            CappedArray<byte> keyBuffer = _bufferPool.SafeRentBuffer(Rlp.LengthOf(theKey));
            keyBuffer.AsRlpStream().Encode(theKey);
            Set(keyBuffer.AsSpan(), buffer);
        }
    }

    public static Keccak CalculateRoot(IList<Transaction> transactions)
    {
        TrackedPooledCappedArrayPool cappedArray = new TrackedPooledCappedArrayPool(transactions.Count * 4);
        Keccak rootHash = new TxTrie(transactions, false, bufferPool: cappedArray).RootHash;
        cappedArray.ReturnAll();
        return rootHash;
    }
}
