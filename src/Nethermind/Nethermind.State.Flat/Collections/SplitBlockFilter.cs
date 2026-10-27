// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
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
    private static readonly Vector128<uint> SaltsLower = Salts.GetLower();
    private static readonly Vector128<uint> SaltsUpper = Salts.GetUpper();

    private uint[] _words = [];
    private int _offset;
    private uint _blockMask;

    /// <summary>Clears the filter and sizes it for <paramref name="count"/> keys.</summary>
    public void Reset(int count)
    {
        int blocks = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, (int)(((long)count * BitsPerKey + BitsPerBlock - 1) / BitsPerBlock)));
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
        ref uint block = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_words), BlockStart(hash, out uint mixed));
        if (Avx2.IsSupported)
        {
            (Vector256.LoadUnsafe(ref block) | Bits(mixed)).StoreUnsafe(ref block);
        }
        else if (AdvSimd.IsSupported)
        {
            (Vector128.LoadUnsafe(ref block) | Bits(mixed, SaltsLower)).StoreUnsafe(ref block);
            (Vector128.LoadUnsafe(ref block, 4) | Bits(mixed, SaltsUpper)).StoreUnsafe(ref block, 4);
        }
        else
        {
            for (int i = 0; i < WordsPerBlock; i++)
            {
                Unsafe.Add(ref block, i) |= 1u << (int)((mixed * Salts.GetElement(i)) >> 27);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MightContain(uint hash)
    {
        uint[] words = _words;
        uint start = (uint)BlockStart(hash, out uint mixed);
        // A reader that breaks a lease can see the mask of a grown filter with its old array; answer "maybe" there
        // instead of reading past the array.
        if (start + WordsPerBlock > (uint)words.Length) return true;

        ref uint block = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(words), start);
        if (Avx2.IsSupported) return Avx.TestC(Vector256.LoadUnsafe(ref block), Bits(mixed));

        if (AdvSimd.IsSupported)
        {
            Vector128<uint> missing = Vector128.AndNot(Bits(mixed, SaltsLower), Vector128.LoadUnsafe(ref block))
                | Vector128.AndNot(Bits(mixed, SaltsUpper), Vector128.LoadUnsafe(ref block, 4));
            return missing == Vector128<uint>.Zero;
        }

        for (int i = 0; i < WordsPerBlock; i++)
        {
            uint bit = 1u << (int)((mixed * Salts.GetElement(i)) >> 27);
            if ((Unsafe.Add(ref block, i) & bit) == 0) return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Bits(uint mixed) =>
        Avx2.ShiftLeftLogicalVariable(Vector256<uint>.One, Vector256.ShiftRightLogical(Vector256.Create(mixed) * Salts, 27));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Bits(uint mixed, Vector128<uint> salts) =>
        AdvSimd.ShiftLogical(Vector128<uint>.One, Vector128.ShiftRightLogical(Vector128.Create(mixed) * salts, 27).AsInt32());

    // The block comes from the high half of a 64-bit mix and the in-block bits from the low half, so neither
    // reuses the low hash bits the dictionary buckets on.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BlockStart(uint hash, out uint mixed)
    {
        ulong product = hash * 0x9E3779B97F4A7C15ul;
        mixed = (uint)product;
        return _offset + (int)((uint)(product >> 32) & _blockMask) * WordsPerBlock;
    }
}
