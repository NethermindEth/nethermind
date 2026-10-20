// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
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
