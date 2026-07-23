// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;

namespace Nethermind.Pbt;

/// <summary>
/// Position of a stem trie node group: its depth (0 at the root, a multiple of
/// the tiling's <see cref="IPbtTileLayout.LevelsPerGroup"/> up to its <see cref="IPbtTileLayout.MaxGroupDepth"/>)
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
    /// descent's slots OR-able.
    /// </remarks>
    public static TrieNodeKey For(int depth, in Stem path)
    {
        Debug.Assert((uint)depth <= Stem.LengthInBits);

        Span<byte> truncated = stackalloc byte[Stem.Length];
        path.Bytes[..((depth + 7) >> 3)].CopyTo(truncated);
        int partialBits = depth & 7;
        if (partialBits != 0) truncated[depth >> 3] &= (byte)(0xFF << (8 - partialBits));
        return new TrieNodeKey((byte)depth, new Stem(truncated));
    }

    /// <summary>
    /// The key of the child group under boundary slot <paramref name="slot"/> of a tiling whose tiles
    /// are <paramref name="levelsPerGroup"/> levels deep.
    /// </summary>
    /// <remarks>
    /// A slot straddles two path bytes wherever the tile's levels do not divide eight, and the deepest
    /// tile of such a tiling reaches past the stem — so the slot is OR-ed through a window over the
    /// zero-padded 32 bytes, of which the key keeps the 31 a stem has.
    /// </remarks>
    public TrieNodeKey ChildGroup(int slot, int levelsPerGroup)
    {
        Stem currentPath = Path;
        Span<byte> path = stackalloc byte[Stem.Length + 1];
        path.Clear();
        currentPath.Bytes.CopyTo(path);

        int shift = 16 - levelsPerGroup - (Depth & 7);
        Span<byte> window = path[(Depth >> 3)..];
        ushort bits = BinaryPrimitives.ReadUInt16BigEndian(window);
        Debug.Assert((bits & (((1 << levelsPerGroup) - 1) << shift)) == 0, "the path must be zero-padded past Depth for the new slot to OR into");
        BinaryPrimitives.WriteUInt16BigEndian(window, (ushort)(bits | (slot << shift)));
        return new TrieNodeKey((byte)(Depth + levelsPerGroup), new Stem(path[..Stem.Length]));
    }

    /// <summary>The 32-byte database key: the padded path bytes followed by the depth byte.</summary>
    /// <remarks>
    /// The depth trails so that byte order is path-major, which sorts a node immediately before its
    /// own subtree and makes that subtree one contiguous range — a traversal then walks adjacent
    /// keys instead of one disjoint range per level, and the high-entropy path leads. This matches
    /// the convention the flat database uses for its own trie node keys.
    /// </remarks>
    public void WriteTo(Span<byte> dest)
    {
        Stem path = Path;
        path.Bytes.CopyTo(dest);
        dest[Stem.Length] = Depth;
    }

    public byte[] ToDbKey()
    {
        byte[] key = new byte[Length];
        WriteTo(key);
        return key;
    }

    public override string ToString() => $"{Depth}:{Path}";
}
