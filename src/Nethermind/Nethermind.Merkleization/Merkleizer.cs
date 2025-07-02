// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Merkleization;

public ref struct Merkleizer
{
    public readonly bool IsKthBitSet(int k)
    {
        return (_filled & ((ulong)1 << k)) != 0;
    }

    public void SetKthBit(int k)
    {
        _filled |= (ulong)1 << k;
    }

    public void UnsetKthBit(int k)
    {
        _filled &= ~((ulong)1 << k);
    }

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

    public void Feed(UInt256 chunk)
    {
        FeedAtLevel(chunk, 0);
    }

    public void Feed(long value)
    {
        Merkle.Merkleize(out _chunks[^1], value);
        Feed(_chunks[^1]);
    }

    public void Feed(ReadOnlySpan<byte> data, int? limit = null)
    {
        if (data.Length is 0)
        {
            Merkle.Merkleize(out _chunks[^1], UInt256.Zero);
        }
        else
        {
            Merkle.Merkleize(out _chunks[^1], data);
        }

        if (limit is not null) Merkle.MixIn(ref _chunks[^1], limit.Value);

        Feed(_chunks[^1]);
    }

    public void Feed(ReadOnlySpan<ushort> data, int? limit = null)
    {
        if (data.Length is 0)
        {
            Merkle.Merkleize(out _chunks[^1], UInt256.Zero);
        }
        else
        {
            Merkle.Merkleize(out _chunks[^1], MemoryMarshal.Cast<ushort, byte>(data));
        }

        if (limit is not null) Merkle.MixIn(ref _chunks[^1], limit.Value);

        Feed(_chunks[^1]);
    }

    public void Feed(ReadOnlySpan<short> data, int? limit = null)
    {
        if (data.Length is 0)
        {
            Merkle.Merkleize(out _chunks[^1], UInt256.Zero);
        }
        else
        {
            Merkle.Merkleize(out _chunks[^1], MemoryMarshal.Cast<short, byte>(data));
        }

        if (limit is not null) Merkle.MixIn(ref _chunks[^1], limit.Value);

        Feed(_chunks[^1]);
    }

    public void Feed(ReadOnlySpan<uint> data, int? limit = null)
    {
        if (data.Length is 0)
        {
            Merkle.Merkleize(out _chunks[^1], UInt256.Zero);
        }
        else
        {
            Merkle.Merkleize(out _chunks[^1], MemoryMarshal.Cast<uint, byte>(data));
        }

        if (limit is not null) Merkle.MixIn(ref _chunks[^1], limit.Value);

        Feed(_chunks[^1]);
    }

    public void Feed(ReadOnlySpan<int> data, int? limit = null)
    {
        if (data.Length is 0)
        {
            Merkle.Merkleize(out _chunks[^1], UInt256.Zero);
        }
        else
        {
            Merkle.Merkleize(out _chunks[^1], MemoryMarshal.Cast<int, byte>(data));
        }

        if (limit is not null) Merkle.MixIn(ref _chunks[^1], limit.Value);

        Feed(_chunks[^1]);
    }

    public void Feed(ReadOnlySpan<ulong> data, int? limit = null)
    {
        if (data.Length is 0)
        {
            Merkle.Merkleize(out _chunks[^1], UInt256.Zero);
        }
        else
        {
            Merkle.Merkleize(out _chunks[^1], MemoryMarshal.Cast<ulong, byte>(data));
        }

        if (limit is not null) Merkle.MixIn(ref _chunks[^1], limit.Value);

        Feed(_chunks[^1]);
    }

    public void Feed(ReadOnlySpan<long> data, int? limit = null)
    {
        if (data.Length is 0)
        {
            Merkle.Merkleize(out _chunks[^1], UInt256.Zero);
        }
        else
        {
            Merkle.Merkleize(out _chunks[^1], MemoryMarshal.Cast<long, byte>(data));
        }

        if (limit is not null) Merkle.MixIn(ref _chunks[^1], limit.Value);

        Feed(_chunks[^1]);
    }

    public void Feed(ReadOnlySpan<UInt128> data, int? limit = null)
    {
        if (data.Length is 0)
        {
            Merkle.Merkleize(out _chunks[^1], UInt256.Zero);
        }
        else
        {
            Merkle.Merkleize(out _chunks[^1], MemoryMarshal.Cast<UInt128, byte>(data));
        }

        if (limit is not null) Merkle.MixIn(ref _chunks[^1], limit.Value);

        Feed(_chunks[^1]);
    }

    public void Feed(bool value)
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

        using ArrayPoolSpan<UInt256> subRoots = new(value.Count);

        for (int i = 0; i < value.Count; i++)
        {
            Merkle.Merkleize(out subRoots[i], value[i]);
        }

        Merkle.Merkleize(out _chunks[^1], subRoots, maxLength);
        Merkle.MixIn(ref _chunks[^1], value.Count);
        Feed(_chunks[^1]);
    }

    public void Feed(IEnumerable<ReadOnlyMemory<byte>>? value, ulong maxLength)
    {
        if (value is null)
        {
            return;
        }

        using ArrayPoolSpan<UInt256> subRoots = new(value.Count());
        int i = 0;

        foreach (ReadOnlyMemory<byte> memory in value)
        {
            Merkle.Merkleize(out UInt256 root, memory.Span);
            subRoots[i++] = root;
        }

        Merkle.Merkleize(out _chunks[^1], subRoots, maxLength);
        Merkle.MixIn(ref _chunks[^1], subRoots.Length);
        Feed(_chunks[^1]);
    }

    public void Feed(Bytes32 value)
    {
        // TODO: Is this going to have correct endianness? (the ulongs inside UInt256 are the correct order,
        // and if only used as memory to store bytes, the native order of a ulong (bit or little) shouldn't matter)
        Feed(MemoryMarshal.Cast<byte, UInt256>(value.AsSpan())[0]);
    }

    public void Feed(Root value)
    {
        Feed(MemoryMarshal.Cast<byte, UInt256>(value.AsSpan())[0]);
    }

    public void Feed(IReadOnlyList<Bytes32> value)
    {
        // TODO: If the above MemoryMarshal.Cast of a single Bytes32, we could use that here
        // (rather than the CreateFromLittleEndian() that wants an (unnecessarily) writeable Span.)
        // Better yet, just MemoryMarshal.Cast the entire span and pass directly to Merkle.Merkleize ?
        using ArrayPoolSpan<UInt256> input = new(value.Count);
        for (int i = 0; i < value.Count; i++)
        {
            Merkle.Merkleize(out input[i], value[i]);
        }

        Merkle.Merkleize(out _chunks[^1], input);
        Feed(_chunks[^1]);
    }

    public void Feed(IReadOnlyList<Bytes32> value, ulong maxLength)
    {
        using ArrayPoolSpan<UInt256> subRoots = new(value.Count);

        for (int i = 0; i < value.Count; i++)
        {
            Merkle.Merkleize(out subRoots[i], value[i]);
        }

        Merkle.Merkleize(out _chunks[^1], subRoots, maxLength);
        Merkle.MixIn(ref _chunks[^1], value.Count);
        Feed(_chunks[^1]);
    }

    public void Feed(IReadOnlyList<ulong> value, ulong maxLength)
    {
        // TODO: If UInt256 is the correct memory layout
        using ArrayPoolSpan<UInt256> subRoots = new(value.Count);

        for (int i = 0; i < value.Count; i++)
        {
            Merkle.Merkleize(out subRoots[i], value[i]);
        }

        Merkle.Merkleize(out _chunks[^1], subRoots, maxLength);
        Merkle.MixIn(ref _chunks[^1], value.Count);
        Feed(_chunks[^1]);
    }

    public void Feed(IReadOnlyList<Root> value)
    {
        using ArrayPoolSpan<UInt256> input = new(value.Count);
        for (int i = 0; i < value.Count; i++)
        {
            Merkle.Merkleize(out input[i], value[i]);
        }

        Merkle.Merkleize(out _chunks[^1], input);
        Feed(_chunks[^1]);
    }

    public void Feed(IReadOnlyList<Root> value, ulong maxLength)
    {
        using ArrayPoolSpan<UInt256> subRoots = new(value.Count);
        for (int i = 0; i < value.Count; i++)
        {
            Merkle.Merkleize(out subRoots[i], value[i]);
        }

        Merkle.Merkleize(out _chunks[^1], subRoots, maxLength);
        Merkle.MixIn(ref _chunks[^1], value.Count);
        Feed(_chunks[^1]);
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
