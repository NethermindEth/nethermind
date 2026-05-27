// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Merkleization;

public ref struct Merkleizer
{
    private readonly bool IsKthBitSet(int k) => (_filled & ((ulong)1 << k)) != 0;

    private void SetKthBit(int k) => _filled |= (ulong)1 << k;

    private void UnsetKthBit(int k) => _filled &= ~((ulong)1 << k);

    private readonly Span<UInt256> _chunks;
    private ulong _filled;

    public readonly UInt256 PartChunk
    {
        get
        {
            _chunks[^1] = UInt256.Zero;
            return _chunks[^1];
        }
    }

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

    public void Feed(ReadOnlySpan<byte> data, int? limit = null) =>
        FeedSpan(data, data.Length, sizeof(byte), limit);

    public void Feed(ReadOnlySpan<ushort> data, int? limit = null) =>
        FeedSpan(MemoryMarshal.Cast<ushort, byte>(data), data.Length, sizeof(ushort), limit);

    public void Feed(ReadOnlySpan<short> data, int? limit = null) =>
        FeedSpan(MemoryMarshal.Cast<short, byte>(data), data.Length, sizeof(short), limit);

    public void Feed(ReadOnlySpan<uint> data, int? limit = null) =>
        FeedSpan(MemoryMarshal.Cast<uint, byte>(data), data.Length, sizeof(uint), limit);

    public void Feed(ReadOnlySpan<int> data, int? limit = null) =>
        FeedSpan(MemoryMarshal.Cast<int, byte>(data), data.Length, sizeof(int), limit);

    public void Feed(ReadOnlySpan<ulong> data, int? limit = null) =>
        FeedSpan(MemoryMarshal.Cast<ulong, byte>(data), data.Length, sizeof(ulong), limit);

    public void Feed(ReadOnlySpan<long> data, int? limit = null) =>
        FeedSpan(MemoryMarshal.Cast<long, byte>(data), data.Length, sizeof(long), limit);

    public void Feed(ReadOnlySpan<UInt128> data, int? limit = null) =>
        FeedSpan(MemoryMarshal.Cast<UInt128, byte>(data), data.Length, 16, limit);

    private void FeedSpan(ReadOnlySpan<byte> byteData, int elementCount, int elementSize, int? limit)
    {
        if (limit is not null)
        {
            ulong chunkCount = ((ulong)limit.Value * (ulong)elementSize + 31) / 32;
            Merkle.Merkleize(out _chunks[^1], byteData, chunkCount);
            Merkle.MixIn(ref _chunks[^1], elementCount);
        }
        else if (byteData.Length is 0)
        {
            Merkle.Merkleize(out _chunks[^1], UInt256.Zero);
        }
        else
        {
            Merkle.Merkleize(out _chunks[^1], byteData);
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
    public void Feed(int? value)
    {
        if (value is null)
        {
            return;
        }
        Merkle.Merkleize(out _chunks[^1], value.Value);
        Feed(_chunks[^1]);
    }

    public void Feed(ulong value)
    {
        Merkle.Merkleize(out _chunks[^1], value);
        Feed(_chunks[^1]);
    }

    public void Feed(byte[]? value)
    {
        if (value is null)
        {
            return;
        }

        Merkle.Merkleize(out _chunks[^1], value);
        Feed(_chunks[^1]);
    }

    public void FeedBits(byte[]? value, uint limit)
    {
        if (value is null)
        {
            return;
        }

        Merkle.MerkleizeBits(out _chunks[^1], value, limit);
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

    public void Feed(IReadOnlyList<byte[]> value, ulong maxLength)
    {
        if (value is null)
        {
            return;
        }

        UInt256[] subRoots = ArrayPool<UInt256>.Shared.Rent(value.Count);

        try
        {
            for (int i = 0; i < value.Count; i++)
            {
                Merkle.Merkleize(out subRoots[i], value[i]);
            }

            Merkle.Merkleize(out _chunks[^1], subRoots.AsSpan(0, value.Count), maxLength);
            Merkle.MixIn(ref _chunks[^1], value.Count);
            Feed(_chunks[^1]);
        }
        finally
        {
            ArrayPool<UInt256>.Shared.Return(subRoots);
        }
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

    public void Feed(IReadOnlyList<ulong> value, ulong maxLength)
    {
        // TODO: If UInt256 is the correct memory layout
        UInt256[] subRoots = ArrayPool<UInt256>.Shared.Rent(value.Count);

        try
        {
            for (int i = 0; i < value.Count; i++)
            {
                Merkle.Merkleize(out subRoots[i], value[i]);
            }

            Merkle.Merkleize(out _chunks[^1], subRoots.AsSpan(0, value.Count), maxLength);
            Merkle.MixIn(ref _chunks[^1], value.Count);
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
