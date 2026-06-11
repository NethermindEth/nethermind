// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Reads the Variable (KeyType=0) key section of a B-tree index node. Wire layout: see
/// <c>Hsst/FORMAT.md</c>, "Keys section (Variable)".
/// </summary>
internal readonly ref struct BTreeNodeVariableKeyReader(ReadOnlySpan<byte> keys, int count)
{
    // Ref-like primary-ctor params can't be used in instance members of a ref struct;
    // forward into a field.
    private readonly ReadOnlySpan<byte> keys = keys;

    /// <summary>
    /// Raw 2-byte prefix slot for entry <paramref name="index"/> in storage (byte-reversed) order.
    /// External callers wanting lex-order bytes use <see cref="GetSeparatorBytes"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetRawSlot(int index) => keys.Slice(index * 2, 2);

    /// <summary>
    /// Find the largest entry index whose key is &lt;= <paramref name="key"/>. Returns -1 when
    /// <paramref name="key"/> is less than every entry. <paramref name="key"/> must already have
    /// the common prefix stripped by the caller.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FindFloorIndex(ReadOnlySpan<byte> key)
    {
        ushort searchPrefix = EncodeSearchPrefix(key);
        int result = -1;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int cmp = CompareEntry(key, searchPrefix, mid);
            if (cmp >= 0) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    /// <summary>
    /// Copy the full lex-order separator (<paramref name="commonKeyPrefix"/> + per-entry suffix) for
    /// entry <paramref name="index"/> into <paramref name="dest"/>. Returns the number of bytes
    /// written. The prefix slot is un-reversed here so the result is in original byte order.
    /// </summary>
    public int GetSeparatorBytes(int index, ReadOnlySpan<byte> commonKeyPrefix, Span<byte> dest)
    {
        int slot = GetOffsetSlot(index);
        int tag = slot >>> 14;
        ReadOnlySpan<byte> tail = tag == 0b11 ? GetTail(index) : default;
        int suffixLen = tag == 0b11 ? 2 + tail.Length : tag;
        int total = commonKeyPrefix.Length + suffixLen;
        if (dest.Length < total)
            throw new ArgumentException("Destination too small for full key", nameof(dest));
        commonKeyPrefix.CopyTo(dest);
        Span<byte> suffixDst = dest.Slice(commonKeyPrefix.Length, suffixLen);
        // Un-reverse prefix slot bytes [b, a] → lex [a, b] up to suffixLen.
        if (suffixLen >= 1) suffixDst[0] = keys[index * 2 + 1];
        if (suffixLen >= 2) suffixDst[1] = keys[index * 2];
        if (tag == 0b11) tail.CopyTo(suffixDst[2..]);
        return total;
    }

    /// <summary>
    /// Load entry <paramref name="index"/>'s prefix slot as a u16 (LE). The slot stores the
    /// original 2-byte prefix byte-reversed, so the unsigned value returned has the same
    /// ordering as a lex compare on the original prefix bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort GetPrefixU16(int index) =>
        Unsafe.ReadUnaligned<ushort>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(keys), (nint)(index * 2)));

    /// <summary>
    /// Load entry <paramref name="index"/>'s offset slot. High 2 bits = lenTag (0..3),
    /// low 14 bits = tailOffset (relative to remainingkeys section start).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetOffsetSlot(int index)
    {
        int offsetArrStart = count * 2;
        return BinaryPrimitives.ReadUInt16LittleEndian(keys[(offsetArrStart + index * 2)..]);
    }

    /// <summary>
    /// Resolve the tail bytes for entry <paramref name="index"/>. Tag &lt; 11 returns an
    /// empty span. For tag 11 the tail spans <c>[tailOffset, nextTailOffset)</c> with the
    /// sentinel for the last entry being <c>remainingkeys.Length</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> GetTail(int index)
    {
        int offsetArrStart = count * 2;
        int tailStart = count * 4;
        int slot = BinaryPrimitives.ReadUInt16LittleEndian(keys[(offsetArrStart + index * 2)..]);
        if ((slot >>> 14) != 0b11) return default;
        int tailOffset = slot & 0x3FFF;
        int tailEnd;
        if (index + 1 < count)
        {
            int nextSlot = BinaryPrimitives.ReadUInt16LittleEndian(keys[(offsetArrStart + (index + 1) * 2)..]);
            tailEnd = nextSlot & 0x3FFF;
        }
        else
        {
            tailEnd = keys.Length - tailStart;
        }
        return keys.Slice(tailStart + tailOffset, tailEnd - tailOffset);
    }

    /// <summary>
    /// Encode the search key into the byte-reversed u16 form used by prefixArr slots.
    /// Zero-pads keys shorter than 2 bytes; the lenTag-aware tie-break on prefix-equal probes
    /// is applied inside <see cref="CompareEntry"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort EncodeSearchPrefix(ReadOnlySpan<byte> q)
    {
        if (q.Length >= 2)
            return BinaryPrimitives.ReverseEndianness(
                Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(q)));
        return q.Length == 1 ? (ushort)(q[0] << 8) : (ushort)0;
    }

    /// <summary>
    /// Compare query <paramref name="q"/> against entry <paramref name="index"/>. Returns
    /// negative, zero, or positive matching <c>SequenceCompareTo</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CompareEntry(ReadOnlySpan<byte> q, ushort searchPrefix, int index)
    {
        ushort midPrefix = GetPrefixU16(index);
        if (searchPrefix != midPrefix)
            return searchPrefix > midPrefix ? 1 : -1;

        int slot = GetOffsetSlot(index);
        int tag = slot >>> 14;
        if (tag != 0b11)
        {
            // Stored key length = tag (0/1/2). Prefix u16 equality (with zero padding) collapses
            // to a length tie-break: q.Length - storedLen.
            return q.Length - tag;
        }

        // Stored key has tail (length ≥ 3). q < stored if q exhausts within the prefix.
        if (q.Length <= 2) return -1;

        int tailOffset = slot & 0x3FFF;
        int offsetArrStart = count * 2;
        int tailStart = count * 4;
        int tailEnd = index + 1 < count
            ? BinaryPrimitives.ReadUInt16LittleEndian(keys[(offsetArrStart + (index + 1) * 2)..]) & 0x3FFF
            : keys.Length - tailStart;
        ReadOnlySpan<byte> tail = keys.Slice(tailStart + tailOffset, tailEnd - tailOffset);
        return q[2..].SequenceCompareTo(tail);
    }
}
