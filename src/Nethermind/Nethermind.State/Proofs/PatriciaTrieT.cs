// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs;

/// <summary>
/// An abstract class that represents a Patricia trie built of a collection of <see cref="T"/>.
/// </summary>
/// <typeparam name="T">The type of the elements in the collection used to build the trie.</typeparam>
public abstract class PatriciaTrie<T> : PatriciaTree
{
    private readonly IRlpStreamDecoder<T> _decoder;
    private readonly RlpBehaviors _behaviors;
    private readonly bool _canBuildProof;

    /// <param name="list">The collection to build the trie of.</param>
    /// <param name="decoder"></param>
    /// <param name="bufferPool"></param>
    /// <param name="canBuildProof">
    ///     <c>true</c> to maintain an in-memory database for proof computation;
    ///     otherwise, <c>false</c>.
    /// </param>
    /// <param name="behaviors"></param>
    protected PatriciaTrie(
        T[]? list,
        IRlpStreamDecoder<T> decoder,
        ICappedArrayPool bufferPool,
        bool canBuildProof,
        RlpBehaviors behaviors = RlpBehaviors.SkipTypedWrapping)
        : base(canBuildProof ? new MemDb() : NullDb.Instance, EmptyTreeHash, false, false, NullLogManager.Instance, bufferPool: bufferPool)
    {
        _decoder = decoder;
        _behaviors = behaviors;
        _canBuildProof = canBuildProof;

        if (list?.Length > 0)
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
        if (!_canBuildProof)
        {
            throw new NotSupportedException("Building proofs not supported");
        }

        var proofCollector = new ProofCollector(Rlp.Encode(index).Bytes);

        Accept(proofCollector, RootHash, new() { ExpectAccounts = false });

        return proofCollector.BuildResult();
    }

    private void Initialize(T[] list)
    {
        for (var key = 0; key < list.Length; key++)
        {
            CappedArray<byte> buffer = _decoder.EncodeToCappedArray(list[key], _bufferPool, _behaviors);
            CappedArray<byte> keyBuffer = key.EncodeToCappedArray(_bufferPool);
            Set(keyBuffer.AsSpan(), buffer);
        }
    }
}
