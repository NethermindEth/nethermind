// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Metadata describing the format of an index node to build.
/// </summary>
internal struct BTreeNodeMetadata
{
    /// <summary>Which kind of addressable thing this is.</summary>
    /// <remarks>
    /// Encoded in the low 2 bits of the on-disk <c>Flags</c> byte. The writer emits only
    /// <see cref="BTreeNodeKind.Intermediate"/>; <see cref="BTreeNodeKind.Entry"/> is the
    /// kind used by data-region entry records and is not written here.
    /// </remarks>
    public BTreeNodeKind NodeKind;

    /// <summary>0=Variable, 1=Uniform.</summary>
    public int KeyType;
    /// <summary>Base offset subtracted from values before writing; caller subtracts it before AddKey. 0 means none.</summary>
    public ulong BaseOffset;
    /// <summary>Uniform: fixed key length or slot size. Variable: ignored.</summary>
    public int KeySlotSize;
    /// <summary>Fixed value slot width in bytes; only <c>{2, 3, 4, 6}</c> are valid (the writer rejects others).</summary>
    public int ValueSlotSize = 4;
    /// <summary>When true, fixed-width key slots are written byte-reversed so an LE integer load matches lex order (Uniform with <see cref="KeySlotSize"/> ∈ {2,4,8} only).</summary>
    public bool IsKeyLittleEndian = false;

    public BTreeNodeMetadata() => NodeKind = BTreeNodeKind.Intermediate;
}
