// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

        for (; i <= length - sizeof(uint); i += sizeof(uint))
        {
            Unsafe.As<byte, uint>(ref Unsafe.Add(ref destination, i)) =
                ((uint)Unsafe.Add(ref from, i) << 24) | ((uint)Unsafe.Add(ref from, i + 1) << 16) |
                ((uint)Unsafe.Add(ref from, i + 2) << 8) | Unsafe.Add(ref from, i + 3);
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

        for (; i <= length - sizeof(uint); i += sizeof(uint))
        {
            uint group = Unsafe.As<byte, uint>(ref Unsafe.Add(ref source, i));
            Unsafe.Add(ref to, i) = (byte)(group >> 24);
            Unsafe.Add(ref to, i + 1) = (byte)(group >> 16);
            Unsafe.Add(ref to, i + 2) = (byte)(group >> 8);
            Unsafe.Add(ref to, i + 3) = (byte)group;
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
    public static byte[] FromNatural(ReadOnlySpan<byte> bytes)
    {
        byte[] laidOut = GC.AllocateUninitializedArray<byte>(bytes.Length);
        Write(ref MemoryMarshal.GetArrayDataReference(laidOut), bytes);
        return laidOut;
    }

    /// <summary>The bytes of <paramref name="laidOut"/>, laid out as memory from its start, in natural order.</summary>
    public static ReadOnlyMemory<byte> ToNatural(ReadOnlyMemory<byte> laidOut) =>
        laidOut.IsEmpty ? laidOut : ToArray(ref MemoryMarshal.GetReference(laidOut.Span), laidOut.Length);

    /// <summary>Copies <paramref name="length"/> bytes of memory from <paramref name="source"/> to <paramref name="destination"/>, which may overlap.</summary>
    /// <remarks>
    /// Ranges apart and in the same place of their groups move as <see cref="Copy"/> moves them; others go through a copy
    /// in natural order, which makes any overlap safe.
    /// </remarks>
    public static void Move(ref byte destination, ref byte source, int length)
    {
        if (length == 0) return;

        nuint to = (nuint)Unsafe.AsPointer(ref destination);
        nuint from = (nuint)Unsafe.AsPointer(ref source);
        if (((to ^ from) & GroupMask) == 0 && (to + (nuint)length <= from || from + (nuint)length <= to))
        {
            Copy(ref destination, ref source, length);
            return;
        }

        Write(ref destination, ToArray(ref source, length));
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
