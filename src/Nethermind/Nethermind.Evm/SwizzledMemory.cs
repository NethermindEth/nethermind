// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm;

/// <summary>Moves bytes into and out of EVM memory kept in the swizzled layout <see cref="EvmPooledMemory.IsSwizzled"/> selects.</summary>
/// <remarks>
/// <para>
/// The layout reverses every aligned group of four bytes: the byte at offset <c>o</c> lies at <c>o ^ 3</c>. A group then
/// reads as one little-endian 32-bit load that holds its four bytes big-endian, so an aligned EVM word is its eight
/// 32-bit digits in reverse order, moved by plain loads and stores where the natural layout byte-swaps each of them.
/// </para>
/// <para>
/// Every reference here addresses bytes laid out from a group boundary at an aligned address: the memory itself, and
/// the call input, which a guest keeps in the same layout so that a call hands its arguments over without converting
/// them (see <see cref="EvmPooledMemory.LoadInputAfterGas"/>).
/// </para>
/// </remarks>
internal static unsafe class SwizzledMemory
{
    private const nuint GroupMask = sizeof(uint) - 1;
    private const int HashScratchLimit = 64 * 1024;
    [ThreadStatic] private static byte[]? s_hashScratch;
    [ThreadStatic] private static KeccakHash? s_hasher;

    /// <summary>Hashes swizzled memory without allocating a copy of each input.</summary>
    /// <remarks>Small inputs keep the keccak cache; larger inputs use bounded reusable scratch, streaming past its limit.</remarks>
    internal static void ComputeHash(ref byte source, int length, out ValueHash256 hash)
    {
        if (length <= 256)
        {
            Span<byte> natural = stackalloc byte[256];
            Read(ref source, natural[..length]);
            KeccakCache.ComputeTo(natural[..length], out hash);
            return;
        }

        byte[] scratch = s_hashScratch ??= new byte[HashScratchLimit];
        if (length <= scratch.Length)
        {
            Read(ref source, scratch.AsSpan(0, length));
            KeccakCache.ComputeTo(scratch.AsSpan(0, length), out hash);
            return;
        }

        KeccakHash hasher = s_hasher ??= KeccakHash.Create();
        hasher.Reset();
        for (int offset = 0; offset < length;)
        {
            int count = Math.Min(scratch.Length, length - offset);
            Read(ref Unsafe.Add(ref source, offset), scratch.AsSpan(0, count));
            hasher.Update(scratch.AsSpan(0, count));
            offset += count;
        }
        hash = default;
        hasher.UpdateFinalTo(hash.BytesAsSpan);
    }

    /// <summary>The memory position of the byte at <paramref name="at"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref byte ByteAt(ref byte at)
    {
        nint inGroup = (nint)((nuint)Unsafe.AsPointer(ref at) & GroupMask);
        return ref Unsafe.Add(ref at, 3 - 2 * inGroup);
    }

    /// <summary>Copies <paramref name="source"/>, in natural order, to the memory at <paramref name="destination"/>.</summary>
    public static void Write(ref byte destination, ReadOnlySpan<byte> source)
    {
        ref byte from = ref MemoryMarshal.GetReference(source);
        nint length = source.Length;
        nint i = 0;
        for (; i < length && ((nuint)Unsafe.AsPointer(ref Unsafe.Add(ref destination, i)) & GroupMask) != 0; i++)
            ByteAt(ref Unsafe.Add(ref destination, i)) = Unsafe.Add(ref from, i);

        // Two cursors rather than an index: the address of each byte is then a displacement, not a sum that
        // the code would compute again for every byte.
        if (i <= length - sizeof(uint))
        {
            nint groups = (length - i) >> 2;
            ref byte next = ref Unsafe.Add(ref from, i);
            ref uint group = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref destination, i));
            ref uint end = ref Unsafe.Add(ref group, groups);
            // A group is the source's four bytes read big-endian: a word of the source with its bytes reversed. A
            // source off a word boundary is read as the aligned words that cover it, each group merged from two.
            fixed (byte* start = &next)
            {
                nuint address = (nuint)start;
                uint* words = (uint*)(address & ~(nuint)3);
                int shift = (int)(address & 3) << 3;
                if (shift == 0)
                {
                    do
                    {
                        group = BinaryPrimitives.ReverseEndianness(*words);
                        words++;
                        group = ref Unsafe.Add(ref group, 1);
                    }
                    while (Unsafe.IsAddressLessThan(ref group, ref end));
                }
                else
                {
                    uint current = *words;
                    do
                    {
                        words++;
                        uint following = *words;
                        group = BinaryPrimitives.ReverseEndianness((current >> shift) | (following << (32 - shift)));
                        current = following;
                        group = ref Unsafe.Add(ref group, 1);
                    }
                    while (Unsafe.IsAddressLessThan(ref group, ref end));
                }
            }
            i += groups << 2;
        }

        for (; i < length; i++)
            ByteAt(ref Unsafe.Add(ref destination, i)) = Unsafe.Add(ref from, i);
    }

    /// <summary>Copies the memory at <paramref name="source"/> to <paramref name="destination"/>, in natural order.</summary>
    public static void Read(ref byte source, Span<byte> destination)
    {
        ref byte to = ref MemoryMarshal.GetReference(destination);
        nint length = destination.Length;
        nint i = 0;
        for (; i < length && ((nuint)Unsafe.AsPointer(ref Unsafe.Add(ref source, i)) & GroupMask) != 0; i++)
            Unsafe.Add(ref to, i) = ByteAt(ref Unsafe.Add(ref source, i));

        if (i <= length - sizeof(uint))
        {
            nint groups = (length - i) >> 2;
            ref byte next = ref Unsafe.Add(ref to, i);
            ref uint from = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref source, i));
            ref uint end = ref Unsafe.Add(ref from, groups);
            if (((nuint)Unsafe.AsPointer(ref next) & 3) == 0)
            {
                // Onto a word boundary a group goes out as one word, its bytes reversed.
                ref uint word = ref Unsafe.As<byte, uint>(ref next);
                do
                {
                    word = BinaryPrimitives.ReverseEndianness(from);
                    word = ref Unsafe.Add(ref word, 1);
                    from = ref Unsafe.Add(ref from, 1);
                }
                while (Unsafe.IsAddressLessThan(ref from, ref end));
            }
            else
            {
                do
                {
                    uint group = from;
                    next = (byte)(group >> 24);
                    Unsafe.Add(ref next, 1) = (byte)(group >> 16);
                    Unsafe.Add(ref next, 2) = (byte)(group >> 8);
                    Unsafe.Add(ref next, 3) = (byte)group;
                    next = ref Unsafe.Add(ref next, sizeof(uint));
                    from = ref Unsafe.Add(ref from, 1);
                }
                while (Unsafe.IsAddressLessThan(ref from, ref end));
            }
            i += groups << 2;
        }

        for (; i < length; i++)
            Unsafe.Add(ref to, i) = ByteAt(ref Unsafe.Add(ref source, i));
    }

    /// <summary>The <paramref name="length"/> bytes of memory at <paramref name="source"/>, in natural order, in a new array.</summary>
    public static byte[] ToArray(ref byte source, int length)
    {
        byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
        Read(ref source, bytes);
        return bytes;
    }

    /// <summary>Zeroes the <paramref name="length"/> bytes of memory at <paramref name="destination"/>.</summary>
    /// <remarks>Zero bytes look the same in either layout, but a range that splits a group must zero only its own bytes of it.</remarks>
    public static void Clear(ref byte destination, nint length)
    {
        nint i = 0;
        for (; i < length && ((nuint)Unsafe.AsPointer(ref Unsafe.Add(ref destination, i)) & GroupMask) != 0; i++)
            ByteAt(ref Unsafe.Add(ref destination, i)) = 0;

        nint whole = (length - i) & ~(nint)GroupMask;
        Unsafe.InitBlockUnaligned(ref Unsafe.Add(ref destination, i), 0, (uint)whole);
        for (i += whole; i < length; i++)
            ByteAt(ref Unsafe.Add(ref destination, i)) = 0;
    }

    /// <summary>Copies <paramref name="length"/> bytes laid out as memory from <paramref name="source"/> to the memory at
    /// <paramref name="destination"/>, which must not overlap.</summary>
    /// <remarks>Ranges in the same place of their groups move their whole groups as they lie; only the partial groups at
    /// the ends move byte by byte. Others move byte by byte throughout.</remarks>
    public static void Copy(ref byte destination, ref byte source, nint length)
    {
        nint i = 0;
        if ((((nuint)Unsafe.AsPointer(ref destination) ^ (nuint)Unsafe.AsPointer(ref source)) & GroupMask) == 0)
        {
            for (; i < length && ((nuint)Unsafe.AsPointer(ref Unsafe.Add(ref destination, i)) & GroupMask) != 0; i++)
                ByteAt(ref Unsafe.Add(ref destination, i)) = ByteAt(ref Unsafe.Add(ref source, i));

            nint whole = (length - i) & ~(nint)GroupMask;
            Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref destination, i), ref Unsafe.Add(ref source, i), (uint)whole);
            i += whole;
        }

        for (; i < length; i++)
            ByteAt(ref Unsafe.Add(ref destination, i)) = ByteAt(ref Unsafe.Add(ref source, i));
    }

    /// <summary><paramref name="bytes"/>, in natural order, laid out as memory from its start, in a new array.</summary>
    /// <remarks>The array ends at a group boundary: the last bytes of a partial group lie past the length.</remarks>
    public static ReadOnlyMemory<byte> FromNatural(ReadOnlySpan<byte> bytes)
    {
        int length = bytes.Length;
        byte[] laidOut = GC.AllocateUninitializedArray<byte>((length + (int)GroupMask) & ~(int)GroupMask);
        ref byte start = ref MemoryMarshal.GetArrayDataReference(laidOut);
        // The bytes of the last group past the length stay zero, as those of a realigned range do.
        if ((length & (int)GroupMask) != 0)
            Unsafe.As<byte, uint>(ref Unsafe.Add(ref start, length & ~(int)GroupMask)) = 0;

        Write(ref start, bytes);
        return laidOut.AsMemory(0, length);
    }

    /// <summary>The bytes of <paramref name="laidOut"/>, laid out as memory from its start, in natural order.</summary>
    public static ReadOnlyMemory<byte> ToNatural(ReadOnlyMemory<byte> laidOut) =>
        laidOut.IsEmpty ? laidOut : ToArray(ref MemoryMarshal.GetReference(laidOut.Span), laidOut.Length);

    /// <summary>Copies a swizzled range into memory laid out from its own aligned start.</summary>
    public static ReadOnlyMemory<byte> Realign(ReadOnlySpan<byte> source)
    {
        byte[] destination = new byte[(source.Length + (int)GroupMask) & ~(int)GroupMask];
        Copy(ref MemoryMarshal.GetArrayDataReference(destination), ref MemoryMarshal.GetReference(source), source.Length);
        return destination.AsMemory(0, source.Length);
    }

    /// <summary>Copies <paramref name="length"/> bytes of memory from <paramref name="source"/> to <paramref name="destination"/>, which may overlap.</summary>
    /// <remarks>
    /// Overlapping ranges move in the direction that preserves unread bytes. A bounded stack buffer converts groups
    /// without allocating scratch proportional to the input in guests that cannot collect it.
    /// </remarks>
    public static void Move(ref byte destination, ref byte source, int length)
    {
        if (length == 0 || Unsafe.AreSame(ref destination, ref source)) return;

        nuint to = (nuint)Unsafe.AsPointer(ref destination);
        nuint from = (nuint)Unsafe.AsPointer(ref source);
        if (((to ^ from) & GroupMask) == 0 && (to + (nuint)length <= from || from + (nuint)length <= to))
        {
            Copy(ref destination, ref source, length);
            return;
        }

        Span<byte> scratch = stackalloc byte[256];
        if (to < from)
        {
            for (int offset = 0; offset < length;)
            {
                int count = Math.Min(scratch.Length, length - offset);
                Read(ref Unsafe.Add(ref source, offset), scratch[..count]);
                Write(ref Unsafe.Add(ref destination, offset), scratch[..count]);
                offset += count;
            }
        }
        else
        {
            while (length > 0)
            {
                int count = Math.Min(scratch.Length, length);
                length -= count;
                Read(ref Unsafe.Add(ref source, length), scratch[..count]);
                Write(ref Unsafe.Add(ref destination, length), scratch[..count]);
            }
        }
    }

    /// <summary>Loads the 32-byte word at <paramref name="source"/> into the stack slot <paramref name="slot"/>, in limb layout.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LoadWord(ref ulong slot, ref byte source)
    {
        if (((nuint)Unsafe.AsPointer(ref source) & GroupMask) != 0)
        {
            LoadMisalignedWord(ref slot, ref source);
            return;
        }

        // Digit i of the word, least significant first, is the group that holds bytes 28 - 4i to 31 - 4i.
        ref uint groups = ref Unsafe.As<byte, uint>(ref source);
        ref uint digits = ref Unsafe.As<ulong, uint>(ref slot);
        digits = Unsafe.Add(ref groups, 7);
        Unsafe.Add(ref digits, 1) = Unsafe.Add(ref groups, 6);
        Unsafe.Add(ref digits, 2) = Unsafe.Add(ref groups, 5);
        Unsafe.Add(ref digits, 3) = Unsafe.Add(ref groups, 4);
        Unsafe.Add(ref digits, 4) = Unsafe.Add(ref groups, 3);
        Unsafe.Add(ref digits, 5) = Unsafe.Add(ref groups, 2);
        Unsafe.Add(ref digits, 6) = Unsafe.Add(ref groups, 1);
        Unsafe.Add(ref digits, 7) = groups;
    }

    /// <summary>Stores the stack word that ends at <paramref name="end"/>, in limb layout, to the 32 bytes of memory at <paramref name="destination"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreWord(ref byte destination, ref ulong end)
    {
        ref uint digits = ref Unsafe.As<ulong, uint>(ref Unsafe.Subtract(ref end, EvmStack.WordSize / sizeof(ulong)));
        if (((nuint)Unsafe.AsPointer(ref destination) & GroupMask) != 0)
        {
            StoreMisalignedWord(ref destination, ref digits);
            return;
        }

        ref uint groups = ref Unsafe.As<byte, uint>(ref destination);
        groups = Unsafe.Add(ref digits, 7);
        Unsafe.Add(ref groups, 1) = Unsafe.Add(ref digits, 6);
        Unsafe.Add(ref groups, 2) = Unsafe.Add(ref digits, 5);
        Unsafe.Add(ref groups, 3) = Unsafe.Add(ref digits, 4);
        Unsafe.Add(ref groups, 4) = Unsafe.Add(ref digits, 3);
        Unsafe.Add(ref groups, 5) = Unsafe.Add(ref digits, 2);
        Unsafe.Add(ref groups, 6) = Unsafe.Add(ref digits, 1);
        Unsafe.Add(ref groups, 7) = digits;
    }

    /// <summary>Stores the native word whose 32 little-endian bytes start at <paramref name="word"/> to the memory at <paramref name="destination"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreNativeWord(ref byte destination, ref byte word) =>
        StoreWord(ref destination, ref Unsafe.As<byte, ulong>(ref Unsafe.Add(ref word, EvmStack.WordSize)));

    /// <remarks>
    /// The word starts <c>m</c> bytes into a group and spans nine. Each group reads as its bytes big-endian, so the word's
    /// j-th big-endian 32-bit part, most significant first, is group j shifted up by m bytes merged with group j + 1
    /// shifted down by the rest: a funnel shift. Of the ninth group only the word's own bytes survive the shift.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadMisalignedWord(ref ulong slot, ref byte source)
    {
        nuint address = (nuint)Unsafe.AsPointer(ref source);
        uint* groups = (uint*)(address & ~GroupMask);
        int low = (int)(address & GroupMask) * 8;
        int high = 32 - low;
        ref uint digits = ref Unsafe.As<ulong, uint>(ref slot);
        for (int j = 0; j < EvmStack.WordSize / sizeof(uint); j++)
            Unsafe.Add(ref digits, 7 - j) = (groups[j] << low) | (groups[j + 1] >> high);
    }

    /// <remarks>
    /// <see cref="LoadMisalignedWord"/> in reverse: the inner groups are merged from two parts of the word, and the first
    /// and ninth keep the bytes outside the word.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StoreMisalignedWord(ref byte destination, ref uint digits)
    {
        nuint address = (nuint)Unsafe.AsPointer(ref destination);
        uint* groups = (uint*)(address & ~GroupMask);
        int low = (int)(address & GroupMask) * 8;
        int high = 32 - low;
        uint below = uint.MaxValue >> low;
        uint previous = Unsafe.Add(ref digits, 7);
        groups[0] = (groups[0] & ~below) | (previous >> low);
        for (int j = 1; j < EvmStack.WordSize / sizeof(uint); j++)
        {
            uint part = Unsafe.Add(ref digits, 7 - j);
            groups[j] = (previous << high) | (part >> low);
            previous = part;
        }

        groups[8] = (groups[8] & below) | (previous << high);
    }
}
