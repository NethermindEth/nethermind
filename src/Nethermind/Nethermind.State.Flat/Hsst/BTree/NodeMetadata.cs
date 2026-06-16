// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Parsed header of a B-tree index node (the leading 12-byte header block).
/// </summary>
public readonly struct NodeMetadata
{
    public byte Flags { get; init; }
    public int KeyCount { get; init; }
    /// <summary>KeyType=0: section size. KeyType=1: fixed key length.</summary>
    public int KeySize { get; init; }
    /// <summary>Base offset added to every Uniform value read. 0 when absent. Encoded on disk as 6-byte LE.</summary>
    public ulong BaseOffset { get; init; }

    /// <summary>Packed into Flags bits 0-1; always <see cref="BTreeNodeKind.Intermediate"/> for nodes parsed here.</summary>
    public BTreeNodeKind NodeKind => (BTreeNodeKind)(Flags & 0x03);
    public int KeyType => (Flags >> 2) & 0x03;
    /// <summary>Fixed value width in bytes, one of {2, 3, 4, 6}.</summary>
    public int ValueSize => ((Flags >> 4) & 0b11) switch
    {
        0 => 2,
        1 => 3,
        2 => 4,
        _ => 6,
    };
    /// <summary>True when fixed-width key slots are stored byte-reversed (Uniform with <see cref="KeySize"/> ∈ {2,4,8}, and always for Variable).</summary>
    public bool IsKeyLittleEndian => (Flags & 0x40) != 0;

    /// <summary>Total byte size of the Keys section.</summary>
    public int KeySectionSize => KeyType switch
    {
        0 => KeySize,              // Variable: KeySize IS the section size
        1 => KeyCount * KeySize,
        _ => throw new InvalidDataException()
    };

    /// <summary>Total byte size of the Values section. Always Uniform: count × fixed width.</summary>
    public int ValueSectionSize => KeyCount * ValueSize;
}
