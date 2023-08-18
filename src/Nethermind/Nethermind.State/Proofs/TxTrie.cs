// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Proofs;

/// <summary>
/// Represents a Patricia trie built of a collection of <see cref="Transaction"/>.
/// </summary>
public class TxTrie : PatriciaTree
{
    private static readonly TxDecoder _txDecoder = new();

    /// <param name="list">The collection to build the trie of.</param>
    /// <param name="canBuildProof">
    /// <c>true</c> to maintain an in-memory database for proof computation;
    /// otherwise, <c>false</c>.
    /// </param>
    public TxTrie(IEnumerable<Transaction>? list, bool canBuildProof = false)
        : base(canBuildProof
            ? new TrieStore(new MemDb(), NullLogManager.Instance)
            : new PooledBufferTrieNodeResolver(new TrieStore(NullDb.Instance, NullLogManager.Instance))
        ,EmptyTreeHash, false, false, NullLogManager.Instance)
    {
        CanBuildProof = canBuildProof;

        if (list?.Any() ?? false)
        {
            Initialize(list);
            UpdateRootHash();
        }
    }

    /// <summary>
    /// Computes the proofs for the index specified.
    /// </summary>
    /// <param name="index">The node index to compute the proof for.</param>
    /// <returns>The array of the computed proofs.</returns>
    /// <exception cref="NotSupportedException"></exception>
    public virtual byte[][] BuildProof(int index)
    {
        if (!CanBuildProof)
            throw new NotSupportedException("Building proofs not supported");

        var proofCollector = new ProofCollector(Rlp.Encode(index).Bytes);

        Accept(proofCollector, RootHash, new() { ExpectAccounts = false });

        return proofCollector.BuildResult();
    }

    private void Initialize(IEnumerable<Transaction> list)
    {
        int key = 0;

        // 3% allocations (2GB) on a Goerli 3M blocks fast sync due to calling transaction encoder here
        // Avoiding it would require pooling byte arrays and passing them as Spans to temporary trees
        // a temporary trie would be a trie that exists to create a state root only and then be disposed of
        foreach (Transaction? transaction in list)
        {
            int size = _txDecoder.GetLength(transaction, RlpBehaviors.SkipTypedWrapping);
            CappedArray<byte> buffer = new CappedArray<byte>(ArrayPool<byte>.Shared.Rent(size), size);

            RlpStream stream = buffer.AsRlpStream();
            _txDecoder.Encode(stream, transaction, RlpBehaviors.SkipTypedWrapping);
            Set(Rlp.Encode(key++).Bytes, buffer);
        }
    }

    protected virtual bool CanBuildProof { get; }

    public void ReturnBuffers()
    {

    }
}
