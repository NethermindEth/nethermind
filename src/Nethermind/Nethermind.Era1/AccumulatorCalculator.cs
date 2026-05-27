// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merkleization;

namespace Nethermind.Era1;

// https://github.com/ethereum/portal-network-specs/blob/master/history/history-network.md#algorithms
public class AccumulatorCalculator : IDisposable
{
    private const int TreeDepth = 13;   // log2(8192)
    private const int ProofLength = 15; // 1 (HeaderRecord field) + 13 (tree) + 1 (length mixin)

    private readonly ArrayPoolList<ReadOnlyMemory<byte>> _roots = new(EraWriter.MaxEra1Size);
    private readonly ArrayPoolList<UInt256> _totalDifficulties = new(EraWriter.MaxEra1Size);

    public void Add(Hash256 headerHash, UInt256 td)
    {
        Merkleizer merkleizer = new(Merkle.NextPowerOfTwoExponent(2));
        Merkle.Merkleize(out UInt256 headerHashRoot, headerHash.Bytes);
        merkleizer.Feed(headerHashRoot);
        merkleizer.Feed(td);
        merkleizer.CalculateRoot(out UInt256 root);
        _roots.Add(root.ToLittleEndian());
        _totalDifficulties.Add(td);
    }

    public ValueHash256 ComputeRoot()
    {
        int count = _roots.Count;
        UInt256[] subRoots = ArrayPool<UInt256>.Shared.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                Merkle.Merkleize(out subRoots[i], _roots[i].Span);
            }

            Merkle.Merkleize(out UInt256 root, subRoots.AsSpan(0, count), EraWriter.MaxEra1Size);
            Merkle.MixIn(ref root, count);
            return new ValueHash256(root.ToLittleEndian());
        }
        finally
        {
            ArrayPool<UInt256>.Shared.Return(subRoots);
        }
    }

    public ValueHash256[] GetProof(int blockIndex)
    {
        if ((uint)blockIndex >= (uint)_roots.Count)
            throw new ArgumentOutOfRangeException(nameof(blockIndex), $"Block index {blockIndex} is out of range [0, {_roots.Count - 1}].");

        int count = _roots.Count;
        ValueHash256[] proof = new ValueHash256[ProofLength];

        // Level 0: sibling of block_hash = total_difficulty as 32-byte LE SSZ chunk
        proof[0] = new ValueHash256(_totalDifficulties[blockIndex].ToLittleEndian());

        // Build the flat binary tree over MaxEra1Size HeaderRecord roots.
        using ArrayPoolList<byte> flatTreeBuffer = new(2 * EraWriter.MaxEra1Size * 32, 2 * EraWriter.MaxEra1Size * 32);
        Span<byte> flatTree = flatTreeBuffer.AsSpan();
        flatTree.Clear();
        for (int i = 0; i < count; i++)
        {
            _roots[i].Span.CopyTo(flatTree.Slice((EraWriter.MaxEra1Size + i) * 32, 32));
        }

        Span<byte> combined = stackalloc byte[64];
        for (int i = EraWriter.MaxEra1Size - 1; i >= 1; i--)
        {
            ReadOnlySpan<byte> left = flatTree.Slice(2 * i * 32, 32);
            ReadOnlySpan<byte> right = flatTree.Slice((2 * i + 1) * 32, 32);
            left.CopyTo(combined);
            right.CopyTo(combined[32..]);
            SHA256.TryHashData(combined, flatTree.Slice(i * 32, 32), out _);
        }

        int current = EraWriter.MaxEra1Size + blockIndex;
        for (int i = 0; i < TreeDepth; i++)
        {
            int sibling = current ^ 1;
            proof[1 + i] = new ValueHash256(flatTree.Slice(sibling * 32, 32));
            current >>= 1;
        }

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
