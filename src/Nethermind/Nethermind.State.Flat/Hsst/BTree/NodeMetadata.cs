// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Metadata for a B-tree index block, parsed from the Metadata section.
/// </summary>
public readonly struct NodeMetadata
{
    public byte Flags { get; init; }
    public int KeyCount { get; init; }
    /// <summary>KeyType=0: section size. KeyType=1: fixed key length.</summary>
    public int KeySize { get; init; }
    /// <summary>Base offset added to every Uniform value read. 0 when absent. Encoded on disk as 6-byte LE.</summary>
    public ulong BaseOffset { get; init; }

    /// <summary>
    /// The <see cref="BTreeNodeKind"/> packed into Flags bits 0-1. For BTreeNode
    /// nodes parsed by this reader, this is always <see cref="BTreeNodeKind.Intermediate"/>;
    /// <see cref="BTreeNodeKind.Entry"/> sits on data-region entries which the BTree
    /// reader recognizes from a single flag-byte read before deciding whether to call
    /// <see cref="BTreeNodeReader.ReadFromStart"/> at all.
    /// </summary>
    public BTreeNodeKind NodeKind => (BTreeNodeKind)(Flags & 0x03);
    public int KeyType => (Flags >> 2) & 0x03;
    /// <summary>
    /// Fixed value width in bytes (one of {2, 3, 4, 6}). Decoded from Flags bits 4-5.
    /// Values are always Uniform.
    /// </summary>
    public int ValueSize => ((Flags >> 4) & 0b11) switch
    {
        0 => 2,
        1 => 3,
        2 => 4,
        _ => 6,
    };
    /// <summary>
    /// True when fixed-width key slots are stored byte-reversed (Flags bit 6). Honored by
    /// readers for Uniform with <see cref="KeySize"/> ∈ {2,4,8}, and unconditionally for
    /// Variable (<see cref="KeyType"/>=0) where the prefixArr slot is uniformly 2 bytes.
    /// See <see cref="BTreeNodeReader"/> docs for details.
    /// </summary>
    public bool IsKeyLittleEndian => (Flags & 0x40) != 0;

    /// <summary>Total byte size of the Keys section.</summary>
    public int KeySectionSize => KeyType switch
    {
        0 => KeySize,              // Variable: KeySize IS the section size
        1 => KeyCount * KeySize,   // Uniform: count * fixed length
        _ => throw new InvalidDataException()
    };

    /// <summary>Total byte size of the Values section. Always Uniform: count × fixed width.</summary>
    public int ValueSectionSize => KeyCount * ValueSize;
}
