// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz.Merkleization;

public ref struct Merkleizer
{
    private readonly bool IsKthBitSet(int k) => k == 64 ? _isFull : (_filled & (1UL << k)) != 0;

    private void SetKthBit(int k)
    {
        if (k == 64)
        {
            _isFull = true;
        }
        else
        {
            _filled |= 1UL << k;
        }
    }

    private void UnsetKthBit(int k)
    {
        if (k == 64)
        {
            _isFull = false;
        }
        else
        {
            _filled &= ~(1UL << k);
        }
    }

    private readonly Span<UInt256> _chunks;
    private ulong _filled;
    private bool _isFull;

    public Merkleizer(Span<UInt256> chunks)
    {
        ArgumentOutOfRangeException.ThrowIfZero(chunks.Length, nameof(chunks));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunks.Length, 65, nameof(chunks));

        _chunks = chunks;
        _filled = 0;
        _isFull = false;
    }

    public Merkleizer(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth, nameof(depth));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depth, 64, nameof(depth));

        _chunks = new UInt256[depth + 1];
        _filled = 0;
        _isFull = false;
    }

    public void Feed(UInt256 chunk)
    {
        if (_chunks.IsEmpty || IsKthBitSet(_chunks.Length - 1))
        {
            throw new InvalidOperationException("Cannot feed more chunks than the merkleizer capacity.");
        }

        FeedAtLevel(chunk, 0);
    }

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
