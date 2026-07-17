// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

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

    /// <summary>
    /// The key of the group at <paramref name="depth"/> on <paramref name="path"/>, whose bits past
    /// <paramref name="depth"/> are dropped for the canonical zero-padded form.
    /// </summary>
    /// <remarks>
    /// For a producer holding a path that runs deeper than the group it wants to address — a
    /// <see cref="PbtNodeChain"/>'s target path naming a group somewhere along it. Truncating is what
    /// makes the key equal the one a <see cref="ChildGroup"/> descent produces, and what keeps that
    /// descent's nibbles OR-able.
    /// </remarks>
    public static TrieNodeKey For(int depth, in Stem path)
    {
        Debug.Assert((uint)depth <= Stem.LengthInBits && depth % PbtTrieNodeGroup.LevelsPerGroup == 0);

        Span<byte> truncated = stackalloc byte[Stem.Length];
        path.Bytes[..((depth + 7) >> 3)].CopyTo(truncated);
        // depth is a multiple of 4, so a half-full byte keeps only its high nibble
        if ((depth & 4) != 0) truncated[depth >> 3] &= 0xF0;
        return new TrieNodeKey((byte)depth, new Stem(truncated));
    }

    /// <summary>The key of the child group under boundary slot <paramref name="slot"/> (0..15).</summary>
    public TrieNodeKey ChildGroup(int slot)
    {
        Stem currentPath = Path;
        Span<byte> path = stackalloc byte[Stem.Length];
        currentPath.Bytes.CopyTo(path);
        // Depth is a multiple of 4, so the four new path bits fill one nibble of the byte at Depth
        Debug.Assert((path[Depth >> 3] & ((Depth & 4) == 0 ? 0xF0 : 0x0F)) == 0, "the path must be zero-padded past Depth for the new nibble to OR into");
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
