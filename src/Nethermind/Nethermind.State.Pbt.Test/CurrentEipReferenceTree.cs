// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Nethermind.State.Pbt.Test;

/// <summary>Independent rebuild-from-entries oracle for the current EIP-8297 tree.</summary>
public sealed class CurrentEipReferenceTree
{
    private readonly SortedDictionary<byte[], byte[]> _entries = new(ByteArrayComparer.Instance);

    public void Insert(ReadOnlySpan<byte> key, byte[] value)
    {
        if (key.Length is < 1 or > 8192) throw new ArgumentException("key must contain 1 through 8192 bytes", nameof(key));
        if (value.Length != 32) throw new ArgumentException("value must be 32 bytes", nameof(value));
        foreach (byte[] existing in _entries.Keys)
        {
            if (!existing.AsSpan().SequenceEqual(key) && (IsPrefix(existing, key) || IsPrefix(key, existing)))
            {
                throw new ArgumentException("keys must be prefix-free", nameof(key));
            }
        }
        _entries[key.ToArray()] = (byte[])value.Clone();
    }

    public bool Delete(ReadOnlySpan<byte> key) => _entries.Remove(key.ToArray());

    public byte[] Merkelize()
    {
        KeyValuePair<byte[], byte[]>[] entries = [.. _entries];
        return entries.Length == 0 ? new byte[32] : Fold(entries, 0, entries.Length, 0);
    }

    private static byte[] Fold(KeyValuePair<byte[], byte[]>[] entries, int start, int end, int depth)
    {
        if (end - start == 1) return Hash([0, .. entries[start].Key, .. entries[start].Value]);
        int differing = FirstDifference(entries[start].Key, entries[end - 1].Key, depth);
        if (differing == Math.Min(entries[start].Key.Length, entries[end - 1].Key.Length) * 8)
        {
            throw new InvalidOperationException("keys must be prefix-free");
        }
        int split = start + 1;
        while (split < end && Bit(entries[split].Key, differing) == 0) split++;
        byte[] left = Fold(entries, start, split, differing + 1);
        byte[] right = Fold(entries, split, end, differing + 1);
        int prefixBits = differing - depth;
        if (prefixBits > ushort.MaxValue) throw new InvalidOperationException("branch prefix is too long");
        byte[] prefix = new byte[(prefixBits + 7) / 8];
        for (int i = 0; i < prefixBits; i++)
        {
            if (Bit(entries[start].Key, depth + i) != 0) prefix[i / 8] |= (byte)(1 << (7 - i % 8));
        }
        byte[] preimage = new byte[3 + prefix.Length + 64];
        preimage[0] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(preimage.AsSpan(1), (ushort)prefixBits);
        prefix.CopyTo(preimage, 3);
        left.CopyTo(preimage, 3 + prefix.Length);
        right.CopyTo(preimage, 3 + prefix.Length + 32);
        return Hash(preimage);
    }

    private static bool IsPrefix(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> value) =>
        prefix.Length <= value.Length && value[..prefix.Length].SequenceEqual(prefix);

    private static int FirstDifference(byte[] first, byte[] last, int start)
    {
        int length = Math.Min(first.Length, last.Length) * 8;
        for (int bit = start; bit < length; bit++) if (Bit(first, bit) != Bit(last, bit)) return bit;
        return length;
    }

    private static int Bit(byte[] key, int bit) => (key[bit / 8] >> (7 - bit % 8)) & 1;

    private static byte[] Hash(byte[] data)
    {
        byte[] result = new byte[32];
        Blake3.Hasher.Hash(data, result);
        return result;
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
