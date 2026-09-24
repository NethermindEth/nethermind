// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State;

internal struct KeyHashBatch
{
    private const int MaximumBatchSize = 8;
    /// <summary>The AVX2 lane width, also the minimum account-batching threshold for both state backends.</summary>
    internal const int MinimumBatchSize = 4;
    /// <summary>Below this, a batch kernel's setup and lane-clearing is not worth it over one scalar hash.</summary>
    private const int MinimumBatchCount = 2;
    private const int Rate = 136;

    [InlineArray(MaximumBatchSize)]
    private struct Keys
    {
        private ValueHash256 _element0;
    }

    [InlineArray(MaximumBatchSize)]
    private struct Indices
    {
        private int _element0;
    }

    [InlineArray(MaximumBatchSize * (Rate + Hash256.Size) / sizeof(ulong))]
    private struct Scratch
    {
        private ulong _element0;
    }

    private Keys _keys;
    private Indices _indices;
    private int _count;
    private int _length;

    internal readonly bool IsFull => _count == (Avx512F.IsSupported && Vector512.IsHardwareAccelerated ? MaximumBatchSize : MinimumBatchSize);

    internal void Initialize(int length)
    {
        _length = length;
        _count = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(ReadOnlySpan<byte> input, int index, out ValueHash256 hash)
    {
        Debug.Assert(input.Length == _length && _count < MaximumBatchSize);
        if (!Avx2.IsSupported)
        {
            KeccakCache.ComputeTo(input, out hash);
            return;
        }
        if (KeccakCache.TryGet(input, out hash)) return;
        AddMissing(input, index);
        hash = default;
    }

    internal void AddMissing(ReadOnlySpan<byte> input, int index)
    {
        Debug.Assert(input.Length == _length && _count < MaximumBatchSize);
        ref ValueHash256 key = ref _keys[_count];
        input.CopyTo(key.BytesAsSpan);
        _indices[_count++] = index;
    }

    [SkipLocalsInit]
    internal void Flush(Span<PatriciaTree.BulkSetEntry> entries)
    {
        if (_count == 0) return;
        int hashed = 0;
        if (Avx2.IsSupported)
        {
            Unsafe.SkipInit(out Scratch scratch);
            Span<byte> buffer = MemoryMarshal.AsBytes((Span<ulong>)scratch);
            // A batch kernel costs the same whether its lanes are full or empty, so any remainder
            // meeting the entry threshold takes one and zero-fills the rest. The narrowest kernel
            // that covers the remainder is preferred: a wider one would permute empty lanes.
            while (_count - hashed >= MinimumBatchCount)
            {
                int remaining = _count - hashed;
                int batchSize = Avx512F.IsSupported && Vector512.IsHardwareAccelerated && remaining > MinimumBatchSize ? MaximumBatchSize : MinimumBatchSize;
                int batchCount = Math.Min(batchSize, remaining);
                Span<byte> inputs = buffer[..(batchSize * Rate)];
                Span<byte> hashes = buffer.Slice(batchSize * Rate, batchSize * Hash256.Size);
                for (int i = 0; i < batchCount; i++)
                {
                    Span<byte> input = inputs.Slice(i * Rate, Rate);
                    _keys[hashed + i].BytesAsSpan[.._length].CopyTo(input);
                    input[_length..].Clear();
                    input[_length] |= 1;
                    input[^1] |= 128;
                }

                // Lanes past batchCount carry no key; zero-fill them so the kernel runs, and their
                // digests are never read back.
                inputs[(batchCount * Rate)..].Clear();
                if (batchSize == MaximumBatchSize)
                    KeccakHash.ComputePaddedBlocks8Avx512(ref inputs[0], ref hashes[0]);
                else
                    KeccakHash.ComputePaddedBlocks4Avx2(ref inputs[0], ref hashes[0]);

                for (int i = 0; i < batchCount; i++)
                {
                    ValueHash256 hash = new(hashes.Slice(i * Hash256.Size, Hash256.Size));
                    KeccakCache.Store(_keys[hashed + i].BytesAsSpan[.._length], in hash);
                    int index = _indices[hashed + i];
                    entries[index] = new(in hash, entries[index].Value);
                }

                hashed += batchCount;
            }
        }

        for (int i = hashed; i < _count; i++)
        {
            KeccakCache.ComputeTo(_keys[i].BytesAsSpan[.._length], out ValueHash256 hash);
            int index = _indices[i];
            entries[index] = new(in hash, entries[index].Value);
        }
        _count = 0;
    }
}
