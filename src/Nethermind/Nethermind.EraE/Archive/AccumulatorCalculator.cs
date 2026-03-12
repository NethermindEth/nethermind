// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merkleization;

namespace Nethermind.EraE.Archive;

// https://github.com/ethereum/portal-network-specs/blob/master/history/history-network.md#algorithms
// SSZ hash_tree_root of List(HeaderRecord, 8192) where HeaderRecord = (block_hash, total_difficulty)
public sealed class AccumulatorCalculator : IDisposable
{
    private const int Limit = EraWriter.MaxEraSize; // 8192
    private const int TreeDepth = 13;               // log2(8192)
    private const int ProofLength = 15;             // 1 (HeaderRecord field) + 13 (tree) + 1 (length mixin)

    private readonly ArrayPoolList<ReadOnlyMemory<byte>> _roots = new(Limit);
    private readonly ArrayPoolList<UInt256> _totalDifficulties = new(Limit);

    public void Add(Hash256 headerHash, UInt256 td)
    {
        Merkleizer merkleizer = new(Merkle.NextPowerOfTwoExponent(2));
        merkleizer.Feed(headerHash.Bytes);
        merkleizer.Feed(td);
        _roots.Add(merkleizer.CalculateRoot().ToLittleEndian());
        _totalDifficulties.Add(td);
    }

    public ValueHash256 ComputeRoot()
    {
        Merkleizer merkleizer = new(0);
        merkleizer.Feed(_roots, Limit);
        UInt256 root = merkleizer.CalculateRoot();
        return new ValueHash256(MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1)));
    }

    /// <summary>
    /// Returns the 15-element SSZ Merkle proof from the <c>block_hash</c> leaf at
    /// <paramref name="blockIndex"/> to the epoch accumulator root.
    /// Must be called after all blocks have been added via <see cref="Add"/>.
    /// </summary>
    /// <remarks>
    /// Proof layout (0-indexed):
    ///   [0]      sibling of block_hash within HeaderRecord = total_difficulty as 32-byte LE
    ///   [1..13]  siblings in the binary tree over 8192 HeaderRecord roots (depth 13)
    ///   [14]     length mixin = uint256(count) as 32-byte LE
    /// </remarks>
    public ValueHash256[] GetProof(int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= _roots.Count)
            throw new ArgumentOutOfRangeException(nameof(blockIndex), $"Block index {blockIndex} is out of range [0, {_roots.Count - 1}].");

        int count = _roots.Count;
        ValueHash256[] proof = new ValueHash256[ProofLength];

        // Level 0: sibling of block_hash = total_difficulty as 32-byte LE SSZ chunk
        proof[0] = new ValueHash256(_totalDifficulties[blockIndex].ToLittleEndian());

        // Build the flat binary tree over `Limit` HeaderRecord roots.
        // Layout: node i occupies flatTree[i*32..(i+1)*32].
        // Leaves: tree[Limit..2*Limit-1]. Empty leaves stay zero (= ZeroHashes[0]).
        byte[] flatTree = new byte[2 * Limit * 32];

        for (int i = 0; i < count; i++)
            _roots[i].Span.CopyTo(flatTree.AsSpan((Limit + i) * 32, 32));

        // Build internal nodes bottom-up (levels TreeDepth-1 down to 1).
        Span<byte> combined = stackalloc byte[64];
        for (int i = Limit - 1; i >= 1; i--)
        {
            ReadOnlySpan<byte> left = flatTree.AsSpan(2 * i * 32, 32);
            ReadOnlySpan<byte> right = flatTree.AsSpan((2 * i + 1) * 32, 32);
            left.CopyTo(combined);
            right.CopyTo(combined[32..]);
            SHA256.TryHashData(combined, flatTree.AsSpan(i * 32, 32), out _);
        }

        // Extract siblings from leaf to root (13 levels: proof[1..13]).
        int current = Limit + blockIndex;
        for (int i = 0; i < TreeDepth; i++)
        {
            int sibling = current ^ 1;
            proof[1 + i] = new ValueHash256(flatTree.AsSpan(sibling * 32, 32));
            current >>= 1;
        }

        // Level 14: length mixin = uint256(count) serialized as 32-byte LE
        Span<byte> lenBytes = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(lenBytes, (ulong)count);
        proof[14] = new ValueHash256(lenBytes);

        return proof;
    }

    internal void Clear()
    {
        _roots.Clear();
        _totalDifficulties.Clear();
    }

    public void Dispose()
    {
        _roots.Dispose();
        _totalDifficulties.Dispose();
    }
}
