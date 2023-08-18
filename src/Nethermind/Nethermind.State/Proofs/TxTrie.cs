// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
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
    public TxTrie(IEnumerable<Transaction> transactions, bool canBuildProof = false)
        : base(transactions, canBuildProof) => ArgumentNullException.ThrowIfNull(transactions);

    protected override void Initialize(IEnumerable<Transaction> list)
    {
        int key = 0;

        // 3% allocations (2GB) on a Goerli 3M blocks fast sync due to calling transaction encoder here
        // Avoiding it would require pooling byte arrays and passing them as Spans to temporary trees
        // a temporary trie would be a trie that exists to create a state root only and then be disposed of
        foreach (Transaction? transaction in list)
        {
            Rlp transactionRlp = _txDecoder.Encode(transaction, RlpBehaviors.SkipTypedWrapping);
            Set(Rlp.Encode(key++).Bytes, transactionRlp.Bytes);
        }
    }

    public static Keccak CalculateRoot(IEnumerable<Transaction> transactions)
    {
        var bufferPool = new TrackedPooledBufferTrieStore(new TrieStore(NullDb.Instance, NullLogManager.Instance));
        var tree = new PatriciaTree(bufferPool , NullLogManager.Instance);

        int key = 0;
        foreach (Transaction? transaction in transactions)
        {
            Rlp transactionRlp = _txDecoder.Encode(transaction, RlpBehaviors.SkipTypedWrapping);
            tree.Set(Rlp.Encode(key++).Bytes, transactionRlp.Bytes);

            int size = _txDecoder.GetLength(transaction, RlpBehaviors.SkipTypedWrapping);
            CappedArray<byte> buffer = bufferPool.SafeRentBuffer(size);

            RlpStream stream = buffer.AsRlpStream();
            _txDecoder.Encode(stream, transaction, RlpBehaviors.SkipTypedWrapping);
            tree.Set(Rlp.Encode(key++).Bytes, buffer);
        }

        tree.UpdateRootHash();

        Keccak root = tree.RootHash;

        bufferPool.ReturnAll();
        return root;
    }
}
