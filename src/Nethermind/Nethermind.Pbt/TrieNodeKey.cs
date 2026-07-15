// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>
/// Position of a stem trie node group: its depth (0 at the root, a multiple of
/// <see cref="PbtTrieNodeGroup.LevelsPerGroup"/> up to <see cref="PbtTrieNodeGroup.MaxGroupDepth"/>)
/// and the path bits leading to it, MSB-first, zero-padded past <see cref="Depth"/> for canonical equality.
/// </summary>
public readonly record struct TrieNodeKey(byte Depth, Stem Path)
{
    public const int Length = 32;

    public static readonly TrieNodeKey Root = default;

    /// <summary>The key of the child group under boundary slot <paramref name="slot"/> (0..15).</summary>
    public TrieNodeKey ChildGroup(int slot)
    {
        Stem currentPath = Path;
        Span<byte> path = stackalloc byte[Stem.Length];
        currentPath.Bytes.CopyTo(path);
        // Depth is a multiple of 4, so the four new path bits fill one nibble of the byte at Depth
        path[Depth >> 3] |= (byte)((Depth & 4) == 0 ? slot << 4 : slot);
        return new TrieNodeKey((byte)(Depth + PbtTrieNodeGroup.LevelsPerGroup), new Stem(path));
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
