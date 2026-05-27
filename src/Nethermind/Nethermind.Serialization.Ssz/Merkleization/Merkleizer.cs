// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Merkleization;

public ref struct Merkleizer
{
    private readonly bool IsKthBitSet(int k) => (_filled & ((ulong)1 << k)) != 0;

    private void SetKthBit(int k) => _filled |= (ulong)1 << k;

    private void UnsetKthBit(int k) => _filled &= ~((ulong)1 << k);

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

    public void Feed(long value)
    {
        Merkle.Merkleize(out _chunks[^1], value);
        Feed(_chunks[^1]);
    }

    public void Feed(ReadOnlySpan<byte> data, int? limit = null)
    {
        if (limit is not null)
        {
            ulong chunkCount = ((ulong)limit.Value + 31) / 32;
            Merkle.Merkleize(out _chunks[^1], data, chunkCount);
            Merkle.MixIn(ref _chunks[^1], data.Length);
        }
        else if (data.Length is 0)
        {
            Merkle.Merkleize(out _chunks[^1], UInt256.Zero);
        }
        else
        {
            Merkle.Merkleize(out _chunks[^1], data);
        }

        Feed(_chunks[^1]);
    }

    public void Feed(bool value)
    {
        Merkle.Merkleize(out _chunks[^1], value);
        Feed(_chunks[^1]);
    }

    public void Feed(byte value)
    {
        Merkle.Merkleize(out _chunks[^1], value);
        Feed(_chunks[^1]);
    }

    public void Feed(ushort value)
    {
        Merkle.Merkleize(out _chunks[^1], value);
        Feed(_chunks[^1]);
    }

    public void Feed(uint value)
    {
        Merkle.Merkleize(out _chunks[^1], value);
        Feed(_chunks[^1]);
    }
    public void Feed(int value)
    {
        Merkle.Merkleize(out _chunks[^1], value);
        Feed(_chunks[^1]);
    }

    public void Feed(ulong value)
    {
        Merkle.Merkleize(out _chunks[^1], value);
        Feed(_chunks[^1]);
    }

    public void Feed(BitArray? vector)
    {
        if (vector is null) return;
        // bitfield_bytes
        byte[] bytes = new byte[(vector.Length + 7) / 8];
        vector.CopyTo(bytes, 0);

        Merkle.Merkleize(out _chunks[^1], bytes);
        Feed(_chunks[^1]);
    }

    public void Feed(BitArray? list, ulong maximumBitlistLength)
    {
        if (list is null) return;

        // chunk count
        ulong chunkCount = (maximumBitlistLength + 255) / 256;

        // bitfield_bytes
        byte[] bytes = new byte[(list.Length + 7) / 8];
        list.CopyTo(bytes, 0);

        Merkle.Merkleize(out _chunks[^1], bytes, chunkCount);
        Merkle.MixIn(ref _chunks[^1], list.Length);
        Feed(_chunks[^1]);
    }

    public void Feed(IEnumerable<ReadOnlyMemory<byte>>? value, ulong maxLength)
    {
        if (value is null)
        {
            return;
        }

        UInt256[] subRoots = ArrayPool<UInt256>.Shared.Rent(4);
        int count = 0;

        try
        {
            foreach (ReadOnlyMemory<byte> memory in value)
            {
                if (count == subRoots.Length)
                {
                    UInt256[] previous = subRoots;
                    subRoots = ArrayPool<UInt256>.Shared.Rent(count * 2);
                    previous.AsSpan(0, count).CopyTo(subRoots);
                    ArrayPool<UInt256>.Shared.Return(previous);
                }

                Merkle.Merkleize(out subRoots[count++], memory.Span);
            }

            Merkle.Merkleize(out _chunks[^1], subRoots.AsSpan(0, count), maxLength);
            Merkle.MixIn(ref _chunks[^1], count);
            Feed(_chunks[^1]);
        }
        finally
        {
            ArrayPool<UInt256>.Shared.Return(subRoots);
        }
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

    public UInt256 CalculateRoot()
    {
        CalculateRoot(out UInt256 result);
        return result;
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
