// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz.Merkleization;

public ref struct Merkleizer
{
    private readonly bool IsKthBitSet(int k) => (_filled & (1UL << k)) != 0;

    private void SetKthBit(int k) => _filled |= 1UL << k;

    private void UnsetKthBit(int k) => _filled &= ~(1UL << k);

    private readonly Span<UInt256> _chunks;
    private ulong _filled;

    public Merkleizer(Span<UInt256> chunks)
    {
        _chunks = chunks;
        _filled = 0;
    }

    public Merkleizer(int depth)
    {
        _chunks = new UInt256[depth + 1];
        _filled = 0;
    }

    public void Feed(UInt256 chunk) => FeedAtLevel(chunk, 0);

    private void FeedAtLevel(UInt256 chunk, int level)
    {
        for (int i = level; i < _chunks.Length; i++)
        {
            if (IsKthBitSet(i))
            {
                chunk = Merkle.HashConcatenation(_chunks[i], chunk, i);
                UnsetKthBit(i);
            }
            else
            {
                _chunks[i] = chunk;
                SetKthBit(i);
                break;
            }
        }
    }

    public void CalculateRoot(out UInt256 root)
    {
        int lowestSet = 0;
        while (true)
        {
            for (int i = lowestSet; i < _chunks.Length; i++)
            {
                if (IsKthBitSet(i))
                {
                    lowestSet = i;
                    break;
                }
            }

            if (lowestSet == _chunks.Length - 1)
            {
                break;
            }

            UInt256 chunk = Merkle.ZeroHashes[lowestSet];
            FeedAtLevel(chunk, lowestSet);
        }

        root = _chunks[^1];
    }
}
