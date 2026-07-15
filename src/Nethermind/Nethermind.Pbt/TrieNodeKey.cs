// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>
/// Position of a stem trie node: its depth (0 at the root, up to 248 at the stem level) and the
/// path bits leading to it, MSB-first, zero-padded past <see cref="Depth"/> for canonical equality.
/// </summary>
public readonly record struct TrieNodeKey(byte Depth, Stem Path)
{
    public const int Length = 32;

    public static readonly TrieNodeKey Root = default;

    /// <summary>The key of this node's child on the given bit (0 = left, 1 = right).</summary>
    public TrieNodeKey Child(int bit)
    {
        if (bit == 0) return new TrieNodeKey((byte)(Depth + 1), Path);

        Stem currentPath = Path;
        Span<byte> path = stackalloc byte[Stem.Length];
        currentPath.Bytes.CopyTo(path);
        path[Depth >> 3] |= (byte)(1 << (7 - (Depth & 7)));
        return new TrieNodeKey((byte)(Depth + 1), new Stem(path));
    }

    /// <summary>The 32-byte database key: the depth byte followed by the padded path bytes.</summary>
    public void WriteTo(Span<byte> dest)
    {
        Stem path = Path;
        dest[0] = Depth;
        path.Bytes.CopyTo(dest[1..]);
    }

    public byte[] ToDbKey()
    {
        byte[] key = new byte[Length];
        WriteTo(key);
        return key;
    }

    public override string ToString() => $"{Depth}:{Path}";
}
