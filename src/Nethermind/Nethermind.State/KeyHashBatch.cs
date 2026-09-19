// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State;

internal struct KeyHashBatch
{
    private const int MaximumBatchSize = 8;
    /// <summary>The AVX2 lane width, also the minimum account-batching threshold for both state backends.</summary>
    internal const int MinimumBatchSize = 4;
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

    internal readonly bool IsFull => _count == (Avx512F.IsSupported ? MaximumBatchSize : MinimumBatchSize);

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
        int batchSize = Avx512F.IsSupported && _count == MaximumBatchSize ? MaximumBatchSize
            : Avx2.IsSupported && _count >= MinimumBatchSize ? MinimumBatchSize : 0;
        if (batchSize != 0)
        {
            Unsafe.SkipInit(out Scratch scratch);
            Span<byte> buffer = MemoryMarshal.AsBytes((Span<ulong>)scratch);
            Span<byte> inputs = buffer[..(batchSize * Rate)];
            Span<byte> hashes = buffer.Slice(batchSize * Rate, batchSize * Hash256.Size);
            for (int i = 0; i < batchSize; i++)
            {
                Span<byte> input = inputs.Slice(i * Rate, Rate);
                _keys[i].BytesAsSpan[.._length].CopyTo(input);
                input[_length..].Clear();
                input[_length] = 1;
                input[^1] = 128;
            }
            if (batchSize == MaximumBatchSize)
                KeccakHash.ComputePaddedBlocks8Avx512(ref inputs[0], ref hashes[0]);
            else
                KeccakHash.ComputePaddedBlocks4Avx2(ref inputs[0], ref hashes[0]);

            for (int i = 0; i < batchSize; i++)
            {
                ValueHash256 hash = new(hashes.Slice(i * Hash256.Size, Hash256.Size));
                KeccakCache.Store(_keys[i].BytesAsSpan[.._length], in hash);
                int index = _indices[i];
                entries[index] = new(in hash, entries[index].Value);
            }
        }
        for (int i = batchSize; i < _count; i++)
        {
            KeccakCache.ComputeTo(_keys[i].BytesAsSpan[.._length], out ValueHash256 hash);
            int index = _indices[i];
            entries[index] = new(in hash, entries[index].Value);
        }
        _count = 0;
    }
}
