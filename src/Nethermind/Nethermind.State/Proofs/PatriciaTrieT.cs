// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.Trie;

namespace Nethermind.State.Trie;

/// <summary>
/// An abstract class that represents a Patricia trie built of a collection of <see cref="T"/>.
/// </summary>
/// <typeparam name="T">The type of the elements in the collection used to build the trie.</typeparam>
public abstract class PatriciaTrie<T> : PatriciaTree
{
    /// <param name="list">The collection to build the trie of.</param>
    /// <param name="canBuildProof">
    /// <c>true</c> to maintain an in-memory database for proof computation;
    /// otherwise, <c>false</c>.
    /// </param>
    public PatriciaTrie(IEnumerable<T>? list, bool canBuildProof)
        : base(canBuildProof ? new MemDb() : NullDb.Instance, EmptyTreeHash, false, false, NullLogManager.Instance)
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

    protected abstract void Initialize(IEnumerable<T> list);

    protected virtual bool CanBuildProof { get; }
}
