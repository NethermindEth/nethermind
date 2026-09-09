// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz.Merkleization;

/// <summary>Accumulates SSZ chunks using one scratch entry per tree depth.</summary>
public ref struct Merkleizer
{
    private readonly Span<UInt256> _chunks;
    private readonly ulong _capacity;
    private ulong _filled;

    /// <summary>Creates an accumulator whose tree depth is one less than the scratch length.</summary>
    /// <remarks>Scratch needs 1 to 65 entries and does not need to be initialized.</remarks>
    public Merkleizer(Span<UInt256> chunks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunks.Length, 1, nameof(chunks));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunks.Length, 65, nameof(chunks));
        _chunks = chunks;
        _capacity = chunks.Length == 65 ? ulong.MaxValue : 1UL << (chunks.Length - 1);
        _filled = 0;
    }

    /// <summary>Creates an accumulator with allocated scratch for a tree of the given depth.</summary>
    public Merkleizer(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depth, 64);
        this = new Merkleizer(new UInt256[depth + 1]);
    }

    /// <summary>Appends a chunk, rejecting input that exceeds the tree capacity.</summary>
    public void Feed(UInt256 chunk)
    {
        if (_filled == _capacity)
        {
            throw new InvalidOperationException("Merkle tree capacity exceeded.");
        }

        // The leaf count is also the bitmap of occupied subtrees; appending carries its low bits.
        int level = BitOperations.TrailingZeroCount(~_filled);
        for (int i = 0; i < level; i++)
        {
            chunk = Merkle.HashConcatenation(_chunks[i], chunk, i);
        }

        _chunks[level] = chunk;
        _filled++;
    }

    /// <summary>Returns the zero-padded root without changing the accumulated chunks.</summary>
    public readonly void CalculateRoot(out UInt256 root)
    {
        int depth = _chunks.Length - 1;
        if (_filled == 0)
        {
            root = Merkle.ZeroHash(depth);
            return;
        }

        int level = BitOperations.TrailingZeroCount(_filled);
        root = _chunks[level];
        for (int i = level; i < depth; i++)
        {
            root = i != level && (_filled & (1UL << i)) != 0
                ? Merkle.HashConcatenation(_chunks[i], root, i)
                : Merkle.HashConcatenation(root, Merkle.ZeroHash(i), i);
        }
    }
}
