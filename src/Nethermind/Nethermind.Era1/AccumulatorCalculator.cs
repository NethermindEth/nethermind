// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Era1;

// https://github.com/ethereum/portal-network-specs/blob/master/history/history-network.md#algorithms
public class AccumulatorCalculator : IDisposable
{
    private const int TreeDepth = 13;   // log2(8192)
    private const int ProofLength = 15; // 1 (HeaderRecord field) + 13 (tree) + 1 (length mixin)

    private readonly ArrayPoolList<ValueHash256> _roots = new(EraWriter.MaxEra1Size);
    private readonly ArrayPoolList<UInt256> _totalDifficulties = new(EraWriter.MaxEra1Size);

    public void Add(Hash256 headerHash, UInt256 td)
    {
        HeaderRecord.Merkleize(new()
        {
            BlockHash = headerHash,
            TotalDifficulty = td
        }, out UInt256 root);
        _roots.Add(ToValueHash256(root));
        _totalDifficulties.Add(td);
    }

    public ValueHash256 ComputeRoot()
    {
        HeaderRecordRoots.Merkleize(new() { Roots = _roots }, out UInt256 root);
        return ToValueHash256(root);
    }

    private static ValueHash256 ToValueHash256(UInt256 root)
    {
        Span<byte> bytes = stackalloc byte[ValueHash256.MemorySize];
        root.ToLittleEndian(bytes);
        return new ValueHash256(bytes);
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
            _roots[i].Bytes.CopyTo(flatTree.Slice((EraWriter.MaxEra1Size + i) * 32, 32));
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

[SszContainer]
internal partial struct HeaderRecord
{
    public Hash256 BlockHash { get; set; }
    public UInt256 TotalDifficulty { get; set; }
}

[SszContainer(isCollectionItself: true)]
internal partial struct HeaderRecordRoots
{
    [SszList(EraWriter.MaxEra1Size)]
    public ArrayPoolList<ValueHash256> Roots { get; set; }
}
