// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.State.Flat.Collections;

/// <summary>
/// Build-once negative filter over 32-bit key hashes: answers "definitely absent" or "maybe present".
/// </summary>
/// <remarks>
/// A split-block Bloom filter (as in Apache Parquet): each key selects one 256-bit block and sets one bit in each
/// of its eight 32-bit words, so a query reads a single cache-line-aligned block. About 10 bits per key give a
/// false-positive rate of roughly 1%. The backing array is pinned so blocks stay 32-byte aligned, and it is kept
/// across builds; reads are only valid against the latest <see cref="Reset"/> and the adds that followed it.
/// </remarks>
internal sealed unsafe class SplitBlockFilter
{
    private const int WordsPerBlock = 8;
    private const int BitsPerBlock = WordsPerBlock * 32;
    private const int BitsPerKey = 10;
    private const int AlignmentWords = 32 / sizeof(uint);

    private static readonly Vector256<uint> Salts = Vector256.Create(
        0x47b6137bu, 0x44974d91u, 0x8824ad5bu, 0xa2b7289du, 0x705495c7u, 0x2df1424bu, 0x9efc4947u, 0x5c6bfb31u);

    private uint[] _words = [];
    private int _offset;
    private uint _blockMask;

    /// <summary>Clears the filter and sizes it for <paramref name="count"/> keys.</summary>
    public void Reset(int count)
    {
        int blocks = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, (int)((long)count * BitsPerKey / BitsPerBlock)));
        int length = blocks * WordsPerBlock;
        if (_words.Length < length + AlignmentWords)
        {
            _words = GC.AllocateUninitializedArray<uint>(length + AlignmentWords, pinned: true);
            nuint address = (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_words));
            _offset = (int)((32 - (address & 31)) & 31) / sizeof(uint);
        }

        _words.AsSpan(_offset, length).Clear();
        _blockMask = (uint)(blocks - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(uint hash)
    {
        ref uint block = ref Block(hash, out uint mixed);
        for (int i = 0; i < WordsPerBlock; i++)
        {
            Unsafe.Add(ref block, i) |= 1u << (int)((mixed * Salts.GetElement(i)) >> 27);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MightContain(uint hash)
    {
        ref uint block = ref Block(hash, out uint mixed);
        if (Avx2.IsSupported)
        {
            Vector256<uint> shifts = Vector256.ShiftRightLogical(Vector256.Create(mixed) * Salts, 27);
            Vector256<uint> bits = Avx2.ShiftLeftLogicalVariable(Vector256<uint>.One, shifts);
            return Avx.TestC(Vector256.LoadUnsafe(ref block), bits);
        }

        for (int i = 0; i < WordsPerBlock; i++)
        {
            uint bit = 1u << (int)((mixed * Salts.GetElement(i)) >> 27);
            if ((Unsafe.Add(ref block, i) & bit) == 0) return false;
        }

        return true;
    }

    // The block comes from the high half of a 64-bit mix and the in-block bits from the low half, so neither
    // reuses the low hash bits the dictionary buckets on.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref uint Block(uint hash, out uint mixed)
    {
        ulong product = hash * 0x9E3779B97F4A7C15ul;
        mixed = (uint)product;
        int index = (int)((uint)(product >> 32) & _blockMask);
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_words), _offset + index * WordsPerBlock);
    }
}
