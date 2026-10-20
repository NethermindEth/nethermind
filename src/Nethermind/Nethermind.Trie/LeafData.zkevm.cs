// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Buffers;

namespace Nethermind.Trie;

public sealed partial class LeafData
{
    private byte[]? _key;

    // The hex-prefix key in the RLP the leaf was decoded from, expanded into nibbles only when first read.
    private byte[]? _compactKeySource;
    private int _compactKeyStart;
    private int _compactKeyLength;

    /// <remarks>Decoded from the leaf's RLP on first read: a leaf the guest only reads is matched against its key in
    /// that form (see <see cref="KeyIsSuffixOf"/>), sparing the nibble array.</remarks>
    public byte[]? Key
    {
        get => _key ?? DecodeCompactKey();
        set
        {
            _key = value;
            _compactKeySource = null;
        }
    }

    /// <param name="rlp">The leaf's RLP, never written to afterwards.</param>
    internal LeafData(byte[] rlp, int compactKeyStart, int compactKeyLength, CappedArray<byte> value)
    {
        _compactKeySource = rlp;
        _compactKeyStart = compactKeyStart;
        _compactKeyLength = compactKeyLength;
        _value = value;
    }

    /// <summary>A leaf keyed by the nibbles of <paramref name="bytes"/> from nibble <paramref name="start"/> on, kept in hex-prefix form.</summary>
    internal static LeafData WithKeyFrom(ReadOnlySpan<byte> bytes, int start, CappedArray<byte> value)
    {
        ReadOnlySpan<byte> tail = bytes[((start + 1) >> 1)..];
        byte[] compactKey = new byte[1 + tail.Length];
        compactKey[0] = (byte)((start & 1) == 0 ? 0x20 : 0x30 | (bytes[start >> 1] & 0xF));
        tail.CopyTo(compactKey.AsSpan(1));
        return new LeafData(compactKey, 0, compactKey.Length, value);
    }

    /// <summary>This leaf's hex-prefix key, where the leaf has one: decoded from RLP or built in that form, and not replaced since.</summary>
    internal bool TryGetCompactKey(out ReadOnlySpan<byte> compactKey)
    {
        byte[]? source = _compactKeySource;
        if (source is null)
        {
            compactKey = default;
            return false;
        }

        compactKey = source.AsSpan(_compactKeyStart, _compactKeyLength);
        return true;
    }

    /// <remarks>Keeps the key in hex-prefix form if it has not been read.</remarks>
    public LeafData CloneWithNewValue(CappedArray<byte> value) =>
        _key is null && _compactKeySource is not null
            ? new LeafData(_compactKeySource, _compactKeyStart, _compactKeyLength, value) { StorageRoot = StorageRoot }
            : new LeafData(_key, value, StorageRoot);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte[]? DecodeCompactKey()
    {
        byte[]? source = _compactKeySource;
        return source is null ? null : _key = HexPrefix.FromBytes(source.AsSpan(_compactKeyStart, _compactKeyLength)).key;
    }

    /// <summary>Whether this leaf's key is the last <paramref name="nibbleCount"/> nibbles of <paramref name="key"/>.</summary>
    internal bool KeyIsSuffixOf(ReadOnlySpan<byte> key, int nibbleCount)
    {
        byte[]? source = _compactKeySource;
        if (source is null) return NibbleKeyIsSuffixOf(key, nibbleCount);

        ReadOnlySpan<byte> compact = source.AsSpan(_compactKeyStart, _compactKeyLength);
        int flags = compact[0];
        bool isOdd = (flags & 0x10) != 0;
        if (2 * compact.Length - (isOdd ? 1 : 2) != nibbleCount) return false;

        // A suffix of whole bytes, the odd key's first nibble sitting in the low half of the byte before them.
        int tailLength = compact.Length - 1;
        if (isOdd && ((flags ^ key[key.Length - compact.Length]) & 0xF) != 0) return false;
        return compact[1..].SequenceEqual(key[(key.Length - tailLength)..]);
    }

    /// <summary>Whether this leaf's key is <paramref name="nibbles"/>, matched in hex-prefix form while the key has not been read.</summary>
    [SkipLocalsInit]
    internal bool KeyEquals(ReadOnlySpan<byte> nibbles)
    {
        byte[]? source = _compactKeySource;
        if (_key is not null || source is null) return KeyNibblesEqual(nibbles);

        ReadOnlySpan<byte> compact = source.AsSpan(_compactKeyStart, _compactKeyLength);
        int flags = compact[0];
        int oddNibble = (flags >> 4) & 1;
        if (2 * compact.Length - 2 + oddNibble != nibbles.Length) return false;
        if (oddNibble != 0 && (flags & 0xF) != nibbles[0]) return false;

        int tailLength = compact.Length - 1;
        if (tailLength > MaxPackedLength) return KeyNibblesEqual(nibbles);

        Span<byte> packed = stackalloc byte[MaxPackedLength];
        ReadOnlySpan<byte> tail = nibbles[oddNibble..];
        Nibbles.PackNibbles(ref MemoryMarshal.GetReference(tail), ref MemoryMarshal.GetReference(packed), tailLength);
        return compact[1..].SequenceEqual(packed[..tailLength]);
    }

    private const int MaxPackedLength = 32;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool KeyNibblesEqual(ReadOnlySpan<byte> nibbles) => Key.AsSpan().SequenceEqual(nibbles);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool NibbleKeyIsSuffixOf(ReadOnlySpan<byte> key, int nibbleCount)
    {
        byte[]? nibbles = _key;
        if (nibbles is null || nibbles.Length != nibbleCount) return false;

        int start = 2 * key.Length - nibbleCount;
        for (int i = 0; i < nibbles.Length; i++)
        {
            if (nibbles[i] != Nibbles.NibbleAt(key, start + i)) return false;
        }

        return true;
    }
}
